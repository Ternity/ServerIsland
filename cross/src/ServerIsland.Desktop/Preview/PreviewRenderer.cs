using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Desktop.Island;
using ServerIsland.Desktop.Settings;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Preview;

/// `--render-preview <目录> [zh|en]`：用演示数据把刘海的各个状态、二级页面、设置窗口离屏渲染成 PNG（不需要显示器）。
/// 用于改界面之后自查，也用于 README / 官网截图。
public static class PreviewRenderer
{
    public static bool Active { get; private set; }

    public static int Run(string dir, AppLanguage lang)
    {
        Active = true;
        Lang.Setting = lang;
        Directory.CreateDirectory(dir);
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var store = new ConfigStore(persist: false);
        Lang.Setting = lang;
        var demo = DemoData.Settings();
        demo.Language = lang;
        store.Replace(demo);
        store.Settings.ProgressRules = [new ProgressRule { Name = "PyTorch", File = "*.log", Pattern = "epoch", Unit = "epoch" }];
        Lang.Setting = lang;
        MonitorEngine.NoNetwork = true;
        var engine = new MonitorEngine(store, demo: true, history: new HistoryStore(persist: false));
        foreach (var h in store.Settings.Hosts) engine.SetSnapshot(DemoData.Snapshot(h, 3), h.Id);
        // 演示关注：每台有任务的服务器关注一个排在后面的任务（关注的任务排到前面）
        foreach (var h in store.Settings.Hosts)
            if (engine.Snapshots[h.Id].Jobs.Skip(1).FirstOrDefault(j => j.ArrayJobID == null || j.Array != null) is { } sj) engine.ToggleStar(h.Id, sj);
        var prompter = new LoginPrompter();
        int count = 0;

        void Save(TopLevel top, string name)
        {
            for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            var frame = top.CaptureRenderedFrame();
            if (frame == null) { Console.WriteLine("✗ " + name); return; }
            frame.Save(Path.Combine(dir, name + ".png"));
            count++;
            Console.WriteLine("✓ " + name);
        }

        // —— 刘海 ——
        Window IslandScene(IslandMode mode, IslandState st, double width = 760, double height = 640)
        {
            var views = new IslandViews(engine, store, st, prompter);
            var hosts = engine.OrderedHosts;
            var ch = st.ContentHeight(hosts, store.Settings.FoldIdleHosts);
            var spec = IslandLayout.Spec(mode, st.Geometry, ch, st.Floating);
            var surface = new IslandSurface { Floating = st.Floating };
            surface.SetTarget(spec, mode == IslandMode.Expanded ? 1 : mode == IslandMode.Toast ? 0.6 : 0, animate: false);
            LiveScope.Begin();
            Control? content = mode switch
            {
                IslandMode.Collapsed => views.Collapsed(),
                IslandMode.Toast => views.Toast(new IslandEvent(EventKind.Finished, L("relax_LiCoO2_hse 已完成", "relax_LiCoO2_hse finished"),
                    L("VASP 结构优化 23 离子步 · 用时 6:41:10", "VASP relaxation 23 ionic steps · took 6:41:10"), L("HPC 集群", "HPC Cluster"))),
                IslandMode.Expanded => views.Expanded(Math.Min(ch, IslandLayout.MaxContentHeight), spec.Scrolls, spec.Width),
                _ => null,
            };
            LiveScope.End();
            surface.SetContent(content, fancy: false);
            var bg = new Grid { Background = Wallpaper() };
            bg.Children.Add(new Border { Height = 40, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) });
            surface.Width = IslandLayout.PanelSize(st.Geometry).Width;
            surface.Height = IslandLayout.PanelSize(st.Geometry).Height;
            surface.HorizontalAlignment = HorizontalAlignment.Center;
            surface.VerticalAlignment = VerticalAlignment.Top;
            bg.Children.Add(surface);
            var w = new Window { Width = width, Height = height, Content = bg, RequestedThemeVariant = ThemeVariant.Dark };
            w.Show();
            return w;
        }

        var hpc = store.Settings.Hosts[0];
        var box = store.Settings.Hosts[1];
        var hpcSnap = engine.Snapshots[hpc.Id];
        var boxSnap = engine.Snapshots[box.Id];

        Save(IslandScene(IslandMode.Hidden, new IslandState(), 760, 120), "island-hidden");
        Save(IslandScene(IslandMode.Collapsed, new IslandState(), 760, 120), "island-collapsed");
        Save(IslandScene(IslandMode.Toast, new IslandState(), 760, 200), "island-toast");
        Save(IslandScene(IslandMode.Expanded, new IslandState { Hovering = true }), "island-expanded");

        // 任务详情（NEP 训练，带曲线和日志）
        var nepJob = boxSnap.Jobs[0];
        var st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Job, box.Id, nepJob.Id);
        st.PageState = new JobDetailState
        {
            Started = true,
            Detail = new JobDetail
            {
                Fields = [(L("PID / 父进程", "PID / parent"), "41022 / 40990"), (L("用户", "User"), "demo"), (L("已运行", "Elapsed"), "1:30:26"),
                          (L("CPU / 内存", "CPU / MEM"), "99.2% / 1.4%"), (L("常驻内存", "RSS"), "1.8G"), (L("状态", "State"), "Rl"),
                          (L("命令", "Command"), "nep")],
                Stdout = new JobDetail.LogTail
                {
                    Path = DemoData.Home + "/Li3PS4/nep-train/nep.log",
                    Lines = ["Generation = 8000, Total = 0.1815, L1 = 0.0450, L2 = 0.0640", "  RMSE-E-train = 0.74 meV/atom", "  RMSE-F-train = 70.98 meV/A",
                             "  RMSE-V-train = 11.31 meV/atom", "Generation = 8020, Total = 0.1812", "WARNING: learning rate decayed", "Generation = 8040, Total = 0.1809"],
                },
            },
            Curves =
            [new AppCurve
            {
                Title = "Loss", LogY = true, XLabel = L("代", "gen"),
                SeriesList =
                [
                    new("Total", Enumerable.Range(1, 80).Select(i => (i * 100.0, 2.0 / Math.Sqrt(i) + 0.15)).ToList()),
                    new(L("能量", "Energy"), Enumerable.Range(1, 80).Select(i => (i * 100.0, 0.05 / i + 0.0007)).ToList()),
                    new(L("力", "Force"), Enumerable.Range(1, 80).Select(i => (i * 100.0, 0.6 / Math.Sqrt(i) + 0.07)).ToList()),
                    new(L("维里", "Virial"), Enumerable.Range(1, 80).Select(i => (i * 100.0, 0.2 / Math.Sqrt(i) + 0.011)).ToList()),
                ],
            }],
        };
        Save(IslandScene(IslandMode.Expanded, st), "page-job-detail");

        // VASP 结构优化：能量和最大力两张图并排（自查用，字段沿用上一张）
        if (st.PageState is JobDetailState jd)
        {
            jd.Curves = DemoData.Curves(new AppProgress { Kind = AppKind.Vasp, Dir = DemoData.Home + "/LiCoO2/relax-hse", Vasp = VaspMode.Relax });
            Save(IslandScene(IslandMode.Expanded, st), "page-job-detail-vasp");
            // 共轭梯度（IBRION=2）的真实走势：最大力上下跳，按能量收敛（EDIFFG 默认 0.001）。数值来自一个真实的分子优化。
            double[] cgF = [2.90587, 5.55486, 1.01194, 2.59552, 0.612749, 0.941168, 0.464911, 0.686184, 0.828412, 0.327084, 0.471256, 0.275648, 0.295926, 0.166203, 0.459653, 0.253956, 0.328663, 0.225154, 0.4237, 0.527276, 0.213997, 0.160526, 0.190614, 0.125337, 0.25902, 0.124044, 0.136184, 0.133553, 0.367229, 0.294823, 0.165715, 0.144307, 0.105007, 0.0775564, 0.147446];
            double[] cgE = [-89.7657, -89.0658, -89.9134, -89.8975, -89.953, -89.9662, -89.9771, -90, -90.0004, -90.0144, -90.0161, -90.0237, -90.0303, -90.0359, -90.0381, -90.0421, -90.0444, -90.0496, -90.0535, -90.0557, -90.0572, -90.0594, -90.0597, -90.0616, -90.0624, -90.0638, -90.064, -90.0658, -90.0679, -90.0688, -90.0701, -90.0709, -90.0719, -90.0725, -90.0728];
            jd.Curves = new List<AppCurve?>
            {
                AppCurves.Curve(new AppCurves.Source("", AppCurves.CurveKind.VaspEnergy), cgE.Select((v, i) => new[] { i + 1.0, v }).ToList()),
                AppCurves.Curve(new AppCurves.Source("", AppCurves.CurveKind.VaspForce), cgF.Select(v => new[] { 0.0, v }).ToList(), new() { ["ediffg"] = "0.1E-02" }),
            }.Where(c => c != null).Select(c => c!).ToList();
            Save(IslandScene(IslandMode.Expanded, st), "page-job-detail-vasp-cg");
            // NEB：能垒图（曲线 + 每个像点的力柱 + EDIFFG 虚线）和能垒随离子步的变化。数值来自一个真实的 3 像点 NEB。
            var nebRows = new List<double[]> { new[] { 0, 0, -387.56731101 }, new[] { 0, -1, 0.009691 }, new[] { 4, 0, -387.58911885 }, new[] { 4, -1, 0.008842 },
                                                new[] { 1, -1, 0.006768 }, new[] { 2, -1, 0.004724 }, new[] { 3, -1, 0.006717 } };
            double[] nebFinal = [-387.25458648, -386.85825059, -387.26974483];
            for (int k = 1; k <= 30; k++)
                for (int j = 0; j < 3; j++) nebRows.Add([j + 1, k, nebFinal[j] + (j == 1 ? 1.13 : 0.4) * Math.Exp(-k / 4.5)]);
            jd.Curves = AppCurves.Curves(new AppCurves.Source("", AppCurves.CurveKind.Neb), nebRows, new() { ["ediffg"] = "-0.01", ["images"] = "3" });
            Save(IslandScene(IslandMode.Expanded, st), "page-job-detail-neb");
        }

        // 数组作业概览
        var eos = hpcSnap.Jobs.First(j => j.Array != null);
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Job, hpc.Id, eos.Id);
        st.PageState = new JobDetailState { Started = true };
        Save(IslandScene(IslandMode.Expanded, st), "page-array");

        // 取消确认
        var md = hpcSnap.Jobs[0];
        hpc.AllowCancel = true;
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Job, hpc.Id, md.Id);
        st.PageState = new JobDetailState { Started = true, Stage = CancelStageKind.Confirm, Detail = new JobDetail
        {
            Fields = [(L("状态", "State"), "RUNNING"), (L("已运行", "Elapsed"), "10:07:00"), (L("时间上限", "Time limit"), "2-00:00:00"), (L("分区", "Partition"), "cpu"),
                      (L("节点", "Nodes"), "cn[012-013]"), (L("工作目录", "Directory"), DemoData.Home + "/Li3PS4/aimd-900K")],
            Stdout = new JobDetail.LogTail { Path = DemoData.Home + "/Li3PS4/aimd-900K/slurm-8812044.out", Lines = ["  1240 T= 903. E= -.53E+03", "  1241 T= 899. E= -.53E+03"] },
        } };
        Save(IslandScene(IslandMode.Expanded, st), "page-cancel");
        hpc.AllowCancel = false;

        // 历史
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.History, hpc.Id);
        st.PageState = new HistoryState
        {
            Started = true, FromServer = true,
            Entries =
            [
                new HistoryEntry { Id = "8811920", Name = "relax_Li3PS4_pbe", State = "COMPLETED", Elapsed = 4_210, EndedAt = DateTime.Now.AddMinutes(-35), ExitCode = "0:0", Dir = "/home/demo/projects/Li3PS4/relax" },
                new HistoryEntry { Id = "8811877", Name = "aimd_LGPS_600K", State = "TIMEOUT", Elapsed = 172_800, EndedAt = DateTime.Now.AddHours(-3), ExitCode = "0:15" },
                new HistoryEntry { Id = "8811702", Name = "eos_scan", State = "COMPLETED", Elapsed = 1_800, EndedAt = DateTime.Now.AddHours(-6), Note = L("数组 38✓ 2✗", "array 38✓ 2✗") },
                new HistoryEntry { Id = "8811650", Name = "phonon_Si_333", State = "FAILED", Elapsed = 210, EndedAt = DateTime.Now.AddHours(-9), ExitCode = "1:0" },
                new HistoryEntry { Id = "8810412", Name = "nep_train_LiF", State = HistoryEntry.EndedUnknown, Elapsed = 86_400, EndedAt = DateTime.Now.AddDays(-3),
                                   Note = L("ServerIsland 未运行时结束", "ended while ServerIsland wasn't running"), Dir = "/home/demo/nep/LiF" },
            ],
        };
        Save(IslandScene(IslandMode.Expanded, st), "page-history");

        // 分区
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Partitions, hpc.Id);
        st.PageState = new PartitionsState
        {
            Started = true,
            Stats =
            [
                new PartitionStat { Name = "gpu-a100", TimeLimit = "2-00:00:00", Nodes = 8, IdleNodes = 1, MixNodes = 3, AllocNodes = 4, CpuIdle = 112, CpuTotal = 512, GpuIdle = 7, GpuTotal = 32, GpuModels = ["a100"] },
                new PartitionStat { Name = "cpu", IsDefault = true, TimeLimit = "7-00:00:00", Nodes = 64, IdleNodes = 9, MixNodes = 20, AllocNodes = 33, DownNodes = 2, CpuIdle = 1_480, CpuTotal = 4_096 },
                new PartitionStat { Name = "gpu-v100", TimeLimit = "1-00:00:00", Nodes = 6, AllocNodes = 6, CpuIdle = 0, CpuTotal = 240, GpuIdle = 0, GpuTotal = 24, GpuModels = ["v100"] },
                new PartitionStat { Name = "debug", Available = false, TimeLimit = "30:00", Nodes = 2, DownNodes = 2, CpuTotal = 64 },
            ],
        };
        Save(IslandScene(IslandMode.Expanded, st), "page-partitions");

        // 分区：曙光的 DCU
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Partitions, hpc.Id);
        st.PageState = new PartitionsState
        {
            Started = true,
            Stats =
            [
                new PartitionStat { Name = "dcu", IsDefault = true, TimeLimit = "7-00:00:00", Nodes = 64, IdleNodes = 4, MixNodes = 10, AllocNodes = 50, CpuIdle = 420, CpuTotal = 2_048, GpuIdle = 38, GpuTotal = 256, GpuModels = ["Hygon"], GpuKinds = ["dcu"] },
                new PartitionStat { Name = "cpu", TimeLimit = "7-00:00:00", Nodes = 40, IdleNodes = 6, MixNodes = 8, AllocNodes = 26, CpuIdle = 310, CpuTotal = 1_280 },
            ],
        };
        Save(IslandScene(IslandMode.Expanded, st), "page-partitions-dcu");

        // 监听进程
        st = new IslandState { Hovering = true };
        st.Page = new IslandPage(PageKind.Picker, box.Id);
        st.PageState = new PickerState { Started = true, Procs = boxSnap.Jobs.Select(j => j.Clone()).ToList() };
        Save(IslandScene(IslandMode.Expanded, st), "page-picker");

        // 全部任务（跨服务器）、只看关注的运行中任务、没有匹配
        st = new IslandState { Hovering = true };
        st.OpenAllJobs();
        Save(IslandScene(IslandMode.Expanded, st), "page-all-jobs");
        st = new IslandState { Hovering = true };
        st.OpenAllJobs();
        st.Jobs = new JobsFilter(Status: JobsStatus.Running, StarredOnly: true);
        Save(IslandScene(IslandMode.Expanded, st), "page-all-jobs-starred");
        st = new IslandState { Hovering = true };
        st.OpenAllJobs(hpc.Id);
        st.Jobs = st.Jobs with { Query = "zzz" };
        Save(IslandScene(IslandMode.Expanded, st), "page-all-jobs-empty");
        // 关注的任务的详情页（头部的星标）
        if (hpcSnap.Jobs.FirstOrDefault(j => engine.IsStarred(hpc.Id, j.Id)) is { } starredJob)
        {
            st = new IslandState { Hovering = true };
            st.Page = new IslandPage(PageKind.Job, hpc.Id, starredJob.Id);
            st.PageState = new JobDetailState { Started = true, Detail = new JobDetail { Fields = [(L("状态", "State"), "RUNNING"), (L("分区", "Partition"), "cpu")] } };
            Save(IslandScene(IslandMode.Expanded, st), "page-detail-starred");
        }

        // 桌宠模式：小章鱼 + 浮动面板
        st = new IslandState { Hovering = true, Floating = true };
        Save(IslandScene(IslandMode.Expanded, st), "pet-panel");

        // 官网用：桌面上的小章鱼 + 它旁边展开的面板（Windows / Linux 版）
        {
            var stp = new IslandState { Hovering = true, Floating = true };
            var views = new IslandViews(engine, store, stp, prompter);
            var hosts = engine.OrderedHosts;
            var ch = stp.ContentHeight(hosts, store.Settings.FoldIdleHosts);
            var spec = IslandLayout.Spec(IslandMode.Expanded, stp.Geometry, ch, floating: true);
            var surface = new IslandSurface { Floating = true, AnchorBottom = true };
            surface.SetTarget(spec, 1, animate: false);
            LiveScope.Begin();
            surface.SetContent(views.Expanded(Math.Min(ch, IslandLayout.MaxContentHeight), spec.Scrolls, spec.Width), fancy: false);
            LiveScope.End();
            var panelSize = IslandLayout.PanelSize(stp.Geometry);
            surface.Width = panelSize.Width; surface.Height = panelSize.Height;
            surface.HorizontalAlignment = HorizontalAlignment.Left;
            surface.VerticalAlignment = VerticalAlignment.Top;
            surface.Margin = new Thickness(0, -40, 0, 0);
            var pet = new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(18), Background = Brushes.Black,
                BorderBrush = Hue.W(0.14).B(), BorderThickness = new Thickness(1),
                BoxShadow = new BoxShadows(new BoxShadow { Blur = 10, OffsetY = 3, Color = Colors.Black.WithAlpha(0.45) }),
                Child = new PetView(PetMood.Working, 30) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(panelSize.Width / 2 - 26, panelSize.Height - 40 - IslandSurface.ShadowMargin + 10, 0, 0),
            };
            var badge = new Border
            {
                MinWidth = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Hue.Mint.B(), BorderBrush = Brushes.Black,
                BorderThickness = new Thickness(2), Padding = new Thickness(4, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(panelSize.Width / 2 + 10, panelSize.Height - 40 - IslandSurface.ShadowMargin + 8, 0, 0),
                Child = Kit.T($"{engine.TotalRunning}", 10, FontWeight.Bold, Colors.Black).HA(HorizontalAlignment.Center),
            };
            var root = new Grid { Background = Wallpaper(), Width = panelSize.Width, Height = panelSize.Height + 30 };
            root.Children.Add(surface);
            root.Children.Add(pet);
            root.Children.Add(badge);
            var pw2 = new Window { Width = panelSize.Width, Height = panelSize.Height + 30, Content = root, RequestedThemeVariant = ThemeVariant.Dark };
            pw2.Show();
            Save(pw2, "cross-pet");
        }
        var pets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(20) };
        foreach (var mood in Enum.GetValues<PetMood>())
            pets.Children.Add(new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(18), Background = Brushes.Black, Child = new PetView(mood, 30) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var pw = new Window { Width = 460, Height = 100, Content = new Grid { Background = Wallpaper(), Children = { pets } } };
        pw.Show();
        Save(pw, "pet-moods");

        // 图标一览（自查用）
        var icons = new WrapPanel { Margin = new Thickness(12) };
        foreach (var name in new[] { "bolt", "hourglass", "pause", "play", "bell", "bellSlash", "eye", "eyeFill", "history", "grid", "timer", "hand", "pin", "pinFill", "refresh", "gear",
                                     "chevronLeft", "chevronRight", "chevronDown", "chevronUp", "check", "xmark", "minus", "plus", "terminal", "copy", "search", "key", "wifiAlert",
                                     "moon", "atom", "stack", "chart", "lock", "warning", "warningFill", "info", "exclamation", "cpu", "drive", "star", "starFill", "server", "trash",
                                     "question", "arrowDown", "wand", "globe", "box", "speaker", "speakerLow", "power", "folder", "pulse", "layout", "external", "pet", "display",
                                     "yen", "dollar", "euro", "pound", "card" })
        {
            icons.Children.Add(new StackPanel { Width = 74, Margin = new Thickness(4), Spacing = 4, Children =
            {
                new Icon(name, 22, Hue.Text.B()) { HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = name, FontSize = 9, Foreground = Hue.Secondary.B(), HorizontalAlignment = HorizontalAlignment.Center },
            } });
        }
        var iw = new Window { Width = 800, Height = 420, Content = new Border { Background = Brushes.Black, Child = icons } };
        iw.Show();
        Save(iw, "icons");

        // —— 计费：打开后头部多一个钱币按钮；费用页、详情页和历史页显示估算费用；设置里的计费页。其他截图保持关闭。——
        {
            store.Settings.BillingEnabled = true;
            var now = DateTime.Now;
            engine.Costs.ReplaceAll(DemoData.CostEntries(hpc.Id, now));
            IslandPages.CostPreviewNow = now;
            Save(IslandScene(IslandMode.Expanded, new IslandState { Hovering = true }), "island-expanded-billing");
            st = new IslandState { Hovering = true };
            st.Page = new IslandPage(PageKind.Costs);
            Save(IslandScene(IslandMode.Expanded, st), "page-costs");
            var costJob = hpcSnap.Jobs.First(j => j.Id == "8812044");
            st = new IslandState { Hovering = true };
            st.PinnedJob = costJob;
            st.Page = new IslandPage(PageKind.Job, hpc.Id, costJob.Id);
            st.PageState = new JobDetailState { Started = true, Detail = new JobDetail { Fields = [(L("状态", "State"), "RUNNING"), (L("分区", "Partition"), "cpu"),
                (L("资源", "Resources"), "cpu=128,mem=480G,node=2")] } };
            Save(IslandScene(IslandMode.Expanded, st), "page-detail-cost");
            var costArray = hpcSnap.Jobs.First(j => j.Array != null);
            st = new IslandState { Hovering = true };
            st.Page = new IslandPage(PageKind.Job, hpc.Id, costArray.Id);
            st.PageState = new JobDetailState { Started = true };
            Save(IslandScene(IslandMode.Expanded, st), "page-detail-cost-array");
            st = new IslandState { Hovering = true };
            st.Page = new IslandPage(PageKind.History, hpc.Id);
            st.PageState = new HistoryState
            {
                Started = true, FromServer = true,
                Entries =
                [
                    new HistoryEntry { Id = "8811911", Name = "relax_Li3PS4_pbe", State = "COMPLETED", Elapsed = 7_560, EndedAt = now.AddMinutes(-30), ExitCode = "0:0" },
                    new HistoryEntry { Id = "8811912", Name = "phonon_LiCoO2_222", State = "OUT_OF_MEMORY", Elapsed = 2_520, EndedAt = now.AddHours(-1.5), ExitCode = "0:125" },
                    new HistoryEntry { Id = "8811914", Name = "gpumd_kappa_250K", State = "COMPLETED", Elapsed = 41_400, EndedAt = now.AddHours(-25), ExitCode = "0:0" },
                    new HistoryEntry { Id = "8811901", Name = "neb_Li_vacancy", State = "CANCELLED by 1000", Elapsed = 412, EndedAt = now.AddHours(-8), ExitCode = "0:15" },
                ],
            };
            Save(IslandScene(IslandMode.Expanded, st), "page-history-cost");
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var bw = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = variant, Width = 900, Height = 1000 };
                bw.Show();
                bw.Select(SettingsPageKind.Billing);
                Save(bw, "settings-billing" + (variant == ThemeVariant.Dark ? "-dark" : ""));
                bw.Close();
            }
            store.Settings.BillingEnabled = false;
            IslandPages.CostPreviewNow = null;
        }

        // —— 设置窗口 ——
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var suffix = variant == ThemeVariant.Dark ? "-dark" : "";
            var sw = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = variant, Width = 900, Height = 1400 };
            sw.Show();
            Save(sw, "settings-general" + suffix);
            sw.Select(SettingsPageKind.Host, hpc.Id);
            Save(sw, "settings-host" + suffix);
            sw.Select(SettingsPageKind.Rules);
            Save(sw, "settings-rules" + suffix);
            sw.Close();
        }
        // 手机推送页：没填 key（深色）/ 已开启、Agent 打开（浅色、深色）
        {
            var ew = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = ThemeVariant.Dark, Width = 900, Height = 1000 };
            ew.Show();
            ew.Select(SettingsPageKind.Push);
            Save(ew, "settings-push-empty-dark");
            ew.Close();
            engine.PhonePush.PreviewState("a1b2c3d4-0000-1111-2222-33334444f00d",
                new PhonePush.PushStatus(DateTime.Now.AddMinutes(-2), true, L("测试消息已发送", "Test message sent")));
            store.Settings.PhonePush.Enabled = true;
            store.Settings.PhonePush.Agents = true;
            var quiet = store.Settings.QuietHours;
            store.Settings.QuietHours = true;
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var w = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = variant, Width = 900, Height = 1500 };
                w.Show();
                w.Select(SettingsPageKind.Push);
                Save(w, "settings-push" + (variant == ThemeVariant.Dark ? "-dark" : ""));
                w.Close();
            }
            store.Settings.QuietHours = quiet;
            store.Settings.PhonePush.Enabled = false;
            store.Settings.PhonePush.Agents = false;
            engine.PhonePush.PreviewState(null, null);
        }
        // 副屏页（没连板子时：板子设置和 Wi-Fi 表单是禁用的）
        {
            store.Settings.UsbDisplay = true;
            store.Settings.WeatherCity = L("北京", "Beijing");
            store.Settings.Deadlines = [new Deadline { Title = L("论文投稿", "Paper due"), Date = DateTime.Today.AddDays(12) }];
            engine.DisplayLink ??= new DisplayLink(engine, store);
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var dw = new SettingsWindow(store, engine, prompter, SettingsPageKind.Display) { RequestedThemeVariant = variant, Width = 900, Height = 2600 };
                dw.Show();
                Save(dw, "settings-display" + (variant == ThemeVariant.Dark ? "-dark" : ""));
                dw.Close();
            }
            store.Settings.UsbDisplay = false;
            store.Settings.WeatherCity = "";
            store.Settings.Deadlines = [];
        }
        // 自动更新的几种状态（自查用）
        {
            var rel = new UpdateChecker.Release("0.3.0", AppInfo.Repo, AppInfo.Repo, "- 一键更新\n- 服务器排序", "0", 45_101_361);
            var uw = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = ThemeVariant.Dark, Width = 900, Height = 420 };
            uw.Show();
            foreach (var (name, ust, msg, pr) in new[] {
                ("available", UpdateChecker.StateKind.Available, "", (double?)null),
                ("downloading", UpdateChecker.StateKind.Downloading, "", 0.37),
                ("failed", UpdateChecker.StateKind.InstallFailed, L("没有权限替换 C:\\Program Files\\ServerIsland 里的程序，请手动下载安装", "No permission"), null) })
            {
                UpdateChecker.SetPreviewState(ust, msg, rel, pr);
                uw.Select(SettingsPageKind.General);
                Save(uw, "settings-update-" + name);
            }
            UpdateChecker.SetPreviewState(UpdateChecker.StateKind.Idle);
            uw.Close();
        }
        // 整页（自查用：页面下方的日志保留、目录过滤等）
        {
            store.Settings.QuietHours = true;   // 勿扰时段的时间行只在打开时显示
            var full = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = ThemeVariant.Dark, Width = 900, Height = 2700 };
            full.Show();
            Save(full, "settings-general-full-dark");
            store.Settings.QuietHours = false;
            hpc.PathFilter = "~/projects/Li3PS4";
            full.Select(SettingsPageKind.Host, hpc.Id);
            Save(full, "settings-host-full-dark");
            hpc.PathFilter = "";
            full.Close();
        }

        // 手动填写 + 跳板机 + 密钥加密码（虚构地址）
        var bastion = new HostConfig("demo@gate.example.edu", L("学校跳板机", "Campus Gateway"))
            { HostName = "gate.example.edu", User = "demo", Auth = AuthMode.Password, RememberPassword = true, Enabled = false };
        var inner = new HostConfig("demo@10.20.0.5", L("内网集群", "Internal Cluster"), HostKind.Slurm)
            { HostName = "10.20.0.5", User = "demo", Port = 2222, IdentityFile = "~/.ssh/id_ed25519", Auth = AuthMode.KeyPassword, RememberPassword = true, JumpHostID = bastion.Id };
        store.Settings.Hosts.AddRange([bastion, inner]);
        store.Touch();
        engine.SetSnapshot(new HostSnapshot { Health = HostHealth.NeedsLogin(L("需要登录", "Login required")) }, inner.Id);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var suffix = variant == ThemeVariant.Dark ? "-dark" : "";
            var sw = new SettingsWindow(store, engine, prompter) { RequestedThemeVariant = variant, Width = 900, Height = 1400 };
            sw.Show();
            sw.Select(SettingsPageKind.Host, inner.Id);
            Save(sw, "settings-host-manual" + suffix);
            if (variant == ThemeVariant.Dark)
            {
                _ = AddHostDialog.Show(sw, store.Settings.Hosts, startManual: true, onCreated: d =>
                {
                    d.RequestedThemeVariant = variant;
                    Save(d, "settings-add-manual" + suffix);
                    d.Close();
                });
            }
            sw.Close();
        }
        store.Settings.Hosts.RemoveAll(x => x.Id == bastion.Id || x.Id == inner.Id);
        store.Touch();

        Console.WriteLine($"{count} 张预览图 → {dir}");
        return 0;
    }

    static IBrush Wallpaper() => new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.Parse("#2B5876"), 0), new GradientStop(Color.Parse("#4E4376"), 1) },
    };
}
