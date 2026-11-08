import SwiftUI

/// 设置 → 手机推送：企业微信群机器人的 key、推送哪些任务、消息详细程度、勿扰。
struct PhonePushSettingsView: View {
    @ObservedObject var store: ConfigStore
    @ObservedObject var push: PhonePush
    @State private var input = ""
    @State private var inputError: String?
    @State private var editing = false
    @State private var testing = false
    @State private var confirmRemove = false

    private var s: Binding<PhonePushSettings> { $store.settings.phonePush }

    var body: some View {
        Form {
            Section {
                Toggle(isOn: s.enabled) {
                    HelpLabel(L("推送到企业微信", "Push to WeCom"),
                              help: L("任务结束、失败时，由这台电脑发到企业微信群机器人，手机上的企业微信（或开了微信插件的微信）就会收到。手机不用连服务器；电脑睡眠时不会推送。",
                                      "When jobs finish or fail, this computer posts to a WeCom group bot and your phone gets it in WeCom (or WeChat, with the WeChat plugin). Your phone never connects to the servers; nothing is sent while this computer sleeps."))
                }
                keyRow
                if push.hasKey && !editing {
                    LabeledContent(L("测试", "Test")) {
                        HStack(spacing: 8) {
                            if let st = push.status {
                                Image(systemName: st.ok ? "checkmark.circle.fill" : "exclamationmark.triangle.fill")
                                    .foregroundStyle(st.ok ? .green : .orange)
                                Text(Format.relative(st.date) + " · " + st.message)
                                    .foregroundStyle(.secondary).lineLimit(1).truncationMode(.tail)
                            }
                            Button(testing ? L("发送中…", "Sending…") : L("发送测试消息", "Send Test Message")) {
                                testing = true
                                Task { await push.sendTest(); testing = false }
                            }
                            .disabled(testing)
                        }
                    }
                }
                if push.keyRejected {
                    Label(L("企业微信拒绝了这个 key，推送已暂停。请重新复制机器人的 Webhook 地址。",
                            "WeCom rejected this key and pushing is paused. Copy the bot's webhook URL again."),
                          systemImage: "exclamationmark.triangle.fill")
                        .foregroundStyle(.orange).font(.callout)
                }
            } header: {
                SettingsHeader(title: L("手机推送", "Phone Push"), symbol: "iphone.radiowaves.left.and.right", tint: .green)
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    footnote(L("获取 Webhook：手机企业微信 → 进一个群 → 右上角 ··· → 群机器人 → 添加 → 新创建一个机器人，复制它的 Webhook 地址粘贴到这里。个人也能免费创建企业（团队），不需要认证。",
                           "Get a webhook: WeCom on your phone → open a group → ··· → Group Bots → Add → create a bot, then paste its webhook URL here. Anyone can create a free team; no verification needed."))
                    footnote(L("key 只存在系统钥匙串里，不写进配置文件和日志。拿到 Webhook 地址的人都能往群里发消息，不要发给别人。",
                           "The key is kept only in the system keychain, never in the settings file or logs. Anyone with the webhook URL can post to the group, so keep it private."))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                Picker(selection: s.scope) {
                    ForEach(PushScope.allCases) { Text($0.label).tag($0) }
                } label: {
                    HelpLabel(L("推送哪些任务", "Push For"),
                              help: L("建议只推送关注的任务：平时人在电脑前看刘海就够了；要离开、或者有重要的任务时，在刘海的任务行上点 ☆ 关注它。关注的任务结束后自动取消关注。手动监听的进程总是推送。\n“和刘海提醒一样”：跟着 设置 → 通用 → 提醒 的范围和种类走。",
                                      "Starred only is recommended: at your desk the island is enough; before you leave, or for an important job, star it with the ☆ on its row. Stars are removed when the job ends. Watched processes always push.\n“Same as Island Alerts” follows the scope and kinds in Settings → General → Alerts."))
                }
                LabeledContent {
                    HStack(spacing: 14) {
                        Toggle(L("完成", "Finished"), isOn: s.finished)
                        Toggle(L("失败", "Failed"), isOn: s.failed)
                        Toggle(L("结果未知", "Unknown"), isOn: s.unknown)
                        Toggle(L("开始运行", "Started"), isOn: s.started)
                    }
                    .toggleStyle(.checkbox)
                } label: {
                    HelpLabel(L("推送这些事件", "Push When"),
                              help: L("结果未知：作业离开了队列，但查不到成功还是失败（例如集群没有开启作业记录）。开始运行：排队的作业开始运行。",
                                      "Unknown: a job left the queue but its result couldn't be found (e.g. no job accounting). Started: a pending job begins running."))
                }
                Toggle(isOn: s.serverWarnings) {
                    HelpLabel(L("服务器警告", "Server Warnings"),
                              help: L("磁盘快满等服务器级的提醒，不受上面的任务范围影响。", "Server-level alerts such as a nearly full disk. Not affected by the job scope above."))
                }
                Toggle(isOn: s.agents) {
                    HelpLabel(L("Agent（Claude Code / Codex / OpenCode）", "Agents (Claude Code / Codex / OpenCode)"),
                              help: L("对话需要你确认、回答完了、出错时推送。Claude Code 和 OpenCode 需要先在下面装上钩子；Codex 不用装。",
                                      "Push when a session needs your OK, is done, or hits an error. Claude Code and OpenCode need the hooks below; Codex needs nothing."))
                }
                if store.settings.phonePush.agents {
                    LabeledContent(L("Agent 事件", "Agent events")) {
                        HStack(spacing: 14) {
                            Toggle(L("需要你确认", "Needs you"), isOn: s.agentAttention)
                            Toggle(L("回答完了 / 出错", "Done / error"), isOn: s.agentDone)
                        }
                        .toggleStyle(.checkbox)
                    }
                    AgentHooksRows()
                }
            } header: {
                SettingsHeader(title: L("推送内容", "What to Push"), symbol: "line.3.horizontal.decrease.circle.fill", tint: .blue)
            } footer: {
                Text(L("静音的服务器（卡片上的铃铛）不推送。", "Muted servers (the bell on a card) never push."))
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }

            Section {
                Picker(selection: s.detailed) {
                    Text(L("详细", "Detailed")).tag(true)
                    Text(L("简洁", "Brief")).tag(false)
                } label: {
                    HelpLabel(L("消息内容", "Message"),
                              help: L("详细：作业号、分区、资源、用时和上限、起止时间、失败原因、计算软件进度、目录（只显示最后两层）、费用（打开计费时）。简洁：只有标题和服务器。不包含用户名、主机地址和日志。",
                                      "Detailed: job ID, partition, resources, run time vs. limit, start/end, failure reason, app progress, directory (last two levels), cost (with billing on). Brief: title and server only. Never includes usernames, host addresses or logs."))
                }
                Toggle(isOn: s.followQuietHours) {
                    HelpLabel(L("勿扰时段不推送", "Pause During Quiet Hours"),
                              help: L("时间在 设置 → 通用 → 提醒 里设置。", "Set the hours in Settings → General → Alerts."))
                }
                if store.settings.phonePush.followQuietHours {
                    Toggle(L("失败的任务照常推送", "Still Push Failures"), isOn: s.failuresDuringQuiet)
                    LabeledContent(L("勿扰时段", "Quiet hours")) {
                        Text(store.settings.quietHours ? store.settings.quietLabel : L("未开启", "Off"))
                            .foregroundStyle(.secondary)
                    }
                }
            } header: {
                SettingsHeader(title: L("消息", "Messages"), symbol: "text.bubble.fill", tint: .teal)
            } footer: {
                Text(L("同一时间的多条提醒会攒 10 秒后合并成一条；每分钟最多 15 条。", "Alerts within 10 seconds are merged into one message; at most 15 messages a minute."))
                    .font(.caption).foregroundStyle(.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .formStyle(.grouped)
        .navigationTitle(L("手机推送", "Phone Push"))
    }

    private func footnote(_ text: String) -> some View {
        Text(text)
            .font(.caption).foregroundStyle(.secondary)
            .multilineTextAlignment(.leading)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    @ViewBuilder
    private var keyRow: some View {
        if push.hasKey && !editing {
            LabeledContent(L("企业微信机器人", "WeCom bot")) {
                HStack(spacing: 8) {
                    Text(L("已保存 ", "Saved ") + (push.maskedKey ?? "****")).foregroundStyle(.secondary).monospaced()
                    Button(L("更换…", "Change…")) { input = ""; inputError = nil; editing = true }
                    Button(L("删除", "Remove"), role: .destructive) { confirmRemove = true }
                        .confirmationDialog(L("删除保存的 key？", "Remove the saved key?"), isPresented: $confirmRemove) {
                            Button(L("删除", "Remove"), role: .destructive) { push.removeKey() }
                        } message: {
                            Text(L("之后不会再推送，直到重新填写。", "Nothing will be pushed until you add one again."))
                        }
                }
            }
        } else {
            LabeledContent(L("企业微信机器人", "WeCom bot")) {
              VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    SecureField("", text: $input, prompt: Text(L("粘贴 Webhook 地址或 key", "Paste the webhook URL or key")))
                        .labelsHidden()
                        .textFieldStyle(.roundedBorder)
                        .onSubmit(save)
                    Button(L("保存", "Save"), action: save).disabled(input.trimmingCharacters(in: .whitespaces).isEmpty)
                    if editing { Button(L("取消", "Cancel")) { editing = false; inputError = nil } }
                }
                if let e = inputError { Text(e).font(.caption).foregroundStyle(.red) }
              }
            }
        }
    }

    private func save() {
        guard PhonePush.extractKey(input) != nil else {
            inputError = L("认不出 key：应该是 https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=… 这样的地址", "Not a valid key: expected https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=…")
            return
        }
        if push.setKey(input) {
            input = ""
            inputError = nil
            editing = false
            if !store.settings.phonePush.enabled { store.settings.phonePush.enabled = true }
        } else {
            inputError = L("保存到钥匙串失败", "Couldn't save to the keychain")
        }
    }
}
