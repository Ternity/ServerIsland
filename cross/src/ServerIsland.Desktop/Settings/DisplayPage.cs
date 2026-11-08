using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ServerIsland.Core;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 设置 → 副屏（和 Mac 版 DisplaySettingsView.swift 对应）：连接、板子上的设置、Wi-Fi 与热点、截止日期、天气、Agent 提醒、AI 用量。
/// 只在通用页底部打开"外接副屏"以后才出现在侧栏。
public sealed class DisplayPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine)
{
    AppSettings S => store.Settings;
    DisplayLink? Link => engine.DisplayLink;
    string _key = "";

    /// Wi-Fi 表单：只留在内存里（页面重建时不丢），连接后清掉密码。电脑端不保存这些。
    static class NetForm
    {
        public static string Ssid = "", WifiPassword = "", ApName = "ServerIsland", ApPassword = "";
        public static bool Share, Remember;
    }

    /// 连接状态、板子设置、电量、配网状态变了就重建这一页（输入框的内容存在 NetForm 里，不会丢）。
    string Key() => Link is not { } l ? "" :
        $"{l.Connected}|{l.Firmware}|{string.Join(",", l.Board.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value))}|{l.Battery}|" +
        string.Join(",", l.Network.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));

    /// 订阅副屏的变化；返回取消订阅的动作（换页时调用）。
    public Action Attach()
    {
        if (Link is not { } l) return () => { };
        _key = Key();
        void OnChanged() => Dispatcher.UIThread.Post(() =>
        {
            window.SidebarChanged();
            var key = Key();
            if (key == _key) return;
            _key = key;
            window.RefreshPage();
        });
        l.Changed += OnChanged;
        return () => l.Changed -= OnChanged;
    }

    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(k.Text(L("副屏", "Display"), 22, FontWeight.Bold).Margin2(0, 0, 0, 18));
        var link = Link;
        var connected = link?.Connected == true;

        // —— 连接 ——
        Control status;
        if (connected)
        {
            var text = (link!.Current == DisplayLink.Transport.Ble ? L("已通过蓝牙连接", "Connected over Bluetooth") : L("已通过 USB 连接", "Connected over USB")) + (link.Firmware is { } fw ? L($" · 固件 {fw}", $" · firmware {fw}") : "");
            status = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new Icon("check", 13, Color.Parse("#2EAD87").B(), 2.6) { VerticalAlignment = VerticalAlignment.Center }, k.Text(text, 13, c: Color.Parse("#2EAD87")) },
            };
        }
        else status = k.Text(L("未连接：用 USB 线插上刷了 ServerIsland 固件的板子", "Not connected: plug in a board running the ServerIsland firmware over USB"), 13, c: k.P.Secondary);
        Control? battery = null;
        if (connected && link!.Battery is { Percent: >= 0 } b)
        {
            var low = !b.Usb && b.Percent <= 15;
            battery = k.Row(L("板子电量", "Board Battery"), null,
                k.Text($"{b.Percent}%" + (b.Charging ? L("，充电中", ", charging") : b.Usb ? L("，已插电", ", plugged in") : ""), 13,
                       c: low ? Color.Parse("#E8890C") : k.P.Secondary));
        }
        col.Children.Add(k.Section(L("连接", "Connection"), "display", Color.Parse("#14B8A6"), [
            k.Row(L("状态", "Status"), null, status),
            battery,
            link?.BluetoothSupported == true ? k.ToggleRow(L("也通过蓝牙连接", "Also Connect over Bluetooth"),
                L("拔掉 USB 时改用蓝牙（插着 USB 时仍然用 USB）。第一次连接时副屏上会显示 6 位配对码，在弹出的对话框里输入。板子上也要打开蓝牙（插着 USB 时打开这里会自动打开）。",
                  "Use Bluetooth when USB is unplugged (USB is still preferred). The first time, the display shows a 6-digit code to type into the dialog that pops up. Bluetooth must also be on on the board (turned on automatically if USB is connected)."),
                S.DisplayBluetooth, v => { S.DisplayBluetooth = v; store.Touch(); if (v) link.SetBoard("ble", 1); }) : null,
            k.ToggleRow(L("电脑锁屏时副屏熄屏", "Turn Off When the Computer Locks"),
                L("锁屏时副屏一起熄屏（期间的提醒照样记下来），解锁后自动亮起。Windows 和大多数 Linux 桌面（GNOME、KDE）都能检测到锁屏。",
                  "The display turns off while your computer is locked (alerts are still logged) and turns back on when you unlock. Works on Windows and most Linux desktops (GNOME, KDE)."),
                S.DisplayLockSync, v => { S.DisplayLockSync = v; store.Touch(); }),
        ], L("需要刷了 ServerIsland 副屏固件的 ESP32 板子（固件暂未公开）。Linux 版目前只支持 USB 连接。",
             "Requires an ESP32 board running the ServerIsland display firmware (not yet public). The Linux version connects over USB only for now.")));

        if (link != null)
        {
            col.Children.Add(BoardSection(link));
            col.Children.Add(NetworkSection(link));
        }
        col.Children.Add(DeadlineSection());
        col.Children.Add(WeatherSection());
        col.Children.Add(AgentSection());
        col.Children.Add(k.Section(L("AI 用量", "AI Usage"), "chart", Color.Parse("#34C759"), [
            k.Row(L("AI 用量", "AI Usage"),
                L("只读 CC Switch 的本地记录（~/.cc-switch），每 3 分钟更新副屏上的调用、token 和费用。",
                  "Reads CC Switch's local log (~/.cc-switch, read-only) and updates calls, tokens and cost on the display every 3 minutes."),
                k.Text(AIUsage.Available ? L("已找到 CC Switch，自动显示", "CC Switch found, shown automatically")
                                         : L("没有安装 CC Switch，不显示", "CC Switch not installed"), 13, c: k.P.Secondary)),
        ]));
        return col;
    }

    /// 板子上的设置：读的是板子发来的值，改了立即发给板子。没连上时整组禁用。
    Control BoardSection(DisplayLink link)
    {
        int V(string key, int fallback) => link.Board.TryGetValue(key, out var v) ? v : fallback;
        void Set(string key, int v) => link.SetBoard(key, v);
        var hours = Enumerable.Range(0, 24).Select(h => (h, $"{h:00}:00")).ToList();
        var night = V("night", 0) != 0;
        var brightness = new Slider { Minimum = 20, Maximum = 255, Value = V("brightness", 180), Width = 220 };
        brightness.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase_Value) return;
            // 拖动时不停地发：合并 0.3 秒内的变化。
            var v = (int)brightness.Value;
            _brightnessVersion++;
            var version = _brightnessVersion;
            DispatcherTimer.RunOnce(() => { if (version == _brightnessVersion) Set("brightness", v); }, TimeSpan.FromMilliseconds(300));
        };
        Control SoundBit(string title, int bit, string key = "sound", int fallback = 9) => k.ToggleRow(title, null, (V(key, fallback) & bit) != 0, on =>
        {
            var m = V(key, fallback);
            Set(key, on ? m | bit : m & ~bit);
        });
        var section = k.Section(L("副屏上的设置", "On the Display"), "sliders", Color.Parse("#5856D6"), [
            k.Row(L("时钟表盘", "Clock Face"), null, k.Combo([(0, L("简洁", "Simple")), (2, L("信息", "Info"))], V("face", 0), v => Set("face", v), 140)),
            k.Row(L("屏幕方向", "Rotation"),
                L("充电口挡着时选一个固定方向。自动旋转不对的话，在板子的 系统 → 按键测试与方向校准 里校准。",
                  "Pick a fixed angle if the charging port is in the way. If auto rotation is wrong, calibrate it on the board under System → Keys & rotation."),
                k.Combo([(0, L("自动", "Auto")), (1, "0°"), (2, "90°"), (3, "180°"), (4, "270°")], V("rotation", 1), v => Set("rotation", v), 140)),
            k.Row(L("亮度", "Brightness"), null, brightness),
            k.Row(L("多久没操作回到时钟", "Dim to Clock After"), null,
                k.Combo([(30, L("30 秒", "30 seconds")), (60, L("1 分钟", "1 minute")), (120, L("2 分钟", "2 minutes")), (300, L("5 分钟", "5 minutes")), (0, L("从不", "Never"))],
                        V("dim", 60), v => Set("dim", v), 140)),
            k.ToggleRow(L("插电时不调暗", "Stay Bright on Power"),
                L("插着电时，没操作回到时钟后也保持上面的亮度（最高 40%，减少 OLED 老化）；用电池时照旧调暗省电。需要固件 0.4.2。",
                  "On power, the clock keeps the brightness above after it returns from inactivity (capped at 40% to limit OLED wear); on battery it still dims to save power. Needs firmware 0.4.2."),
                V("brightOnPower", 1) != 0, v => Set("brightOnPower", v ? 1 : 0)),
            k.Row(L("插电待机时显示", "On Power, When Idle"),
                L("插着电、没操作一段时间后显示什么。表盘：你选的表盘，会轻微挪动防烧屏。手表：Tako、大时间和三个小圆环。信息条：大时间和三条横条（最快任务、运行 / 排队、AI 今天）。进度环：外圈是最快任务的进度，写着几点结束。Tako 说：大 Tako 加一两句话。轮换：这四种每 10 分钟换一种。屏保整个画面会缓慢漂移，摸一下回到表盘。需要固件 0.4.6。",
                  "What the display shows on power after a while without touches. Clock face: your chosen face, shifted slightly to avoid burn-in. Watch: Tako, a large clock and three small rings. Bars: a large clock and three bars (furthest job, running / queued, AI today). Ring: the outer ring is the furthest job's progress, with its end time. Tako says: a big Tako and a sentence or two. Rotate: cycles through these four every 10 minutes. Screensavers drift slowly; tap to return. Needs firmware 0.4.6."),
                k.Combo([(0, L("表盘", "Clock face")), (1, L("手表", "Watch")), (2, L("信息条", "Bars")), (3, L("进度环", "Ring")), (4, L("Tako 说", "Tako says")), (5, L("轮换", "Rotate"))], V("saver", 0), v => Set("saver", v), 140)),
            k.ToggleRow(L("装饰效果（任务完成撒彩带、下雨下雪）", "Effects (Confetti, Rain, Snow)"), null, V("effects", 1) != 0, v => Set("effects", v ? 1 : 0)),
            k.ToggleRow(L("用电池时时钟常亮", "Keep Clock On (Battery)"), null, V("standbyOnBattery", 0) != 0, v => Set("standbyOnBattery", v ? 1 : 0)),
            k.ToggleRow(L("夜间熄屏", "Night Mode"),
                L("这段时间里调暗后直接熄屏，任务提醒不亮屏也不响（会记进提醒记录）；计时器照常响。",
                  "During these hours the screen turns off instead of dimming and job alerts stay silent (they are still logged). Timers still ring."),
                night, v => Set("night", v ? 1 : 0)),
            night ? k.Row(L("夜间时段", "Night Hours"), null, new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children =
                {
                    k.Text(L("从", "From"), 12.5, c: k.P.Secondary), k.Combo(hours, V("nightFrom", 23), v => Set("nightFrom", v), 90),
                    k.Text(L("到", "To"), 12.5, c: k.P.Secondary), k.Combo(hours, V("nightTo", 7), v => Set("nightTo", v), 90),
                },
            }) : null,
            night ? k.ToggleRow(L("插电时显示很暗的小时钟", "Dim Small Clock on Power"),
                L("夜间插着电时不熄屏，只显示一个很暗的小时间，每分钟换个位置。需要固件 0.4.7。",
                  "At night on power, shows a very dim small clock that moves every minute instead of turning off. Needs firmware 0.4.7."),
                V("nightClock", 0) != 0, v => Set("nightClock", v ? 1 : 0)) : null,
            k.Row(L("久坐提醒", "Break Reminder"),
                L("电脑没锁屏的时候，每隔这么久提醒你起来活动一下（夜间不提醒，锁屏后重新计时）。",
                  "While your computer is unlocked, reminds you to get up every so often (not at night; restarts after you lock)."),
                k.Combo([(0, L("关", "Off")), (30, L("30 分钟", "30 min")), (45, L("45 分钟", "45 min")), (60, L("60 分钟", "60 min")), (90, L("90 分钟", "90 min"))],
                        V("sit", 0), v => Set("sit", v), 140)),
            SoundBit(L("全屏：Agent 需要你确认", "Full screen: agent needs you"), 8, "popup", 31),
            SoundBit(L("全屏：Agent 回答完了", "Full screen: agent is done"), 16, "popup", 31),
            SoundBit(L("全屏：服务器任务完成", "Full screen: job finished"), 2, "popup", 31),
            SoundBit(L("全屏：任务失败、磁盘警告", "Full screen: job failed, disk warning"), 4, "popup", 31),
            SoundBit(L("全屏：计时器、久坐、截止日期", "Full screen: timer, break, deadline"), 1, "popup", 31),
            k.Text(L("关闭全屏后仍记入提醒记录，声音单独设置", "Alerts stay in history; sounds are separate"), 12, c: k.P.Secondary),
            SoundBit(L("提示音：Agent 需要你确认", "Sound: agent needs you"), 8),
            SoundBit(L("提示音：Agent 回答完了", "Sound: agent is done"), 16),
            SoundBit(L("提示音：服务器任务完成", "Sound: job finished"), 2),
            SoundBit(L("提示音：任务失败、磁盘警告", "Sound: job failed, disk warning"), 4),
            SoundBit(L("提示音：计时器、久坐提醒", "Sound: timer, break reminder"), 1),
            k.Row(L("音量", "Volume"), null, k.Segmented([(0, L("小", "Low")), (1, L("中", "Medium")), (2, L("大", "High"))], V("volume", 1), v => Set("volume", v))),
        ], link.Connected ? L("改了立即同步到板子；在板子上改的也会显示在这里。", "Changes sync to the board right away; changes made on the board show up here too.")
                          : L("连上副屏后才能修改。", "Connect the display to change these."));
        section.IsEnabled = link.Connected;
        return section;
    }

    static int _brightnessVersion;
    static readonly AvaloniaProperty RangeBase_Value = Avalonia.Controls.Primitives.RangeBase.ValueProperty;

    /// Wi-Fi 与热点：板子连一个 Wi-Fi 并共享热点。需要网页登录的网络由手机连上热点后在手机上登录。
    Control NetworkSection(DisplayLink link)
    {
        var n = link.Network;
        var ready = link.Connected && n.Count > 0;
        var state = n.GetValueOrDefault("state");
        var statusText = state switch
        {
            "off" => L("已关闭", "Off"),
            "connecting" => L("正在连接 Wi-Fi", "Connecting to Wi-Fi"),
            "online" => n.GetValueOrDefault("ap_name", "") is { Length: > 0 } ap ? L($"已连接，热点“{ap}”已开启", $"Connected; hotspot “{ap}” is on") : L("已连接", "Connected"),
            "error" => L("连接失败", "Connection failed"),
            _ => L("需要用 USB 连接副屏（固件 0.3.9 及以上）", "Connect the display over USB (firmware 0.3.9 or later)"),
        };
        var errorText = n.GetValueOrDefault("error") switch
        {
            "no_config" => L("板子上还没有保存过 Wi-Fi，请在下面填写后连接。", "No Wi-Fi saved on the board yet; fill in the form below and connect."),
            "worker_unavailable" or "wifi_init_failed" => L("板子内存不足，Wi-Fi 没能启动。可以先关掉板子的蓝牙再试。", "The board ran out of memory starting Wi-Fi. Try turning off its Bluetooth."),
            "invalid_config" => L("请检查 Wi-Fi 名称和热点密码格式。", "Check the Wi-Fi name and hotspot password."),
            "wifi_retry" => L("尚未连上 Wi-Fi，正在重试。仅支持 2.4 GHz 的开放或个人密码网络。", "Retrying Wi-Fi; requires a 2.4 GHz open or personal-password network."),
            "hotspot_failed" => L("已连上 Wi-Fi，但热点启动失败。", "Connected, but the hotspot failed to start."),
            _ => "",
        };
        static int Bytes(string s) => System.Text.Encoding.UTF8.GetByteCount(s);
        bool Valid() => NetForm.Ssid.Length > 0 && Bytes(NetForm.Ssid) <= 32 && Bytes(NetForm.WifiPassword) <= 63 &&
                        (!NetForm.Share || (NetForm.ApName.Length > 0 && Bytes(NetForm.ApName) <= 32 && NetForm.ApPassword.Length is >= 8 and <= 63
                                            && NetForm.ApPassword.All(c => c is >= ' ' and <= '~')));
        Button connect = null!;
        TextBox Secret(string value, Action<string> set, string watermark)
        {
            var t = k.Field(value, v => { set(v); connect.IsEnabled = ready && Valid(); }, watermark);
            t.PasswordChar = '•';
            t.Width = 260;
            return t;
        }
        TextBox Plain(string value, Action<string> set, string watermark)
        {
            var t = k.Field(value, v => { set(v); connect.IsEnabled = ready && Valid(); }, watermark);
            t.Width = 260;
            return t;
        }
        var apRows = new StackPanel { Spacing = 8, IsVisible = NetForm.Share };
        apRows.Children.Add(k.Row(L("热点名称", "Hotspot Name"), null, Plain(NetForm.ApName, v => NetForm.ApName = v, "ServerIsland")));
        apRows.Children.Add(k.Row(L("热点密码", "Hotspot Password"), L("8–63 位英文字符", "8–63 ASCII characters"), Secret(NetForm.ApPassword, v => NetForm.ApPassword = v, "")));
        connect = k.Button(L("连接", "Connect"), () =>
        {
            link.ConfigureNetwork(new JsonObject
            {
                ["action"] = "connect", ["ssid"] = NetForm.Ssid, ["wifi_password"] = NetForm.WifiPassword, ["share"] = NetForm.Share,
                ["ap_name"] = NetForm.ApName, ["ap_password"] = NetForm.ApPassword, ["remember"] = NetForm.Remember,
            });
            NetForm.WifiPassword = ""; NetForm.ApPassword = "";
            window.RefreshPage();
        }, accent: true);
        connect.IsEnabled = ready && Valid();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                connect,
                k.Button(L("断开", "Disconnect"), () => link.ConfigureNetwork(new JsonObject { ["action"] = "off" })),
                k.Button(L("忘记配置", "Forget Configuration"), () =>
                {
                    link.ConfigureNetwork(new JsonObject { ["action"] = "forget" });
                    NetForm.WifiPassword = ""; NetForm.ApPassword = "";
                    window.RefreshPage();
                }),
            },
        };
        var form = new StackPanel
        {
            Spacing = 8, IsEnabled = ready,
            Children =
            {
                k.Row(L("Wi-Fi 名称", "Wi-Fi Name"), null, Plain(NetForm.Ssid, v => NetForm.Ssid = v, "")),
                k.Row(L("Wi-Fi 密码", "Wi-Fi Password"), L("开放网络留空", "Leave empty for open networks"), Secret(NetForm.WifiPassword, v => NetForm.WifiPassword = v, "")),
                k.ToggleRow(L("共享 Wi-Fi 热点", "Share Wi-Fi Hotspot"), null, NetForm.Share, v =>
                {
                    NetForm.Share = v;
                    apRows.IsVisible = v;
                    connect.IsEnabled = ready && Valid();
                }),
                apRows,
                k.ToggleRow(L("在板子上记住配置，重启后自动连接", "Remember on Board and Reconnect After Restart"), null, NetForm.Remember, v => NetForm.Remember = v),
                buttons,
            },
        };
        var errorLabel = k.Text(errorText, 11.5, c: Color.Parse("#E8890C"), wrap: true);
        errorLabel.IsVisible = errorText.Length > 0;
        return k.Section(L("Wi-Fi 与热点", "Wi-Fi & Hotspot"), "wifi", Color.Parse("#007AFF"), [
            k.Row(L("状态", "Status"), null, k.Text(statusText, 13, c: k.P.Secondary)),
            n.GetValueOrDefault("mac", "") is { Length: > 0 } mac ? k.Row(L("固定 MAC", "Fixed MAC"), null, new SelectableTextBlock { Text = mac, FontFamily = Hue.Mono, FontSize = 12, Foreground = k.P.Secondary.B() }) : null,
            n.GetValueOrDefault("ip", "") is { Length: > 0 } ip ? k.Row("IP", null, k.Text(ip, 13, c: k.P.Secondary)) : null,
            n.GetValueOrDefault("ap_ip", "") is { Length: > 0 } apIp ? k.Row(L("热点网关", "Hotspot Gateway"), null, k.Text(apIp, 13, c: k.P.Secondary)) : null,
            errorText.Length > 0 ? errorLabel : null,
            form,
        ], L("板子连上这个 Wi-Fi 后开启自己的热点。需要网页登录的网络（如校园网）：手机连上板子的热点，在手机浏览器里登录，板子和热点下的设备就都能上网；账号密码不经过 ServerIsland。仅支持 2.4 GHz、开放或个人密码的 Wi-Fi（不支持 eduroam）；板子使用芯片出厂的固定 MAC。Wi-Fi 密码只经 USB 发送，勾选记住后保存在板子的未加密 Flash 中，电脑端不保存。",
             "The board joins this Wi-Fi and opens its own hotspot. For networks with a web login (e.g. campus Wi-Fi), join the board's hotspot on your phone and log in in the phone's browser; the board and everything on the hotspot then get online. Your login never passes through ServerIsland. 2.4 GHz open or personal-password Wi-Fi only (no eduroam); the board uses the chip's fixed factory MAC. The Wi-Fi password is sent over USB only; if remembered it is stored in the board's unencrypted flash, never on this computer."));
    }

    Control DeadlineSection()
    {
        var rows = new List<Control?>();
        foreach (var d in S.Deadlines.ToList())
        {
            var item = d;
            var title = k.Field(item.Title, v => { item.Title = v; store.Touch(); }, L("名称", "Name"));
            title.Width = 220;
            var date = new CalendarDatePicker { SelectedDate = item.Date ?? DateTime.Today, Width = 150 };
            date.SelectedDateChanged += (_, _) =>
            {
                if (date.SelectedDate is not { } day) return;
                // 和 Mac 版一样只选日期；保留原来的时刻（默认是添加时的时刻）。
                var t = item.Date ?? DateTime.Now;
                item.Date = day.Date + t.TimeOfDay;
                store.Touch();
            };
            var remove = new Button { Content = new Icon("minus", 12, k.P.Secondary.B(), 2.4), Background = Brushes.Transparent, Padding = new Thickness(6) };
            ToolTip.SetTip(remove, L("删除", "Remove"));
            remove.Click += (_, _) => { S.Deadlines.RemoveAll(x => x.Id == item.Id); store.Touch(); window.RefreshPage(); };
            rows.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { title, date, remove } });
        }
        rows.Add(k.Button(L("添加截止日期", "Add Deadline"), () =>
        {
            S.Deadlines.Add(new Deadline { Title = L("截止日期", "Deadline"), Date = DateTime.Now.AddDays(14) });
            store.Touch();
            window.RefreshPage();
        }));
        return k.Section(L("截止日期", "Deadlines"), "calendar", Color.Parse("#FF3B30"), rows,
            L("截止日期会在副屏的时钟和日历上倒计时，比如\"投稿还剩 12 天\"。（系统日历里的日程只有 Mac 版能读。）",
              "Deadlines count down on the display's clock and calendar, e.g. \"Paper due in 12 days\". (Calendar events are read on the Mac version only.)"));
    }

    Control WeatherSection()
    {
        var city = new TextBox { Text = S.WeatherCity, Watermark = L("城市，例如 北京", "City, e.g. Beijing"), Width = 220 };
        void Apply()
        {
            var v = (city.Text ?? "").Trim();
            if (v == S.WeatherCity) return;
            S.WeatherCity = v;
            store.Touch();
        }
        city.LostFocus += (_, _) => Apply();
        city.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Apply(); };
        return k.Section(L("天气", "Weather"), "cloud", Color.Parse("#32ADE6"), [
            k.Row(L("天气城市", "Weather City"),
                L("填了以后副屏的时钟上显示当前天气和今天的最高最低温（来自 Open-Meteo，每 30 分钟查一次，只发送城市名）。留空就不查。",
                  "Shows current weather and today's high/low on the display's clock (from Open-Meteo, every 30 minutes; only the city name is sent). Leave empty to turn off."),
                city),
        ]);
    }

    /// 一键安装 / 卸载 Claude Code 的钩子和 OpenCode 的插件；Codex 不用装（直接读它的会话记录）。手机推送页也用（换一句说明）。
    public Control AgentSection(string? footer = null)
    {
        var error = k.Status(Color.Parse("#E5484D"));
        Control Row(string name, bool available, bool installed, bool current, Func<string?> update, Func<string?> toggle, string help)
        {
            Control right;
            if (!available) right = k.Text(L("未安装", "Not installed"), 13, c: k.P.Secondary);
            else
            {
                var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                if (installed)
                {
                    box.Children.Add(k.Text(current ? L("已开启", "On") : L("需要更新", "Update needed"), 13,
                                            c: current ? Color.Parse("#2EAD87") : Color.Parse("#E8890C")));
                    if (!current) box.Children.Add(k.Button(L("更新", "Update"), () => { error.Text = update() ?? ""; if (error.Text.Length == 0) window.RefreshPage(); }));
                }
                box.Children.Add(k.Button(installed ? L("卸载", "Uninstall") : L("安装", "Install"), () => { error.Text = toggle() ?? ""; if (error.Text.Length == 0) window.RefreshPage(); }));
                right = box;
            }
            return k.Row(L($"{name} 提醒", $"{name} Alerts"), help, right);
        }
        var claude = AgentHooks.ClaudePresent();
        var opencode = AgentHooks.OpenCodeInstalled;
        return k.Section(L("Agent 提醒", "Agent Alerts"), "terminal", Color.Parse("#FF9500"), [
            Row("Claude Code", AgentHooks.ClaudeAvailable, claude, AgentHooks.ClaudeInstalled() && AgentHooks.ClaudePointsHere(),
                () => AgentHooks.SetClaude(true), () => AgentHooks.SetClaude(!claude),
                L("在 ~/.claude/settings.json 里追加几条异步钩子（原有的配置不动，改之前备份为 settings.json.serverisland-backup）。对话回答完、需要你确认时，副屏会提醒。钩子从不替你批准或拒绝权限请求。",
                  "Adds a few async hooks to ~/.claude/settings.json (existing hooks are kept; a backup is saved as settings.json.serverisland-backup). The display then tells you when a reply is done or Claude needs your OK. The hooks never approve or deny anything.")),
            Row("OpenCode", AgentHooks.OpenCodeAvailable, opencode, AgentHooks.OpenCodeCurrent,
                () => AgentHooks.SetOpenCode(true), () => AgentHooks.SetOpenCode(!opencode),
                L("在 ~/.config/opencode/plugins/ 里放一个小插件，会话空闲、需要确认时通知副屏。卸载会删除这个文件。",
                  "Puts a small plugin in ~/.config/opencode/plugins/ that tells the display when a session is idle or needs your OK. Uninstall deletes it.")),
            AgentHooks.CodexAvailable ? k.Row("Codex",
                L("Codex 的提醒程序只能设一个（常被别的工具占用），所以直接读 ~/.codex/sessions 里的会话记录，只读。",
                  "Codex allows only one notify program, so ServerIsland reads ~/.codex/sessions instead (read-only)."),
                k.Text(L("自动读取会话记录，无需安装", "Read automatically, nothing to install"), 13, c: k.P.Secondary)) : null,
            error,
        ], footer ?? L("Claude Code、Codex、OpenCode 的对话需要你确认、回答完了时，副屏会提醒。钩子调用的是正在运行的这个 ServerIsland；挪动或更新了程序位置后，点一下“更新”。",
             "The display tells you when a Claude Code, Codex or OpenCode session needs your OK or has finished. The hooks call this copy of ServerIsland; if you move or update it, click Update."));
    }
}
