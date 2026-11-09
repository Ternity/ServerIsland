using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace ServerIsland.Desktop.Platform;

/// Windows 专属：把「窗口到底在不在置顶（topmost）的 z 序带里」变成可查询、可修复。
///
/// 为什么需要它：Avalonia 的 Window.Topmost 只在属性值**变化**时下发一次 SetWindowPos；
/// 而 Native.ChangeExStyle 是直接 SetWindowLongPtr(GWL_EXSTYLE) 改位的，写完不跟 SetWindowPos。
/// 于是存在这种状态：WS_EX_TOPMOST 位还在，窗口却已经不在置顶带里 —— 被同一块显示器上
/// 最大化的普通窗口整块盖住，IsWindowVisible 仍然返回 true，用户看到的就是「灵动岛不见了」。
/// 进入这个状态后程序里没有任何路径能自己恢复，只能重启进程（新窗口重新入带）。
internal static class WinTopmost
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOPMOST = 0x00000008;
    const uint GA_ROOT = 2;

    static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);
    static readonly IntPtr HWND_NOTOPMOST = (IntPtr)(-2);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010,
               SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static IntPtr Handle(Window w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// 把窗口放回置顶带：不抢焦点、不改位置大小、不改所有者层级（实测对 WS_EX_NOACTIVATE 窗口安全）。
    public static void Raise(Window w)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// 改完 GWL_EXSTYLE 之后必须补一次：让 Windows 重新评估窗口框架，并按 Topmost 重申 z 序。
    public static void FlushStyles(Window w, bool topmost)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, topmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    /// 屏幕物理点 screenPoint 上压着的是不是「别人的普通窗口」。
    /// 是 → 我们已经不在置顶带里（真正的 topmost 窗口不可能被普通窗口压住）；
    /// 别的 topmost 窗口压着我们算正常（例如另一个置顶面板），不抢它的位置。
    public static bool IsCoveredByNormalWindow(Window w, PixelPoint screenPoint)
    {
        var h = Handle(w);
        if (h == IntPtr.Zero) return false;
        var at = WindowFromPoint(new POINT { X = screenPoint.X, Y = screenPoint.Y });
        if (at == IntPtr.Zero) return false;
        var root = GetAncestor(at, GA_ROOT);
        if (root == h) return false;
        if ((GetWindowLongPtr(root, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0) return false;
        return true;
    }
}
