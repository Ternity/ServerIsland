import Foundation

enum Parsers {
    // MARK: - CPU / 内存

    struct CPUSample: Equatable {
        var busy: Double
        var total: Double
    }

    /// 解析 `head -1 /proc/stat; nproc; cat /proc/loadavg`。
    static func cpu(_ lines: [String]) -> (sample: CPUSample?, cores: Int?, load1: Double?) {
        var sample: CPUSample?
        var cores: Int?
        var load: Double?
        for line in lines {
            let parts = line.split(separator: " ").map(String.init)
            if parts.first == "cpu" {
                let nums = parts.dropFirst().compactMap(Double.init)
                if nums.count >= 4 {
                    // user nice system idle iowait irq softirq steal
                    let idle = nums[3] + (nums.count > 4 ? nums[4] : 0)
                    let total = nums.prefix(8).reduce(0, +)
                    sample = CPUSample(busy: total - idle, total: total)
                }
            } else if parts.count == 1, let n = Int(parts[0]) {
                cores = n
            } else if parts.count >= 3, let l = Double(parts[0]), parts[3...].first?.contains("/") ?? false {
                load = l
            }
        }
        return (sample, cores, load)
    }

    static func memory(_ lines: [String]) -> (used: Double, total: Double)? {
        var total: Double?, available: Double?
        for line in lines {
            let parts = line.split(whereSeparator: { $0 == " " || $0 == ":" }).map(String.init)
            guard parts.count >= 2, let kb = Double(parts[1]) else { continue }
            if parts[0] == "MemTotal" { total = kb * 1024 }
            if parts[0] == "MemAvailable" { available = kb * 1024 }
        }
        guard let t = total, let a = available else { return nil }
        return (t - a, t)
    }

    // MARK: - GPU

    static func gpus(_ lines: [String]) -> [GPUStat] {
        lines.compactMap { line in
            let f = line.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            guard f.count >= 5, let idx = Int(f[0]) else { return nil }
            return GPUStat(index: idx, name: f[1],
                           utilization: Double(f[2]) ?? 0,
                           memoryUsedMB: Double(f[3]) ?? 0,
                           memoryTotalMB: Double(f[4]) ?? 0,
                           temperature: f.count > 5 ? Double(f[5]) : nil)
        }
    }

    /// pid -> 显存占用（MB）
    static func gpuApps(_ lines: [String]) -> [String: Double] {
        var map: [String: Double] = [:]
        for line in lines {
            let f = line.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }
            guard f.count >= 2, let mem = Double(f[1]) else { continue }
            map[f[0], default: 0] += mem
        }
        return map
    }

    // MARK: - PBS

    /// `qstat -f` 的一个作业：`    key = value`，续行以 Tab 开头。Variable_List 里的 PBS_O_WORKDIR 单独取出来。
    static func pbsKeyValues(_ lines: [String]) -> [String: String] {
        var kv: [String: String] = [:]
        var key: String?
        for raw in lines {
            let line = raw.hasSuffix("\r") ? String(raw.dropLast()) : raw
            if line.hasPrefix("\t") {
                if let k = key { kv[k, default: ""] += line.drop(while: { $0 == "\t" }) }
                continue
            }
            key = nil
            let t = line.drop(while: { $0 == " " })
            guard t.count < line.count, let r = t.range(of: " = ") else { continue }
            let k = String(t[..<r.lowerBound])
            if kv[k] == nil { kv[k] = String(t[r.upperBound...]); key = k }
        }
        if let vars = kv["Variable_List"], let r = vars.range(of: #"PBS_O_WORKDIR=[^,]*"#, options: .regularExpression) {
            kv["PBS_O_WORKDIR"] = String(vars[r].dropFirst("PBS_O_WORKDIR=".count))
        }
        return kv
    }

    // MARK: - 目录过滤

    /// workdirs 段：`id|目录`，以及 `@@SI:HOME 家目录`。
    static func workdirs(_ lines: [String]) -> (home: String?, dirs: [String: String]) {
        var home: String?
        var dirs: [String: String] = [:]
        for line in lines {
            if line.hasPrefix(RemoteScript.marker + "HOME ") { home = String(line.dropFirst(RemoteScript.marker.count + 5)); continue }
            guard let bar = line.firstIndex(of: "|") else { continue }
            let id = String(line[..<bar]).trimmingCharacters(in: .whitespaces)
            let dir = String(line[line.index(after: bar)...])
            if !id.isEmpty && !dir.isEmpty { dirs[id] = dir }
        }
        return (home, dirs)
    }

    /// 目录是否在某个过滤目录里（含子目录）。`~` 按远端家目录展开；末尾的 / 忽略。
    static func pathMatches(_ dir: String, prefixes: [String], home: String?) -> Bool {
        func norm(_ p: String) -> String {
            var s = p.trimmingCharacters(in: .whitespaces)
            if let home, s == "~" || s.hasPrefix("~/") { s = home + s.dropFirst() }
            while s.count > 1 && s.hasSuffix("/") { s.removeLast() }
            return s
        }
        let d = norm(dir)
        return prefixes.contains { raw in
            let p = norm(raw)
            return p == "/" || d == p || d.hasPrefix(p + "/")
        }
    }

    // MARK: - Slurm

    /// `%i|%j|%T|%M|%l|%D|%R|%P|%F|%K`（后两列是数组主编号和元素序号；老格式没有也能解析）。
    /// 作业名里可能含 `|`，所以除名称外的列从两端按位置取。
    static func squeue(_ lines: [String]) -> [RemoteJob] {
        lines.compactMap { line in
            let f = line.components(separatedBy: "|").map { $0.trimmingCharacters(in: .whitespaces) }
            guard f.count >= 3, !f[0].isEmpty else { return nil }
            // 新格式在名称后面还有 8 列，老格式 6 列；用倒数第二列是否为纯数字（%F）来判断。
            let tail: Int = {
                if f.count >= 10, f[f.count - 2].allSatisfy(\.isNumber), !f[f.count - 2].isEmpty { return 8 }
                if f.count >= 8 { return 6 }
                return max(0, f.count - 2)
            }()
            let nameEnd = f.count - tail
            let name = f[1..<max(nameEnd, 2)].joined(separator: "|")
            let rest = Array(f[max(nameEnd, 2)...])
            func col(_ i: Int) -> String { i < rest.count ? rest[i] : "" }

            let id = f[0]
            let raw = col(0)
            var job = RemoteJob(id: id, name: name, state: JobState(slurm: raw), rawState: raw, source: .slurm)
            job.elapsed = slurmDuration(col(1))
            job.limit = slurmDuration(col(2))
            var detail: [String] = []
            let reason = col(4)
            if !reason.isEmpty && reason != "(null)" { detail.append(reason) }
            if job.state == .pending, reason.hasPrefix("(") {
                job.reason = String(reason.dropFirst().dropLast())
            }
            if !col(5).isEmpty { detail.append(col(5)) }
            job.detail = detail.joined(separator: " · ")
            if !col(5).isEmpty { job.partition = col(5) }
            job.nodes = Int(col(3))

            // 数组：优先用 %F/%K，否则从 "123_5" / "123_[7-99%10]" 里拆。
            if tail == 8, col(7) != "N/A", !col(7).isEmpty, col(6) != id {
                job.arrayJobID = col(6)
                job.arrayTask = col(7)
            } else if let m = id.range(of: #"^(\d+)_(\d+|\[.+\])$"#, options: .regularExpression), m.lowerBound == id.startIndex,
                      let us = id.firstIndex(of: "_") {
                job.arrayJobID = String(id[..<us])
                job.arrayTask = String(id[id.index(after: us)...])
            }
            return job
        }
    }

    /// "[7-99%10]" -> 93；"[1,3,5-7]" -> 5；"[1-99:2]" -> 50；"5" -> 1
    static func arrayTaskCount(_ spec: String) -> Int {
        var s = spec.trimmingCharacters(in: CharacterSet(charactersIn: "[] "))
        if let pct = s.firstIndex(of: "%") { s = String(s[..<pct]) }
        var n = 0
        for part in s.split(separator: ",") {
            let stepParts = part.split(separator: ":")
            let step = stepParts.count > 1 ? max(1, Int(stepParts[1]) ?? 1) : 1
            let range = stepParts[0].split(separator: "-").compactMap { Int($0) }
            if range.count == 2, range[1] >= range[0] { n += (range[1] - range[0]) / step + 1 }
            else if range.count == 1 { n += 1 }
        }
        return max(n, 1)
    }

    /// 一行排队的数组记录代表几个元素。范围以编号里的 "123_[7-99%10]" 为准（总是带方括号）；
    /// %K 列在不同 Slurm 版本里可能不带方括号（"7-99%10"），以前只认带括号的，排队数被算成 1，进度条就偏满了。
    static func pendingTaskCount(_ job: RemoteJob) -> Int {
        if let l = job.id.range(of: "_["), job.id.hasSuffix("]") {
            return arrayTaskCount(String(job.id[l.upperBound...]))
        }
        guard let task = job.arrayTask, task.contains(where: { "-,[".contains($0) }) else { return 1 }
        return arrayTaskCount(task)
    }

    /// 把同一个数组的元素合并成一行。done / failed / total 由引擎补充。
    static func groupArrays(_ jobs: [RemoteJob]) -> [RemoteJob] {
        var out: [RemoteJob] = []
        var groups: [String: Int] = [:]
        for job in jobs {
            guard job.source == .slurm, let base = job.arrayJobID else { out.append(job); continue }
            let i: Int
            if let existing = groups[base] {
                i = existing
            } else {
                var g = RemoteJob(id: base, name: job.name, state: .pending, source: .slurm)
                g.arrayJobID = base
                g.array = ArrayInfo(baseID: base)
                g.limit = job.limit
                g.workdir = job.workdir
                g.partition = job.partition
                out.append(g)
                i = out.count - 1
                groups[base] = i
            }
            if job.state == .pending {
                out[i].array!.pending += pendingTaskCount(job)
                if out[i].detail.isEmpty { out[i].detail = job.detail }
                if out[i].reason == nil { out[i].reason = job.reason }
            } else {
                out[i].array!.running.append(job)
                out[i].state = .running
                out[i].elapsed = max(out[i].elapsed ?? 0, job.elapsed ?? 0)
            }
        }
        for i in out.indices where out[i].array != nil {
            out[i].array!.running.sort { (Int($0.arrayTask ?? "") ?? 0) < (Int($1.arrayTask ?? "") ?? 0) }
        }
        return out
    }

    /// sacct -P 输出：JobID|JobName|State|Elapsed|ExitCode
    static func sacct(_ lines: [String]) -> [String: (state: JobState, raw: String, elapsed: TimeInterval?, exit: String)] {
        var out: [String: (JobState, String, TimeInterval?, String)] = [:]
        for line in lines {
            let f = line.components(separatedBy: "|")
            guard f.count >= 3 else { continue }
            let id = f[0].trimmingCharacters(in: .whitespaces)
            let raw = f[2].trimmingCharacters(in: .whitespaces)
            out[id] = (JobState(slurm: raw), raw, f.count > 3 ? slurmDuration(f[3]) : nil, f.count > 4 ? f[4] : "")
        }
        return out
    }

    /// 计费用的资源：`alloc` 段（`作业号|分到的资源|每节点申请|整个作业申请|节点数`，PBS 只有前两列）
    /// 和 sacct / `si_pbs a` 的最后一列（AllocTRES）。认不出的行跳过。
    static func allocs(_ lines: [String]) -> [String: JobAlloc] {
        var out: [String: JobAlloc] = [:]
        for line in lines {
            let f = line.components(separatedBy: "|")
            guard f.count >= 2 else { continue }
            let id = f[0].trimmingCharacters(in: .whitespaces)
            func col(_ i: Int) -> String { i < f.count ? f[i] : "" }
            guard !id.isEmpty, let a = JobAlloc.parse(f[1], perNode: col(2), perJob: col(3), nodes: Int(col(4).prefix { $0.isNumber })) else { continue }
            out[id] = a
        }
        return out
    }

    /// `qstat -Q` 的队列名（跳过表头和分隔线）。
    static func pbsQueues(_ lines: [String]) -> [String] {
        var out: [String] = []
        for line in lines {
            guard let first = line.split(separator: " ", omittingEmptySubsequences: true).first.map(String.init) else { continue }
            if first == "Queue" || first.hasPrefix("-") || first.hasPrefix("qstat") || out.contains(first) { continue }
            out.append(first)
        }
        return out
    }

    static func sacctAllocs(_ lines: [String]) -> [String: JobAlloc] {
        var out: [String: JobAlloc] = [:]
        for line in lines {
            let f = line.components(separatedBy: "|")
            guard f.count >= 6, let last = f.last, last.contains("="), let a = JobAlloc.parse(last) else { continue }
            out[f[0].trimmingCharacters(in: .whitespaces)] = a
        }
        return out
    }

    /// Slurm 时间格式：`[days-]hours:minutes:seconds`、`minutes:seconds`、`UNLIMITED` 等。
    static func slurmDuration(_ s: String) -> TimeInterval? {
        let str = s.trimmingCharacters(in: .whitespaces)
        guard !str.isEmpty, str.first?.isNumber == true else { return nil }
        var days = 0.0
        var rest = Substring(str)
        if let dash = str.firstIndex(of: "-") {
            days = Double(str[..<dash]) ?? 0
            rest = str[str.index(after: dash)...]
        }
        let parts = rest.split(separator: ":").compactMap { Double($0) }
        var secs = 0.0
        switch parts.count {
        case 3: secs = parts[0] * 3600 + parts[1] * 60 + parts[2]
        case 2: secs = parts[0] * 60 + parts[1]
        case 1: secs = days > 0 ? parts[0] * 3600 : parts[0] * 60   // "1-12" 表示 1 天 12 小时；单独数字为分钟
        default: return nil
        }
        return days * 86400 + secs
    }

    // MARK: - 直连进程

    static func processes(_ lines: [String], filter: String, exclude: String = "", gpuMem: [String: Double],
                          alwaysInclude: Set<String> = []) -> [RemoteJob] {
        let regex = try? NSRegularExpression(pattern: filter, options: [.caseInsensitive])
        let excludeRegex = exclude.isEmpty ? nil : try? NSRegularExpression(pattern: exclude, options: [.caseInsensitive])
        var jobs: [RemoteJob] = []
        for line in lines {
            let parts = line.split(separator: " ", maxSplits: 4, omittingEmptySubsequences: true).map(String.init)
            guard parts.count == 5, let pid = Int(parts[0]) else { continue }
            let args = parts[4]
            // 过滤掉我们自己的采集脚本和 ssh 会话。
            if args.contains("@@SI:") || args.hasPrefix("ps ") || args.hasPrefix("head ") || args.contains("sshd")
                || args == "bash -l -s" || args.hasPrefix("bash -l -s") { continue }
            // 系统 / 桌面进程（如 Ubuntu 的 update-manager、gnome-remote-desktop）不算任务。
            if let excludeRegex, excludeRegex.firstMatch(in: args, range: NSRange(args.startIndex..., in: args)) != nil { continue }
            let matchesFilter: Bool = {
                guard let regex, !filter.isEmpty else { return true }
                return regex.firstMatch(in: args, range: NSRange(args.startIndex..., in: args)) != nil
            }()
            let onGPU = gpuMem[String(pid)] != nil
            guard matchesFilter || onGPU || alwaysInclude.contains(String(pid)) else { continue }
            var job = RemoteJob(id: String(pid), name: prettyCommand(args), state: .running, source: .process)
            job.elapsed = Double(parts[1])
            job.cpuPercent = Double(parts[2])
            job.memoryBytes = Double(parts[3]).map { $0 * 1024 }
            job.gpuMemoryMB = gpuMem[String(pid)]
            job.detail = args
            jobs.append(job)
        }
        // 显存占用多、运行时间长的排前面；最多保留 12 个。
        return Array(jobs.sorted {
            ($0.gpuMemoryMB ?? 0, $0.elapsed ?? 0) > ($1.gpuMemoryMB ?? 0, $1.elapsed ?? 0)
        }.prefix(12))
    }

    /// "/usr/bin/python3 -u train.py --lr 3e-4" -> "python3 train.py"
    static func prettyCommand(_ args: String) -> String {
        let tokens = args.split(separator: " ").map(String.init)
        guard let first = tokens.first else { return args }
        let exe = (first as NSString).lastPathComponent
        if let script = tokens.dropFirst().first(where: { !$0.hasPrefix("-") && ($0.contains(".") || $0.contains("/")) }) {
            return exe + " " + (script as NSString).lastPathComponent
        }
        if tokens.count > 1, tokens[1] == "-m", tokens.count > 2 { return exe + " -m " + tokens[2] }
        return exe
    }

    // MARK: - 磁盘

    /// `df -P -k` 输出：Filesystem 1024-blocks Used Available Capacity Mounted-on
    static func disks(_ lines: [String]) -> [DiskStat] {
        var out: [DiskStat] = []
        for line in lines {
            let f = line.split(separator: " ", omittingEmptySubsequences: true).map(String.init)
            guard f.count >= 6, let total = Double(f[1]), let used = Double(f[2]), total > 0 else { continue }
            let mount = f[5...].joined(separator: " ")
            if out.contains(where: { $0.mount == mount }) { continue }
            out.append(DiskStat(mount: mount, usedKB: used, totalKB: total))
        }
        return out
    }

    // MARK: - 详情 / 历史

    /// `scontrol show job -o` 的单行 key=value。值里可能有空格（如 Command），按"下一个 key="切分。
    static func keyValues(_ line: String) -> [String: String] {
        var out: [String: String] = [:]
        let regex = try! NSRegularExpression(pattern: #"(?:^|\s)([A-Za-z_:/]+)="#)
        let ns = line as NSString
        let matches = regex.matches(in: line, range: NSRange(location: 0, length: ns.length))
        for (i, m) in matches.enumerated() {
            let key = ns.substring(with: m.range(at: 1))
            let start = m.range.location + m.range.length
            let end = i + 1 < matches.count ? matches[i + 1].range.location : ns.length
            out[key] = ns.substring(with: NSRange(location: start, length: max(0, end - start)))
                .trimmingCharacters(in: .whitespaces)
        }
        return out
    }

    /// sacct -P：JobID|JobName|State|Elapsed|End|ExitCode
    static func history(_ lines: [String]) -> [HistoryEntry] {
        let iso = DateFormatter()
        iso.locale = Locale(identifier: "en_US_POSIX")
        iso.dateFormat = "yyyy-MM-dd'T'HH:mm:ss"
        var entries: [HistoryEntry] = []
        for line in lines {
            let f = line.components(separatedBy: "|")
            guard f.count >= 5, !f[0].isEmpty else { continue }
            let state = JobState(slurm: f[2])
            guard !state.isActive else { continue }   // 还在跑的不算历史
            let dir = f.count > 6 ? f[6...].joined(separator: "|") : ""
            entries.append(HistoryEntry(id: f[0], name: f[1], state: f[2], elapsed: slurmDuration(f[3]),
                                        endedAt: iso.date(from: f[4]) ?? Date(), exitCode: f.count > 5 ? f[5] : nil,
                                        dir: dir.isEmpty ? nil : dir))
        }
        entries.sort { $0.endedAt > $1.endedAt }

        // 数组元素合并成一条，备注里记 "数组 97✓ 3✗"。
        var out: [HistoryEntry] = []
        var counts: [String: (ok: Int, bad: Int)] = [:]
        for e in entries {
            guard let us = e.id.firstIndex(of: "_") else { out.append(e); continue }
            let base = String(e.id[..<us])
            let failed = [JobState.failed, .timeout].contains(e.jobState)
            var c = counts[base] ?? (0, 0)
            if failed { c.bad += 1 } else { c.ok += 1 }
            counts[base] = c
            if let i = out.firstIndex(where: { $0.id == base }) {
                if failed { out[i].state = e.state }
                out[i].elapsed = max(out[i].elapsed ?? 0, e.elapsed ?? 0)
                out[i].note = L("数组 ", "array ") + "\(c.ok)✓ \(c.bad)✗"
            } else {
                out.append(HistoryEntry(id: base, name: e.name, state: e.state, elapsed: e.elapsed, endedAt: e.endedAt,
                                        exitCode: nil, note: L("数组 ", "array ") + "\(c.ok)✓ \(c.bad)✗", dir: e.dir))
            }
        }
        return out
    }

    // MARK: - 进度文件

    struct ProgressEntry {
        var id: String
        var name: String
        var progress: Double?
        var message: String?
        var jobID: String?
        var updated: Date?
        var done: Bool
        var failed: Bool
    }

    /// 进度文件格式（字段均可选）：
    /// {"name": "train", "progress": 0.42, "message": "epoch 3/10", "jobid": "12345",
    ///  "current": 42, "total": 100, "updated": 1730000000, "status": "running|done|failed"}
    static func progress(_ lines: [String]) -> [ProgressEntry] {
        var entries: [ProgressEntry] = []
        var path: String?
        var buffer: [String] = []

        func flush() {
            defer { buffer = [] }
            guard let path else { return }
            let json = buffer.joined(separator: "\n")
            guard let data = json.data(using: .utf8),
                  let obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return }
            let fileName = ((path as NSString).lastPathComponent as NSString).deletingPathExtension
            var p: Double?
            if let v = number(obj["progress"]) { p = v > 1 ? v / 100 : v }
            else if let cur = number(obj["current"]), let tot = number(obj["total"]), tot > 0 { p = cur / tot }
            let status = (obj["status"] as? String)?.lowercased() ?? ""
            let updated = number(obj["updated"]).map { Date(timeIntervalSince1970: $0) }
            let jobID = (obj["jobid"] as? String) ?? number(obj["jobid"]).map { String(Int($0)) }
            entries.append(ProgressEntry(
                id: fileName,
                name: (obj["name"] as? String) ?? fileName,
                progress: p,
                message: obj["message"] as? String,
                jobID: jobID,
                updated: updated,
                done: status == "done" || status == "completed" || status == "finished",
                failed: status == "failed" || status == "error"))
        }

        for line in lines {
            if line.hasPrefix(RemoteScript.marker + "FILE ") {
                flush()
                path = String(line.dropFirst((RemoteScript.marker + "FILE ").count))
            } else {
                buffer.append(line)
            }
        }
        flush()
        return entries
    }

    private static func number(_ v: Any?) -> Double? {
        if let d = v as? Double { return d }
        if let i = v as? Int { return Double(i) }
        if let s = v as? String { return Double(s) }
        return nil
    }
}
