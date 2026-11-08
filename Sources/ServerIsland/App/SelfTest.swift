import Foundation

/// `ServerIsland --selftest`：用样例输出检查解析逻辑。
enum SelfTest {
    static func run() -> Bool {
        Lang.setting = .zh
        var ok = true
        func check(_ cond: Bool, _ name: String) {
            print((cond ? "✓ " : "✗ ") + name)
            if !cond { ok = false }
        }

        check(Parsers.slurmDuration("1-02:03:04") == 93784, "slurm 时长 d-hh:mm:ss")
        check(Parsers.slurmDuration("45:16") == 2716, "slurm 时长 mm:ss")
        check(Parsers.slurmDuration("4:00:00") == 14400, "slurm 时长 h:mm:ss")
        check(Parsers.slurmDuration("UNLIMITED") == nil, "slurm 时长 UNLIMITED")
        check(Parsers.slurmDuration("2-00") == 172800, "slurm 时长 d-hh")

        let output = """
        Welcome to cluster!
        @@SI:BEGIN
        @@SI:SEC cpu
        cpu  100 0 50 800 50 0 0 0 0 0
        64
        6.20 5.80 5.10 3/1200 4242
        @@SI:RC 0
        @@SI:SEC mem
        MemTotal:       263842716 kB
        MemAvailable:   160000000 kB
        @@SI:RC 0
        @@SI:SEC gpu
        0, NVIDIA A100-SXM4-80GB, 97, 61000, 81920, 66
        1, NVIDIA A100-SXM4-80GB, 0, 3, 81920, 31
        @@SI:RC 0
        @@SI:SEC squeue
        4821337|pretrain_7b|RUNNING|4:24:00|2-00:00:00|2|gpu[03-04]|a100
        4821455|eval_mmlu|PENDING|0:00|2:00:00|1|(Priority)|a100
        @@SI:RC 0
        @@SI:SEC progress
        @@SI:FILE /home/u/.serverisland/progress/pretrain.json
        {"name":"pretrain_7b","progress":0.62,"message":"step 12400/20000","jobid":"4821337"}
        @@SI:FILE /home/u/.serverisland/progress/bad.json
        not json
        @@SI:RC 0
        @@SI:END
        """
        let parsed = RemoteScript.parse(output)
        check(parsed.sawBegin && parsed.sawEnd, "识别 BEGIN/END，忽略登录横幅")
        let cpu = Parsers.cpu(parsed.sections["cpu"]?.lines ?? [])
        check(cpu.cores == 64 && cpu.load1 == 6.2 && cpu.sample?.total == 1000, "CPU 解析")
        let mem = Parsers.memory(parsed.sections["mem"]?.lines ?? [])
        check(mem != nil && abs(mem!.total - 263842716 * 1024) < 1, "内存解析")
        let gpus = Parsers.gpus(parsed.sections["gpu"]?.lines ?? [])
        check(gpus.count == 2 && gpus[0].utilization == 97 && gpus[1].memoryTotalMB == 81920, "GPU 解析")
        let jobs = Parsers.squeue(parsed.sections["squeue"]?.lines ?? [])
        check(jobs.count == 2 && jobs[0].state == .running && jobs[0].limit == 172800 && jobs[1].state == .pending,
              "squeue 解析")
        let prog = Parsers.progress(parsed.sections["progress"]?.lines ?? [])
        check(prog.count == 1 && prog[0].jobID == "4821337" && prog[0].progress == 0.62, "进度文件解析（跳过坏文件）")

        let procs = Parsers.processes([
            "  18342  9250 312.0 8123456 /usr/bin/python3 -u train_diffusion.py --lr 3e-4",
            "  20977   400   0.1   20480 -bash",
            "  20999    10   0.0    1024 ps -u yan -o pid=",
        ], filter: "python|torchrun", gpuMem: ["18342": 61000])
        check(procs.count == 1 && procs[0].name == "python3 train_diffusion.py" && procs[0].gpuMemoryMB == 61000,
              "直连进程过滤")

        let sacct = Parsers.sacct(["4821337|pretrain_7b|COMPLETED|11:42:05|0:0", "4821455|eval_mmlu|OUT_OF_MEMORY|00:42:18|0:125"])
        check(sacct["4821337"]?.state == .completed && sacct["4821455"]?.state == .failed, "sacct 最终状态")

        let script = RemoteScript.build(for: HostConfig(alias: "x", kind: .slurm))
        check(script.contains("squeue") && script.contains("@@SI:END"), "生成远端脚本")

        // 在本机 bash 上真实执行一遍直连脚本：macOS 没有 /proc，应当优雅地产生 warning 而不是中断。
        var direct = HostConfig(alias: "local", kind: .direct)
        direct.showGPU = true
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/bash")
        p.arguments = ["-s"]
        let inPipe = Pipe(), outPipe = Pipe()
        p.standardInput = inPipe; p.standardOutput = outPipe; p.standardError = outPipe
        try? p.run()
        try? inPipe.fileHandleForWriting.write(contentsOf: Data(RemoteScript.build(for: direct).utf8))
        try? inPipe.fileHandleForWriting.close()
        let out = String(decoding: outPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        p.waitUntilExit()
        let localParsed = RemoteScript.parse(out)
        check(localParsed.sawBegin && localParsed.sawEnd && localParsed.sections["procs"] != nil,
              "脚本可在 bash 中完整执行（各段：\(localParsed.sections.keys.sorted().joined(separator: ",")))")

        let df = Parsers.disks(["Filesystem 1024-blocks Used Available Capacity Mounted on",
                                "/dev/nvme0n1p2 960000000 912000000 48000000 95% /",
                                "nas:/home 2000000000 1000000000 1000000000 50% /mnt/my home"])
        check(df.count == 2 && abs(df[0].fraction - 0.95) < 0.001 && df[1].mount == "/mnt/my home", "df 解析（含空格挂载点）")

        let kv = Parsers.keyValues("JobId=4821337 JobName=pretrain 7b UserId=yan(1000) JobState=RUNNING Reason=None StdOut=/home/yan/logs/out-4821337.log WorkDir=/home/yan/proj")
        check(kv["JobName"] == "pretrain 7b" && kv["StdOut"] == "/home/yan/logs/out-4821337.log" && kv["JobState"] == "RUNNING",
              "scontrol key=value 解析")

        let hist = Parsers.history(["4821001|pretrain|COMPLETED|11:42:05|2026-10-16T09:12:00|0:0",
                                    "4821002|eval|FAILED|00:02:10|2026-10-16T10:00:00|1:0",
                                    "4821003|run|RUNNING|00:10:00|Unknown|0:0"])
        check(hist.count == 2 && hist[0].id == "4821002" && hist[0].jobState == .failed, "sacct 历史解析（跳过运行中）")
        let histDir = Parsers.history(["4821004|scf|COMPLETED|00:05:00|2026-10-16T11:00:00|0:0|/home/u/a|b"])
        check(histDir.first?.dir == "/home/u/a|b" && hist[0].dir == nil, "sacct 历史：WorkDir 列（老格式没有也能解析）")
        let merged = MonitorEngine.mergeHistory(
            server: [HistoryEntry(id: "1", name: "a", state: "COMPLETED", endedAt: Date(timeIntervalSince1970: 300)),
                     HistoryEntry(id: "2", name: "b", state: "FAILED", endedAt: Date(timeIntervalSince1970: 200))],
            local: [HistoryEntry(id: "1", name: "a", state: "ENDED", endedAt: Date(timeIntervalSince1970: 290), dir: "/x"),
                    HistoryEntry(id: "0", name: "old", state: "COMPLETED", endedAt: Date(timeIntervalSince1970: 100), dir: "/y")])
        check(merged.map(\.id) == ["1", "2", "0"] && merged[0].state == "COMPLETED" && merged[0].dir == "/x" && merged[2].dir == "/y",
              "历史：sacct 和本机记录合并（以 sacct 为准，保留目录）")
        MainActor.assumeIsolated {
            let tmp = FileManager.default.temporaryDirectory.appendingPathComponent("si-hist-\(UUID().uuidString)")
            defer { try? FileManager.default.removeItem(at: tmp) }
            let hid = UUID()
            let h1 = HistoryStore(directory: tmp)
            var running = RemoteJob(id: "77", name: "relax", state: .running, source: .slurm); running.workdir = "/w/relax"
            h1.setActive(hid, [running, RemoteJob(id: "file:x", name: "p", state: .running, source: .progressFile)])
            h1.record(hid, HistoryEntry(id: "70", name: "old", state: "COMPLETED", endedAt: Date().addingTimeInterval(-3 * 86_400), dir: "/w/old"))
            h1.record(hid, HistoryEntry(id: "60", name: "older", state: "COMPLETED", endedAt: Date().addingTimeInterval(-10 * 86_400)))
            let h2 = HistoryStore(directory: tmp)   // 模拟重启
            check(h2.entries(for: hid).map(\.id) == ["70"] && h2.entries(for: hid).first?.dir == "/w/old"
                  && h2.activeJobs(for: hid).map(\.id) == ["77"] && h2.activeJobs(for: hid).first?.dir == "/w/relax",
                  "历史：重启后还在（保留 7 天、带目录），记住上次运行中的任务")
        }

        // 卡片上的终端按钮：清屏、设窗口标题、显示服务器名、删掉脚本自己，再 exec ssh（这里把 ssh 换成 echo 真正跑一遍）
        do {
            let f = FileManager.default.temporaryDirectory.appendingPathComponent("si-shell-\(getpid()).command")
            let text = SSHRunner.shellScript(host: HostConfig(alias: "it's"), directory: "/w/it's dir")
                .replacingOccurrences(of: "exec ssh ", with: "exec echo SSH ")
            try? text.write(to: f, atomically: true, encoding: .utf8)
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = [f.path]
            p.environment = ["TERM": "dumb"]
            let out = Pipe()
            p.standardOutput = out
            p.standardError = FileHandle.nullDevice
            try? p.run()
            let s = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            p.waitUntilExit()
            check(s.contains("\u{1B}]0;ServerIsland · it's · /w/it's dir\u{07}") && s.contains("ServerIsland → it's（/w/it's dir）")
                  && s.contains("SSH ") && s.contains("cd '/w/it'\\''s dir' && exec $SHELL -l") && !FileManager.default.fileExists(atPath: f.path),
                  "终端按钮：窗口标题、服务器名、进目录的命令，脚本用完即删")
        }

        // 生成的详情 / 取消 / 历史脚本在 bash 下语法正确（只做 -n 语法检查，不执行）。
        var slurm = HostConfig(alias: "x", kind: .slurm); slurm.allowCancel = true
        let sj = RemoteJob(id: "4821337", name: "it's \"quoted\"", state: .running, source: .slurm)
        let pj = RemoteJob(id: "18342", name: "python3 train.py", state: .running, source: .process)
        let scripts = [
            RemoteScript.detailScript(host: slurm, job: sj),
            RemoteScript.detailScript(host: HostConfig(alias: "x"), job: pj),
            RemoteScript.cancelScript(host: slurm, job: sj, expectedElapsed: nil, force: false)!,
            RemoteScript.cancelScript(host: HostConfig(alias: "x"), job: pj, expectedElapsed: 9250, force: false)!,
            RemoteScript.cancelScript(host: HostConfig(alias: "x"), job: pj, expectedElapsed: 9250, force: true)!,
            RemoteScript.historyScript(host: slurm),
            RemoteScript.topProcessesScript(host: HostConfig(alias: "x")),
            AppDetectors.finalStateScript(id: "123", dir: "/home/u/it's dir"),
            RemoteScript.build(for: { var h = HostConfig(alias: "x"); h.watched = [WatchedProcess(pid: "1", name: "")]; return h }()),
            SSHRunner.shellScript(host: HostConfig(alias: "it's"), directory: "/w/it's dir"),
            SSHRunner.shellScript(host: HostConfig(alias: "x"), directory: nil),
        ]
        for (i, sc) in scripts.enumerated() {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/bin/bash")
            p.arguments = ["-n"]
            let inPipe = Pipe(), errPipe = Pipe()
            p.standardInput = inPipe; p.standardError = errPipe
            try? p.run()
            try? inPipe.fileHandleForWriting.write(contentsOf: Data(sc.utf8))
            try? inPipe.fileHandleForWriting.close()
            p.waitUntilExit()
            let err = String(decoding: errPipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            check(p.terminationStatus == 0, "脚本 \(i) bash 语法" + (err.isEmpty ? "" : "：" + err))
        }
        // Job Array
        let arr = Parsers.squeue([
            "123_5|sweep|lr|RUNNING|1:00|4:00:00|1|gpu01|a100|123|5",
            "123_6|sweep|lr|RUNNING|0:30|4:00:00|1|gpu02|a100|123|6",
            "123_[7-99%10]|sweep|lr|PENDING|0:00|4:00:00|1|(JobArrayTaskLimit)|a100|123|[7-99%10]",
            "200|single|RUNNING|0:10|1:00:00|1|gpu03|a100|200|N/A",
            "300_[1-4]|old|PENDING|0:00|1:00:00|1|(Priority)|a100",
        ])
        check(arr.count == 5 && arr[0].name == "sweep|lr" && arr[0].arrayJobID == "123" && arr[2].arrayTask == "[7-99%10]"
              && arr[3].arrayJobID == nil && arr[4].arrayJobID == "300", "squeue 数组解析（含名称里的 |、老格式）")
        check(Parsers.arrayTaskCount("[7-99%10]") == 93 && Parsers.arrayTaskCount("[1,3,5-7]") == 5
              && Parsers.arrayTaskCount("[1-99:2]") == 50, "数组范围计数")
        let grouped = Parsers.groupArrays(arr)
        let g = grouped.first { $0.id == "123" }
        check(grouped.count == 3 && g?.array?.running.count == 2 && g?.array?.pending == 93 && g?.state == .running
              && grouped.reduce(0, { $0 + $1.runningUnits }) == 3 && grouped.reduce(0, { $0 + $1.pendingUnits }) == 97,
              "数组合并为一行，按元素计数")
        // %K 不带方括号（"7-99%10"）时也按范围计数；以编号里的 _[...] 为准
        let noBracket = Parsers.groupArrays(Parsers.squeue([
            "400_1|scan|RUNNING|1:00|4:00:00|1|node01|cpu|400|1",
            "400_[2-50%4]|scan|PENDING|0:00|4:00:00|1|(JobArrayTaskLimit)|cpu|400|2-50%4",
            "500_[1,3,5-7]|scan2|PENDING|0:00|4:00:00|1|(Priority)|cpu|500|1,3,5-7",
        ]))
        check(noBracket.first { $0.id == "400" }?.array?.pending == 49 && noBracket.first { $0.id == "500" }?.array?.pending == 5
              && Parsers.pendingTaskCount(RemoteJob(id: "600_[1-10]", name: "x", state: .pending, source: .slurm)) == 10,
              "数组排队数：%K 不带方括号时按范围计数")
        let ah = Parsers.history(["123_1|sweep|COMPLETED|0:10:00|2026-10-16T09:00:00|0:0",
                                  "123_2|sweep|FAILED|0:01:00|2026-10-16T09:05:00|1:0",
                                  "123_3|sweep|COMPLETED|0:09:00|2026-10-16T09:06:00|0:0"])
        check(ah.count == 1 && ah[0].note == "数组 2✓ 1✗", "历史里的数组元素合并")
        check(RemoteScript.safeSlurmID("123_5") == "123_5" && RemoteScript.safeSlurmID("123;rm") == nil, "作业号校验")

        // 计算软件识别
        let appsOut = AppDetectors.parse([
            "@@SI:DIR 1275823|/home/u/nep/train",
            "kind=nep", "total=100000", "last=   12000 0.12345 0.01 0.002 0.003 0.08 0.01", "mtime=1760600000",
            "@@SI:DIR 4821337_5|/scratch/u/md/300K",
            "kind=gpumd", "total=2000000", "interval=1000",
            "neighbor=Neighbor info at step 1200000: radial(max=120,actual=48), angular(max=60,actual=21).", "mtime=1760600000",
            "@@SI:DIR 99|/scratch/u/md2", "kind=gpumd", "interval=100", "thermo_rows=500", "mtime=1",
        ])
        check(appsOut["1275823"]?.current == 12000 && appsOut["1275823"]?.fraction == 0.12
              && appsOut["1275823"]?.losses.first?.label == "Total" && appsOut["1275823"]?.losses.count == 6, "NEP loss.out / nep.in 解析")
        check(appsOut["4821337_5"]?.current == 1_200_000 && appsOut["4821337_5"]?.fraction == 0.6
              && appsOut["4821337_5"]?.neighbor?.radialActual == 48, "GPUMD neighbor.out / run.in 解析")
        check(appsOut["99"]?.current == 50_000 && appsOut["99"]?.hasTarget == false, "GPUMD thermo.out 退化路径（无 run 步数）")
        check(PendingReason.explain("DependencyNeverSatisfied")?.needsAction == true
              && PendingReason.explain("Dependency")?.short == "等待依赖作业"
              && PendingReason.explain("AssocGrpBillingRunMinutes")?.short == "组计费额度已满"
              && PendingReason.explain("Job's QOS not permitted to use this partition (cpu-debug allows short-qos)")?.needsAction == true,
              "排队原因翻译")
        // sinfo -N -O 的 Gres / GresUsed：NVIDIA 的 gpu、曙光（海光）的 dcu；"dcu:0,gpu:0" 不算一种卡
        let parts = Partitions.parse(RemoteScript.parse([
            "@@SI:SEC nodes",
            "c01  cpu*  mix    29/3/0/32  (null)              dcu:0,gpu:0",
            "d01  dcu   alloc  32/0/0/32  dcu:Hygon:4(S:0-3)  dcu:Hygon:4(IDX:0-3)",
            "d02  dcu   mix    8/24/0/32  dcu:Hygon:4(S:0-3)  dcu:Hygon:1(IDX:0)",
            "g01  gpu   idle   0/64/0/64  gpu:a100:8(S:0-1)   gpu:0",
            "@@SI:RC 0",
        ].joined(separator: "\n"))) ?? []
        let dcuP = parts.first { $0.name == "dcu" }, cpuP = parts.first { $0.name == "cpu" }, gpuP = parts.first { $0.name == "gpu" }
        check(dcuP?.gpuTotal == 8 && dcuP?.gpuIdle == 3 && dcuP?.gpuLabel == "DCU" && dcuP?.gpuModels == ["Hygon"]
              && cpuP?.gpuTotal == 0 && cpuP?.gpuLabel == "GPU" && cpuP?.cpuIdle == 3 && cpuP?.isDefault == true
              && gpuP?.gpuIdle == 8 && gpuP?.gpuLabel == "GPU"
              && Partitions.gpuCount("gpu:v100:2,dcu:Hygon:4").count == 6
              && PartitionStat.label(["gpu", "dcu"]) == "DCU/GPU", "分区资源：GPU 和 DCU（Gres）")

        let cancelS = RemoteScript.cancelScript(host: slurm, job: sj, expectedElapsed: nil, force: false)!
        check(cancelS.contains("squeue -h -j 4821337") && cancelS.contains("scancel 4821337"), "取消前先核对作业")
        SelfTestApps.run(check)
        SelfTestConnection.run(check)
        SelfTestPBS.run(check)
        SelfTestMonitor.run(check)
        SelfTestBilling.run(check)
        SelfTestPush.run(check)
        return ok
    }

    @MainActor
    static func diagnose(alias: String, kind: HostKind, alsoProcesses: Bool = false) async {
        let store = ConfigStore(persist: false)
        var host = HostConfig(alias: alias, kind: kind)
        host.slurmAlsoProcesses = alsoProcesses
        // 已添加到 ServerIsland 的服务器（按别名或显示名称）：用它的全部连接设置（手动地址、跳板机、认证）。
        if let data = try? Data(contentsOf: ConfigStore.fileURL),
           let saved = try? JSONDecoder().decode(AppSettings.self, from: data),
           var configured = saved.hosts.first(where: { $0.alias == alias || $0.displayName == alias }) {
            configured.kind = kind
            configured.slurmAlsoProcesses = alsoProcesses
            host = configured
            print("（使用设置里的“\(host.displayName)”：\(SSHHosts.summary(host))，跳板机 \(SSHHosts.chain(host, in: Dictionary(saved.hosts.map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })).map(\.displayName)))")
        }
        store.settings.hosts = [host]
        // 跳板机要能在全部服务器里找到（上面那行只放了这一台）。
        if let data = try? Data(contentsOf: ConfigStore.fileURL),
           let saved = try? JSONDecoder().decode(AppSettings.self, from: data) {
            SSHHosts.update(saved.hosts, write: true)
        }
        let engine = MonitorEngine(store: store)
        print(await engine.diagnose(host))
        print("\n===== 解析结果 =====")
        await engine.pollForDiagnosis(host)
        guard let snap = engine.snapshots[host.id] else { return }
        print("health:", snap.health)
        print("metrics:", snap.metrics)
        for g in snap.gpus { print("gpu:", g) }
        for j in snap.jobs {
            print("job:", j.id, j.name, j.state, j.elapsed ?? -1, j.detail.prefix(80))
            if let m = j.message { print("     message:", m, "progress:", j.progress.map { Format.percent($0) } ?? "-") }
            if let a = j.app { print("     losses:", a.losses.map { "\($0.label)=\($0.value)" }.joined(separator: " ")) }
        }
        print("warnings:", snap.warnings)
        for d in snap.disks { print("disk:", d.mount, Format.percent(d.fraction)) }
        print("\n===== 高负载进程（监听候选） =====")
        let top = await engine.topProcesses(host: host) ?? []
        for p in top { print(p.id, p.name, p.cpuPercent ?? 0, p.gpuMemoryMB ?? 0) }
        if let first = top.first {
            print("\n===== 详情：pid \(first.id) =====")
            let d = await engine.fetchDetail(host: host, job: first)
            for (k, v) in d.fields { print(k, "=", v) }
            print("stdout:", d.stdout.path ?? "-", d.stdout.note ?? "", "lines:", d.stdout.lines.count)
            d.stdout.lines.suffix(5).forEach { print("  |", $0) }
            if let e = d.stderr { print("stderr:", e.path ?? "-", e.note ?? "") }
            print("error:", d.error ?? "-")
        }
        let h = await engine.fetchHistory(host: host)
        print("\nhistory: fromServer=\(h.fromServer) count=\(h.entries.count)")
        if host.kind.isScheduler { await diagnoseBilling(engine: engine, store: store, host: host) }
    }

    /// 计费（只读）：用临时单价（每卡时 1、其他每核时 0.01）再采一轮，打印每个作业的分区、分到的资源和估算费用；
    /// 再用 sacct / qstat -x 查最近结束的作业的 AllocTRES，读取分区列表。不改设置、不写账本。
    @MainActor
    static func diagnoseBilling(engine: MonitorEngine, store: ConfigStore, host original: HostConfig) async {
        print("\n===== 计费（临时单价，只读） =====")
        var host = original
        let parts = await engine.fetchBillingPartitions(host: host)
        print("partitions:", parts.error ?? parts.list.map { "\($0.name)[\($0.note)]" }.joined(separator: "  "))
        // 设置里已经填了单价就用它，否则临时单价
        if !host.prices.contains(where: { $0.price > 0 }) {
            host.prices = [PartitionPrice(partition: "", unit: .cpu, price: 0.01)]
            for p in parts.list where p.hasGPU { host.prices.append(PartitionPrice(partition: p.name, unit: .gpu, price: 1)) }
        }
        print("prices:", host.prices.map { "\($0.partition.isEmpty ? "*" : $0.partition)=\($0.price)/\($0.unit.rawValue)" })
        store.settings.hosts = [host]
        store.settings.billingEnabled = true
        let query = engine.allocQuery(host) ?? RemoteScript.AllocRequest()
        print("alloc query:", query)
        do {
            let raw = await SSHRunner.run(host: host, remoteCommand: host.commands.shell, script: RemoteScript.build(for: host, alloc: query))
            let sec = RemoteScript.parse(raw.stdout).sections["alloc"]
            print("alloc section (rc \(sec?.exitCode ?? -1)):", sec?.lines ?? [])
        }
        await engine.pollForDiagnosis(host)
        for j in engine.snapshots[host.id]?.jobs ?? [] where j.source == .slurm {
            let c = engine.liveCost(host, j)
            print("job:", j.id, j.state, "partition=\(j.partition ?? "-") nodes=\(j.nodes ?? -1) alloc=\(j.alloc.map { "cpu \($0.cpus ?? -1) gpu \($0.gpus ?? -1) node \($0.nodes ?? -1)" } ?? "-")",
                  c.map { "≈\(Billing.money($0.spent, currency: "¥")) \(Billing.money($0.perHour, currency: "¥"))/h \($0.detail)" } ?? "")
        }
        let ids = await engine.fetchHistory(host: host).entries.prefix(3).map(\.id)
        if !ids.isEmpty {
            let r = await SSHRunner.run(host: host, remoteCommand: host.commands.shell,
                                        script: RemoteScript.sacctScript(for: host, jobIDs: ids), timeout: 20)
            let lines = RemoteScript.parse(r.stdout).sections["sacct"]?.lines ?? []
            print("sacct:", lines.prefix(3))
            print("sacct allocs:", Parsers.sacctAllocs(lines))
        }
        store.settings.billingEnabled = false
    }
}
