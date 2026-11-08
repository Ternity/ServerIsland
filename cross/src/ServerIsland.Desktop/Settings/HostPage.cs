using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Platform;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 设置 → 单台服务器。分组顺序：概览 → 连接 → 环境 → 轮询 → 显示内容 → 计算软件 → 提醒 → 监听的进程 → 危险操作 → 高级 → 诊断。
/// 按服务器 ID 操作（不是下标），异步结果不会写到别的服务器上。
public sealed class HostPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine, ILoginPrompter prompter, HostConfig host)
{
    readonly Guid _id = host.Id;
    StackPanel? _chips;
    PetView? _pet;
    TextBlock? _loginState;
    Border? _loginDot;

    /// 修改这台服务器（按 ID 找到当前的配置对象再改）。
    void Edit(Action<HostConfig> change, bool refreshPage = false)
    {
        if (store.Host(_id) is not { } h) return;
        change(h);
        store.Touch();
        if (refreshPage) window.RefreshPage();
    }

    HostSnapshot? Snap => engine.Snapshots.GetValueOrDefault(_id);

    public void EngineChanged()
    {
        if (_chips != null) FillChips(_chips);
        if (_pet != null) { _pet.Mood = Mood(); _pet.InvalidateVisual(); }
    }

    PetMood Mood() => (Snap?.Health.Kind ?? HealthKind.Unknown) switch
    {
        HealthKind.Error => PetMood.Sad,
        HealthKind.NeedsLogin => PetMood.Worried,
        HealthKind.Unknown => PetMood.Waiting,
        _ => (Snap?.RunningCount ?? 0) > 0 ? PetMood.Working : PetMood.Idle,
    };

    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(Hero().Margin2(0, 0, 0, 18));
        col.Children.Add(Connection());
        col.Children.Add(Environment());
        col.Children.Add(Polling());
        col.Children.Add(Display());
        col.Children.Add(Apps());
        col.Children.Add(k.Section(L("提醒", "Alerts"), "bell", Color.Parse("#FF9F0A"), [
            k.ToggleRow(L("静音", "Mute"), L("不弹提示、不发通知、不响铃，数据照常刷新。面板卡片上的铃铛也能切换。",
                "No pop-ups, notifications or sounds; data still refreshes. Also toggled by the bell on the card."), host.Muted, v => Edit(h => h.Muted = v)),
        ]));
        if (host.Kind == HostKind.Direct || host.SlurmAlsoProcesses) col.Children.Add(Watched());
        var cmd = host.Kind.CancelCommand();
        col.Children.Add(k.Section(L("危险操作", "Danger Zone"), "warning", Color.Parse("#FF453A"), [
            k.ToggleRow(L("允许在面板里取消任务", "Allow Cancelling from the Panel"),
                L($"在任务详情页按住按钮 1.5 秒并再确认一次，才会执行 {cmd}；执行前会再次核对任务名称、所有者和启动时间。",
                  $"Hold the button for 1.5 s on the job page and confirm again to run {cmd}. The job's name, owner and start time are re-checked first."),
                host.AllowCancel, v => Edit(h => h.AllowCancel = v)),
        ]));
        col.Children.Add(Advanced());
        col.Children.Add(Diagnostics());
        return col;
    }

    // MARK: - 概览

    Control Hero()
    {
        _pet = new PetView(Mood(), 38) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var petBox = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(14), Background = Brushes.Black, Child = _pet, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBox { Text = host.DisplayName, FontSize = 19, FontWeight = FontWeight.Bold, Watermark = L("显示名称", "Display Name"), MinWidth = 260, HorizontalAlignment = HorizontalAlignment.Left };
        name.TextChanged += (_, _) => Edit(h => h.DisplayName = name.Text ?? "");
        var entry = SshConfig.Load().FirstOrDefault(e => e.Alias == host.Alias);
        var aliasRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { k.Text(host.IsManual ? SshHosts.Summary(host) : host.Alias, 12.5, mono: true) } };
        if (!host.IsManual && entry != null) aliasRow.Children.Add(k.Text(entry.Summary, 11.5, c: k.P.Secondary));
        if (host.JumpHostID is { } jid && store.Host(jid) is { } jump) aliasRow.Children.Add(k.Text(L($"经 {jump.DisplayName}", $"via {jump.DisplayName}"), 11.5, c: k.P.Secondary));
        _chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        FillChips(_chips);
        var texts = new StackPanel { Spacing = 5, Margin = new Thickness(14, 0, 0, 0), Children = { name, aliasRow, _chips } };
        var enabled = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { k.Text(L("启用", "Enabled"), 12.5, c: k.P.Secondary), k.Toggle(host.Enabled, v => Edit(h => h.Enabled = v)) } };
        var right = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { enabled, k.Button(L("立即刷新", "Refresh Now"), () => engine.Refresh(_id)).HAlign(HorizontalAlignment.Right) } };
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add(petBox);
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        Grid.SetColumn(right, 2);
        g.Children.Add(right);
        return new Border { Background = k.P.Card.B(), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), BorderBrush = k.P.Separator.B(), BorderThickness = new Thickness(0.5), Child = g };
    }

    void FillChips(StackPanel row)
    {
        row.Children.Clear();
        var snap = Snap;
        string status;
        if (!host.Enabled) status = L("已停用", "Disabled");
        else status = (snap?.Health.Kind ?? HealthKind.Unknown) switch
        {
            HealthKind.Ok => L("在线", "Online") + (snap!.Latency is { } l ? $" · {(int)(l * 1000)}ms" : ""),
            HealthKind.Unknown => L("连接中…", "Connecting…"),
            _ => snap!.Health.Message,
        };
        row.Children.Add(Chip(status, Hue.For(snap?.Health ?? HostHealth.Unknown)));
        if (snap is { Health.Kind: HealthKind.Ok } s)
        {
            row.Children.Add(Chip(L($"运行 {s.RunningCount}", $"Running {s.RunningCount}"), Hue.Mint));
            row.Children.Add(Chip(L($"排队 {s.PendingCount}", $"Pending {s.PendingCount}"), Hue.Amber));
            if (s.Metrics.CpuPercent is { } c) row.Children.Add(Chip($"CPU {(int)c}%", k.P.Secondary));
            if (s.Gpus.FirstOrDefault() is { } g) row.Children.Add(Chip($"GPU {(int)g.Utilization}%", k.P.Secondary));
        }
    }

    Control Chip(string text, Color c)
    {
        var dark = Color.FromRgb((byte)(c.R * 0.62), (byte)(c.G * 0.62), (byte)(c.B * 0.62));
        var fg = k.P.Card.R > 128 ? dark : c;
        return new Border
        {
            CornerRadius = new CornerRadius(100), Padding = new Thickness(8, 2.5), Background = c.WithAlpha(0.16).B(),
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Medium, Foreground = fg.B(), MaxWidth = 360, TextTrimming = TextTrimming.CharacterEllipsis },
        };
    }

    // MARK: - 连接

    Control Connection()
    {
        var detectMsg = k.Caption("");
        Button? detectBtn = null;
        detectBtn = k.Button(L("自动检测类型", "Detect Type"), async () =>
        {
            detectBtn!.IsEnabled = false;
            detectBtn.Content = L("检测中…", "Detecting…");
            detectMsg.Text = "";
            var h = store.Host(_id)?.Clone() ?? host;
            var r = await engine.Detect(h);
            if (r is { } d)
            {
                // 写回到"发起检测的那台服务器"。本机就跑着 slurmd（单机 Slurm 工作站）：GPU 就在这台机器上，打开 GPU 显示；"同时显示直接运行的进程"不自动打开，只在提示里说一声。
                engine.SetKind(_id, d.Slurm ? HostKind.Slurm : d.Pbs ? HostKind.Pbs : HostKind.Direct, d.Slurm ? d.Gpu || d.LocalSlurmd : null, null);
                DebugLog.Log($"[{h.DisplayName}] 自动检测类型：{(d.Slurm ? "Slurm" : d.Pbs ? "PBS" : "直连")} gpu={d.Gpu} slurmd={d.LocalSlurmd}");
                var msg = (d.Slurm ? (d.LocalSlurmd ? L("检测到单机 Slurm（也直接运行程序的话，可以在“显示内容”里打开“同时显示直接运行的进程”）",
                                                          "Single-machine Slurm detected (if you also run programs directly, turn on “Also Show Direct Processes” under Display)")
                                                        : L("检测到 Slurm", "Slurm detected"))
                           : d.Pbs ? L("检测到 PBS", "PBS detected")
                                   : L("未检测到 Slurm / PBS，设为直连", "No Slurm / PBS found; set to direct"))
                          + (d.Gpu ? " · nvidia-smi" : "") + (d.Info.Length == 0 ? "" : " · " + d.Info);
                window.RefreshPage();
                detectMsg.Text = msg;
            }
            else detectMsg.Text = L("连接失败，请先测试连接", "Connection failed; test the connection first");
            detectBtn.IsEnabled = true;
            detectBtn.Content = L("自动检测类型", "Detect Type");
        });
        var rows = new List<Control?>
        {
            k.Row(L("类型", "Type"), null, k.Segmented(Enum.GetValues<HostKind>().Select(x => (x, x.Label())), host.Kind, v => Edit(h => h.Kind = v, refreshPage: true))),
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { detectBtn, detectMsg } },
            k.Row(L("连接方式", "Connect Using"),
                L("别名：用 ~/.ssh/config 里写好的 Host（推荐已经配置过 ssh 的用户）。\n手动填写：直接填地址、用户名、端口、私钥，不需要 ~/.ssh/config。ServerIsland 不会修改 ~/.ssh/config。",
                  "Alias: use a Host from ~/.ssh/config (for people who already set up ssh).\nManual: fill in the address, user, port and key here — no ~/.ssh/config needed. ServerIsland never modifies ~/.ssh/config."),
                k.Segmented([(false, L("~/.ssh/config 别名", "~/.ssh/config alias")), (true, L("手动填写", "Manual"))], host.IsManual, SetManual)),
        };
        if (host.IsManual) rows.AddRange(ConnectionFields.Manual(k, host, a => Edit(a), window));
        else rows.Add(k.Row(L("别名", "Alias"), null, k.Text(host.Alias, 12.5, mono: true)));
        rows.Add(ConnectionFields.Jump(k, host, store.Settings.Hosts, a => Edit(a)));
        rows.AddRange(ConnectionFields.Auth(k, host, a => Edit(a, refreshPage: true)));
        if (host.Auth.NeedsLogin())
        {
            rows.Add(k.Row(L("保持连接", "Keep Connection"),
                L("登录后连接在后台保持多久，期间无需再次输入。每 30 秒发心跳；网络切换或长时间休眠可能断开。",
                  "How long the connection stays open after logging in. Heartbeats every 30 s; switching networks or long sleep may drop it."),
                k.Combo(Enum.GetValues<KeepAlive>().Select(x => (x, x.Label())), host.KeepAlive, v => Edit(h => h.KeepAlive = v))));
            _loginDot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            _loginState = k.Text(L("检查中…", "Checking…"), 12.5);
            var err = k.Status(Color.Parse("#FF9F0A"));
            Button? login = null;
            login = k.Button(L("登录…", "Log In…"), async () =>
            {
                login!.IsEnabled = false;
                login.Content = L("等待输入…", "Waiting…");
                err.Text = "";
                var h = store.Host(_id)?.Clone() ?? host;
                var e = await engine.Login(h, prompter);
                if (e != null) err.Text = L("登录失败：", "Login failed: ") + e;
                login.IsEnabled = true;
                login.Content = L("登录…", "Log In…");
                await CheckLogin();
            }, accent: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { login } };
            if (engine.Ssh is OpenSshTransport && OpenSshTransport.SupportsControlMaster)
                buttons.Children.Add(k.Button(L("终端登录", "Terminal"), () => Shell.OpenLoginInTerminal(store.Host(_id) ?? host)));
            buttons.Children.Add(k.Button(L("断开", "Disconnect"), async () => { await engine.Ssh.CloseAsync(store.Host(_id) ?? host); await CheckLogin(); }));
            var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _loginDot, _loginState } };
            rows.Add(k.Row(state, buttons));
            rows.Add(err);
            _ = CheckLogin();
        }
        return k.Section(L("连接", "Connection"), "globe", Color.Parse("#0A84FF"), rows,
            engine.Ssh is ManagedSshTransport ? L("使用内置 SSH：读取 ~/.ssh/config（HostName、User、Port、IdentityFile、ProxyJump），首次连接要确认主机指纹。",
                                                  "Using the built-in SSH: reads ~/.ssh/config (HostName, User, Port, IdentityFile, ProxyJump); confirm the host key on first connection.") : null);
    }

    /// 切换"别名 / 手动填写"：切到手动时把别名里能看出来的信息带过去。
    void SetManual(bool on)
    {
        if (on == host.IsManual) return;
        Edit(h =>
        {
            if (on)
            {
                var r = SshConfig.Resolve(h.Alias);
                h.HostName = string.IsNullOrEmpty(r.HostName) ? h.Alias : r.HostName;
                if (h.User.Length == 0 && h.Alias.Contains('@')) h.User = h.Alias[..h.Alias.LastIndexOf('@')];
                if (h.User.Length == 0 && SshConfig.Load().FirstOrDefault(e => e.Alias == h.Alias)?.User is { } u) h.User = u;
                if (h.Port == null && r.Port != 22) h.Port = r.Port;
            }
            else h.HostName = "";
        }, refreshPage: true);
    }

    async Task CheckLogin()
    {
        if (_loginState == null || _loginDot == null) return;
        bool alive = await engine.Ssh.IsLoggedInAsync(store.Host(_id) ?? host);
        _loginState.Text = alive ? L("已登录", "Logged in") : L("未登录", "Not logged in");
        _loginDot.Background = (alive ? Hue.Mint : Hue.Amber).B();
    }

    // MARK: - 环境

    Control Environment()
    {
        var preamble = k.Field(host.Commands.Preamble, v => Edit(h => h.Commands.Preamble = v), "source ~/software/slurm/env.sh", mono: true, multiline: true);
        preamble.MaxHeight = 120;
        var results = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0) };
        Button? check = null;
        check = k.Button(L("检查命令", "Check Commands"), async () =>
        {
            check!.IsEnabled = false;
            check.Content = L("检查中…", "Checking…");
            var r = await engine.CheckEnvironment(store.Host(_id)?.Clone() ?? host);
            results.Children.Clear();
            foreach (var (n, p) in r)
            {
                results.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
                {
                    new Icon(p == null ? "xmark" : "check", 12, (p == null ? k.P.Secondary : Hue.Mint).B(), 2.8) { VerticalAlignment = VerticalAlignment.Center },
                    k.Text(n, 12.5, mono: true),
                    k.Text(p ?? L("未找到", "not found"), 11.5, c: k.P.Secondary, mono: true),
                } });
            }
            check.IsEnabled = true;
            check.Content = L("检查命令", "Check Commands");
        });
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { check.VAlign(VerticalAlignment.Top), results } };
        return k.Section(L("环境", "Environment"), "box", Color.Parse("#AC8E68"), [
            new StackPanel { Spacing = 6, Children =
            {
                k.Label(L("连接后先执行", "Run First"), L("每次查询前先执行这些命令，用来加载 Slurm、CUDA 等环境。例如：\nsource ~/software/slurm/env.sh\nmodule load slurm\nexport PATH=$HOME/software/slurm/install/bin:$PATH",
                    "Commands run before every query, to load Slurm, CUDA, etc. For example:\nsource ~/software/slurm/env.sh\nmodule load slurm\nexport PATH=$HOME/software/slurm/install/bin:$PATH")),
                preamble,
            } },
            k.ToggleRow(L("补全 PATH", "Import PATH from rc files"),
                L("非交互登录不会执行 ~/.bashrc 中“非交互就 return”之后的内容，装在家目录下的程序会找不到。开启后，把 ~/.bashrc 等文件里 PATH= 行中存在的目录追加到 PATH 末尾，不会启动交互式 shell。",
                  "Non-interactive logins skip most of ~/.bashrc, so programs installed under your home directory may not be found. When on, existing directories from PATH= lines in ~/.bashrc etc. are appended to PATH, without starting an interactive shell."),
                host.ImportShellPath, v => Edit(h => h.ImportShellPath = v)),
            checkRow,
        ]);
    }

    // MARK: - 轮询

    Control Polling() => k.Section(L("轮询", "Refresh"), "refresh", Color.Parse("#32ADE6"), [
        k.ToggleRow(L("暂停自动刷新", "Pause Auto Refresh"), L("仍然显示在面板里，但不会自动连接；需要时在卡片上点“刷新一次”。",
            "Stays in the panel but is never contacted automatically; use “Refresh Once” on its card."), host.Paused, v => engine.SetPaused(_id, v)),
        k.Row(L("轮询间隔", "Refresh Interval"), null, IntervalPicker.Build(k, host.Interval, true, store.Settings.DefaultInterval, v => engine.SetHostInterval(_id, v))),
    ]);

    // MARK: - 显示内容

    Control Display()
    {
        var rows = new List<Control?>
        {
            k.ToggleRow(host.Kind switch { HostKind.Slurm => L("我的 Slurm 作业", "My Slurm Jobs"), HostKind.Pbs => L("我的 PBS 作业", "My PBS Jobs"), _ => L("我的进程", "My Processes") },
                host.Kind switch
                {
                    HostKind.Slurm => L("用 squeue 读取你的作业，数组作业会合并成一行。", "Reads your jobs with squeue; job arrays are merged into one row."),
                    HostKind.Pbs => L("用 qselect + qstat -f 读取你的作业（PBS Pro / OpenPBS / Torque），数组作业会合并成一行。", "Reads your jobs with qselect + qstat -f (PBS Pro / OpenPBS / Torque); job arrays are merged into one row."),
                    _ => L("用 ps 读取你的进程，按下面的规则过滤。", "Reads your processes with ps, filtered by the rules below."),
                },
                host.WatchJobs, v => Edit(h => h.WatchJobs = v)),
        };
        if (host.Kind.IsScheduler())
            rows.Add(k.ToggleRow(L("同时显示直接运行的进程", "Also Show Direct Processes"),
                L("适合单机装了作业调度系统的工作站：既提交作业，也直接运行程序。属于作业的进程不会重复显示。集群登录节点一般不需要。",
                  "For single-machine workstations with a scheduler where you also run programs directly. Processes that belong to jobs are not listed twice. Usually not needed on cluster login nodes."),
                host.SlurmAlsoProcesses, v => Edit(h => h.SlurmAlsoProcesses = v, refreshPage: true)));
        rows.Add(k.WideRow(L("只看这些目录", "Only These Folders"),
            L("只显示工作目录在这些文件夹（含子文件夹）里的任务，每行一个，可以用 ~。作业按提交时的工作目录判断，进程按当前所在目录判断。留空显示全部。手动监听的进程和进度文件不受影响。",
              "Only show tasks whose working directory is inside one of these folders (subfolders included), one per line; ~ works. Jobs use their submit directory, processes their current directory. Empty shows everything. Watched processes and progress files are not affected."),
            k.Field(host.PathFilter, v => Edit(h => h.PathFilter = v), "~/projects/LLZO", mono: true, multiline: true)));
        if (host.Kind == HostKind.Direct || host.SlurmAlsoProcesses)
        {
            rows.Add(k.WideRow(L("包含", "Include"), L("只显示命令行匹配这个正则的进程；留空显示全部。占用 GPU 的进程总会显示。",
                "Only processes whose command line matches this regex are shown; empty shows all. GPU processes are always shown."),
                k.Field(host.ProcessFilter, v => Edit(h => h.ProcessFilter = v), mono: true)));
            rows.Add(k.WideRow(L("排除", "Exclude"), L("匹配这个正则的进程一律忽略，默认排除系统服务、桌面和编辑器后台。运行不到 30 秒就结束的进程不提醒。",
                "Processes matching this regex are ignored (system services, desktop, editor backends by default). Processes that end within 30 s are not reported."),
                WithReset(k.Field(host.ProcessExclude, v => Edit(h => h.ProcessExclude = v), mono: true), HostConfig.DefaultExclude)));
        }
        rows.Add(k.ToggleRow("CPU", null, host.ShowCPU, v => Edit(h => h.ShowCPU = v)));
        rows.Add(k.ToggleRow(L("内存", "Memory"), null, host.ShowMemory, v => Edit(h => h.ShowMemory = v)));
        rows.Add(k.ToggleRow("GPU", L("通过 nvidia-smi 读取。Slurm 登录节点上通常看不到计算节点的 GPU。", "Read with nvidia-smi. On Slurm login nodes the compute nodes' GPUs are usually not visible."),
            host.ShowGPU, v => Edit(h => h.ShowGPU = v)));
        rows.Add(k.ToggleRow(L("磁盘", "Disk"), L("超过提醒阈值时提醒一次；路径可以在“高级”里修改。", "Alerts once when usage passes the threshold; paths can be changed under Advanced."),
            host.ShowDisk, v => Edit(h => h.ShowDisk = v)));
        rows.Add(k.ToggleRow(L("进度文件", "Progress Files"), L("读取 ~/.serverisland/progress/*.json，用来显示脚本自己上报的进度。", "Reads ~/.serverisland/progress/*.json to show progress reported by your scripts."),
            host.ReadProgressFiles, v => Edit(h => h.ReadProgressFiles = v)));
        return k.Section(L("显示内容", "Display"), "layout", Color.Parse("#14B8A6"), rows);
    }

    Control WithReset(TextBox box, string def)
    {
        var reset = k.Link(L("恢复默认", "Reset"), () => box.Text = def);
        reset.IsVisible = box.Text != def;
        box.TextChanged += (_, _) => reset.IsVisible = box.Text != def;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        g.Children.Add(box);
        reset.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(reset, 1);
        g.Children.Add(reset);
        return g;
    }

    // MARK: - 计算软件

    Control Apps()
    {
        var rows = new List<Control?>
        {
            k.ToggleRow(L("识别计算进度", "Detect Progress"),
                L("在任务的工作目录里找特征文件：\nloss.out + nep.in → NEP 训练\nloss.out + gnep.in → GNEP 训练\n带表头的 loss.out（可在 output/ 里）→ TorchNEP 训练\nlogs/*.log → MACE 训练\nlcurve.out → DeePMD 训练\nrun.in + neighbor.out / thermo.out → GPUMD 模拟\nINCAR + OSZICAR → VASP\nlog.lammps → LAMMPS\n与任务列表同一次轮询完成，只读文件末尾。",
                  "Looks for marker files in each job's working directory:\nloss.out + nep.in → NEP training\nloss.out + gnep.in → GNEP training\nloss.out with a header (may be in output/) → TorchNEP training\nlogs/*.log → MACE training\nlcurve.out → DeePMD training\nrun.in + neighbor.out / thermo.out → GPUMD MD\nINCAR + OSZICAR → VASP\nlog.lammps → LAMMPS\nDone in the same poll, reading only file tails."),
                host.DetectApps, v => Edit(h => h.DetectApps = v, refreshPage: true)),
        };
        if (host.DetectApps)
            rows.Add(k.WideRow(L("进程名", "Process Names"), L("名字匹配的进程会被检查工作目录；另外，占用 GPU、CPU ≥ 50% 或属于 Slurm 作业的进程也会检查。",
                "Processes with matching names have their working directory checked; so do processes using a GPU, ≥ 50% CPU, or belonging to a Slurm job."),
                WithReset(k.Field(host.AppProcessNames, v => Edit(h => h.AppProcessNames = v), mono: true), HostConfig.DefaultAppProcessNames)));
        return k.Section(L("计算软件", "Scientific Software"), "atom", Color.Parse("#00C7BE"), rows,
            L("支持 NEP / GNEP、GPUMD、VASP、LAMMPS；也可以在“进度识别”里加自己的规则。", "Supports NEP / GNEP, GPUMD, VASP and LAMMPS; add your own rules under Progress Detection."));
    }

    // MARK: - 监听的进程

    Control Watched()
    {
        var rows = new List<Control?>();
        if (host.Watched.Count == 0)
            rows.Add(k.Caption(L("还没有监听的进程。也可以在面板卡片上点 👁 从高负载进程里挑。监听的进程不受过滤规则影响，结束时一定会提醒。",
                                 "No watched processes yet. You can also pick one with 👁 on the card. Watched processes ignore filters and always alert when they end.")));
        foreach (var w in host.Watched)
        {
            var pid = w.Pid;
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
            row.Children.Add(new Icon("eyeFill", 13, Hue.Mint.B()) { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            var name = k.Text(w.Name.Length == 0 ? $"PID {w.Pid}" : w.Name);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var p = k.Text($"pid {w.Pid}", 11.5, c: k.P.Secondary, mono: true).Margin2(8, 0, 0, 0);
            Grid.SetColumn(p, 2);
            row.Children.Add(p);
            var rm = k.Link(L("移除", "Remove"), () => Edit(h => h.Watched.RemoveAll(x => x.Pid == pid), refreshPage: true));
            Grid.SetColumn(rm, 3);
            row.Children.Add(rm);
            rows.Add(row);
        }
        var box = new TextBox { Watermark = L("输入 PID", "PID"), Width = 140, FontFamily = Hue.Mono };
        void Add()
        {
            var pid = box.Text?.Trim() ?? "";
            if (Text.Int(pid) == null || host.Watched.Any(x => x.Pid == pid)) return;
            Edit(h => h.Watched.Add(new WatchedProcess { Pid = pid }), refreshPage: true);
        }
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Add(); };
        rows.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, k.Button(L("添加", "Add"), Add) } });
        return k.Section(L("监听的进程", "Watched Processes"), "eye", Color.Parse("#30D158"), rows);
    }

    // MARK: - 高级

    Control Advanced()
    {
        var d = CommandSet.Defaults;
        var fields = new StackPanel { Spacing = 10 };
        fields.Children.Add(CommandField(L("远端 shell", "Remote Shell"), L("脚本通过 stdin 传入", "script is sent via stdin"), host.Commands.Shell, d.Shell, v => Edit(h => h.Commands.Shell = v)));
        if (host.Kind == HostKind.Slurm)
        {
            fields.Children.Add(CommandField("squeue", "%i|%j|%T|%M|%l|%D|%R|%P|%F|%K", host.Commands.Squeue, d.Squeue, v => Edit(h => h.Commands.Squeue = v)));
            fields.Children.Add(CommandField("sacct", L("{jobs} 替换为作业号", "{jobs} = job IDs"), host.Commands.Sacct, d.Sacct, v => Edit(h => h.Commands.Sacct = v)));
        }
        if (host.Kind == HostKind.Pbs)
            fields.Children.Add(CommandField("qstat", L("输出自己作业的 qstat -f 全文", "prints qstat -f for your jobs"), host.Commands.Qstat, d.Qstat, v => Edit(h => h.Commands.Qstat = v)));
        if (host.Kind == HostKind.Direct || host.SlurmAlsoProcesses)
        {
            fields.Children.Add(CommandField(L("进程列表", "Processes"), "pid etimes pcpu rss args", host.Commands.Processes, d.Processes, v => Edit(h => h.Commands.Processes = v)));
            fields.Children.Add(CommandField(L("GPU 进程", "GPU Processes"), "pid, used_memory(MB)", host.Commands.GpuApps, d.GpuApps, v => Edit(h => h.Commands.GpuApps = v)));
        }
        fields.Children.Add(CommandField("GPU", "index, name, util, mem.used, mem.total, temp", host.Commands.Gpu, d.Gpu, v => Edit(h => h.Commands.Gpu = v)));
        fields.Children.Add(CommandField(L("进度文件", "Progress Files"), L("shell 通配符", "shell glob"), host.Commands.ProgressGlob, d.ProgressGlob, v => Edit(h => h.Commands.ProgressGlob = v)));
        fields.Children.Add(CommandField(L("磁盘", "Disk"), "df -P -k ~ /scratch", host.Commands.Disk, d.Disk, v => Edit(h => h.Commands.Disk = v)));
        if (engine.Ssh is OpenSshTransport && OpenSshTransport.SupportsControlMaster)
        {
            var reuse = k.ToggleRow(L("复用 SSH 连接", "Reuse SSH Connection"), null, host.ReuseConnection, v => Edit(h => h.ReuseConnection = v));
            reuse.IsEnabled = !host.Auth.NeedsLogin();
            fields.Children.Add(reuse);
        }
        var expander = new Expander { Header = L("远程命令", "Remote Commands"), Content = fields, HorizontalAlignment = HorizontalAlignment.Stretch };
        return k.Section(L("高级", "Advanced"), "terminal", Color.Parse("#636366"), [expander]);
    }

    Control CommandField(string title, string hint, string value, string def, Action<string> set)
    {
        var box = k.Field(value, set, mono: true);
        var reset = k.Link(L("恢复默认", "Reset"), () => box.Text = def);
        reset.IsVisible = value != def;
        box.TextChanged += (_, _) => reset.IsVisible = box.Text != def;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(k.Text(title, 12.5, FontWeight.Medium));
        var h2 = k.Caption(hint).Margin2(8, 0, 0, 0);
        h2.TextWrapping = TextWrapping.NoWrap;
        Grid.SetColumn(h2, 1);
        head.Children.Add(h2);
        Grid.SetColumn(reset, 2);
        head.Children.Add(reset);
        return new StackPanel { Spacing = 4, Children = { head, box } };
    }

    // MARK: - 诊断

    Control Diagnostics()
    {
        var output = new SelectableTextBlock { FontFamily = Hue.Mono, FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = k.P.Text.B() };
        var box = new Border
        {
            Height = 220, CornerRadius = new CornerRadius(6), Background = k.P.Field.B(), BorderBrush = k.P.Separator.B(), BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = output, Padding = new Thickness(8) }, IsVisible = false,
            Tag = "diag",
        };
        Button? test = null;
        test = k.Button(L("测试连接并查看原始输出", "Test Connection & Show Raw Output"), async () =>
        {
            test!.IsEnabled = false;
            test.Content = L("运行中…", "Running…");
            var h = store.Host(_id)?.Clone() ?? host;
            output.Text = await engine.Diagnose(h);
            box.IsVisible = true;
            test.IsEnabled = true;
            test.Content = L("测试连接并查看原始输出", "Test Connection & Show Raw Output");
            engine.Refresh(_id);
        });
        var remove = k.Button(L("移除这台服务器", "Remove Server"), async () =>
        {
            var ok = await PromptDialog.Confirm(L($"移除 {host.DisplayName}？", $"Remove {host.DisplayName}?"),
                L("只会从 ServerIsland 中移除，不会修改 ~/.ssh/config。", "Only removes it from ServerIsland; ~/.ssh/config is not modified."), L("移除", "Remove"), L("取消", "Cancel"));
            if (!ok) return;
            store.Settings.Hosts.RemoveAll(x => x.Id == _id);
            // 以它为跳板机的服务器改回直连；记住的密码一并删除。
            foreach (var other in store.Settings.Hosts.Where(x => x.JumpHostID == _id)) other.JumpHostID = null;
            CredentialStore.Delete(_id);
            store.Touch();
            window.HostRemoved();
        }, danger: true);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        row.Children.Add(test);
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return k.Section(L("诊断", "Diagnostics"), "pulse", Color.Parse("#BF5AF2"), [row, box]);
    }
}

public static class AlignExt
{
    public static T HAlign<T>(this T c, HorizontalAlignment a) where T : Control { c.HorizontalAlignment = a; return c; }
    public static T VAlign<T>(this T c, VerticalAlignment a) where T : Control { c.VerticalAlignment = a; return c; }
}
