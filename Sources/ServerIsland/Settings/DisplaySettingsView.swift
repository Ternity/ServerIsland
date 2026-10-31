import SwiftUI

/// 设置 → 副屏（只有在 通用 → 扩展 里打开"外接副屏"后才出现在侧栏）。
/// 连接状态、板子上的设置（改了直接发给板子）、锁屏同步、Agent 提醒、AI 用量。
struct DisplaySettingsView: View {
    @ObservedObject var store: ConfigStore
    /// 演示模式和预览渲染时没有。
    var link: DisplayLink?

    var body: some View {
        Form {
            Section {
                LabeledContent(L("状态", "Status")) {
                    if let link, link.connected {
                        Label((link.transport == .ble ? L("已通过蓝牙连接", "Connected over Bluetooth") : L("已通过 USB 连接", "Connected over USB"))
                              + (link.firmware.map { L(" · 固件 \($0)", " · firmware \($0)") } ?? ""),
                              systemImage: "checkmark.circle.fill")
                            .foregroundStyle(.green)
                    } else {
                        Text(L("未连接：用 USB 线插上刷了 ServerIsland 固件的板子", "Not connected: plug in a board running the ServerIsland firmware over USB"))
                            .foregroundStyle(.secondary)
                    }
                }
                Toggle(isOn: Binding(get: { store.settings.displayBluetooth }, set: { on in
                    store.settings.displayBluetooth = on
                    // 插着 USB 时顺便让板子打开蓝牙广播。
                    if on { link?.setBoard("ble", 1) }
                })) {
                    HelpLabel(L("也通过蓝牙连接", "Also Connect over Bluetooth"),
                              help: L("拔掉 USB 时改用蓝牙（插着 USB 时仍然用 USB）。第一次连接时副屏上会显示 6 位配对码，在 Mac 弹出的对话框里输入。板子上也要打开蓝牙（插着 USB 时打开这里会自动打开）。",
                                      "Use Bluetooth when USB is unplugged (USB is still preferred). The first time, the display shows a 6-digit code to type into the Mac's pairing dialog. Bluetooth must also be on on the board (turned on automatically if USB is connected)."))
                }
                Toggle(isOn: $store.settings.displayLockSync) {
                    HelpLabel(L("电脑锁屏时副屏熄屏", "Turn Off When Mac Locks"),
                              help: L("锁屏或显示器睡眠时副屏一起熄屏（期间的提醒照样记下来），解锁后自动亮起。",
                                      "The display turns off when your Mac locks or its screen sleeps (alerts are still logged) and turns back on when you unlock."))
                }
            } header: { SettingsHeader(title: L("连接", "Connection"), symbol: "cable.connector", tint: .teal) } footer: {
                Text(L("需要刷了 ServerIsland 副屏固件的 ESP32 板子（固件暂未公开）。", "Requires an ESP32 board running the ServerIsland display firmware (not yet public)."))
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            if let link {
                BoardSettingsSection(link: link)
                CampusNetworkSection(link: link)
            }

            ExtrasSections(store: store, link: link)

            Section {
                AgentHooksRows()
            } header: { SettingsHeader(title: L("Agent 提醒", "Agent Alerts"), symbol: "terminal.fill", tint: .orange) } footer: {
                Text(L("Claude Code、Codex、OpenCode 的对话需要你确认、回答完了时，副屏会提醒。",
                       "The display tells you when a Claude Code, Codex or OpenCode session needs your OK or has finished."))
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                LabeledContent {
                    Text(AIUsage.available ? L("已找到 CC Switch，自动显示", "CC Switch found, shown automatically")
                                           : L("没有安装 CC Switch，不显示", "CC Switch not installed"))
                        .foregroundStyle(.secondary)
                } label: {
                    HelpLabel(L("AI 用量", "AI Usage"),
                              help: L("只读 CC Switch 的本地记录（~/.cc-switch），每分钟更新副屏上的调用、token 和费用。",
                                      "Reads CC Switch's local log (~/.cc-switch, read-only) and updates calls, tokens and cost on the display every minute."))
                }
            } header: { SettingsHeader(title: L("AI 用量", "AI Usage"), symbol: "chart.bar.xaxis", tint: .green) }
        }
        .formStyle(.grouped)
    }
}

/// 板子上的设置：读的是板子发来的值，改了立即发给板子。没连上时整组禁用。
private struct BoardSettingsSection: View {
    @ObservedObject var link: DisplayLink
    @State private var brightness: Double = 180

    private func value(_ key: String, _ fallback: Int) -> Binding<Int> {
        Binding(get: { link.board[key] ?? fallback }, set: { link.setBoard(key, $0) })
    }

    private func soundBit(_ bit: Int) -> Binding<Bool> {
        Binding(get: { (link.board["sound"] ?? 9) & bit != 0 },
                set: { on in
                    let m = link.board["sound"] ?? 9
                    link.setBoard("sound", on ? m | bit : m & ~bit)
                })
    }

    var body: some View {
        Section {
            Picker(L("时钟表盘", "Clock Face"), selection: value("face", 0)) {
                Text(L("简洁", "Simple")).tag(0)
                Text(L("信息", "Info")).tag(2)
            }
            Picker(selection: value("rotation", 1)) {
                Text(L("自动", "Auto")).tag(0)
                Text("0°").tag(1)
                Text("90°").tag(2)
                Text("180°").tag(3)
                Text("270°").tag(4)
            } label: {
                HelpLabel(L("屏幕方向", "Rotation"), help: L("充电口挡着时选一个固定方向。自动旋转不对的话，在板子的 系统 → 按键测试与方向校准 里校准。",
                                                           "Pick a fixed angle if the charging port is in the way. If auto rotation is wrong, calibrate it on the board under System → Keys & rotation."))
            }
            LabeledContent(L("亮度", "Brightness")) {
                Slider(value: $brightness, in: 20...255) { editing in
                    if !editing { link.setBoard("brightness", Int(brightness)) }
                }
                .frame(maxWidth: 260)
            }
            Picker(L("多久没操作回到时钟", "Dim to Clock After"), selection: value("dim", 60)) {
                Text(L("30 秒", "30 seconds")).tag(30)
                Text(L("1 分钟", "1 minute")).tag(60)
                Text(L("2 分钟", "2 minutes")).tag(120)
                Text(L("5 分钟", "5 minutes")).tag(300)
                Text(L("从不", "Never")).tag(0)
            }
            Toggle(L("用电池时时钟常亮", "Keep Clock On (Battery)"), isOn: Binding(get: { (link.board["standbyOnBattery"] ?? 0) != 0 },
                                                                               set: { link.setBoard("standbyOnBattery", $0 ? 1 : 0) }))
            Picker(L("夜间熄屏", "Night Mode"), selection: value("night", 0)) {
                Text(L("关", "Off")).tag(0)
                Text(L("22–7 点", "10pm–7am")).tag(1)
                Text(L("23–7 点", "11pm–7am")).tag(2)
                Text(L("0–8 点", "12am–8am")).tag(3)
            }
            Picker(selection: value("sit", 0)) {
                Text(L("关", "Off")).tag(0)
                Text(L("30 分钟", "30 min")).tag(30)
                Text(L("45 分钟", "45 min")).tag(45)
                Text(L("60 分钟", "60 min")).tag(60)
                Text(L("90 分钟", "90 min")).tag(90)
            } label: {
                HelpLabel(L("久坐提醒", "Break Reminder"), help: L("电脑没锁屏的时候，每隔这么久提醒你起来活动一下（夜间不提醒，锁屏后重新计时）。",
                                                               "While your Mac is unlocked, reminds you to get up every so often (not at night; restarts after you lock)."))
            }
            LabeledContent(L("提示音", "Sounds")) {
                VStack(alignment: .leading, spacing: 4) {
                    Toggle(L("Agent 需要你确认", "Agent needs you"), isOn: soundBit(8))
                    Toggle(L("Agent 回答完了", "Agent is done"), isOn: soundBit(16))
                    Toggle(L("服务器任务完成", "Job finished"), isOn: soundBit(2))
                    Toggle(L("任务失败、磁盘警告", "Job failed, disk warning"), isOn: soundBit(4))
                    Toggle(L("计时器、久坐提醒", "Timer, break reminder"), isOn: soundBit(1))
                }
                .toggleStyle(.checkbox)
            }
            Picker(L("音量", "Volume"), selection: value("volume", 1)) {
                Text(L("小", "Low")).tag(0)
                Text(L("中", "Medium")).tag(1)
                Text(L("大", "High")).tag(2)
            }
            .pickerStyle(.segmented)
        } header: { SettingsHeader(title: L("副屏上的设置", "On the Display"), symbol: "slider.horizontal.3", tint: .indigo) } footer: {
            Text(link.connected ? L("改了立即同步到板子；在板子上改的也会显示在这里。", "Changes sync to the board right away; changes made on the board show up here too.")
                                : L("连上副屏后才能修改。", "Connect the display to change these."))
                .font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .disabled(!link.connected)
        .onAppear { brightness = Double(link.board["brightness"] ?? 180) }
        .onChange(of: link.board["brightness"]) { _, v in if let v { brightness = Double(v) } }
    }
}

/// 日程、截止日期、天气。
private struct ExtrasSections: View {
    @ObservedObject var store: ConfigStore
    var link: DisplayLink?
    @State private var calendarNote = ""
    @State private var city = ""

    var body: some View {
        Section {
            Toggle(isOn: Binding(get: { store.settings.displayCalendar }, set: { on in
                guard on else { store.settings.displayCalendar = false; return }
                if DisplayExtras.calendarAuthorized { store.settings.displayCalendar = true; return }
                link?.extras.requestCalendarAccess { granted in
                    store.settings.displayCalendar = granted
                    calendarNote = granted ? "" : L("没有得到日历权限：到 系统设置 → 隐私与安全性 → 日历 里允许 ServerIsland。",
                                                    "No calendar access: allow ServerIsland in System Settings → Privacy & Security → Calendars.")
                }
            })) {
                HelpLabel(L("显示日历里的日程", "Show Calendar Events"),
                          help: L("读 macOS 日历里今天起一个月的日程（只读），显示在副屏的日历和时钟上。第一次打开时系统会问你要不要授权。",
                                  "Reads the next month of events from macOS Calendar (read-only) for the display's calendar and clock. macOS asks for permission the first time."))
            }
            if !calendarNote.isEmpty { Text(calendarNote).font(.caption).foregroundStyle(.orange) }
            ForEach($store.settings.deadlines) { $d in
                HStack {
                    TextField(L("名称", "Name"), text: $d.title).textFieldStyle(.roundedBorder)
                    DatePicker("", selection: $d.date, displayedComponents: .date).labelsHidden()
                    Button { store.settings.deadlines.removeAll { $0.id == d.id } } label: { Image(systemName: "minus.circle.fill") }
                        .buttonStyle(.plain).foregroundStyle(.secondary)
                }
            }
            Button {
                store.settings.deadlines.append(Deadline(title: L("截止日期", "Deadline"), date: Date().addingTimeInterval(14 * 86400)))
            } label: { Label(L("添加截止日期", "Add Deadline"), systemImage: "plus.circle") }
        } header: { SettingsHeader(title: L("日程与截止日期", "Events & Deadlines"), symbol: "calendar", tint: .red) } footer: {
            Text(L("截止日期会在副屏的时钟和日历上倒计时，比如\"投稿还剩 12 天\"。", "Deadlines count down on the display's clock and calendar, e.g. \"Paper due in 12 days\"."))
                .font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }

        Section {
            LabeledContent {
                TextField(L("城市，例如 北京", "City, e.g. Beijing"), text: $city)
                    .textFieldStyle(.roundedBorder).frame(maxWidth: 220)
                    .onSubmit { store.settings.weatherCity = city }
            } label: {
                HelpLabel(L("天气城市", "Weather City"),
                          help: L("填了以后副屏的时钟上显示当前天气和今天的最高最低温（来自 Open-Meteo，每 30 分钟查一次，只发送城市名）。留空就不查。",
                                  "Shows current weather and today's high/low on the display's clock (from Open-Meteo, every 30 minutes; only the city name is sent). Leave empty to turn off."))
            }
        } header: { SettingsHeader(title: L("天气", "Weather"), symbol: "cloud.sun.fill", tint: .cyan) }
        .onAppear { city = store.settings.weatherCity }
    }
}
