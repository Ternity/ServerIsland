import CoreBluetooth
import Foundation

/// 通过蓝牙（BLE）连副屏：和 USB 串口一样收发"一行一条 JSON"。UUID 和固件 src/ble.h 里的一致。
/// 只在设置里打开"也通过蓝牙连接"时才创建（创建 CBCentralManager 就会触发系统的蓝牙授权）。
/// 写入一律带回应：慢一点，但可靠；第一次写加密特征时 macOS 会弹配对框，输入副屏上显示的 6 位数。
@MainActor
final class BLELink: NSObject {
    static let service = CBUUID(string: "5a1d0001-7e6f-4f4b-9c2a-3e1f5d7b9a01")
    static let rx = CBUUID(string: "5a1d0002-7e6f-4f4b-9c2a-3e1f5d7b9a01")
    static let tx = CBUUID(string: "5a1d0003-7e6f-4f4b-9c2a-3e1f5d7b9a01")

    var onReady: (() -> Void)?
    var onLine: ((String) -> Void)?
    var onDisconnect: (() -> Void)?
    private(set) var ready = false

    private var central: CBCentralManager!
    private var peripheral: CBPeripheral?
    private var rxChar: CBCharacteristic?
    private var buffer = Data()
    private struct Message {
        let data: Data
        let kind: String?
    }
    private var pending: [Message] = []
    private var current: Message?
    private var offset = 0
    private var inFlight = 0
    private var writing = false
    private var stopped = false
    private var waitingForPairing = false

    override init() {
        super.init()
        central = CBCentralManager(delegate: self, queue: .main)
    }

    func stop() {
        stopped = true
        if let p = peripheral { central.cancelPeripheralConnection(p) }
        central.stopScan()
        dropped()
    }

    /// 发一段数据：按单次最大写入长度切块，一块写完（收到回应）再写下一块。
    func write(_ data: Data) {
        guard ready else { return }
        let kind = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["t"] as? String
        // 只替换尚未开始发送的完整快照，绝不删除半条 JSON 的分块。
        if let kind, ["hi", "snap", "ai", "agents", "history", "cal", "weather", "screen"].contains(kind) {
            pending.removeAll { $0.kind == kind }
        }
        guard pending.count < 64 else { reconnect(); return }
        pending.append(Message(data: data, kind: kind))
        pump()
    }

    private func pump() {
        guard !writing, ready, let p = peripheral, let c = rxChar else { return }
        if let current, offset >= current.data.count { self.current = nil }
        if current == nil {
            guard !pending.isEmpty else { return }
            current = pending.removeFirst()
            offset = 0
        }
        guard let current else { return }
        // 每块不超过一个 ATT 包（MTU - 3）：更长的"带回应写入"会变成 Prepare/Execute 长写，
        // 板子的协议栈缓冲不够时不回应，30 秒后连接被系统断开。
        inFlight = min(max(1, p.maximumWriteValueLength(for: .withoutResponse)), current.data.count - offset)
        writing = true
        p.writeValue(current.data.subdata(in: offset..<offset + inFlight), for: c, type: .withResponse)
    }

    func reconnect() {
        if let p = peripheral { central.cancelPeripheralConnection(p) }
        dropped()
    }

    private func scan() {
        guard !stopped, central.state == .poweredOn, peripheral == nil else { return }
        // 先看系统里是不是已经连着（配对过的设备 macOS 可能自己连上了）。
        if let p = central.retrieveConnectedPeripherals(withServices: [Self.service]).first {
            DebugLog.log("副屏蓝牙：系统已连着副屏，直接接管")
            connect(p)
            return
        }
        DebugLog.log("副屏蓝牙：开始扫描")
        central.scanForPeripherals(withServices: [Self.service])
    }

    private func connect(_ p: CBPeripheral) {
        central.stopScan()
        peripheral = p
        p.delegate = self
        central.connect(p)
    }

    private func dropped() {
        let was = ready
        ready = false
        peripheral = nil
        rxChar = nil
        pending.removeAll()
        current = nil
        offset = 0
        inFlight = 0
        writing = false
        waitingForPairing = false
        buffer.removeAll()
        if was { onDisconnect?() }
        // 过一会儿再找（板子重启、走出范围又回来）。
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.scan() }
    }
}

extension BLELink: CBCentralManagerDelegate {
    nonisolated func centralManagerDidUpdateState(_ central: CBCentralManager) {
        MainActor.assumeIsolated {
            if central.state == .poweredOn { scan() } else {
                DebugLog.log("副屏蓝牙：状态 \(central.state.rawValue)")
                dropped()
            }
        }
    }

    nonisolated func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral,
                                    advertisementData: [String: Any], rssi RSSI: NSNumber) {
        MainActor.assumeIsolated {
            DebugLog.log("副屏蓝牙：发现副屏（信号 \(RSSI)），连接中")
            connect(peripheral)
        }
    }

    nonisolated func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        MainActor.assumeIsolated {
            DebugLog.log("副屏蓝牙：已连上，查找服务")
            peripheral.discoverServices([Self.service])
        }
    }

    nonisolated func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) {
        MainActor.assumeIsolated { DebugLog.log("副屏蓝牙：连接失败 \(error?.localizedDescription ?? "")") }
        MainActor.assumeIsolated { if peripheral === self.peripheral { dropped() } }
    }

    nonisolated func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral else { return }
            DebugLog.log("副屏蓝牙：断开\(error.map { "（\($0.localizedDescription)）" } ?? "")")
            dropped()
        }
    }
}

extension BLELink: CBPeripheralDelegate {
    nonisolated func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral else { return }
            guard error == nil, let s = peripheral.services?.first(where: { $0.uuid == Self.service }) else { reconnect(); return }
            peripheral.discoverCharacteristics([Self.rx, Self.tx], for: s)
        }
    }

    nonisolated func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral else { return }
            rxChar = service.characteristics?.first { $0.uuid == Self.rx }
            if error == nil, rxChar != nil, let tx = service.characteristics?.first(where: { $0.uuid == Self.tx }) {
                peripheral.setNotifyValue(true, for: tx)
            } else { reconnect() }
        }
    }

    nonisolated func peripheral(_ peripheral: CBPeripheral, didUpdateNotificationStateFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral, characteristic.uuid == Self.tx else { return }
            if let error {
                DebugLog.log("副屏蓝牙：订阅失败 \(error.localizedDescription)")
                reconnect()
                return
            }
            guard characteristic.isNotifying else { reconnect(); return }
            ready = true
            onReady?()
        }
    }

    nonisolated func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral, characteristic.uuid == Self.tx, let v = characteristic.value else { return }
            buffer.append(v)
            while let i = buffer.firstIndex(of: 0x0A) {
                let line = String(decoding: buffer[buffer.startIndex..<i], as: UTF8.self)
                buffer.removeSubrange(buffer.startIndex...i)
                onLine?(line)
            }
            if buffer.count > 64 * 1024 { buffer.removeAll() }
        }
    }

    nonisolated func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        MainActor.assumeIsolated {
            guard peripheral === self.peripheral, writing else { return }
            writing = false
            if let error {
                // 还没配对：macOS 收到这个错误后会自己发起配对并弹出输入框（配对码显示在副屏上）。
                // 这时不能断开，否则配对被打断、永远弹不出来；等一会儿重发这一块。
                if let att = error as? CBATTError, att.code == .insufficientAuthentication || att.code == .insufficientEncryption {
                    if !waitingForPairing { DebugLog.log("副屏蓝牙：等待配对（在 Mac 弹框里输入副屏上的 6 位数）") }
                    waitingForPairing = true
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self, weak peripheral] in
                        guard let self, let peripheral, peripheral === self.peripheral else { return }
                        self.pump()
                    }
                    return
                }
                DebugLog.log("副屏蓝牙：写入失败 \(error.localizedDescription)")
                reconnect()
                return
            }
            if waitingForPairing { DebugLog.log("副屏蓝牙：配对完成") }
            waitingForPairing = false
            offset += inFlight
            pump()
        }
    }
}
