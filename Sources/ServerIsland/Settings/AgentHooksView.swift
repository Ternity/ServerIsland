import SwiftUI

/// 设置 → 通用 → 刘海：USB 副屏下面的"Agent 提醒"几行。一键安装 / 卸载 Claude Code 的钩子和 OpenCode 的插件；
/// Codex 不用装（直接读它的会话记录）。
struct AgentHooksRows: View {
    @State private var claude = AgentHooks.claudePresent()
    @State private var claudeCurrent = AgentHooks.claudeInstalled()
    @State private var opencode = AgentHooks.opencodeInstalled
    @State private var opencodeCurrent = AgentHooks.opencodeCurrent
    @State private var error: String?

    var body: some View {
        row("Claude Code", available: AgentHooks.claudeAvailable, installed: claude, current: claudeCurrent, update: {
            error = AgentHooks.setClaude(installed: true)
            claude = AgentHooks.claudePresent()
            claudeCurrent = AgentHooks.claudeInstalled()
        },
            help: L("在 ~/.claude/settings.json 里追加几条异步钩子（原有的配置不动，改之前备份为 settings.json.serverisland-backup）。对话回答完、需要你确认时，副屏会提醒。钩子从不替你批准或拒绝权限请求。",
                    "Adds a few async hooks to ~/.claude/settings.json (existing hooks are kept; a backup is saved as settings.json.serverisland-backup). The display then tells you when a reply is done or Claude needs your OK. The hooks never approve or deny anything.")) {
            error = AgentHooks.setClaude(installed: !claude)
            claude = AgentHooks.claudePresent()
            claudeCurrent = AgentHooks.claudeInstalled()
        }
        row("OpenCode", available: AgentHooks.opencodeAvailable, installed: opencode, current: opencodeCurrent, update: {
            error = AgentHooks.setOpenCode(installed: true)
            opencode = AgentHooks.opencodeInstalled
            opencodeCurrent = AgentHooks.opencodeCurrent
        },
            help: L("在 ~/.config/opencode/plugins/ 里放一个小插件，会话空闲、需要确认时通知副屏。卸载会删除这个文件。",
                    "Puts a small plugin in ~/.config/opencode/plugins/ that tells the display when a session is idle or needs your OK. Uninstall deletes it.")) {
            error = AgentHooks.setOpenCode(installed: !opencode)
            opencode = AgentHooks.opencodeInstalled
            opencodeCurrent = AgentHooks.opencodeCurrent
        }
        if AgentHooks.codexAvailable {
            LabeledContent {
                Text(L("自动读取会话记录，无需安装", "Read automatically, nothing to install")).foregroundStyle(.secondary)
            } label: {
                HelpLabel("Codex", help: L("Codex 的提醒程序只能设一个（常被别的工具占用），所以直接读 ~/.codex/sessions 里的会话记录，只读。",
                                            "Codex allows only one notify program, so ServerIsland reads ~/.codex/sessions instead (read-only)."))
            }
        }
        if let error {
            Text(error).font(.caption).foregroundStyle(.red)
        }
    }

    @ViewBuilder
    private func row(_ name: String, available: Bool, installed: Bool, current: Bool, update: @escaping () -> Void, help: String, toggle: @escaping () -> Void) -> some View {
        LabeledContent {
            if !available {
                Text(L("未安装", "Not installed")).foregroundStyle(.secondary)
            } else {
                HStack(spacing: 10) {
                    if installed {
                        Label(current ? L("已开启", "On") : L("需要更新", "Update needed"), systemImage: current ? "checkmark.circle.fill" : "arrow.triangle.2.circlepath")
                            .foregroundStyle(current ? Color.green : Color.orange).labelStyle(.titleAndIcon)
                        if !current { Button(L("更新", "Update"), action: update) }
                    }
                    Button(installed ? L("卸载", "Uninstall") : L("安装", "Install"), action: toggle)
                }
            }
        } label: {
            HelpLabel(L("\(name) 提醒", "\(name) Alerts"), help: help)
        }
    }
}
