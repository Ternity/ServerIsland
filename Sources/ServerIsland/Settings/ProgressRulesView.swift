import SwiftUI

/// 设置 → 进度识别：内置软件的说明 + 用户自定义规则。
struct ProgressRulesView: View {
    @ObservedObject var store: ConfigStore
    @ObservedObject var engine: MonitorEngine
    @State private var editing: ProgressRule?
    @State private var isNew = false

    var body: some View {
        Form {
            Section {
                builtinRow("NEP / GNEP", files: "loss.out + nep.in / gnep.in",
                           note: L("代数 / epoch，目标取自 nep.in / gnep.in", "Generation / epoch; target from nep.in / gnep.in"))
                builtinRow("TorchNEP", files: "loss.out + output.log",
                           note: L("epoch，目标取自 output.log / nep.in；loss.out 可以在 output/ 等子目录里", "Epochs; target from output.log / nep.in; loss.out may be in a subfolder such as output/"))
                builtinRow("MACE", files: "logs/*.log",
                           note: L("epoch，目标是 max_num_epochs（取自 _debug.log）", "Epochs; target is max_num_epochs (from _debug.log)"))
                builtinRow("DeePMD", files: "lcurve.out",
                           note: L("步数，目标取自日志的 Start to train N steps 或输入 JSON 的 numb_steps", "Steps; target from \"Start to train N steps\" in the log or numb_steps in the input JSON"))
                builtinRow("GPUMD", files: "run.in + neighbor.out / thermo.out",
                           note: L("步数，目标是 run.in 里所有 run 之和", "Steps; target is the sum of run in run.in"))
                builtinRow("VASP", files: "INCAR + OSZICAR",
                           note: L("MD 显示百分比；结构优化显示离子步，单点显示电子步", "MD shows a percentage; relaxation shows ionic steps, SCF shows electronic steps"))
                builtinRow("LAMMPS", files: "log.lammps",
                           note: L("最后一段 run 的步数；minimize 等不常见写法不显示", "Steps of the last run; minimize and unusual setups are skipped"))
            } header: {
                SettingsHeader(title: L("内置识别", "Built-in"), symbol: "atom", tint: .teal)
            } footer: {
                Text(L("按任务的工作目录里有哪些文件判断。格式和预期不一致时不显示进度。",
                       "Detected from files in the job's working directory. Nothing is shown when the format isn't as expected."))
                    .font(.caption).foregroundStyle(.secondary)
            }

            Section {
                if store.settings.progressRules.isEmpty {
                    Text(L("还没有规则。添加后，所有服务器都会按它识别进度。", "No rules yet. Rules apply to every server."))
                        .foregroundStyle(.secondary)
                }
                ForEach($store.settings.progressRules) { $rule in
                    ruleRow($rule)
                }
                .onMove { store.settings.progressRules.move(fromOffsets: $0, toOffset: $1) }
                HStack {
                    Menu {
                        Button(L("空白规则", "Blank Rule")) { startNew(ProgressRule()) }
                        Divider()
                        Button(L("训练日志（Epoch 37/100）", "Training log (Epoch 37/100)")) {
                            var r = ProgressRule(); r.name = L("训练", "Training"); r.file = "*.log"; r.pattern = "epoch"; r.unit = "epoch"
                            startNew(r)
                        }
                        Button(L("通用步数（step 120/1000）", "Generic steps (step 120/1000)")) {
                            var r = ProgressRule(); r.name = L("计算", "Run"); r.file = "*.log"; r.pattern = "step"; r.unit = L("步", "steps")
                            startNew(r)
                        }
                    } label: {
                        Label(L("添加规则", "Add Rule"), systemImage: "plus")
                    }
                    .fixedSize()
                    Spacer()
                }
            } header: {
                SettingsHeader(title: L("自定义规则", "Custom Rules"), symbol: "wand.and.stars", tint: .indigo)
            } footer: {
                Text(L("自定义规则排在内置识别前面检查；匹配到文件但取不到数字时不显示进度。",
                       "Custom rules are checked before the built-in ones. If the file matches but no number is found, nothing is shown."))
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
        .navigationTitle(L("进度识别", "Progress Detection"))
        .sheet(item: $editing) { rule in
            RuleEditorSheet(rule: rule, isNew: isNew, hosts: store.settings.hosts.filter(\.enabled), engine: engine) { saved in
                if let i = store.settings.progressRules.firstIndex(where: { $0.id == saved.id }) {
                    store.settings.progressRules[i] = saved
                } else {
                    store.settings.progressRules.append(saved)
                }
            } onDelete: {
                store.settings.progressRules.removeAll { $0.id == rule.id }
            }
        }
    }

    private func startNew(_ r: ProgressRule) {
        isNew = true
        editing = r
    }

    private func builtinRow(_ name: String, files: String, note: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(name).frame(width: 92, alignment: .leading)
            VStack(alignment: .leading, spacing: 2) {
                Text(files).font(.system(.callout, design: .monospaced))
                Text(note).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
        }
    }

    private func ruleRow(_ rule: Binding<ProgressRule>) -> some View {
        let r = rule.wrappedValue
        return HStack(spacing: 10) {
            Toggle("", isOn: rule.enabled).labelsHidden().toggleStyle(.switch).controlSize(.small)
            VStack(alignment: .leading, spacing: 2) {
                Text(r.name.isEmpty ? L("未命名", "Untitled") : r.name)
                    .foregroundStyle(r.enabled ? .primary : .secondary)
                HStack(spacing: 4) {
                    Text(r.file).font(.system(.caption, design: .monospaced))
                    Text("·")
                    Text(r.match == .keyword ? L("关键字 ", "keyword ") + r.pattern : L("正则", "regex"))
                        .font(.caption).lineLimit(1)
                }
                .foregroundStyle(.secondary)
                if let p = r.problem {
                    Label(p, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
                }
            }
            Spacer()
            Button(L("编辑…", "Edit…")) { isNew = false; editing = r }
        }
    }
}

/// 编辑一条规则，并可以在真实服务器的目录上测试。
struct RuleEditorSheet: View {
    @State var rule: ProgressRule
    var isNew: Bool
    var hosts: [HostConfig]
    var engine: MonitorEngine
    var onSave: (ProgressRule) -> Void
    var onDelete: () -> Void
    /// 预览渲染用：直接显示一个测试结果。
    var previewResult: RuleTestResult? = nil
    @Environment(\.dismiss) private var dismiss

    @State private var testHost: UUID?
    @State private var testDir = ""
    @State private var testing = false
    @State private var result: RuleTestResult?

    var body: some View {
        VStack(spacing: 0) {
            Form {
                Section {
                    TextField(L("名称", "Name"), text: $rule.name, prompt: Text(L("比如 PyTorch 训练", "e.g. PyTorch training")))
                    TextField(L("单位", "Unit"), text: $rule.unit, prompt: Text(L("比如 epoch、步（可不填）", "e.g. epoch, steps (optional)")))
                }
                Section {
                    LabeledContent {
                        TextField("", text: $rule.file, prompt: Text("train.log"))
                            .font(.system(.body, design: .monospaced))
                    } label: {
                        HelpLabel(L("文件", "File"), help: L("任务工作目录里的文件，可以用 * 通配，比如 *.log、logs/*.out；有多个时取最新的。",
                                                             "A file in the job's working directory. Wildcards work, e.g. *.log or logs/*.out; the newest match is used."))
                    }
                    Picker(L("取当前值", "Current Value"), selection: $rule.match) {
                        ForEach(ProgressRule.Match.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    LabeledContent {
                        TextField("", text: $rule.pattern, prompt: Text(rule.match == .keyword ? "epoch" : #"iter=(\d+)/(\d+)"#))
                            .font(.system(.body, design: .monospaced))
                    } label: {
                        HelpLabel(rule.match == .keyword ? L("关键字", "Keyword") : L("正则", "Regex"),
                                  help: rule.match == .keyword
                                      ? L("取最后一次出现时紧跟在后面的数字，不区分大小写。比如 epoch 能匹配 “Epoch 37/100”、“epoch: 37”；写成 37/100 时会顺便取到目标值。",
                                          "Takes the number right after the last occurrence, case-insensitive. “epoch” matches “Epoch 37/100” or “epoch: 37”; a “37/100” form also gives the target.")
                                      : L("在文件最后 40 行里从下往上找。第 1 个括号是当前值，第 2 个括号（可选）是目标值。",
                                          "Searched bottom-up in the last 40 lines. Group 1 is the current value; group 2 (optional) is the target."))
                    }
                } header: { Text(L("从哪里读进度", "Where to Read Progress")) }

                Section {
                    LabeledContent {
                        TextField("", text: $rule.total, prompt: Text(L("比如 100（可不填）", "e.g. 100 (optional)")))
                    } label: {
                        HelpLabel(L("固定目标值", "Fixed Target"), help: L("日志里没有写总数时使用。", "Used when the log doesn't show the total."))
                    }
                    LabeledContent {
                        HStack {
                            TextField("", text: $rule.totalFile, prompt: Text("config.yaml"))
                                .font(.system(.body, design: .monospaced))
                            TextField("", text: $rule.totalKeyword, prompt: Text("max_epochs"))
                                .font(.system(.body, design: .monospaced))
                        }
                    } label: {
                        HelpLabel(L("从文件读目标值", "Target from File"),
                                  help: L("文件 + 关键字：取这个文件里关键字第一次出现时后面的数字。", "File + keyword: the number after the first occurrence of the keyword."))
                    }
                    LabeledContent {
                        TextField("", text: $rule.doneMarker, prompt: Text(L("比如 Training finished（可不填）", "e.g. Training finished (optional)")))
                    } label: {
                        HelpLabel(L("结束标记", "End Marker"),
                                  help: L("文件末尾出现这段文字就算正常结束；进程结束时没有它，就提醒“提前结束”。",
                                          "If this text appears at the end of the file, the job finished normally; if the process ends without it, you'll get an “ended early” alert."))
                    }
                } header: { Text(L("目标值与结束（都可不填）", "Target & End (optional)")) }

                Section {
                    HStack {
                        Picker(L("服务器", "Server"), selection: $testHost) {
                            ForEach(hosts) { Text($0.displayName).tag(Optional($0.id)) }
                        }
                        .frame(maxWidth: 220)
                        TextField("", text: $testDir, prompt: Text(L("任务目录，比如 ~/runs/exp1", "Job directory, e.g. ~/runs/exp1")))
                            .font(.system(.body, design: .monospaced))
                        Button {
                            runTest()
                        } label: {
                            if testing { ProgressView().controlSize(.small) } else { Text(L("测试", "Test")) }
                        }
                        .disabled(testing || testHost == nil || testDir.isEmpty || rule.problem != nil)
                    }
                    if let result { resultView(result) }
                } header: {
                    Text(L("测试", "Test"))
                } footer: {
                    Text(L("只读取文件，不会修改服务器上的任何东西。", "Read-only: nothing on the server is changed."))
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .formStyle(.grouped)

            Divider()
            HStack {
                if !isNew {
                    Button(L("删除规则", "Delete Rule"), role: .destructive) { onDelete(); dismiss() }
                }
                if let p = rule.problem {
                    Label(p, systemImage: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.orange)
                }
                Spacer()
                Button(L("取消", "Cancel")) { dismiss() }.keyboardShortcut(.cancelAction)
                Button(isNew ? L("添加", "Add") : L("保存", "Save")) { onSave(rule); dismiss() }
                    .keyboardShortcut(.defaultAction)
                    .disabled(rule.name.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            .padding(14)
            .background(Color(nsColor: .windowBackgroundColor))
        }
        .frame(width: 620, height: 640)
        .onAppear {
            if testHost == nil { testHost = hosts.first?.id }
            if let previewResult { result = previewResult; testDir = "~/runs/exp1" }
        }
    }

    private func runTest() {
        guard let id = testHost, let host = hosts.first(where: { $0.id == id }) else { return }
        testing = true
        result = nil
        let r = rule, dir = testDir.trimmingCharacters(in: .whitespaces)
        Task {
            let out = await engine.testRule(r, host: host, dir: dir)
            result = out
            testing = false
        }
    }

    private func resultView(_ r: RuleTestResult) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(r.headline, systemImage: r.ok ? "checkmark.circle.fill" : "xmark.circle.fill")
                .foregroundStyle(r.ok ? .green : .orange)
            ForEach(r.notes, id: \.self) { Text($0).font(.caption).foregroundStyle(.secondary) }
            if !r.lines.isEmpty {
                VStack(alignment: .leading, spacing: 1) {
                    ForEach(Array(r.lines.enumerated()), id: \.offset) { _, l in
                        Text(l).font(.system(.caption, design: .monospaced)).lineLimit(1).truncationMode(.tail)
                    }
                }
                .padding(6)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 6).fill(Color.primary.opacity(0.05)))
            }
        }
        .textSelection(.enabled)
    }
}
