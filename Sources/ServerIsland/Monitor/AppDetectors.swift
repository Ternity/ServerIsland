import Foundation

/// 计算软件的进度识别（NEP / GPUMD 参考 GPUMDkit Web Console 的判断逻辑；另有 VASP、LAMMPS 和用户自定义规则）。
/// 原则：任何一处和预期不一致（文件缺失、格式对不上、数字矛盾）就当作没识别到，不显示进度。
/// 与任务列表在同一次 SSH 轮询中完成，只对任务的工作目录做 `tail -n 1` / `awk` 这类轻量读取。
/// 新增软件时：在 `script` 里加一个分支输出 key=value，再在 `parse` 里解析即可。
struct AppProgress: Equatable {
    enum Kind: String { case nep, gnep, torchnep, mace, deepmd, gpumd, vasp, lammps, custom }
    /// VASP 的计算类型。
    enum VaspMode: String { case md, relax, scf, neb }

    struct Value: Equatable {
        var label: String
        var value: Double
    }

    struct Neighbor: Equatable {
        var radialActual: Int, radialMax: Int, angularActual: Int, angularMax: Int
    }

    var kind: Kind
    var dir: String
    var current: Double?
    var total: Double?
    /// nep.in / run.in 里是否写了目标（没写时用默认值，进度仅供参考）。
    var hasTarget = true
    /// 曲线读的文件（相对工作目录）：TorchNEP 的 loss.out、MACE 的训练日志，不在固定位置。
    var dataFile: String?
    var losses: [Value] = []
    var neighbor: Neighbor?
    /// 输出文件的最后修改时间（用来判断是否卡住）。
    var updatedAt: Date?
    /// 由引擎根据两次采样算出：每秒推进多少代 / 步。
    var rate: Double?
    /// 正常结束标记（VASP 的 OUTCAR 计时段、LAMMPS 的 Total wall time、自定义规则的结束文字）。
    /// nil：这种软件 / 规则没有结束标记。
    var endMarker: Bool?
    var vaspMode: VaspMode?
    /// NEB：正向能垒（最高点 − 初态，eV；没有初态 OUTCAR 时为 nil）、中间像点里最大的 VTST 力、中间像点数。
    var nebBarrier: Double?
    var nebForce: Double?
    var nebImages: Int?
    /// 自定义规则的名称和单位。
    var customName = ""
    var customUnit = ""
    /// 识别时读过的文件的签名（修改时间 + 大小）。下次轮询把它发给服务器，文件没变就不再重读。nil：这种软件不做这一步。
    var signature: String?
    /// 服务器说文件没变（`same=1`）：引擎用上次的结果替换这一条。
    var unchanged = false

    var title: String {
        switch kind {
        case .nep: return L("NEP 训练", "NEP training")
        case .gnep: return L("GNEP 训练", "GNEP training")
        case .torchnep: return L("TorchNEP 训练", "TorchNEP training")
        case .mace: return L("MACE 训练", "MACE training")
        case .deepmd: return L("DeePMD 训练", "DeePMD training")
        case .gpumd: return L("GPUMD 模拟", "GPUMD MD")
        case .vasp:
            switch vaspMode ?? .scf {
            case .md: return "VASP MD"
            case .relax: return L("VASP 结构优化", "VASP relaxation")
            case .scf: return L("VASP 单点", "VASP SCF")
            case .neb: return "VASP NEB"
            }
        case .lammps: return "LAMMPS MD"
        case .custom: return customName
        }
    }

    var unit: String {
        switch kind {
        case .nep: return L("代", "gen")
        case .gnep, .torchnep, .mace: return "epoch"
        case .gpumd, .lammps, .deepmd: return L("步", "steps")
        case .vasp:
            switch vaspMode ?? .scf {
            case .md: return L("步", "steps")
            case .relax, .neb: return L("离子步", "ionic steps")
            case .scf: return L("电子步", "electronic steps")
            }
        case .custom: return customUnit
        }
    }

    var fraction: Double? {
        guard let c = current, let t = total, t > 0 else { return nil }
        return min(max(c / t, 0), 1)
    }

    var finished: Bool {
        if let m = endMarker { return m }
        guard let c = current, let t = total, hasTarget else { return false }
        return c >= t
    }

    /// 任务结束时能否判断成功 / 失败：有目标值或有结束标记。
    var canJudge: Bool { endMarker != nil || hasTarget }

    var eta: TimeInterval? {
        guard let c = current, let t = total, let r = rate, r > 0, c < t else { return nil }
        return (t - c) / r
    }

    /// 一行摘要，显示在任务行里。
    var summary: String {
        var parts: [String] = []
        if let c = current {
            let cur = Self.compact(c), tot = total.map(Self.compact)
            let u = unit.isEmpty ? "" : " \(unit)"
            parts.append(tot.map { "\(cur)/\($0)\(u)" } ?? "\(cur)\(u)")
        }
        switch kind {
        case .nep, .gnep, .torchnep, .mace:
            if let l = losses.first { parts.append("\(l.label) \(Self.sci(l.value))") }
        case .deepmd:
            // 损失已按 能量 → 力 排好（有验证集时先验证集），摘要显示前两项
            for l in losses.prefix(2) { parts.append("\(l.label) \(Self.sci(l.value))") }
        case .gpumd, .lammps:
            if let r = rate { parts.append(Self.compact(r) + L(" 步/秒", " steps/s")) }
        case .vasp where vaspMode == .neb:
            if let b = nebBarrier { parts.append(L("能垒 ", "barrier ") + String(format: "%.3f eV", b)) }
            if let f = nebForce { parts.append(L("最大力 ", "max force ") + Self.sci(f)) }
        case .vasp, .custom:
            break
        }
        return parts.joined(separator: " · ")
    }

    static func compact(_ v: Double) -> String {
        let a = abs(v)
        if a >= 1e9 { return String(format: "%.2fG", v / 1e9) }
        if a >= 1e6 { return String(format: "%.2fM", v / 1e6) }
        if a >= 100 || v == v.rounded() {
            let f = NumberFormatter()
            f.numberStyle = .decimal
            f.maximumFractionDigits = 0
            return f.string(from: NSNumber(value: v.rounded())) ?? String(Int(v.rounded()))
        }
        return String(format: "%.2f", v)
    }

    static func sci(_ v: Double) -> String {
        let a = abs(v)
        if a != 0 && (a < 1e-3 || a >= 1e4) { return String(format: "%.2e", v) }
        return String(format: "%.4f", v)
    }
}

enum AppDetectors {
    static let marker = RemoteScript.marker

    /// 远端脚本：收集"任务 ID | 工作目录"，再逐个目录识别软件。
    /// - 直连服务器：按进程名（pgrep -x）找进程，读 /proc/<pid>/cwd。
    /// - Slurm：squeue %Z 拿运行中作业的工作目录。
    /// shell 函数：从 /proc/<pid>/environ 读出所属 Slurm 作业号（数组元素为 主编号_序号）。
    /// 比 cgroup 可靠：不依赖 Slurm 的 cgroup / proctrack 配置。
    static let jobIDFunction = #"""
    si_pick() { (cd "$1" 2>/dev/null && ls -td -- $2 2>/dev/null | head -n 1); }
    si_tnep() { (cd "$1" 2>/dev/null && ls -td -- loss.out */loss.out 2>/dev/null) | while IFS= read -r f; do case "$(head -n 1 "$1/$f" 2>/dev/null)" in "#"*epoch*loss*rmse_*) echo "$f"; break;; esac; done; }
    si_jobid() { tr '\0' '\n' < /proc/$1/environ 2>/dev/null | awk -F= '$1=="SLURM_ARRAY_JOB_ID"{a=$2} $1=="SLURM_ARRAY_TASK_ID"{t=$2} $1=="SLURM_JOB_ID"{j=$2} $1=="PBS_JOBID"{b=$2} END{if(a!=""&&t!="")print a"_"t; else if(j!="")print j; else if(b!=""){sub(/\..*$/,"",b); sub(/\[/,"_",b); sub(/\]/,"",b); print b}}'; }

    """#

    /// 是轮询脚本的一段，用前面留下的 `SI_JW` / `SI_PQ`、`SI_PT`、`SI_GA` 和 `si_pick` / `si_jobid` / `si_pbs`（见 `RemoteScript.build`）。
    /// `signatures`：上次识别时的文件签名（目录 → 签名）。签名没变的目录只输出 `same=1`，引擎沿用上次的结果。
    static func script(host: HostConfig, signatures: [String: String] = [:]) -> String {
        let me = "\"$(id -un)\""
        let names = host.appProcessNames.replacingOccurrences(of: "'", with: "")
        // 候选进程：名字匹配，或者正在用 GPU，或者 CPU ≥ 50%，或者属于作业。真正判断是哪种软件靠工作目录里的文件。
        // 属于作业的进程映射回作业号（单机 Slurm 能拿到作业真正运行的目录）。
        let showLocal = host.kind == .direct || (host.kind.isScheduler && host.slurmAlsoProcesses)
        let candidates = showLocal ? """
          SI_C=$( { pgrep -u \(me) -x '\(names)'
                    printf '%s\\n' "$SI_GA" | awk -F, '$1 + 0 > 0 { print $1 + 0 }'
                    ps -u \(me) -o pid=,pcpu= | awk '$2 >= 50 {print $1}'
                    \(host.kind.isScheduler ? #"printf '%s\n' "$SI_PT" | awk -F'|' '$2 != "" { print $1 }'"# : "")
                  } 2>/dev/null | sort -un | head -n 60 | tr '\\n' ' ')
          printf '%s\\n' "$SI_PT" | awk -F'|' -v c="$SI_C" 'BEGIN { n = split(c, a, " "); for (i = 1; i <= n; i++) w[a[i]] = 1 }
            ($1 in w) && $3 != "" { print ($2 != "" ? $2 : $1) "|" substr($0, length($1) + length($2) + 3) }'
        """ : """
          printf '%s\\n' "$SI_PT" | awk -F'|' '$2 != "" && $3 != "" { print $2 "|" substr($0, length($1) + length($2) + 3) }' | head -n 60
        """
        let slurm: String
        switch host.kind {
        case .slurm: slurm = #"printf '%s\n' "$SI_JW" | sed -n 's/^\([^|]*\)|RUNNING|/\1|/p' | head -n 40"#
        case .pbs: slurm = #"printf '%s\n' "$SI_PQ" | si_pbs w | head -n 40"#
        case .direct: slurm = ""
        }
        return """
        \(sameFunction(signatures))
        { \(slurm)
        \(candidates)
        } | awk -F'|' '!seen[$2]++' | while IFS='|' read -r id d; do
        \(dirBody(rules: ProgressRule.active.filter(\.isUsable)))
        done; true
        """
    }

    /// `si_same 目录 签名`：签名和上次一样时返回 0（文件没变，不用重读）。签名为空（stat 失败）时总是重读。
    static func sameFunction(_ signatures: [String: String]) -> String {
        guard !signatures.isEmpty else { return "si_same() { false; }" }
        let q = SSHRunner.shellQuote
        var s = "si_same() { [ -n \"$2\" ] || return 1; case \"$1\" in\n"
        for (d, g) in signatures.sorted(by: { $0.key.utf8.lexicographicallyPrecedes($1.key.utf8) }) {
            s += "  \(q(d))) [ \"$2\" = \(q(g)) ];;\n"
        }
        return s + "  *) false;;\nesac; }"
    }

    /// 任务结束时再读一次最终状态（只读这一个目录），避免"最后一次采样还没到目标"被误判为提前结束。
    static func finalStateScript(id: String, dir: String) -> String {
        directoryScript(id: id, dir: dir, rules: ProgressRule.active.filter(\.isUsable), builtin: true)
    }

    /// 只对一个目录跑识别。设置页"测试规则"用 `rules: [规则], builtin: false`。
    static func directoryScript(id: String, dir: String, rules: [ProgressRule], builtin: Bool) -> String {
        """
        \(jobIDFunction)\(sameFunction([:]))
        id=\(SSHRunner.shellQuote(id)); d=\(SSHRunner.shellQuote(dir))
        case "$d" in "~"/*) d="$HOME/${d#\\~/}";; esac
        for _ in 1; do
        \(dirBody(rules: rules, builtin: builtin))
        done; true

        """
    }

    /// 对单个目录（变量 $id、$d）识别软件并输出 key=value。自定义规则排在内置识别前面。
    private static func dirBody(rules: [ProgressRule], builtin: Bool = true) -> String {
        var branches = rules.map(customBranch)
        if builtin { branches.append(builtinBranches) }
        guard !branches.isEmpty else { return "  :" }
        return "  [ -n \"$d\" ] && [ -d \"$d\" ] || continue\n  if "
            + branches.joined(separator: "\n  elif ")
            + "\n  fi"
    }

    /// 一条自定义规则：条件 + 输出。文件名已经过 isSafeFileName 检查，其余字符串一律 shellQuote。
    private static func customBranch(_ r: ProgressRule) -> String {
        let q = SSHRunner.shellQuote
        var body = """
        f="$d/$(si_pick "$d" '\(r.file)')"
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=custom; echo rule=\(r.id.uuidString)
        """
        if r.totalFile.isEmpty {
            body += "\n    sg=$(stat -c '%n %Y %s' \"$f\" 2>/dev/null | tr '\\n' ' ')"
        } else {
            body += "\n    tf=$(si_pick \"$d\" '\(r.totalFile)'); sg=$(stat -c '%n %Y %s' \"$f\" ${tf:+\"$d/$tf\"} 2>/dev/null | tr '\\n' ' ')"
        }
        body += "\n    if si_same \"$d\" \"$sg\"; then echo same=1; else echo \"sig=$sg\""
        if r.match == .keyword {
            body += "\n    grep -aiF -- \(q(r.pattern)) \"$f\" | tail -n 5 | cut -c1-300 | sed 's/^/tail=/'"
        } else {
            body += "\n    tail -n 40 \"$f\" | cut -c1-300 | sed 's/^/tail=/'"
        }
        if !r.totalFile.isEmpty {
            body += "\n    [ -n \"$tf\" ] && grep -aiF -m 1 -- \(q(r.totalKeyword)) \"$d/$tf\" | cut -c1-300 | sed 's/^/totalline=/'"
        }
        if !r.doneMarker.isEmpty {
            body += "\n    if tail -n 50 \"$f\" | grep -qaF -- \(q(r.doneMarker)); then echo done=1; else echo done=0; fi"
        }
        body += "\n    echo \"mtime=$(stat -c %Y \"$f\" 2>/dev/null)\"; fi"
        return "[ -n \"$(si_pick \"$d\" '\(r.file)')\" ]; then\n    " + body
    }

    /// LAMMPS：从最后一个 run 命令开始读 log.lammps。第一行必须是 "run <整数>"，之后要找到含 Step 列的 thermo 表头，
    /// 数据行列数和表头一致且全是数字。出现 minimize / rerun、多余的表头、upto / every 等都标记 bad。
    private static let lammpsAwk = #"""
    awk 'function isnum(x) { return x ~ /^[-+]?([0-9]+[.]?[0-9]*|[.][0-9]+)([eE][-+]?[0-9]+)?$/ }
    NR == 1 { total = $2; if (NF < 2 || $2 !~ /^[0-9]+$/) bad = 1
              for (i = 3; i <= NF; i++) if ($i == "upto" || $i == "every") bad = 1; next }
    /^[ 	]*(minimize|rerun)([ 	]|$)/ { bad = 1 }
    /^Loop time of/ { if (hdr) loop = 1; next }
    loop { next }
    !hdr { k = 0; for (i = 1; i <= NF; i++) if ($i == "Step") k = i
           if (k) { hdr = 1; n = NF }; next }
    $k == "Step" { bad = 1; next }
    NF == n { ok = 1; for (i = 1; i <= NF; i++) if (!isnum($i)) { ok = 0; break }
              if (ok && $k ~ /^[0-9]+$/) { if (first == "") first = $k; last = $k } }
    END { print "total=" total; print "first=" first; print "last=" last; print "loop=" (loop ? 1 : 0); print "bad=" (bad ? 1 : 0) }'
    """#

    /// VASP：INCAR 里的 NSW / IBRION（去掉注释，支持分号分隔），OSZICAR 末尾的离子步 / 电子步。
    private static let vaspIncarAwk = #"""
    awk '{ sub(/[!#].*/, ""); n = split($0, parts, ";")
           for (i = 1; i <= n; i++) { s = toupper(parts[i])
             if (s ~ /^[ 	]*NSW[ 	]*=/) { sub(/^[^=]*=[ 	]*/, "", s); split(s, v, " "); print "nsw=" v[1] }
             if (s ~ /^[ 	]*IBRION[ 	]*=/) { sub(/^[^=]*=[ 	]*/, "", s); split(s, v, " "); print "ibrion=" v[1] }
             if (s ~ /^[ 	]*IMAGES[ 	]*=/) { sub(/^[^=]*=[ 	]*/, "", s); split(s, v, " "); print "images=" v[1] } } }'
    """#
    private static let vaspOszicarAwk = #"""
    awk '$1 ~ /^[0-9]+$/ && $2 ~ /^[FT]=/ { ion = $1 }
         /^[A-Z][A-Z][A-Z ]:/ { s = substr($0, 5); split(s, a, " "); if (a[1] ~ /^[0-9]+$/) elec = a[1] }
         END { print "ionic=" ion; print "elec=" elec }'
    """#

    private static var builtinBranches: String {
        """
        tf=$(si_tnep "$d"); [ -n "$tf" ]; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=torchnep; echo "file=$tf"; o=$(dirname "$d/$tf")
            echo "hdr=$(head -n 1 "$d/$tf" | cut -c1-300)"; echo "last=$(tail -n 1 "$d/$tf" | cut -c1-300)"
            head -n 300 "$o/output.log" 2>/dev/null | grep -a '^Training: epochs' | tail -n 1 | sed 's/^/sched=/'
            awk '$1=="epoch" && $2~/^[0-9]+$/ {print "total="$2; exit}' "$d/nep.in" 2>/dev/null
            tail -n 40 "$o/output.log" 2>/dev/null | grep -qa '^Done\\. Best loss' && echo done=1
            echo "mtime=$(stat -c %Y "$d/$tf" 2>/dev/null)"
          elif [ -f "$d/loss.out" ] && [ -f "$d/nep.in" ]; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=nep
            awk '$1=="generation" && $2~/^[0-9]+$/ {print "total="$2; exit}' "$d/nep.in"
            echo "last=$(tail -n 8 "$d/loss.out" | grep -v '^[[:space:]]*#' | tail -n 1)"; echo "mtime=$(stat -c %Y "$d/loss.out" 2>/dev/null)"
          elif [ -f "$d/loss.out" ] && [ -f "$d/gnep.in" ]; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=gnep
            awk '$1=="epoch" && $2~/^[0-9]+$/ {print "total="$2; exit}' "$d/gnep.in"
            echo "last=$(tail -n 1 "$d/loss.out")"; echo "mtime=$(stat -c %Y "$d/loss.out" 2>/dev/null)"
          elif ml=$(cd "$d" 2>/dev/null && ls -td -- logs/*.log 2>/dev/null | grep -v '_debug\\.log$' | head -n 1); [ -n "$ml" ] && head -n 20 "$d/$ml" | grep -qa 'MACE version'; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=mace; echo "file=$ml"; t=$(basename "$ml" .log); r="$d/results/${t}_train.txt"
            grep -ao 'max_num_epochs=[0-9]*' "$d/logs/${t}_debug.log" 2>/dev/null | tail -n 1 | sed 's/^max_num_epochs=/total=/'
            tail -n 200 "$d/$ml" | grep -a 'INFO: Epoch [0-9]*:' | tail -n 1 | cut -c1-300 | sed 's/^/last=/'
            [ -f "$r" ] && tail -n 1 "$r" | sed -n 's/.*"epoch": *\\([0-9][0-9]*\\).*/opt=\\1/p'
            tail -n 80 "$d/$ml" | grep -qa 'INFO: Training complete' && echo done=1
            echo "mtime=$(stat -c %Y "$d/$ml" "$r" 2>/dev/null | sort -n | tail -n 1)"
          elif [ -f "$d/lcurve.out" ] && head -n 1 "$d/lcurve.out" | grep -qa '^#.*step'; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=deepmd
            lg=$(cd "$d" 2>/dev/null && ls -td -- log *.log *.out 2>/dev/null | grep -vx 'lcurve\\.out' | head -n 5)
            sg=$( { echo lcurve.out; printf '%s\\n' "$lg"; } | while IFS= read -r x; do [ -n "$x" ] && stat -c '%Y.%s' -- "$d/$x" 2>/dev/null; done | tr '\\n' ' ')
            if si_same "$d" "$sg"; then echo same=1; else echo "sig=$sg"
            echo "hdr=$(head -n 1 "$d/lcurve.out" | cut -c1-400)"
            echo "last=$(tail -n 8 "$d/lcurve.out" | grep -v '^[[:space:]]*#' | tail -n 1 | cut -c1-400)"
            printf '%s\\n' "$lg" | while IFS= read -r x; do [ -f "$d/$x" ] || continue
              s=$(head -c 3000000 "$d/$x" | grep -ao -m 1 'Start to train [0-9]* steps' | tr -dc '0-9'); t=$(tail -n 40 "$d/$x")
              if [ -n "$s" ] || printf '%s\\n' "$t" | grep -qa 'DEEPMD'; then [ -n "$s" ] && echo "logsteps=$s"
                printf '%s\\n' "$t" | grep -qa 'Trained model has been saved to\\|average training time' && echo done=1; break; fi
            done
            cat "$d/input.json" "$d/out.json" 2>/dev/null | grep -ao '"\\(numb_steps\\|num_steps\\|stop_batch\\)"[[:space:]]*:[[:space:]]*[0-9]*' | head -n 1 | sed 's/.*:[[:space:]]*/steps=/'
            echo "mtime=$(stat -c %Y "$d/lcurve.out" 2>/dev/null)"; fi
          elif [ -f "$d/run.in" ] && { [ -f "$d/neighbor.out" ] || [ -f "$d/thermo.out" ]; }; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=gpumd
            sg=$(stat -c '%Y.%s' "$d/run.in" "$d/neighbor.out" "$d/thermo.out" 2>/dev/null | tr '\\n' ' ')
            if si_same "$d" "$sg"; then echo same=1; else echo "sig=$sg"
            awk '$1=="run" && $2~/^[0-9]+$/ {s+=$2} END {if (s>0) print "total="s}' "$d/run.in"
            awk '$1=="dump_thermo" && $2~/^[0-9]+$/ && $2>0 {print "interval="$2; exit}' "$d/run.in"
            if [ -f "$d/neighbor.out" ]; then
              echo "neighbor=$(tail -n 1 "$d/neighbor.out")"; echo "mtime=$(stat -c %Y "$d/neighbor.out" 2>/dev/null)"
            else
              echo "thermo_rows=$(grep -cv '^[[:space:]]*#' "$d/thermo.out")"; echo "mtime=$(stat -c %Y "$d/thermo.out" 2>/dev/null)"
            fi; fi
          elif [ -f "$d/INCAR" ] && [ -f "$d/01/OSZICAR" ] && grep -qiE '^[[:space:]]*IMAGES[[:space:]]*=[[:space:]]*[1-9]' "$d/INCAR"; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=vasp; echo neb=1
            sg=$(stat -c '%Y.%s' "$d/INCAR" "$d"/[0-9][0-9]/OSZICAR "$d"/[0-9][0-9]/OUTCAR 2>/dev/null | tr '\\n' ' ')
            if si_same "$d" "$sg"; then echo same=1; else echo "sig=$sg"
            \(vaspIncarAwk) "$d/INCAR"
            tail -n 3000 "$d/01/OSZICAR" | \(vaspOszicarAwk)
            for s in "$d"/[0-9][0-9]; do
              [ -f "$s/OUTCAR" ] && tail -n 25000 "$s/OUTCAR" | awk -v i="${s##*/}" '/energy  without entropy/ { e = $NF } /FORCES: max atom, RMS/ { f = $5 } END { print "nebimg=" i "|" e "|" f }'
            done
            if [ -f "$d/01/OUTCAR" ]; then
              if tail -n 400 "$d/01/OUTCAR" | grep -q 'General timing and accounting'; then echo done=1; else echo done=0; fi
            fi
            echo "mtime=$(stat -c %Y "$d/01/OSZICAR" 2>/dev/null)"; fi
          elif [ -f "$d/INCAR" ] && [ -f "$d/OSZICAR" ]; then
            printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=vasp
            sg=$(stat -c '%Y.%s' "$d/INCAR" "$d/OSZICAR" "$d/OUTCAR" 2>/dev/null | tr '\\n' ' ')
            if si_same "$d" "$sg"; then echo same=1; else echo "sig=$sg"
            \(vaspIncarAwk) "$d/INCAR"
            tail -n 3000 "$d/OSZICAR" | \(vaspOszicarAwk)
            if [ -f "$d/OUTCAR" ]; then
              if tail -n 400 "$d/OUTCAR" | grep -q 'General timing and accounting'; then echo done=1; else echo done=0; fi
            fi
            echo "mtime=$(stat -c %Y "$d/OSZICAR" 2>/dev/null)"; fi
          elif [ -f "$d/log.lammps" ]; then
            sg=$(stat -c '%Y.%s' "$d/log.lammps" 2>/dev/null | tr '\\n' ' ')
            if si_same "$d" "$sg"; then printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=lammps; echo same=1; else
            L=$(grep -naE '^[[:space:]]*run[[:space:]]' "$d/log.lammps" | tail -n 1 | cut -d: -f1)
            if [ -n "$L" ]; then
              printf '\(marker)DIR %s|%s\\n' "$id" "$d"; echo kind=lammps; echo "sig=$sg"
              tail -n +"$L" "$d/log.lammps" | \(lammpsAwk)
              if tail -n 30 "$d/log.lammps" | grep -q '^Total wall time'; then echo done=1; else echo done=0; fi
              echo "mtime=$(stat -c %Y "$d/log.lammps" 2>/dev/null)"
            fi; fi
        """
    }

    private static let neighborRegex = try! NSRegularExpression(
        pattern: #"^Neighbor info at step\s+(\d+):\s*radial\(max=(\d+),actual=(\d+)\),\s*angular\(max=(\d+),actual=(\d+)\)"#)

    /// 解析 "apps" 段：返回 任务ID -> 进度。
    /// `rules`：解析自定义规则用的规则表（默认是当前设置里的；测试规则时传入正在编辑的那一条）。
    static func parse(_ lines: [String], rules: [ProgressRule]? = nil) -> [String: AppProgress] {
        var out: [String: AppProgress] = [:]
        var id: String?
        var dir = ""
        var kv: [String: String] = [:]
        var tails: [String] = []
        var nebImgs: [String] = []

        func flush() {
            defer { kv = [:]; tails = []; nebImgs = [] }
            guard let id, let kind = kv["kind"].flatMap(AppProgress.Kind.init(rawValue:)) else { return }
            var p = AppProgress(kind: kind, dir: dir)
            if kv["same"] == "1" { p.unchanged = true; out[id] = p; return }
            p.signature = kv["sig"].flatMap { $0.isEmpty ? nil : $0 }
            p.updatedAt = kv["mtime"].flatMap(Double.init).map { Date(timeIntervalSince1970: $0) }
            switch kv["done"] {
            case "1": p.endMarker = true
            case "0": p.endMarker = false
            default: break
            }
            switch kind {
            case .nep, .gnep:
                if let t = kv["total"].flatMap(Double.init) { p.total = t } else {
                    p.total = kind == .nep ? 100_000 : 50
                    p.hasTarget = false
                }
                let nums = (kv["last"] ?? "").split(separator: " ").compactMap { Double($0) }
                if !nums.isEmpty {
                    p.current = nums[0]
                    let labels = kind == .nep ? Array(nepLossLabels(columns: nums.count).dropFirst()) : ["Loss", "Energy", "Force", "Virial"]
                    for (i, v) in nums.dropFirst().enumerated() where i < labels.count {
                        p.losses.append(.init(label: labels[i], value: v))
                    }
                    // 没有测试集时 test 列全是 0，不显示。
                    let tests = p.losses.filter { $0.label.hasSuffix("-test") }
                    if !tests.isEmpty, tests.allSatisfy({ $0.value == 0 }) {
                        p.losses.removeAll { $0.label.hasSuffix("-test") }
                    }
                }
            case .torchnep:
                // output.log 里的 "Training: epochs 1..200"（续跑时是 101..200），其次是 nep.in 的 epoch
                if let s = kv["sched"], let m = s.range(of: #"\.\.(\d+)"#, options: .regularExpression) {
                    p.total = Double(s[m].dropFirst(2))
                } else {
                    p.total = kv["total"].flatMap(Double.init)
                }
                p.hasTarget = p.total != nil
                p.dataFile = kv["file"]
                let nums = (kv["last"] ?? "").split(separator: " ").compactMap { Double($0) }
                // 表头：# epoch  loss  rmse_e(eV/atom)  rmse_f(eV/A)  rmse_v(eV/atom)  rmse_stress(GPa)
                let names = torchnepLabels(kv["hdr"] ?? "")
                guard nums.count >= 2, names.count == nums.count else { return }
                p.current = nums[0]
                for (i, v) in nums.enumerated().dropFirst() { p.losses.append(.init(label: names[i], value: v)) }
            case .mace:
                p.total = kv["total"].flatMap(Double.init)
                p.hasTarget = p.total != nil
                p.dataFile = kv["file"]
                // 日志里的 Epoch N（从 0 数，验证集误差每 eval_interval 个 epoch 一行）表示已经跑完 N+1 个；
                // results/*_train.txt 最后一行的 epoch K 是正在跑的那个（已经跑完 K 个），更新更及时。
                let m = maceEpochLine(kv["last"] ?? "")
                let done = [m.map { $0.epoch + 1 }, kv["opt"].flatMap(Double.init)].compactMap { $0 }.max()
                p.current = done ?? 0
                p.losses = m?.values ?? []
                if let t = p.total, let c = p.current, c > t { return }
            case .deepmd:
                // 目标：日志里 "Start to train N steps"（num_epochs 换算后的实际步数）优先，其次输入 JSON 的 numb_steps
                p.total = (kv["logsteps"] ?? kv["steps"]).flatMap(Double.init).flatMap { $0 > 0 ? $0 : nil }
                p.hasTarget = p.total != nil
                p.dataFile = "lcurve.out"
                let names = deepmdLabels(kv["hdr"] ?? "")
                let cols = (kv["last"] ?? "").split(separator: " ").map(String.init)
                guard cols.count >= 2, cols.count == names.count, let step = Double(cols[0]) else { return }
                p.current = step
                if let t = p.total, step > t { return }
                var values: [AppProgress.Value] = []
                for (i, c) in cols.enumerated().dropFirst() {
                    // 没有验证集时是 nan，不显示
                    if let v = Double(c), v.isFinite { values.append(.init(label: names[i], value: v)) }
                }
                p.losses = values.enumerated().sorted { a, b in
                    let ra = deepmdRank(a.element.label), rb = deepmdRank(b.element.label)
                    return ra != rb ? ra < rb : a.offset < b.offset
                }.map(\.element)
            case .gpumd:
                p.total = kv["total"].flatMap(Double.init)
                p.hasTarget = p.total != nil
                if let line = kv["neighbor"],
                   let m = neighborRegex.firstMatch(in: line, range: NSRange(line.startIndex..., in: line)) {
                    let ns = line as NSString
                    let n = (1...5).map { Int(ns.substring(with: m.range(at: $0))) ?? 0 }
                    p.current = Double(n[0])
                    p.neighbor = .init(radialActual: n[2], radialMax: n[1], angularActual: n[4], angularMax: n[3])
                } else if let rows = kv["thermo_rows"].flatMap(Double.init), let iv = kv["interval"].flatMap(Double.init) {
                    p.current = rows * iv
                }
            case .vasp:
                guard let v = parseVasp(kv) else { return }
                p.vaspMode = v.mode; p.current = v.current; p.total = v.total; p.hasTarget = v.total != nil
                if v.mode == .neb {
                    let n = parseNeb(nebImgs, images: kv["images"].flatMap(Int.init))
                    p.nebBarrier = n.barrier; p.nebForce = n.force; p.nebImages = kv["images"].flatMap(Int.init)
                }
            case .lammps:
                guard let l = parseLammps(kv) else { return }
                p.current = l.current; p.total = l.total
            case .custom:
                guard let rid = kv["rule"].flatMap(UUID.init(uuidString:)),
                      let rule = (rules ?? ProgressRule.active).first(where: { $0.id == rid }),
                      let x = rule.extract(from: tails) else { return }
                p.customName = rule.name
                p.customUnit = rule.unit
                p.current = x.current
                p.total = x.total ?? Double(rule.total)
                    ?? kv["totalline"].flatMap { ProgressRule.number(after: rule.totalKeyword, in: $0) }
                if let t = p.total {
                    // 目标值不合理或当前值超过目标值：当作没识别到
                    guard t > 0, x.current >= 0, x.current <= t * 1.000001 else { return }
                }
                p.hasTarget = p.total != nil
                if rule.doneMarker.isEmpty { p.endMarker = nil }
            }
            out[id] = p
        }

        for line in lines {
            if line.hasPrefix(marker + "DIR ") {
                flush()
                let rest = line.dropFirst((marker + "DIR ").count)
                if let bar = rest.firstIndex(of: "|") {
                    id = String(rest[..<bar]); dir = String(rest[rest.index(after: bar)...])
                } else { id = nil }
            } else if line.hasPrefix("tail=") {
                tails.append(String(line.dropFirst(5)))
            } else if line.hasPrefix("nebimg=") {
                nebImgs.append(String(line.dropFirst(7)))
            } else if let eq = line.firstIndex(of: "=") {
                kv[String(line[..<eq])] = String(line[line.index(after: eq)...])
            }
        }
        flush()
        return out
    }
    /// VASP：NSW ≤ 0 → 单点（电子步）；IBRION=0 → MD（离子步 / NSW）；IBRION=1/2/3 → 结构优化（只显示离子步）。
    /// 其它 IBRION（声子等）或写法看不懂时返回 nil。
    static func parseVasp(_ kv: [String: String]) -> (mode: AppProgress.VaspMode, current: Double, total: Double?)? {
        // NEB：只显示离子步（01 像点的 OSZICAR），不按 NSW 算百分比（一般早于 NSW 收敛）
        if kv["neb"] == "1" {
            guard let i = kv["ionic"].flatMap(Int.init) else { return nil }
            return (.neb, Double(i), nil)
        }
        var nsw = 0, ibrion: Int?
        if let s = kv["nsw"] { guard let v = Int(s) else { return nil }; nsw = v }
        if let s = kv["ibrion"] { guard let v = Int(s) else { return nil }; ibrion = v }
        let ionic = kv["ionic"].flatMap(Int.init), elec = kv["elec"].flatMap(Int.init)
        if nsw <= 0 {
            guard let e = elec else { return nil }
            return (.scf, Double(e), nil)
        }
        switch ibrion ?? 0 {
        case 0:
            guard let i = ionic, i <= nsw else { return nil }
            return (.md, Double(i), Double(nsw))
        case 1, 2, 3:
            guard let i = ionic else { return nil }
            return (.relax, Double(i), nil)
        default:
            return nil
        }
    }

    /// NEB 各像点（`编号|能量|VTST 力`，来自每个像点 OUTCAR 的末尾）：
    /// 能垒 = 除初态外最高的能量 − 初态（00）能量，和 VTST 的 nebef.pl 一样用 energy(sigma→0)；
    /// 最大力 = 中间像点（1…IMAGES）里最大的 VTST `FORCES: max atom`。缺数据时对应的值为 nil。
    static func parseNeb(_ lines: [String], images: Int?) -> (barrier: Double?, force: Double?) {
        var energy: [Int: Double] = [:], force: [Int: Double] = [:]
        for l in lines {
            let f = l.components(separatedBy: "|")
            guard f.count == 3, let i = Int(f[0]) else { continue }
            if let e = Double(f[1]) { energy[i] = e }
            if let v = Double(f[2]) { force[i] = v }
        }
        let last = images.map { $0 + 1 } ?? (energy.keys.max() ?? 0)
        let isMiddle: (Int) -> Bool = { $0 >= 1 && $0 < last }
        var barrier: Double?
        if let e0 = energy[0] {
            let others = energy.filter { $0.key > 0 }.map(\.value)
            if let top = others.max() { barrier = top - e0 }
        }
        let fm = force.filter { isMiddle($0.key) }.map(\.value).max()
        return (barrier, fm)
    }

    /// LAMMPS：当前步 = 最后一行的 Step − 这段 run 第一行的 Step，目标 = run 的步数。任何一处不对就返回 nil。
    static func parseLammps(_ kv: [String: String]) -> (current: Double, total: Double)? {
        guard kv["bad"] == "0",
              let total = kv["total"].flatMap(Double.init), total > 0,
              let first = kv["first"].flatMap(Double.init),
              let last = kv["last"].flatMap(Double.init) else { return nil }
        let cur = last - first
        guard cur >= 0, cur <= total else { return nil }
        return (cur, total)
    }

    /// NEP loss.out 各列（含第一列代数）的名字，按列数区分（GPUMD fitness.cu 的 write_row）：
    /// 10 列普通势、12 列多了 ediff、14 / 16 列带电荷（Q、Z = BEC）、6 列偶极 / 极化率。
    static func nepLossLabels(columns n: Int) -> [String] {
        let head = ["gen", "Total", "L1", "L2"]
        switch n {
        case 6: return head + ["P-train", "P-test"]
        case 12: return head + ["Energy", "Force", "Virial", "E-test", "F-test", "V-test", "Ediff", "Ediff-test"]
        case 14: return head + ["Energy", "Force", "Virial", "Charge", "BEC", "E-test", "F-test", "V-test", "Q-test", "Z-test"]
        case 16: return head + ["Energy", "Force", "Virial", "Charge", "BEC", "E-test", "F-test", "V-test", "Q-test", "Z-test", "Ediff", "Ediff-test"]
        case 7...: return head + ["Energy", "Force", "Virial", "E-test", "F-test", "V-test"]
        default: return ["epoch", "Loss", "Energy", "Force", "Virial"]
        }
    }

    /// TorchNEP loss.out 的表头 → 每列的名字（含第一列 epoch）：`rmse_e(eV/atom)` → `E (eV/atom)`，`loss` → `Loss`。
    static func torchnepLabels(_ header: String) -> [String] {
        let short = ["e": "E", "f": "F", "v": "V", "stress": "S"]
        return header.split(separator: " ").filter { $0 != "#" }.map { t -> String in
            var name = String(t), unit = ""
            if let p = name.firstIndex(of: "(") { unit = " " + name[p...]; name = String(name[..<p]) }
            if name == "loss" { return "Loss" }
            if name.hasPrefix("#") { name.removeFirst() }
            if name.hasPrefix("rmse_") { name = String(name.dropFirst(5)); name = short[name] ?? name }
            return name + unit
        }
    }

    /// DeePMD lcurve.out 的表头 → 每列的名字（含第一列 step）：
    /// `rmse_e_val` → `RMSE E val`，`mae_f_trn` → `MAE F trn`，`rmse_val` → `RMSE val`，多任务的 `rmse_e_val_water` → `RMSE E val water`。
    static func deepmdLabels(_ header: String) -> [String] {
        header.split(separator: " ").filter { $0 != "#" }.map { t -> String in
            var name = String(t)
            if name.hasPrefix("#") { name.removeFirst() }
            let parts = name.split(separator: "_").map(String.init)
            guard let m = parts.first, m == "rmse" || m == "mae" else { return name }
            return parts.map { ["rmse", "mae", "e", "f", "v"].contains($0) ? $0.uppercased() : $0 }.joined(separator: " ")
        }
    }

    /// 摘要和详情里的顺序：验证集在前，能量 → 力 → 维里 → 总误差，学习率最后。
    static func deepmdRank(_ label: String) -> Int {
        let w = label.split(separator: " ").map(String.init)
        if label == "lr" { return 99 }
        let split = w.contains("val") ? 0 : w.contains("trn") ? 10 : 20
        let q = w.contains("E") ? 0 : w.contains("F") ? 1 : w.contains("V") ? 2 : 3
        return split + q
    }

    /// MACE 训练日志里的一行验证集误差：
    /// `… INFO: Epoch 595: head: Default, loss=0.00833367, RMSE_E_per_atom=    0.22 meV, RMSE_F=    9.08 meV / A`
    /// （error_table 不同，误差的名字也不同，比如 RMSE_E、MAE_F；按 名字=数字 [单位] 通用地取。）
    static func maceEpochLine(_ line: String) -> (epoch: Double, values: [AppProgress.Value])? {
        guard let r = line.range(of: #"Epoch (\d+):"#, options: .regularExpression),
              let ep = Double(line[r].dropFirst(6).dropLast()) else { return nil }
        let rest = String(line[r.upperBound...])
        let re = try! NSRegularExpression(pattern: #"(\w+)=\s*([-+0-9.eE]+)\s*([^,=]*?)\s*(?:,|$)"#)
        var values: [AppProgress.Value] = []
        for m in re.matches(in: rest, range: NSRange(rest.startIndex..., in: rest)) {
            let ns = rest as NSString
            guard let v = Double(ns.substring(with: m.range(at: 2))) else { continue }
            let key = ns.substring(with: m.range(at: 1))
            let unit = ns.substring(with: m.range(at: 3)).replacingOccurrences(of: " / A", with: "/Å")
            if key == "loss" { values.append(.init(label: "Loss", value: v)); continue }
            let name = key.replacingOccurrences(of: "_per_atom", with: "").replacingOccurrences(of: "_", with: " ")
            values.append(.init(label: unit.isEmpty ? name : "\(name) (\(unit))", value: v))
        }
        return (ep, values)
    }
}

// MARK: - 设置页"测试规则"

struct RuleTestResult: Equatable {
    var ok: Bool
    var headline: String
    /// 补充说明，以及没匹配上时文件的最后几行。
    var notes: [String] = []
    var lines: [String] = []
}

extension AppDetectors {
    /// 先报告目录和文件是否存在，再跑这一条规则（不跑内置识别）。只读。
    static func ruleTestScript(dir: String, rule: ProgressRule) -> String {
        var s = """
        \(jobIDFunction)
        d=\(SSHRunner.shellQuote(dir))
        case "$d" in "~"/*) d="$HOME/${d#\\~/}";; esac
        if [ ! -d "$d" ]; then echo '\(marker)NODIR'; else
          echo "picked=$(si_pick "$d" '\(rule.file)')"

        """
        if !rule.totalFile.isEmpty { s += "  echo \"tpicked=$(si_pick \"$d\" '\(rule.totalFile)')\"\n" }
        if rule.match == .keyword {
            // 没匹配上时给用户看文件最后几行
            s += "  f=\"$d/$(si_pick \"$d\" '\(rule.file)')\"; [ -f \"$f\" ] && tail -n 5 \"$f\" | cut -c1-200 | sed 's/^/last=/'\n"
        }
        s += "fi\n"
        return s + directoryScript(id: "t", dir: dir, rules: [rule], builtin: false)
    }

    static func interpretTest(_ stdout: String, rule: ProgressRule) -> RuleTestResult {
        let lines = stdout.components(separatedBy: "\n")
        if lines.contains(marker + "NODIR") { return .init(ok: false, headline: L("服务器上没有这个目录", "Directory not found on the server")) }
        func value(_ key: String) -> String? {
            lines.first { $0.hasPrefix(key + "=") }.map { String($0.dropFirst(key.count + 1)) }
        }
        guard let picked = value("picked") else {
            return .init(ok: false, headline: L("没有收到服务器的回应", "No response from the server"))
        }
        if picked.isEmpty {
            return .init(ok: false, headline: L("目录里没有匹配「\(rule.file)」的文件", "No file matching “\(rule.file)” in this directory"))
        }
        var notes = [L("文件：", "File: ") + picked]
        if !rule.totalFile.isEmpty, value("tpicked")?.isEmpty != false {
            notes.append(L("没找到目标值文件「\(rule.totalFile)」", "Target file “\(rule.totalFile)” not found"))
        }
        if let a = AppDetectors.parse(lines, rules: [rule])["t"], let c = a.current {
            var head = L("当前值 ", "Current ") + AppProgress.compact(c)
            if let t = a.total {
                head += L(" · 目标值 ", " · target ") + AppProgress.compact(t)
                if let f = a.fraction { head += "（\(Format.percent(f))）" }
            } else {
                head += L(" · 没有目标值，只显示当前值", " · no target, showing the current value only")
            }
            if let m = a.endMarker { notes.append(m ? L("已出现结束标记", "End marker found") : L("还没有出现结束标记", "End marker not found yet")) }
            return .init(ok: true, headline: head, notes: notes)
        }
        let tails = lines.filter { $0.hasPrefix("tail=") }.map { String($0.dropFirst(5)) }
        let shown = rule.match == .keyword
            ? lines.filter { $0.hasPrefix("last=") }.map { String($0.dropFirst(5)) }
            : Array(tails.suffix(5))
        if let x = rule.extract(from: tails), let t = x.total ?? Double(rule.total) {
            return .init(ok: false, headline: L("当前值 \(AppProgress.compact(x.current)) 超过了目标值 \(AppProgress.compact(t))，不会显示进度",
                                                "Current \(AppProgress.compact(x.current)) exceeds target \(AppProgress.compact(t)); progress won't be shown"),
                         notes: notes, lines: shown)
        }
        let head = rule.match == .keyword
            ? L("文件里没有「\(rule.pattern)」后面紧跟数字的行", "No line has a number right after “\(rule.pattern)”")
            : L("文件最后 40 行都没有匹配上正则", "The regex matched none of the last 40 lines")
        return .init(ok: false, headline: head, notes: notes + [L("文件最后几行：", "Last lines of the file:")], lines: shown)
    }
}
