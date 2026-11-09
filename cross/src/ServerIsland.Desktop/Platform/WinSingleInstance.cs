using Avalonia.Threading;

namespace ServerIsland.Desktop.Platform;

/// Windows 专属的单实例「转发」：第二个实例不再只是静默退出，而是让第一个实例把岛显出来。
/// 用命名事件（EventWaitHandle）传递，不依赖窗口类名/标题，也不需要 WndProc 钩子。
internal static class WinSingleInstance
{
    static EventWaitHandle? _event;
    static string Name => "ServerIsland-Raise-" + Environment.UserName;

    /// 第一个实例：创建接收事件（第二个实例会 Set 它）。
    public static void Create()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _event = new EventWaitHandle(false, EventResetMode.AutoReset, Name); } catch { }
    }

    /// 第一个实例：后台等信号，收到就回 UI 线程执行 onRaise。
    public static void Listen(Action onRaise)
    {
        if (_event is null) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { _event.WaitOne(); } catch { return; }
                Dispatcher.UIThread.Post(onRaise);
            }
        }) { IsBackground = true, Name = "single-instance-raise" };
        thread.Start();
    }

    /// 第二个实例：把「叫出来」的信号发出去（第一个实例没在跑也不会报错），然后自己退出。
    public static void RaiseExisting()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { using var e = new EventWaitHandle(false, EventResetMode.AutoReset, Name); e.Set(); } catch { }
    }
}
