using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace ServerIsland.Desktop.Platform;

/// 与窗口系统打交道的少量原生调用：
/// - 输入区域：刘海窗口比形状大，只让形状所在的矩形接收鼠标，透明区域的点击落到下面的窗口（Windows：SetWindowRgn；Linux：XShape 输入区域）；
/// - 不抢焦点：点刘海里的按钮时不把焦点从当前窗口抢走（Windows：WS_EX_NOACTIVATE）；
/// - 不出现在 Alt+Tab 里（WS_EX_TOOLWINDOW）。
public static class Native
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static IntPtr Handle(Window w) => w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    static void ChangeExStyle(Window w, long add, long remove)
    {
        if (!OperatingSystem.IsWindows()) return;
        var h = Handle(w);
        if (h == IntPtr.Zero) return;
        long style = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        long next = (style | add) & ~remove;
        if (next != style)
        {
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(next));
            // 只写扩展样式不会动 z 序：写完补一次 SetWindowPos(SWP_FRAMECHANGED)，
            // 既让 Windows 重新评估框架，也顺带按 Topmost 重申一次置顶（否则可能「位在、带失」）。
            if (OperatingSystem.IsWindows()) WinTopmost.FlushStyles(w, w.Topmost);
        }
    }

    public static void SetNoActivate(Window w, bool on) =>
        ChangeExStyle(w, on ? WS_EX_NOACTIVATE : 0, on ? 0 : WS_EX_NOACTIVATE);

    public static void SetToolWindow(Window w) => ChangeExStyle(w, WS_EX_TOOLWINDOW, WS_EX_APPWINDOW);

    /// 把窗口重新放回「置顶」z 序带（Windows 专属，其它平台空实现）。
    /// Avalonia 的 Topmost 只在值变化时下发 SetWindowPos，这里给程序一个「必要时重申」的入口。
    public static void EnsureTopmost(Window w)
    {
        if (OperatingSystem.IsWindows()) WinTopmost.Raise(w);
    }

    /// 形状中心上压着的是不是别人的普通窗口（= 已经掉出置顶带）。只有 Windows 能查。
    public static bool IsTopmostLost(Window w, PixelPoint screenPoint) =>
        OperatingSystem.IsWindows() && WinTopmost.IsCoveredByNormalWindow(w, screenPoint);

    /// 屏幕上的鼠标位置（物理像素）。拿不到时返回 null。
    public static PixelPoint? CursorPosition()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var p)) return new PixelPoint(p.X, p.Y);
        if (OperatingSystem.IsLinux()) return X11.Pointer();
        return null;
    }

    /// 只让窗口里的 rect（窗口坐标，DIP）接收鼠标；rect 为 null 时整个窗口都接收。
    public static void SetInputRegion(Window w, Rect? rect)
    {
        double scale = w.RenderScaling;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var h = Handle(w);
                if (h == IntPtr.Zero) return;
                if (rect is not { } r) { SetWindowRgn(h, IntPtr.Zero, true); return; }
                var rgn = CreateRectRgn((int)Math.Floor(r.X * scale), (int)Math.Floor(r.Y * scale), (int)Math.Ceiling(r.Right * scale), (int)Math.Ceiling(r.Bottom * scale));
                SetWindowRgn(h, rgn, true);   // 系统接管 rgn，不用释放
            }
            else if (OperatingSystem.IsLinux())
            {
                var h = Handle(w);
                if (h == IntPtr.Zero) return;
                X11.SetInputShape(h, rect is { } r
                    ? new PixelRect((int)Math.Floor(r.X * scale), (int)Math.Floor(r.Y * scale), (int)Math.Ceiling(r.Width * scale), (int)Math.Ceiling(r.Height * scale))
                    : null);
            }
        }
        catch (Exception e) { Core.DebugLog.Log("设置输入区域失败：" + e.Message); }
    }

    /// Linux / X11（XWayland 下同样可用）。
    static class X11
    {
        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XFlush(IntPtr display);
        [DllImport("libX11.so.6")] static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport("libX11.so.6")]
        static extern bool XQueryPointer(IntPtr display, IntPtr w, out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int winX, out int winY, out uint mask);
        [DllImport("libXext.so.6")]
        static extern void XShapeCombineRectangles(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, XRectangle[] rects, int n, int op, int ordering);
        [DllImport("libXext.so.6")]
        static extern void XShapeCombineMask(IntPtr display, IntPtr window, int destKind, int xOff, int yOff, IntPtr pixmap, int op);

        [StructLayout(LayoutKind.Sequential)] struct XRectangle { public short X, Y; public ushort Width, Height; }

        const int ShapeInput = 2, ShapeSet = 0, Unsorted = 0;
        static IntPtr _display;
        static bool _failed;

        static IntPtr Display()
        {
            if (_display == IntPtr.Zero && !_failed)
            {
                try { _display = XOpenDisplay(IntPtr.Zero); } catch { _failed = true; }
                if (_display == IntPtr.Zero) _failed = true;
            }
            return _display;
        }

        public static void SetInputShape(IntPtr window, PixelRect? rect)
        {
            var d = Display();
            if (d == IntPtr.Zero) return;
            if (rect is { } r)
                XShapeCombineRectangles(d, window, ShapeInput, 0, 0, [new XRectangle { X = (short)r.X, Y = (short)r.Y, Width = (ushort)r.Width, Height = (ushort)r.Height }], 1, ShapeSet, Unsorted);
            else
                XShapeCombineMask(d, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet);
            XFlush(d);
        }

        public static PixelPoint? Pointer()
        {
            var d = Display();
            if (d == IntPtr.Zero) return null;
            try
            {
                if (XQueryPointer(d, XDefaultRootWindow(d), out _, out _, out int x, out int y, out _, out _, out _)) return new PixelPoint(x, y);
            }
            catch { }
            return null;
        }
    }
}
