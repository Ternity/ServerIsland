using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Platform;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;
using static ServerIsland.Desktop.Ui.Kit;

namespace ServerIsland.Desktop.Island;

/// 按当前状态构建刘海里的内容（与 Swift 版 IslandView.swift 对应）。数据变化时整棵重建；走字由 LiveScope 每秒更新。
public sealed class IslandViews(MonitorEngine engine, ConfigStore store, IslandState st, ILoginPrompter prompter)
{
    readonly IslandPages _pages = new(engine, st);

    // MARK: - 收起：两侧的小翅膀

    public Control Collapsed()
    {
        int running = engine.TotalRunning, pending = engine.TotalPending;
        var left = H(5, new PetView(engine.PetMood, 17));
        if (engine.HasProblem && running == 0) left.Children.Add(new Icon("exclamation", 11, LeftColor().B(), 3.2));
        else if (running > 0) left.Children.Add(T($"{running}", 12.5, FontWeight.SemiBold));
        left.HorizontalAlignment = HorizontalAlignment.Center;

        Control right;
        if (engine.LeadJob is { Fraction: { } f } lead)
            right = H(5, new ProgressRing { Fraction = f, LineWidth = 2.4, Width = 13, Height = 13 }, T(Format.Percent(f), 11, FontWeight.SemiBold, lead.HasRealProgress ? Hue.Mint : Hue.Secondary));
        else if (pending > 0)
            right = H(4, new Icon("hourglass", 10, Hue.Amber.B(), 2.8), T($"{pending}", 12, FontWeight.SemiBold, Hue.Amber));
        else if (engine.Snapshots.Values.SelectMany(s => s.Gpus).OrderByDescending(g => g.Utilization).FirstOrDefault() is { } gpu)
            right = H(4, new Icon("cpu", 10, Hue.Secondary.B(), 2.4), T($"{(int)gpu.Utilization}%", 11, FontWeight.SemiBold, Hue.Secondary));
        else
            right = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = (engine.HasProblem ? LeftColor() : Hue.Mint.WithAlpha(0.7)).B() };
        right.HorizontalAlignment = HorizontalAlignment.Center;
        right.VerticalAlignment = VerticalAlignment.Center;

        var g = new Grid { ColumnDefinitions = new ColumnDefinitions($"{IslandLayout.Wing},*,{IslandLayout.Wing}"), Height = st.Geometry.NotchHeight };
        g.Children.Add(left);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return g;
    }

    Color LeftColor()
    {
        if (engine.HasError) return Hue.Coral;
        if (engine.HasProblem) return Hue.Amber;
        if (engine.TotalRunning > 0) return Hue.Mint;
        if (engine.TotalPending > 0) return Hue.Amber;
        return Hue.Mint.WithAlpha(0.5);
    }

    // MARK: - 事件提示

    public Control Toast(IslandEvent ev)
    {
        var tint = ev.Kind switch
        {
            EventKind.Finished => Hue.Mint,
            EventKind.Failed => Hue.Coral,
            EventKind.Started => Hue.Cyan,
            EventKind.Info => Hue.Blue,
            _ => Hue.Amber,
        };
        var symbol = ev.Kind switch
        {
            EventKind.Finished => "check",
            EventKind.Failed => "xmark",
            EventKind.Started => "play",
            EventKind.Info => "info",
            _ => "exclamation",
        };
        var mood = ev.Kind switch { EventKind.Failed => PetMood.Sad, EventKind.Finished => PetMood.Happy, EventKind.Warning => PetMood.Worried, _ => PetMood.Working };

        // 硬件刘海在正中间：服务器名只能占左半边（和 Swift 版的 Spacer(minLength: notchWidth + 16) 一样）
        var toastWidth = Math.Max(st.Geometry.NotchWidth + 240, IslandLayout.ToastMinWidth);
        var hostName = T(ev.HostName, 10.5, FontWeight.SemiBold, Hue.Secondary);
        if (st.Geometry.NotchWidth > 0) hostName.MaxWidth = Math.Max(40, (toastWidth - st.Geometry.NotchWidth) / 2 - 18 - 8);
        var top = Row(16, Fill(hostName), T(Format.Time(ev.Date), 10.5, FontWeight.Medium, Hue.Tertiary))
            .Margin(18, 0, 18, 0).Hgt(st.Geometry.NotchHeight);

        var badge = new Border
        {
            Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5), Background = tint.B(),
            BorderBrush = Brushes.Black, BorderThickness = new Thickness(1.5),
            Child = new Icon(symbol, 8, Brushes.Black, 4) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -4, 0),
        };
        var pet = new Grid { Width = 40, Height = 40 };
        pet.Children.Add(new PetView(mood, 30).HA(HorizontalAlignment.Center).VA(VerticalAlignment.Center));
        pet.Children.Add(badge);

        var texts = V(3, T(ev.Title, 14, FontWeight.SemiBold));
        if (ev.Subtitle.Length > 0) texts.Children.Add(T(ev.Subtitle, 11, color: Hue.Secondary));
        var body = Row(12, pet, Fill(texts.VA(VerticalAlignment.Center))).Margin(18, 0, 18, 0).Hgt(IslandLayout.ToastBody - 8);

        var countdown = new Countdown(ev.Date, tint.WithAlpha(0.55)) { Height = 2, Margin = new Thickness(40, 0, 40, 5), VerticalAlignment = VerticalAlignment.Bottom };
        var g = new Grid();
        g.Children.Add(V(0, top, body));
        g.Children.Add(countdown);
        return g;
    }

    /// 自动收起倒计时（5 秒）。
    sealed class Countdown(DateTime start, Color color) : Control
    {
        DispatcherTimer? _timer;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (Preview.PreviewRenderer.Active) return;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => InvalidateVisual());
            _timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer?.Stop();
        }

        public override void Render(DrawingContext ctx)
        {
            var f = Preview.PreviewRenderer.Active ? 0.6 : Math.Clamp(1 - (DateTime.Now - start).TotalSeconds / 5, 0, 1);
            var w = Bounds.Width * f;
            ctx.DrawRectangle(color.B(), null, new RoundedRect(new Rect((Bounds.Width - w) / 2, 0, w, 2), 1));
        }
    }

    // MARK: - 展开面板

    public Control Expanded(double contentHeight, bool scrolls, double shapeWidth)
    {
        var hosts = engine.OrderedHosts;
        Control body;
        if (st.Page.Kind != PageKind.List) body = Page();
        else if (hosts.Count == 0) body = EmptyState();
        else if (scrolls)
        {
            var sv = new ScrollViewer { Content = Cards(hosts), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var restore = st.ListOffset;
            sv.ScrollChanged += (_, _) => st.ListOffset = sv.Offset;
            Dispatcher.UIThread.Post(() => sv.Offset = restore, DispatcherPriority.Loaded);
            body = sv;
        }
        else body = Cards(hosts);
        body.Height = contentHeight;
        body.VerticalAlignment = VerticalAlignment.Top;

        var col = new StackPanel { Margin = new Thickness(IslandLayout.SidePad, 0) };
        col.Children.Add(Header(shapeWidth).Hgt(st.Geometry.NotchHeight));
        col.Children.Add(new Control { Height = IslandLayout.TopGap });
        col.Children.Add(body);
        return col;
    }

    Control Cards(List<(HostConfig Host, HostSnapshot Snap)> hosts)
    {
        var (cards, idle) = IslandLayout.Groups(hosts, store.Settings.FoldIdleHosts);
        var col = new StackPanel { Spacing = IslandLayout.CardGap };
        foreach (var (h, s) in cards) col.Children.Add(Card(h, s));
        if (idle.Count > 0)
        {
            col.Children.Add(HostCardView.IdleHostsRow(idle, st.ShowIdle, () => { st.ShowIdle = !st.ShowIdle; st.RequestRebuild(); }).Hgt(IslandLayout.IdleRow));
            if (st.ShowIdle) foreach (var (h, s) in idle) col.Children.Add(Card(h, s));
        }
        return col;
    }

    Control Card(HostConfig host, HostSnapshot snap)
    {
        var id = host.Id;
        var a = new CardActions
        {
            Login = () => _ = engine.Login(host, prompter),
            Refresh = () => engine.Refresh(id),
            OpenJob = job => { st.PinnedJob = job; st.ReturnPage = null; st.Page = new IslandPage(PageKind.Job, id, job.Id); },
            History = () => st.Page = new IslandPage(PageKind.History, id),
            Picker = () => st.Page = new IslandPage(PageKind.Picker, id),
            AllJobs = () => st.OpenAllJobs(id),
            Partitions = () => st.Page = new IslandPage(PageKind.Partitions, id),
            SwitchToDirect = () => engine.SetKind(id, HostKind.Direct),
            TogglePause = () => engine.SetPaused(id, !host.Paused),
            SetInterval = s => engine.SetHostInterval(id, s),
            ToggleMute = () => engine.ToggleMute(id),
            TerminalLogin = () => Shell.OpenLoginInTerminal(host),
            Terminal = () => Shell.OpenShellInTerminal(host),
            CanTerminalLogin = engine.Ssh is OpenSshTransport && OpenSshTransport.SupportsControlMaster,
            GlobalInterval = store.Settings.DefaultInterval,
            GlobalAuto = store.Settings.AutoRefresh,
            LoggingIn = engine.LoggingIn.Contains(id),
            ToggleStar = job => engine.ToggleStar(id, job),
            Starred = store.Settings.StarredJobs.Where(s => s.HostID == id).Select(s => s.JobID).ToHashSet(),
        };
        return HostCardView.Build(host, snap, a, st);
    }

    Control Page()
    {
        var p = st.Page;
        if (p.Kind == PageKind.Jobs)
            return _pages.AllJobs((host, job) =>
            {
                st.PinnedJob = job;
                st.ReturnPage = st.Page;
                st.Page = new IslandPage(PageKind.Job, host.Id, job.Id);
            });
        if (p.Kind == PageKind.Costs) return _pages.CostPage();
        if (store.Host(p.Host) is not { } host) return new Control();
        switch (p.Kind)
        {
            case PageKind.Job:
                var snap = engine.Snapshots.GetValueOrDefault(p.Host);
                // 数组元素嵌在分组行里，也要能找到。
                var live = snap?.Jobs.FirstOrDefault(j => j.Id == p.Job)
                           ?? snap?.Jobs.Where(j => j.Array != null).SelectMany(j => j.Array!.Running).FirstOrDefault(j => j.Id == p.Job);
                if ((live ?? st.PinnedJob) is not { } job) return new Control();
                return _pages.JobDetail(host, job, snap?.LastUpdate, live != null, e =>
                {
                    st.PinnedJob = e;
                    st.Page = new IslandPage(PageKind.Job, p.Host, e.Id);
                });
            case PageKind.History: return _pages.History(host);
            case PageKind.Picker: return _pages.Picker(host);
            case PageKind.Partitions: return _pages.PartitionsPage(host);
            default: return new Control();
        }
    }

    Control Header(double shapeWidth)
    {
        double ear = st.Floating ? 0 : 14;
        double sideWidth = Math.Max(0, (shapeWidth - 2 * ear - 2 * IslandLayout.SidePad - st.Geometry.NotchWidth) / 2 - 6);
        Control Summary(string icon, int value, Color c, string help) =>
            H(3, new Icon(icon, 9.5, (value > 0 ? c : Hue.Tertiary).B(), 2.8), T($"{value}", 11, FontWeight.SemiBold, value > 0 ? c : Hue.Tertiary)).Tip(help);
        var left = H(7, new PetView(engine.PetMood, 15), T("ServerIsland", 12.5, FontWeight.SemiBold),
            Summary("bolt", engine.TotalRunning, Hue.Mint, L("运行中", "Running")).Margin(4, 0, 0, 0),
            Summary("hourglass", engine.TotalPending, Hue.Amber, L("排队中", "Pending")));
        if (store.Settings.IsQuiet())
            left.Children.Add(new Icon("moon", 10, Hue.Tertiary.B())
                .Tip(L($"勿扰时段（{store.Settings.QuietLabel}）：不响铃、不弹窗", $"Quiet hours ({store.Settings.QuietLabel}): no sounds or pop-ups")));
        if (engine.Demo) left.Children.Add(Capsule(T(L("演示", "Demo"), 9, FontWeight.Bold, Hue.Amber), Hue.Amber.WithAlpha(0.16), 5, 1.5));
        left.Width = sideWidth;

        bool onJobs = st.Page.Kind == PageKind.Jobs;
        bool onCosts = st.Page.Kind == PageKind.Costs;
        var right = H(8,
            GlobalPollMenu(),
            store.Settings.BillingEnabled
                ? IconButton(Billing.IconFor(store.Settings.Currency), L("费用：今天、本月、按分区和作业（估算）", "Costs: today, this month, by partition and job (estimate)"),
                             () => st.Page = onCosts ? IslandPage.List : new IslandPage(PageKind.Costs), active: onCosts)
                : null,
            IconButton("list", L("全部任务：所有服务器的任务，可搜索、筛选", "All jobs across servers: search and filter"), () =>
            {
                if (onJobs) st.Page = IslandPage.List; else st.OpenAllJobs();
            }, active: onJobs),
            IconButton(st.Pinned ? "pinFill" : "pin", st.Pinned ? L("取消固定", "Unpin") : L("固定面板（鼠标移开也不收起）", "Pin (stay open when the mouse leaves)"), () =>
            {
                st.Pinned = !st.Pinned;
                if (!st.Pinned) st.Unpinned();
                st.RequestRebuild();
            }, active: st.Pinned),
            IconButton("refresh", L("立即刷新", "Refresh now"), engine.RefreshAll, spinning: engine.IsRefreshing),
            IconButton("gear", L("设置", "Settings"), st.OpenSettings));
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var rightBox = new Border { Width = sideWidth, Child = right };
        return Row(0, left, Spacer, rightBox).Margin(0, 2, 0, 0);
    }

    /// 头部的全局轮询菜单：间隔 + 仅手动刷新。
    Control GlobalPollMenu()
    {
        bool auto = store.Settings.AutoRefresh;
        var s = store.Settings.DefaultInterval;
        var label = auto ? HostCardView.ShortInterval(s) : L("手动", "manual");
        var c = auto ? Hue.Secondary : Hue.Amber;
        var icon = new Icon(auto ? "timer" : "hand", 10.5, c.B(), 2.5);
        var text = T(label, 10.5, FontWeight.SemiBold, c);
        Clickable? b = null;
        b = new Clickable(() =>
        {
            var entries = new List<Menus.Entry> { new(L("自动刷新间隔", "Auto refresh interval"), Header: true) };
            entries.AddRange(HostCardView.Presets.Select(p => new Menus.Entry(L($"每 {HostCardView.IntervalLabel(p)}", $"Every {HostCardView.IntervalLabel(p)}"),
                () => engine.SetGlobalInterval(p), auto && s == p)));
            entries.Add(Menus.Sep);
            entries.Add(new(L("仅手动刷新", "Manual refresh only"), () => engine.SetAutoRefresh(!store.Settings.AutoRefresh), !auto));
            entries.Add(Menus.Sep);
            entries.Add(new(L("单台服务器可以在卡片上单独设置或暂停", "Each server can be set or paused on its card"), Header: true));
            Menus.Show(b!, entries, st);
        })
        {
            Height = 24,
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(12),
            Normal = auto ? Hue.W(0.07) : Hue.Amber.WithAlpha(0.16),
            Hover = auto ? Hue.W(0.14) : Hue.Amber.WithAlpha(0.24),
            Child = H(3, icon, text),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.HoverChanged = h => { if (auto) { var cc = (h ? Hue.Text : Hue.Secondary).B(); icon.Brush = cc; icon.InvalidateVisual(); text.Foreground = cc; } };
        return b.Tip(auto ? L("自动刷新间隔（点击修改，或改为仅手动刷新）", "Auto refresh interval (click to change)") : L("当前为仅手动刷新", "Manual refresh only"));
    }

    Control EmptyState()
    {
        var button = new Clickable(st.OpenSettings)
        {
            CornerRadius = new CornerRadius(100), Padding = new Thickness(12, 5),
            Normal = Hue.Mint.WithAlpha(0.18), Hover = Hue.Mint.WithAlpha(0.26),
            Child = T(L("从 ~/.ssh/config 添加…", "Add from ~/.ssh/config…"), 11, FontWeight.SemiBold, Hue.Mint),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var col = V(8, new PetView(PetMood.Idle, 30).HA(HorizontalAlignment.Center),
            T(L("还没有添加服务器", "No servers yet"), 13, FontWeight.SemiBold).HA(HorizontalAlignment.Center), button);
        col.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Height = IslandLayout.EmptyState, CornerRadius = new CornerRadius(16), Background = Hue.Card.B(), Child = col };
    }
}
