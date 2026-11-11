using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeRcManager;

/// <summary>
/// claude 가 ~/.claude/projects/&lt;폴더&gt;/bridge-pointer.json 에 남기는 기록을 읽어
/// "어느 폴더에서 rc 를 쓴 적이 있는지 / 지금 돌고 있는지"를 알아낸다.
/// </summary>
static class BridgePointers
{
    public sealed record Info(string Directory, int Pid, bool Alive, DateTime LastUsed);

    static string ProjectsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>claude 가 프로젝트 폴더 이름을 만드는 규칙: 영숫자가 아닌 문자는 모두 '-'.</summary>
    public static string Mangle(string path) => Regex.Replace(path, "[^a-zA-Z0-9]", "-");

    /// <summary>이 폴더에서 (우리가 띄우지 않은) rc 서버가 살아 있으면 그 PID.</summary>
    public static int? LivePid(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory).TrimEnd('\\', '/');
            var file = Path.Combine(ProjectsDir, Mangle(full), "bridge-pointer.json");
            if (!File.Exists(file)) return null;
            var (pid, procStart) = Read(file);
            return IsAlive(pid, procStart) ? pid : null;
        }
        catch { return null; }
    }

    public static List<Info> Scan()
    {
        var result = new List<Info>();
        try
        {
            if (!System.IO.Directory.Exists(ProjectsDir)) return result;
            foreach (var dir in System.IO.Directory.GetDirectories(ProjectsDir))
            {
                var file = Path.Combine(dir, "bridge-pointer.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var real = Resolve(Path.GetFileName(dir));
                    if (real == null) continue;
                    var (pid, procStart) = Read(file);
                    result.Add(new Info(real, pid, IsAlive(pid, procStart), File.GetLastWriteTime(file)));
                }
                catch { }
            }
        }
        catch { }
        return result.OrderByDescending(i => i.Alive).ThenByDescending(i => i.LastUsed).ToList();
    }

    static (int pid, long procStart) Read(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        int pid = doc.RootElement.TryGetProperty("pid", out var p) && p.TryGetInt32(out var v) ? v : 0;
        long start = 0;
        if (doc.RootElement.TryGetProperty("procStart", out var s))
        {
            if (s.ValueKind == JsonValueKind.String) long.TryParse(s.GetString(), out start);
            else if (s.ValueKind == JsonValueKind.Number) s.TryGetInt64(out start);
        }
        return (pid, start);
    }

    static bool IsAlive(int pid, long procStart)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!p.ProcessName.Contains("claude", StringComparison.OrdinalIgnoreCase) &&
                !p.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)) return false;
            // procStart 는 FILETIME. PID 재사용으로 다른 프로세스를 오인하지 않도록 시작 시각도 비교한다.
            if (procStart > 0)
            {
                long diff = Math.Abs(p.StartTime.ToFileTimeUtc() - procStart);
                if (diff > TimeSpan.FromSeconds(10).Ticks) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>"D--dev-my-app" 같은 이름을 실제 존재하는 경로로 되돌린다 ('-' 가 원래 무엇이었는지는 디스크를 보고 판단).</summary>
    static string? Resolve(string mangled)
    {
        var m = Regex.Match(mangled, "^([A-Za-z])--(.*)$");
        if (!m.Success) return null;
        var root = m.Groups[1].Value.ToUpperInvariant() + ":\\";
        if (!System.IO.Directory.Exists(root)) return null;
        return m.Groups[2].Value.Length == 0 ? root : Walk(root, m.Groups[2].Value);
    }

    static string? Walk(string current, string remaining)
    {
        string[] subs;
        try { subs = System.IO.Directory.GetDirectories(current); }
        catch { return null; }

        foreach (var sub in subs)
        {
            var name = Mangle(Path.GetFileName(sub));
            if (remaining.Equals(name, StringComparison.OrdinalIgnoreCase)) return sub;
            if (remaining.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase))
            {
                var found = Walk(sub, remaining[(name.Length + 1)..]);
                if (found != null) return found;
            }
        }
        return null;
    }
}
