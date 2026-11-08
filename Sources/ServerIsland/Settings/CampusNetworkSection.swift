import SwiftUI

/// USB 配网：板子连一个 Wi-Fi 并共享热点。需要网页认证的网络由手机连上热点后在手机上登录，
/// 账号密码不经过 ServerIsland。Wi-Fi 密码仅存在本次表单内存，板端是否保存由用户选择。
struct CampusNetworkSection: View {
    @ObservedObject var link: DisplayLink
    @State private var ssid = ""
    @State private var wifiPassword = ""
    @State private var share = false
    @State private var apName = "ServerIsland"
    @State private var apPassword = ""
    @State private var remember = false

    private var ready: Bool { link.connected && link.transport == .usb && !link.network.isEmpty }
    private var valid: Bool {
        !ssid.isEmpty && ssid.utf8.count <= 32 && wifiPassword.utf8.count <= 63 &&
        (!share || (!apName.isEmpty && apName.utf8.count <= 32 && (8...63).contains(apPassword.utf8.count) && apPassword.utf8.allSatisfy({ $0 >= 32 && $0 <= 126 })))
    }
    private var status: String {
        switch link.network["state"] {
        case "off": return L("已关闭", "Off")
        case "connecting": return L("正在连接 Wi-Fi", "Connecting to Wi-Fi")
        case "online":
            if let ap = link.network["ap_name"], !ap.isEmpty { return L("已连接，热点“\(ap)”已开启", "Connected; hotspot “\(ap)” is on") }
            return L("已连接", "Connected")
        case "error": return L("连接失败", "Connection failed")
        default: return L("需要用 USB 连接副屏（固件 0.3.9 及以上）", "Connect the display over USB (firmware 0.3.9 or later)")
        }
    }
    private var error: String {
        switch link.network["error"] {
        case "no_config": return L("板子上还没有保存过 Wi-Fi，请在下面填写后连接。", "No Wi-Fi saved on the board yet; fill in the form below and connect.")
        case "worker_unavailable", "wifi_init_failed": return L("板子内存不足，Wi-Fi 没能启动。可以先关掉板子的蓝牙再试。", "The board ran out of memory starting Wi-Fi. Try turning off its Bluetooth.")
        case "invalid_config": return L("请检查 Wi-Fi 名称和热点密码格式。", "Check the Wi-Fi name and hotspot password.")
        case "wifi_retry": return L("尚未连上 Wi-Fi，正在重试。仅支持 2.4 GHz 的开放或个人密码网络。", "Retrying Wi-Fi; requires a 2.4 GHz open or personal-password network.")
        case "hotspot_failed": return L("已连上 Wi-Fi，但热点启动失败。", "Connected, but the hotspot failed to start.")
        default: return ""
        }
    }
    var body: some View {
        Section {
            LabeledContent(L("状态", "Status"), value: status)
            if let mac = link.network["mac"], !mac.isEmpty {
                LabeledContent(L("固定 MAC", "Fixed MAC"), value: mac).textSelection(.enabled)
            }
            if let ip = link.network["ip"], !ip.isEmpty { LabeledContent("IP", value: ip) }
            if let ip = link.network["ap_ip"], !ip.isEmpty {
                LabeledContent(L("热点网关", "Hotspot gateway"), value: ip)
            }
            if !error.isEmpty { Text(error).foregroundStyle(.orange).font(.caption) }
            Group {
                TextField(L("Wi-Fi 名称", "Wi-Fi name"), text: $ssid)
                SecureField(L("Wi-Fi 密码（开放网络留空）", "Wi-Fi password (empty for open networks)"), text: $wifiPassword)
                Toggle(L("共享 Wi-Fi 热点", "Share Wi-Fi hotspot"), isOn: $share)
                if share {
                    TextField(L("热点名称", "Hotspot name"), text: $apName)
                    SecureField(L("热点密码（8–63 位英文字符）", "Hotspot password (8–63 ASCII characters)"), text: $apPassword)
                }
                Toggle(L("在板子上记住配置，重启后自动连接", "Remember on board and reconnect after restart"), isOn: $remember)
                HStack {
                    Button(L("连接", "Connect")) {
                        link.configureCampus(["action": "connect", "ssid": ssid, "wifi_password": wifiPassword,
                                              "share": share, "ap_name": apName, "ap_password": apPassword, "remember": remember])
                        wifiPassword = ""; apPassword = ""
                    }.disabled(!valid)
                    Button(L("断开", "Disconnect")) { link.configureCampus(["action": "off"]) }
                    Button(L("忘记配置", "Forget configuration")) {
                        link.configureCampus(["action": "forget"])
                        wifiPassword = ""; apPassword = ""
                    }
                }
            }.disabled(!ready)
        } header: {
            SettingsHeader(title: L("Wi-Fi 与热点", "Wi-Fi & Hotspot"), symbol: "wifi", tint: .blue)
        } footer: {
            Text(L("板子连上这个 Wi-Fi 后开启自己的热点。需要网页登录的网络（如校园网）：手机连上板子的热点，在手机浏览器里登录，板子和热点下的设备就都能上网；账号密码不经过 ServerIsland。仅支持 2.4 GHz、开放或个人密码的 Wi-Fi（不支持 eduroam）；板子使用芯片出厂的固定 MAC。Wi-Fi 密码只经 USB 发送，勾选记住后保存在板子的未加密 Flash 中，电脑端不保存。",
                   "The board joins this Wi-Fi and opens its own hotspot. For networks with a web login (e.g. campus Wi-Fi), join the board's hotspot on your phone and log in in the phone's browser; the board and everything on the hotspot then get online. Your login never passes through ServerIsland. 2.4 GHz open or personal-password Wi-Fi only (no eduroam); the board uses the chip's fixed factory MAC. The Wi-Fi password is sent over USB only; if remembered it is stored in the board's unencrypted flash, never on the Mac."))
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
