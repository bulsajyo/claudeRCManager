using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeRcManager;

/// <summary>
/// 다른 프로세스에 Ctrl+C 를 보내 "정상 종료"시킨다.
///
/// claude remote-control 은 Ctrl+C(SIGINT) 로 끝날 때에만 "지금 연결돼 있던 세션 목록"을 기록하고,
/// 다음 실행에서 그 목록 전체를 다시 붙인다. 강제 종료(taskkill /F)하면 이 기록이 남지 않아
/// 서버가 처음 만들었던 세션 하나만 돌아온다. 그래서 중지할 때는 항상 이쪽을 먼저 시도한다.
/// </summary>
static class ConsoleCtrl
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll")] static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroup);

    /// <summary>
    /// 콘솔 붙이기/Ctrl+C 무시 설정은 프로세스 전역 상태라 한 번에 하나씩만 한다.
    /// "Ctrl+C 무시" 상태는 그 순간 새로 만든 자식에게 상속되므로, 자식 프로세스를 시작할 때도 이 잠금을 잡는다.
    /// </summary>
    public static readonly object Gate = new();

    public static bool SendCtrlC(int pid)
    {
        lock (Gate)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid)) return false;      // 대상의 (숨겨진) 콘솔에 잠깐 붙는다
            SetConsoleCtrlHandler(IntPtr.Zero, true);         // 우리 자신은 Ctrl+C 를 무시
            try
            {
                if (!GenerateConsoleCtrlEvent(0 /* CTRL_C_EVENT */, 0)) return false;
                Thread.Sleep(300);                            // 이벤트가 전달될 시간을 준 뒤에 떨어진다
                return true;
            }
            finally
            {
                FreeConsole();
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    /// <summary>Ctrl+C 로 끝내 보고, 시간 안에 안 끝나면 프로세스 트리를 강제 종료한다. 정상 종료됐으면 true.</summary>
    public static bool StopGracefully(int pid, int timeoutMs)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (SendCtrlC(pid) && p.WaitForExit(timeoutMs)) return true;
            try { p.Kill(entireProcessTree: true); p.WaitForExit(5000); } catch { }
            return false;
        }
        catch { return true; }   // 이미 없는 프로세스
    }
}
