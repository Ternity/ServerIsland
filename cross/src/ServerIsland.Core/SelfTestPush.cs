using System.Text;
using System.Text.Json;

namespace ServerIsland.Core;

/// 自检：手机推送的 key 识别、过滤范围、消息格式、合并、截断。不联网。和 Swift 版 SelfTestPush.swift 逐项对应。
public static class SelfTestPush
{
    public static void Run(Action<bool, string> check)
    {
        // —— key ——
        const string k = "a1b2c3d4-0000-1111-2222-333344445555";
        check(PhonePush.ExtractKey($"https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key={k}") == k, "推送：从 Webhook 地址取出 key");
        check(PhonePush.ExtractKey($"  {k}\n") == k, "推送：直接粘贴 key");
        check(PhonePush.ExtractKey($"https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key={k}&debug=1") == k, "推送：key 后面还有参数");
        check(PhonePush.ExtractKey("hello world") == null && PhonePush.ExtractKey("abc") == null, "推送：认不出的内容不保存");
        check(PhonePush.Mask(k) == "****5555" && !PhonePush.Scrub($"bad url ...key={k}", k).Contains(k), "推送：日志里 key 打码");

        // —— 设置：旧配置没有这一项时用默认值（关闭、只推送关注的任务）；和 Mac 版的键名一致 ——
        var old = JsonSerializer.Deserialize<AppSettings>("{}", Json.Options)!;
        var partial = JsonSerializer.Deserialize<AppSettings>("""{"phonePush":{"enabled":true,"scope":"island"}}""", Json.Options)!;
        var written = JsonSerializer.Serialize(new AppSettings(), Json.Options);
        check(!old.PhonePush.Enabled && old.PhonePush.Scope == PushScope.Starred && old.PhonePush.Finished && !old.PhonePush.Started
              && partial.PhonePush.Enabled && partial.PhonePush.Scope == PushScope.Island && partial.PhonePush.Detailed
              && written.Contains("\"phonePush\"") && written.Contains("\"failuresDuringQuiet\"") && !written.Contains("wantsAgents"),
              "推送：设置缺字段时补默认值（键名和 Mac 版一致）");

        // —— 过滤 ——
        var st = new AppSettings();
        var h = Guid.NewGuid();
        st.PhonePush.Enabled = true;
        IslandEvent Ev(EventKind kind, string? job) => new(kind, "t", "", "h", h, job);
        st.StarredJobs = [new StarredJob { HostID = h, JobID = "1" }];
        check(PhonePush.Wants(Ev(EventKind.Finished, "1"), st) && !PhonePush.Wants(Ev(EventKind.Finished, "2"), st), "推送：默认只推送关注的任务");
        check(PhonePush.Wants(Ev(EventKind.Finished, "2"), st, ["2"]), "推送：手动监听的进程总是推送");
        check(!PhonePush.Wants(Ev(EventKind.Started, "1"), st), "推送：开始运行默认不推送");
        check(PhonePush.Wants(Ev(EventKind.Warning, null), st), "推送：磁盘警告不受任务范围影响");
        st.PhonePush.ServerWarnings = false;
        check(!PhonePush.Wants(Ev(EventKind.Warning, null), st) && !PhonePush.Wants(Ev(EventKind.Info, null), st), "推送：关掉服务器警告");
        st.PhonePush.Scope = PushScope.All;
        check(PhonePush.Wants(Ev(EventKind.Failed, "9"), st), "推送：全部任务");
        st.PhonePush.Failed = false;
        check(!PhonePush.Wants(Ev(EventKind.Failed, "9"), st), "推送：关掉“失败”");
        st.PhonePush.Failed = true;
        st.PhonePush.Scope = PushScope.Island;
        st.AlertScope = AlertScope.Starred;
        check(PhonePush.Wants(Ev(EventKind.Failed, "1"), st) && !PhonePush.Wants(Ev(EventKind.Failed, "9"), st), "推送：和刘海提醒一样（跟着刘海的范围）");

        // —— 消息格式 ——
        var saved = Lang.Setting;
        Lang.Setting = AppLanguage.Zh;
        try
        {
            var now = DateTime.Today.AddHours(14).AddMinutes(32);
            var samples = PhonePush.Samples(now);
            var texts = samples.Select(s => PhonePush.ItemFor(s.Event, s.Host, true, now).Text).ToList();
            var done = texts[0];
            check(done.StartsWith("✅ gnep-train 已完成\n服务器：集群A（Slurm）\n作业号：12345 · 分区 gpu\n资源：1 节点 · 32 核 · 4 GPU"),
                  "推送：完成消息的开头（结果、服务器、作业号、资源）");
            check(done.Contains("用时：3 小时 12 分（上限 24 小时，用了 13%）") && done.Contains("时间：11:20 开始 → 14:32 结束"), "推送：用时、上限占比、起止时间");
            check(done.Contains("目录：~/nep/train-03") && !done.Contains("/home/demo") && done.Contains("费用：约 ¥38.40"), "推送：目录只显示最后两层，带费用");
            check(texts[1].Contains("状态：TIMEOUT") && texts[1].Contains("（已到时间上限）") && !texts[1].Contains("退出码 0:0"), "推送：超时写明到了上限，退出码 0:0 不显示");
            check(texts[2].Contains("进程：8812") && texts[2].Contains("运行了至少 5 小时 40 分") && !texts[2].Contains("时间：")
                  && texts[2].Contains("最后一次采样：CPU 98%"), "推送：进程只写“至少运行了”，带最后一次采样");
            check(texts[3].StartsWith("❌") && texts[3].Contains("失败的元素：#17、#52"), "推送：数组列出失败的元素");
            check(texts[4].Contains("节点：gpu07") && texts[4].Contains("时间上限：24 小时") && !texts[4].Contains("用时"), "推送：开始运行的消息");
            check(texts[5] == "⚠️ 磁盘快满了：/home\n服务器：集群A（Slurm）\n已用 92% · 剩余 41 GB", "推送：没有详情时用提示的副标题");
            var brief = PhonePush.ItemFor(samples[0].Event, samples[0].Host, false, now).Text;
            check(brief == "✅ gnep-train 已完成\n服务器：集群A（Slurm）", "推送：简洁模式只有两行");

            // —— 合并、截断 ——
            var items = samples.Take(4).Select(s => PhonePush.ItemFor(s.Event, s.Host, true, now)).ToList();
            var merged = PhonePush.Merged(items, [samples[0].Host, samples[2].Host]);
            check(merged.StartsWith("📦 ServerIsland：4 条提醒\n✅ gnep-train 已完成 · 3 小时 12 分 · ¥38.40 · 集群A"), "推送：多台服务器合并成一条");
            var one = PhonePush.Merged([items[0], items[1]], [samples[0].Host]);
            check(one.StartsWith("📦 集群A：2 条提醒\n✅ gnep-train") && one.Contains("❌ relax-Si 超时 · TIMEOUT · 24 小时"), "推送：同一台服务器合并时标题写服务器名");
            check(PhonePush.ShortDir("/home/alice/scan") == "~/scan" && PhonePush.ShortDir("/public/home/alice/nep/a/b") == "…/a/b"
                  && PhonePush.ShortDir("/home/alice") == "~" && PhonePush.ShortDir("/Users/alice/x/y") == "~/x/y"
                  && PhonePush.ShortDir("/data/proj/run1/step2") == "…/run1/step2", "推送：目录里不带用户名");
            var cut = PhonePush.Truncate(string.Concat(Enumerable.Repeat("很长的消息", 500)));
            check(Encoding.UTF8.GetByteCount(cut) <= PhonePush.MaxBytes && cut.EndsWith('…'), "推送：超长消息按字节截断");
            check(PhonePush.Duration(42 * 60) == "42 分" && PhonePush.Duration(26 * 3600) == "26 小时" && PhonePush.Duration(50 * 3600) == "2 天 2 小时"
                  && PhonePush.Duration(3600) == "1 小时", "推送：时长格式");
        }
        finally { Lang.Setting = saved; }
    }
}
