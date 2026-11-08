import Foundation

/// 自检：手机推送的 key 识别、过滤范围、消息格式、合并、截断。不联网。
enum SelfTestPush {
    static func run(_ check: (Bool, String) -> Void) {
        // —— key ——
        let k = "a1b2c3d4-0000-1111-2222-333344445555"
        check(PhonePush.extractKey("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=\(k)") == k, "推送：从 Webhook 地址取出 key")
        check(PhonePush.extractKey("  \(k)\n") == k, "推送：直接粘贴 key")
        check(PhonePush.extractKey("https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=\(k)&debug=1") == k, "推送：key 后面还有参数")
        check(PhonePush.extractKey("hello world") == nil && PhonePush.extractKey("abc") == nil, "推送：认不出的内容不保存")
        check(PhonePush.mask(k) == "****5555" && !PhonePush.scrub("bad url ...key=\(k)", key: k).contains(k), "推送：日志里 key 打码")

        // —— 设置：旧配置没有这一项时用默认值（关闭、只推送关注的任务） ——
        if let old = try? JSONDecoder().decode(AppSettings.self, from: Data("{}".utf8)),
           let partial = try? JSONDecoder().decode(PhonePushSettings.self, from: Data(#"{"enabled":true}"#.utf8)) {
            check(!old.phonePush.enabled && old.phonePush.scope == .starred && old.phonePush.finished && !old.phonePush.started
                  && partial.enabled && partial.scope == .starred && partial.detailed, "推送：设置缺字段时补默认值")
        } else {
            check(false, "推送：设置缺字段时补默认值")
        }

        // —— 过滤 ——
        var st = AppSettings()
        let h = UUID()
        st.phonePush.enabled = true
        func ev(_ kind: IslandEvent.Kind, _ job: String?) -> IslandEvent {
            IslandEvent(kind: kind, title: "t", subtitle: "", hostName: "h", hostID: h, jobID: job)
        }
        st.starredJobs = [StarredJob(hostID: h, jobID: "1")]
        check(PhonePush.wants(ev(.finished, "1"), settings: st) && !PhonePush.wants(ev(.finished, "2"), settings: st),
              "推送：默认只推送关注的任务")
        check(PhonePush.wants(ev(.finished, "2"), settings: st, watchedPIDs: ["2"]), "推送：手动监听的进程总是推送")
        check(!PhonePush.wants(ev(.started, "1"), settings: st), "推送：开始运行默认不推送")
        check(PhonePush.wants(ev(.warning, nil), settings: st), "推送：磁盘警告不受任务范围影响")
        st.phonePush.serverWarnings = false
        check(!PhonePush.wants(ev(.warning, nil), settings: st) && !PhonePush.wants(ev(.info, nil), settings: st), "推送：关掉服务器警告")
        st.phonePush.scope = .all
        check(PhonePush.wants(ev(.failed, "9"), settings: st), "推送：全部任务")
        st.phonePush.failed = false
        check(!PhonePush.wants(ev(.failed, "9"), settings: st), "推送：关掉“失败”")
        st.phonePush.failed = true
        st.phonePush.scope = .island
        st.alertScope = .starred
        check(PhonePush.wants(ev(.failed, "1"), settings: st) && !PhonePush.wants(ev(.failed, "9"), settings: st),
              "推送：和刘海提醒一样（跟着刘海的范围）")

        // —— 消息格式 ——
        let saved = Lang.setting
        Lang.setting = .zh
        defer { Lang.setting = saved }
        let now = Calendar.current.date(bySettingHour: 14, minute: 32, second: 0, of: Date()) ?? Date()
        let samples = PhonePush.samples(now: now)
        let texts = samples.map { PhonePush.item($0.0, host: $0.1, detailed: true, now: now).text }
        let done = texts[0]
        check(done.hasPrefix("✅ gnep-train 已完成\n服务器：集群A（Slurm）\n作业号：12345 · 分区 gpu\n资源：1 节点 · 32 核 · 4 GPU"),
              "推送：完成消息的开头（结果、服务器、作业号、资源）")
        check(done.contains("用时：3 小时 12 分（上限 24 小时，用了 13%）") && done.contains("时间：11:20 开始 → 14:32 结束"),
              "推送：用时、上限占比、起止时间")
        check(done.contains("目录：~/nep/train-03") && !done.contains("/home/demo") && done.contains("费用：约 ¥38.40"),
              "推送：目录只显示最后两层，带费用")
        check(texts[1].contains("状态：TIMEOUT") && texts[1].contains("（已到时间上限）") && !texts[1].contains("退出码 0:0"),
              "推送：超时写明到了上限，退出码 0:0 不显示")
        check(texts[2].contains("进程：8812") && texts[2].contains("运行了至少 5 小时 40 分") && !texts[2].contains("时间：")
              && texts[2].contains("最后一次采样：CPU 98%"), "推送：进程只写“至少运行了”，带最后一次采样")
        check(texts[3].hasPrefix("⚠️ scan 数组已全部结束") == false && texts[3].hasPrefix("❌") && texts[3].contains("失败的元素：#17、#52"),
              "推送：数组列出失败的元素")
        check(texts[4].contains("节点：gpu07") && texts[4].contains("时间上限：24 小时") && !texts[4].contains("用时"), "推送：开始运行的消息")
        check(texts[5] == "⚠️ 磁盘快满了：/home\n服务器：集群A（Slurm）\n已用 92% · 剩余 41 GB", "推送：没有详情时用提示的副标题")
        let brief = PhonePush.item(samples[0].0, host: samples[0].1, detailed: false, now: now).text
        check(brief == "✅ gnep-train 已完成\n服务器：集群A（Slurm）", "推送：简洁模式只有两行")

        // —— 合并、截断 ——
        let items = samples.prefix(4).map { PhonePush.item($0.0, host: $0.1, detailed: true, now: now) }
        let merged = PhonePush.merged(Array(items), hosts: [samples[0].1, samples[2].1])
        check(merged.hasPrefix("📦 ServerIsland：4 条提醒\n✅ gnep-train 已完成 · 3 小时 12 分 · ¥38.40 · 集群A"), "推送：多台服务器合并成一条")
        let one = PhonePush.merged([items[0], items[1]], hosts: [samples[0].1])
        check(one.hasPrefix("📦 集群A：2 条提醒\n✅ gnep-train") && one.contains("❌ relax-Si 超时 · TIMEOUT · 24 小时"), "推送：同一台服务器合并时标题写服务器名")
        check(PhonePush.shortDir("/home/alice/scan") == "~/scan" && PhonePush.shortDir("/public/home/alice/nep/a/b") == "…/a/b"
              && PhonePush.shortDir("/home/alice") == "~" && PhonePush.shortDir("/Users/alice/x/y") == "~/x/y"
              && PhonePush.shortDir("/data/proj/run1/step2") == "…/run1/step2", "推送：目录里不带用户名")
        let long = String(repeating: "很长的消息", count: 500)
        let cut = PhonePush.truncate(long)
        check(cut.utf8.count <= PhonePush.maxBytes && cut.hasSuffix("…"), "推送：超长消息按字节截断")
        check(PhonePush.duration(42 * 60) == "42 分" && PhonePush.duration(26 * 3600) == "26 小时" && PhonePush.duration(50 * 3600) == "2 天 2 小时" && PhonePush.duration(3600) == "1 小时",
              "推送：时长格式")
    }
}
