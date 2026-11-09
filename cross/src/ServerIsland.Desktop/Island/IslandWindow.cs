using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Platform;
using ServerIsland.Desktop.Ui;

namespace ServerIsland.Desktop.Island;

/// 刘海窗口：固定大小、透明、置顶，放在主屏幕顶部正中（或桌宠旁边）。
/// 只有形状所在的矩形接收鼠标（输入区域），透明区域的点击会落到下面的窗口。
/// 悬停逻辑与 Swift 版 IslandController 一致：收起时鼠标进入 0.14 秒后展开；展开时离开形状 + 12 后收起（二级页面延迟更久）。
public sealed class IslandWindow : Window
{
    readonly MonitorEngine _engine;
    readonly ConfigStore _store;
    public readonly IslandState State = new();
    readonly IslandSurface _surface = new();
    readonly IslandViews _views;
    IslandMode _mode = IslandMode.Hidden;
    Guid? _toastId;
    LiveScope? _live;
    bool _rebuildPending, _shownOnce;
    Point? _pointer;
    IDisposable? _expandTimer, _collapseTimer;
    readonly DispatcherTimer _liveTimer, _watchTimer, _topTimer;
    bool _petHover;

    /// 桌宠模式：面板浮在小章鱼旁边。返回小章鱼窗口在屏幕上的矩形（物理像素）。
    public Func<PixelRect?>? FloatingAnchor;
    bool Floating => FloatingAnchor != null;

    public IslandWindow(MonitorEngine engine, ConfigStore store, ILoginPrompter prompter)
    {
        _engine = engine;
        _store = store;
        _views = new IslandViews(engine, store, State, prompter);
        Title = "ServerIsland";
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        ShowActivated = false;
        RequestedThemeVariant = ThemeVariant.Dark;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var size = IslandLayout.PanelSize(State.Geometry);
        Width = size.Width;
        Height = size.Height;
        Content = _surface;
        Icon = App.WindowIcon;

        State.RequestRebuild = RequestRebuild;
        State.PageChanged += () =>
        {
            UpdateActivation();
            RequestRebuild();
        };
        State.Unpinned = () => CheckPointer();

        _surface.AnimationFinished += UpdateInputRegion;
        PointerEntered += (_, e) => OnPointer(e.GetPosition(this));
        PointerMoved += (_, e) => OnPointer(e.GetPosition(this));
        PointerExited += (_, _) => OnPointer(null);

        _liveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_mode == IslandMode.Expanded) _live?.Tick();
        });
        _liveTimer.Start();
        // 保险：菜单关闭后鼠标可能已经不在窗口上，收不到 PointerExited；展开期间每 0.25 秒用系统光标位置核对一次。
        _watchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => CheckPointer());
        _watchTimer.Start();
        // 保险：Windows 上偶尔会被系统挪出「置顶」z 序带（样式位还在、带没了），
        // 那样会被同屏最大化的普通窗口整块盖住，看起来就是「岛消失了」。每 2 秒核对一次，掉了就抬回来。
        _topTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => GuardTopmost());
        _topTimer.Start();
    }

    Rect _inputRect;
    bool _macIgnoring = true;
    DispatcherTimer? _macTimer;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (OperatingSystem.IsMacOS()) SetupMac();
        Native.SetToolWindow(this);
        UpdateActivation();
        Reposition();
        Screens.Changed += (_, _) => { Reposition(); Native.EnsureTopmost(this); };
        Rebuild();
        Native.EnsureTopmost(this);                // 新窗口刚出来：确认真的进了置顶带
        WinSingleInstance.Listen(RestoreVisible);  // 第二个实例被双击时，把岛叫出来
    }

    /// 平时不抢键盘焦点；只有"全部任务"页（有搜索框）允许成为活动窗口。
    void UpdateActivation()
    {
        bool allow = State.Page.Kind == PageKind.Jobs;
        Native.SetNoActivate(this, !allow);
        Native.EnsureTopmost(this);                // 刚写过 GWL_EXSTYLE，重申一次置顶带
        if (allow && IsVisible) Activate();
    }

    /// 把岛重新显示出来（单实例转发、系统事件、定时保险都走这里）：不抢焦点、不动位置大小。
    public void RestoreVisible()
    {
        if (!IsVisible) Show();
        if (Floating) PositionNearPet();
        else Native.EnsureTopmost(this);
    }

    /// 定时核对：形状中心上压着别的普通窗口，说明已经掉出置顶带 —— 抬回来。
    /// 隐藏时、弹出菜单打开时不动 z 序（否则会把岛自己的菜单压到岛下面）。
    void GuardTopmost()
    {
        if (!IsVisible || State.MenusOpen > 0) return;
        var c = _inputRect.Center;
        var scale = RenderScaling;
        var screen = new PixelPoint(Position.X + (int)Math.Round(c.X * scale), Position.Y + (int)Math.Round(c.Y * scale));
        if (Native.IsTopmostLost(this, screen)) Native.EnsureTopmost(this);
    }

    public bool ShapeAtTop => _surface.AtTop;

    /// macOS（只用于试用跨平台版）：用硬件刘海的真实位置，层级盖过菜单栏，并用光标轮询实现点击穿透。
    void SetupMac()
    {
        var (nw, nh, _) = MacNative.NotchInfo();
        State.Geometry = new NotchGeometry(nw, nh);
        var size = IslandLayout.PanelSize(State.Geometry);
        Width = size.Width;
        Height = size.Height;
        MacNative.MakeOverlay(this);
        MacNative.SetIgnoresMouse(this, true);
        _macTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Input, (_, _) =>
        {
            if (!IsVisible || MacNative.CursorInWindow(this) is not { } p) return;
            bool inside = _inputRect.Contains(p);
            if (inside == _macIgnoring)
            {
                _macIgnoring = !inside;
                MacNative.SetIgnoresMouse(this, _macIgnoring);
            }
            if (inside) OnPointer(p);
            else if (_pointer != null) OnPointer(null);
        });
        _macTimer.Start();
    }

    public void Reposition()
    {
        if (Floating) { PositionNearPet(); return; }
        if (OperatingSystem.IsMacOS())
        {
            if (_surface.AnchorBottom) { _surface.AnchorBottom = false; _surface.InvalidateArrange(); }
            MacNative.PlaceAtTop(this, Width, Height);
            return;
        }
        // 从桌宠模式切回来时，形状可能还贴着窗口底边（面板曾经向上展开），要复位到顶边。
        if (_surface.AnchorBottom)
        {
            _surface.AnchorBottom = false;
            _surface.InvalidateArrange();
            _surface.InvalidateVisual();
        }
        var screen = ChosenScreen();
        if (screen == null) return;
        var scale = screen.Scaling;
        var w = (int)Math.Round(Width * scale);
        Position = new PixelPoint(screen.Bounds.X + (screen.Bounds.Width - w) / 2, screen.Bounds.Y);
    }

    /// 设置里选的屏幕（AppSettings.IslandScreen）；没选或没连接时用主屏幕。
    Avalonia.Platform.Screen? ChosenScreen()
    {
        var pref = _store.Settings.IslandScreen;
        var chosen = pref is "" or "primary" ? null : Screens.All.FirstOrDefault(s => s.DisplayName == pref);
        return chosen ?? Screens.Primary ?? Screens.All.FirstOrDefault();
    }

    void PositionNearPet()
    {
        if (FloatingAnchor?.Invoke() is not { } pet) return;
        var screen = Screens.ScreenFromPoint(pet.Center) ?? Screens.Primary;
        if (screen == null) return;
        var scale = screen.Scaling;
        var work = screen.WorkingArea;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        bool below = pet.Center.Y < work.Y + work.Height / 2;
        _surface.AnchorBottom = !below;
        int x = pet.Center.X - w / 2;
        int panelW = (int)Math.Round((IslandLayout.ExpandedMinWidth + 20) * scale);
        int minX = work.X - (w - panelW) / 2, maxX = work.Right - (w + panelW) / 2;
        x = Math.Clamp(x, Math.Min(minX, maxX), Math.Max(minX, maxX));
        int y = below ? pet.Bottom + (int)(4 * scale) : pet.Y - h - (int)(4 * scale) + (int)(IslandSurface.ShadowMargin * scale);
        Position = new PixelPoint(x, y);
    }

    // MARK: - 重建

    public void RequestRebuild()
    {
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.UIThread.Post(() => { _rebuildPending = false; Rebuild(); }, DispatcherPriority.Background);
    }

    IslandMode CurrentMode()
    {
        var mode = IslandLayout.Mode(State.Hovering, _engine, _store.Settings.CollapsedStyle);
        // 桌宠模式：收起状态由小章鱼自己显示，面板窗口只在提示 / 展开时出现。
        if (Floating && mode is IslandMode.Collapsed or IslandMode.Hidden) return IslandMode.Hidden;
        return mode;
    }

    public void Rebuild()
    {
        State.Floating = Floating;
        _surface.Floating = Floating;
        if (!Floating && _surface.AnchorBottom) Reposition();
        var hosts = _engine.OrderedHosts;
        var mode = CurrentMode();
        var contentHeight = State.ContentHeight(hosts, _store.Settings.FoldIdleHosts);
        var spec = IslandLayout.Spec(mode, State.Geometry, contentHeight, Floating);
        bool modeChanged = mode != _mode;
        bool toastChanged = mode == IslandMode.Toast && _engine.CurrentEvent?.Id != _toastId;
        _mode = mode;
        _toastId = _engine.CurrentEvent?.Id;

        if (Floating)
        {
            if (mode == IslandMode.Hidden) { if (IsVisible) Hide(); State.Hovering = false; return; }
            PositionNearPet();
            if (!IsVisible) { Show(); _shownOnce = false; }
        }

        double shadow = mode == IslandMode.Expanded ? 1 : mode == IslandMode.Toast ? 0.6 : 0;
        _surface.SetTarget(spec, shadow, animate: _shownOnce && IsVisible);
        _shownOnce = true;

        var scope = LiveScope.Begin();
        Control? content = null;
        try
        {
            content = mode switch
            {
                IslandMode.Collapsed => _views.Collapsed(),
                IslandMode.Toast when _engine.CurrentEvent is { } ev => _views.Toast(ev),
                IslandMode.Expanded => _views.Expanded(Math.Min(contentHeight, IslandLayout.MaxContentHeight), spec.Scrolls, spec.Width),
                _ => null,
            };
        }
        catch (Exception e) { DebugLog.Log("构建刘海界面出错：" + e); }
        finally { LiveScope.End(); }
        _live = scope;
        _surface.SetContent(content, fancy: (modeChanged || toastChanged) && mode is IslandMode.Expanded or IslandMode.Toast);
        UpdateInputRegion();
    }

    // MARK: - 输入区域与悬停

    /// 收起 / 隐藏时的触发区域：形状太窄时至少 160 宽、10 高。
    Rect TriggerRect()
    {
        var r = _surface.TargetRect;
        if (r.Width < 160) r = r.Inflate(new Thickness((160 - r.Width) / 2, 0));
        if (r.Height < 10) r = r.WithHeight(10);
        return r.Inflate(2);
    }

    void UpdateInputRegion()
    {
        if (_mode == IslandMode.Expanded)
        {
            // 展开时多留 12 的余量：离开这个范围才开始计时收起。
            var r = _surface.TargetRect.Union(_surface.CurrentRect).Inflate(12);
            _inputRect = r;
            Native.SetInputRegion(this, r);
        }
        else
        {
            _inputRect = TriggerRect().Union(_surface.CurrentRect);
            Native.SetInputRegion(this, _inputRect);
        }
    }

    void OnPointer(Point? p)
    {
        _pointer = p;
        if (State.Hovering)
        {
            var inside = p is { } pt && _surface.TargetRect.Inflate(12).Contains(pt);
            if (inside || _petHover || State.Pinned || State.MenusOpen > 0) CancelCollapse();
            else ScheduleCollapse();
        }
        else
        {
            if (p is { } pt && TriggerRect().Contains(pt)) ScheduleExpand();
            else CancelExpand();
        }
    }

    /// 桌宠窗口的悬停状态（桌宠模式下，鼠标在小章鱼上也算"在面板上"）。
    public void SetPetHover(bool on)
    {
        _petHover = on;
        if (on)
        {
            if (!State.Hovering) ScheduleExpand(0.25);
            else CancelCollapse();
        }
        else
        {
            CancelExpand();
            if (State.Hovering && _pointer == null && !State.Pinned && State.MenusOpen == 0) ScheduleCollapse();
        }
    }

    /// 用系统光标位置核对（收不到鼠标事件时的保险）。
    void CheckPointer()
    {
        if (!State.Hovering || State.Pinned || State.MenusOpen > 0 || _petHover) return;
        Point local;
        if (OperatingSystem.IsMacOS() && MacNative.CursorInWindow(this) is { } mp) local = mp;
        else if (Native.CursorPosition() is { } cursor) local = this.PointToClient(cursor);
        else
        {
            if (_pointer == null) ScheduleCollapse();
            return;
        }
        bool inside = _surface.TargetRect.Inflate(12).Contains(local);
        if (inside) CancelCollapse(); else ScheduleCollapse();
    }

    void ScheduleExpand(double delay = 0.14)
    {
        if (_expandTimer != null) return;
        _expandTimer = DispatcherTimer.RunOnce(() =>
        {
            _expandTimer = null;
            if ((_pointer is { } p && TriggerRect().Contains(p)) || _petHover) SetHovering(true);
        }, TimeSpan.FromSeconds(delay));
    }

    void CancelExpand()
    {
        _expandTimer?.Dispose();
        _expandTimer = null;
    }

    void ScheduleCollapse()
    {
        if (_collapseTimer != null) return;
        // 在详情等二级页面里多给一点余量，避免鼠标稍微滑出就丢失页面。
        var delay = State.Page.Kind == PageKind.List ? 0.25 : 0.7;
        _collapseTimer = DispatcherTimer.RunOnce(() =>
        {
            _collapseTimer = null;
            if (!State.Pinned && State.MenusOpen == 0 && !_petHover) SetHovering(false);
        }, TimeSpan.FromSeconds(delay));
    }

    void CancelCollapse()
    {
        _collapseTimer?.Dispose();
        _collapseTimer = null;
    }

    public void SetHovering(bool on)
    {
        CancelCollapse();
        CancelExpand();
        if (State.Hovering == on) return;
        State.Hovering = on;
        if (!on)
        {
            State.Page = IslandPage.List;
            State.Pinned = false;
            State.ShowIdle = false;
        }
        else _engine.DismissEvent();
        Rebuild();
    }

    /// 托盘图标：打开并固定面板 / 收起。
    public void TogglePinned()
    {
        if (State.Hovering) { State.Pinned = false; SetHovering(false); return; }
        State.Pinned = true;
        SetHovering(true);
    }
}
