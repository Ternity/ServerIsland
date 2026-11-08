#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using ServerIsland.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace ServerIsland.Desktop.Platform;

/// Windows 上副屏的蓝牙通道（WinRT 的 Bluetooth LE 接口；和 Mac 版 BLELink.swift 对应，UUID 相同）。
/// 第一次连接时副屏上显示 6 位配对码，这里弹框让用户输入（AskPasskey）；配对信息由 Windows 保存，之后不再问。
/// 收发的都是"一行一条 JSON"；写入用"带回应写入"，每块不超过一个 ATT 包（MTU − 3），一块写完再写下一块。
public sealed class WinBle : IDisplayBle
{
    static readonly Guid Service = Guid.Parse("5a1d0001-7e6f-4f4b-9c2a-3e1f5d7b9a01");
    static readonly Guid Rx = Guid.Parse("5a1d0002-7e6f-4f4b-9c2a-3e1f5d7b9a01");  // 电脑 → 副屏（写）
    static readonly Guid Tx = Guid.Parse("5a1d0003-7e6f-4f4b-9c2a-3e1f5d7b9a01");  // 副屏 → 电脑（通知）
    /// 同类快照只保留最新一条（还没开始发送的），握手的 hi 也一样。
    static readonly HashSet<string> Replaceable = ["hi", "snap", "ai", "agents", "history", "cal", "weather", "screen"];

    public Action? OnReady { get; set; }
    public Action<string>? OnLine { get; set; }
    public Action? OnDisconnect { get; set; }
    /// 让用户输入副屏上的配对码（在界面线程上弹框；取消返回 null）。
    readonly Func<Task<string?>> _askPasskey;

    readonly object _lock = new();
    readonly List<(byte[] Data, string? Kind)> _pending = [];
    readonly SemaphoreSlim _signal = new(0);
    BluetoothLEAdvertisementWatcher? _watcher;
    BluetoothLEDevice? _device;
    GattSession? _session;
    GattCharacteristic? _rx, _tx;
    volatile bool _ready, _stopped, _connecting;
    readonly List<byte> _buffer = [];
    int _generation;

    public WinBle(Func<Task<string?>> askPasskey)
    {
        _askPasskey = askPasskey;
        _ = Task.Run(WriteLoop);
        Scan();
    }

    void Scan()
    {
        if (_stopped || _device != null || _connecting) return;
        if (_watcher == null)
        {
            _watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(Service);
            _watcher.Received += (w, a) => { if (!_connecting && _device == null) _ = Connect(a.BluetoothAddress); };
        }
        try
        {
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
            {
                _watcher.Start();
                DebugLog.Log("副屏蓝牙：开始扫描");
            }
        }
        catch (Exception e) { DebugLog.Log("副屏蓝牙：扫描失败 " + e.Message); }
    }

    async Task Connect(ulong address)
    {
        if (_connecting || _stopped) return;
        _connecting = true;
        var gen = ++_generation;
        try
        {
            _watcher?.Stop();
            var dev = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (dev == null) throw new Exception("找不到设备");
            DebugLog.Log("副屏蓝牙：发现副屏，连接中");
            _device = dev;
            dev.ConnectionStatusChanged += (d, _) =>
            {
                if (d.ConnectionStatus == BluetoothConnectionStatus.Disconnected && gen == _generation && _ready)
                {
                    DebugLog.Log("副屏蓝牙：断开");
                    Dropped();
                }
            };
            // 没配对过：用自定义配对，副屏只能显示配对码，所以这边要输入（ProvidePin）。
            var pairing = dev.DeviceInformation.Pairing;
            if (!pairing.IsPaired)
            {
                DebugLog.Log("副屏蓝牙：等待配对（输入副屏上的 6 位数）");
                pairing.Custom.PairingRequested += OnPairingRequested;
                var r = await pairing.Custom.PairAsync(DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmOnly | DevicePairingKinds.ConfirmPinMatch,
                                                       DevicePairingProtectionLevel.EncryptionAndAuthentication);
                pairing.Custom.PairingRequested -= OnPairingRequested;
                if (r.Status is not (DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired))
                    throw new Exception("配对没有完成：" + r.Status);
                DebugLog.Log("副屏蓝牙：配对完成");
            }
            _session = await GattSession.FromDeviceIdAsync(dev.BluetoothDeviceId);
            _session.MaintainConnection = true;
            var services = await dev.GetGattServicesForUuidAsync(Service, BluetoothCacheMode.Uncached);
            var service = services.Status == GattCommunicationStatus.Success ? services.Services.FirstOrDefault() : null;
            if (service == null) throw new Exception("找不到服务");
            var rx = await service.GetCharacteristicsForUuidAsync(Rx, BluetoothCacheMode.Uncached);
            var tx = await service.GetCharacteristicsForUuidAsync(Tx, BluetoothCacheMode.Uncached);
            _rx = rx.Characteristics.FirstOrDefault();
            _tx = tx.Characteristics.FirstOrDefault();
            if (_rx == null || _tx == null) throw new Exception("找不到收发特征");
            _tx.ValueChanged += OnValue;
            var sub = await _tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
            if (sub != GattCommunicationStatus.Success) throw new Exception("订阅失败：" + sub);
            lock (_lock) _buffer.Clear();
            _ready = true;
            _connecting = false;
            DebugLog.Log("副屏蓝牙：已连上");
            OnReady?.Invoke();
            _signal.Release();
        }
        catch (Exception e)
        {
            DebugLog.Log("副屏蓝牙：连接失败 " + e.Message);
            _connecting = false;
            Dropped(notify: false);
        }
    }

    async void OnPairingRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (args.PairingKind == DevicePairingKinds.ProvidePin)
            {
                var pin = (await _askPasskey())?.Trim();
                if (!string.IsNullOrEmpty(pin)) args.Accept(pin);
            }
            else args.Accept();
        }
        catch { }
        finally { deferral.Complete(); }
    }

    void OnValue(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = args.CharacteristicValue.ToArray();
        var lines = new List<string>();
        lock (_lock)
        {
            foreach (var b in bytes)
            {
                if (b != (byte)'\n') { _buffer.Add(b); continue; }
                lines.Add(System.Text.Encoding.UTF8.GetString(_buffer.ToArray()));
                _buffer.Clear();
            }
            if (_buffer.Count > 64 * 1024) _buffer.Clear();
        }
        foreach (var l in lines) OnLine?.Invoke(l);
    }

    public void Write(byte[] data, string? kind)
    {
        if (!_ready) return;
        var tooMany = false;
        lock (_lock)
        {
            if (kind != null && Replaceable.Contains(kind)) _pending.RemoveAll(p => p.Kind == kind);
            tooMany = _pending.Count >= 64;
            if (!tooMany) _pending.Add((data, kind));
        }
        if (tooMany) { Reconnect(); return; }
        _signal.Release();
    }

    /// 一条消息一条消息地发；消息按 MTU 切块，每块等写入回应再发下一块。
    async Task WriteLoop()
    {
        while (!_stopped)
        {
            await _signal.WaitAsync();
            while (_ready && !_stopped)
            {
                byte[] data;
                lock (_lock)
                {
                    if (_pending.Count == 0) break;
                    data = _pending[0].Data;
                    _pending.RemoveAt(0);
                }
                if (!await Send(data)) break;
            }
        }
    }

    async Task<bool> Send(byte[] data)
    {
        var rx = _rx;
        if (rx == null) return false;
        var max = Math.Max(20, (_session?.MaxPduSize ?? 23) - 3);
        for (var off = 0; off < data.Length;)
        {
            var n = Math.Min(max, data.Length - off);
            var w = new DataWriter();
            w.WriteBytes(data.AsSpan(off, n).ToArray());
            GattWriteResult r;
            try { r = await rx.WriteValueWithResultAsync(w.DetachBuffer(), GattWriteOption.WriteWithResponse); }
            catch (Exception e) { DebugLog.Log("副屏蓝牙：写入失败 " + e.Message); Reconnect(); return false; }
            if (r.Status == GattCommunicationStatus.Success) { off += n; continue; }
            // 还没加密好（刚配对完的一瞬间）：等一会儿重发这一块。
            if (r.ProtocolError is { } pe && (pe == GattProtocolError.InsufficientAuthentication || pe == GattProtocolError.InsufficientEncryption))
            {
                await Task.Delay(2000);
                if (!_ready) return false;
                continue;
            }
            DebugLog.Log($"副屏蓝牙：写入失败 {r.Status}");
            Reconnect();
            return false;
        }
        return true;
    }

    public void Reconnect() => Dropped();

    void Dropped(bool notify = true)
    {
        var was = _ready;
        _ready = false;
        _generation++;
        lock (_lock) { _pending.Clear(); _buffer.Clear(); }
        try { if (_tx != null) _tx.ValueChanged -= OnValue; } catch { }
        _rx = null; _tx = null;
        try { _session?.Dispose(); } catch { }
        _session = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
        if (was && notify) OnDisconnect?.Invoke();
        // 过一会儿再找（板子重启、走出范围又回来）。
        if (!_stopped) _ = Task.Delay(3000).ContinueWith(_ => Scan());
    }

    public void Stop()
    {
        _stopped = true;
        try { _watcher?.Stop(); } catch { }
        Dropped(notify: false);
        _signal.Release();
    }
}
#endif
