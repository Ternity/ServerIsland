using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

public enum SettingsPageKind { General, Rules, Billing, Push, Host, Display }

/// 设置窗口：左侧栏是"通用""进度识别"和服务器列表，右侧是详情。（与 Swift 版 SettingsView.swift 对应）
public sealed class SettingsWindow : Window
{
    readonly ConfigStore _store;
    readonly MonitorEngine _engine;
    readonly ILoginPrompter _prompter;
    SettingsPageKind _kind = SettingsPageKind.General;
    Guid _hostID;
    readonly Grid _root = new() { ColumnDefinitions = new ColumnDefinitions("236,*") };
    readonly Border _sidebar = new();
    readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    Palette _p = Palette.For(ThemeVariant.Light);
    Action? _pageEngineUpdate;
    /// 当前页面的订阅（副屏页订阅连接状态），换页或关窗时取消。
    Action? _pageDetach;
    string _sidebarKey = "";
    AppLanguage _lang;

    public SettingsWindow(ConfigStore store, MonitorEngine engine, ILoginPrompter prompter, SettingsPageKind initial = SettingsPageKind.General)
    {
        _store = store;
        _engine = engine;
        _prompter = prompter;
        _kind = initial;
        Title = "ServerIsland";
        Width = 900;
        Height = 660;
        MinWidth = 760;
        MinHeight = 520;
        Icon = App.WindowIcon;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _lang = store.Settings.Language;
        Grid.SetColumn(_scroll, 1);
        _root.Children.Add(_sidebar);
        _root.Children.Add(_scroll);
        Content = _root;
        ActualThemeVariantChanged += (_, _) => RebuildAll();
        _store.Changed += OnStoreChanged;
        Closed += (_, _) => { _store.Changed -= OnStoreChanged; _pageDetach?.Invoke(); _pageDetach = null; };
        RebuildAll();
    }

    public void Select(SettingsPageKind kind, Guid host = default)
    {
        _kind = kind;
        _hostID = host;
        RebuildAll();
    }

    void OnStoreChanged()
    {
        // 切换语言后整页重绘；其它改动只更新侧栏（避免打字时重建输入框）。
        if (_store.Settings.Language != _lang)
        {
            _lang = _store.Settings.Language;
            Dispatcher.UIThread.Post(RebuildAll);
            return;
        }
        Dispatcher.UIThread.Post(BuildSidebar, DispatcherPriority.Background);
    }

    public void EngineChanged()
    {
        BuildSidebar();
        _pageEngineUpdate?.Invoke();
    }

    void RebuildAll()
    {
        _p = Palette.For(ActualThemeVariant);
        Background = _p.Bg.B();
        _sidebarKey = "";
        BuildSidebar();
        BuildPage();
    }

    /// 重建当前页面，保留滚动位置（切换会改变其它项可见性的开关时用）。
    public void RefreshPage()
    {
        var offset = _scroll.Offset;
        BuildPage();
        Dispatcher.UIThread.Post(() => _scroll.Offset = offset, DispatcherPriority.Loaded);
    }

    /// 副屏连接状态变了：侧栏的副屏行跟着变。
    public void SidebarChanged() => BuildSidebar();

    void BuildSidebar()
    {
        var hosts = _store.Settings.Hosts;
        var display = _engine.DisplayLink;
        var key = $"{_kind}|{_hostID}|{_store.Settings.ProgressRules.Count}|{_store.Settings.UsbDisplay}|{_store.Settings.BillingEnabled}|{_store.Settings.PhonePush.Enabled}|{_store.Settings.PhonePush.Scope}|{_engine.PhonePush.HasKey}|{display?.Connected}|" + string.Join(";", hosts.Select(h =>
        {
            var s = _engine.Snapshots.GetValueOrDefault(h.Id);
            return $"{h.Id}|{h.DisplayName}|{h.Enabled}|{h.Paused}|{h.Muted}|{h.Kind}|{s?.Health}|{s?.RunningCount}|{s?.PendingCount}";
        }));
        if (key == _sidebarKey) return;
        _sidebarKey = key;
        var k = new SettingsKit(_p);
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(10, 14, 10, 10) };
        list.Children.Add(SidebarRow(k, L("通用", "General"), L("轮询、提醒、提示音", "Refresh, alerts, sounds"), "gear", Color.Parse("#8E8E93"),
            _kind == SettingsPageKind.General, () => Select(SettingsPageKind.General)));
        var rules = _store.Settings.ProgressRules.Count;
        list.Children.Add(SidebarRow(k, L("进度识别", "Progress Detection"),
            rules == 0 ? L("内置软件、自定义规则", "Built-in apps, custom rules") : L($"{rules} 条自定义规则", $"{rules} custom rules"),
            "wand", Color.Parse("#5856D6"), _kind == SettingsPageKind.Rules, () => Select(SettingsPageKind.Rules)));
        list.Children.Add(SidebarRow(k, L("计费", "Billing"),
            _store.Settings.BillingEnabled ? L("已开启 · 估算费用", "On · cost estimates") : L("按卡时估算花费", "Estimate GPU-hour costs"),
            "card", Color.Parse("#F59E0B"), _kind == SettingsPageKind.Billing, () => Select(SettingsPageKind.Billing)));
        var push = _store.Settings.PhonePush;
        list.Children.Add(SidebarRow(k, L("手机推送", "Phone Push"),
            push.Enabled && _engine.PhonePush.HasKey ? L("已开启 · ", "On · ") + push.Scope.ShortLabel() : L("任务结束推送到微信", "Job alerts to WeChat / WeCom"),
            "phone", PhonePushPage.Tint, _kind == SettingsPageKind.Push, () => Select(SettingsPageKind.Push)));
        // 外接副屏：只有在通用页底部打开以后才出现。
        if (_store.Settings.UsbDisplay)
            list.Children.Add(SidebarRow(k, L("副屏", "Display"), display?.Connected == true ? L("已连接", "Connected") : L("ESP32 小屏幕", "ESP32 screen"),
                "display", Color.Parse("#14B8A6"), _kind == SettingsPageKind.Display, () => Select(SettingsPageKind.Display)));
        list.Children.Add(k.Text(L("服务器", "Servers"), 11.5, FontWeight.SemiBold, _p.Secondary).Margin2(10, 14, 0, 4));
        foreach (var h in hosts)
        {
            var host = h;
            var snap = _engine.Snapshots.GetValueOrDefault(h.Id);
            list.Children.Add(Reorderable(HostRow(k, host, snap, _kind == SettingsPageKind.Host && _hostID == h.Id, () => Select(SettingsPageKind.Host, host.Id)), host.Id));
        }
        if (hosts.Count > 1)
            list.Children.Add(k.Text(L("拖动或右键可调整顺序", "Drag or right-click to reorder"), 10.5, c: _p.Secondary)
                .Margin2(10, 6, 0, 0));
        var add = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Icon("plus", 13, Brushes.White, 2.8), new TextBlock { Text = L("添加服务器", "Add Server"), Foreground = Brushes.White } } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Color.Parse("#2EAD87").B(), Margin = new Thickness(12), Padding = new Thickness(0, 9),
        };
        add.Click += async (_, _) =>
        {
            var added = await AddHostDialog.Show(this, _store.Settings.Hosts);
            if (added.Count == 0) return;
            _store.Settings.Hosts.AddRange(added);
            _store.Touch();
            Select(SettingsPageKind.Host, added[0].Id);
        };
        add.Margin = new Thickness(0);
        // 托盘图标可能被系统收起，设置窗口里也放一个退出入口
        var quit = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Icon("power", 12, _p.Secondary.B(), 2), k.Text(L("退出 ServerIsland", "Quit ServerIsland"), 12.5, c: _p.Secondary) } },
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brushes.Transparent, Padding = new Thickness(0, 4),
        };
        ToolTip.SetShowDelay(quit, 250);
        ToolTip.SetTip(quit, L("完全退出（灵动岛、桌宠和托盘图标都会消失）", "Quit completely (the island, desktop pet and tray icon go away)"));
        quit.Click += (_, _) => (Application.Current as App)?.Quit();
        var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(12), Children = { add, quit } };
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _sidebar.Background = _p.Sidebar.B();
        _sidebar.BorderBrush = _p.Separator.B();
        _sidebar.BorderThickness = new Thickness(0, 0, 1, 0);
        _sidebar.Child = dock;
    }

    /// 服务器行可以上下拖动，或者用右键菜单调整顺序（灵动岛里也按这个顺序显示）。
    Control Reorderable(Control row, Guid id)
    {
        int Index() => _store.Settings.Hosts.FindIndex(h => h.Id == id);
        void Move(int to)
        {
            var hosts = _store.Settings.Hosts;
            var i = Index();
            to = Math.Clamp(to, 0, hosts.Count - 1);
            if (i < 0 || to == i) return;
            var h = hosts[i];
            hosts.RemoveAt(i);
            hosts.Insert(to, h);
            _store.Touch();
            BuildSidebar();
        }
        MenuItem Item(string title, Func<int> target, bool enabled)
        {
            var m = new MenuItem { Header = title, IsEnabled = enabled };
            m.Click += (_, _) => Move(target());
            return m;
        }
        var count = _store.Settings.Hosts.Count;
        var index = Index();
        var menu = new ContextMenu();
        menu.Items.Add(Item(L("移到最上面", "Move to Top"), () => 0, index > 0));
        menu.Items.Add(Item(L("上移", "Move Up"), () => Index() - 1, index > 0));
        menu.Items.Add(Item(L("下移", "Move Down"), () => Index() + 1, index < count - 1));
        menu.Items.Add(Item(L("移到最下面", "Move to Bottom"), () => count - 1, index < count - 1));
        row.ContextMenu = menu;

        // 拖动：按下后上下移动超过 6 像素开始拖，松开时按移动的行数换位置（松开不再触发点击）。
        double? startY = null;
        var dragging = false;
        double Y(PointerEventArgs e) => e.GetPosition(row.GetVisualParent() ?? row).Y;
        row.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { startY = Y(e); dragging = false; }
        }, RoutingStrategies.Tunnel);
        row.AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (startY is not { } y0 || !e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            var dy = Y(e) - y0;
            if (!dragging && Math.Abs(dy) > 6) { dragging = true; row.Opacity = 0.75; row.ZIndex = 1; }
            if (dragging) row.RenderTransform = new TranslateTransform(0, dy);
        }, RoutingStrategies.Tunnel);
        row.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (startY is { } y0 && dragging)
            {
                e.Handled = true;
                var steps = (int)Math.Round((Y(e) - y0) / Math.Max(row.Bounds.Height + 2, 1));
                row.RenderTransform = null; row.Opacity = 1; row.ZIndex = 0;
                Dispatcher.UIThread.Post(() => Move(Index() + steps));
            }
            startY = null; dragging = false;
        }, RoutingStrategies.Tunnel);
        return row;
    }

    Control SidebarRow(SettingsKit k, string title, string subtitle, string icon, Color tint, bool selected, Action action)
    {
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { k.Text(title, 13), k.Text(subtitle, 11, c: _p.Secondary) } };
        var row = new DockPanel { Children = { k.IconSquare(icon, tint, 26).Margin2(0, 0, 10, 0), texts } };
        DockPanel.SetDock(row.Children[0], Dock.Left);
        return Selectable(row, selected, action);
    }

    Control HostRow(SettingsKit k, HostConfig host, HostSnapshot? snap, bool selected, Action action)
    {
        var icon = k.IconSquare(host.Kind.IsScheduler() ? "stack" : "server",
            host.Kind switch { HostKind.Slurm => Color.Parse("#A855F7"), HostKind.Pbs => Color.Parse("#6366F1"), _ => Color.Parse("#14B8A6") }, 26);
        var dotColor = host.Enabled ? Hue.For(snap?.Health ?? HostHealth.Unknown) : Color.Parse("#8E8E93");
        if (!host.Enabled || snap == null || snap.Health.Kind == HealthKind.Unknown) dotColor = host.Enabled ? Color.Parse("#8E8E93") : dotColor;
        var dot = new Border
        {
            Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = dotColor.B(),
            BorderBrush = _p.Sidebar.B(), BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -3, -3),
        };
        var iconBox = new Grid { Children = { icon, dot }, Margin = new Thickness(0, 0, 10, 0) };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { k.Text(host.DisplayName, 13) } };
        if (host.Muted) nameRow.Children.Add(new Icon("bellSlash", 10, _p.Secondary.B()) { VerticalAlignment = VerticalAlignment.Center });
        if (host.Paused) nameRow.Children.Add(new Icon("pause", 9, _p.Secondary.B()) { VerticalAlignment = VerticalAlignment.Center });
        string subtitle;
        if (!host.Enabled) subtitle = L("已停用", "Disabled");
        else if (host.Paused) subtitle = L("已暂停", "Paused");
        else subtitle = (snap?.Health.Kind ?? HealthKind.Unknown) switch
        {
            HealthKind.Ok => (snap!.RunningCount + snap.PendingCount) == 0 ? host.Kind.Label() + L(" · 空闲", " · idle")
                : L($"运行 {snap.RunningCount} · 排队 {snap.PendingCount}", $"{snap.RunningCount} running · {snap.PendingCount} pending"),
            HealthKind.NeedsLogin => L("需要登录", "Login required"),
            HealthKind.Error => snap!.Health.Message,
            _ => L("连接中…", "Connecting…"),
        };
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { nameRow, k.Text(subtitle, 11, c: _p.Secondary) } };
        var row = new DockPanel { Children = { iconBox, texts } };
        DockPanel.SetDock(iconBox, Dock.Left);
        return Selectable(row, selected, action);
    }

    Control Selectable(Control content, bool selected, Action action)
    {
        var b = new Clickable(action)
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6),
            Normal = selected ? _p.Selection : Colors.Transparent,
            Hover = selected ? _p.Selection : _p.Separator,
            Child = content,
        };
        b.Cursor = Avalonia.Input.Cursor.Default;
        return b;
    }

    void BuildPage()
    {
        var k = new SettingsKit(_p);
        _pageEngineUpdate = null;
        _pageDetach?.Invoke();
        _pageDetach = null;
        Control page;
        // 关掉"外接副屏"时，副屏页从侧栏消失：回到通用页。
        if (_kind == SettingsPageKind.Display && !_store.Settings.UsbDisplay) _kind = SettingsPageKind.General;
        switch (_kind)
        {
            case SettingsPageKind.Display:
                var dp = new DisplayPage(this, k, _store, _engine);
                page = dp.Build();
                _pageDetach = dp.Attach();
                break;
            case SettingsPageKind.Host when _store.Host(_hostID) is { } host:
                var hp = new HostPage(this, k, _store, _engine, _prompter, host);
                page = hp.Build();
                _pageEngineUpdate = hp.EngineChanged;
                break;
            case SettingsPageKind.Rules:
                page = new RulesPage(this, k, _store, _engine).Build();
                break;
            case SettingsPageKind.Billing:
                page = new BillingPage(this, k, _store, _engine).Build();
                break;
            case SettingsPageKind.Push:
                var pp = new PhonePushPage(this, k, _store, _engine);
                page = pp.Build();
                _pageDetach = pp.Attach();
                break;
            default:
                _kind = SettingsPageKind.General;
                var gp = new GeneralPage(this, k, _store, _engine);
                page = gp.Build();
                _pageEngineUpdate = gp.EngineChanged;
                break;
        }
        _scroll.Content = new Border { Padding = new Thickness(28, 22, 28, 28), Child = page, MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    public void HostRemoved()
    {
        _kind = SettingsPageKind.General;
        RebuildAll();
    }
}
