using System.Security.Cryptography;
using System.Text;

namespace ClaudeRcManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // 설정 폴더(CLAUDE_RC_MANAGER_HOME)가 다르면 별개의 인스턴스로 취급한다.
        string scope = "";
        if (Environment.GetEnvironmentVariable("CLAUDE_RC_MANAGER_HOME") is { Length: > 0 })
            scope = "." + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Store.Dir.ToLowerInvariant())))[..8];

        // 한 번만 실행: 이미 떠 있으면 그 창을 앞으로 가져오고 끝낸다.
        using var mutex = new Mutex(true, "ClaudeRcManager.Singleton" + scope, out bool isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudeRcManager.Show" + scope);
        using var exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudeRcManager.Exit" + scope);

        if (!isFirst)
        {
            if (Has("--exit"))
            {
                // 실행 중인 매니저에게 "세션들을 정상 종료하고 끝내라"고 알리고, 끝날 때까지 기다린다.
                exitSignal.Set();
                try { mutex.WaitOne(60000); } catch (AbandonedMutexException) { }
            }
            else showSignal.Set();
            return;
        }
        if (Has("--exit")) return;   // 떠 있는 매니저가 없음

        ApplicationConfiguration.Initialize();
        var form = new MainForm(Has("--minimized"));

        new Thread(() =>
        {
            var handles = new WaitHandle[] { showSignal, exitSignal };
            while (true)
            {
                int which = WaitHandle.WaitAny(handles);
                try { form.BeginInvoke(which == 0 ? form.ShowFromTray : form.ExitNow); } catch { }
            }
        }) { IsBackground = true }.Start();

        Application.Run(form);
    }
}
