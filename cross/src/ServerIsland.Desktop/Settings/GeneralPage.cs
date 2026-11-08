using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Platform;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 轮询间隔：常用预设 + 自定义秒数（10 秒 ~ 1 小时）。
public static class IntervalPicker
{
    static readonly int[] Presets = [15, 30, 60, 120, 300, 600];

    public static Control Build(SettingsKit k, int? seconds, bool allowsInherit, int inheritedValue, Action<int?> set)
    {
        var items = new List<(int, string)>();
        if (allowsInherit) items.Add((-1, L($"跟随全局（{Island.HostCardView.IntervalLabel(inheritedValue)}）", $"Global ({Island.HostCardView.IntervalLabel(inheritedValue)})")));
        items.AddRange(Presets.Select(p => (p, Island.HostCardView.IntervalLabel(p))));
        items.Add((-2, L("自定义…", "Custom…")));
        bool custom = seconds is { } s0 && !Presets.Contains(s0);
        int current = seconds == null ? (allowsInherit ? -1 : 60) : custom ? -2 : seconds.Value;

        var box = new TextBox { Width = 70, Text = $"{seconds ?? inheritedValue}", HorizontalContentAlignment = HorizontalAlignment.Right };
        var apply = k.Button(L("应用", "Apply"), () =>
        {
            if (Text.Int(box.Text?.Trim()) is not { } v) return;
            var clamped = Math.Clamp(v, 10, 3600);
            box.Text = $"{clamped}";
            set(clamped);
        });
        var customRow = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = custom,
            Children = { box, k.Text(L("秒", "seconds"), 12.5, c: k.P.Secondary), apply, k.Caption(L("范围 10 ~ 3600 秒", "Range 10 – 3600 s")) },
        };
        var combo = k.Combo(items, current, v =>
        {
            switch (v)
            {
                case -1: customRow.IsVisible = false; set(null); break;
                case -2: customRow.IsVisible = true; break;
                default: customRow.IsVisible = false; set(v); break;
            }
        });
        return new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { combo, customRow } };
    }
}

/// 设置 → 通用。
public sealed class GeneralPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine)
{
    /// 导入设置后整页重建，结果文字留到重建后显示。
    static string _transferNote = "";

    AppSettings S => store.Settings;
    TextBlock? _summary;
    PetView? _pet;

    void Touch() => store.Touch();

    /// "灵动岛显示在"的选项：主屏幕、每块已连接的屏幕；选过但现在没连接的也列出来（标"未连接"）。
    List<(string Value, string Label)> ScreenChoices()
    {
        var r = new List<(string, string)> { ("", L("主屏幕（默认）", "Main Screen (default)")) };
        var names = (window.Screens?.All ?? []).Select(s => s.DisplayName).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct().ToList();
        r.AddRange(names.Select(n => (n, n)));
        var saved = S.IslandScreen;
        if (saved is not ("" or "primary") && !names.Contains(saved)) r.Add((saved, saved + L("（未连接）", " (not connected)")));
        if (saved == "primary") r[0] = ("primary", r[0].Item2);
        return r;
    }

    public void EngineChanged()
    {
        if (_summary != null) _summary.Text = Summary();
        if (_pet != null && _pet.Mood != engine.PetMood) { _pet.Mood = engine.PetMood; _pet.InvalidateVisual(); }
    }

    string Summary() => L($"{S.Hosts.Count} 台服务器 · 运行 {engine.TotalRunning} · 排队 {engine.TotalPending}",
                          $"{S.Hosts.Count} servers · {engine.TotalRunning} running · {engine.TotalPending} pending");

    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(k.Text(L("通用", "General"), 22, FontWeight.Bold).Margin2(2, 0, 0, 16));

        // 概览
        Control appIcon;
        try { appIcon = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://ServerIsland/Assets/AppIcon.png"))), Width = 58, Height = 58 }; }
        catch { appIcon = new Control { Width = 58, Height = 58 }; }
        _summary = k.Text(Summary(), 12.5, c: k.P.Secondary);
        _pet = new PetView(engine.PetMood, 40);
        var heroTexts = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children =
        {
            k.Text("ServerIsland", 20, FontWeight.Bold),
            k.Text(L("版本 ", "Version ") + AppInfo.Version + "  ·  " + (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "macOS (dev)"), 11.5, c: k.P.Secondary),
            _summary,
        } };
        var hero = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        hero.Children.Add(appIcon);
        Grid.SetColumn(heroTexts, 1);
        heroTexts.Margin = new Thickness(14, 0, 0, 0);
        hero.Children.Add(heroTexts);
        var petBox = new Border { Width = 64, Height = 64, CornerRadius = new CornerRadius(16), Background = Brushes.Black, Child = _pet };
        _pet.HorizontalAlignment = HorizontalAlignment.Center;
        _pet.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(petBox, 2);
        hero.Children.Add(petBox);

        var star = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        star.Children.Add(new Icon("starFill", 16, Color.Parse("#F5C518").B()) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        var starTexts = new StackPanel { Spacing = 1, Children = { k.Text(L("喜欢 ServerIsland？", "Enjoying ServerIsland?")),
            k.Caption(L("它是免费的开源软件，在 GitHub 上点个 Star 能让更多人看到它。", "It's free and open source — a star on GitHub helps others find it.")) } };
        Grid.SetColumn(starTexts, 1);
        star.Children.Add(starTexts);
        var starButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { k.Button(L("点个 Star", "Star on GitHub"), () => Shell.OpenUrl(AppInfo.Repo)), k.Button(L("反馈问题", "Report an Issue"), () => Shell.OpenUrl(AppInfo.Issues)) } };
        Grid.SetColumn(starButtons, 2);
        star.Children.Add(starButtons);

        // 检查更新：状态 + 按钮；有新版本时显示"更新"（自动下载、替换、重新启动）和更新说明。状态变化时只重画这一行。
        var updateHost = new ContentControl();
        void FillUpdate()
        {
            var r = UpdateChecker.Latest;
            var state = UpdateChecker.State;
            var size = r?.Size is { } bytes ? $"（{Format.Bytes(bytes)}）" : "";
            var title = state switch
            {
                UpdateChecker.StateKind.Checking => L("正在检查 GitHub 上的最新版本…", "Checking GitHub for the latest version…"),
                UpdateChecker.StateKind.UpToDate => L($"已是最新版本（{UpdateChecker.Message}）", $"You're up to date ({UpdateChecker.Message})"),
                UpdateChecker.StateKind.Available => L($"有新版本 v{r!.Version}（当前 {AppInfo.Version}）", $"Version {r!.Version} is available (you have {AppInfo.Version})"),
                UpdateChecker.StateKind.Failed => L("检查失败：", "Check failed: ") + UpdateChecker.Message,
                UpdateChecker.StateKind.Downloading => L($"正在下载并核对 v{r!.Version}{size}", $"Downloading and verifying v{r!.Version}{size}")
                    + (UpdateChecker.Progress is { } pr ? $" {Format.Percent(pr)}" : "…"),
                UpdateChecker.StateKind.Installing => L($"v{r!.Version} 已装好，马上重新启动…", $"v{r!.Version} is installed; restarting…"),
                UpdateChecker.StateKind.InstallFailed => L($"没能自动更新到 v{r!.Version}", $"Couldn't update to v{r!.Version} automatically"),
                _ => L("当前版本 ", "Current version ") + AppInfo.Version,
            };
            var sub = state == UpdateChecker.StateKind.InstallFailed ? UpdateChecker.Message
                : state is UpdateChecker.StateKind.Available or UpdateChecker.StateKind.Downloading && r!.Notes.Length > 0 ? r!.Notes
                : UpdateChecker.LastChecked is { } t ? L("上次检查：", "Last checked: ") + Format.Relative(t) : "";
            var texts = new StackPanel { Spacing = 1, Children = { k.Text(title) } };
            if (sub.Length > 0) texts.Children.Add(k.Caption(sub));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            if (state == UpdateChecker.StateKind.Available && r != null)
            {
                buttons.Children.Add(k.Button(L($"更新到 v{r.Version}", $"Update to v{r.Version}"), () => (Application.Current as App)?.InstallUpdate(r), accent: true));
                buttons.Children.Add(k.Button(L("更新说明", "Release Notes"), () => Shell.OpenUrl(r.PageUrl)));
            }
            else if (state == UpdateChecker.StateKind.InstallFailed && r != null)
            {
                buttons.Children.Add(k.Button(L($"下载 v{r.Version}", $"Download v{r.Version}"), () => Shell.OpenUrl(r.DownloadUrl), accent: true));
                buttons.Children.Add(k.Button(L("更新说明", "Release Notes"), () => Shell.OpenUrl(r.PageUrl)));
            }
            else if (state is not (UpdateChecker.StateKind.Downloading or UpdateChecker.StateKind.Installing))
            {
                var checkBtn = k.Button(state == UpdateChecker.StateKind.Checking ? L("检查中…", "Checking…") : L("检查更新", "Check for Updates"),
                    () => _ = UpdateChecker.CheckAsync());
                checkBtn.IsEnabled = state != UpdateChecker.StateKind.Checking;
                buttons.Children.Add(checkBtn);
            }
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            g.Children.Add(new Icon("arrowDown", 16, Color.Parse("#0A84FF").B()) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 10, 0) });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            Grid.SetColumn(buttons, 2);
            g.Children.Add(buttons);
            updateHost.Content = g;
        }
        FillUpdate();
        Action onUpdate = () => Avalonia.Threading.Dispatcher.UIThread.Post(FillUpdate);
        UpdateChecker.Changed += onUpdate;
        updateHost.DetachedFromVisualTree += (_, _) => UpdateChecker.Changed -= onUpdate;

        col.Children.Add(k.Section("", "", default, [
            hero,
            k.Row(L("语言", "Language"), null, k.Combo(Enum.GetValues<AppLanguage>().Select(l => (l, l.Label())), S.Language, v => { S.Language = v; Touch(); })),
            star,
            updateHost,
            k.ToggleRow(L("自动检查更新", "Check for Updates Automatically"),
                L("启动时和之后每天一次，读取 GitHub 上最新的发布版本号（只读，不上传任何信息）。有新版本时在刘海里提醒一次。",
                  "At launch and once a day, reads the latest release number from GitHub (read-only; nothing is sent). You'll get one alert per new version."),
                S.AutoCheckUpdates, v => { S.AutoCheckUpdates = v; Touch(); }),
        ]));

        // 显示方式（Windows / Linux 上没有硬件刘海，可以选择灵动岛或桌面小章鱼）
        col.Children.Add(k.Section(L("显示方式", "Display Style"), "pet", Color.Parse("#30D158"), [
            k.Row(L("显示为", "Show as"), null, k.Segmented([(DisplayMode.Island, L("灵动岛", "Island")), (DisplayMode.Pet, L("桌面小章鱼", "Desktop Pet"))],
                S.DisplayMode, v => { S.DisplayMode = v; Touch(); })),
            k.Row(L("灵动岛显示在", "Island Screen"),
                L("有多块显示器时，灵动岛放在哪块屏幕的顶部。默认是主屏幕；选了某块显示器但它没连接时，也回到主屏幕。",
                  "With several displays, which screen's top edge the island sits on. Default is the main screen; if the chosen display isn't connected, the main screen is used."),
                k.Combo(ScreenChoices(), S.IslandScreen, v => { S.IslandScreen = v; Touch(); }, 220)),
            k.Caption(L("灵动岛：挂在屏幕顶部正中（有刘海的 Mac 上贴着刘海），鼠标移上去展开。\n桌面小章鱼：Tako 待在桌面上，可以拖到任何地方；鼠标移上去展开面板，点一下固定。",
                        "Island: hangs from the top center of the screen (around the notch on Macs that have one) and expands on hover.\nDesktop pet: Tako sits on your desktop and can be dragged anywhere; hover to expand the panel, click to pin it.")),
        ]));

        // 轮询
        col.Children.Add(k.Section(L("轮询", "Refresh"), "refresh", Color.Parse("#0A84FF"), [
            k.ToggleRow(L("自动刷新", "Auto Refresh"), L("关闭后只有点击刷新按钮时才会连接服务器。", "When off, servers are only contacted when you click Refresh."),
                S.AutoRefresh, v => engine.SetAutoRefresh(v)),
            k.Row(L("默认轮询间隔", "Default Interval"), null, IntervalPicker.Build(k, S.DefaultInterval, false, 60, v => { S.DefaultInterval = v ?? 60; Touch(); })),
        ]));

        // 刘海（显示）
        col.Children.Add(k.Section(L("刘海", "Island"), "display", Color.Parse("#5E5CE6"), [
            k.Row(L("收起时", "When Collapsed"), L("灵动岛收起时两侧小翅膀的显示方式；从不显示时只在屏幕顶端留一条细把手。", "What the collapsed island shows; with Never only a thin handle stays at the top edge."),
                k.Combo(Enum.GetValues<CollapsedStyle>().Select(c => (c, c.Label())), S.CollapsedStyle, v => { S.CollapsedStyle = v; Touch(); })),
            k.ToggleRow(L("合并空闲的服务器", "Group Idle Servers"), L("展开面板里，没有任务的服务器合并成一行，点击可以展开。全部空闲时照常显示。",
                "Servers with no jobs are grouped into one line you can click to expand. All cards show when everything is idle."), S.FoldIdleHosts, v => { S.FoldIdleHosts = v; Touch(); }),
        ]));

        // 提醒
        CheckBox Check(string title, bool value, Action<bool> set)
        {
            var cb = new CheckBox { IsChecked = value, Content = k.Text(title), MinWidth = 0 };
            cb.IsCheckedChanged += (_, _) => { set(cb.IsChecked == true); Touch(); };
            return cb;
        }
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children =
        {
            Check(L("开始运行", "Started"), S.AlertStarted, v => S.AlertStarted = v),
            Check(L("完成", "Finished"), S.AlertFinished, v => S.AlertFinished = v),
            Check(L("失败", "Failed"), S.AlertFailed, v => S.AlertFailed = v),
            Check(L("结果未知", "Unknown"), S.AlertUnknown, v => S.AlertUnknown = v),
        } };
        var quietNext = k.Text(L("（次日）", "(next day)"), c: k.P.Secondary);
        quietNext.VerticalAlignment = VerticalAlignment.Center;
        quietNext.IsVisible = S.QuietStart > S.QuietEnd;
        ComboBox QuietTime(int value, Action<int> set) =>
            k.Combo(Enumerable.Range(0, 48).Select(i => (i * 30, AppSettings.Clock(i * 30))), value, v => { set(v); Touch(); quietNext.IsVisible = S.QuietStart > S.QuietEnd; }, 96);
        var toWord = k.Text(L("至", "to"), c: k.P.Secondary);
        toWord.VerticalAlignment = VerticalAlignment.Center;
        var quietTimes = k.Row(L("时间", "Time"), null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
        {
            QuietTime(S.QuietStart, v => S.QuietStart = v), toWord, QuietTime(S.QuietEnd, v => S.QuietEnd = v), quietNext,
        } });
        quietTimes.IsVisible = S.QuietHours;
        var rows = new List<Control?>
        {
            k.ToggleRow(L("弹出提示", "Pop-ups"), L("任务结束 / 开始时从灵动岛（或小章鱼旁边）弹出提示。", "Show a pop-up from the island (or next to Tako) when jobs end or start."),
                S.IslandAlerts, v => { S.IslandAlerts = v; Touch(); }),
            Notifier.Supported ? k.ToggleRow(L("系统通知", "System Notifications"), null, S.SystemNotifications, v => { S.SystemNotifications = v; Touch(); }) : null,
            k.Row(L("提醒哪些任务", "Alert For"),
                L("关注任务：在刘海的任务行上点 ☆，或在详情页点星标。关注的任务结束后自动取消关注。手动监听的进程照常提醒；磁盘等服务器提醒不受影响。",
                  "Star a job with the ☆ on its row or on its details page. Stars are removed when the job ends. Watched processes still alert; disk and other server alerts are not affected."),
                k.Combo(Enum.GetValues<AlertScope>().Select(a => (a, a.Label())), S.AlertScope, v => { S.AlertScope = v; Touch(); })),
            k.Row(L("提醒这些事件", "Alert When"),
                L("开始运行：排队的作业开始运行（不响提示音）。结果未知：作业离开了队列，但查不到成功还是失败（例如集群没有开启作业记录）。",
                  "Started: a pending job begins running (no sound). Unknown: a job left the queue but its result couldn't be found (e.g. no job accounting)."),
                kinds),
            k.ToggleRow(L("勿扰时段", "Quiet Hours"),
                L("这段时间里不响提示音、不弹窗；系统通知照发但不出声，历史记录照常。",
                  "During these hours there are no sounds or pop-ups; system notifications still arrive silently and history is kept as usual."),
                S.QuietHours, v => { S.QuietHours = v; quietTimes.IsVisible = v; Touch(); }),
            quietTimes,
            k.ToggleRow(L("数组作业首个失败立即提醒", "Alert on First Array Failure"), L("数组作业平时只在全部结束时提醒一次；打开后第一个元素失败时会额外提醒一次。",
                "Job arrays normally alert once when all elements finish; this adds one alert when the first element fails."), S.ArrayFirstFailureAlert, v => { S.ArrayFirstFailureAlert = v; Touch(); }),
            k.Row(L("磁盘提醒阈值", "Disk Alert Threshold"), L("超过时提醒一次；回落后再次超过会重新提醒。不受“提醒哪些任务”影响。", "Alerts once when exceeded, and again after it drops and rises again. Not affected by Alert For."),
                k.Combo(new[] { 0.8, 0.85, 0.9, 0.95 }.Select(v => (v, $"{(int)Math.Round(v * 100)}%")), S.DiskWarnThreshold, v => { S.DiskWarnThreshold = v; Touch(); }, 100)),
        };
        var muteNote = L("静音的服务器（卡片上的铃铛）什么都不提醒，优先于这里的设置。", "Muted servers (the bell on a card) never alert, whatever is set here.");
        col.Children.Add(k.Section(L("提醒", "Alerts"), "bell", Color.Parse("#FF9F0A"), rows,
            OperatingSystem.IsWindows() ? muteNote + "\n" + L("Windows 上没有打包安装的程序发不了系统通知，提醒以弹窗和提示音为主。", "On Windows, alerts use pop-ups and sounds (unpackaged apps can't post system notifications).") : muteNote));

        // 提示音
        var slider = new Slider { Minimum = 0.1, Maximum = 1, Value = S.SoundVolume, Width = 220, IsEnabled = S.SoundEnabled };
        slider.ValueChanged += (_, e) => { S.SoundVolume = e.NewValue; Touch(); };
        var vol = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Icon("speakerLow", 14, k.P.Secondary.B()) { VerticalAlignment = VerticalAlignment.Center }, slider, new Icon("speaker", 14, k.P.Secondary.B()) { VerticalAlignment = VerticalAlignment.Center } } };
        var previews = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
        {
            k.Button(L("成功", "Success"), () => Sound.Preview("success", S.SoundVolume)),
            k.Button(L("失败", "Failure"), () => Sound.Preview("failure", S.SoundVolume)),
            k.Button(L("提醒", "Warning"), () => Sound.Preview("warning", S.SoundVolume)),
        } };
        col.Children.Add(k.Section(L("提示音", "Sounds"), "speaker", Color.Parse("#FF375F"), [
            k.ToggleRow(L("提示音", "Sounds"), null, S.SoundEnabled, v => { S.SoundEnabled = v; slider.IsEnabled = v; Touch(); }),
            k.Row(L("音量", "Volume"), null, vol),
            k.Row(L("试听", "Preview"), null, previews),
        ]));

        // SSH
        var backendHelp = L("自动：Windows 上用内置 SSH（Windows 自带的 OpenSSH 不能复用连接），Linux 上用系统 ssh（和 Mac 版一样用 ControlMaster 复用连接）。\n" +
                            "内置 SSH：读取 ~/.ssh/config 里的 HostName、User、Port、IdentityFile、ProxyJump，用 known_hosts 校验指纹；不支持 ssh-agent 和 ProxyCommand。\n" +
                            "系统 ssh：所有 ssh 配置都生效；但在 Windows 上每次查询都要重新握手，也不能弹窗登录密码 / 动态口令的机器。",
                            "Automatic: built-in SSH on Windows (its OpenSSH can't reuse connections), system ssh on Linux (ControlMaster, like the Mac app).\n" +
                            "Built-in: reads HostName, User, Port, IdentityFile and ProxyJump from ~/.ssh/config and checks known_hosts; no ssh-agent or ProxyCommand.\n" +
                            "System ssh: every ssh option works, but on Windows each query does a full handshake and password / OTP logins aren't possible.");
        var current = SshCommon.Resolve(S.SshBackend) == SshBackend.Managed ? L("当前：内置 SSH", "Now: built-in SSH") : L("当前：系统 ssh", "Now: system ssh");
        col.Children.Add(k.Section("SSH", "key", Color.Parse("#30B0C7"), [
            k.Row(L("SSH 实现", "SSH Implementation"), backendHelp,
                k.Combo(Enum.GetValues<SshBackend>().Select(b => (b, b.Label())), S.SshBackend, v => { S.SshBackend = v; Touch(); window.RefreshPage(); })),
            k.Row(L("状态", "Status"), null, k.Text(current, 12.5, c: k.P.Secondary)),
        ], L("密码默认不保存，只在登录时经过对话框交给 SSH；在服务器页打开“记住密码”时只存进系统凭据库。动态口令和密钥口令从不保存到磁盘。", "Passwords are not stored by default and go straight to SSH from a dialog; with “Remember Password” on a server page they are kept only in the system credential store. One-time codes and key passphrases are never written to disk.")));

        // 启动
        if (Autostart.Supported)
        {
            var err = k.Status(Color.Parse("#FF9F0A"));
            col.Children.Add(k.Section(L("启动", "Startup"), "power", Color.Parse("#30D158"), [
                k.ToggleRow(L("登录时自动启动", "Launch at Login"), null, Autostart.IsEnabled, v =>
                {
                    var e = Autostart.Set(v);
                    err.Text = e == null ? "" : L("设置失败：", "Failed: ") + e;
                }),
                err,
            ]));
        }

        // 进度文件
        var sample = new SelectableTextBlock { Text = """{"name": "train", "progress": 0.42, "message": "epoch 3/10", "jobid": "$SLURM_JOB_ID"}""", FontFamily = Hue.Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = k.P.Text.B() };
        col.Children.Add(k.Section(L("进度文件", "Progress Files"), "chart", Color.Parse("#14B8A6"), [
            k.Text(L("训练脚本往服务器的 ~/.serverisland/progress/<名称>.json 写一行 JSON，面板里就能显示真实进度：",
                     "Have your script write one line of JSON to ~/.serverisland/progress/<name>.json on the server to show real progress:"), 12.5, wrap: true),
            sample,
        ], L("写了 jobid 的条目会合并到对应作业上（数组元素写 主编号_序号）。仓库里的 remote/si-progress 是现成的小工具。",
             "Entries with a jobid are merged into that job (array elements use base_index). remote/si-progress in the repo is a ready-made helper.")));

        // 文件与诊断
        var logStatus = k.Caption("");
        logStatus.VerticalAlignment = VerticalAlignment.Center;
        var logClear = k.Button(L("清除日志…", "Clear Log…"), async () =>
        {
            var ok = await PromptDialog.Confirm(L("清除全部诊断日志？", "Clear the whole diagnostic log?"),
                L($"当前 {Format.Bytes(DebugLog.Size)}。不影响设置和任务历史。", $"Currently {Format.Bytes(DebugLog.Size)}. Settings and job history are kept."),
                L("清除", "Clear"), L("取消", "Cancel"));
            if (!ok) return;
            DebugLog.Clear();
            logStatus.Text = L("已清除", "Cleared");
        });
        var historyClear = k.Button(L("清除历史记录…", "Clear History…"), async () =>
        {
            var ok = await PromptDialog.Confirm(L("清除全部历史记录？", "Clear all job history?"),
                L($"本机记下的 {engine.History.Count} 条已结束任务（最多保留 7 天）都会删除。不影响设置和日志；集群上 sacct 的记录不受影响。",
                  $"All {engine.History.Count} finished jobs recorded on this computer (kept up to 7 days) will be deleted. Settings and logs are kept; sacct records on the cluster are not affected."),
                L("清除", "Clear"), L("取消", "Cancel"));
            if (!ok) return;
            engine.History.Clear();
            logStatus.Text = L("历史记录已清除", "History cleared");
        });
        // 导入 / 导出设置
        var transferStatus = k.Caption(_transferNote);
        _transferNote = "";
        transferStatus.VerticalAlignment = VerticalAlignment.Center;
        var export = k.Button(L("导出设置…", "Export Settings…"), async () =>
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = L("导出设置", "Export Settings"),
                SuggestedFileName = ConfigTransfer.DefaultFileName(),
                DefaultExtension = "json",
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            try
            {
                File.WriteAllText(path, ConfigTransfer.ExportJson(S));
                transferStatus.Text = L("已导出", "Exported");
                DebugLog.Log("导出设置：" + Path.GetFileName(path));
            }
            catch (Exception e) { transferStatus.Text = L("导出失败：", "Export failed: ") + e.Message; }
        });
        var import = k.Button(L("导入设置…", "Import Settings…"), async () =>
        {
            var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = L("导入设置", "Import Settings"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            AppSettings imported;
            try { imported = ConfigTransfer.Read(File.ReadAllText(path)); }
            catch (ConfigTransfer.NotSettingsException) { transferStatus.Text = L("这不是 ServerIsland 的设置文件", "Not a ServerIsland settings file"); return; }
            catch { transferStatus.Text = L("读不出这个文件", "Couldn't read this file"); return; }
            int n = ConfigTransfer.NewHosts(S, imported).Count;
            var choice = await PromptDialog.Choose(L("导入设置", "Import Settings"),
                L("替换前会把当前设置备份到配置文件旁边。密码不在设置文件里，需要时重新登录即可。",
                  "Your current settings are backed up next to the config file before replacing. Passwords aren't in the file; log in again if asked."),
                L($"替换全部设置（{imported.Hosts.Count} 台服务器）", $"Replace All Settings ({imported.Hosts.Count} servers)"),
                L($"只添加新的服务器（{n} 台）", $"Only Add New Servers ({n})"),
                L("取消", "Cancel"), secondEnabled: n > 0);
            if (choice < 0) return;
            var mode = choice == 0 ? ConfigTransfer.Mode.Replace : ConfigTransfer.Mode.AddHosts;
            if (mode == ConfigTransfer.Mode.Replace && store.Persist) ConfigTransfer.BackupCurrent(S);
            int before = S.Hosts.Count;
            store.Replace(ConfigTransfer.Apply(S, imported, mode));
            DebugLog.Log($"导入设置（{(mode == ConfigTransfer.Mode.Replace ? "替换" : "只添加服务器")}）");
            // 整页重建（导入后各项设置都可能变了），结果写在重建后的状态文字里
            _transferNote = mode == ConfigTransfer.Mode.Replace ? L($"已导入 {store.Settings.Hosts.Count} 台服务器", $"Imported {store.Settings.Hosts.Count} servers")
                                                                : L($"已添加 {store.Settings.Hosts.Count - before} 台服务器", $"Added {store.Settings.Hosts.Count - before} servers");
            window.RefreshPage();
        });

        col.Children.Add(k.Section(L("文件与诊断", "Files & Diagnostics"), "folder", Color.Parse("#8E8E93"), [
            k.Row(L("配置文件", "Config File"), null, new SelectableTextBlock { Text = AppPaths.Tilde(AppPaths.ConfigFile), FontFamily = Hue.Mono, FontSize = 12, Foreground = k.P.Secondary.B() }),
            k.Row(L("诊断日志", "Diagnostic Log"), null, new SelectableTextBlock { Text = AppPaths.Tilde(DebugLog.Path), FontFamily = Hue.Mono, FontSize = 12, Foreground = k.P.Secondary.B() }),
            k.Row(L("日志保留", "Keep Logs For"),
                L("诊断日志里早于这个天数的记录会自动删除（启动时和之后每 6 小时检查一次）。日志只在本机，用来排查“为什么没提醒”之类的问题。",
                  "Diagnostic log entries older than this are deleted automatically (at launch and every 6 hours). The log stays on this computer and helps answer questions like “why was there no alert?”."),
                k.Combo(new[] { 1, 3, 7, 14, 30, 0 }.Select(d => (d, d == 0 ? L("永久保留", "Forever") : L($"{d} 天", d == 1 ? "1 day" : $"{d} days"))),
                    store.Settings.LogRetentionDays, v => { store.Settings.LogRetentionDays = v; Touch(); DebugLog.Prune(v); }, minWidth: 120)),
            k.Row(L("导入 / 导出", "Import / Export"),
                L("把全部设置（服务器、提醒、进度识别规则……）存成一个 JSON 文件，换电脑或在 Mac 和 Windows / Linux 版之间迁移。不包含密码。~/.ssh/config 里的别名和私钥路径要在新电脑上也有效。",
                  "Save all settings (servers, alerts, progress rules…) to a JSON file to move to another computer, or between the Mac and Windows / Linux versions. Passwords aren't included. ~/.ssh/config aliases and key paths must also work on the new computer."),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { transferStatus, export, import } }),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children =
            {
                k.Button(L("显示配置文件", "Show Config"), () => { store.SaveNow(); Shell.Reveal(AppPaths.ConfigFile); }),
                k.Button(L("打开诊断日志", "Open Log"), () => Shell.OpenFile(DebugLog.Path)),
                logClear,
                historyClear,
                k.Button(L("立即刷新全部", "Refresh All"), engine.RefreshAll),
                logStatus,
            } },
        ]));
        col.Children.Add(k.Section(L("扩展", "Extensions"), "puzzle", Color.Parse("#14B8A6"), [
            k.ToggleRow(L("外接副屏（ESP32）", "External Display (ESP32)"),
                L("把任务状态、AI 编程工具的提醒显示在一块刷了 ServerIsland 固件的 ESP32 小屏幕上（固件暂未公开）。打开后侧栏多一个\"副屏\"页面。没有这种屏幕就不用管它。",
                  "Mirror job status and AI coding-tool alerts on a small ESP32 screen running the ServerIsland firmware (not yet public). Turning this on adds a Display page to the sidebar. Ignore it if you don't have one."),
                S.UsbDisplay, v => { S.UsbDisplay = v; Touch(); }),
        ]));
        return col;
    }
}
