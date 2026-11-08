import Foundation

/// 一次轮询只开一个 SSH 通道：把所有要采集的命令拼成一个脚本，
/// 每段输出用 `@@SI:` 标记分隔，并带上退出码，方便定位哪条命令有问题。
enum RemoteScript {
    static let marker = "@@SI:"

    /// 计费要查的资源：`known` 是已经知道资源的作业（默认 squeue 时脚本自己从这一轮的列表里找新作业，跳过它们）；
    /// `query` 是自定义了 squeue 时要查的作业（上一轮看到的运行中作业）。
    struct AllocRequest: Equatable {
        var query: [String] = []
        var known: [String] = []
    }

    /// 默认的 squeue 命令后面再接上状态和工作目录（用 `|@@SI:WD|` 隔开），一次查询同时给任务列表、工作目录和计算软件识别用。
    static let squeueWithWorkdir = #"squeue -u "$(id -un)" -h -o '%i|%j|%T|%M|%l|%D|%R|%P|%F|%K|@@SI:WD|%T|%Z'"#

    /// 一个周期里的数据尽量只取一次，存在 shell 变量里给后面的段用（各段都在同一个 shell 里执行）：
    /// - `SI_JW`（Slurm）：`作业号|状态|工作目录`；`SI_PQ`（PBS）：`qstat -f` 全文。调度系统每个周期只查询一次。
    /// - `SI_PT`：自己的每个进程 `PID|所属作业号|当前目录`，只遍历一遍 /proc。
    /// - `SI_GA`：nvidia-smi 的计算进程，计算软件识别直接用。
    /// `appSignatures`：上次识别结果的文件签名（目录 → 签名），文件没变的目录不再重读（见 `AppDetectors.script`）。
    /// `includeDisk`：磁盘占用变化慢，由引擎决定隔几分钟查一次。
    /// `alloc`：打开计费时不为 nil。Slurm 是还不知道分到多少资源的运行中作业（每个作业只查一次 `scontrol show job`，
    /// 新版本的字段叫 AllocTRES，老版本叫 TRES）。输出 `作业号|分到的资源|每节点申请的资源|整个作业申请的资源|节点数`：
    /// 有的 Slurm 没把 gres/gpu 写进 AccountingStorageTRES，分到的资源里就没有卡，这时按申请的卡数算（见 `JobAlloc.parse`）；
    /// PBS 直接从这一轮的 `qstat -f` 全文里取，不再查询。
    static func build(for host: HostConfig, appSignatures: [String: String] = [:], includeDisk: Bool = true,
                      alloc: AllocRequest? = nil) -> String {
        let c = host.commands
        var lines: [String] = ["printf '\(marker)BEGIN\\n'"]
        if !c.preamble.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            lines.append(c.preamble)
        }
        lines.append(#"si_rc() { return "$1"; }"#)
        func section(_ name: String, _ body: String) {
            lines.append("printf '\(marker)SEC \(name)\\n'")
            lines.append("{ \(body)\n} 2>&1")
            lines.append("printf '\(marker)RC %s\\n' \"$?\"")
        }

        if host.showCPU {
            section("cpu", "head -n 1 /proc/stat; nproc; cat /proc/loadavg")
        }
        if host.showMemory {
            section("mem", "grep -E '^(MemTotal|MemAvailable):' /proc/meminfo")
        }
        let listsProcesses = host.kind == .direct || (host.kind.isScheduler && host.slurmAlsoProcesses)
        if host.showGPU || (listsProcesses && host.watchJobs) {
            // 没有 nvidia-smi 的机器直接跳过，不算错误。
            section("gpu", "if command -v nvidia-smi >/dev/null 2>&1; then \(c.gpu); else echo '\(marker)NA'; fi")
            if listsProcesses && host.watchJobs {
                section("gpuapps", #"if command -v nvidia-smi >/dev/null 2>&1; then SI_GA=$("# + c.gpuApps
                        + #"); SI_RC=$?; [ -z "$SI_GA" ] || printf '%s\n' "$SI_GA"; si_rc $SI_RC; else echo '"# + marker + "NA'; fi")
            }
        }
        if host.watchJobs {
            switch host.kind {
            case .slurm:
                if c.squeue == CommandSet.defaults.squeue {
                    section("squeue", """
                    SI_Q=$(\(squeueWithWorkdir) 2>&1); SI_RC=$?
                    [ -z "$SI_Q" ] || printf '%s\\n' "$SI_Q" | sed 's/|@@SI:WD|.*$//'
                    SI_JW=$(printf '%s\\n' "$SI_Q" | sed -n 's/^\\([^|]*\\)|.*|@@SI:WD|/\\1|/p')
                    si_rc $SI_RC
                    """)
                } else {
                    section("squeue", c.squeue)
                }
            case .pbs:
                section("qstat", pbsFunction + """
                if command -v qstat >/dev/null 2>&1; then SI_PQ=$({ \(c.qstat)
                }); printf '%s\\n' "$SI_PQ" | si_pbs q; else echo 'qstat: command not found'; false; fi
                """)
            case .direct:
                break
            }
            if let alloc {
                let body = #"SI_S=$(scontrol show job -o "$j" 2>/dev/null | head -n 1 | tr ' ' '\n'); "#
                    + #"si_v() { printf '%s\n' "$SI_S" | sed -n "s/^$1=//p" | head -n 1; }; "#
                    + #"printf '%s|%s|%s|%s|%s\n' "$j" "$(si_v AllocTRES)$(si_v TRES)" "$(si_v TresPerNode)" "$(si_v TresPerJob)" "$(si_v NumNodes)"; done; true"#
                if host.kind == .slurm && c.squeue == CommandSet.defaults.squeue {
                    // 默认 squeue：这一轮的运行中作业就在 SI_JW 里，跳过已经知道资源的，同一轮就能查到新作业（一轮最多 20 个）。
                    let known = alloc.known.compactMap(safeSlurmID).prefix(300).joined(separator: " ")
                    section("alloc", "SI_K=' \(known) '; SI_N=0; for j in $(printf '%s\\n' \"$SI_JW\" | awk -F'|' '$2 == \"RUNNING\" { print $1 }'); do "
                            + #"case "$SI_K" in *" $j "*) continue;; esac; SI_N=$((SI_N + 1)); [ "$SI_N" -le 20 ] || break; "# + body)
                } else if host.kind == .slurm {
                    // 自定义了 squeue：查上一轮看到、还不知道资源的运行中作业。
                    let ids = alloc.query.compactMap(safeSlurmID).prefix(20).map(SSHRunner.shellQuote)
                    if !ids.isEmpty { section("alloc", "for j in \(ids.joined(separator: " ")); do " + body) }
                } else if host.kind == .pbs {
                    section("alloc", #"printf '%s\n' "$SI_PQ" | si_pbs t"#)
                }
            }
            if listsProcesses || (host.kind.isScheduler && host.detectApps) {
                lines.append(AppDetectors.jobIDFunction + processScan(host: host))
            }
            if listsProcesses {
                section("procs", c.processes)
            }
            if host.kind.isScheduler && host.slurmAlsoProcesses {
                // 属于作业的进程（环境变量里有 SLURM_JOB_ID / PBS_JOBID）由作业行显示，不再重复列出。
                section("slurmpids", #"printf '%s\n' "$SI_PT" | awk -F'|' '$2 != "" { print $1 }'"#)
            }
            section("workdirs", workdirScript(host: host))
            if host.detectApps {
                section("apps", AppDetectors.script(host: host, signatures: appSignatures))
            }
        }
        if host.kind == .direct && !host.watched.isEmpty {
            let pids = host.watched.map(\.pid).filter { Int($0) != nil }.joined(separator: ",")
            if !pids.isEmpty {
                // 不加 -u：监听的进程不受过滤规则影响。不存在的 PID 会让 ps 返回非 0，属于正常情况。
                section("watch", "ps -o pid=,etimes=,pcpu=,rss=,args= -p \(pids); true")
            }
        }
        if host.showDisk && includeDisk {
            section("disk", c.disk)
        }
        if host.readProgressFiles {
            section("progress", """
            for f in \(c.progressGlob); do
              [ -f "$f" ] || continue
              printf '\(marker)FILE %s\\n' "$f"; head -c 4096 "$f"; echo
            done; true
            """)
        }
        lines.append("printf '\(marker)END\\n'")
        return lines.joined(separator: "\n") + "\n"
    }

    static func sacctScript(for host: HostConfig, jobIDs: [String]) -> String {
        if host.kind == .pbs {
            // PBS Pro / OpenPBS 用 qstat -x 查已结束的作业；Torque 没有 -x，已结束的作业会以 C 状态保留一段时间。
            let ids = jobIDs.compactMap(safeSlurmID).map { SSHRunner.shellQuote(pbsID($0)) }.joined(separator: " ")
            return wrap(host, [("sacct", pbsFunction + "{ qstat -x -f -t \(ids) 2>/dev/null || qstat -f -t \(ids) 2>/dev/null; } | si_pbs a")])
        }
        let cmd = host.commands.sacct.replacingOccurrences(of: "{jobs}", with: jobIDs.joined(separator: ","))
        var s = ""
        if !host.commands.preamble.isEmpty { s += host.commands.preamble + "\n" }
        s += "printf '\(marker)SEC sacct\\n'\n{ \(cmd)\n} 2>&1\nprintf '\(marker)RC %s\\n' \"$?\"\n"
        return s
    }

    private static func wrap(_ host: HostConfig, _ sections: [(String, String)]) -> String {
        var s = ""
        if !host.commands.preamble.isEmpty { s += host.commands.preamble + "\n" }
        for (name, body) in sections {
            s += "printf '\(marker)SEC \(name)\\n'\n{ \(body)\n} 2>&1\nprintf '\(marker)RC %s\\n' \"$?\"\n"
        }
        return s
    }

    /// 只读 log 文件末尾；路径来自远端（scontrol / /proc），在远端 shell 变量里传递，不做本地拼接。
    private static func tailVar(_ v: String, lines: Int) -> String {
        """
        if [ -n "$\(v)" ] && [ -f "$\(v)" ] && [ -r "$\(v)" ]; then printf '\(marker)PATH %s\\n' "$\(v)"; tail -n \(lines) "$\(v)" | cut -c1-400; \
        elif [ -n "$\(v)" ]; then printf '\(marker)PATH %s\\n' "$\(v)"; echo '\(marker)UNREADABLE'; fi
        """
    }

    /// 任务详情 + 日志末尾（只在用户打开详情时调用）。
    /// 作业号：123 或数组元素 123_5（只允许数字和下划线，防止注入）。PBS 的作业也统一写成这种形式。
    static func safeSlurmID(_ s: String) -> String? {
        s.range(of: #"^\d+(_\d+)?$"#, options: .regularExpression) != nil ? s : nil
    }

    /// "123_5" -> "123[5]"（PBS 数组子作业的写法）；普通作业不变。
    static func pbsID(_ id: String) -> String {
        guard let us = id.firstIndex(of: "_") else { return id }
        return String(id[..<us]) + "[" + String(id[id.index(after: us)...]) + "]"
    }

    static func detailScript(host: HostConfig, job: RemoteJob, logLines: Int = 40) -> String {
        let idOK: String? = job.source == .slurm ? safeSlurmID(job.id) : Int(job.id).map(String.init)
        guard let id = idOK else { return wrap(host, []) }
        switch job.source {
        case .slurm where host.kind == .pbs:
            let q = SSHRunner.shellQuote(pbsID(id))
            let full = "{ qstat -x -f \(q) 2>/dev/null || qstat -f \(q); }"
            // Output_Path / Error_Path 形如 主机名:/路径；作业运行时 PBS 常把输出先放在计算节点的 spool 里，结束后才拷回来。
            return wrap(host, [
                ("qstat", full),
                ("stdout", pbsFunction + "SI_P=$(\(full) 2>/dev/null | si_pbsval Output_Path); SI_P=${SI_P#*:}\n" + tailVar("SI_P", lines: logLines)),
                ("stderr", pbsFunction + "SI_O=$(\(full) 2>/dev/null | si_pbsval Output_Path); SI_O=${SI_O#*:}\nSI_E=$(\(full) 2>/dev/null | si_pbsval Error_Path); SI_E=${SI_E#*:}\n[ \"$SI_E\" = \"$SI_O\" ] && SI_E=''\n" + tailVar("SI_E", lines: logLines)),
            ])
        case .slurm:
            return wrap(host, [
                ("scontrol", "scontrol show job -o \(id)"),
                ("sacct", "sacct -n -X -P -j \(id) -o JobID,JobName,Partition,State,ExitCode,Elapsed,Start,End,NodeList,AllocTRES,MaxRSS 2>/dev/null; true"),
                ("stdout", "SI_P=$(scontrol show job -o \(id) 2>/dev/null | tr ' ' '\\n' | sed -n 's/^StdOut=//p' | head -n 1)\n" + tailVar("SI_P", lines: logLines)),
                ("stderr", "SI_O=$(scontrol show job -o \(id) 2>/dev/null | tr ' ' '\\n' | sed -n 's/^StdOut=//p' | head -n 1)\nSI_E=$(scontrol show job -o \(id) 2>/dev/null | tr ' ' '\\n' | sed -n 's/^StdErr=//p' | head -n 1)\n[ \"$SI_E\" = \"$SI_O\" ] && SI_E=''\n" + tailVar("SI_E", lines: logLines)),
            ])
        case .process:
            return wrap(host, [
                ("ps", "ps -o pid=,ppid=,user=,etimes=,pcpu=,pmem=,rss=,stat= -p \(id)"),
                ("args", "tr '\\0' ' ' < /proc/\(id)/cmdline; echo"),
                ("cwd", "readlink /proc/\(id)/cwd"),
                ("gpu", "command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader,nounits | grep '^\(id),'; true"),
                // 进程的标准输出 / 错误如果被重定向到了文件，就能直接读到日志。
                ("stdout", "SI_P=$(readlink /proc/\(id)/fd/1 2>/dev/null); case \"$SI_P\" in /dev/*|socket:*|pipe:*) echo \"\(marker)NOFILE $SI_P\"; SI_P='';; /*) ;; *) echo \"\(marker)NOFILE $SI_P\"; SI_P='';; esac\n" + tailVar("SI_P", lines: logLines)),
                ("stderr", "SI_O=$(readlink /proc/\(id)/fd/1 2>/dev/null); SI_E=$(readlink /proc/\(id)/fd/2 2>/dev/null); case \"$SI_E\" in /dev/*) SI_E='';; /*) ;; *) SI_E='';; esac; [ \"$SI_E\" = \"$SI_O\" ] && SI_E=''\n" + tailVar("SI_E", lines: logLines)),
            ])
        case .progressFile:
            return wrap(host, [])
        }
    }

    /// 取消前再核对一次（名称一致、属于自己），通过后才执行。
    static func cancelScript(host: HostConfig, job: RemoteJob, expectedElapsed: TimeInterval?, force: Bool) -> String? {
        let idOK: String? = job.source == .slurm ? safeSlurmID(job.id) : Int(job.id).map(String.init)
        guard let id = idOK else { return nil }
        let me = "\"$(id -un)\""
        switch job.source {
        case .slurm where host.kind == .pbs:
            let q = SSHRunner.shellQuote(pbsID(id))
            let name = SSHRunner.shellQuote(job.name)
            return wrap(host, [("cancel", pbsFunction + """
            SI_F=$(qstat -f \(q) 2>/dev/null)
            SI_N=$(printf '%s\\n' "$SI_F" | si_pbsval Job_Name)
            SI_W=$(printf '%s\\n' "$SI_F" | si_pbsval Job_Owner); SI_W=${SI_W%%@*}
            SI_S=$(printf '%s\\n' "$SI_F" | si_pbsval job_state)
            if [ -z "$SI_F" ] || [ "$SI_N" != \(name) ] || [ "$SI_W" != \(me) ]; then echo '\(marker)ABORT 作业已不在队列中，或不属于你'; exit 3; fi
            case "$SI_S" in F|X|C) echo '\(marker)ABORT 作业已经结束'; exit 3;; esac
            qdel \(q) && echo '\(marker)DONE'
            """)])
        case .slurm:
            let name = SSHRunner.shellQuote(job.name)
            return wrap(host, [("cancel", """
            SI_N=$(squeue -h -j \(id) -u \(me) -o '%j' 2>/dev/null | head -n 1)
            if [ "$SI_N" != \(name) ]; then echo '\(marker)ABORT 作业已不在队列中，或不属于你'; exit 3; fi
            scancel \(id) && echo '\(marker)DONE'
            """)])
        case .process:
            let tol = 20
            let check = expectedElapsed.map { e in
                "SI_T=$(ps -o etimes= -p \(id) | tr -d ' '); SI_D=$(( SI_T - \(Int(e)) )); [ ${SI_D#-} -le \(tol) ] || { echo '\(marker)ABORT 进程已变化（PID 可能被复用）'; exit 3; }"
            } ?? ""
            return wrap(host, [("cancel", """
            SI_U=$(ps -o user= -p \(id) 2>/dev/null | tr -d ' ')
            if [ -z "$SI_U" ]; then echo '\(marker)ABORT 进程已经结束'; exit 3; fi
            if [ "$SI_U" != \(me) ]; then echo '\(marker)ABORT 进程不属于你'; exit 3; fi
            \(check)
            kill -\(force ? "KILL" : "TERM") \(id) && echo '\(marker)DONE'
            """)])
        case .progressFile:
            return nil
        }
    }

    /// 最近 24 小时的作业（需要集群开启 accounting；不支持时返回非 0）。
    static func historyScript(host: HostConfig) -> String {
        // WorkDir 列是后来才有的，老版本 sacct 不认识这一列时去掉它再查一次
        wrap(host, [("history", #"sacct -n -X -P -S now-24hours -u "$(id -un)" -o JobID,JobName,State,Elapsed,End,ExitCode,WorkDir 2>/dev/null || sacct -n -X -P -S now-24hours -u "$(id -un)" -o JobID,JobName,State,Elapsed,End,ExitCode"#)])
    }

    /// PBS 的队列列表（设置页填单价用）。
    static func pbsQueuesScript(host: HostConfig) -> String {
        wrap(host, [("queues", "qstat -Q")])
    }

    /// 高负载进程（给用户挑选要监听的 PID）。
    static func topProcessesScript(host: HostConfig) -> String {
        wrap(host, [
            ("procs", #"ps -u "$(id -un)" -o pid=,etimes=,pcpu=,rss=,args= --sort=-pcpu | head -n 40"#),
            ("gpuapps", "command -v nvidia-smi >/dev/null 2>&1 && \(host.commands.gpuApps); true"),
        ])
    }

    /// 用于"自动检测类型"。
    static let detectScript = """
    for c in squeue sbatch sinfo qstat qsub qselect nvidia-smi; do
      if command -v "$c" >/dev/null 2>&1; then echo "$c=yes"; else echo "$c=no"; fi
    done
    if pgrep -x slurmd >/dev/null 2>&1; then echo "slurmd=yes"; else echo "slurmd=no"; fi
    uname -sr
    """

    /// 每个任务的工作目录（写进历史记录，也用于目录过滤）：作业用调度系统记录的工作目录，进程用当前目录。输出 `id|目录`，另有一行 `@@SI:HOME 家目录`。
    /// 用的是前面的段留下的 `SI_JW` / `SI_PQ` / `SI_PT`（见 `build`）；自定义了 squeue 命令时单独查一次工作目录。
    static func workdirScript(host: HostConfig) -> String {
        let me = "\"$(id -un)\""
        var s = "printf '\(marker)HOME %s\\n' \"$HOME\"\n"
        switch host.kind {
        case .slurm:
            if host.commands.squeue != CommandSet.defaults.squeue {
                s += "SI_JW=$(squeue -u \(me) -h -o '%i|%T|%Z' 2>/dev/null)\n"
            }
            s += #"printf '%s\n' "$SI_JW" | sed -n 's/^\([^|]*\)|[^|]*|/\1|/p'"# + "\n"
        case .pbs:
            s += #"printf '%s\n' "$SI_PQ" | si_pbs d"# + "\n"
        case .direct:
            break
        }
        if host.kind == .direct || host.slurmAlsoProcesses {
            s += #"printf '%s\n' "$SI_PT" | sed -n 's/^\([0-9]*\)|[^|]*|\(..*\)$/\1|\2/p'"# + "\n"
        }
        return s + "true"
    }

    /// 自己的进程只遍历一遍：`SI_PT` 每行 `PID|所属作业号|当前目录`。作业号只在调度系统的机器上读（/proc/PID/environ）；
    /// 不列出普通进程时（只为计算软件识别），只保留属于作业的进程。
    static func processScan(host: HostConfig) -> String {
        let me = "\"$(id -un)\""
        let lists = host.kind == .direct || host.slurmAlsoProcesses
        let body: String
        switch (host.kind.isScheduler, lists) {
        case (true, true): body = "j=$(si_jobid $p); d=$(readlink /proc/$p/cwd 2>/dev/null)"
        case (true, false): body = "j=$(si_jobid $p); [ -n \"$j\" ] || continue; d=$(readlink /proc/$p/cwd 2>/dev/null)"
        case (false, _): body = "j=; d=$(readlink /proc/$p/cwd 2>/dev/null) || continue"
        }
        return "SI_PT=$(for p in $(pgrep -u \(me)); do \(body); printf '%s|%s|%s\\n' \"$p\" \"$j\" \"$d\"; done 2>/dev/null)\n"
    }

    /// PBS（PBS Pro / OpenPBS / Torque）：把 `qstat -f` 的全文转换成和 squeue / sacct 一样的行，后面的解析、数组合并、提醒全部沿用 Slurm 的。
    /// `si_pbs q`：`作业号|名称|状态|已运行|上限|节点数|原因或节点|队列|数组主编号|元素序号`（已结束的不输出）；
    /// `si_pbs a`：`作业号|名称|状态|已运行|退出码|资源`（作业结束后查最终状态）；`si_pbs t`：运行中作业的 `作业号|资源`（计费用）。
    /// 资源写成 Slurm TRES 的样子（`cpu=32,node=1,gres/gpu=4`）：PBS Pro 用 Resource_List.ncpus / ngpus，Torque 从 nodes=2:ppn=16:gpus=2 里算。
    ///`si_pbs w` / `si_pbs d`：运行中 / 全部作业的 `作业号|工作目录`。
    /// 作业号统一写成 Slurm 的形式：1234.server → 1234，数组子作业 1234[5].server → 1234_5；数组本身（1234[]）跳过，子作业由 -t 列出。
    /// 状态：R/B 运行，E 收尾，H/Q/W/T 排队，S/U 挂起，F/X/C 结束（按 Exit_status：0 完成，-29/-11 超时，≥256 被信号终止，其它失败）。
    /// 续行（以 Tab 开头）接到上一个值后面。`si_pbsval 键` 取单个值。
    static let pbsFunction = #"""
    si_pbs() { awk -v mode="$1" '
    function clear() { id = ""; k = ""; delete v }
    function out(   jid, arr, task, st, ex, S, name, n, r, h, nh, parts, i, x, seen, wd, c, g, tc, tg, m, p, q, tr) {
      if (id == "") return
      jid = id; sub(/\..*$/, "", jid)
      if (jid ~ /\[\]$/) { clear(); return }
      arr = jid; task = "N/A"
      if (match(jid, /\[[0-9]+\]$/)) { task = substr(jid, RSTART + 1, RLENGTH - 2); arr = substr(jid, 1, RSTART - 1); jid = arr "_" task }
      st = v["job_state"]; ex = v["Exit_status"]; if (ex == "") ex = v["exit_status"]
      if (st == "R" || st == "B") S = "RUNNING"
      else if (st == "E") S = "COMPLETING"
      else if (st == "S" || st == "U") S = "SUSPENDED"
      else if (st == "F" || st == "X" || st == "C") {
        if (ex == "") S = (v["stime"] == "" && v["start_time"] == "" && v["resources_used.walltime"] == "") ? "CANCELLED" : "COMPLETED"
        else if (ex + 0 == 0) S = "COMPLETED"
        else if (ex + 0 == -29 || ex + 0 == -11) S = "TIMEOUT"
        else if (ex + 0 >= 256) S = "CANCELLED"
        else S = "FAILED"
      } else S = "PENDING"
      name = v["Job_Name"]; gsub(/\|/, "/", name)
      n = v["Resource_List.nodect"]; if (n == "") { n = v["Resource_List.nodes"]; sub(/[^0-9].*$/, "", n) }; if (n == "") n = 1
      if (S == "PENDING") {
        r = v["comment"]; gsub(/\|/, "/", r)
        if (st == "H") r = "JobHeldUser" (r == "" ? "" : ": " r)
        else if (st == "W") r = "BeginTime"
        else if (r == "") r = "Priority"
        r = "(" r ")"
      } else {
        h = v["exec_host"]; r = ""; nh = split(h, parts, "+")
        for (i = 1; i <= nh; i++) { x = parts[i]; sub(/\/.*$/, "", x); if (x != "" && !(x in seen)) { seen[x] = 1; r = r (r == "" ? "" : ",") x } }
      }
      c = v["Resource_List.ncpus"]; g = v["Resource_List.ngpus"]
      if (c == "" || g == "") {
        nh = split(v["Resource_List.nodes"], parts, "+"); tc = 0; tg = 0
        for (i = 1; i <= nh; i++) {
          x = parts[i]; m = (x ~ /^[0-9]+(:|$)/) ? x + 0 : 1; p = 1; q = 0
          if (match(x, /ppn=[0-9]+/)) p = substr(x, RSTART + 4, RLENGTH - 4) + 0
          if (match(x, /gpus=[0-9]+/)) q = substr(x, RSTART + 5, RLENGTH - 5) + 0
          tc += m * p; tg += m * q
        }
        if (c == "" && nh > 0) c = tc
        if (g == "" && nh > 0) g = tg
      }
      tr = (c == "" ? "" : "cpu=" c ",") "node=" n (g == "" ? "" : ",gres/gpu=" g)
      wd = v["Variable_List"]
      if (match(wd, /PBS_O_WORKDIR=[^,]*/)) wd = substr(wd, RSTART + 14, RLENGTH - 14); else wd = ""
      if (mode == "q") { if (st != "F" && st != "X" && st != "C") print jid "|" name "|" S "|" v["resources_used.walltime"] "|" v["Resource_List.walltime"] "|" n "|" r "|" v["queue"] "|" arr "|" task }
      else if (mode == "a") print jid "|" name "|" S "|" v["resources_used.walltime"] "|" (ex == "" ? "" : ex ":0") "|" tr
      else if (mode == "t") { if (S == "RUNNING" || S == "COMPLETING") print jid "|" tr }
      else if (mode == "w") { if (S == "RUNNING" && wd != "") print jid "|" wd }
      else if (mode == "d") { if (wd != "") print jid "|" wd }
      clear()
    }
    /^Job Id: / { out(); id = substr($0, 9); gsub(/[ \t\r]/, "", id); next }
    /^\t/ { if (k != "") { t = $0; sub(/^\t+/, "", t); sub(/\r$/, "", t); v[k] = v[k] t }; next }
    /^ +[A-Za-z_][A-Za-z0-9_.]* = / { t = $0; sub(/^ +/, "", t); sub(/\r$/, "", t); i = index(t, " = "); k = substr(t, 1, i - 1); v[k] = substr(t, i + 3); next }
    { k = "" }
    END { out() }'; }
    si_pbsval() { awk -v key="$1" '/^\t/ { if (on) { t = $0; sub(/^\t+/, "", t); val = val t }; next } { on = 0 } $0 ~ ("^ +" key " = ") { on = 1; val = substr($0, index($0, " = ") + 3) } END { print val }'; }

    """#

    struct Section {
        var lines: [String] = []
        var exitCode: Int?
        var notAvailable: Bool { lines.first == RemoteScript.marker + "NA" }
        var text: String { lines.joined(separator: "\n") }
    }

    struct Parsed {
        var sections: [String: Section] = [:]
        var sawBegin = false
        var sawEnd = false
    }

    static func parse(_ output: String) -> Parsed {
        var parsed = Parsed()
        var current: String?
        for raw in output.components(separatedBy: "\n") {
            let line = raw.hasSuffix("\r") ? String(raw.dropLast()) : raw
            if line.hasPrefix(marker) {
                let rest = line.dropFirst(marker.count)
                if rest == "BEGIN" { parsed.sawBegin = true; continue }
                if rest == "END" { parsed.sawEnd = true; current = nil; continue }
                if rest.hasPrefix("SEC ") {
                    let name = String(rest.dropFirst(4))
                    parsed.sections[name] = Section()
                    current = name
                    continue
                }
                if rest.hasPrefix("RC "), let name = current {
                    parsed.sections[name]?.exitCode = Int(rest.dropFirst(3))
                    current = nil
                    continue
                }
            }
            // BEGIN 之前的内容（如 .bashrc 的输出）直接忽略。
            if let name = current { parsed.sections[name]?.lines.append(line) }
        }
        return parsed
    }
}
