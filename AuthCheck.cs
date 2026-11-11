using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeRcManager;

/// <summary>LoggedIn 이 null 이면 "확인 자체를 못 했다"는 뜻 (claude 를 못 찾음, 시간 초과 등).</summary>
sealed record AuthStatus(bool? LoggedIn, string Detail);

/// <summary>`claude auth status` 로 로그인 여부를 확인하고, 필요하면 `claude auth login` 창을 띄운다.</summary>
static class AuthCheck
{
    public static AuthStatus Check(AppConfig cfg)
    {
        try
        {
            using var p = new Process { StartInfo = Build(cfg, "auth", "status", "--json") };
            p.StartInfo.CreateNoWindow = true;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;
            p.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            p.Start();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new AuthStatus(null, "시간 초과");
            }
            var text = stdout.Result;

            // 로그인 안 된 경우에도 JSON 은 나오므로 종료 코드와 무관하게 파싱한다.
            int start = text.IndexOf('{'), end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                var err = (stderr.Result + " " + text).Trim();
                return new AuthStatus(null, err.Length > 80 ? err[..80] : err.Length > 0 ? err : $"종료 코드 {p.ExitCode}");
            }
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("loggedIn", out var li) || li.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return new AuthStatus(null, "알 수 없는 응답");

            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var detail = Str("subscriptionType") is { Length: > 0 } sub ? sub : Str("authMethod");
            return new AuthStatus(li.GetBoolean(), detail);
        }
        catch (Exception ex)
        {
            return new AuthStatus(null, ex.Message);
        }
    }

    /// <summary>콘솔 창에서 `claude auth login` 을 실행한다 (브라우저 인증 / 코드 붙여넣기용). 끝나면 창이 닫힌다.</summary>
    public static Process StartLogin(AppConfig cfg)
    {
        var psi = Build(cfg, "auth", "login");
        psi.CreateNoWindow = false;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        return p;
    }

    static ProcessStartInfo Build(AppConfig cfg, params string[] args)
    {
        var exe = ClaudeLocator.Find(cfg);
        var psi = new ProcessStartInfo { UseShellExecute = false };
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else psi.FileName = exe;
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}
