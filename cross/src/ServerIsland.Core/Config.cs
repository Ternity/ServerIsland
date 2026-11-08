using System.Text.Json;
using System.Text.Json.Serialization;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 服务器类型：决定"任务"从哪里来。
/// 存储的值和 Mac 版一致：slurm / pbs / direct。
public enum HostKind { Slurm, Pbs, Direct }

/// 认证方式。需要密码 / 动态口令的机器先弹窗登录一次，之后复用这条连接。密码默认不保存；
/// 勾选"记住密码"时只存进系统的凭据库（Windows 凭据管理器 / Linux Secret Service），动态口令从不保存。
/// 存储的值 key / password / keyPassword / interactive 和 Mac 版一致（旧版的"密码 / 动态口令"即 interactive）。
public enum AuthMode { Key, Password, KeyPassword, Interactive }

/// 需要密码登录的机器，主连接保持多久（期间不用再输密码）。
public enum KeepAlive { H8, H24, D3, Forever }

/// 收起状态下刘海两侧"小翅膀"的显示策略。
public enum CollapsedStyle { Auto, Always, Never }

/// Windows / Linux 版的显示形态：屏幕顶部的虚拟刘海，或者桌面上的小章鱼。
public enum DisplayMode { Island, Pet }

/// SSH 实现：系统 ssh（支持 ControlMaster 的平台）或内置的 SSH.NET（Windows 默认）。
public enum SshBackend { Auto, OpenSsh, Managed }

public static class EnumLabels
{
    public static string Label(this HostKind k) => k switch
    {
        HostKind.Slurm => L("Slurm 集群", "Slurm cluster"),
        HostKind.Pbs => L("PBS 集群", "PBS cluster"),
        _ => L("直连服务器", "Direct server"),
    };
    public static string Badge(this HostKind k) => k switch { HostKind.Slurm => "SLURM", HostKind.Pbs => "PBS", _ => "DIRECT" };
    /// 有作业调度系统（Slurm / PBS）：任务是队列里的作业，而不是进程。
    public static bool IsScheduler(this HostKind k) => k != HostKind.Direct;
    /// 取消作业 / 终止进程用的命令（界面文案用）。
    public static string CancelCommand(this HostKind k) => k switch { HostKind.Slurm => "scancel", HostKind.Pbs => "qdel", _ => "kill -TERM" };
    public static string Label(this AuthMode a) => a switch
    {
        AuthMode.Key => L("密钥 / ssh-agent", "SSH key / agent"),
        AuthMode.Password => L("密码", "Password"),
        AuthMode.KeyPassword => L("密钥 + 密码", "Key + password"),
        _ => L("动态口令 / 二次验证", "One-time code / 2FA"),
    };
    /// 需要先弹窗登录、之后复用连接。
    public static bool NeedsLogin(this AuthMode a) => a != AuthMode.Key;
    /// 可以记住密码（动态口令每次都不同，不能记）。
    public static bool CanRemember(this AuthMode a) => a is AuthMode.Password or AuthMode.KeyPassword;

    public static string Label(this AlertScope a) => a == AlertScope.All ? L("全部任务", "All Jobs") : L("只提醒关注的任务", "Starred Jobs Only");

    public static string Label(this PushScope s) => s switch
    {
        PushScope.Starred => L("只推送关注（⭐）的任务", "Starred Jobs Only"),
        PushScope.All => L("全部任务", "All Jobs"),
        _ => L("和刘海提醒一样", "Same as Island Alerts"),
    };
    public static string ShortLabel(this PushScope s) => s switch
    {
        PushScope.Starred => L("关注的任务", "starred jobs"),
        PushScope.All => L("全部任务", "all jobs"),
        _ => L("同刘海提醒", "as island alerts"),
    };

    public static string Label(this KeepAlive k) => k switch
    {
        KeepAlive.H8 => L("8 小时", "8 hours"),
        KeepAlive.H24 => L("24 小时", "24 hours"),
        KeepAlive.D3 => L("3 天", "3 days"),
        _ => L("一直保持", "Until disconnected"),
    };

    public static string ControlPersist(this KeepAlive k) => k switch
    {
        KeepAlive.H8 => "8h",
        KeepAlive.H24 => "24h",
        KeepAlive.D3 => "72h",
        _ => "yes",
    };

    public static TimeSpan? Duration(this KeepAlive k) => k switch
    {
        KeepAlive.H8 => TimeSpan.FromHours(8),
        KeepAlive.H24 => TimeSpan.FromHours(24),
        KeepAlive.D3 => TimeSpan.FromHours(72),
        _ => null,
    };

    public static string Label(this CollapsedStyle s) => s switch
    {
        CollapsedStyle.Auto => L("有任务或异常时显示", "When busy or failing"),
        CollapsedStyle.Always => L("始终显示", "Always"),
        _ => L("从不显示", "Never"),
    };

    public static string Label(this DisplayMode m) => m == DisplayMode.Island
        ? L("屏幕顶部的灵动岛", "Island at the top of the screen")
        : L("桌面小章鱼", "Desktop pet");

    public static string Label(this SshBackend b) => b switch
    {
        SshBackend.Auto => L("自动", "Automatic"),
        SshBackend.OpenSsh => L("系统 ssh", "System ssh"),
        _ => L("内置 SSH（SSH.NET）", "Built-in SSH (SSH.NET)"),
    };
}

/// 远程执行的命令。默认值适用于大多数 Linux / Slurm 环境，对不上时可以在设置里逐项修改。
public sealed class CommandSet : IJsonOnDeserialized
{
    public const string OldSqueue = "squeue -u \"$(id -un)\" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'";

    public string Shell { get; set; } = "bash -l -s";
    public string Preamble { get; set; } = "";
    public string Squeue { get; set; } = "squeue -u \"$(id -un)\" -h -o '%i|%j|%T|%M|%l|%D|%R|%P|%F|%K'";
    /// `{jobs}` 会被替换为逗号分隔的作业号。只在作业从队列消失时调用一次。
    /// 最后一列 AllocTRES 是作业分到的资源（计费用）；没有这一列也能解析。
    public string Sacct { get; set; } = "sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode,AllocTRES -j {jobs}";
    public const string OldSacct = "sacct -n -X -P -o JobID,JobName,State,Elapsed,ExitCode -j {jobs}";
    public string Gpu { get; set; } = "nvidia-smi --query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits";
    public string GpuApps { get; set; } = "nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits";
    public string Processes { get; set; } = "ps -u \"$(id -un)\" -o pid=,etimes=,pcpu=,rss=,args= --sort=-pcpu | head -n 300";
    public string ProgressGlob { get; set; } = "~/.serverisland/progress/*.json";
    public string Disk { get; set; } = "df -P -k ~";
    /// PBS（PBS Pro / OpenPBS / Torque）：输出自己作业的 `qstat -f` 全文，由 App 的转换脚本解析。没有作业时什么都不输出。
    public string Qstat { get; set; } = """SI_IDS=$(qselect -u "$(id -un)"); [ -z "$SI_IDS" ] || qstat -f -t $SI_IDS""";

    public static readonly CommandSet Defaults = new();

    public void OnDeserialized()
    {
        // 旧版默认命令没有数组列（%F|%K），自动升级。
        if (Squeue == OldSqueue) Squeue = Defaults.Squeue;
        // 旧版默认命令没有 AllocTRES（计费用），自动升级。
        if (Sacct == OldSacct) Sacct = Defaults.Sacct;
    }
}

public sealed class WatchedProcess
{
    public string Pid { get; set; } = "";
    public string Name { get; set; } = "";
    /// 进程启动时刻（由 etimes 推算），用来识别 PID 被复用的情况。存成"距 2001-01-01 的秒数"，和 Swift 版一致。
    [JsonConverter(typeof(ReferenceDateConverter))]
    public DateTime? StartedAt { get; set; }
}

public sealed class HostConfig : IJsonOnDeserialized
{
    public const string DefaultExclude = @"/usr/(lib|libexec|share)/|^/snap/|^/usr/sbin/|gnome-|Xorg|xdg-|ibus|gsd-|gvfs|update-manager|check-new-release|unattended-upgr|networkd-dispatcher|vscode-server|cursor-server|windsurf-server|\.codex/|codex app-server|jupyter-lab|ipykernel_launcher|pylsp|pyright|ruff|wireplumber|pipewire|dbus-daemon|update-notifier|systemd|at-spi|gcr-ssh|gnome-keyring|FlClash|clash";
    public const string DefaultAppProcessNames = "gpumd|nep|gnep|vasp_std|vasp_gam|vasp_ncl|lmp|lmp_mpi|lmp_serial";
    public const string DefaultFilter = @"python|torchrun|accelerate|deepspeed|julia|matlab|Rscript|java|node|\./";

    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// `~/.ssh/config` 里的 Host 别名，或 user@1.2.3.4 这样的目标。
    public string Alias { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public HostKind Kind { get; set; } = HostKind.Direct;
    public AuthMode Auth { get; set; } = AuthMode.Key;
    public bool Enabled { get; set; } = true;
    /// null 表示跟随全局轮询间隔。
    public int? Interval { get; set; }
    public bool ShowCPU { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowGPU { get; set; } = true;
    public bool WatchJobs { get; set; } = true;
    public bool ReadProgressFiles { get; set; } = true;
    public string ProcessFilter { get; set; } = DefaultFilter;
    public string ProcessExclude { get; set; } = DefaultExclude;
    public bool ReuseConnection { get; set; } = true;
    public CommandSet Commands { get; set; } = new();
    public bool Muted { get; set; }
    public bool AllowCancel { get; set; }
    public List<WatchedProcess> Watched { get; set; } = [];
    public bool ShowDisk { get; set; } = true;
    public KeepAlive KeepAlive { get; set; } = KeepAlive.H24;
    public bool Paused { get; set; }
    public bool DetectApps { get; set; } = true;
    public string AppProcessNames { get; set; } = DefaultAppProcessNames;
    public bool SlurmAlsoProcesses { get; set; }
    public bool ImportShellPath { get; set; } = true;
    /// 手动填写的连接信息（不依赖 ~/.ssh/config）。HostName 为空表示用 Alias 连接。
    public string HostName { get; set; } = "";
    public int? Port { get; set; }
    public string User { get; set; } = "";
    /// 私钥文件路径（可用 ~）。空表示用默认密钥。
    public string IdentityFile { get; set; } = "";
    /// 经由另一台 ServerIsland 服务器（跳板机）连接；跳板机自己也可以再有跳板机。
    [JsonConverter(typeof(UpperNullableGuidConverter))]
    public Guid? JumpHostID { get; set; }
    /// 把密码记进系统凭据库，连接断开后自动重新登录（默认关闭）。
    public bool RememberPassword { get; set; }
    /// 只看这些目录下的任务（每行一个，可用 ~）。作业按工作目录、进程按当前目录判断；空表示不过滤。
    public string PathFilter { get; set; } = "";
    /// 各分区的单价（计费）；分区名为空的一行是"其他分区"。
    public List<PartitionPrice> Prices { get; set; } = [];

    /// 过滤目录列表（去掉空行）。
    [JsonIgnore]
    public List<string> PathPrefixes => PathFilter.Split(['\n', ',', ';']).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    /// 是否使用手动填写的连接信息。
    [JsonIgnore] public bool IsManual => HostName.Trim().Length > 0;

    public HostConfig() { }

    public HostConfig(string alias, string? displayName = null, HostKind kind = HostKind.Direct)
    {
        Alias = alias;
        DisplayName = displayName ?? alias;
        Kind = kind;
    }

    public void OnDeserialized()
    {
        if (string.IsNullOrEmpty(DisplayName)) DisplayName = Alias;
        // 旧版默认值自动升级（用户改过的保持不动）
        if (AppProcessNames == "gpumd|nep|gnep") AppProcessNames = DefaultAppProcessNames;
        Commands ??= new CommandSet();
        Watched ??= [];
        HostName ??= ""; User ??= ""; IdentityFile ??= ""; PathFilter ??= "";
        Prices ??= [];
    }

    public HostConfig Clone() => Json.Clone(this);
    public string Fingerprint() => JsonSerializer.Serialize(this, Json.Options);
}

public sealed class AppSettings
{
    public List<HostConfig> Hosts { get; set; } = [];
    public int DefaultInterval { get; set; } = 60;
    /// false：所有机器都只手动刷新。
    public bool AutoRefresh { get; set; } = true;
    public CollapsedStyle CollapsedStyle { get; set; } = CollapsedStyle.Always;
    /// 展开面板里把空闲的机器合并成一行（全部空闲时照常显示卡片）。
    public bool FoldIdleHosts { get; set; } = true;
    public bool IslandAlerts { get; set; } = true;
    public bool SystemNotifications { get; set; } = true;
    public bool SoundEnabled { get; set; } = true;
    public double SoundVolume { get; set; } = 0.6;
    public bool ArrayFirstFailureAlert { get; set; } = true;
    public double DiskWarnThreshold { get; set; } = 0.9;
    public AppLanguage Language { get; set; } = AppLanguage.System;
    public List<ProgressRule> ProgressRules { get; set; } = [];
    /// 诊断日志保留天数（更早的行自动删除；0 表示永久保留）。
    public int LogRetentionDays { get; set; } = 7;
    /// 启动时和之后每天检查一次 GitHub 上有没有新版本。
    public bool AutoCheckUpdates { get; set; } = true;
    /// 灵动岛显示在哪块屏幕：""=自动（Windows / Linux 上是主屏幕），"primary"=主屏幕，其它=屏幕名称。
    public string IslandScreen { get; set; } = "";
    /// 已经在刘海里提醒过的新版本（同一个版本只提醒一次）。
    public string NotifiedUpdateVersion { get; set; } = "";
    /// 关注的任务（跨服务器）。任务从完整的列表里消失后自动移除。
    public List<StarredJob> StarredJobs { get; set; } = [];
    /// 任务提醒的范围：全部任务 / 只提醒关注的任务。磁盘等服务器级提醒不受影响。
    public AlertScope AlertScope { get; set; } = AlertScope.All;
    /// 分别控制四类任务提醒。
    public bool AlertStarted { get; set; } = true;
    public bool AlertFinished { get; set; } = true;
    public bool AlertFailed { get; set; } = true;
    public bool AlertUnknown { get; set; } = true;
    /// 勿扰时段：这段时间里不响铃、不弹窗，系统通知照发但不出声（默认关）。时间是一天里的分钟数，起点晚于终点表示跨午夜。
    public bool QuietHours { get; set; }
    public int QuietStart { get; set; } = 23 * 60;
    public int QuietEnd { get; set; } = 8 * 60;

    /// 外接副屏（ESP32 固件，暂不进仓库）。默认关：不知道有这块板子的用户看不到任何相关设置。
    /// 打开后设置窗口侧栏多一个"副屏"页面，插上 USB 就自动连接并推送状态。
    public bool UsbDisplay { get; set; }
    /// 也通过蓝牙连接（插着 USB 时仍然优先用 USB）。
    public bool DisplayBluetooth { get; set; }
    /// 电脑锁屏时副屏一起熄屏。
    public bool DisplayLockSync { get; set; } = true;
    /// 副屏的日历页显示系统日历里的日程（只有 Mac 版能读系统日历；这里只保留这个键，配置文件两边通用）。
    public bool DisplayCalendar { get; set; }
    /// 截止日期倒计时（副屏的时钟和日历上显示）。
    public List<Deadline> Deadlines { get; set; } = [];
    /// 副屏上显示天气的城市（空 = 不显示；数据来自 Open-Meteo）。
    public string WeatherCity { get; set; } = "";
    /// 计费（估算费用），默认关闭。单价在各服务器的 `Prices` 里。
    public bool BillingEnabled { get; set; }
    /// 货币符号。
    public string Currency { get; set; } = "¥";
    /// 手机推送（企业微信群机器人）。key 只存凭据库，不在这里。
    public PhonePushSettings PhonePush { get; set; } = new();

    // —— 只有 Windows / Linux 版用到的设置 ——
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Island;
    public SshBackend SshBackend { get; set; } = SshBackend.Auto;
    /// 桌面小章鱼的位置（物理像素，屏幕坐标）。
    public int? PetX { get; set; }
    public int? PetY { get; set; }

    public AppSettings Clone() => Json.Clone(this);

    /// 现在是否在勿扰时段里。起点等于终点时不算勿扰。
    public bool IsQuiet(DateTime? at = null)
    {
        if (!QuietHours || QuietStart == QuietEnd) return false;
        var t = at ?? DateTime.Now;
        int m = t.Hour * 60 + t.Minute;
        return QuietStart < QuietEnd ? m >= QuietStart && m < QuietEnd : m >= QuietStart || m < QuietEnd;
    }

    /// 勿扰时段的文字，例如 23:00–08:00。
    [JsonIgnore] public string QuietLabel => Clock(QuietStart) + "–" + Clock(QuietEnd);
    public static string Clock(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    public bool IsStarred(Guid hostId, string jobId) => StarredJobs.Any(s => s.HostID == hostId && s.JobID == jobId);

    /// 这条提醒要不要发：服务器级提醒（没有任务号，如磁盘）总是发；任务提醒看种类开关和关注范围。
    /// 静音在 Emit 里判断，优先于这里。anyScope：不看关注范围（手动监听的进程）。
    public bool AllowsAlert(IslandEvent e, bool anyScope = false)
    {
        if (e.JobID is not { } job || e.HostID is not { } host) return true;
        var on = e.Kind switch
        {
            EventKind.Started => AlertStarted,
            EventKind.Finished => AlertFinished,
            EventKind.Failed => AlertFailed,
            EventKind.Info => AlertUnknown,
            _ => true,
        };
        return on && (anyScope || AlertScope == AlertScope.All || IsStarred(host, job));
    }
}

/// 一个关注的任务。数组作业关注的是整个数组（主编号）。
public sealed class StarredJob
{
    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid HostID { get; set; }
    public string JobID { get; set; } = "";
    /// 关注时的名称（只用于日志和排查）。
    public string Name { get; set; } = "";
    [JsonConverter(typeof(ReferenceDateConverter))]
    public DateTime? Since { get; set; } = DateTime.Now;
}

/// 一个截止日期（副屏上倒计时）。
public sealed class Deadline
{
    [JsonConverter(typeof(UpperGuidConverter))]
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    [JsonConverter(typeof(ReferenceDateConverter))]
    public DateTime? Date { get; set; } = DateTime.Now;
}

/// 存储的值 all / starred 和 Mac 版一致。
public enum AlertScope { All, Starred }

/// 手机推送的设置（和 Mac 版 PhonePushSettings 一致）。默认只推送关注（⭐）的任务：平时人在电脑前看刘海就够了，要离开时给重要的任务点个星。
public sealed class PhonePushSettings
{
    public bool Enabled { get; set; }
    public PushScope Scope { get; set; } = PushScope.Starred;
    public bool Started { get; set; }
    public bool Finished { get; set; } = true;
    public bool Failed { get; set; } = true;
    public bool Unknown { get; set; } = true;
    /// 磁盘快满等服务器级提醒。
    public bool ServerWarnings { get; set; } = true;
    /// Claude Code / Codex / OpenCode。
    public bool Agents { get; set; }
    public bool AgentAttention { get; set; } = true;
    public bool AgentDone { get; set; } = true;
    /// 详细：资源、用时、目录、软件进度、费用……；简洁：只有标题和服务器。
    public bool Detailed { get; set; } = true;
    /// 勿扰时段（通用页设置的时间）里不推送。
    public bool FollowQuietHours { get; set; } = true;
    /// 勿扰时段里失败的任务照常推送。
    public bool FailuresDuringQuiet { get; set; } = true;

    /// 需要读取 Agent 会话（副屏没连着时也要读）。
    [JsonIgnore] public bool WantsAgents => Enabled && Agents && (AgentAttention || AgentDone);
}

public enum PushScope { Starred, All, Island }

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}

/// Swift 的 JSONEncoder 默认把 Date 存成"距 2001-01-01 的秒数"。
public sealed class ReferenceDateConverter : JsonConverter<DateTime?>
{
    static readonly DateTime Reference = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number) return Reference.AddSeconds(reader.GetDouble()).ToLocalTime();
        if (reader.TokenType == JsonTokenType.String && DateTime.TryParse(reader.GetString(), out var d)) return d;
        return null;
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is { } d) writer.WriteNumberValue((d.ToUniversalTime() - Reference).TotalSeconds);
        else writer.WriteNullValue();
    }
}

/// UUID 和 Swift 版一样写成大写。
public sealed class UpperGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out var g) ? g : Guid.NewGuid();

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

public sealed class UpperNullableGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var g) ? g : null;

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } g) writer.WriteStringValue(g.ToString("D").ToUpperInvariant());
        else writer.WriteNullValue();
    }
}

/// 各平台的数据目录。
public static class AppPaths
{
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// 配置目录：Windows %APPDATA%\ServerIsland；Linux ~/.config/ServerIsland；
    /// macOS 上只用于开发调试，单独放一个目录，避免覆盖 Swift 版的真实配置。
    public static string ConfigDirectory
    {
        get
        {
            if (Environment.GetEnvironmentVariable("SERVERISLAND_CONFIG_DIR") is { Length: > 0 } custom) return custom;
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServerIsland");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Home, "Library", "Application Support", "ServerIsland-Avalonia");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Home, ".config") : xdg, "ServerIsland");
        }
    }

    public static string ConfigFile => Path.Combine(ConfigDirectory, "config.json");

    public static string LogDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ServerIsland", "Logs");
            if (OperatingSystem.IsMacOS()) return Path.Combine(Home, "Library", "Logs", "ServerIsland-Avalonia");
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            return Path.Combine(string.IsNullOrEmpty(state) ? Path.Combine(Home, ".local", "state") : state, "ServerIsland");
        }
    }

    public static string SshDirectory => Path.Combine(Home, ".ssh");

    public static string Tilde(string path) =>
        path.StartsWith(Home, StringComparison.Ordinal) ? "~" + path[Home.Length..] : path;
}

/// 读写 config.json。修改后 0.5 秒防抖保存。所有修改都在界面线程上进行。
public sealed class ConfigStore
{
    public AppSettings Settings { get; private set; }
    readonly bool _persist;
    public bool Persist => _persist;
    System.Threading.Timer? _saveTimer;
    readonly object _lock = new();

    /// 设置有变化（界面据此刷新、引擎据此重新安排轮询）。
    public event Action? Changed;

    /// 配置文件读不出来时的说明（启动后在刘海里提醒一次）；首次启动没有文件时为 null。
    public string? LoadProblem { get; }
    /// 读取失败、原文件又没能备份时不再保存，免得默认设置把它覆盖掉。
    readonly bool _saveBlocked;

    public ConfigStore(bool persist = true)
    {
        _persist = persist;
        AppSettings? loaded = null;
        if (persist) (loaded, LoadProblem, _saveBlocked) = Load(AppPaths.ConfigFile);
        Settings = loaded ?? new AppSettings();
        Apply();
    }

    /// 读配置文件。没有文件（首次启动）返回 null、没有问题；读不出来时先把原文件复制成 `config.broken-<时间>.json`，
    /// 再返回原因（之后改设置会覆盖 config.json，备份不动）。复制也失败时 BlockSave 为 true。
    public static (AppSettings? Settings, string? Problem, bool BlockSave) Load(string path)
    {
        if (!File.Exists(path)) return (null, null, false);
        try
        {
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json.Options) is { } s) return (s, null, false);
            throw new JsonException("null");
        }
        catch (Exception e)
        {
            DebugLog.Log("读取配置失败：" + e.Message);
            var backup = Path.Combine(Path.GetDirectoryName(path) ?? ".", $"config.broken-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(path, backup);
                return (null, L($"设置文件读取失败，已改用默认设置；原文件备份为 {Path.GetFileName(backup)}",
                                $"Couldn't read the settings file; using defaults. The original was saved as {Path.GetFileName(backup)}"), false);
            }
            catch
            {
                return (null, L($"设置文件读取失败，也没能备份；这次改动不会保存，原文件留在 {path}",
                                $"Couldn't read or back up the settings file; changes won't be saved. The original is at {path}"), true);
            }
        }
    }

    void Apply()
    {
        Lang.Setting = Settings.Language;
        ProgressRule.Active = Settings.ProgressRules;
        Ssh.SshHosts.Update(Settings.Hosts.Select(h => h.Clone()).ToList(), write: _persist);
    }

    public void Replace(AppSettings s)
    {
        Settings = s;
        Touch();
    }

    public HostConfig? Host(Guid id) => Settings.Hosts.FirstOrDefault(h => h.Id == id);

    public int Interval(HostConfig host) => Math.Max(10, host.Interval ?? Settings.DefaultInterval);

    /// 修改了 Settings 之后调用：更新语言 / 规则，通知界面，并安排保存。
    public void Touch()
    {
        Apply();
        Changed?.Invoke();
        if (!_persist || _saveBlocked) return;
        lock (_lock)
        {
            _saveTimer?.Dispose();
            var json = JsonSerializer.Serialize(Settings, Json.Options);
            _saveTimer = new System.Threading.Timer(_ => Write(json), null, 500, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        if (!_persist || _saveBlocked) return;
        lock (_lock) { _saveTimer?.Dispose(); _saveTimer = null; }
        Write(JsonSerializer.Serialize(Settings, Json.Options));
    }

    void Write(string json)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, overwrite: true);
        }
        catch (Exception e) { DebugLog.Log("保存配置失败：" + e.Message); }
    }
}
