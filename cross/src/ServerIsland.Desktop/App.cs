using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Desktop.Island;
using ServerIsland.Desktop.Platform;
using ServerIsland.Desktop.Settings;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop;

public sealed class App : Application
{
    public static bool AskPassMode;
    public static WindowIcon? WindowIcon;
    public static App? Instance;

    ConfigStore _store = null!;
    MonitorEngine _engine = null!;
    IslandWindow _island = null!;
    PetWindow? _pet;
    SettingsWindow? _settings;
    TrayController? _tray;
    LoginPrompter _prompter = new();
    DisplayMode? _displayMode;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
        Name = "ServerIsland";
        try { WindowIcon = new WindowIcon(AssetLoader.Open(new Uri("avares://ServerIsland/Assets/AppIcon.png"))); } catch { }
        Instance = this;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (AskPassMode || Preview.PreviewRenderer.Active) return;
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 界面线程上的异常：记日志后继续运行，不让整个程序消失。
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            DebugLog.Log("界面异常：" + e.Exception);
            Preview.UiSmoke.Errors.Add(e.Exception.ToString());
            e.Handled = true;
        };
        bool demo = Program.Demo;
        _store = new ConfigStore(persist: !demo);
        if (demo) _store.Replace(DemoData.Settings());
        _engine = new MonitorEngine(_store, demo);
        _engine.PlaySound = Sound.Play;
        _engine.EventEmitted += e =>
        {
            if (_store.Settings.SystemNotifications && !demo && e.Kind != EventKind.Started && Notifier.Supported)
                Notifier.Post(e, _store.Settings.SoundEnabled || _store.Settings.IsQuiet());
        };

        _island = new IslandWindow(_engine, _store, _prompter);
        _island.State.OpenSettings = () => { _island.SetHovering(false); ShowSettings(); };
        _island.State.OpenSettingsPage = page =>
        {
            _island.SetHovering(false);
            ShowSettings();
            if (page == "billing") _settings?.Select(SettingsPageKind.Billing);
        };
        _tray = new TrayController(this, _engine, _store);

        _engine.Changed += OnEngineChanged;
        _store.Changed += OnStoreChanged;
        ApplyDisplayMode();
        // 外接副屏：没打开"外接副屏"时只是空转（每 3 秒看一眼设置）。
        _engine.DisplayLink = new DisplayLink(_engine, _store) { IsLocked = LockState.IsLocked };
#if WINDOWS
        // Windows 上可以走蓝牙（第一次配对时弹框输入副屏上的 6 位码）。
        _engine.DisplayLink.BleFactory = () => new WinBle(() => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            PromptDialog.Ask(L("ServerIsland 副屏", "ServerIsland Display"), L("输入副屏上显示的 6 位配对码", "Enter the 6-digit code shown on the display"), false)));
#endif
        _engine.DisplayLink.Start();
        _engine.Start();
        desktop.Exit += (_, _) => _store.SaveNow();

        if (Preview.UiSmoke.Directory != null) Preview.UiSmoke.Run(this, _engine, _store, _island);

        // 第一次启动：直接打开设置，引导添加服务器。
        if (_store.Settings.Hosts.Count == 0 && !demo)
            DispatcherTimer.RunOnce(ShowSettings, TimeSpan.FromSeconds(0.6));
        // 配置文件损坏：在刘海里说明一次（原文件已备份）。
        if (_store.LoadProblem is { } problem)
            DispatcherTimer.RunOnce(() => _engine.Emit(new IslandEvent(EventKind.Warning, L("设置文件读取失败", "Couldn't read settings"), problem, "ServerIsland")),
                TimeSpan.FromSeconds(1.5));
        DebugLog.Log($"ServerIsland {AppInfo.Version} 启动（{Environment.OSVersion}，SSH：{_engine.Ssh.Name}）");
        // 按设置的天数清理诊断日志：启动时一次，之后每 6 小时一次。
        DebugLog.Prune(_store.Settings.LogRetentionDays);
        DispatcherTimer.Run(() => { DebugLog.Prune(_store.Settings.LogRetentionDays); return true; }, TimeSpan.FromHours(6));
        // 自动检查更新：启动 20 秒后一次，之后每 24 小时一次（演示模式、界面走查不检查）。
        UpdateChecker.CurrentVersion = AppInfo.Version;
        if (!demo) UpdateInstaller.CleanupOld();
        UpdateChecker.Changed += () => Dispatcher.UIThread.Post(() => _tray?.Update());
        if (!demo && Preview.UiSmoke.Directory == null)
        {
            DispatcherTimer.RunOnce(AutoCheckForUpdates, TimeSpan.FromSeconds(20));
            DispatcherTimer.Run(() => { AutoCheckForUpdates(); return true; }, TimeSpan.FromHours(24));
        }
    }

    /// 后台检查更新：有新版本时在刘海里提醒一次（同一个版本只提醒一次），托盘菜单里多一项"下载新版本"。
    async void AutoCheckForUpdates()
    {
        if (!_store.Settings.AutoCheckUpdates) return;
        if (await UpdateChecker.CheckAsync() is not { } r || _store.Settings.NotifiedUpdateVersion == r.Version) return;
        _store.Settings.NotifiedUpdateVersion = r.Version;
        _store.Touch();
        _engine.Emit(new IslandEvent(EventKind.Info, L($"ServerIsland 有新版本 v{r.Version}", $"ServerIsland v{r.Version} is available"),
            L("在托盘图标或 设置 → 通用 里一键更新", "Update from the tray icon or Settings → General"), "ServerIsland"));
    }

    /// 自动更新：下载、核对、替换程序文件，再启动新程序（它会等这个进程退出）并退出。失败时设置页显示原因和下载按钮。
    public async void InstallUpdate(UpdateChecker.Release r)
    {
        ShowSettings();
        if (await UpdateChecker.InstallAsync(r) is not { } exe) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            psi.ArgumentList.Add("--after-update");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            System.Diagnostics.Process.Start(psi);
            DebugLog.Log($"自动更新：已换成 v{r.Version}，启动新程序后退出");
        }
        catch (Exception e) { DebugLog.Log("自动更新：启动新程序失败（下次打开就是新版本）：" + e.Message); }
        Quit();
    }

    bool _refreshQueued;

    void OnEngineChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            _island.RequestRebuild();
            _pet?.Refresh();
            _tray?.Update();
            _settings?.EngineChanged();
        }, DispatcherPriority.Background);
    }

    SshBackend _backend;

    void OnStoreChanged()
    {
        if (_store.Settings.SshBackend != _backend)
        {
            _backend = _store.Settings.SshBackend;
            _engine.SetSshBackend(_backend);
        }
        ApplyDisplayMode();
        if (_store.Settings.IslandScreen != _islandScreen)
        {
            _islandScreen = _store.Settings.IslandScreen;
            if (_displayMode == DisplayMode.Island) Dispatcher.UIThread.Post(_island.Reposition);
        }
        OnEngineChanged();
    }
    string? _islandScreen;

    void ApplyDisplayMode()
    {
        _backend = _store.Settings.SshBackend;
        var mode = _store.Settings.DisplayMode;
        if (mode == _displayMode) return;
        _displayMode = mode;
        if (mode == DisplayMode.Pet)
        {
            _pet ??= new PetWindow(_engine, _store, _island);
            _island.FloatingAnchor = () => _pet.IsVisible ? _pet.ScreenRect : null;
            _pet.Show();
            _island.Hide();
        }
        else
        {
            _island.FloatingAnchor = null;
            _pet?.Hide();
            _island.Show();
            _island.Reposition();
        }
        _island.State.Hovering = false;
        _island.Rebuild();
    }

    public void ShowSettings()
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_store, _engine, _prompter);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Show();
        _settings.Activate();
    }

    public void TogglePanel() => _island.TogglePinned();

    public PetWindow? PetWindow => _pet;
    public SettingsWindow? Settings => _settings;

    public void SetDisplayMode(DisplayMode mode)
    {
        _store.Settings.DisplayMode = mode;
        _store.Touch();
    }

    public void Quit()
    {
        _store.SaveNow();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}

/// 托盘图标（Windows 通知区域 / Linux 状态栏）：点击打开面板，右键菜单里有各服务器状态、刷新、设置、显示方式、退出。
public sealed class TrayController
{
    readonly App _app;
    readonly MonitorEngine _engine;
    readonly ConfigStore _store;
    readonly TrayIcon _icon;
    readonly WindowIcon? _normal, _alert;
    bool? _lastAlert;

    public TrayController(App app, MonitorEngine engine, ConfigStore store)
    {
        _app = app;
        _engine = engine;
        _store = store;
        try
        {
            _normal = new WindowIcon(AssetLoader.Open(new Uri("avares://ServerIsland/Assets/Tray.png")));
            _alert = new WindowIcon(AssetLoader.Open(new Uri("avares://ServerIsland/Assets/TrayAlert.png")));
        }
        catch { }
        var menu = new NativeMenu();
        menu.NeedsUpdate += (_, _) => BuildMenu(menu);
        BuildMenu(menu);
        _icon = new TrayIcon { Icon = _normal, ToolTipText = "ServerIsland", Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => _app.TogglePanel();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
        Update();
    }

    public void Update()
    {
        bool alert = _engine.HasError;
        if (alert != _lastAlert)
        {
            _lastAlert = alert;
            _icon.Icon = alert ? _alert : _normal;
        }
        _icon.ToolTipText = "ServerIsland · " + L($"运行 {_engine.TotalRunning} · 排队 {_engine.TotalPending}", $"{_engine.TotalRunning} running · {_engine.TotalPending} pending");
        if (_icon.Menu is { } m) BuildMenu(m);
    }

    void BuildMenu(NativeMenu menu)
    {
        menu.Items.Clear();
        var hosts = _engine.OrderedHosts;
        if (hosts.Count == 0) menu.Items.Add(new NativeMenuItem(L("还没有添加服务器", "No servers yet")) { IsEnabled = false });
        foreach (var (host, snap) in hosts)
        {
            var status = snap.Health.Kind switch
            {
                HealthKind.Ok => L($"运行 {snap.RunningCount} · 排队 {snap.PendingCount}", $"{snap.RunningCount} running · {snap.PendingCount} pending"),
                HealthKind.Unknown => L("连接中…", "Connecting…"),
                _ => snap.Health.Message,
            };
            var dot = snap.Health.Kind switch { HealthKind.Ok => "🟢", HealthKind.NeedsLogin => "🟡", HealthKind.Error => "🔴", _ => "⚪" };
            menu.Items.Add(new NativeMenuItem($"{dot}  {host.DisplayName} — {status}") { IsEnabled = false });
        }
        menu.Items.Add(new NativeMenuItemSeparator());
        if (UpdateChecker.State == UpdateChecker.StateKind.Available && UpdateChecker.Latest is { } r)
        {
            menu.Items.Add(Item(L($"更新到 v{r.Version}…", $"Update to v{r.Version}…"), () => _app.InstallUpdate(r)));
            menu.Items.Add(new NativeMenuItemSeparator());
        }
        menu.Items.Add(Item(L("打开面板", "Open Panel"), _app.TogglePanel));
        menu.Items.Add(Item(L("立即刷新", "Refresh Now"), _engine.RefreshAll));
        var mode = new NativeMenuItem(L("显示方式", "Display")) { Menu = new NativeMenu() };
        foreach (var dm in Enum.GetValues<DisplayMode>())
        {
            var d = dm;
            var it = Item(dm.Label(), () => _app.SetDisplayMode(d));
            it.ToggleType = NativeMenuItemToggleType.Radio;
            it.IsChecked = _store.Settings.DisplayMode == dm;
            mode.Menu.Items.Add(it);
        }
        menu.Items.Add(mode);
        menu.Items.Add(Item(L("设置…", "Settings…"), _app.ShowSettings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item(L("退出 ServerIsland", "Quit ServerIsland"), _app.Quit));
    }

    static NativeMenuItem Item(string title, Action action)
    {
        var i = new NativeMenuItem(title);
        i.Click += (_, _) => action();
        return i;
    }
}
