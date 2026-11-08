using ServerIsland.Core.Ssh;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

public enum PetMood { Idle, Working, Waiting, Happy, Sad, Worried }

/// 负责按间隔轮询每台服务器、合并结果、识别"任务完成 / 失败 / 开始"等事件。
/// 和 Swift 版一样只在界面线程上使用：异步操作 await 之后会回到界面线程（命令行模式下没有界面线程，也按顺序执行）。
public sealed partial class MonitorEngine
{
    public Dictionary<Guid, HostSnapshot> Snapshots { get; } = [];
    public IslandEvent? CurrentEvent { get; private set; }
    public HashSet<Guid> LoggingIn { get; } = [];

    public ConfigStore Store { get; }
    public bool Demo { get; }
    public ISshTransport Ssh { get; private set; }
    public HistoryStore History { get; }
    /// 费用账本（打开计费后，作业结束时记一笔）。
    public CostLedger Costs { get; }
    /// 运行中作业分到的资源（主机 → 作业号 → 资源）。Slurm 每个作业只查一次；PBS 每轮从 qstat 全文里取。
    internal readonly Dictionary<Guid, Dictionary<string, JobAlloc>> AllocCache = [];
    /// 外接副屏（没插也照样存在，只是不发）。由界面层创建并启动。
    public DisplayLink? DisplayLink { get; set; }
    /// 手机推送（企业微信）。
    public PhonePush PhonePush { get; }

    /// 状态变化（快照、提示、登录中）。界面据此重绘。
    public event Action? Changed;
    /// 每条提醒（静音的除外）。界面据此发系统通知。
    public event Action<IslandEvent>? EventEmitted;
    /// 播放提示音：kind = success / failure / warning。
    public Action<string, double>? PlaySound;

    readonly Dictionary<Guid, CancellationTokenSource> _loops = [];
    readonly Dictionary<Guid, HostConfig> _runningConfig = [];
    readonly Dictionary<Guid, Parsers.CPUSample> _cpuSamples = [];
    readonly Dictionary<Guid, int> _failures = [];
    readonly Queue<IslandEvent> _eventQueue = new();
    CancellationTokenSource? _eventDismiss;
    readonly Dictionary<Guid, HashSet<string>> _diskAlerted = [];
    /// 上次查磁盘的时间。磁盘占用变化慢，自动轮询时隔 DiskInterval 才查一次；手动刷新时总是查。
    readonly Dictionary<Guid, DateTime> _diskCheckedAt = [];
    public static readonly TimeSpan DiskInterval = TimeSpan.FromSeconds(300);
    /// 计算软件识别的上次结果（主机 → 目录 → 进度，带文件签名）。文件没变时服务器只回 `same=1`，这里沿用。
    /// 自定义规则改了以后整个清掉。
    readonly Dictionary<Guid, Dictionary<string, AppProgress>> _appCache = [];
    string _appCacheRules = "";
    readonly SynchronizationContext? _context;

    /// 数组作业的累计进度（key: 主机|主编号）。
    public sealed class ArrayTrack
    {
        public int Done, Failed, Total;
        public bool Initialized, Accurate, AlertedFailure;
    }
    public readonly Dictionary<string, ArrayTrack> ArrayTracks = [];
    sealed class AppRateState { public double Current; public DateTime At; public double? Rate; }
    readonly Dictionary<string, AppRateState> _appRates = [];
    /// 详情页曲线的增量缓存（见 AppCurves）。
    public readonly Dictionary<string, CurveCache> CurveCaches = [];
    /// 目录过滤用：每台服务器上各任务的工作目录。
    sealed class WorkdirCache { public string? Home; public Dictionary<string, string> Dirs = []; }
    readonly Dictionary<Guid, WorkdirCache> _workdirs = [];

    /// 服务器说文件没变（`same=1`）的目录换成上次的结果，再记下这一轮带签名的结果。
    Dictionary<string, AppProgress> ResolveApps(Guid id, Dictionary<string, AppProgress>? parsed)
    {
        if (parsed == null) { _appCache.Remove(id); return []; }
        var old = _appCache.GetValueOrDefault(id) ?? [];
        var outMap = new Dictionary<string, AppProgress>();
        var cache = new Dictionary<string, AppProgress>();
        foreach (var (jobID, p) in parsed)
        {
            var app = p.Unchanged ? old.GetValueOrDefault(p.Dir) : p;
            if (app == null) continue;
            outMap[jobID] = app;
            if (app.Signature != null) cache[app.Dir] = app;
        }
        _appCache[id] = cache;
        return outMap;
    }

    /// 工作目录和目录过滤。工作目录按任务号缓存：某一次没查到（网络抖动、刚提交的作业）时沿用上次的结果，
    /// 不会让任务忽隐忽现、误发"已结束"的提醒。设了目录过滤时，查不到目录的任务不显示。
    List<RemoteJob> ApplyWorkdirs(HostConfig host, List<RemoteJob> jobs, RemoteScript.Section? section)
    {
        var cache = _workdirs.GetValueOrDefault(host.Id) ?? new WorkdirCache();
        if (section != null)
        {
            var (home, dirs) = Parsers.Workdirs(section.Lines);
            if (home != null) cache.Home = home;
            foreach (var (k, v) in dirs) cache.Dirs[k] = v;
        }
        // 只保留当前还在列表里的，缓存不会越积越大。
        var ids = jobs.Select(j => j.Id).ToHashSet();
        cache.Dirs = cache.Dirs.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        _workdirs[host.Id] = cache;
        foreach (var j in jobs) j.Workdir ??= cache.Dirs.GetValueOrDefault(j.Id);
        var prefixes = host.PathPrefixes;
        if (prefixes.Count == 0) return jobs;
        return jobs.Where(j => j.Source == JobSource.ProgressFile || j.Watched
                               || (cache.Dirs.TryGetValue(j.Id, out var d) && Parsers.PathMatches(d, prefixes, cache.Home))).ToList();
    }
    public readonly Dictionary<Guid, (List<PartitionStat> Stats, DateTime At)> PartitionCache = [];
    /// 不支持 sacct 历史查询的主机（本次运行内不再尝试）。
    public readonly HashSet<Guid> SacctUnsupported = [];

    public string ArrayKey(Guid host, string bas) => host.ToString() + "|" + bas;

    public MonitorEngine(ConfigStore store, bool demo = false, ISshTransport? ssh = null, HistoryStore? history = null)
    {
        Store = store;
        Demo = demo;
        Ssh = ssh ?? SshCommon.Create(store.Settings.SshBackend);
        History = history ?? new HistoryStore(persist: !demo && store.Persist);
        Costs = new CostLedger(persist: !demo && store.Persist);
        PhonePush = new PhonePush(store, demo);
        _context = SynchronizationContext.Current;
        Store.Changed += Sync;
    }

    void NotifyChanged() => Changed?.Invoke();

    /// SSH 实现改变后重建连接。
    public void SetSshBackend(SshBackend backend)
    {
        var resolved = SshCommon.Resolve(backend);
        if ((resolved == SshBackend.Managed) == (Ssh is ManagedSshTransport)) return;
        var old = Ssh;
        Ssh = SshCommon.Create(backend);
        foreach (var h in Store.Settings.Hosts) _ = old.CloseAsync(h);
        RefreshAll();
    }

    Task<SshResult> Run(HostConfig host, string script, double timeout = 30) =>
        Ssh.RunAsync(host, host.Commands.Shell, script, TimeSpan.FromSeconds(timeout));

    // MARK: - 生命周期

    public void Start()
    {
        SyncNow();
        _ = WakeWatcher();
    }

    /// 没有跨平台的"系统即将睡眠 / 已唤醒"通知：每 10 秒看一次时钟，跳变超过 30 秒就当作刚唤醒。
    async Task WakeWatcher()
    {
        var last = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            var now = DateTime.UtcNow;
            if ((now - last).TotalSeconds > 30)
            {
                DebugLog.Log("系统已唤醒（时钟跳变）");
                // 网络恢复需要一点时间。
                await Task.Delay(TimeSpan.FromSeconds(4));
                foreach (var host in Store.Settings.Hosts.Where(h => h.Enabled && AutoPolls(h))) Restart(host, pollNow: true);
            }
            last = DateTime.UtcNow;
        }
    }

    CancellationTokenSource? _syncDebounce;

    /// 配置变化后调用（带防抖，避免在设置里打字时反复连接）：只重启有改动的主机。
    public async void Sync()
    {
        _syncDebounce?.Cancel();
        var cts = _syncDebounce = new CancellationTokenSource();
        try { await Task.Delay(_loops.Count == 0 ? 0 : 1200, cts.Token); }
        catch (OperationCanceledException) { return; }
        SyncNow();
    }

    bool? _lastAutoRefresh;
    int? _lastDefaultInterval;

    /// 这台机器是否自动轮询（全局"仅手动刷新"或单台"暂停"时为 false）。
    public bool AutoPolls(HostConfig host) => Store.Settings.AutoRefresh && !host.Paused;

    static string Normalized(HostConfig h)
    {
        var c = h.Clone();
        c.DisplayName = ""; c.Muted = false; c.AllowCancel = false; c.Paused = false; c.RememberPassword = false;
        return c.Fingerprint();
    }

    /// 运行中各机器的跳板机链（跳板机的连接设置变了，经它跳转的机器也要重连）。
    readonly Dictionary<Guid, string> _runningChain = [];

    string ChainKey(HostConfig h)
    {
        var all = Store.Settings.Hosts.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        return string.Join(";", Core.Ssh.SshHosts.Chain(h, all).Select(j =>
            string.Join("|", j.Id, j.Alias, j.HostName, j.User, j.Port, j.IdentityFile, j.JumpHostID, j.Auth)));
    }

    public void SyncNow()
    {
        var hosts = Store.Settings.Hosts.Where(h => h.Enabled).ToList();
        var ids = hosts.Select(h => h.Id).ToHashSet();
        foreach (var id in _loops.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _loops[id].Cancel();
            _loops.Remove(id);
            _runningConfig.Remove(id);
            Snapshots.Remove(id);
        }
        foreach (var id in _runningConfig.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _runningConfig.Remove(id);
            Snapshots.Remove(id);
        }
        // 全局开关 / 默认间隔变化：重新安排所有机器（打开自动刷新时立刻刷一次）。
        bool globalChanged = _lastAutoRefresh != null
            && (_lastAutoRefresh != Store.Settings.AutoRefresh || _lastDefaultInterval != Store.Settings.DefaultInterval);
        bool turnedOn = _lastAutoRefresh == false && Store.Settings.AutoRefresh;
        _lastAutoRefresh = Store.Settings.AutoRefresh;
        _lastDefaultInterval = Store.Settings.DefaultInterval;

        foreach (var host in hosts)
        {
            // 首次启动：暂停的机器完全不连接，直到用户手动刷新。
            if (!_runningConfig.TryGetValue(host.Id, out var old)) { Restart(host, pollNow: !host.Paused); continue; }
            // 只改了名称 / 静音 / 取消权限时不需要重新连接。
            var chain = ChainKey(host);
            bool chainChanged = _runningChain.TryGetValue(host.Id, out var oldChain) && oldChain != chain;
            _runningChain[host.Id] = chain;
            if (Normalized(old) != Normalized(host) || chainChanged) Restart(host, pollNow: true);
            else if (old.Paused != host.Paused) Restart(host, pollNow: !host.Paused);   // 暂停：停止循环；恢复：立刻刷新一次并继续。
            else if (globalChanged) Restart(host, pollNow: turnedOn);
            else _runningConfig[host.Id] = host.Clone();
        }
        NotifyChanged();
    }

    /// 刷新全部（不包括已暂停的机器；暂停的机器只能在它自己的卡片上手动刷新）。
    public void RefreshAll()
    {
        foreach (var host in Store.Settings.Hosts.Where(h => h.Enabled && !h.Paused)) Restart(host, pollNow: true);
    }

    /// 手动刷新（暂停或"仅手动刷新"时也可以用）。
    public void Refresh(Guid id)
    {
        if (Store.Host(id) is { Enabled: true } host) Restart(host, pollNow: true);
    }

    public void SetPaused(Guid id, bool paused)
    {
        if (Store.Host(id) is not { } h) return;
        h.Paused = paused;
        DebugLog.Log($"[{h.DisplayName}] {(paused ? "暂停" : "恢复")}自动刷新");
        Store.Touch();
        SyncNow();
    }

    public void SetHostInterval(Guid id, int? seconds)
    {
        if (Store.Host(id) is not { } h) return;
        h.Interval = seconds;
        Store.Touch();
        SyncNow();
    }

    public void SetGlobalInterval(int seconds)
    {
        Store.Settings.DefaultInterval = seconds;
        Store.Settings.AutoRefresh = true;
        Store.Touch();
        SyncNow();
    }

    public void SetAutoRefresh(bool on)
    {
        Store.Settings.AutoRefresh = on;
        DebugLog.Log(on ? "开启自动刷新" : "切换为仅手动刷新");
        Store.Touch();
        SyncNow();
    }

    void Restart(HostConfig host, bool pollNow)
    {
        if (_loops.Remove(host.Id, out var old)) old.Cancel();
        var cfg = host.Clone();
        _runningConfig[host.Id] = cfg;
        _failures[host.Id] = 0;
        _diskCheckedAt.Remove(host.Id);
        if (!Snapshots.ContainsKey(host.Id)) Snapshots[host.Id] = new HostSnapshot();
        bool auto = AutoPolls(cfg);
        if (!pollNow && !auto)
        {
            DebugLog.Log($"[{host.DisplayName}] " + (host.Paused ? "已暂停自动刷新" : "全局为仅手动刷新"));
            return;
        }
        DebugLog.Log($"[{host.DisplayName}] " + (auto ? $"启动轮询（间隔 {Store.Interval(cfg)}s）" : "手动刷新一次"));
        var cts = new CancellationTokenSource();
        _loops[host.Id] = cts;
        _ = Loop(host.Id, pollNow, cts.Token);
    }

    async Task Loop(Guid id, bool pollNow, CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            if (!first || pollNow) await PollOnce(id, ct);
            first = false;
            // 暂停 / 仅手动刷新：刷完这一次就停。
            if (!_runningConfig.TryGetValue(id, out var h) || !AutoPolls(h) || ct.IsCancellationRequested) return;
            try { await Task.Delay(TimeSpan.FromSeconds(NextDelay(id)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// 连续失败时指数退避（60s → 2m → 4m … 最多 15m），避免触发服务器的 fail2ban。
    double NextDelay(Guid id)
    {
        if (!_runningConfig.TryGetValue(id, out var host)) return 60;
        if (Demo && DemoData.IsDemoHost(host)) return 2;
        double b = Store.Interval(host);
        int n = _failures.GetValueOrDefault(id);
        if (n <= 0) return b;
        return Math.Min(b * Math.Pow(2, n - 1), 900);
    }

    // MARK: - 轮询

    async Task PollOnce(Guid id, CancellationToken ct = default)
    {
        if (!_runningConfig.TryGetValue(id, out var host)) return;
        if (Demo && DemoData.IsDemoHost(host)) { ApplyDemo(host); return; }
        // 渲染预览图时不连接任何真实服务器（虚构的主机用 SetSnapshot 给定状态）。
        if (NoNetwork) return;
        if (Snapshots.TryGetValue(id, out var cur)) { cur.IsRefreshing = true; NotifyChanged(); }
        var rules = System.Text.Json.JsonSerializer.Serialize(ProgressRule.Active, Json.Options);
        if (rules != _appCacheRules) { _appCache.Clear(); _appCacheRules = rules; }
        var signatures = (_appCache.GetValueOrDefault(id) ?? []).Where(kv => kv.Value.Signature != null)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Signature!);
        var withDisk = DateTime.Now - _diskCheckedAt.GetValueOrDefault(id, DateTime.MinValue) >= DiskInterval;
        var result = await Run(host, RemoteScript.Build(host, signatures, withDisk, AllocQuery(host)));
        if (ct.IsCancellationRequested)
        {
            if (Snapshots.TryGetValue(id, out var s0)) { s0.IsRefreshing = false; NotifyChanged(); }
            return;
        }

        var snap = (Snapshots.GetValueOrDefault(id) ?? new HostSnapshot()).Copy();
        snap.IsRefreshing = false;

        if (SshCommon.Classify(result, host) is { } problem)
        {
            _failures[id] = _failures.GetValueOrDefault(id) + 1;
            snap.Health = problem;
            Snapshots[id] = snap;
            NotifyChanged();
            // 记住了密码的机器（含跳板机）断线后自动重新登录：先远端的跳板机，再自己。
            if (problem.Kind == HealthKind.NeedsLogin)
                foreach (var h in Enumerable.Reverse(Core.Ssh.SshHosts.JumpChain(host)).Append(host)) AutoRelogin(h);
            DebugLog.Log($"[{host.DisplayName}] 连接失败（第 {_failures[id]} 次）：{problem}  stderr={result.Stderr.Prefix(200)}");
            return;
        }

        var parsed = RemoteScript.Parse(result.Stdout);
        if (!parsed.SawBegin)
        {
            _failures[id] = _failures.GetValueOrDefault(id) + 1;
            var err = (result.Stderr + result.Stdout).Trim();
            snap.Health = HostHealth.Error(err.Length == 0 ? L("远端 shell 没有输出（检查“远端 shell”设置）", "No output from the remote shell (check Remote Shell)") : err.Prefix(120));
            Snapshots[id] = snap;
            NotifyChanged();
            return;
        }

        _failures[id] = 0;
        await Ingest(host, parsed, result.Duration);
    }

    /// 处理一次成功的采样：更新状态、识别事件、记下运行中的任务。自检直接用样例输出调用它。
    public async Task Ingest(HostConfig host, RemoteScript.Parsed parsed, double latency)
    {
        var id = host.Id;
        var snap = (Snapshots.GetValueOrDefault(id) ?? new HostSnapshot()).Copy();
        snap.IsRefreshing = false;
        var previous = snap;
        snap = Build(host, parsed, previous);
        var listed = JobsListed(host, parsed);
        snap.Jobs = JobsAfterPoll(snap.Jobs, previous.Jobs, listed);
        snap.Latency = latency;
        snap.LastUpdate = DateTime.Now;
        snap.Health = HostHealth.Ok;
        Snapshots[id] = snap;
        NotifyChanged();
        DebugLog.Log($"[{host.DisplayName}] 轮询成功 {latency:0.00}s：运行 {snap.RunningCount} 排队 {snap.PendingCount}"
                     + (snap.Jobs.Count == 0 ? "" : " · " + string.Join(" ", snap.Jobs.Take(8).Select(j => $"{j.Id}({j.Name.Prefix(24)},{(int)(j.Elapsed ?? -1)}s)"))));
        await DetectEvents(host, previous, snap);
        // 关注的任务结束后自动取消关注（在提醒发出之后；列表没读全时不动）
        if (listed) PruneStarred(id, snap.Jobs.Select(j => j.Id).ToHashSet());
        // 任务列表读全了才更新"上次看到的任务"（squeue 出错时列表是空的，不能当成全部结束）
        if (host.WatchJobs && !Demo && listed)
        {
            if (previous.LastUpdate == null) await RecordEndedWhileAway(host, snap);
            History.SetActive(id, snap.Jobs);
        }
        if (listed && AllocCache.TryGetValue(id, out var cache))
        {
            var ids = snap.Jobs.SelectMany(j => new[] { j.Id }.Concat(j.Array?.Running.Select(e => e.Id) ?? [])).ToHashSet();
            foreach (var k in cache.Keys.Where(k => !ids.Contains(k)).ToList()) cache.Remove(k);
        }
        await InitializeArrays(host);
    }

    /// 这次采样后的任务列表：没读全时（squeue / qstat / 进程列表出错）沿用上一份，
    /// 否则会把所有任务当成已经结束、弹出结束提醒。出错的原因照常显示在警告里。
    public static List<RemoteJob> JobsAfterPoll(List<RemoteJob> built, List<RemoteJob> previous, bool listed) => listed ? built : [.. previous];

    /// 这次采样的任务列表是否完整：远端脚本里列任务的段（作业队列 / 进程列表 / 手动监听的进程）都有输出且成功了。
    /// 段的条件和 RemoteScript.Build 一致；关闭了"监控任务"时没有这些段，进度文件照常更新。
    public static bool JobsListed(HostConfig host, RemoteScript.Parsed parsed)
    {
        var names = new List<string>();
        if (host.WatchJobs)
        {
            names.Add(host.Kind switch { HostKind.Slurm => "squeue", HostKind.Pbs => "qstat", _ => "procs" });
            if (host.Kind.IsScheduler() && host.SlurmAlsoProcesses) names.Add("procs");
        }
        if (host.Kind == HostKind.Direct && host.Watched.Any(w => int.TryParse(w.Pid, out _))) names.Add("watch");
        return names.All(n => parsed.Get(n)?.ExitCode == 0);
    }

    /// 启动后第一次连上服务器：上次退出时还在运行 / 排队、现在不在了的任务，补记到历史里（不弹提醒）。
    /// Slurm 作业用 sacct 查结果；查不到时记成"已结束"（不知道成功与否），结束时间按上次看到它的时间。
    async Task RecordEndedWhileAway(HostConfig host, HostSnapshot now)
    {
        var current = now.Jobs.Select(j => j.Id).ToHashSet();
        var gone = History.ActiveJobs(host.Id).Where(j => !current.Contains(j.Id)).ToList();
        if (gone.Count == 0) return;
        var finals = new Dictionary<string, SacctFinal>();
        var allocs = new Dictionary<string, JobAlloc>();
        var slurmIds = gone.Where(j => j.Slurm && !j.Id.Contains('[')).Select(j => j.Id).ToList();
        if (slurmIds.Count > 0 && !SacctUnsupported.Contains(host.Id))
        {
            var r = await Run(host, RemoteScript.SacctScript(host, slurmIds), 20);
            if (RemoteScript.Parse(r.Stdout).Get("sacct") is { } sec)
            {
                finals = Parsers.Sacct(sec.Lines);
                allocs = Parsers.SacctAllocs(sec.Lines);
            }
        }
        RecordEndedWhileAway(host, gone, finals, allocs);
    }

    /// 查询结果与历史写入分开，便于用本地样例验证重启后的补记，不连接服务器。
    internal void RecordEndedWhileAway(HostConfig host, List<HistoryStore.ActiveJob> gone, Dictionary<string, SacctFinal> finals,
                                       Dictionary<string, JobAlloc>? allocs = null)
    {
        foreach (var j in Enumerable.Reverse(gone))
        {
            var f = finals.GetValueOrDefault(j.Id);
            // 数组的主编号在 sacct 里没有单独一行，按元素统计
            var elems = finals.Where(kv => kv.Key.StartsWith(j.Id + "_", StringComparison.Ordinal) && !kv.Key.Contains('['))
                .Select(kv => kv.Value).ToList();
            var state = f == null ? HistoryEntry.EndedUnknown : f.State.IsActive() ? HistoryEntry.EndedUnknown : f.Raw;
            var note = L("ServerIsland 未运行时结束", "ended while ServerIsland wasn't running");
            if (f == null && (j.ArrayTotal != null || elems.Count > 0))
            {
                var c = ArrayCounts(elems.Select(e => e.State), j.ArrayTotal ?? 0);
                // 旧记录没有总数时无法确认 sacct 是否漏了元素，不能仅凭返回的几条记录判成功。
                bool accurate = (j.ArrayTotal ?? 0) > 0 && c.Unknown == 0;
                state = ArrayOutcome(c.Failed, accurate).State;
                note = L("数组 ", "array ") + $"{c.Done}✓ {c.Failed}✗ · "
                    + (accurate ? "" : L("结果未确认 · ", "results unconfirmed · ")) + note;
            }
            History.Record(host.Id, new HistoryEntry
            {
                Id = j.Id, Name = j.Name, State = state, Elapsed = f?.Elapsed ?? j.Elapsed, EndedAt = j.SeenAt,
                ExitCode = f?.Exit, Note = note, Dir = j.Dir,
            });
            // 数组只在运行时按元素记费用，这里不补
            if (j.Slurm && j.ArrayTotal == null && elems.Count == 0)
                RecordCost(host, j.Id, j.Name, j.Partition, JobAlloc.Best(allocs?.GetValueOrDefault(j.Id), j.Alloc), j.Nodes, f?.Elapsed ?? j.Elapsed, endedAt: j.SeenAt);
        }
        DebugLog.Log($"[{host.DisplayName}] 补记 ServerIsland 未运行时结束的任务：{string.Join(" ", gone.Select(j => j.Id))}");
    }

    HostSnapshot Build(HostConfig host, RemoteScript.Parsed parsed, HostSnapshot? previous = null)
    {
        var snap = new HostSnapshot();
        var warnings = new List<string>();

        RemoteScript.Section? Check(string name, string label)
        {
            if (parsed.Get(name) is not { } sec) return null;
            if (sec.ExitCode is { } rc && rc != 0)
            {
                var msg = sec.Lines.LastOrDefault(l => l.Length > 0) ?? L($"退出码 {rc}", $"exit code {rc}");
                warnings.Add($"{label}：{msg.Prefix(90)}");
            }
            return sec;
        }

        if (Check("cpu", "CPU") is { } cpuSec)
        {
            var r = Parsers.Cpu(cpuSec.Lines);
            snap.Metrics.Cores = r.Cores;
            snap.Metrics.Load1 = r.Load1;
            if (r.Sample is { } s)
            {
                if (_cpuSamples.TryGetValue(host.Id, out var prev) && s.Total > prev.Total)
                    snap.Metrics.CpuPercent = (s.Busy - prev.Busy) / (s.Total - prev.Total) * 100;
                else if (r.Load1 is { } l && r.Cores is { } c && c > 0)
                    snap.Metrics.CpuPercent = Math.Min(l / c * 100, 100);
                _cpuSamples[host.Id] = s;
            }
        }
        if (Check("mem", L("内存", "Memory")) is { } memSec && Parsers.Memory(memSec.Lines) is { } m)
        {
            snap.Metrics.MemoryUsed = m.Used;
            snap.Metrics.MemoryTotal = m.Total;
        }
        if (host.ShowGPU && Check("gpu", "GPU") is { NotAvailable: false } gpuSec) snap.Gpus = Parsers.Gpus(gpuSec.Lines);

        var apps = ResolveApps(host.Id, parsed.Get("apps") is { } appsSec ? AppDetectors.Parse(appsSec.Lines) : null);
        var jobs = new List<RemoteJob>();
        if (host.Kind.IsScheduler())
        {
            // PBS 的 qstat 输出在远端已经转换成 squeue 的格式（见 RemoteScript.PbsFunction）。
            var queue = host.Kind == HostKind.Pbs ? "qstat" : "squeue";
            if (Check(queue, queue) is { } sq) jobs = Parsers.Squeue(sq.ExitCode == 0 ? sq.Lines : []);
            // 单机 Slurm：同时列出不经过 Slurm 直接运行的进程（属于作业的进程已由作业行显示）。
            if (host.SlurmAlsoProcesses && Check("procs", L("进程", "Processes")) is { } ps)
            {
                var gpuMem = parsed.Get("gpuapps") is { NotAvailable: false } g ? Parsers.GpuApps(g.Lines) : [];
                var inJobs = (parsed.Get("slurmpids")?.Lines ?? []).Select(l => l.TrimWs()).ToHashSet();
                var jobIDs = jobs.Select(j => j.Id).ToHashSet();
                var always = apps.Keys.Where(k => !jobIDs.Contains(k)).ToHashSet();
                var procs = Parsers.Processes(ps.Lines, host.ProcessFilter, host.ProcessExclude, gpuMem, always);
                jobs.AddRange(procs.Where(p => !inJobs.Contains(p.Id)));
            }
        }
        else if (Check("procs", L("进程", "Processes")) is { } ps)
        {
            var gpuMem = parsed.Get("gpuapps") is { NotAvailable: false } g ? Parsers.GpuApps(g.Lines) : [];
            jobs = Parsers.Processes(ps.Lines, host.ProcessFilter, host.ProcessExclude, gpuMem, apps.Keys.ToHashSet());
        }

        // 手动监听的进程：不受过滤规则影响。
        if (host.Kind == HostKind.Direct && host.Watched.Count > 0 && parsed.Get("watch") is { } watchSec)
        {
            var all = Parsers.Processes(watchSec.Lines, "", "", new Dictionary<string, double>());
            foreach (var w in host.Watched)
            {
                if (all.FirstOrDefault(j => j.Id == w.Pid) is not { } p) continue;
                // PID 被复用（启动时间对不上）就当作原进程已经结束。
                if (w.StartedAt is { } started && p.Elapsed is { } e && Math.Abs((DateTime.Now.AddSeconds(-e) - started).TotalSeconds) > 30) continue;
                if (jobs.FirstOrDefault(j => j.Id == w.Pid) is { } existing) existing.Watched = true;
                else
                {
                    p.Watched = true;
                    if (w.Name.Length > 0) p.Name = w.Name;
                    jobs.Add(p);
                }
            }
        }
        // 只看指定目录：按工作目录过滤作业和进程（手动监听的进程不受影响）。
        // 工作目录：记到每个任务上（写进历史记录）；设了"只看这些目录"时再按目录过滤
        if (host.WatchJobs) jobs = ApplyWorkdirs(host, jobs, parsed.Get("workdirs"));
        if (host.ShowDisk)
        {
            if (parsed.Get("disk") is { } diskSec)
            {
                if (diskSec.ExitCode == 0) snap.Disks = Parsers.Disks(diskSec.Lines);
                _diskCheckedAt[host.Id] = DateTime.Now;
            }
            else if (previous != null) snap.Disks = previous.Disks;   // 这一轮没查磁盘
        }

        if (parsed.Get("progress") is { } progSec)
        {
            var staleCutoff = DateTime.Now.AddHours(-36);
            foreach (var entry in Parsers.Progress(progSec.Lines))
            {
                if (entry.Updated is { } u && u < staleCutoff) continue;
                if (entry.JobID is { } jid && jobs.FirstOrDefault(j => j.Id == jid) is { } job)
                {
                    job.Progress = entry.Progress;
                    job.Message = entry.Message;
                    if (job.Source == JobSource.Slurm && entry.Name != entry.Id) job.Name = entry.Name;
                }
                else
                {
                    var j = new RemoteJob("file:" + entry.Id, entry.Name,
                        entry.Failed ? JobState.Failed : entry.Done ? JobState.Completed : JobState.Running, JobSource.ProgressFile)
                    {
                        Progress = entry.Done ? 1 : entry.Progress,
                        Message = entry.Message,
                    };
                    if (entry.Updated is { } up) j.Detail = L("更新于 ", "updated ") + Format.Relative(up);
                    jobs.Add(j);
                }
            }
        }

        // 计算软件进度：合并到对应任务，用两次采样估算速度和剩余时间。
        if (apps.Count > 0)
        {
            foreach (var job in jobs)
            {
                if (!apps.TryGetValue(job.Id, out var found)) continue;
                var app = found.Clone();
                ApplyAppRate(host, job.Id, app);
                job.App = app;
                if (app.Fraction is { } f) job.Progress = f;
                job.Eta = app.Eta;
                var msg = app.Title + " · " + app.Summary;
                if (app.UpdatedAt is { } u && !app.Finished)
                {
                    var quiet = (DateTime.Now - u).TotalSeconds;
                    if (quiet > Math.Max(600, Store.Interval(host) * 5.0))
                        msg = L($"⚠︎ {Format.ShortDuration(quiet)} 没有新输出 · ", $"⚠︎ no output for {Format.ShortDuration(quiet)} · ") + msg;
                }
                job.Message = msg;
            }
        }
        if (host.Kind.IsScheduler())
        {
            if (parsed.Get("alloc") is { } allocSec)
            {
                if (!AllocCache.TryGetValue(host.Id, out var c)) AllocCache[host.Id] = c = [];
                foreach (var (k, v) in Parsers.Allocs(allocSec.Lines)) c[k] = v;
            }
            if (AllocCache.TryGetValue(host.Id, out var known))
                foreach (var j in jobs.Where(j => j.Source == JobSource.Slurm)) j.Alloc = known.GetValueOrDefault(j.Id);
            jobs = Parsers.GroupArrays(jobs);
            foreach (var j in jobs.Where(j => j.Array != null)) ApplyTrack(host.Id, j);
        }
        snap.Jobs = jobs.OrderBy(Order).ToList();
        snap.Warnings = warnings.Take(2).ToList();
        return snap;
    }

    /// 每秒推进量：与上次采样比较，指数平滑；数字倒退（续训 / 重跑）时重置。
    void ApplyAppRate(HostConfig host, string jobID, AppProgress app)
    {
        var key = host.Id + "|" + jobID + "|" + app.Dir;
        if (app.Current is not { } cur) return;
        var now = DateTime.Now;
        if (_appRates.TryGetValue(key, out var st))
        {
            if (cur < st.Current) { st.Current = cur; st.At = now; st.Rate = null; }
            else if (cur > st.Current && (now - st.At).TotalSeconds >= 2)
            {
                var r = (cur - st.Current) / (now - st.At).TotalSeconds;
                st.Rate = st.Rate is { } old ? old * 0.5 + r * 0.5 : r;
                st.Current = cur;
                st.At = now;
            }
            app.Rate = st.Rate;
        }
        else _appRates[key] = new AppRateState { Current = cur, At = now };
    }

    public void ApplyTrack(Guid host, RemoteJob job)
    {
        if (job.Array is not { } a) return;
        var key = ArrayKey(host, a.BaseID);
        if (!ArrayTracks.TryGetValue(key, out var t)) ArrayTracks[key] = t = new ArrayTrack();
        t.Total = Math.Max(t.Total, t.Done + t.Failed + a.Running.Count + a.Pending);
        a.Done = t.Done; a.Failed = t.Failed; a.Total = t.Total; a.Accurate = t.Accurate;
        job.Detail = L($"运行 {a.Running.Count} · 排队 {a.Pending}", $"{a.Running.Count} running · {a.Pending} pending")
                     + (a.Failed > 0 ? L($" · 失败 {a.Failed}", $" · {a.Failed} failed") : "");
    }

    /// 第一次看到某个数组时，用 sacct 补上 ServerIsland 启动前就结束了的元素。
    async Task InitializeArrays(HostConfig host)
    {
        if (!Snapshots.TryGetValue(host.Id, out var snap)) return;
        var bases = snap.Jobs.Where(j => j.Array != null).Select(j => j.Array!.BaseID)
            .Where(b => !(ArrayTracks.GetValueOrDefault(ArrayKey(host.Id, b))?.Initialized ?? false)).ToList();
        if (bases.Count == 0) return;
        foreach (var b in bases)
        {
            var key = ArrayKey(host.Id, b);
            if (!ArrayTracks.TryGetValue(key, out var t)) ArrayTracks[key] = t = new ArrayTrack();
            t.Initialized = true;
        }
        if (SacctUnsupported.Contains(host.Id)) return;
        var r = await Run(host, RemoteScript.SacctScript(host, bases), 25);
        if (RemoteScript.Parse(r.Stdout).Get("sacct") is not { ExitCode: 0 } sec) return;
        var finals = Parsers.Sacct(sec.Lines);
        foreach (var b in bases)
        {
            var t = ArrayTracks[ArrayKey(host.Id, b)];
            var mine = finals.Where(kv => kv.Key.StartsWith(b + "_") && !kv.Key.Contains('[')).Select(kv => kv.Value).ToList();
            t.Done = mine.Count(f => f.State == JobState.Completed);
            t.Failed = mine.Count(f => f.State is JobState.Failed or JobState.Timeout or JobState.Cancelled);
            t.Accurate = true;
        }
        RefreshTracks(host.Id);
    }

    /// 计数变化后，把最新的累计值写回当前快照。
    void RefreshTracks(Guid id)
    {
        if (!Snapshots.TryGetValue(id, out var snap)) return;
        foreach (var j in snap.Jobs.Where(j => j.Array != null)) ApplyTrack(id, j);
        NotifyChanged();
    }

    static int Order(RemoteJob j)
    {
        if (j.Watched && j.State == JobState.Running) return -1;
        return j.State switch
        {
            JobState.Running or JobState.Completing => 0,
            JobState.Pending => 1,
            _ => 2,
        };
    }

    // MARK: - 事件识别

    async Task DetectEvents(HostConfig host, HostSnapshot old, HostSnapshot nw)
    {
        // 第一次成功采样时不产生事件。
        if (old.LastUpdate == null) return;
        var batch = new List<IslandEvent>();
        try
        {
            CheckDisks(host, nw, batch);
            var newByID = new Dictionary<string, RemoteJob>();
            foreach (var j in nw.Jobs) newByID.TryAdd(j.Id, j);

            var vanished = new List<RemoteJob>();
            var arrayElementsDone = new List<(string Base, RemoteJob Job)>();
            var arraysFinished = new List<RemoteJob>();
            foreach (var job in old.Jobs.Where(j => j.State.IsActive()))
            {
                if (job.Array is { } a)
                {
                    // 数组：按元素比较；排队范围的编号变化（123_[7-99] -> 123_[8-99]）不算"消失"。
                    if (newByID.TryGetValue(job.Id, out var now) && now.Array is { } na)
                    {
                        var still = na.Running.Select(e => e.Id).ToHashSet();
                        foreach (var e in a.Running.Where(e => !still.Contains(e.Id))) arrayElementsDone.Add((a.BaseID, e));
                    }
                    else
                    {
                        foreach (var e in a.Running) arrayElementsDone.Add((a.BaseID, e));
                        arraysFinished.Add(job);
                    }
                    continue;
                }
                if (newByID.TryGetValue(job.Id, out var n2))
                {
                    if (job.State == JobState.Pending && n2.State == JobState.Running)
                    {
                        var p = new PushInfo(n2) { Elapsed = null };
                        var first = n2.Detail.Split(" · ")[0];
                        if (first.Length > 0 && !first.StartsWith('(') && first != n2.Partition) p.NodeList = first;
                        batch.Add(new IslandEvent(EventKind.Started, L($"{n2.Name} 开始运行", $"{n2.Name} started"), n2.Detail, host.DisplayName, host.Id, n2.Id, p));
                    }
                    else if (job.Source == JobSource.ProgressFile && n2.State == JobState.Completed)
                        batch.Add(new IslandEvent(EventKind.Finished, L($"{n2.Name} 已完成", $"{n2.Name} finished"), n2.Message ?? "", host.DisplayName, host.Id, n2.Id,
                            new PushInfo(n2) { Note = n2.Message }));
                    else if (job.Source == JobSource.ProgressFile && n2.State == JobState.Failed)
                        batch.Add(new IslandEvent(EventKind.Failed, L($"{n2.Name} 失败", $"{n2.Name} failed"), n2.Message ?? "", host.DisplayName, host.Id, n2.Id,
                            new PushInfo(n2) { Note = n2.Message }));
                }
                else if (job.Source == JobSource.Slurm || job.Watched || (job.Elapsed ?? 0) >= 30)
                {
                    // 很短的进程（运行不到 30 秒）来去很正常，不打扰。
                    vanished.Add(job);
                    DebugLog.Log($"[{host.DisplayName}] 任务结束：{job.Id} {job.Name}");
                }
                else DebugLog.Log($"[{host.DisplayName}] 忽略短进程结束：{job.Id} {job.Name}（{(int)(job.Elapsed ?? 0)}s）");
            }
            if (arrayElementsDone.Count > 0 || arraysFinished.Count > 0)
                await HandleArrays(host, arrayElementsDone, arraysFinished, batch);
            if (vanished.Count == 0) return;

            var slurmJobs = vanished.Where(j => j.Source == JobSource.Slurm).ToList();
            var finals = new Dictionary<string, SacctFinal>();
            var finalAllocs = new Dictionary<string, JobAlloc>();
            if (slurmJobs.Count > 0)
            {
                // 只在作业从队列中消失时调用一次 sacct（sacct 查数据库，比 squeue 更重）。
                var r = await Run(host, RemoteScript.SacctScript(host, slurmJobs.Select(j => j.Id)), 20);
                if (RemoteScript.Parse(r.Stdout).Get("sacct") is { } sec)
                {
                    finals = Parsers.Sacct(sec.Lines);
                    finalAllocs = Parsers.SacctAllocs(sec.Lines);
                }
                // 作业刚离开队列时 sacct 可能还没写入最终状态（仍是 RUNNING / COMPLETING），稍等再查一次。
                var unsettled = slurmJobs.Where(j => finals.TryGetValue(j.Id, out var f) && f.State.IsActive()).ToList();
                if (unsettled.Count > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    var r2 = await Run(host, RemoteScript.SacctScript(host, unsettled.Select(j => j.Id)), 20);
                    if (RemoteScript.Parse(r2.Stdout).Get("sacct") is { } sec2)
                    {
                        foreach (var (k, v) in Parsers.Sacct(sec2.Lines)) finals[k] = v;
                        foreach (var (k, v) in Parsers.SacctAllocs(sec2.Lines)) finalAllocs[k] = v;
                    }
                }
            }

            foreach (var job in vanished)
            {
                switch (job.Source)
                {
                    case JobSource.Slurm:
                    {
                        var f = finals.GetValueOrDefault(job.Id);
                        var alloc = JobAlloc.Best(finalAllocs.GetValueOrDefault(job.Id), job.Alloc);
                        var cost = RecordCost(host, job.Id, job.Name, job.Partition, alloc, job.Nodes, f?.Elapsed ?? job.Elapsed);
                        var p = new PushInfo(job);
                        p.SetAlloc(alloc);
                        p.Cost = cost is { } cv ? Billing.Money(cv, Store.Settings.Currency) : null;
                        if (f != null)
                        {
                            p.State = f.Raw;
                            p.ExitCode = string.IsNullOrEmpty(f.Exit) ? null : f.Exit;
                            if (f.Elapsed is { } fe) p.Elapsed = fe;
                        }
                        // 没有 sacct（集群未开启作业记录）但识别到了计算软件：用输出文件的最终状态判断成功与否。
                        if (f == null && job.App is { } app)
                        {
                            var final = app;
                            var r = await Run(host, AppDetectors.FinalStateScript(job.Id, app.Dir), 15);
                            if (AppDetectors.Parse(r.Stdout.Split('\n')).GetValueOrDefault(job.Id) is { } fresh) final = fresh;
                            bool done = final.Finished;
                            var pos = final.Current is { } c ? AppProgress.Compact(c) : "?";
                            var tot = final.Total is { } tt ? AppProgress.Compact(tt) : "?";
                            var posText = final.Total == null ? $"{pos} {final.Unit}" : $"{pos}/{tot} {final.Unit}";
                            History.Record(host.Id, new HistoryEntry
                            {
                                Id = job.Id, Name = job.Name, State = done ? "COMPLETED" : "FAILED", Elapsed = job.Elapsed, EndedAt = DateTime.Now,
                                Note = L($"按 {final.Title} 输出判断：{posText}", $"from {final.Title} output: {posText}"),
                                Dir = job.Workdir ?? app.Dir,
                            });
                            p.SetApp(final);
                            if (!done) p.AppNote = AppStopNote(final);
                            p.Note = L($"集群没有作业记录，按 {final.Title} 的输出判断", $"no job accounting; judged from {final.Title} output");
                            batch.Add(new IslandEvent(done ? EventKind.Finished : final.CanJudge ? EventKind.Failed : EventKind.Info,
                                $"{job.Name} " + (done ? L("已完成", "finished") : final.CanJudge ? L("提前结束", "ended early") : L("已离开队列", "left the queue")),
                                $"{final.Title} {posText} · #{job.Id}", host.DisplayName, host.Id, job.Id, p));
                            continue;
                        }
                        var outcome = SlurmOutcome(f);
                        string? note = f == null ? L("sacct 不可用，按离开队列记录", "no sacct; recorded when it left the queue")
                            : f.State.IsActive() ? L($"sacct 仍显示 {f.Raw}，结果未确认", $"sacct still shows {f.Raw}; result unconfirmed") : null;
                        History.Record(host.Id, new HistoryEntry
                        {
                            Id = job.Id, Name = job.Name, State = outcome.State, Elapsed = f?.Elapsed ?? job.Elapsed, EndedAt = DateTime.Now,
                            ExitCode = f?.Exit, Note = note, Dir = job.Workdir ?? job.App?.Dir,
                        });
                        var elapsed = f?.Elapsed ?? job.Elapsed;
                        var time = elapsed is { } el ? L("用时 ", "took ") + Format.Duration(el) : "";
                        if (outcome.Kind == EventKind.Failed && job.App is { } fa) p.AppNote = AppStopNote(fa);
                        if (outcome.Kind != EventKind.Failed) { p.State = null; p.ExitCode = null; }
                        switch (outcome.Kind)
                        {
                            case EventKind.Failed:
                                batch.Add(new IslandEvent(EventKind.Failed, $"{job.Name} {f?.State.Label()}",
                                    string.Join(" · ", new[] { Capitalized(f?.Raw ?? ""), time }.Where(s => s.Length > 0)), host.DisplayName, host.Id, job.Id, p));
                                break;
                            case EventKind.Finished:
                                batch.Add(new IslandEvent(EventKind.Finished, L($"{job.Name} 已完成", $"{job.Name} finished"), time, host.DisplayName, host.Id, job.Id, p));
                                break;
                            default:
                                // 只知道离开了队列，不能断定成功。
                                var why = f == null ? L("集群未开启作业记录，无法确认成功或失败", "no job accounting; result unknown")
                                    : L($"作业记录显示 {f.Raw}，无法确认成功或失败", $"accounting shows {f.Raw}; result unknown");
                                p.Note = why;
                                batch.Add(new IslandEvent(EventKind.Info, L($"{job.Name} 已离开队列", $"{job.Name} left the queue"),
                                    string.Join(" · ", new[] { why, time }.Where(s => s.Length > 0)), host.DisplayName, host.Id, job.Id, p));
                                break;
                        }
                        break;
                    }
                    case JobSource.Process:
                    {
                        History.Record(host.Id, new HistoryEntry
                        {
                            Id = job.Id, Name = job.Name, State = JobState.Completed.Raw(), Elapsed = job.Elapsed, EndedAt = DateTime.Now,
                            Note = job.Watched ? L("监听的进程", "watched process") : null,
                            Dir = job.Workdir ?? job.App?.Dir,
                        });
                        if (job.Watched) Unwatch(host.Id, job.Id);
                        var finalApp = job.App;
                        if (job.App is { } app)
                        {
                            // 最后一次采样可能还没到目标：再读一次输出文件的最终状态。
                            var r = await Run(host, AppDetectors.FinalStateScript(job.Id, app.Dir), 15);
                            if (AppDetectors.Parse(r.Stdout.Split('\n')).GetValueOrDefault(job.Id) is { } fresh) finalApp = fresh;
                            DebugLog.Log($"[{host.DisplayName}] {app.Title}结束时的最终状态：{(finalApp?.Current is { } c1 ? AppProgress.Compact(c1) : "?")}/{(finalApp?.Total is { } t1 ? AppProgress.Compact(t1) : "?")}");
                        }
                        var pp = new PushInfo(job);
                        if (finalApp != null) pp.SetApp(finalApp);
                        if (finalApp is { CanJudge: true, Current: { } c })
                        {
                            // 计算软件：没到目标 / 没有正常结束标记就退出，基本是出错或被杀。
                            bool done = finalApp.Finished;
                            var folder = Text.LastPathComponent(finalApp.Dir);
                            var pos = AppProgress.Compact(c) + (finalApp.Total is { } t ? "/" + AppProgress.Compact(t) : "") + " " + finalApp.Unit;
                            if (!done) pp.AppNote = AppStopNote(finalApp);
                            batch.Add(new IslandEvent(done ? EventKind.Finished : EventKind.Failed,
                                finalApp.Title + (done ? L("完成", " finished") : L("提前结束", " ended early")),
                                (done ? pos : L("停在 ", "stopped at ") + pos) + " · " + folder, host.DisplayName, host.Id, job.Id, pp));
                        }
                        else
                        {
                            pp.Note = L("进程已退出（看不到退出码，无法判断成功与否）", "process exited (no exit code, so success is unknown)");
                            batch.Add(new IslandEvent(EventKind.Finished, L($"{job.Name} 已结束", $"{job.Name} ended"),
                                job.Elapsed is { } e ? L("运行了至少 ", "ran for at least ") + Format.Duration(e) : "", host.DisplayName, host.Id, job.Id, pp));
                        }
                        break;
                    }
                }
            }
        }
        finally
        {
            Flush(batch, host);
        }
    }

    /// Swift 的 String.capitalized：单词只按空白分隔（"OUT_OF_MEMORY" -> "Out_of_memory"，"CANCELLED by 123" -> "Cancelled By 123"）。
    static string Capitalized(string s)
    {
        var chars = s.ToLowerInvariant().ToCharArray();
        bool start = true;
        for (int i = 0; i < chars.Length; i++)
        {
            if (char.IsWhiteSpace(chars[i])) { start = true; continue; }
            if (start) chars[i] = char.ToUpperInvariant(chars[i]);
            start = false;
        }
        return new string(chars);
    }

    async Task HandleArrays(HostConfig host, List<(string Base, RemoteJob Job)> elements, List<RemoteJob> finished, List<IslandEvent> batch)
    {
        // 一次 sacct 查完：离开运行的元素 + 已经整体结束的数组（取全部元素的最终状态）。
        var ids = elements.Select(e => e.Job.Id).Concat(finished.Where(j => j.Array != null).Select(j => j.Array!.BaseID)).Distinct().ToList();
        var finals = new Dictionary<string, SacctFinal>();
        var finalAllocs = new Dictionary<string, JobAlloc>();
        if (ids.Count > 0 && !SacctUnsupported.Contains(host.Id))
        {
            var r = await Run(host, RemoteScript.SacctScript(host, ids), 25);
            if (RemoteScript.Parse(r.Stdout).Get("sacct") is { } sec)
            {
                finals = Parsers.Sacct(sec.Lines);
                finalAllocs = Parsers.SacctAllocs(sec.Lines);
            }
        }
        foreach (var (bas, e) in elements)
            RecordCost(host, e.Id, e.Name, e.Partition, JobAlloc.Best(finalAllocs.GetValueOrDefault(e.Id), e.Alloc), e.Nodes,
                       finals.GetValueOrDefault(e.Id)?.Elapsed ?? e.Elapsed, arrayBase: bas);
        var finishedBases = finished.Where(j => j.Array != null).Select(j => j.Array!.BaseID).ToHashSet();
        foreach (var (bas, e) in elements.Where(x => !finishedBases.Contains(x.Base)))
        {
            var key = ArrayKey(host.Id, bas);
            if (!ArrayTracks.TryGetValue(key, out var t)) ArrayTracks[key] = t = new ArrayTrack();
            var st = finals.TryGetValue(e.Id, out var fe) ? fe.State : JobState.Completed;
            if (st is JobState.Failed or JobState.Timeout or JobState.Cancelled)
            {
                t.Failed += 1;
                if (!t.AlertedFailure && Store.Settings.ArrayFirstFailureAlert)
                {
                    t.AlertedFailure = true;
                    var p = new PushInfo(e) { Id = bas, State = fe?.Raw ?? st.Label(), ExitCode = string.IsNullOrEmpty(fe?.Exit) ? null : fe!.Exit };
                    p.SetAlloc(finalAllocs.GetValueOrDefault(e.Id));
                    if (fe?.Elapsed is { } fel) p.Elapsed = fel;
                    p.FailedElements = [ElementLabel(e.Id, bas)];
                    p.Note = L("其余元素还在继续；之后的失败不再单独提醒", "other elements keep running; later failures won't alert separately");
                    batch.Add(new IslandEvent(EventKind.Failed, L($"{e.Name} 数组中有元素失败", $"{e.Name}: an array element failed"),
                        $"#{e.Id} {fe?.Raw ?? st.Label()}" + L(" · 之后的失败不再单独提醒", " · later failures won't alert separately"),
                        host.DisplayName, host.Id, bas, p));
                }
            }
            else t.Done += 1;
        }
        RefreshTracks(host.Id);

        foreach (var job in finished)
        {
            if (job.Array is not { } a) continue;
            var key = ArrayKey(host.Id, a.BaseID);
            if (!ArrayTracks.TryGetValue(key, out var t)) t = new ArrayTrack();
            t.Total = Math.Max(t.Total, a.Total);
            var mineKV = finals.Where(kv => kv.Key.StartsWith(a.BaseID + "_") && !kv.Key.Contains('[')).ToList();
            var mine = mineKV.Select(kv => kv.Value).ToList();
            if (mine.Count > 0)
            {
                var c = ArrayCounts(mine.Select(f => f.State), t.Total);
                t.Done = c.Done;
                t.Failed = c.Failed;
                t.Accurate = c.Unknown == 0;
            }
            else
            {
                // 这次没查到 sacct：之前的计数可能来自 sacct，但最后这批元素的结果没确认
                t.Done += a.Running.Count;
                t.Accurate = false;
            }
            var total = Math.Max(t.Total, t.Done + t.Failed);
            var outcome = ArrayOutcome(t.Failed, t.Accurate);
            History.Record(host.Id, new HistoryEntry
            {
                Id = a.BaseID, Name = job.Name, State = outcome.State, Elapsed = job.Elapsed, EndedAt = DateTime.Now,
                Note = L("数组 ", "array ") + $"{t.Done}✓ {t.Failed}✗",
                Dir = job.Workdir,
            });
            var unconfirmed = mine.Count == 0 ? L("（sacct 不可用，结果未确认）", " (no sacct; unconfirmed)")
                                              : L("（部分元素的结果还未确认）", " (some results unconfirmed)");
            var result = L($"{t.Done} 个成功", $"{t.Done} done") + (t.Failed > 0 ? L($" · {t.Failed} 个失败", $" · {t.Failed} failed") : "")
                    + L($" / 共 {total} 个", $" / {total} total");
            var ap = new PushInfo(job) { Elapsed = null, Cpus = null, Gpus = null, Nodes = null, Array = result + (t.Accurate ? "" : unconfirmed) };
            ap.FailedElements = mineKV.Where(kv => kv.Value.State is JobState.Failed or JobState.Timeout or JobState.Cancelled)
                .Select(kv => ElementLabel(kv.Key, a.BaseID))
                .OrderBy(x => int.TryParse(x[1..], out var n) ? n : 0).ToList();
            ap.Cost = Costs.CostOf(host.Id, a.BaseID) is { } ac ? Billing.Money(ac, Store.Settings.Currency) : null;
            batch.Add(new IslandEvent(outcome.Kind,
                L($"{job.Name} 数组已全部结束", $"{job.Name}: array finished"),
                result + (t.Accurate ? "" : unconfirmed),
                host.DisplayName, host.Id, a.BaseID, ap));
            ArrayTracks.Remove(key);
        }
    }

    /// 数组元素的显示编号：12400_17 → #17。
    public static string ElementLabel(string id, string bas) => "#" + (id.StartsWith(bas + "_") ? id[(bas.Length + 1)..] : id);

    /// 计算软件没跑完时补一句：输出文件多久之前停止更新。
    public static string? AppStopNote(AppProgress app, DateTime? now = null) =>
        app.UpdatedAt is { } u ? L("输出文件最后更新于 ", "output last updated ") + Format.Relative(u, now) : null;

    /// 同一次轮询里事件太多时合并成一条，避免连续弹窗。
    /// 先按提醒设置（种类、关注范围）过滤，再合并。
    void Flush(List<IslandEvent> batch, HostConfig host)
    {
        // 手机推送有自己的范围设置（默认只推送关注的任务），先拿到完整的一批再各自过滤。
        PhonePush.Consider(batch, Store.Host(host.Id) ?? host, host.Watched.Select(w => w.Pid).ToHashSet());
        batch = Alertable(batch, Store.Settings, host.Watched.Select(w => w.Pid).ToHashSet());
        if (batch.Count == 0) return;
        if (batch.Count <= 2) { batch.ForEach(Emit); return; }
        Emit(Merged(batch, host.DisplayName, host.Id));
    }

    /// 按提醒设置过滤这一批事件，被过滤掉的写进日志（排查"为什么没提醒"）。
    /// 手动监听的进程是用户点名要看的，"只提醒关注的任务"时也照常提醒。
    public static List<IslandEvent> Alertable(List<IslandEvent> batch, AppSettings settings, HashSet<string>? watchedPids = null)
    {
        return batch.Where(e =>
        {
            if (settings.AllowsAlert(e)) return true;
            if (e.JobID is { } j && watchedPids?.Contains(j) == true && settings.AllowsAlert(e, anyScope: true)) return true;
            DebugLog.Log($"提醒按设置跳过：{e.Title}（范围 {settings.AlertScope.ToString().ToLowerInvariant()}）");
            return false;
        }).ToList();
    }

    /// 同一次轮询里的多条事件合并成一条：按种类分别计数，种类取最要紧的（失败 > 警告 > 完成 > 结果未知 > 开始运行）。
    public static IslandEvent Merged(List<IslandEvent> batch, string hostName, Guid? hostId)
    {
        int Count(EventKind k) => batch.Count(e => e.Kind == k);
        var order = new (EventKind Kind, string Text)[]
        {
            (EventKind.Finished, L($"{Count(EventKind.Finished)} 个完成", $"{Count(EventKind.Finished)} finished")),
            (EventKind.Failed, L($"{Count(EventKind.Failed)} 个失败", $"{Count(EventKind.Failed)} failed")),
            (EventKind.Info, L($"{Count(EventKind.Info)} 个结果未知", $"{Count(EventKind.Info)} result unknown")),
            (EventKind.Started, L($"{Count(EventKind.Started)} 个开始运行", $"{Count(EventKind.Started)} started")),
            (EventKind.Warning, L($"{Count(EventKind.Warning)} 条警告", $"{Count(EventKind.Warning)} warnings")),
        };
        var parts = order.Where(o => Count(o.Kind) > 0).Select(o => o.Text);
        var kind = new[] { EventKind.Failed, EventKind.Warning, EventKind.Finished, EventKind.Info, EventKind.Started }
            .Where(k => Count(k) > 0).DefaultIfEmpty(EventKind.Info).First();
        return new IslandEvent(kind, string.Join(L("，", ", "), parts),
            string.Join(" · ", batch.Take(3).Select(e => e.Title)), hostName, hostId);
    }

    /// 数组元素在 sacct 里的最终状态计数：运行中、状态不认识、以及已知总数里缺失的元素都算"未确认"。
    public static (int Done, int Failed, int Unknown) ArrayCounts(IEnumerable<JobState> states, int expectedTotal = 0)
    {
        var list = states.ToList();
        int done = list.Count(s => s == JobState.Completed);
        int failed = list.Count(s => s is JobState.Failed or JobState.Timeout or JobState.Cancelled);
        return (done, failed, Math.Max(list.Count, expectedTotal) - done - failed);
    }

    /// 整个数组结束时历史里记的状态和提醒种类：有失败就算失败；全部元素都确认完成才算成功，否则"结果未知"。
    public static (string State, EventKind Kind) ArrayOutcome(int failed, bool accurate) =>
        failed > 0 ? ("FAILED", EventKind.Failed) : accurate ? ("COMPLETED", EventKind.Finished) : (HistoryEntry.EndedUnknown, EventKind.Info);

    /// 离开队列的 Slurm 作业（没有按计算软件输出判断时）：按 sacct 结果定历史里记的状态和提醒的种类。
    /// 只有 sacct 明确是 COMPLETED 才算成功；查不到、仍显示运行中或状态不认识，都算"结果未知"（Info）。
    public static (string State, EventKind Kind) SlurmOutcome(SacctFinal? f)
    {
        if (f == null || f.State.IsActive()) return (HistoryEntry.EndedUnknown, EventKind.Info);
        return f.State switch
        {
            JobState.Completed => (f.Raw, EventKind.Finished),
            JobState.Failed or JobState.Timeout or JobState.Cancelled => (f.Raw, EventKind.Failed),
            _ => (f.Raw, EventKind.Info),
        };
    }

    public void Emit(IslandEvent ev)
    {
        // 静音的服务器：不弹提示、不发通知、不响铃。
        if (ev.HostID is { } hid && Store.Host(hid)?.Muted == true)
        {
            DebugLog.Log("提醒已静音：" + ev.Title);
            return;
        }
        // 勿扰时段：系统通知照发（不出声，见 App），不响铃、不弹窗。
        var quiet = Store.Settings.IsQuiet();
        DebugLog.Log($"提醒：{ev.Title} · {ev.Subtitle}（弹窗 {Store.Settings.IslandAlerts} 声音 {Store.Settings.SoundEnabled}{(quiet ? " · 勿扰时段" : "")}）");
        EventEmitted?.Invoke(ev);
        if (quiet) return;
        if (Store.Settings.SoundEnabled && ev.Kind != EventKind.Started)
            PlaySound?.Invoke(ev.Kind == EventKind.Failed ? "failure" : ev.Kind == EventKind.Warning ? "warning" : "success", Store.Settings.SoundVolume);
        if (!Store.Settings.IslandAlerts) return;
        _eventQueue.Enqueue(ev);
        if (CurrentEvent == null) ShowNextEvent();
    }

    async void ShowNextEvent()
    {
        if (_eventQueue.Count == 0) { CurrentEvent = null; NotifyChanged(); return; }
        CurrentEvent = _eventQueue.Dequeue();
        NotifyChanged();
        _eventDismiss?.Cancel();
        var cts = _eventDismiss = new CancellationTokenSource();
        try
        {
            await Task.Delay(5000, cts.Token);
            CurrentEvent = null;
            NotifyChanged();
            await Task.Delay(600, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        ShowNextEvent();
    }

    public void DismissEvent()
    {
        _eventDismiss?.Cancel();
        _eventQueue.Clear();
        if (CurrentEvent != null) { CurrentEvent = null; NotifyChanged(); }
    }

    void CheckDisks(HostConfig host, HostSnapshot nw, List<IslandEvent> batch)
    {
        var threshold = Store.Settings.DiskWarnThreshold;
        if (!_diskAlerted.TryGetValue(host.Id, out var alerted)) _diskAlerted[host.Id] = alerted = [];
        foreach (var d in nw.Disks)
        {
            if (d.Fraction >= threshold && alerted.Add(d.Mount))
            {
                var free = Format.Bytes((d.TotalKB - d.UsedKB) * 1024);
                batch.Add(new IslandEvent(EventKind.Warning, L($"磁盘快满了：{d.Mount}", $"Disk almost full: {d.Mount}"),
                    L($"已用 {Format.Percent(d.Fraction)} · 剩余 {free}", $"{Format.Percent(d.Fraction)} used · {free} free"),
                    host.DisplayName, host.Id));
            }
            else if (d.Fraction < threshold - 0.03) alerted.Remove(d.Mount);   // 回落后再次超过时会重新提醒
        }
    }

    /// 预览渲染用：完全不连接网络。
    public static bool NoNetwork { get; set; }

    public async Task PollForDiagnosis(HostConfig host)
    {
        _runningConfig[host.Id] = host;
        await PollOnce(host.Id);
    }

    public void SetSnapshot(HostSnapshot snap, Guid id)
    {
        Snapshots[id] = snap;
        NotifyChanged();
    }

    // MARK: - 关注

    public bool IsStarred(Guid hostId, string jobId) => Store.Settings.IsStarred(hostId, jobId);

    /// 关注 / 取消关注。数组作业用主编号（分组行的 id），关注的是整个数组。
    public void ToggleStar(Guid hostId, RemoteJob job)
    {
        var id = job.Array?.BaseID ?? job.Id;
        var list = Store.Settings.StarredJobs;
        var i = list.FindIndex(s => s.HostID == hostId && s.JobID == id);
        if (i >= 0)
        {
            list.RemoveAt(i);
            DebugLog.Log($"取消关注：{id} {job.Name}");
        }
        else
        {
            list.Add(new StarredJob { HostID = hostId, JobID = id, Name = job.Name });
            DebugLog.Log($"关注：{id} {job.Name}");
        }
        Store.Touch();
        NotifyChanged();
    }

    /// 去掉已经不在列表里的关注（任务结束了）。只在这台服务器的任务列表读全时调用。
    public void PruneStarred(Guid hostId, HashSet<string> keep)
    {
        var gone = Store.Settings.StarredJobs.Where(s => s.HostID == hostId && !keep.Contains(s.JobID)).ToList();
        if (gone.Count == 0) return;
        Store.Settings.StarredJobs.RemoveAll(s => s.HostID == hostId && !keep.Contains(s.JobID));
        Store.Touch();
        DebugLog.Log("关注的任务已结束，取消关注：" + string.Join(" ", gone.Select(g => g.JobID)));
    }

    /// 显示顺序：关注的任务排在前面，其余保持原来的顺序。
    public List<RemoteJob> DisplayJobs(Guid hostId, IEnumerable<RemoteJob> jobs) => StarredFirst(jobs, j => IsStarred(hostId, j.Id));

    public static List<RemoteJob> StarredFirst(IEnumerable<RemoteJob> jobs, Func<RemoteJob, bool> starred)
    {
        var all = jobs.ToList();
        return [.. all.Where(starred), .. all.Where(j => !starred(j))];
    }

    // MARK: - 汇总

    public List<(HostConfig Host, HostSnapshot Snap)> OrderedHosts =>
        Store.Settings.Hosts.Where(h => h.Enabled).Select(h => (h, Snapshots.GetValueOrDefault(h.Id) ?? new HostSnapshot())).ToList();

    public int TotalRunning => Snapshots.Values.Sum(s => s.RunningCount);
    public int TotalPending => Snapshots.Values.Sum(s => s.PendingCount);
    public bool HasProblem => Snapshots.Values.Any(s => s.Health.IsProblem);
    public bool HasError => Snapshots.Values.Any(s => s.Health.Kind == HealthKind.Error);
    public bool IsRefreshing => Snapshots.Values.Any(s => s.IsRefreshing);

    /// 收起状态右侧显示的"主任务"：有真实进度的优先，否则取最接近完成的运行中作业。
    public RemoteJob? LeadJob =>
        Snapshots.Values.SelectMany(s => s.Jobs).Where(j => j.State == JobState.Running && j.Fraction != null)
            .OrderBy(j => j.HasRealProgress ? 1 : 0).ThenBy(j => j.Fraction ?? 0).LastOrDefault();

    /// 根据整体状态决定宠物心情。
    public PetMood PetMood
    {
        get
        {
            if (CurrentEvent is { } e)
                return e.Kind switch { EventKind.Failed => PetMood.Sad, EventKind.Finished => PetMood.Happy, _ => PetMood.Working };
            if (HasError) return PetMood.Sad;
            if (HasProblem) return PetMood.Worried;
            if (TotalRunning > 0) return PetMood.Working;
            if (TotalPending > 0) return PetMood.Waiting;
            return PetMood.Idle;
        }
    }

    // MARK: - 诊断

    /// 设置页的"测试连接"：返回原始输出，方便核对命令是否符合预期。
    public async Task<string> Diagnose(HostConfig host)
    {
        var r = await Run(host, RemoteScript.Build(host));
        var outText = $"$ ssh {host.Alias} {host.Commands.Shell}   ({r.Duration:0.00}s, exit {r.Status}, {Ssh.Name})\n";
        if (SshCommon.Classify(r, host) is { } problem) outText += $"✗ {problem.Message}\n";
        if (r.Stderr.Length > 0) outText += "\n[stderr]\n" + r.Stderr + "\n";
        outText += "\n[stdout]\n" + r.Stdout;
        return outText;
    }

    /// 检测远端有没有 squeue / nvidia-smi，用于自动判断类型。
    public async Task<(bool Slurm, bool Pbs, bool Gpu, bool LocalSlurmd, string Info)?> Detect(HostConfig host)
    {
        var r = await Run(host, (host.Commands.Preamble.Length == 0 ? "" : host.Commands.Preamble + "\n") + RemoteScript.DetectScript, 20);
        if (r.Status != 0) return null;
        var o = r.Stdout;
        var info = o.Split('\n').LastOrDefault(l => l.Length > 0 && !l.Contains('=')) ?? "";
        bool slurm = o.Contains("squeue=yes");
        return (slurm, !slurm && o.Contains("qstat=yes") && o.Contains("qselect=yes"), o.Contains("nvidia-smi=yes"), o.Contains("slurmd=yes"), info.Trim());
    }

    // MARK: - 演示模式

    int _demoTick;

    void ApplyDemo(HostConfig host)
    {
        _demoTick++;
        Snapshots[host.Id] = DemoData.Snapshot(host, _demoTick);
        if (host.Kind.IsScheduler() && Costs.Entries.Count == 0) Costs.ReplaceAll(DemoData.CostEntries(host.Id));
        NotifyChanged();
        if (_demoTick % 30 == 0 && host.Kind.IsScheduler())
        {
            bool fail = _demoTick % 60 == 0;
            var ev = new IslandEvent(fail ? EventKind.Failed : EventKind.Finished,
                fail ? L("eval_mmlu 失败", "eval_mmlu failed") : L("pretrain_7b 已完成", "pretrain_7b finished"),
                fail ? L("OUT_OF_MEMORY · 用时 0:42:18", "OUT_OF_MEMORY · took 0:42:18") : L("用时 11:42:05", "took 11:42:05"),
                host.DisplayName, host.Id);
            // 演示模式默认不推送；SERVERISLAND_PUSH_DEMO=1 时用来试推送（见 PhonePush.Consider）。
            PhonePush.Consider([ev], host);
            Emit(ev);
        }
    }
}
