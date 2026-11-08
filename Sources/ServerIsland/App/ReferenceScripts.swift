import Foundation

/// `ServerIsland --dump-scripts <目录>`：导出一组固定配置下生成的远端脚本，
/// 和 Windows / Linux 版（cross/）的 `SelfTest.ReferenceScripts()` 逐字比较：
/// `SERVERISLAND_SCRIPT_REF=<目录> dotnet run -- --selftest`。两边的列表要保持一致。
enum ReferenceScripts {
    @MainActor
    static func all() -> [(String, String)] {
        func rule(_ id: String, _ build: (inout ProgressRule) -> Void) -> ProgressRule {
            var r = ProgressRule(); r.id = UUID(uuidString: id)!; build(&r); return r
        }
        let rules = [
            rule("11111111-2222-3333-4444-555555555555") { $0.name = "PyTorch"; $0.file = "*.log"; $0.pattern = "epoch"; $0.unit = "epoch"; $0.doneMarker = "Training finished" },
            rule("66666666-7777-8888-9999-AAAAAAAAAAAA") { $0.name = "Solver"; $0.file = "run.log"; $0.pattern = "step"; $0.totalFile = "config.ini"; $0.totalKeyword = "max_steps" },
            rule("BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF") { $0.name = "Re"; $0.file = "progress.txt"; $0.match = .regex; $0.pattern = #"iter=(\d+)"# },
        ]
        let saved = ProgressRule.active
        ProgressRule.active = rules
        defer { ProgressRule.active = saved }

        var slurm = HostConfig(alias: "x", kind: .slurm); slurm.slurmAlsoProcesses = true; slurm.allowCancel = true
        slurm.commands.preamble = "module load slurm"
        var direct = HostConfig(alias: "y"); direct.watched = [WatchedProcess(pid: "4242", name: "")]
        var pbs = HostConfig(alias: "z", kind: .pbs); pbs.slurmAlsoProcesses = true; pbs.allowCancel = true
        pbs.pathFilter = "~/LLZO\n/scratch/demo"
        var filtered = direct; filtered.pathFilter = "~/runs"
        let sj = RemoteJob(id: "4821337", name: "it's \"quoted\"", state: .running, source: .slurm)
        let pbsJob = RemoteJob(id: "1003_2", name: "it's \"scan\"", state: .running, source: .slurm)
        let pj = RemoteJob(id: "18342", name: "python3 train.py", state: .running, source: .process)
        let cluster = HostConfig(alias: "c", kind: .slurm)
        var custom = HostConfig(alias: "c", kind: .slurm)
        custom.commands.squeue = #"squeue -u "$(id -un)" -h -o '%i|%j|%T|%M|%l|%D|%R|%P'"#
        return [
            ("poll-slurm.sh", RemoteScript.build(for: slurm)),
            ("poll-direct.sh", RemoteScript.build(for: direct)),
            ("detail-slurm.sh", RemoteScript.detailScript(host: slurm, job: sj)),
            ("detail-process.sh", RemoteScript.detailScript(host: direct, job: pj)),
            ("cancel-slurm.sh", RemoteScript.cancelScript(host: slurm, job: sj, expectedElapsed: nil, force: false)!),
            ("cancel-process.sh", RemoteScript.cancelScript(host: direct, job: pj, expectedElapsed: 9250, force: true)!),
            ("history.sh", RemoteScript.historyScript(host: slurm)),
            ("top.sh", RemoteScript.topProcessesScript(host: direct)),
            ("sacct.sh", RemoteScript.sacctScript(for: slurm, jobIDs: ["1", "2_3"])),
            ("final-state.sh", AppDetectors.finalStateScript(id: "123", dir: "/home/u/it's dir")),
            ("rule-test.sh", AppDetectors.ruleTestScript(dir: "~/runs/exp1", rule: rules[1])),
            ("curve-loss.sh", AppCurves.script(.loss(path: "/home/u/nep/loss.out", kind: .gnep), offset: 120, head: "100 0.3")),
            ("curve-nep.sh", AppCurves.script(.loss(path: "/home/u/nep/loss.out", kind: .nep), offset: 0, head: "")),
            ("curve-mace.sh", AppCurves.script(.loss(path: "/home/u/mace/logs/LLZO_run-123.log", kind: .mace), offset: 40, head: "x")),
            ("curve-vasp.sh", AppCurves.script(.vaspEnergy(path: "/home/u/v/OSZICAR"), offset: 0, head: "")),
            ("curve-force.sh", AppCurves.script(.vaspForce(path: "/home/u/v/OUTCAR"), offset: 300, head: " vasp.6.4.2")),
            ("partitions.sh", Partitions.script(host: slurm)),
            ("detect.sh", RemoteScript.detectScript),
            ("path-prelude.sh", SSHRunner.pathPrelude),
            ("poll-pbs.sh", RemoteScript.build(for: pbs)),
            ("poll-direct-filtered.sh", RemoteScript.build(for: filtered)),
            ("detail-pbs.sh", RemoteScript.detailScript(host: pbs, job: pbsJob)),
            ("cancel-pbs.sh", RemoteScript.cancelScript(host: pbs, job: pbsJob, expectedElapsed: nil, force: false)!),
            ("sacct-pbs.sh", RemoteScript.sacctScript(for: pbs, jobIDs: ["1001", "1003_2"])),
            ("curve-neb.sh", AppCurves.script(.neb(dir: "/home/u/neb run"), offset: 0, head: "")),
            ("poll-slurm-cached.sh", RemoteScript.build(for: slurm, appSignatures: ["/home/u/it's run": "1.2 3.4 ", "/home/u/a": "/home/u/a/x.log 1 2 "], includeDisk: false)),
            ("poll-cluster.sh", RemoteScript.build(for: cluster)),
            ("poll-cluster-custom.sh", RemoteScript.build(for: custom)),
            ("poll-cluster-billing.sh", RemoteScript.build(for: cluster, alloc: .init(query: ["1"], known: ["4821337", "8812300_16", "x;y"]))),
            ("poll-cluster-custom-billing.sh", RemoteScript.build(for: custom, alloc: .init(query: ["4821337", "8812300_16", "x;y"], known: ["2"]))),
            ("poll-pbs-billing.sh", RemoteScript.build(for: pbs, alloc: .init())),
            ("pbs-queues.sh", RemoteScript.pbsQueuesScript(host: pbs)),
        ]
    }
}
