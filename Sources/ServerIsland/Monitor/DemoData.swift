import Foundation

/// `--demo` 模式和预览图 / 官网截图用的假数据，不会连接任何服务器。
/// 场景是计算材料学的日常：Slurm 集群上跑 VASP / GPUMD / 数组扫描，工作站上跑 NEP、LAMMPS 和机器学习势训练。
/// 注意：这里的用户名、路径、主机名都是虚构的（用户名 demo），不要写进真实环境的信息。
enum DemoData {
    static func settings() -> AppSettings {
        var s = AppSettings()
        var a = HostConfig(alias: "hpc-login", displayName: L("HPC 集群", "HPC Cluster"), kind: .slurm)
        a.auth = .interactive
        a.prices = [PartitionPrice(partition: "", unit: .cpu, price: 0.03),
                    PartitionPrice(partition: "cpu", unit: .cpu, price: 0.04, note: L("64 节点 · 共 4096 核", "64 nodes · 4096 cores")),
                    PartitionPrice(partition: "gpu-v100", unit: .gpu, price: 2.5, note: L("16 节点 · V100 共 64 张GPU", "16 nodes · V100 64 GPUs"))]
        let b = HostConfig(alias: "gpu-box", displayName: L("4090 工作站", "4090 Workstation"), kind: .direct)
        s.hosts = [a, b]
        return s
    }

    /// 费用页的演示账本：本月和上月在 HPC 集群上结束的作业（虚构）。
    static func costEntries(host: UUID, now: Date = Date()) -> [CostEntry] {
        let cal = Calendar.current
        let monthStart = cal.date(from: cal.dateComponents([.year, .month], from: now)) ?? now
        let today = cal.component(.day, from: now)
        var out: [CostEntry] = []
        // (名称, 分区, 计价, 资源量, 小时, 单价, 几天前)
        let rows: [(String, String, PriceUnit, Double, Double, Double, Int)] = [
            ("aimd_LGPS_1200K", "cpu", .cpu, 128, 48, 0.04, 1),
            ("relax_Li3PS4_pbe", "cpu", .cpu, 64, 2.1, 0.04, 0),
            ("phonon_LiCoO2_222", "cpu", .cpu, 64, 0.7, 0.04, 0),
            ("nep_train_LiF", "gpu-v100", .gpu, 4, 24, 2.5, 3),
            ("gpumd_kappa_250K", "gpu-v100", .gpu, 1, 11.5, 2.5, 2),
            ("scf_LiCoO2_kmesh", "cpu", .cpu, 32, 0.9, 0.04, 4),
            ("neb_Li_vacancy", "cpu", .cpu, 128, 6, 0.04, 6),
            ("gpumd_kappa_300K_old", "gpu-v100", .gpu, 1, 9, 2.5, 8),
            ("md_HEA_npt", "gpu-v100", .gpu, 2, 14, 2.5, 11),
            ("relax_LiCoO2_pbe", "cpu", .cpu, 64, 5, 0.04, 13),
        ]
        for (i, r) in rows.enumerated() where r.6 < today {
            let end = cal.startOfDay(for: now).addingTimeInterval(Double(-r.6) * 86400 + 3600 + Double(i) * 600)
            out.append(CostEntry(host: host, id: "88119\(10 + i)", name: r.0, partition: r.1, unit: r.2, amount: r.3,
                                 seconds: r.4 * 3600, price: r.5, endedAt: min(end, now.addingTimeInterval(-600))))
        }
        // 数组的 14 个元素
        for k in 0..<14 where today > 1 {
            out.append(CostEntry(host: host, id: "8812300_\(k)", arrayBase: "8812300", name: "eos_scan", partition: "cpu", unit: .cpu,
                                 amount: 32, seconds: 2400, price: 0.04, endedAt: cal.startOfDay(for: now).addingTimeInterval(1800 + Double(k) * 300)))
        }
        // 上月
        for k in 0..<8 {
            out.append(CostEntry(host: host, id: "8800\(100 + k)", name: "train_run_\(k)", partition: "gpu-v100", unit: .gpu, amount: 2,
                                 seconds: 20 * 3600, price: 2.5, endedAt: monthStart.addingTimeInterval(Double(-k - 1) * 3 * 86400)))
        }
        return out
    }

    static let aliases: Set<String> = ["hpc-login", "gpu-box"]
    static func isDemoHost(_ h: HostConfig) -> Bool { aliases.contains(h.alias) }

    /// 演示用的工作目录前缀。
    static let home = "/home/demo/projects"

    static func snapshot(for host: HostConfig, tick: Int, previous: HostSnapshot) -> HostSnapshot {
        var s = HostSnapshot()
        s.health = .ok
        s.lastUpdate = Date()
        s.latency = 0.08 + Double(tick % 5) * 0.01
        let t = Double(tick)
        let wave = { (phase: Double) in (sin(t / 3 + phase) + 1) / 2 }

        func appJob(_ id: String, _ name: String, source: RemoteJob.Source, _ app: AppProgress) -> RemoteJob {
            var j = RemoteJob(id: id, name: name, state: .running, source: source)
            j.app = app; j.progress = app.fraction; j.eta = app.eta
            j.message = app.title + " · " + app.summary
            return j
        }

        switch host.kind {
        case .slurm, .pbs:
            s.metrics = HostMetrics(cpuPercent: 18 + wave(0) * 12, load1: 6.2, cores: 64,
                                    memoryUsed: 92e9 + wave(1) * 8e9, memoryTotal: 256e9)

            // VASP 从头算分子动力学：NSW 已知，显示百分比
            var aimd = AppProgress(kind: .vasp, dir: home + "/Li3PS4/aimd-900K")
            aimd.vaspMode = .md; aimd.current = 1_240 + t; aimd.total = 5_000; aimd.hasTarget = true
            aimd.rate = 0.034; aimd.updatedAt = Date(); aimd.endMarker = false
            var md = appJob("8812044", "aimd_Li3PS4_900K", source: .slurm, aimd)
            md.elapsed = 36_420 + t * 2; md.limit = 48 * 3600; md.detail = "cn[012-013] · cpu"
            md.partition = "cpu"; md.nodes = 2; md.alloc = JobAlloc(cpus: 128, gpus: 0, nodes: 2)

            // VASP 结构优化：不知道总步数，只显示离子步
            var relaxApp = AppProgress(kind: .vasp, dir: home + "/LiCoO2/relax-hse")
            relaxApp.vaspMode = .relax; relaxApp.current = 17; relaxApp.hasTarget = false
            relaxApp.updatedAt = Date(); relaxApp.endMarker = false
            var relax = appJob("8812101", "relax_LiCoO2_hse", source: .slurm, relaxApp)
            relax.elapsed = 11_530 + t * 2; relax.limit = 24 * 3600; relax.detail = "cn027 · cpu"
            relax.partition = "cpu"; relax.nodes = 1; relax.alloc = JobAlloc(cpus: 64, gpus: 0, nodes: 1)

            // GPUMD 热导率
            var kappa = AppProgress(kind: .gpumd, dir: home + "/graphene/kappa-300K")
            kappa.current = 1_240_000 + t * 3_000; kappa.total = 2_000_000; kappa.rate = 1_520; kappa.updatedAt = Date()
            kappa.neighbor = .init(radialActual: 48, radialMax: 120, angularActual: 21, angularMax: 60)
            var gpumd = appJob("8812230", "gpumd_kappa_300K", source: .slurm, kappa)
            gpumd.elapsed = 2_710 + t * 2; gpumd.limit = 12 * 3600; gpumd.detail = "gpu07 · gpu-v100"
            gpumd.partition = "gpu-v100"; gpumd.nodes = 1; gpumd.alloc = JobAlloc(cpus: 8, gpus: 1, nodes: 1)

            // 数组作业：40 个应变点的状态方程扫描
            var eos = RemoteJob(id: "8812300", name: "eos_scan", state: .running, source: .slurm)
            eos.arrayJobID = "8812300"
            var running: [RemoteJob] = []
            for k in 0..<6 {
                var e = RemoteJob(id: "8812300_\(16 + k)", name: "eos_scan", state: .running, source: .slurm)
                e.arrayJobID = "8812300"; e.arrayTask = "\(16 + k)"
                e.elapsed = Double(300 + k * 170) + t * 2; e.limit = 4 * 3600; e.detail = "cn0\(40 + k) · cpu"
                e.partition = "cpu"; e.nodes = 1; e.alloc = JobAlloc(cpus: 32, gpus: 0, nodes: 1)
                running.append(e)
            }
            eos.array = ArrayInfo(baseID: "8812300", running: running, pending: 18, done: 14, failed: 2, total: 40, accurate: true)
            eos.elapsed = running.compactMap(\.elapsed).max()
            eos.limit = 4 * 3600
            eos.partition = "cpu"
            eos.detail = L("运行 6 · 排队 18 · 失败 2", "6 running · 18 pending · 2 failed")

            var phonon = RemoteJob(id: "8812355", name: "phonon_Si_444", state: .pending, source: .slurm)
            phonon.limit = 12 * 3600; phonon.detail = "(Priority) · cpu"; phonon.reason = "Priority"
            var band = RemoteJob(id: "8812356", name: "band_LiCoO2", state: .pending, source: .slurm)
            band.limit = 6 * 3600; band.detail = "(Dependency) · cpu"; band.reason = "Dependency"

            s.jobs = [md, relax, eos, gpumd, phonon, band]
        case .direct:
            s.metrics = HostMetrics(cpuPercent: 41 + wave(2) * 30, load1: 9.8, cores: 32,
                                    memoryUsed: 71e9 + wave(3) * 6e9, memoryTotal: 128e9)
            var gpus: [GPUStat] = []
            for i in 0..<4 {
                let idle = i == 3
                let util: Double = idle ? 0 : 70 + wave(Double(i)) * 29
                let mem: Double = idle ? 312 : 6_000 + wave(Double(i)) * 9_000
                gpus.append(GPUStat(index: i, name: "RTX 4090", utilization: util, memoryUsedMB: mem,
                                    memoryTotalMB: 24_564, temperature: 64))
            }
            s.gpus = gpus

            // NEP 训练（GPUMD 套件）
            var nepApp = AppProgress(kind: .nep, dir: home + "/Li3PS4/nep-train")
            nepApp.current = 7_800 + t * 20; nepApp.total = 20_000; nepApp.rate = 26.4; nepApp.updatedAt = Date()
            nepApp.losses = [.init(label: "Total", value: 0.1815), .init(label: "L1", value: 0.045), .init(label: "L2", value: 0.064),
                             .init(label: "Energy", value: 0.00074), .init(label: "Force", value: 0.07098), .init(label: "Virial", value: 0.01131)]
            var nep = appJob("41022", "nep", source: .process, nepApp)
            nep.elapsed = 5_420 + t * 2; nep.gpuMemoryMB = 2_600

            // LAMMPS 高熵合金 NPT
            var lmpApp = AppProgress(kind: .lammps, dir: home + "/HEA/npt-1200K")
            lmpApp.current = 380_000 + t * 2_000; lmpApp.total = 1_000_000; lmpApp.rate = 830; lmpApp.updatedAt = Date()
            lmpApp.endMarker = false
            var lmp = appJob("41377", "lmp", source: .process, lmpApp)
            lmp.elapsed = 2_300 + t * 2; lmp.cpuPercent = 1_580

            // MACE 机器学习势训练
            var mace = AppProgress(kind: .mace, dir: home + "/MACE/run-02")
            mace.current = 37; mace.total = 200; mace.updatedAt = Date(); mace.dataFile = "logs/MACE_run-123.log"
            mace.losses = [.init(label: "Loss", value: 0.0213), .init(label: "RMSE E (meV)", value: 0.41), .init(label: "RMSE F (meV/Å)", value: 14.6)]
            var train = appJob("41856", "python train_mace.py", source: .process, mace)
            train.elapsed = 9_250 + t * 2; train.cpuPercent = 104; train.gpuMemoryMB = 14_800

            s.jobs = [nep, lmp, train]
        }
        return s
    }
}
