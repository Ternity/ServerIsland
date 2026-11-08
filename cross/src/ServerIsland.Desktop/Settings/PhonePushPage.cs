using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ServerIsland.Core;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 设置 → 手机推送：企业微信群机器人的 key、推送哪些任务、消息详细程度、勿扰。与 Swift 版 PhonePushSettingsView 对应。
public sealed class PhonePushPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine)
{
    public static readonly Color Tint = Color.Parse("#34C759");
    /// 正在"更换"key（跨页面重建保留）。
    static bool _editing;
    static bool _testing;
    static string? _inputError;

    PhonePush Push => engine.PhonePush;
    PhonePushSettings S => store.Settings.PhonePush;
    void Touch() => store.Touch();

    /// 发送结果、key 被拒绝时刷新页面。返回取消订阅的动作。
    public Action Attach()
    {
        void OnChanged() => Dispatcher.UIThread.Post(() => { window.SidebarChanged(); window.RefreshPage(); });
        Push.Changed += OnChanged;
        return () => Push.Changed -= OnChanged;
    }

    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(k.Text(L("手机推送", "Phone Push"), 22, FontWeight.Bold).Margin2(2, 0, 0, 16));
        var s = S;

        // —— 连接 ——
        var rows = new List<Control?>
        {
            k.ToggleRow(L("推送到企业微信", "Push to WeCom"),
                L("任务结束、失败时，由这台电脑发到企业微信群机器人，手机上的企业微信（或开了微信插件的微信）就会收到。手机不用连服务器；电脑睡眠时不会推送。",
                  "When jobs finish or fail, this computer posts to a WeCom group bot and your phone gets it in WeCom (or WeChat, with the WeChat plugin). Your phone never connects to the servers; nothing is sent while this computer sleeps."),
                s.Enabled, v => { s.Enabled = v; Touch(); window.SidebarChanged(); }),
            KeyRow(),
        };
        if (Push.HasKey && !_editing)
        {
            var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (Push.Status is { } st)
                box.Children.Add(k.Text((st.Ok ? "✓ " : "⚠ ") + Format.Relative(st.Date) + " · " + st.Message, 12.5,
                    c: st.Ok ? Color.Parse("#2EAD87") : Color.Parse("#E8890C")).VAlign(VerticalAlignment.Center));
            var test = k.Button(_testing ? L("发送中…", "Sending…") : L("发送测试消息", "Send Test Message"), async () =>
            {
                _testing = true;
                window.RefreshPage();
                await Push.SendTest();
                _testing = false;
                window.RefreshPage();
            });
            test.IsEnabled = !_testing;
            box.Children.Add(test);
            rows.Add(k.Row(L("测试", "Test"), null, box));
        }
        if (Push.KeyRejected)
            rows.Add(k.Text("⚠ " + L("企业微信拒绝了这个 key，推送已暂停。请重新复制机器人的 Webhook 地址。",
                                     "WeCom rejected this key and pushing is paused. Copy the bot's webhook URL again."), 12.5, c: Color.Parse("#E8890C"), wrap: true));
        col.Children.Add(k.Section(L("手机推送", "Phone Push"), "phone", Tint, rows,
            L("获取 Webhook：手机企业微信 → 进一个群 → 右上角 ··· → 群机器人 → 添加 → 新创建一个机器人，复制它的 Webhook 地址粘贴到这里。个人也能免费创建企业（团队），不需要认证。\n",
              "Get a webhook: WeCom on your phone → open a group → ··· → Group Bots → Add → create a bot, then paste its webhook URL here. Anyone can create a free team; no verification needed.\n")
            + L("key 只存在系统凭据库里（Windows 凭据管理器 / Linux 的 Secret Service），不写进配置文件和日志。拿到 Webhook 地址的人都能往群里发消息，不要发给别人。",
                "The key is kept only in the system credential store (Windows Credential Manager / Secret Service on Linux), never in the settings file or logs. Anyone with the webhook URL can post to the group, so keep it private.")));

        // —— 推送内容 ——
        var kinds = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        kinds.Children.Add(Check(L("完成", "Finished"), s.Finished, v => s.Finished = v));
        kinds.Children.Add(Check(L("失败", "Failed"), s.Failed, v => s.Failed = v));
        kinds.Children.Add(Check(L("结果未知", "Unknown"), s.Unknown, v => s.Unknown = v));
        kinds.Children.Add(Check(L("开始运行", "Started"), s.Started, v => s.Started = v));
        var what = new List<Control?>
        {
            k.Row(L("推送哪些任务", "Push For"),
                L("建议只推送关注的任务：平时人在电脑前看刘海就够了；要离开、或者有重要的任务时，在刘海的任务行上点 ☆ 关注它。关注的任务结束后自动取消关注。手动监听的进程总是推送。\n“和刘海提醒一样”：跟着 设置 → 通用 → 提醒 的范围和种类走。",
                  "Starred only is recommended: at your desk the island is enough; before you leave, or for an important job, star it with the ☆ on its row. Stars are removed when the job ends. Watched processes always push.\n“Same as Island Alerts” follows the scope and kinds in Settings → General → Alerts."),
                k.Combo(Enum.GetValues<PushScope>().Select(v => (v, v.Label())), s.Scope, v => { s.Scope = v; Touch(); window.SidebarChanged(); }, 220)),
            k.Row(L("推送这些事件", "Push When"),
                L("结果未知：作业离开了队列，但查不到成功还是失败（例如集群没有开启作业记录）。开始运行：排队的作业开始运行。",
                  "Unknown: a job left the queue but its result couldn't be found (e.g. no job accounting). Started: a pending job begins running."), kinds),
            k.ToggleRow(L("服务器警告", "Server Warnings"),
                L("磁盘快满等服务器级的提醒，不受上面的任务范围影响。", "Server-level alerts such as a nearly full disk. Not affected by the job scope above."),
                s.ServerWarnings, v => { s.ServerWarnings = v; Touch(); }),
            k.ToggleRow(L("Agent（Claude Code / Codex / OpenCode）", "Agents (Claude Code / Codex / OpenCode)"),
                L("对话需要你确认、回答完了、出错时推送。Claude Code 和 OpenCode 需要先在下面装上钩子；Codex 不用装。",
                  "Push when a session needs your OK, is done, or hits an error. Claude Code and OpenCode need the hooks below; Codex needs nothing."),
                s.Agents, v => { s.Agents = v; Touch(); window.RefreshPage(); }),
        };
        if (s.Agents)
        {
            var ag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            ag.Children.Add(Check(L("需要你确认", "Needs you"), s.AgentAttention, v => s.AgentAttention = v));
            ag.Children.Add(Check(L("回答完了 / 出错", "Done / error"), s.AgentDone, v => s.AgentDone = v));
            what.Add(k.Row(L("Agent 事件", "Agent events"), null, ag));
        }
        col.Children.Add(k.Section(L("推送内容", "What to Push"), "list", Color.Parse("#0A84FF"), what,
            L("静音的服务器（卡片上的铃铛）不推送。", "Muted servers (the bell on a card) never push.")));
        if (s.Agents)
            col.Children.Add(new DisplayPage(window, k, store, engine).AgentSection(
                L("Claude Code、Codex、OpenCode 的对话需要你确认、回答完了时，会推送到手机。钩子调用的是正在运行的这个 ServerIsland；挪动或更新了程序位置后，点一下“更新”。",
                  "Your phone is told when a Claude Code, Codex or OpenCode session needs your OK or has finished. The hooks call this copy of ServerIsland; if you move or update it, click Update.")));

        // —— 消息 ——
        var msg = new List<Control?>
        {
            k.Row(L("消息内容", "Message"),
                L("详细：作业号、分区、资源、用时和上限、起止时间、失败原因、计算软件进度、目录（只显示最后两层）、费用（打开计费时）。简洁：只有标题和服务器。不包含用户名、主机地址和日志。",
                  "Detailed: job ID, partition, resources, run time vs. limit, start/end, failure reason, app progress, directory (last two levels), cost (with billing on). Brief: title and server only. Never includes usernames, host addresses or logs."),
                k.Combo(new[] { (true, L("详细", "Detailed")), (false, L("简洁", "Brief")) }, s.Detailed, v => { s.Detailed = v; Touch(); }, 120)),
            k.ToggleRow(L("勿扰时段不推送", "Pause During Quiet Hours"),
                L("时间在 设置 → 通用 → 提醒 里设置。", "Set the hours in Settings → General → Alerts."),
                s.FollowQuietHours, v => { s.FollowQuietHours = v; Touch(); window.RefreshPage(); }),
        };
        if (s.FollowQuietHours)
        {
            msg.Add(k.ToggleRow(L("失败的任务照常推送", "Still Push Failures"), null, s.FailuresDuringQuiet, v => { s.FailuresDuringQuiet = v; Touch(); }));
            msg.Add(k.Row(L("勿扰时段", "Quiet hours"), null,
                k.Text(store.Settings.QuietHours ? store.Settings.QuietLabel : L("未开启", "Off"), c: k.P.Secondary)));
        }
        col.Children.Add(k.Section(L("消息", "Messages"), "info", Color.Parse("#30B0C7"), msg,
            L("同一时间的多条提醒会攒 10 秒后合并成一条；每分钟最多 15 条。", "Alerts within 10 seconds are merged into one message; at most 15 messages a minute.")));
        return col;
    }

    CheckBox Check(string title, bool value, Action<bool> set)
    {
        var c = new CheckBox { Content = title, IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        c.IsCheckedChanged += (_, _) => { set(c.IsChecked == true); Touch(); };
        return c;
    }

    Control KeyRow()
    {
        if (Push.HasKey && !_editing)
        {
            var box = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            box.Children.Add(k.Text(L("已保存 ", "Saved ") + (Push.MaskedKey ?? "****"), c: k.P.Secondary, mono: true).VAlign(VerticalAlignment.Center));
            box.Children.Add(k.Button(L("更换…", "Change…"), () => { _editing = true; _inputError = null; window.RefreshPage(); }));
            box.Children.Add(k.Button(L("删除", "Remove"), async () =>
            {
                if (!await PromptDialog.Confirm(L("删除保存的 key？", "Remove the saved key?"),
                        L("之后不会再推送，直到重新填写。", "Nothing will be pushed until you add one again."), L("删除", "Remove"), L("取消", "Cancel"))) return;
                Push.RemoveKey();
                window.RefreshPage();
            }, danger: true));
            return k.Row(L("企业微信机器人", "WeCom bot"), null, box);
        }
        string input = "";
        var field = k.Field("", v => input = v, L("粘贴 Webhook 地址或 key", "Paste the webhook URL or key"), mono: true);
        field.PasswordChar = '•';
        field.MinWidth = 320;
        void Save()
        {
            if (PhonePush.ExtractKey(input) == null)
            {
                _inputError = L("认不出 key：应该是 https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=… 这样的地址",
                                "Not a valid key: expected https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…");
                window.RefreshPage();
                return;
            }
            if (!Push.SetKey(input))
            {
                _inputError = L("保存到凭据库失败（Linux 需要 secret-tool）", "Couldn't save to the credential store (Linux needs secret-tool)");
                window.RefreshPage();
                return;
            }
            _editing = false;
            _inputError = null;
            if (!S.Enabled) { S.Enabled = true; Touch(); }
            window.SidebarChanged();
            window.RefreshPage();
        }
        field.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Save(); };
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { field, k.Button(L("保存", "Save"), Save, accent: true) } };
        if (_editing) line.Children.Add(k.Button(L("取消", "Cancel"), () => { _editing = false; _inputError = null; window.RefreshPage(); }));
        var right = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, Children = { line } };
        if (_inputError is { } err) right.Children.Add(k.Text(err, 11.5, c: Color.Parse("#E5484D"), wrap: true));
        return k.Row(L("企业微信机器人", "WeCom bot"), null, right);
    }
}
