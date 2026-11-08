using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ServerIsland.Core;
using ServerIsland.Core.Ssh;
using ServerIsland.Desktop.Ui;
using static ServerIsland.Core.Lang;

namespace ServerIsland.Desktop.Settings;

/// 设置 → 进度识别：内置软件的说明 + 用户自定义规则。
public sealed class RulesPage(SettingsWindow window, SettingsKit k, ConfigStore store, MonitorEngine engine)
{
    public Control Build()
    {
        var col = new StackPanel();
        col.Children.Add(k.Text(L("进度识别", "Progress Detection"), 22, FontWeight.Bold).Margin2(2, 0, 0, 16));
        col.Children.Add(k.Section(L("内置识别", "Built-in"), "atom", Color.Parse("#14B8A6"), [
            Builtin("NEP / GNEP", "loss.out + nep.in / gnep.in", L("代数 / epoch，目标取自 nep.in / gnep.in", "Generation / epoch; target from nep.in / gnep.in")),
            Builtin("TorchNEP", "loss.out + output.log", L("epoch，目标取自 output.log / nep.in；loss.out 可以在 output/ 等子目录里", "Epochs; target from output.log / nep.in; loss.out may be in a subfolder such as output/")),
            Builtin("MACE", "logs/*.log", L("epoch，目标是 max_num_epochs（取自 _debug.log）", "Epochs; target is max_num_epochs (from _debug.log)")),
            Builtin("DeePMD", "lcurve.out", L("步数，目标取自日志的 Start to train N steps 或输入 JSON 的 numb_steps", "Steps; target from \"Start to train N steps\" in the log or numb_steps in the input JSON")),
            Builtin("GPUMD", "run.in + neighbor.out / thermo.out", L("步数，目标是 run.in 里所有 run 之和", "Steps; target is the sum of run in run.in")),
            Builtin("VASP", "INCAR + OSZICAR", L("MD 显示百分比；结构优化显示离子步，单点显示电子步", "MD shows a percentage; relaxation shows ionic steps, SCF shows electronic steps")),
            Builtin("LAMMPS", "log.lammps", L("最后一段 run 的步数；minimize 等不常见写法不显示", "Steps of the last run; minimize and unusual setups are skipped")),
        ], L("按任务的工作目录里有哪些文件判断。格式和预期不一致时不显示进度。", "Detected from files in the job's working directory. Nothing is shown when the format isn't as expected.")));

        var rows = new List<Control?>();
        if (store.Settings.ProgressRules.Count == 0)
            rows.Add(k.Caption(L("还没有规则。添加后，所有服务器都会按它识别进度。", "No rules yet. Rules apply to every server.")));
        foreach (var rule in store.Settings.ProgressRules) rows.Add(RuleRow(rule));

        Button? add = null;
        add = k.Button(L("添加规则…", "Add Rule…"), () =>
        {
            var menu = new MenuFlyout();
            void Item(string title, Func<ProgressRule> make)
            {
                var mi = new MenuItem { Header = title };
                mi.Click += async (_, _) => await Edit(make(), true);
                menu.Items.Add(mi);
            }
            Item(L("空白规则", "Blank Rule"), () => new ProgressRule());
            menu.Items.Add(new Separator());
            Item(L("训练日志（Epoch 37/100）", "Training log (Epoch 37/100)"), () => new ProgressRule { Name = L("训练", "Training"), File = "*.log", Pattern = "epoch", Unit = "epoch" });
            Item(L("通用步数（step 120/1000）", "Generic steps (step 120/1000)"), () => new ProgressRule { Name = L("计算", "Run"), File = "*.log", Pattern = "step", Unit = L("步", "steps") });
            menu.ShowAt(add!);
        });
        rows.Add(add);
        col.Children.Add(k.Section(L("自定义规则", "Custom Rules"), "wand", Color.Parse("#5856D6"), rows,
            L("自定义规则排在内置识别前面检查；匹配到文件但取不到数字时不显示进度。", "Custom rules are checked before the built-in ones. If the file matches but no number is found, nothing is shown.")));
        return col;
    }

    Control Builtin(string name, string files, string note)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("100,*") };
        g.Children.Add(k.Text(name).VAlign(VerticalAlignment.Top));
        var texts = new StackPanel { Spacing = 2, Children = { k.Text(files, 12.5, mono: true), k.Caption(note) } };
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        return g;
    }

    Control RuleRow(ProgressRule rule)
    {
        var id = rule.Id;
        var toggle = k.Toggle(rule.Enabled, v =>
        {
            if (store.Settings.ProgressRules.FirstOrDefault(r => r.Id == id) is { } r) { r.Enabled = v; store.Touch(); }
        });
        var texts = new StackPanel { Spacing = 2, Margin = new Thickness(10, 0, 0, 0) };
        texts.Children.Add(k.Text(rule.Name.Length == 0 ? L("未命名", "Untitled") : rule.Name, c: rule.Enabled ? null : k.P.Secondary));
        texts.Children.Add(k.Text(rule.File + " · " + (rule.Match == RuleMatch.Keyword ? L("关键字 ", "keyword ") + rule.Pattern : L("正则", "regex")), 11.5, c: k.P.Secondary, mono: true));
        if (rule.Problem is { } p)
            texts.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children =
                { new Icon("warningFill", 11, Color.Parse("#FF9F0A").B()), k.Text(p, 11.5, c: Color.Parse("#FF9F0A")) } });
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        g.Children.Add(toggle.VAlign(VerticalAlignment.Center));
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        var edit = k.Button(L("编辑…", "Edit…"), async () => await Edit(rule.Clone(), false));
        Grid.SetColumn(edit, 2);
        g.Children.Add(edit);
        return g;
    }

    async Task Edit(ProgressRule rule, bool isNew)
    {
        var result = await RuleEditor.Show(window, k, rule, isNew, store.Settings.Hosts.Where(h => h.Enabled).ToList(), engine);
        if (result == null) return;
        var list = store.Settings.ProgressRules;
        if (result.Value.Delete) list.RemoveAll(r => r.Id == rule.Id);
        else if (list.FindIndex(r => r.Id == result.Value.Rule.Id) is var i and >= 0) list[i] = result.Value.Rule;
        else list.Add(result.Value.Rule);
        store.Touch();
        window.RefreshPage();
    }
}

/// 编辑一条规则，并可以在真实服务器的目录上测试（只读）。
public static class RuleEditor
{
    public static Task<(ProgressRule Rule, bool Delete)?> Show(Window owner, SettingsKit k, ProgressRule rule, bool isNew, List<HostConfig> hosts, MonitorEngine engine)
    {
        var tcs = new TaskCompletionSource<(ProgressRule, bool)?>();
        var w = new Window
        {
            Title = isNew ? L("添加规则", "Add Rule") : L("编辑规则", "Edit Rule"),
            Width = 640, Height = 680, CanResize = true, Icon = App.WindowIcon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = k.P.Bg.B(),
        };
        var problem = k.Text("", 11.5, c: Color.Parse("#FF9F0A"));
        Button? save = null;
        void Validate()
        {
            problem.Text = rule.Problem ?? "";
            if (save != null) save.IsEnabled = rule.Name.Trim().Length > 0;
        }

        var patternLabel = new ContentControl();
        void SetPatternLabel() => patternLabel.Content = k.Label(rule.Match == RuleMatch.Keyword ? L("关键字", "Keyword") : L("正则", "Regex"),
            rule.Match == RuleMatch.Keyword
                ? L("取最后一次出现时紧跟在后面的数字，不区分大小写。比如 epoch 能匹配 “Epoch 37/100”、“epoch: 37”；写成 37/100 时会顺便取到目标值。",
                    "Takes the number right after the last occurrence, case-insensitive. “epoch” matches “Epoch 37/100” or “epoch: 37”; a “37/100” form also gives the target.")
                : L("在文件最后 40 行里从下往上找。第 1 个括号是当前值，第 2 个括号（可选）是目标值。",
                    "Searched bottom-up in the last 40 lines. Group 1 is the current value; group 2 (optional) is the target."));
        SetPatternLabel();
        var patternBox = k.Field(rule.Pattern, v => { rule.Pattern = v; Validate(); }, rule.Match == RuleMatch.Keyword ? "epoch" : @"iter=(\d+)/(\d+)", mono: true);
        var patternRow = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        patternRow.Children.Add(patternLabel);
        Grid.SetColumn(patternBox, 1);
        patternRow.Children.Add(patternBox);

        var col = new StackPanel { Margin = new Thickness(22, 18, 22, 0) };
        col.Children.Add(k.Section("", "", default, [
            k.WideRow(L("名称", "Name"), null, k.Field(rule.Name, v => { rule.Name = v; Validate(); }, L("比如 PyTorch 训练", "e.g. PyTorch training")), 140),
            k.WideRow(L("单位", "Unit"), null, k.Field(rule.Unit, v => rule.Unit = v, L("比如 epoch、步（可不填）", "e.g. epoch, steps (optional)")), 140),
        ]));
        col.Children.Add(k.Section(L("从哪里读进度", "Where to Read Progress"), "search", Color.Parse("#0A84FF"), [
            k.WideRow(L("文件", "File"), L("任务工作目录里的文件，可以用 * 通配，比如 *.log、logs/*.out；有多个时取最新的。",
                "A file in the job's working directory. Wildcards work, e.g. *.log or logs/*.out; the newest match is used."),
                k.Field(rule.File, v => { rule.File = v; Validate(); }, "train.log", mono: true), 140),
            k.Row(L("取当前值", "Current Value"), null, k.Segmented(Enum.GetValues<RuleMatch>().Select(m => (m, ProgressRule.MatchLabel(m))), rule.Match,
                v => { rule.Match = v; SetPatternLabel(); patternBox.Watermark = v == RuleMatch.Keyword ? "epoch" : @"iter=(\d+)/(\d+)"; Validate(); })),
            patternRow,
        ]));
        var totalFiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var tf = k.Field(rule.TotalFile, v => { rule.TotalFile = v; Validate(); }, "config.yaml", mono: true);
        var tk = k.Field(rule.TotalKeyword, v => { rule.TotalKeyword = v; Validate(); }, "max_epochs", mono: true);
        totalFiles.Children.Add(tf);
        Grid.SetColumn(tk, 2);
        totalFiles.Children.Add(tk);
        col.Children.Add(k.Section(L("目标值与结束（都可不填）", "Target & End (optional)"), "check", Color.Parse("#30D158"), [
            k.WideRow(L("固定目标值", "Fixed Target"), L("日志里没有写总数时使用。", "Used when the log doesn't show the total."),
                k.Field(rule.Total, v => { rule.Total = v; Validate(); }, L("比如 100（可不填）", "e.g. 100 (optional)")), 140),
            k.WideRow(L("从文件读目标值", "Target from File"), L("文件 + 关键字：取这个文件里关键字第一次出现时后面的数字。", "File + keyword: the number after the first occurrence of the keyword."), totalFiles, 140),
            k.WideRow(L("结束标记", "End Marker"), L("文件末尾出现这段文字就算正常结束；进程结束时没有它，就提醒“提前结束”。",
                "If this text appears at the end of the file, the job finished normally; if the process ends without it, you'll get an “ended early” alert."),
                k.Field(rule.DoneMarker, v => rule.DoneMarker = v, L("比如 Training finished（可不填）", "e.g. Training finished (optional)")), 140),
        ]));

        // 测试
        var hostCombo = new ComboBox { MinWidth = 160 };
        foreach (var h in hosts) hostCombo.Items.Add(h.DisplayName);
        if (hosts.Count > 0) hostCombo.SelectedIndex = 0;
        var dirBox = new TextBox { Watermark = L("任务目录，比如 ~/runs/exp1", "Job directory, e.g. ~/runs/exp1"), FontFamily = Hue.Mono };
        var result = new StackPanel { Spacing = 4 };
        Button? testBtn = null;
        testBtn = k.Button(L("测试", "Test"), async () =>
        {
            if (hostCombo.SelectedIndex < 0 || string.IsNullOrWhiteSpace(dirBox.Text) || rule.Problem != null) return;
            testBtn!.IsEnabled = false;
            testBtn.Content = L("测试中…", "Testing…");
            result.Children.Clear();
            var r = await engine.TestRule(rule.Clone(), hosts[hostCombo.SelectedIndex], dirBox.Text.Trim());
            result.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children =
            {
                new Icon(r.Ok ? "check" : "xmark", 13, (r.Ok ? Hue.Mint : Color.Parse("#FF9F0A")).B(), 2.8),
                k.Text(r.Headline, 12.5, c: r.Ok ? null : Color.Parse("#FF9F0A"), wrap: true),
            } });
            foreach (var n in r.Notes) result.Children.Add(k.Caption(n));
            if (r.Lines.Count > 0)
                result.Children.Add(new Border
                {
                    Background = k.P.Field.B(), CornerRadius = new CornerRadius(6), Padding = new Thickness(6),
                    Child = new SelectableTextBlock { Text = string.Join("\n", r.Lines), FontFamily = Hue.Mono, FontSize = 11, Foreground = k.P.Text.B() },
                });
            testBtn.IsEnabled = true;
            testBtn.Content = L("测试", "Test");
        });
        var testRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,8,*,8,Auto") };
        testRow.Children.Add(hostCombo);
        Grid.SetColumn(dirBox, 2);
        testRow.Children.Add(dirBox);
        Grid.SetColumn(testBtn, 4);
        testRow.Children.Add(testBtn);
        col.Children.Add(k.Section(L("测试", "Test"), "pulse", Color.Parse("#BF5AF2"), [testRow, result],
            L("只读取文件，不会修改服务器上的任何东西。", "Read-only: nothing on the server is changed.")));

        save = k.Button(isNew ? L("添加", "Add") : L("保存", "Save"), () => { tcs.TrySetResult((rule, false)); w.Close(); }, accent: true);
        save.IsDefault = true;
        var cancel = k.Button(L("取消", "Cancel"), () => w.Close());
        cancel.IsCancel = true;
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,8,Auto"), Margin = new Thickness(22, 10, 22, 14) };
        if (!isNew)
        {
            var del = k.Button(L("删除规则", "Delete Rule"), () => { tcs.TrySetResult((rule, true)); w.Close(); }, danger: true);
            bottom.Children.Add(del);
        }
        problem.Margin = new Thickness(10, 0, 10, 0);
        Grid.SetColumn(problem, 1);
        bottom.Children.Add(problem);
        Grid.SetColumn(cancel, 2);
        bottom.Children.Add(cancel);
        Grid.SetColumn(save, 4);
        bottom.Children.Add(save);
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(new ScrollViewer { Content = col });
        w.Content = dock;
        w.Closed += (_, _) => tcs.TrySetResult(null);
        Validate();
        w.ShowDialog(owner);
        return tcs.Task;
    }
}

/// 添加服务器：从 ~/.ssh/config 里挑选，或手动填写地址 / 用户名 / 端口 / 私钥 / 跳板机。
public static class AddHostDialog
{
    /// startManual：直接打开"手动填写"；onCreated：预览渲染时拿到窗口截图。
    public static Task<List<HostConfig>> Show(Window owner, List<HostConfig> existingHosts, bool startManual = false, Action<Window>? onCreated = null)
    {
        var existing = existingHosts.Select(x => x.Alias).ToHashSet();
        var k = new SettingsKit(Palette.For(owner.ActualThemeVariant));
        var tcs = new TaskCompletionSource<List<HostConfig>>();
        var w = new Window
        {
            Title = L("添加服务器", "Add Servers"), Width = 540, Height = 640, Icon = App.WindowIcon,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = k.P.Bg.B(),
        };
        var entries = SshConfig.Load();
        // ~/.ssh/config 里什么都没有时，直接打开手动填写。
        bool manualMode = startManual || entries.Count == 0;

        // —— 从 ~/.ssh/config 选择 ——
        var picked = new HashSet<string>();
        var list = new StackPanel { Spacing = 2 };
        foreach (var e in entries)
        {
            var cb = new CheckBox
            {
                IsEnabled = !existing.Contains(e.Alias),
                Content = new StackPanel { Spacing = 1, Children = { k.Text(e.Alias, 13, mono: true), k.Text(e.Summary, 11, c: k.P.Secondary) } },
            };
            var alias = e.Alias;
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) picked.Add(alias); else picked.Remove(alias); };
            list.Children.Add(cb);
        }
        if (entries.Count == 0) list.Children.Add(k.Caption(L("~/.ssh/config 里没有找到具体的 Host 条目", "No concrete Host entries in ~/.ssh/config")));
        var manual = new TextBox { Watermark = L("或直接输入 ssh 目标，如 user@10.0.0.2", "Or type an ssh target, e.g. user@10.0.0.2"), FontFamily = Hue.Mono };
        var kind = HostKind.Direct;
        var auth = AuthMode.Key;
        Guid? jump = null;
        var fromConfig = new StackPanel { Spacing = 12 };
        fromConfig.Children.Add(k.Caption(L("从 ~/.ssh/config 中选择。跳板机（ProxyJump）、密钥（IdentityFile）等配置都会生效。",
                                            "Pick from ~/.ssh/config. ProxyJump, IdentityFile, etc. all apply.")));
        fromConfig.Children.Add(new Border
        {
            Height = 200, Background = k.P.Card.B(), CornerRadius = new CornerRadius(8), BorderBrush = k.P.Separator.B(), BorderThickness = new Thickness(0.5),
            Padding = new Thickness(10, 6), Child = new ScrollViewer { Content = list },
        });
        fromConfig.Children.Add(manual);
        fromConfig.Children.Add(k.Row(L("默认类型", "Type"), null, k.Segmented(Enum.GetValues<HostKind>().Select(x => (x, x.Label())), kind, v => kind = v)));
        fromConfig.Children.Add(k.Row(L("认证方式", "Authentication"), L(ConnectionFields.AuthHelpZh, ConnectionFields.AuthHelpEn),
            k.Combo(Enum.GetValues<AuthMode>().Select(a => (a, a.Label())), auth, v => auth = v)));
        if (existingHosts.Count > 0)
            fromConfig.Children.Add(ConnectionFields.Jump(k, new HostConfig(""), existingHosts, a => { var t = new HostConfig(""); a(t); jump = t.JumpHostID; }));
        fromConfig.Children.Add(k.Caption(L("添加后可以在详情页点“自动检测类型”。", "You can use “Detect Type” on the server page afterwards.")));

        // —— 手动填写 ——
        var draft = new HostConfig("");
        var manualPanel = new StackPanel { Spacing = 12 };
        Button add = null!;
        void Validate() => add.IsEnabled = manualMode ? draft.IsManual : picked.Count > 0 || (manual.Text?.Trim().Length ?? 0) > 0;
        void BuildManual()
        {
            manualPanel.Children.Clear();
            void Edit(Action<HostConfig> a, bool rebuild = false) { a(draft); Validate(); if (rebuild) BuildManual(); }
            var basics = new List<Control?>
            {
                k.WideRow(L("显示名称", "Name"), null, k.Field(draft.DisplayName, v => Edit(h => h.DisplayName = v), L("比如：实验室集群", "e.g. Lab Cluster"))),
            };
            basics.AddRange(ConnectionFields.Manual(k, draft, a => Edit(a), w));
            manualPanel.Children.Add(k.Section(L("地址", "Address"), "globe", Color.Parse("#0A84FF"), basics));
            var more = new List<Control?>
            {
                k.Row(L("类型", "Type"), null, k.Segmented(Enum.GetValues<HostKind>().Select(x => (x, x.Label())), draft.Kind, v => Edit(h => h.Kind = v))),
            };
            more.AddRange(ConnectionFields.Auth(k, draft, a => Edit(a, rebuild: true)));
            if (existingHosts.Count > 0) more.Add(ConnectionFields.Jump(k, draft, existingHosts, a => Edit(a)));
            manualPanel.Children.Add(k.Section(L("登录", "Login"), "lock", Color.Parse("#30D158"), more,
                L("不会修改 ~/.ssh/config。需要密码的服务器添加后点“登录…”。", "~/.ssh/config is not modified. For servers that need a password, click “Log In…” after adding.")));
        }
        BuildManual();

        var body = new ContentControl();
        void Show() { body.Content = manualMode ? manualPanel : fromConfig; Validate(); }
        var tabs = k.Segmented([(false, L("从 ~/.ssh/config 选择", "From ~/.ssh/config")), (true, L("手动填写", "Enter Manually"))], manualMode, v => { manualMode = v; Show(); });
        tabs.HorizontalAlignment = HorizontalAlignment.Stretch;

        var col = new StackPanel { Spacing = 12, Margin = new Thickness(20) };
        col.Children.Add(k.Text(L("添加服务器", "Add Servers"), 17, FontWeight.SemiBold));
        col.Children.Add(tabs);
        col.Children.Add(body);
        add = k.Button(L("添加", "Add"), () =>
        {
            if (manualMode)
            {
                var h = draft;
                var (user, addr) = SshHosts.SplitAddress(h);
                // 别名只用于显示和"改回别名模式"时的兜底，写成 ssh 能识别的 user@host。
                h.Alias = (user.Length > 0 ? user + "@" : "") + addr;
                if (h.DisplayName.Trim().Length == 0) h.DisplayName = SshHosts.Summary(h);
                tcs.TrySetResult([h]);
            }
            else
            {
                var aliases = entries.Select(e => e.Alias).Where(picked.Contains).ToList();
                var m = manual.Text?.Trim() ?? "";
                if (m.Length > 0 && !existing.Contains(m)) aliases.Add(m);
                tcs.TrySetResult(aliases.Select(a => new HostConfig(a, kind: kind) { Auth = auth, JumpHostID = jump }).ToList());
            }
            w.Close();
        }, accent: true);
        add.IsDefault = true;
        manual.TextChanged += (_, _) => Validate();
        foreach (var cb in list.Children.OfType<CheckBox>()) cb.IsCheckedChanged += (_, _) => Validate();
        var cancel = k.Button(L("取消", "Cancel"), () => w.Close());
        cancel.IsCancel = true;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 0, 20, 16), Children = { cancel, add } };
        var dock = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);
        dock.Children.Add(new ScrollViewer { Content = col });
        w.Content = dock;
        Show();
        w.Closed += (_, _) => tcs.TrySetResult([]);
        w.ShowDialog(owner);
        onCreated?.Invoke(w);
        return tcs.Task;
    }
}
