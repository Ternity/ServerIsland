using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Core;

/// 副屏的蓝牙通道（各平台各自实现：Windows 用 WinRT，见 Desktop/Platform）。收发的都是"一行一条 JSON"。
/// 回调可能在任意线程上触发；DisplayLink 会转回界面线程。
public interface IDisplayBle
{
    /// 连上、配好对、订阅了通知，可以握手了。
    Action? OnReady { get; set; }
    Action<string>? OnLine { get; set; }
    Action? OnDisconnect { get; set; }
    /// 发一条完整的消息（带换行）；没就绪时丢弃。实现要按 MTU 切块，并且只替换还没开始发送的同类快照。
    void Write(byte[] data, string? kind);
    /// 断开重连（握手超时时用）。
    void Reconnect();
    void Stop();
}

/// 外接副屏（ESP32-S3 + AMOLED，固件暂不进仓库）：把当前状态和提醒通过 USB 串口推过去（和 Mac 版 DisplayLink.swift 对应，协议相同）。
/// 只连 Espressif 的设备（USB 厂商号 0x303a），而且要先握手：发 hi，对方回 hello 才算副屏。
/// 在界面线程上创建和 Start：计时器的 await 回到界面线程，串口读到的行也 Post 回界面线程。
public sealed class DisplayLink
{
    public const int EspressifVendorID = 0x303a;

    readonly MonitorEngine _engine;
    readonly ConfigStore _store;
    SynchronizationContext? _ui;
    SerialLink? _port;
    bool _usbReady;
    IDisplayBle? _ble;
    bool _bleReady;
    int _bleHandshake;
    /// 现在用哪条通道：USB 优先，没有 USB 时用蓝牙。
    public enum Transport { Usb, Ble }
    public Transport? Current { get; private set; }
    /// 平台的蓝牙实现（没有就是不支持蓝牙）。在界面线程上调用。
    public Func<IDisplayBle?>? BleFactory;
    public bool BluetoothSupported => BleFactory != null;
    bool _started;
    /// 不是副屏固件（握手没回应）的设备，本次运行内不再尝试。
    readonly HashSet<string> _rejected = [];
    int _handshake;
    int _snapVersion;
    string _extrasKey = "";
    bool _lastLockSync;
    bool _locked;

    /// 电脑是否锁屏（由界面层按平台提供；没有就当作没锁）。在后台线程上调用。
    public Func<bool>? IsLocked;
    /// 连接状态、板子设置、电量、配网状态变化时（设置页据此刷新）。在界面线程上触发。
    public event Action? Changed;

    public bool Connected { get; private set; }
    public string? Firmware { get; private set; }
    /// 板子上的设置（板子发来的 settings 消息，布尔值也转成 0 / 1）。
    public Dictionary<string, int> Board { get; private set; } = [];
    /// 配网状态（只来自 USB；不含 Wi-Fi 名称和密码）。
    public Dictionary<string, string> Network { get; private set; } = [];
    int? _networkClients;
    /// 板子的电量（固件 0.4.1 起才有）：百分比（-1 = 没有电池读数）、是否在充电、是否插着电。
    public sealed record BatteryInfo(int Percent, bool Charging, bool Usb);
    public BatteryInfo? Battery { get; private set; }

    /// AI 编程工具的会话（Claude Code / Codex / OpenCode）。
    public AgentMonitor Agents { get; } = new();
    /// 截止日期、天气。
    public DisplayExtras Extras { get; } = new();

    public DisplayLink(MonitorEngine engine, ConfigStore store)
    {
        _engine = engine;
        _store = store;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ui = SynchronizationContext.Current;
        _engine.Changed += ScheduleSnapshot;
        _engine.EventEmitted += Send;
        _store.Changed += SettingsChanged;
        _extrasKey = ExtrasKey(_store.Settings);
        _lastLockSync = _store.Settings.DisplayLockSync;
        WatchAgentEvents();
        // 插拔检测用定时扫描（每 3 秒一次）；状态一变就推，另外每 10 秒推一次当心跳，副屏 35 秒收不到就显示断开。
        _ = Every(TimeSpan.FromSeconds(3), Scan);
        _ = Every(TimeSpan.FromSeconds(10), SendSnapshot);
        // 用量只供参考，三分钟读一次。
        _ = Every(TimeSpan.FromSeconds(180), SendAIUsage);
        // Agents 列表（里面有"多久之前"）和任务历史：半分钟发一次；截止日期和天气 5 分钟一次（天气自己有 30 分钟的缓存）。
        var tick = 0;
        _ = Every(TimeSpan.FromSeconds(30), () =>
        {
            SendAgents();
            SendHistory();
            if (++tick % 10 == 0) SendExtras(false);
        });
        _ = Every(TimeSpan.FromSeconds(5), PollAgents);
        _ = Every(TimeSpan.FromSeconds(3), PollLock);
        Scan();
    }

    async Task Every(TimeSpan interval, Action action)
    {
        while (true)
        {
            await Task.Delay(interval);
            try { action(); } catch (Exception e) { DebugLog.Log("副屏：" + e.Message); }
        }
    }

    void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null);
        else a();
    }

    /// 钩子写事件文件时马上读一次（不用等 5 秒的轮询）。
    void WatchAgentEvents()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ConfigDirectory);
            var w = new FileSystemWatcher(AppPaths.ConfigDirectory, Path.GetFileName(AgentEvents.FilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            w.Changed += (_, _) => Post(PollAgents);
            w.Created += (_, _) => Post(PollAgents);
            w.EnableRaisingEvents = true;
            _watcher = w;
        }
        catch { }
    }
    FileSystemWatcher? _watcher;

    /// Agent 状态只给副屏用：连着才读；刚连上时先安静地读一遍（不把以前的提醒再发一遍）。
    /// 副屏连着，或手机推送要 Agent 提醒（副屏没连也要读）。
    bool AgentsWanted => Connected || _store.Settings.PhonePush.WantsAgents;

    async void PollAgents()
    {
        if (!AgentsWanted) { _agentsLive = false; _agentList = []; return; }
        if (_agentsReading) return;
        _agentsReading = true;
        var generation = _agentsGeneration;
        var silent = !_agentsLive;
        try
        {
            var result = await Task.Run(() =>
            {
                var alerts = new List<AgentSession>();
                Agents.OnAlert = alerts.Add;
                try
                {
                    if (silent) Agents.LoadInitial(); else Agents.Poll();
                    return (List: Agents.List, Alerts: alerts);
                }
                finally { Agents.OnAlert = null; }
            });
            if (AgentsWanted && generation == _agentsGeneration)
            {
                _agentsLive = true;
                var changed = !_agentList.SequenceEqual(result.List);
                _agentList = result.List;
                if (silent || changed) SendAgents();
                foreach (var alert in result.Alerts)
                {
                    SendAgentAlert(alert);
                    _engine.PhonePush.Agent(alert);
                }
            }
        }
        catch (Exception e) { DebugLog.Log("Agent 状态：" + e.Message); }
        finally
        {
            _agentsReading = false;
            if (AgentsWanted && generation != _agentsGeneration) PollAgents();
        }
    }
    bool _agentsLive;
    bool _agentsReading;
    int _agentsGeneration;
    List<AgentSession> _agentList = [];

    /// 引擎发出的提醒（静音的服务器在那之前就被过滤掉了）。
    void Send(IslandEvent ev)
    {
        if (!Connected) return;
        var kind = ev.Kind switch
        {
            EventKind.Finished => "finished",
            EventKind.Failed => "failed",
            EventKind.Started => "started",
            EventKind.Warning => "warning",
            _ => "info",
        };
        Write(new JsonObject { ["t"] = "event", ["kind"] = kind, ["title"] = ev.Title, ["sub"] = ev.Subtitle, ["host"] = ev.HostName });
    }

    // —— 连接 ——

    void Scan()
    {
        UpdateBluetooth();
        if (!_store.Settings.UsbDisplay)
        {
            if (_port != null) Disconnect("设置里关闭了副屏");
            return;
        }
        var devices = EspressifPorts().Where(p => !_rejected.Contains(p)).ToList();
        if (_port is { } p)
        {
            if (!devices.Contains(p.Path) && !_rejected.Contains(p.Path)) Disconnect("副屏已拔出");
            return;
        }
        if (devices.FirstOrDefault() is not { } path) return;
        var link = SerialLink.Open(path, line => Post(() => Handle(line, Transport.Usb)), () => Post(() => Disconnect("串口断开")));
        if (link == null)
        {
            DebugLog.Log("副屏：打不开 " + path);
            _rejected.Add(path);
            return;
        }
        DebugLog.Log($"副屏：发现 {path}，握手中");
        _port = link;
        _ = Handshake(link, ++_handshake);
    }

    /// 刚打开时最先写入的字节可能丢失，所以每 0.5 秒重发一次 hi，3 秒内没有 hello 就放弃这台设备。
    async Task Handshake(SerialLink link, int id)
    {
        for (var tries = 0; tries <= 6; tries++)
        {
            if (_port != link || id != _handshake || _usbReady) return;
            link.Write(Encode(new JsonObject { ["t"] = "hi" }));
            await Task.Delay(500);
        }
        if (_port != link || _usbReady) return;
        DebugLog.Log($"副屏：{link.Path} 没有回应握手，不是 ServerIsland 副屏固件");
        _rejected.Add(link.Path);
        Disconnect("握手超时");
    }

    void Disconnect(string reason)
    {
        if (_port != null && Connected) DebugLog.Log($"副屏：USB 断开（{reason}）");
        _handshake++;
        _port?.Close();
        _port = null;
        _usbReady = false;
        Network = [];  // 配网状态只来自 USB；改走蓝牙时别显示旧状态
        RefreshTransport();
        Changed?.Invoke();
    }

    /// 蓝牙：设置里打开了、平台支持才创建（会用到系统的蓝牙），关了就断开。
    void UpdateBluetooth()
    {
        var want = _store.Settings.UsbDisplay && _store.Settings.DisplayBluetooth && BleFactory != null;
        if (want && _ble == null)
        {
            var b = BleFactory!();
            if (b == null) return;
            b.OnReady = () => Post(() =>
            {
                if (_ble != b) return;
                DebugLog.Log("副屏蓝牙：通道就绪，握手中");
                _ = BleHandshake(b, ++_bleHandshake);
            });
            b.OnLine = line => Post(() => { if (_ble == b) Handle(line, Transport.Ble); });
            b.OnDisconnect = () => Post(() =>
            {
                if (_ble != b) return;
                _bleHandshake++;
                _bleReady = false;
                RefreshTransport();
            });
            _ble = b;
        }
        else if (!want && _ble != null)
        {
            _bleHandshake++;
            _ble.Stop();
            _ble = null;
            _bleReady = false;
            RefreshTransport();
        }
    }

    /// 第一次连接要在电脑上输入配对码，给足两分钟；每秒发一次 hi。
    async Task BleHandshake(IDisplayBle b, int id)
    {
        for (var tries = 0; tries < 120; tries++)
        {
            if (_ble != b || id != _bleHandshake || _bleReady) return;
            b.Write(Encode(new JsonObject { ["t"] = "hi" }), "hi");
            await Task.Delay(1000);
        }
        if (_ble == b && id == _bleHandshake && !_bleReady) b.Reconnect();
    }

    /// 根据两条通道的握手状态决定用哪条；换了通道（或刚连上）就把所有数据重发一遍。
    void RefreshTransport()
    {
        Transport? now = _usbReady ? Transport.Usb : _bleReady ? Transport.Ble : null;
        if (now == Current) return;
        var wasConnected = Connected;
        Current = now;
        Connected = now != null;
        if (wasConnected != Connected) _agentsGeneration++;
        if (now == null)
        {
            // 手机推送还要 Agent 提醒时接着读（不重新安静地读一遍）。
            if (!AgentsWanted) { _agentsLive = false; _agentList = []; }
            DebugLog.Log("副屏：断开");
            Firmware = null;
            Board = [];
            Network = [];
            Battery = null;
            Changed?.Invoke();
            return;
        }
        Linked();
    }

    void Linked()
    {
        DebugLog.Log($"副屏：通过{(Current == Transport.Usb ? "USB" : "蓝牙")}连接（固件 {Firmware ?? "?"}）");
        Changed?.Invoke();
        Write(new JsonObject { ["t"] = "getsettings" });
        SendSnapshot();
        SendAIUsage();
        PollAgents();
        SendAgents();
        SendHistory();
        SendExtras(false);
        SendScreenState();
    }

    void Handle(string line, Transport from)
    {
        if (!line.StartsWith('{')) return;
        JsonObject? obj;
        try { obj = JsonNode.Parse(line) as JsonObject; } catch { return; }
        if (obj == null) return;
        var t = AgentEvents.Str(obj, "t");
        if (t == "network" && from == Transport.Usb && _usbReady)
        {
            var next = obj.Where(kv => kv.Value is JsonValue v && v.TryGetValue<string>(out _))
                          .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>());
            // 只记状态变化（不含 Wi-Fi 名称和密码），排查配网用。
            int? clients = obj["clients"] is JsonValue cv && cv.TryGetValue<int>(out var c) ? c : null;
            if (clients != _networkClients)
            {
                _networkClients = clients;
                if (clients != null && next.GetValueOrDefault("ap_ip", "").Length > 0) DebugLog.Log($"副屏 Wi-Fi：热点上有 {clients} 台设备");
            }
            if (next.GetValueOrDefault("state") != Network.GetValueOrDefault("state") || next.GetValueOrDefault("error") != Network.GetValueOrDefault("error"))
            {
                var err = next.GetValueOrDefault("error", "");
                DebugLog.Log($"副屏 Wi-Fi：{next.GetValueOrDefault("state", "?")}{(err.Length > 0 ? $"（{err}）" : "")}{(next.GetValueOrDefault("ap_ip", "").Length > 0 ? "，热点已开" : "")}");
            }
            Network = next;
            Changed?.Invoke();
            return;
        }
        if (t == "battery" && Connected)
        {
            Battery = new BatteryInfo(obj["pct"] is JsonValue pv && pv.TryGetValue<int>(out var pct) ? pct : -1,
                                      obj["charging"] is JsonValue chv && chv.TryGetValue<bool>(out var ch) && ch,
                                      obj["usb"] is JsonValue uv && uv.TryGetValue<bool>(out var u) && u);
            Changed?.Invoke();
            return;
        }
        if (t == "settings" && Connected)
        {
            var b = new Dictionary<string, int>();
            foreach (var (k, v) in obj)
            {
                if (k == "t" || v is not JsonValue jv) continue;
                if (jv.TryGetValue<int>(out var n)) b[k] = n;
                else if (jv.TryGetValue<bool>(out var bo)) b[k] = bo ? 1 : 0;
                else if (jv.TryGetValue<double>(out var dbl)) b[k] = (int)dbl;
            }
            Board = b;
            Changed?.Invoke();
            return;
        }
        if (t != "hello" || AgentEvents.Str(obj, "app") != "serverisland-display") return;
        Firmware = AgentEvents.Str(obj, "fw");
        if (from == Transport.Usb && !_usbReady)
        {
            _usbReady = true;
            RefreshTransport();
            return;
        }
        if (from == Transport.Ble && !_bleReady)
        {
            DebugLog.Log($"副屏蓝牙：握手成功（固件 {Firmware ?? "?"}）{(_usbReady ? "，插着 USB 时仍走 USB" : "")}");
            _bleReady = true;
            RefreshTransport();
            return;
        }
        if (from == Current) SendSnapshot();
    }

    static byte[] Encode(JsonObject obj) => Encoding.UTF8.GetBytes(obj.ToJsonString(WireOptions) + "\n");

    static readonly JsonSerializerOptions WireOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// 发给副屏：走当前通道。
    void Write(JsonObject obj)
    {
        switch (Current)
        {
            case Transport.Usb when _port is { } p: p.Write(Encode(obj)); break;
            case Transport.Ble when _ble is { } b: b.Write(Encode(obj), AgentEvents.Str(obj, "t")); break;
        }
    }

    /// 设置页改了板子上的某一项：先在本地显示新值，再发给板子（板子应用后会回一条完整的 settings）。
    public void SetBoard(string key, int value)
    {
        if (!Connected) return;
        Board[key] = value;
        Write(new JsonObject { ["t"] = "set", [key] = value });
        Changed?.Invoke();
    }

    /// Wi-Fi 配置只通过 USB 发给板子，不保存进 App 配置或日志。
    public void ConfigureNetwork(JsonObject fields)
    {
        if (!_usbReady || Current != Transport.Usb || _port == null) return;
        fields["t"] = "network";
        _port.Write(Encode(fields));
    }

    static string ExtrasKey(AppSettings s) =>
        string.Join(";", s.Deadlines.Select(d => d.Title + "@" + d.Date?.ToString("O"))) + "|" + s.WeatherCity.Trim();

    void SettingsChanged()
    {
        // 设置页改了截止日期、城市：马上发一次。
        var key = ExtrasKey(_store.Settings);
        if (key != _extrasKey)
        {
            var cityChanged = key.Split('|').Last() != _extrasKey.Split('|').Last();
            _extrasKey = key;
            SendExtras(cityChanged);
        }
        if (_lastLockSync != _store.Settings.DisplayLockSync)
        {
            _lastLockSync = _store.Settings.DisplayLockSync;
            SendScreenState();
        }
        if (!_store.Settings.UsbDisplay && _port != null) Disconnect("设置里关闭了副屏");
        UpdateBluetooth();
    }

    public void SendExtras(bool forceWeather)
    {
        if (!Connected) return;
        Write(DisplayExtras.CalendarMessage(_store.Settings));
        _ = Extras.RefreshWeather(_store.Settings.WeatherCity, forceWeather, Write);
    }

    /// 锁屏检测：每 3 秒在后台问一次，状态变了才发。
    void PollLock()
    {
        if (IsLocked is not { } probe || !Connected) return;
        Task.Run(() => { try { return probe(); } catch { return false; } })
            .ContinueWith(t => Post(() =>
            {
                if (t.Result == _locked) return;
                _locked = t.Result;
                SendScreenState();
            }));
    }

    void SendScreenState()
    {
        if (!Connected) return;
        Write(new JsonObject { ["t"] = "screen", ["on"] = !_store.Settings.DisplayLockSync || !_locked });
    }

    // —— 快照 ——

    /// AI 用量（CC Switch 的数据库）：装了 CC Switch 就自动发，不用设置。在后台线程里读，读完回界面线程发。
    bool _aiReading;
    async void SendAIUsage()
    {
        if (!Connected || _aiReading || !AIUsage.Available) return;
        _aiReading = true;
        try
        {
            var msg = await Task.Run(() => AIUsage.Snapshot());
            if (Connected && msg != null) Write(msg);
        }
        catch (Exception e) { DebugLog.Log("AI 用量：" + e.Message); }
        finally { _aiReading = false; }
    }

    public static string ToolName(string tool) => tool switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        _ => tool,
    };

    static string Project(string cwd) =>
        cwd.Length == 0 ? "" : Path.GetFileName(cwd.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : cwd;

    static string StateRaw(AgentState s) => s.ToString().ToLowerInvariant();

    void SendAgents()
    {
        if (!Connected) return;
        var now = DateTime.Now;
        Write(new JsonObject
        {
            ["t"] = "agents",
            ["list"] = new JsonArray(_agentList.Take(10).Select(s =>
            {
                var o = new JsonObject
                {
                    ["tool"] = s.Tool, ["proj"] = Project(s.Cwd), ["title"] = s.Title, ["state"] = StateRaw(s.State), ["msg"] = s.Message,
                    ["ago"] = (int)(now - s.ChangedAt).TotalSeconds,
                };
                if (s.Plan.Total > 0) o["todo"] = new JsonObject { ["done"] = s.Plan.Done, ["total"] = s.Plan.Total, ["now"] = s.Plan.Now };
                return (JsonNode)o;
            }).ToArray()),
        });
    }

    void SendAgentAlert(AgentSession s)
    {
        if (!Connected) return;
        DebugLog.Log($"副屏 Agent：{s.Tool} {StateRaw(s.State)}");
        var name = ToolName(s.Tool);
        var (kind, title, detail) = s.State switch
        {
            AgentState.Attention => ("attention", L($"{name} 需要你确认", $"{name} needs you"), s.Message),
            AgentState.Error => ("failed", L($"{name} 出错了", $"{name} hit an error"), s.Title),
            _ => ("agent_done", L($"{name} 回答完了", $"{name} is done"), s.Title),
        };
        var sub = string.Join(" · ", new[] { Project(s.Cwd), detail }.Where(x => x.Length > 0));
        Write(new JsonObject { ["t"] = "event", ["kind"] = kind, ["title"] = title, ["sub"] = sub, ["host"] = "", ["tool"] = s.Tool });
        SendAgents();
    }

    /// 最近结束的任务（所有服务器合在一起，新的在前）。
    void SendHistory()
    {
        if (!Connected) return;
        var now = DateTime.Now;
        var all = _engine.OrderedHosts.SelectMany(h => _engine.History.Entries(h.Host.Id).Select(e => (h.Host, e)))
                         .OrderByDescending(x => x.e.EndedAt).Take(20);
        Write(new JsonObject
        {
            ["t"] = "history",
            ["list"] = new JsonArray(all.Select(x =>
            {
                var o = new JsonObject
                {
                    ["name"] = Clip(x.e.Name, 60), ["host"] = x.Host.DisplayName, ["label"] = x.e.StateLabel,
                    ["state"] = x.e.State == HistoryEntry.EndedUnknown ? "other" : x.e.JobState.Raw(),
                    ["ago"] = (int)(now - x.e.EndedAt).TotalSeconds,
                };
                if (x.e.Elapsed is { } el) o["el"] = (int)el;
                if (_engine.BillingActive(x.Host) && _engine.Costs.CostOf(x.Host.Id, x.e.Id) is { } cost) o["cost"] = Cents(cost);
                return (JsonNode)o;
            }).ToArray()),
        });
    }

    /// 状态一变就推（合并 0.3 秒内的连续变化）。
    void ScheduleSnapshot()
    {
        if (!Connected) return;
        var v = ++_snapVersion;
        _ = Task.Delay(300).ContinueWith(_ => Post(() => { if (v == _snapVersion) SendSnapshot(); }));
    }

    void SendSnapshot()
    {
        if (!Connected) return;
        Write(Snapshot(_engine, _store));
    }

    /// 每台机器最多带这么多任务（副屏一张卡片显示 4 个，多带几个以后翻页用）。
    public const int MaxJobs = 8;

    public static JsonObject Snapshot(MonitorEngine engine, ConfigStore store, DateTime? nowArg = null)
    {
        var now = nowArg ?? DateTime.Now;
        var hosts = new JsonArray();
        foreach (var (host, snap) in engine.OrderedHosts)
        {
            var h = new JsonObject { ["name"] = host.DisplayName };
            switch (snap.Health.Kind)
            {
                case HealthKind.Ok: h["health"] = "ok"; break;
                case HealthKind.Unknown: h["health"] = "unknown"; break;
                case HealthKind.NeedsLogin: h["health"] = "login"; h["msg"] = snap.Health.Message; break;
                case HealthKind.Error: h["health"] = "error"; h["msg"] = snap.Health.Message; break;
            }
            if (host.ShowCPU && snap.Metrics.CpuPercent is { } c) h["cpu"] = Round(c);
            if (host.ShowMemory && snap.Metrics.MemoryFraction is { } f) h["mem"] = Round(f * 100);
            if (host.ShowGPU && snap.Gpus.Count > 0)
            {
                h["gpu"] = Round(snap.Gpus.Average(g => g.Utilization));
                // 每张卡：利用率、显存占用、温度（资源页用）。
                h["gpus"] = new JsonArray(snap.Gpus.Take(8).Select(g =>
                {
                    var o = new JsonObject { ["u"] = Round(g.Utilization), ["m"] = Round(g.MemoryFraction * 100) };
                    if (g.Temperature is { } t) o["t"] = Round(t);
                    return (JsonNode)o;
                }).ToArray());
            }
            if (host.ShowDisk && snap.Disks.Count > 0)
                h["disks"] = new JsonArray(snap.Disks.Take(4).Select(d => (JsonNode)new JsonObject { ["m"] = Clip(d.Mount, 24), ["p"] = Round(d.Fraction * 100) }).ToArray());
            var starred = store.Settings.StarredJobs.Where(s => s.HostID == host.Id).Select(s => s.JobID).ToHashSet();
            var jobs = MonitorEngine.StarredFirst(snap.Jobs, j => starred.Contains(j.Id));
            // 已运行 / 剩余时间是采样时刻的值，按距采样过去的时间修正。
            var age = snap.LastUpdate is { } lu ? Math.Max(0, (now - lu).TotalSeconds) : 0;
            // 计费：每个任务已花多少（cs）、每小时多少（cr），整台机器每小时多少（cr）。
            h["jobs"] = new JsonArray(jobs.Take(MaxJobs).Select(j =>
            {
                var o = Job(j, age, starred.Contains(j.Id));
                if (engine.JobCost(host, j) is { } jc)
                {
                    o["cs"] = Cents(jc.Spent + jc.PerHour * age / 3600);
                    if (jc.PerHour > 0) o["cr"] = Cents(jc.PerHour);
                }
                return (JsonNode)o;
            }).ToArray());
            var rate = jobs.Sum(j => engine.JobCost(host, j)?.PerHour ?? 0);
            if (rate > 0) h["cr"] = Cents(rate);
            h["njobs"] = jobs.Count;
            hosts.Add(h);
        }
        var mood = engine.PetMood.ToString().ToLowerInvariant();
        // now / tz：副屏的时钟用电脑的时间（UTC 秒 + 时区偏移秒）。
        var message = new JsonObject
        {
            ["t"] = "snap", ["lang"] = IsEnglish ? "en" : "zh", ["mood"] = mood,
            ["run"] = engine.TotalRunning, ["pend"] = engine.TotalPending, ["hosts"] = hosts,
            ["now"] = new DateTimeOffset(now).ToUnixTimeSeconds(), ["tz"] = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalSeconds,
        };
        // 计费打开时：今天 / 本月 / 上月已结束任务的费用、正在运行的每小时费用（都是估算）。
        if (store.Settings.BillingEnabled)
        {
            var s = new CostSummary(engine.CostEntriesWithRunning(), now);
            message["cost"] = new JsonObject
            {
                ["cur"] = store.Settings.Currency, ["today"] = Cents(s.Today), ["month"] = Cents(s.Month),
                ["last"] = Cents(s.LastMonth), ["rate"] = Cents(engine.TotalBurnRate),
            };
        }
        return FitSnapshot(message);
    }

    /// 金额保留两位小数（JSON 里是数字）。
    public static double Cents(double v) => Math.Round(v * 100) / 100;

    /// 按 UTF-8 总字节数裁剪，关注任务最后才裁掉；不修改调用者的消息。
    public static JsonObject FitSnapshot(JsonObject message, int limit = 30 * 1024)
    {
        var result = (JsonObject)message.DeepClone();
        var hosts = result["hosts"] as JsonArray ?? [];
        bool Fits() => Encode(result).Length <= limit;
        if (Fits()) return result;
        foreach (var host in hosts.OfType<JsonObject>())
            foreach (var job in (host["jobs"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var key in new[] { "detail", "msg", "dir", "app" }) job.Remove(key);
        while (!Fits() && hosts.Count > 0)
        {
            var removed = false;
            foreach (var starred in new[] { false, true })
            {
                foreach (var host in hosts.OfType<JsonObject>().Reverse())
                {
                    if (host["jobs"] is not JsonArray jobs) continue;
                    for (var j = jobs.Count - 1; j >= 0; j--)
                        if ((jobs[j]?["star"]?.GetValue<bool>() ?? false) == starred)
                        {
                            jobs.RemoveAt(j);
                            removed = true;
                            break;
                        }
                    if (removed) break;
                }
                if (removed) break;
            }
            if (!removed) hosts.RemoveAt(hosts.Count - 1);
        }
        return result;
    }

    /// Swift 的 rounded()：四舍五入，.5 远离 0。
    static int Round(double v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);

    /// 详情页的文字字段截短，免得一条 snap 太大（副屏的接收缓冲是 32KB）。
    static string Clip(string s, int n = 120) => s.Length > n ? s[..(n - 1)] + "…" : s;

    static JsonObject Job(RemoteJob j, double age, bool starred)
    {
        var o = new JsonObject { ["id"] = j.Id, ["name"] = Clip(j.Name, 80), ["state"] = j.State.Raw() };
        if (starred) o["star"] = true;
        if (j.Fraction is { } f) o["pct"] = Round(f * 100);
        if (j.HasRealProgress) o["real"] = true;
        if (j.Array is { } a) o["array"] = $"{a.Done + a.Failed}/{a.Total}";
        if (j.State is JobState.Running or JobState.Completing)
        {
            if (j.Eta is { } e) o["eta"] = (int)Math.Max(0, e - age);
            if (j.Elapsed is { } el) o["el"] = (int)(el + age);
        }
        if (j.State == JobState.Pending && j.Reason is { } r) o["reason"] = r;
        // 任务详情页用的字段。
        if (j.Limit is { } l && l > 0) o["limit"] = (int)l;
        if (j.Detail.Length > 0) o["detail"] = Clip(j.Detail);
        if (j.App is { } app)
        {
            o["app"] = app.Title;
            if (app.Summary is { Length: > 0 } summary) o["msg"] = Clip(summary);
        }
        else if (j.Message is { Length: > 0 } m) o["msg"] = Clip(m);
        if (j.CpuPercent is { } cpu) o["cpu"] = Round(cpu);
        if (j.MemoryBytes is { } mem) o["memMB"] = (int)(mem / 1_048_576);
        if (j.GpuMemoryMB is { } g) o["gpuMB"] = (int)g;
        if (j.Workdir is { Length: > 0 } d) o["dir"] = Clip(d);
        return o;
    }

    // —— 找串口 ——

    /// 当前插着的 Espressif USB 串口。
    /// Windows：注册表里 USB\VID_303A 设备的 PortName（再和现有的 COM 口取交集）；Linux：/sys/class/tty 里往上找 idVendor；
    /// macOS（只用于在 Mac 上试用）：/dev/cu.usbmodem*，靠握手过滤。
    public static List<string> EspressifPorts()
    {
        var result = new List<string>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
                using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
                if (usb != null)
                    foreach (var dev in usb.GetSubKeyNames().Where(n => n.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase)))
                    {
                        using var dk = usb.OpenSubKey(dev);
                        if (dk == null) continue;
                        foreach (var inst in dk.GetSubKeyNames())
                        {
                            using var pk = dk.OpenSubKey(inst + @"\Device Parameters");
                            if (pk?.GetValue("PortName") is string name && present.Contains(name)) result.Add(name);
                        }
                    }
            }
            else if (OperatingSystem.IsLinux())
            {
                foreach (var tty in Directory.GetDirectories("/sys/class/tty"))
                {
                    var name = Path.GetFileName(tty);
                    if (!name.StartsWith("ttyACM") && !name.StartsWith("ttyUSB")) continue;
                    var dev = Path.Combine(tty, "device");
                    if (!Directory.Exists(dev)) continue;
                    var dir = new DirectoryInfo(dev).ResolveLinkTarget(true)?.FullName ?? dev;
                    for (var i = 0; i < 6 && dir != null; i++, dir = Path.GetDirectoryName(dir))
                    {
                        var vid = Path.Combine(dir, "idVendor");
                        if (!File.Exists(vid)) continue;
                        if (Convert.ToInt32(File.ReadAllText(vid).Trim(), 16) == EspressifVendorID) result.Add("/dev/" + name);
                        break;
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                result.AddRange(Directory.GetFiles("/dev", "cu.usbmodem*"));
            }
        }
        catch (Exception e) { DebugLog.Log("副屏：找串口失败 " + e.Message); }
        result.Sort(StringComparer.Ordinal);
        return result.Distinct().ToList();
    }
}

/// 一个打开的串口：后台读取、按行回调；写入在后台按顺序进行。
sealed class SerialLink
{
    public string Path { get; }
    readonly SerialPort _port;
    readonly Action<string> _onLine;
    readonly Action _onClose;
    readonly System.Threading.Channels.Channel<byte[]> _writes = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
    volatile bool _closed;

    SerialLink(string path, SerialPort port, Action<string> onLine, Action onClose)
    {
        Path = path;
        _port = port;
        _onLine = onLine;
        _onClose = onClose;
    }

    public static SerialLink? Open(string path, Action<string> onLine, Action onClose)
    {
        try
        {
            // ESP32-S3 的 USB 串口把"RTS 有效、DTR 无效"当成复位信号：保持 DTR 有效、关掉 RTS，板子就不会被复位。
            var port = new SerialPort(path, 115200)
            {
                DtrEnable = true, RtsEnable = false, Handshake = Handshake.None, ReadTimeout = SerialPort.InfiniteTimeout,
                WriteTimeout = 2000, ReadBufferSize = 64 * 1024, WriteBufferSize = 64 * 1024,
            };
            port.Open();
            var link = new SerialLink(path, port, onLine, onClose);
            new Thread(link.ReadLoop) { IsBackground = true, Name = "display-read" }.Start();
            _ = Task.Run(link.WriteLoop);
            return link;
        }
        catch (Exception e)
        {
            DebugLog.Log($"副屏：打开 {path} 失败 {e.Message}");
            return null;
        }
    }

    public void Write(byte[] data)
    {
        if (!_closed) _writes.Writer.TryWrite(data);
    }

    async Task WriteLoop()
    {
        try
        {
            await foreach (var data in _writes.Reader.ReadAllAsync())
            {
                if (_closed) break;
                try { _port.BaseStream.Write(data, 0, data.Length); _port.BaseStream.Flush(); }
                catch (TimeoutException) { }
                catch { Fail(); break; }
            }
        }
        catch { }
    }

    void ReadLoop()
    {
        var buffer = new List<byte>(4096);
        var chunk = new byte[4096];
        while (!_closed)
        {
            int n;
            try { n = _port.BaseStream.Read(chunk, 0, chunk.Length); }
            catch (TimeoutException) { continue; }
            catch { Fail(); return; }
            if (n <= 0) { Fail(); return; }
            for (var i = 0; i < n; i++)
            {
                if (chunk[i] != (byte)'\n') { buffer.Add(chunk[i]); continue; }
                var line = Encoding.UTF8.GetString(buffer.ToArray()).Trim();
                buffer.Clear();
                _onLine(line);
            }
            // 截图等二进制数据不需要：缓冲过大就丢掉。
            if (buffer.Count > 64 * 1024) buffer.Clear();
        }
    }

    void Fail()
    {
        if (_closed) return;
        Close();
        _onClose();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _writes.Writer.TryComplete();
        try { _port.Close(); } catch { }
    }
}
