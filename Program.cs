namespace ClaudeRcManager;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // 한 번만 실행: 이미 떠 있으면 그 창을 앞으로 가져오고 끝낸다.
        using var mutex = new Mutex(true, "ClaudeRcManager.Singleton", out bool isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "ClaudeRcManager.Show");
        if (!isFirst)
        {
            showSignal.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        bool hidden = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var form = new MainForm(hidden);

        new Thread(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                try { form.BeginInvoke(form.ShowFromTray); } catch { }
            }
        }) { IsBackground = true }.Start();

        Application.Run(form);
    }
}
