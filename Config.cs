using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace ClaudeRcManager;

/// <summary>관리 대상 remote-control 서버 하나의 설정.</summary>
sealed class SessionConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>--name 으로 넘길 세션 이름. 비어 있으면 claude 가 자동으로 정한다.</summary>
    public string Name { get; set; } = "";
    public string Directory { get; set; } = "";
    public string SpawnMode { get; set; } = "same-dir";
    /// <summary>비어 있으면 --permission-mode 를 넘기지 않는다.</summary>
    public string PermissionMode { get; set; } = "";
    public string ExtraArgs { get; set; } = "";
    /// <summary>프로그램이 시작될 때(=재부팅 후) 자동으로 시작할지 여부.</summary>
    public bool Enabled { get; set; } = true;

    [JsonIgnore]
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Name) ? Name
        : Path.GetFileName(Directory.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf
        : Directory;
}

sealed class AppConfig
{
    /// <summary>claude 실행 파일 경로. 비어 있으면 PATH 에서 찾는다.</summary>
    public string ClaudePath { get; set; } = "";
    public bool AutoRestart { get; set; } = true;
    public List<SessionConfig> Sessions { get; set; } = new();
}

static class Store
{
    // CLAUDE_RC_MANAGER_HOME 으로 설정/로그 위치를 바꿀 수 있다 (테스트·포터블 용도).
    public static readonly string Dir =
        Environment.GetEnvironmentVariable("CLAUDE_RC_MANAGER_HOME") is { Length: > 0 } home ? home
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeRcManager");
    public static readonly string ConfigPath = Path.Combine(Dir, "config.json");
    public static readonly string LogDir = Path.Combine(Dir, "logs");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 깨진 설정은 덮어쓰지 않도록 백업해 둔다.
            try { File.Copy(ConfigPath, ConfigPath + ".broken", true); } catch { }
            MessageBox.Show("설정 파일을 읽지 못해 빈 설정으로 시작합니다.\n(기존 파일은 config.json.broken 으로 백업)\n\n" + ex.Message,
                "Claude RC Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        System.IO.Directory.CreateDirectory(Dir);
        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, ConfigPath, true);
    }
}

/// <summary>Windows 로그인 시 자동 실행 (HKCU Run 키).</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "ClaudeRcManager";

    static string Command => $"\"{Environment.ProcessPath}\" --minimized";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, false);
    }

    /// <summary>exe 를 옮긴 경우 등록된 경로를 현재 위치로 고친다.</summary>
    public static void FixPathIfMoved()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string cur && !string.Equals(cur, Command, StringComparison.OrdinalIgnoreCase))
            Set(true);
    }
}

static class ClaudeLocator
{
    public static string Find(AppConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ClaudePath))
        {
            if (File.Exists(cfg.ClaudePath)) return cfg.ClaudePath;
            throw new FileNotFoundException("설정된 claude 경로를 찾을 수 없습니다: " + cfg.ClaudePath);
        }

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
            foreach (var d in dirs)
            {
                try
                {
                    var p = Path.Combine(d.Trim().Trim('"'), name);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        throw new FileNotFoundException("claude 실행 파일을 PATH 에서 찾을 수 없습니다. config.json 의 ClaudePath 에 경로를 지정하세요.");
    }
}
