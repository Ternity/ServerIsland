using System.Runtime.InteropServices;
using Avalonia;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Platform;

namespace ServerIsland.Desktop;

static class Program
{
    public static bool Demo { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // 作为 AI 编程工具的钩子被调用：记一条会话事件就退出（见 AgentMonitor.cs）。不打印任何东西，越快越好。
        //   ServerIsland --agent-event claude|opencode [事件 JSON；不给就从标准输入读]
        if (Index(args, "--agent-event") is { } ae && ae + 1 < args.Length)
        {
            AgentEvents.Record(args[ae + 1], ae + 2 < args.Length ? args[ae + 2] : null);
            return 0;
        }

        // 作为 ssh 的 SSH_ASKPASS 被调用（Linux 上用系统 ssh 登录时）：弹出密码框，结果写到 stdout。
        if (Environment.GetEnvironmentVariable(OpenSshTransport.AskPassFlag) == "1")
        {
            AttachConsoleIfAny();
            return AskPassApp.Run(args);
        }

        bool cli = args.Any(a => a is "--ai-usage" or "--selftest" or "--render-preview" or "--diagnose" or "--dump-scripts" or "--check-update" or "--curve" or "--version" or "--help" or "--ui-smoke" or "--push-preview");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => DebugLog.Log("未处理的异常：" + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { DebugLog.Log("后台任务异常：" + e.Exception); e.SetObserved(); };
        if (cli) AttachConsoleIfAny();

        if (args.Contains("--version")) { Console.WriteLine("ServerIsland " + AppInfo.Version); return 0; }
        if (args.Contains("--help"))
        {
            Console.WriteLine("""
                ServerIsland
                  --demo                         演示模式（不连接服务器）
                  --selftest                     解析 / 脚本自检
                  --diagnose <别名> [slurm|pbs|direct|slurm+]   对真实服务器跑一次完整采集并打印
                  --render-preview <目录> [zh|en]  把各个界面渲染成 PNG（演示数据）
                  --dump-scripts <目录>          导出生成的远端脚本（和 Swift 版对比用）
                  --push-preview [zh|en]         打印手机推送的示例消息（不联网）
                  --ui-smoke <目录>              用演示数据在真实窗口上走一遍界面并截图（自查用）
                  --ai-usage                     读一次 CC Switch 的 AI 用量并打印（只读，开发用）
                  --agent-event claude|opencode  AI 编程工具的钩子入口（由钩子调用，不要手动运行）
                """);
            return 0;
        }

        // 开发用：读一次 CC Switch 的 AI 用量，打印发给副屏的 JSON（只读）。
        if (args.Contains("--ai-usage"))
        {
            if (AIUsage.Snapshot() is not { } msg) { Console.WriteLine("没有找到 CC Switch 的数据库：" + AIUsage.DatabasePath); return 1; }
            Console.WriteLine(msg.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return 0;
        }

        // 开发用：打印手机推送的示例消息（不联网）。
        if (Index(args, "--push-preview") is { } pi)
        {
            Lang.Setting = pi + 1 < args.Length && args[pi + 1] == "en" ? AppLanguage.En : AppLanguage.Zh;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var samples = PhonePush.Samples();
            var items = samples.Select(s => PhonePush.ItemFor(s.Event, s.Host, true)).ToList();
            foreach (var it in items) Console.WriteLine(it.Text + "\n");
            Console.WriteLine("—— " + Lang.L("简洁", "Brief") + " ——\n" + PhonePush.ItemFor(samples[0].Event, samples[0].Host, false).Text + "\n");
            Console.WriteLine("—— " + Lang.L("合并", "Merged") + " ——\n" + PhonePush.Merged(items, samples.Select(s => s.Host).ToList()));
            return 0;
        }

        if (args.Contains("--selftest"))
        {
            DebugLog.Enabled = false;
            return SelfTest.Run() ? 0 : 1;
        }

        // 开发用：检查 GitHub 上的最新版本。  --check-update [假装的当前版本]
        if (Index(args, "--check-update") is { } cu)
        {
            UpdateChecker.CurrentVersion = cu + 1 < args.Length ? args[cu + 1] : AppInfo.Version;
            var rel = UpdateChecker.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine(rel != null ? $"有新版本 {rel.Version}\n  下载：{rel.DownloadUrl}\n  页面：{rel.PageUrl}\n  说明：{rel.Notes}"
                : UpdateChecker.State == UpdateChecker.StateKind.UpToDate ? $"已是最新（GitHub 最新 {UpdateChecker.Message}）" : "失败：" + UpdateChecker.Message);
            return 0;
        }

        // 开发用：读取（两次，第二次应是增量）详情页曲线。  --curve <别名> <目录> [nep|gnep|vasp|auto]
        if (Index(args, "--curve") is { } ci && ci + 2 < args.Length)
        {
            Diagnose.Curve(args[ci + 1], args[ci + 2], ci + 3 < args.Length ? args[ci + 3] : "nep").GetAwaiter().GetResult();
            return 0;
        }

        if (Index(args, "--dump-scripts") is { } di && di + 1 < args.Length)
        {
            Directory.CreateDirectory(args[di + 1]);
            foreach (var (name, text) in SelfTest.ReferenceScripts()) File.WriteAllText(Path.Combine(args[di + 1], name), text);
            Console.WriteLine("ok");
            return 0;
        }

        if (Index(args, "--diagnose") is { } gi && gi + 1 < args.Length)
        {
            var kindArg = gi + 2 < args.Length ? args[gi + 2] : "direct";
            var kind = kindArg.StartsWith("slurm") ? HostKind.Slurm : kindArg.StartsWith("pbs") ? HostKind.Pbs : HostKind.Direct;
            Diagnose.Run(args[gi + 1], kind, kindArg.EndsWith('+')).GetAwaiter().GetResult();
            return 0;
        }

        if (Index(args, "--render-preview") is { } ri && ri + 1 < args.Length)
        {
            var lang = ri + 2 < args.Length && args[ri + 2] == "en" ? AppLanguage.En : AppLanguage.Zh;
            DebugLog.Enabled = false;
            return Preview.PreviewRenderer.Run(args[ri + 1], lang);
        }

        // 开发用：在临时目录里走一遍自动更新（假的安装包，不碰真正的程序）
        if (Index(args, "--test-update") is { } tu && tu + 1 < args.Length)
        {
            Lang.Setting = AppLanguage.Zh;
            return UpdateInstallerTest.Run(args[tu + 1]) ? 0 : 1;
        }
        if (Index(args, "--ui-smoke") is { } ui && ui + 1 < args.Length) Preview.UiSmoke.Directory = args[ui + 1];
        Demo = args.Contains("--demo") || Preview.UiSmoke.Directory != null || Environment.GetEnvironmentVariable("SERVERISLAND_DEMO") == "1";
        // 自动更新后由旧程序启动：先等旧程序退出，再去抢"只运行一个"的锁
        if (Index(args, "--after-update") is { } au && au + 1 < args.Length && int.TryParse(args[au + 1], out var oldPid))
            UpdateInstaller.WaitForExit(oldPid);
        if (!Demo)
        {
            if (!SingleInstance.TryAcquire())
            {
                WinSingleInstance.RaiseExisting();   // 已经在运行：让它把岛重新显出来，再静默退出
                return 0;
            }
            WinSingleInstance.Create();              // 第一个实例：准备接收「把岛叫出来」的信号
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .LogToTrace();

    static int? Index(string[] args, string flag) => Array.IndexOf(args, flag) is var i and >= 0 ? i : null;

    /// Windows 的 WinExe 没有控制台：从命令行启动时把输出接到父进程的控制台上。
    static void AttachConsoleIfAny()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (AttachConsole(-1))
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            Console.SetError(stdout);
        }
        Console.OutputEncoding = System.Text.Encoding.UTF8;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}

public static class AppInfo
{
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "dev";

    public const string Repo = "https://github.com/Ternity/ServerIsland";
    public const string Issues = "https://github.com/Ternity/ServerIsland/issues";
}

/// 只允许运行一个实例（两个实例会同时轮询、同时弹窗）。
static class SingleInstance
{
    static Mutex? _mutex;

    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(true, "ServerIsland-" + Environment.UserName, out bool created);
            return created;
        }
        catch { return true; }
    }
}

/// `--diagnose <别名> [slurm|pbs|direct|slurm+]`：对真实服务器跑一次完整采集并打印（只读）。
static class Diagnose
{
    /// 和 Mac 版 `--curve` 一样：用设置里的 SSH 实现（或 SERVERISLAND_SSH=managed|openssh）读两次曲线。
    public static async Task Curve(string alias, string dir, string mode)
    {
        var real = new ConfigStore(persist: true);
        var backend = Environment.GetEnvironmentVariable("SERVERISLAND_SSH") switch
        {
            "managed" => SshBackend.Managed, "openssh" => SshBackend.OpenSsh, _ => real.Settings.SshBackend,
        };
        var ssh = SshCommon.Create(backend);
        var host = real.Settings.Hosts.FirstOrDefault(h => h.Alias == alias || h.DisplayName == alias)?.Clone() ?? new HostConfig(alias);
        var app = new AppProgress { Kind = mode == "gnep" ? AppKind.Gnep : mode == "vasp" ? AppKind.Vasp : AppKind.Nep, Dir = dir };
        if (mode == "vasp") app.Vasp = VaspMode.Relax;
        Console.WriteLine($"SSH：{ssh.Name}");
        if (mode == "auto")
        {
            // 先在目录上跑一次识别（TorchNEP / MACE 要用它找文件）
            var d = await ssh.RunAsync(host, host.Commands.Shell, AppDetectors.FinalStateScript("x", dir), TimeSpan.FromSeconds(30));
            if (AppDetectors.Parse(d.Stdout.Split('\n')).GetValueOrDefault("x") is not { } found) { Console.WriteLine("没识别到\n" + d.Stdout); return; }
            app = found;
            Console.WriteLine($"{app.Title} {app.Summary}");
        }
        foreach (var src in AppCurves.SourcesFor(app))
        {
            Console.WriteLine("== " + src.Path);
            var cache = new CurveCache();
            for (int round = 1; round <= 2; round++)
            {
                var r = await ssh.RunAsync(host, host.Commands.Shell, AppCurves.Script(src, cache.LineCount, cache.Head), TimeSpan.FromSeconds(30));
                var rows = r.Stdout.Split('\n').Count(l => l.StartsWith("row="));
                if (AppCurves.Merge(r.Stdout, cache) is not { } merged) { Console.WriteLine($"读不到（exit {r.Status}）：{r.Stderr.Trim()}\n{r.Stdout}"); break; }
                cache = merged;
                Console.WriteLine($"第 {round} 次：文件 {cache.LineCount} 行，本次传回 {rows} 行，累计 {cache.Rows.Count} 行，{r.Duration:0.00}s");
            }
            foreach (var c in AppCurves.Curves(src, cache.Rows, cache.Extra))
                foreach (var se in c.SeriesList)
                    Console.WriteLine($"  {se.Name} {se.Points.Count} 点：" + string.Join(" ", se.Points.Select(p => Text.Fmt(p.Y, "0.####"))));
            if (AppCurves.Curve(src, cache.Rows, cache.Extra) is { } c2) { if (c2.Threshold is { } t) Console.WriteLine($"  目标 {t}"); if (c2.Note != null) Console.WriteLine("  说明 " + c2.Note); }
        }
        await ssh.CloseAsync(host);
    }

    public static async Task Run(string alias, HostKind kind, bool alsoProcesses)
    {
        var store = new ConfigStore(persist: false);
        var real = new ConfigStore(persist: true);
        store.Settings.SshBackend = Environment.GetEnvironmentVariable("SERVERISLAND_SSH") switch
        {
            "managed" => SshBackend.Managed,
            "openssh" => SshBackend.OpenSsh,
            _ => real.Settings.SshBackend,
        };
        var host = new HostConfig(alias, kind: kind) { SlurmAlsoProcesses = alsoProcesses };
        // 已添加的服务器（按别名或显示名称）：用它的全部连接设置（手动地址、跳板机、认证）。
        if (real.Settings.Hosts.FirstOrDefault(h => h.Alias == alias || h.DisplayName == alias) is { } configured)
        {
            host = configured.Clone();
            host.Kind = kind;
            host.SlurmAlsoProcesses = alsoProcesses;
            Console.WriteLine($"（使用设置里的“{host.DisplayName}”：{SshHosts.Summary(host)}，跳板机 [{string.Join(", ", SshHosts.JumpChain(host).Select(j => j.DisplayName))}]）");
        }
        store.Settings.Hosts = [host];
        var engine = new MonitorEngine(store, ssh: SshCommon.Create(store.Settings.SshBackend), history: new HistoryStore(persist: false));
        Console.WriteLine(await engine.Diagnose(host));
        Console.WriteLine("\n===== 解析结果 =====");
        await engine.PollForDiagnosis(host);
        if (!engine.Snapshots.TryGetValue(host.Id, out var snap)) return;
        Console.WriteLine("health: " + snap.Health);
        Console.WriteLine($"metrics: cpu={snap.Metrics.CpuPercent} cores={snap.Metrics.Cores} load={snap.Metrics.Load1} mem={snap.Metrics.MemoryUsed}/{snap.Metrics.MemoryTotal}");
        foreach (var g in snap.Gpus) Console.WriteLine($"gpu: {g.Index} {g.Name} {g.Utilization}% {g.MemoryUsedMB}/{g.MemoryTotalMB}");
        foreach (var j in snap.Jobs)
        {
            Console.WriteLine($"job: {j.Id} {j.Name} {j.State} {j.Elapsed ?? -1} {j.Detail.Prefix(80)}");
            if (j.Message != null) Console.WriteLine($"     message: {j.Message} progress: {(j.Progress is { } p ? Format.Percent(p) : "-")}");
        }
        Console.WriteLine("warnings: " + string.Join(" | ", snap.Warnings));
        foreach (var d in snap.Disks) Console.WriteLine($"disk: {d.Mount} {Format.Percent(d.Fraction)}");
        var top = await engine.TopProcesses(host) ?? [];
        Console.WriteLine("\n===== 高负载进程（监听候选） =====");
        foreach (var p in top) Console.WriteLine($"{p.Id} {p.Name} {p.CpuPercent ?? 0} {p.GpuMemoryMB ?? 0}");
        var h = await engine.FetchHistory(host);
        Console.WriteLine($"\nhistory: fromServer={h.FromServer} count={h.Entries.Count}");
    }
}
