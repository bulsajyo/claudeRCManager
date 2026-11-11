using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeRcManager;

enum RunState { Stopped, Starting, Connected, Waiting, Exited, External }

/// <summary>`claude remote-control` 프로세스 하나를 창 없이 띄우고, 감시하고, 죽으면 다시 살린다.</summary>
sealed class SessionRunner
{
    public SessionConfig Config { get; }
    readonly AppConfig _app;
    readonly JobObject _job;
    readonly object _lock = new();
    readonly object _logLock = new();

    Process? _proc;
    bool _stopRequested;
    CancellationTokenSource? _restartCts;
    int _failures;

    // 화면이 1초마다 다시 그려지므로, 직전 프레임에 있던 줄은 로그에 다시 쓰지 않는다.
    HashSet<string> _prevFrame = new();
    HashSet<string> _curFrame = new();
    string _lastErrLine = "";

    public RunState State { get; private set; } = RunState.Stopped;
    public int? Pid { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public int? ExitCode { get; private set; }
    public string Capacity { get; private set; } = "";
    public string? SessionUrl { get; private set; }
    public string? EnvironmentUrl { get; private set; }
    public DateTime? RetryAt { get; private set; }
    public string LastError { get; private set; } = "";

    public event Action? Changed;

    public SessionRunner(SessionConfig config, AppConfig app, JobObject job)
    {
        Config = config; _app = app; _job = job;
    }

    public string LogPath => Path.Combine(Store.LogDir, Config.Id + ".log");
    public bool IsRunning { get { lock (_lock) return _proc != null; } }
    /// <summary>실행 중이거나 재시작 대기 중 (= 사용자가 켜 둔 상태).</summary>
    public bool IsActive { get { lock (_lock) return _proc != null || State == RunState.Waiting; } }

    public string StatusText
    {
        get
        {
            lock (_lock)
            {
                return State switch
                {
                    RunState.Starting => "연결 중…",
                    RunState.Connected => Capacity.Length > 0 ? $"연결됨 · 세션 {Capacity}" : "연결됨",
                    RunState.Waiting => $"재시작 대기 ({Math.Max(0, (int)Math.Ceiling(((RetryAt ?? DateTime.Now) - DateTime.Now).TotalSeconds))}초)"
                                        + (LastError.Length > 0 ? " · " + LastError : ""),
                    RunState.Exited => LastError.Length > 0 ? "실패 · " + LastError : $"종료됨 (코드 {ExitCode})",
                    RunState.External => "다른 곳에서 실행 중 (터미널)",
                    _ => "중지됨",
                };
            }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_proc != null) return;
            _restartCts?.Cancel();
            _restartCts = null;
            _stopRequested = false;
            RetryAt = null;
            try
            {
                var psi = BuildStartInfo();
                RotateLog();
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.Exited += (_, _) => OnExited(p);
                p.Start();
                _job.Add(p);
                _proc = p;
                Pid = p.Id;
                StartedAt = DateTime.Now;
                ExitCode = null;
                LastError = "";
                Capacity = "";
                SessionUrl = null;
                _prevFrame = new(); _curFrame = new(); _lastErrLine = "";
                State = RunState.Starting;
                Log($"=== 시작 (PID {p.Id}) · {Config.Directory} · claude {string.Join(' ', BuildArgs())}");
                _ = Task.Run(() => Pump(p.StandardOutput, false));
                _ = Task.Run(() => Pump(p.StandardError, true));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = RunState.Exited;
                Pid = null;
                Log("=== 시작 실패: " + ex.Message);
                _failures++;
                if (_app.AutoRestart) ScheduleRestart();
            }
        }
        Changed?.Invoke();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _stopRequested = true;
            _restartCts?.Cancel();
            _restartCts = null;
            RetryAt = null;
            _failures = 0;
            if (_proc != null)
            {
                Log("=== 중지 요청");
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
            else
            {
                State = RunState.Stopped;
                LastError = "";
            }
        }
        Changed?.Invoke();
    }

    public void MarkExternal()
    {
        lock (_lock) { if (_proc == null) State = RunState.External; }
        Changed?.Invoke();
    }

    void OnExited(Process p)
    {
        lock (_lock)
        {
            if (!ReferenceEquals(_proc, p)) return;
            try { ExitCode = p.ExitCode; } catch { ExitCode = null; }
            var ran = DateTime.Now - (StartedAt ?? DateTime.Now);
            try { p.Dispose(); } catch { }
            _proc = null;
            Pid = null;
            Capacity = "";
            Log($"=== 종료 (코드 {ExitCode}, 실행 시간 {ran:d\\.hh\\:mm\\:ss})");

            if (_stopRequested)
            {
                State = RunState.Stopped;
            }
            else
            {
                State = RunState.Exited;
                // 오래 돌다 죽었으면 일시적 문제로 보고 바로 재시도, 뜨자마자 죽으면 점점 간격을 늘린다.
                if (ran > TimeSpan.FromMinutes(1)) _failures = 0; else _failures++;
                if (_app.AutoRestart) ScheduleRestart();
            }
        }
        Changed?.Invoke();
    }

    // _lock 안에서만 호출
    void ScheduleRestart()
    {
        int delay = Math.Min(60, 5 * (1 << Math.Min(Math.Max(_failures - 1, 0), 4)));
        var cts = new CancellationTokenSource();
        _restartCts = cts;
        RetryAt = DateTime.Now.AddSeconds(delay);
        State = RunState.Waiting;
        Log($"=== {delay}초 뒤 자동 재시작");
        Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            lock (_lock) { if (_stopRequested || State != RunState.Waiting) return; }
            Start();
        }, TaskScheduler.Default);
    }

    ProcessStartInfo BuildStartInfo()
    {
        if (!Directory.Exists(Config.Directory))
            throw new DirectoryNotFoundException("폴더 없음: " + Config.Directory);

        var exe = ClaudeLocator.Find(_app);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = Config.Directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,   // 닫지 않고 열어 둔다 (EOF 로 종료되지 않게)
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;

        foreach (var a in BuildArgs()) psi.ArgumentList.Add(a);
        return psi;
    }

    List<string> BuildArgs()
    {
        var args = new List<string> { "remote-control" };
        if (!string.IsNullOrWhiteSpace(Config.Name)) { args.Add("--name"); args.Add(Config.Name.Trim()); }
        if (!string.IsNullOrWhiteSpace(Config.SpawnMode) && Config.SpawnMode != "same-dir") { args.Add("--spawn"); args.Add(Config.SpawnMode); }
        if (!string.IsNullOrWhiteSpace(Config.PermissionMode)) { args.Add("--permission-mode"); args.Add(Config.PermissionMode); }
        args.AddRange(SplitArgs(Config.ExtraArgs));
        return args;
    }

    static IEnumerable<string> SplitArgs(string s)
    {
        var sb = new StringBuilder();
        bool inQuote = false, has = false;
        foreach (char c in s ?? "")
        {
            if (c == '"') { inQuote = !inQuote; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (has) { yield return sb.ToString(); sb.Clear(); has = false; }
            }
            else { sb.Append(c); has = true; }
        }
        if (has) yield return sb.ToString();
    }

    // ---------- 출력 처리 ----------

    static readonly Regex CursorUp = new(@"\x1b\[\d*A", RegexOptions.Compiled);
    static readonly Regex Ansi = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b[@-Z\\-_]|\r", RegexOptions.Compiled);
    static readonly Regex Url = new(@"https://claude\.ai/code[^\s\x07\x1b""']*", RegexOptions.Compiled);
    static readonly Regex CapacityRx = new(@"Capacity:\s*(\d+\s*/\s*\d+)", RegexOptions.Compiled);

    async Task Pump(StreamReader reader, bool isErr)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                HandleLine(line, isErr);
        }
        catch { }
    }

    void HandleLine(string raw, bool isErr)
    {
        bool changed = false;
        string? toLog = null;

        lock (_lock)
        {
            foreach (Match m in Url.Matches(raw))
            {
                var u = m.Value;
                if (u.Contains("/session_")) { if (SessionUrl != u) { SessionUrl = u; changed = true; } }
                else if (u.Contains("environment=")) { if (EnvironmentUrl != u) { EnvironmentUrl = u; changed = true; } }
            }

            bool newFrame = !isErr && CursorUp.IsMatch(raw);
            var text = Ansi.Replace(raw, "").TrimEnd();
            if (newFrame) { _prevFrame = _curFrame; _curFrame = new(); }
            if (text.Trim().Length == 0) return;

            if (isErr)
            {
                if (text != _lastErrLine) { _lastErrLine = text; toLog = "[stderr] " + text; }
            }
            else
            {
                if (_curFrame.Add(text) && !_prevFrame.Contains(text)) toLog = text;

                if (_proc != null)
                {
                    if (text.Contains("Connecting", StringComparison.OrdinalIgnoreCase))
                    {
                        if (State != RunState.Starting) { State = RunState.Starting; changed = true; }
                    }
                    else if (text.Contains("Connected"))
                    {
                        if (State != RunState.Connected) { State = RunState.Connected; changed = true; }
                    }
                    var cm = CapacityRx.Match(text);
                    if (cm.Success)
                    {
                        var cap = cm.Groups[1].Value.Replace(" ", "");
                        if (cap != Capacity) { Capacity = cap; changed = true; }
                    }
                }
            }
        }

        if (toLog != null) Log(toLog);
        if (changed) Changed?.Invoke();
    }

    void Log(string line)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Store.LogDir);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }

    void RotateLog()
    {
        try
        {
            lock (_logLock)
            {
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Move(LogPath, LogPath + ".old", true);
            }
        }
        catch { }
    }
}
