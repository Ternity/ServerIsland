# 远端脚本协议

所有远端操作都是：`ssh <alias> <commands.shell>`（默认 `bash -l -s`），把脚本从 stdin 送过去。
`SSHRunner.run` 会自动在脚本最前面加上 **`pathPrelude`**（`importShellPath` 关闭时不加）。

## PATH 补全（`SSHRunner.pathPrelude`）

- **背景**：Ubuntu 默认的 `~/.bashrc` 开头写着"非交互 shell 直接 return"，非交互登录读不到后面加的 PATH。比如有的单机 Slurm 工作站，Slurm 装在 `~/software/slurm/install/bin`，就是在这一行之后加进 PATH 的。
- **做法**：用 grep 找出 `~/.bashrc`、`~/.bash_profile`、`~/.profile`、`~/.zshrc` 里的 `PATH=` 行，展开 `~`、`$HOME` 和 `${HOME}`，跳过含其它变量的条目，把实际存在的目录**追加**到 PATH 末尾（只追加，不覆盖）。另外固定追加 `/opt/slurm/bin` 和 `/usr/local/slurm/bin`。
- 这个过程不启动交互式 shell：交互式 shell 会触发 conda init、欢迎信息等，可能卡住，也会污染输出。
- 用户还可以在"连接后先执行"（`commands.preamble`）里写 `source …` 或 `module load …`。各脚本生成函数会把 preamble 放在脚本最前面。

## 输出标记

```
@@SI:BEGIN
@@SI:SEC <名字>      一段开始
...                 该段命令的输出（stderr 也并进来）
@@SI:RC <退出码>     一段结束
@@SI:END
```
段内还可能出现这些特殊行：

| 标记 | 含义 |
|---|---|
| `@@SI:NA` | 命令不存在（例如没有 nvidia-smi） |
| `@@SI:FILE <路径>` | 进度文件的开头 |
| `@@SI:DIR <任务ID>\|<目录>` | 计算软件识别的一个条目 |
| `@@SI:PATH <路径>` | 日志文件路径 |
| `@@SI:NOFILE <目标>` | 输出不是普通文件（tty、socket、pipe） |
| `@@SI:UNREADABLE` | 日志文件读不到 |
| `@@SI:ABORT <原因>` | 取消前的核对没通过，没有执行 |
| `@@SI:DONE` | 取消命令已执行 |

`BEGIN` 之前的内容（比如 `.bashrc` 打印的欢迎信息）一律忽略。退出码不为 0 的段会变成卡片上的一条 warning。

## 轮询脚本（`RemoteScript.build`）的各段

**同一份数据一个周期只取一次**（2026-10-19）：各段都在同一个 shell 里执行，前面取到的东西存进 shell 变量，后面的段直接用。
- `SI_JW`（Slurm）：默认的 squeue 命令换成 `RemoteScript.squeueWithWorkdir`，在原来的列后面接上 `|@@SI:WD|%T|%Z`。`squeue` 段去掉这一截输出（和原来的格式完全一样），`SI_JW` 留下 `作业号|状态|工作目录`，给 `workdirs` 和计算软件识别用。**调度系统每个周期只查一次**（以前是 3 次：列表、工作目录、运行中作业的目录）。用户自定义了 squeue 命令时不动它，`workdirs` 段另外查一次 `%i|%T|%Z`。
- `SI_PQ`（PBS）：`qstat` 段把 `qstat -f` 全文存下来，`workdirs`（`si_pbs d`）和计算软件识别（`si_pbs w`）都从它转换（以前 qselect + qstat -f 跑 3 遍）。
- `SI_PT`：`RemoteScript.processScan` 把自己的进程遍历一遍，每行 `PID|所属作业号|当前目录`（作业号只在调度系统的机器上读 environ；只为计算软件识别时只留属于作业的进程）。`slurmpids`、`workdirs`、计算软件识别都用它（以前遍历 2～3 遍，每个进程都要起 tr / awk / readlink）。这一步不在任何段里，报错丢掉（以前读不了的 environ 会把 Permission denied 漏进 slurmpids 段）。
- `SI_GA`：`gpuapps` 段的 nvidia-smi 输出，计算软件识别直接取里面的 PID（以前再调用一次 nvidia-smi）。
- `si_rc 码`：让段的退出码等于被存进变量的那条命令的退出码。
- **磁盘**（`disk` 段）：引擎隔 `MonitorEngine.diskInterval`（300 秒）才带上这一段，中间沿用上次的结果；手动刷新、改设置时总是查。
- **计算软件识别跳过没变的目录**：见下文"文件没变时不重读"。

| 段 | 条件 | 命令 |
|---|---|---|
| `cpu` | showCPU | `head -n1 /proc/stat; nproc; cat /proc/loadavg` |
| `mem` | showMemory | `/proc/meminfo` 里的 MemTotal 和 MemAvailable |
| `gpu`、`gpuapps` | showGPU，或者需要列进程 | nvidia-smi（没有就输出 NA） |
| `squeue` | Slurm 且 watchJobs | `squeue -u $(id -un) -h -o '%i\|%j\|%T\|%M\|%l\|%D\|%R\|%P\|%F\|%K'` |
| `qstat` | PBS 且 watchJobs | `commands.qstat`（默认 `qselect -u` + `qstat -f -t`）的输出经 `si_pbs q` 转换成和 squeue 一样的行（见下文 PBS） |
| `alloc` | 打开计费、这台机器填了单价（自定义 squeue 时还要有没查过资源的运行中作业） | Slurm：默认 squeue 时从这一轮的 `SI_JW` 里取 RUNNING 的作业、跳过 `SI_K` 里已经知道资源的（新作业同一轮就查到）；自定义 squeue 时查上一轮看到的。每个作业一次 `scontrol show job -o`，输出 `作业号\|AllocTRES（老版本叫 TRES）\|TresPerNode\|TresPerJob\|NumNodes`，一轮最多 20 个；PBS：`si_pbs t` 从 `SI_PQ` 里取，不再查询（见下文计费） |
| `workdirs` | watchJobs（2026-10-18 起总是有：工作目录写进历史记录） | `@@SI:HOME 家目录`，再加 `id\|工作目录`：Slurm 用 `squeue -o '%i\|%Z'`，PBS 用 `si_pbs d`，进程用 `readlink /proc/PID/cwd` |
| `procs` | 直连；或 Slurm / PBS 且 slurmAlsoProcesses | `ps -u … -o pid=,etimes=,pcpu=,rss=,args= --sort=-pcpu \| head -300` |
| `slurmpids` | Slurm / PBS 且 slurmAlsoProcesses | 属于作业的进程（`SI_PT` 里作业号不为空的；作业号用 `si_jobid` 判断：`SLURM_JOB_ID` / `SLURM_ARRAY_*` / `PBS_JOBID`），这些进程不再单独列出 |
| `apps` | detectApps 且 watchJobs | 计算软件识别（见下文） |
| `watch` | 直连且有监听的 PID | `ps -p <pids>`（不加 `-u`） |
| `disk` | showDisk，且距上次查磁盘已满 5 分钟（或手动刷新） | `df -P -k ~` |
| `progress` | readProgressFiles | 逐个 `cat` 进度文件 `~/.serverisland/progress/*.json`（每个最多 4KB） |

squeue 的解析是**从两端按位置取列**，所以作业名里有 `|` 也能正确解析；也兼容没有 `%F|%K` 两列的老格式。数组作业从 `%F`/`%K` 识别，没有这两列时从编号 `123_5` 或 `123_[7-99%10]` 里拆出来。排队的一整段元素数量以编号里的 `_[...]` 为准（`%K` 在有的 Slurm 版本里不带方括号）。

## PBS（PBS Pro / OpenPBS / Torque，2026-10-18）

没有真实的 PBS 集群测试过，按文档和公开的 `qstat -f` 输出格式实现，自检用 PBS Pro 和 Torque 的样例文本跑真正的 awk。

- 思路：**在服务器上把 `qstat -f` 转换成 squeue 的格式**（`RemoteScript.pbsFunction` 里的 `si_pbs`），之后的解析、数组合并、排队原因、提醒、详情全部沿用 Slurm 的代码。作业的 `source` 仍是 `.slurm`（含义是"调度系统的作业"），用到命令的地方按 `host.kind` 分支。
- 列表：`qselect -u 用户` 拿到自己的作业号（qstat -u 的表格会截断作业号），再 `qstat -f -t`（`-t` 展开数组子作业）。没有作业时不调用 qstat（否则会列出所有人的作业）。
- 作业号统一成 Slurm 的写法：`1234.server` → `1234`；数组子作业 `1234[5].server` → `1234_5`；数组本身 `1234[]` 跳过。发回服务器时 `pbsID` 换回 `1234[5]` 并加单引号（方括号是 shell 通配符）。
- 状态：R/B 运行，E 收尾，Q/H/W/T 排队（H 显示 JobHeldUser，W 显示 BeginTime，Q 的原因来自 `comment`），S/U 挂起；F/X/C 已结束（列表里不显示）。
- 结束状态（代替 sacct）：`qstat -x -f -t 作业号`（PBS Pro / OpenPBS 的历史记录），失败时退回 `qstat -f -t`（Torque 会把结束的作业以 C 状态保留一段时间），`si_pbs a` 输出 `作业号|名称|状态|用时|退出码`。退出码：0 完成，-29（PBS Pro）/ -11（Torque）超时，≥256 被信号终止（qdel 一般是 271），其它失败。查不到时和"没开 sacct"一样处理。
- 字段：`Job_Name`、`job_state`、`resources_used.walltime`、`Resource_List.walltime`、`Resource_List.nodect`（Torque 用 `Resource_List.nodes` 开头的数字）、`exec_host`（节点去重）、`queue`、`comment`、`Exit_status` / `exit_status`、`Variable_List` 里的 `PBS_O_WORKDIR`。续行以 Tab 开头，接到上一个值后面。
- 详情：`qstat -x -f`（或 `qstat -f`）全文按 key = value 显示；日志读 `Output_Path` / `Error_Path`（去掉 `主机名:`）。PBS 常在作业结束后才把输出拷回来，运行中读不到时会提示。
- 取消：先 `qstat -f` 核对名称、`Job_Owner`（@ 之前）和状态，再 `qdel`。
- 计算软件识别：运行中作业的工作目录用 `si_pbs w`；进程通过 `PBS_JOBID` 映射回作业号。
- 不支持：分区页（卡片上不显示这个按钮）、24 小时历史（只用本机记录）。
- awk 只用 POSIX 写法（`delete 数组` 除外，gawk / mawk / BSD awk 都支持）；Mac 上用 BSD awk 跑自检，Linux CI 上是 mawk。

## 计费（`Models/Billing.swift`，2026-11-05）

估算费用 = 分到的资源 × 运行时长 × 用户填的单价。默认关闭；关闭时远端脚本和以前完全一样。
- **运行中的作业**：`alloc` 段（见上表）。Slurm 每个作业只查一次（引擎的 `allocCache` 记住结果，作业离开列表后清掉），新版 `scontrol show job` 的字段是 `AllocTRES=`，老版本是 `TRES=`，两个都认。PBS 的资源在 `qstat -f` 全文里本来就有，`si_pbs t` 每轮直接转换，不多查。
- **结束的作业**：默认 sacct 命令末尾加了 `AllocTRES`（旧的默认命令自动升级；用户改过的不动），`si_pbs a` 末尾也加了一列资源。`Parsers.sacctAllocs` 只认最后一列里带 `=` 的；原来的 `Parsers.sacct` 不受影响。结束时优先用这里的资源和时长，查不到时用运行时记下的（ServerIsland 退出期间结束的作业，用 `active-jobs.json` 里存的分区和资源补记）。
- **没统计 gres/gpu 的 Slurm**：`AccountingStorageTRES` 里没有 `gres/gpu` 时，作业分到了卡，AllocTRES（和 sacct）里却没有。这时按申请的卡数算：`TresPerJob`（`--gpus=4`）优先，否则 `TresPerNode`（`--gres=gpu:1`，如 `gres/gpu:a100:2`、老版本 `gres:gpu:1`）× 节点数。GPU 是整张分配的，申请几张就占几张。结束时 sacct 里没有卡、运行时看到过卡，卡数用运行时的（`JobAlloc.best`）。`--gpus-per-task` 暂不处理。
- **TRES 解析**（`JobAlloc.parse`）：`cpu=`、`node=`；加速卡认 `gres/gpu|dcu|npu|mlu`，同一种卡既有总数（`gres/gpu=4`）又有分型号（`gres/gpu:a100=4`）时只用总数，只有分型号时相加；写了 CPU 却没有卡就是 0 张卡。
- **PBS 的资源**：PBS Pro / OpenPBS 用 `Resource_List.ncpus` / `ngpus`；Torque 从 `Resource_List.nodes=2:ppn=16:gpus=2`（多段用 `+` 连接）里乘出来；节点数同列表里的 nodect。写成 `cpu=32,node=2,gres/gpu=4`，和 Slurm 共用解析。
- **分区**：Slurm 是 squeue 的 `%P`，PBS 是队列；节点数是 `%D`。单价先找同名分区，再用"其他分区"（分区名为空的一行）；单价为 0 不计费。计价方式：按卡时 / 核时 / 节点时（节点数没写时用 `%D`）。
- **设置页读取分区**：Slurm 复用分区页的 `Partitions.script`（sinfo），PBS 用 `qstat -Q`（`pbsQueuesScript`）。只读。
- **账本**：`cost-ledger.json`（保留 400 天），每条记下当时的单价（以后改单价不影响旧账）；数组按元素记，按主编号汇总；同一个编号只记一次。没开计费、没有单价或不知道资源量时不记。
- 2026-11-05 在真实服务器上只读核对过（`--diagnose` 末尾的"计费"一段）：一台 Slurm 26 集群的 sacct AllocTRES 形如 `billing=150,cpu=8,gres/gpu=1,mem=15G,node=1`；一台单机 Slurm 工作站的 scontrol 只有 `AllocTRES=`（没有 `TRES=`），而且它的 `AccountingStorageTRES` 没有 `gres/gpu`，申请了 `--gres=gpu:1` 的作业 AllocTRES 里也没有卡；按申请数兜底后算 1 卡（同日复查过）。

## 目录过滤（`HostConfig.pathFilter`，2026-10-18）

- 每行一个目录（逗号、分号也行），`~` 按服务器上的家目录展开；目录本身和子目录都算，`/a/LLZO` 不会匹配 `/a/LLZO2`。
- `workdirs` 段现在总是有（历史记录要用目录），只有设置了"只看这些目录"才按它过滤。作业按调度系统记录的工作目录（Slurm `%Z`、PBS `PBS_O_WORKDIR`），进程按当前目录。
- 引擎按任务号缓存目录（`workdirCache`）：某次没查到时沿用上次的，避免任务忽隐忽现、误发"已结束"。查不到目录的任务不显示；手动监听的进程和进度文件不受过滤影响。

其它脚本：
- `sacctScript`：`{jobs}` 替换成作业号列表（PBS 见上文）；
- `detailScript`、`cancelScript`：作业号经过 `safeSlurmID`（只允许 `^\d+(_\d+)?$`），PID 必须是纯数字；
- `pbsQueuesScript`：`qstat -Q`（计费设置页读取 PBS 队列）；
- `historyScript`、`topProcessesScript`、`detectScript`（另外检测本机是否在运行 slurmd；没有 squeue 但有 qstat + qselect 时判定为 PBS）；
- `Partitions.script`：
  - `sinfo -h -o '%P|%a|%l'`：分区汇总；
  - `sinfo -N -h -O 'NodeList,Partition,StateCompact,CPUsState,Gres,GresUsed'`：逐个节点，按空白切分。
  - Gres / GresUsed 里的加速卡认 `gpu`、`dcu`（曙光 / 海光，如 `dcu:Hygon:4(S:0-3)`）、`npu`、`mlu`，空闲 = 总数 − 已用；数量为 0 的（`dcu:0,gpu:0`）不算一种卡。界面上的名字跟着种类走（GPU / DCU，混在一起是 DCU/GPU）。

## 计算软件识别（`Monitor/AppDetectors.swift`）

参考的是 GPUMDkit Web Console（GPUMDkit 仓库里 `Scripts/utils/server/server.py` 的 `training_status` 和 `simulation_status`），但不做实时刷新，跟着普通轮询走。

1. **候选目录**：
   - Slurm：`SI_JW` 里状态是 RUNNING 的作业的提交目录（PBS 是 `SI_PQ` 经 `si_pbs w`）；
   - 本机进程，满足任一条件：进程名匹配 `pgrep -x '<appProcessNames>'`（默认 `gpumd|nep|gnep`）、在 nvidia-smi 的计算进程里（`SI_GA`）、CPU ≥ 50%、或者（Slurm 机器上）属于 Slurm 作业。目录取自 `SI_PT`（`readlink /proc/<pid>/cwd`）。不列出普通进程的集群上只看属于作业的进程。直连服务器上不读作业号（以前带 SLURM_JOB_ID 的进程会被记到一个列表里没有的作业号上，进度显示不出来）；
   - 进程属于哪个 Slurm 作业：用 shell 函数 `si_jobid` 读 `/proc/<pid>/environ` 里的 `SLURM_JOB_ID`（数组元素则用 `SLURM_ARRAY_JOB_ID_SLURM_ARRAY_TASK_ID`）。**不要用 cgroup 判断**：有些 Slurm 安装不会把 job_N 写进 cgroup。
   - 用 `awk '!seen[$2]++'` 按目录去重，每个目录只算一个任务。
2. **由目录里的文件决定软件类型**（`dirBody`）：
   - **TorchNEP**（排在 NEP 前面）：`si_tnep` 在目录本身和一层子目录（默认 `output/`）里找第一行是 `# epoch  loss  rmse_e(...)` 表头的 `loss.out`，输出 `file=`（相对路径，曲线要用）、`hdr=`（表头，决定各列名字）、`last=`；目标优先取同目录 `output.log` 前 300 行里的 `Training: epochs 1..N`，其次 `nep.in` 的 `epoch`；`output.log` 末尾有 `Done. Best loss` 时 `done=1`（**只输出 done=1，不输出 done=0**：版本不同找不到这一行时，按 当前 ≥ 目标 判断，不误报失败）；
   - `loss.out` + `nep.in` → NEP：目标是 `generation`（没写时默认 100000，并标记 `hasTarget=false`）；当前代数和各项 loss 取自 loss.out 最后一行**不以 # 开头**的数据（新版 GPUMD 每次开始训练会先写 `# format_version` / `# output_interval` / `# columns` 表头）；列数决定列名（`AppDetectors.nepLossLabels`：10 普通、12 带 ediff、14/16 带电荷、6 偶极 / 极化率）；
   - `loss.out` + `gnep.in` → GNEP：目标是 `epoch`（默认 50）；
   - **MACE**：`logs/*.log`（去掉 `_debug.log`）里最新的一个，前 20 行有 `MACE version`。目标 = `logs/<名字>_debug.log` 里参数 Namespace 的 `max_num_epochs`；当前 = max(日志最后一行 `INFO: Epoch N:` 的 N+1，`results/<名字>_train.txt` 最后一行 `"epoch": K` 的 K)（epoch 从 0 数；日志每 eval_interval 个 epoch 才一行，results 每个 batch 一行）；误差按 `名字=数字 单位` 通用地取（error_table 不同名字不同）；`Training complete` → `done=1`（提前停止也算完成，同样只输出 done=1）。mtime 取日志和 results 里较新的；
   - （VASP、LAMMPS、自定义规则见下一节）
   - **DeePMD-kit**（2026-11-07）：`lcurve.out` 第一行是带 `step` 的 `#` 表头。当前步 = 最后一行不以 # 开头的数据的第一列（`--restart` 续跑时 lcurve.out 追加写入、表头再写一遍，步数接着数）。
     - 目标步数：先在目录里最新的 5 个 `log` / `*.log` / `*.out`（去掉 lcurve.out）的前 3MB 里找 `Start to train N steps`（PyTorch 后端打印，`num_epochs` 换算成步数后只在这里有）→ `logsteps=`；其次 `input.json` / `out.json` 的 `numb_steps` / `num_steps` / `stop_batch` → `steps=`。都没有就只显示当前步。当前步超过目标时不显示（按总原则）。
     - 日志：第一个"前 3MB 有 Start to train 或末尾 40 行有 DEEPMD"的候选文件；末尾有 `Trained model has been saved to`（PyTorch）或 `average training time`（TensorFlow）→ `done=1`（只输出 done=1）。
     - 列名由表头决定（`AppDetectors.deepmdLabels`）：`mae_e_val` → `MAE E val`，`rmse_f_trn` → `RMSE F trn`，`rmse_val` → `RMSE val`，多任务后缀保留；`nan`（没有验证集）不显示。排序：验证集在前，E → F → V → 总误差，lr 最后；任务行显示前两项。
     - 签名：lcurve.out 和这几个日志的 `%Y.%s`，没变就 `same=1`（日志要 grep，所以做这一步）。
     - 曲线：lcurve.out，E / F / V 各一条（有验证集用验证集，多任务取第一个，没有就用训练集并标"训练集"；三项都没有时画总误差），横轴是 step，过一遍 `monotonic`。
     - 实测（一台单卡工作站上 DeePMD v3.2.1 PyTorch 后端、num_epochs=100 → 3016700 步）：识别、目标、曲线增量读取都正常。
   - `run.in` + (`neighbor.out` | `thermo.out`) → GPUMD：目标是所有 `run N` 之和；当前步数优先取 `neighbor.out` 最后一行（`Neighbor info at step N: radial(max=,actual=), angular(...)`），否则用 `thermo.out` 的有效行数 × `dump_thermo`；
   - 同时输出 `mtime=$(stat -c %Y …)`，用来判断输出是否停止更新。
3. loss.out 的列：`gen Total L1 L2 Energy Force Virial [E-test F-test V-test]`（7 列及以上），或者 `epoch Loss Energy Force Virial`。test 列全为 0 时不显示。
4. 速度由引擎在两次采样之间计算（`applyAppRate`：指数平滑；数字倒退时重置）。

### VASP / LAMMPS / 自定义规则（2026-10-17）

**总原则（用户定的）**：任何一处和预期不一致（文件缺失、格式对不上、数字矛盾），就当作没识别到，**不输出、不显示进度**。远端只负责把原始信息吐出来，判断都在 Swift 里（`parseVasp` / `parseLammps` / `ProgressRule.extract`），返回 nil 就丢弃。

- **VASP**（`INCAR` + `OSZICAR`）：
  - INCAR 用 awk 取 NSW / IBRION（去掉 `!` `#` 注释，支持 `;` 分隔，大小写不敏感）；写法不是整数就放弃；
  - `tail -n 3000 OSZICAR`：离子步是"第 1 列整数且第 2 列以 F= / T= 开头"的行；电子步是 `DAV:` `RMM:` `CG :` 这类行的第一个数字；
  - NSW ≤ 0 → 单点，显示电子步；IBRION=0 → MD，离子步 / NSW（超过 NSW 就放弃）；IBRION=1/2/3 → 结构优化，**只显示离子步，没有百分比**；其它 IBRION（声子等）放弃；
  - 结束标记：`tail -n 400 OUTCAR` 里有 `General timing and accounting` → `done=1`。没有 OUTCAR 时不输出 done。
- **LAMMPS**（`log.lammps`，只看常见的 MD）：
  - `grep -n` 找最后一个 `run` 命令行，从那一行开始 `tail -n +L` 交给 awk；
  - 第一行必须是 `run <整数>`（不能带 upto / every；变量没展开就放弃）；之后找含 `Step` 列的 thermo 表头，数据行要求列数一致且全是数字；
  - 当前 = 最后一行 Step − 这段 run 第一行 Step，目标 = run 的步数；
  - 遇到 minimize / rerun、多余的表头、`thermo_style multi` → `bad=1`；
  - 结束标记：`tail -n 30` 里有 `Total wall time` → `done=1`。
- **结束标记的作用**（`AppProgress.endMarker`）：有标记时 `finished` 以标记为准；`canJudge = endMarker != nil || hasTarget`，进程 / 作业结束时用它决定能否提醒"完成 / 提前结束"。
- **自定义规则**（`Models/ProgressRule.swift`，存在 `AppSettings.progressRules`，所有服务器通用，排在内置识别**之前**）：
  - 文件名只允许 `[A-Za-z0-9._-*?/+]`（会原样放进 shell 做通配，`si_pick` 取最新的匹配）；关键字、结束标记等一律 `shellQuote`；
  - 关键字模式：远端 `grep -aiF 关键字 | tail -n 5`；Swift 里用"关键字 + 可选的 :=# + 数字 + 可选的 /N 或 of N"匹配，不区分大小写；
  - 正则模式：远端 `tail -n 40`，Swift 里用 NSRegularExpression，第 1 组当前值，第 2 组目标值；
  - 目标值优先级：同一行里的 /N → 固定目标值 → 目标值文件里关键字后的数字；当前值超过目标值就放弃；
  - 规则匹配到了文件但取不到数字时，**不会再退回内置识别**（elif 链已经进了这个分支）。
- 设置页"测试"：`AppDetectors.ruleTestScript` 先报告目录 / 文件是否存在，再只跑这一条规则；`interpretTest` 给出结果或失败原因 + 文件最后几行。只读。
- 自检 `SelfTestApps` 在本机临时目录造假文件，用 `/bin/bash` 真正执行脚本；另外已在一台 Ubuntu 工作站上用 mawk 1.3.4 + GNU grep 3.11 验证过三段 awk（只通过 stdin 喂样例，没有写文件）。

### 详情页曲线（`Monitor/AppCurves.swift`，2026-10-17）

- NEP / GNEP / TorchNEP / MACE 的 loss；VASP 结构优化每个离子步的 E0 和**最大力**（两张图并排，2026-10-18）。**只在详情页打开时读**：第一次立即读，之后每 20 秒一次（日志末尾仍是 4 秒）。
- 增量：`MonitorEngine.curveCaches`（键是 主机 + 文件路径）记住已读行数和文件第一行。脚本把偏移量和第一行一起传过去，服务器上比较：第一行变了或文件变短 → `reset=1` 从头读；一次最多 5 万行。
- `head -n $((n-off))` 只取 `wc -l` 数到的完整行，正在写的半行下次再读。
- 输出：`n=`、`head=`、可选的 `reset=1`，再加上 `row=` 开头的数据行。loss.out 只保留第一列是整数的行；OSZICAR 只取"步号 + E0"两列。
- 最大力（2026-10-18 改）：和 VASP 判断 EDIFFG 力收敛时的口径一致。
  - OUTCAR 里有 **VTST** 补丁打印的 `FORCES: max atom, RMS  <max> <rms>` 时，直接用 `<max>`（输出 `row=1 值`），和用户 `grep RMS OUTCAR` 看到的一样。标准 VASP 没有这一行。
  - 没有时读 `TOTAL-FORCE` 块，每个原子 |F| = √(Fx²+Fy²+Fz²)，**按 POSCAR 的 Selective dynamics 去掉固定（F）的方向**后取最大值（输出 `row=0 值`）。POSCAR 没有 Selective dynamics 时和以前一样。用户反馈表面体系"力不对"就是因为以前把固定的底层原子也算进去了，曲线降不下来。
  - 两种都有时曲线只用 VTST 的。`n=` 取最后一个读完的块**或 VTST 行**所在的行，所以块之后才写出来的 VTST 行不会被读两次。
  - 以下是原来的说明：awk 只在读到块的第二条虚线时输出这一步，并用 `n=` 报告最后一个完整块所在的行，正在写的半个块下次重读；OUTCAR 很长，所以不限 5 万行。用对数坐标。
  - 收敛判据（2026-10-18）：**EDIFFG 先从 OUTCAR 读**（`EDIFFG = …` 那一行，是 VASP 实际用的值，包括 INCAR 没写时的默认值 10×EDIFF），读不到再看 INCAR。`EDIFFG < 0` 按力收敛：画虚线目标，右上角写"按力收敛 < x eV/Å"；`EDIFFG ≥ 0` 按能量收敛：不画目标线，右上角写"按能量收敛 ΔE < x eV"。
  - 共轭梯度（IBRION=2）有试探步，最大力会上下跳，所以再画一条淡色虚线"最低"（到目前为止的最小值）。
  - 用户反馈"力不对"时在一个真实的 CG 分子优化上核对过：35 个离子步的最大力和独立的 Python 计算完全一致；当时的困惑来自 CG 的跳动，以及 EDIFFG 默认按能量收敛（停下时最大力还有 0.15 eV/Å）。C# 版当时还有一个显示问题：图例太宽时文字叠在一起，已改成图例放不下就裁掉、判据放到图的右上角。
- **NEB**（2026-10-18）：识别条件是目录里有 INCAR（`IMAGES = N`，N ≥ 1）和 `01/OSZICAR`。卡片标题"VASP NEB"，离子步取 01 的 OSZICAR，摘要加"能垒 x eV · 最大力 y"。
  - 进度脚本对每个 `[0-9][0-9]` 子目录读 OUTCAR 末尾，输出 `nebimg=像点|energy(sigma->0)|VTST 最大力`；端点（00 和 N+1）的 OUTCAR 是初态 / 末态优化拷过来的，没有力也没关系。
  - 能量用 `energy(sigma->0)`（= OSZICAR 的 E0），和 VTST 的 `nebef.pl` 一样；能垒 = 最高的像点 − 00，逆向能垒 = 最高的像点 − 末态。在一个真实的 3 像点 NEB 上和 nebef.pl 的输出逐项一致。
  - 详情页两张图：左边"能垒图"——曲线是每个像点的相对能量（带圆点），柱子是每个中间像点的最大力（自己的纵轴，只占下半部分），琥珀色虚线是 |EDIFFG|（标"EDIFFG"，只在 EDIFFG < 0 时画），柱子高于虚线时变成琥珀色；右边"能垒"——每个离子步的能垒变化（从各像点 OSZICAR 的 E0 算），右上角写当前能垒和逆向能垒。
  - 曲线脚本 `curve-neb.sh`：`row=像点 能量 力`，以及每一步的能垒；不做增量（文件少、行数少），每次都重读。
- **NEP 横轴（2026-10-21）**：loss.out 是**追加写入**的（GPUMD `fopen("loss.out", "a")`），重新开始 / 续跑时代数从 output_interval 重新数起，所以横轴**不用第一列**，而是按行累加：每行加上当前的间隔（`nep.in` 的 `output_interval`，默认 100；新版 GPUMD 每段开头的 `# output_interval N` 改变之后的间隔，曲线脚本把它输出成 `row=-1 N`）。以前直接用第一列，续跑后曲线会折回去。进度条仍用最后一行的代数（续跑时 nep.in 的 generation 是这一次要跑的代数）。
- TorchNEP 用 loss.out 第一列的 epoch，MACE 用日志里 `Epoch N:` 的验证集误差（awk 输出 `keys=误差名字` 和 `epoch 数字…`）；两者都过一遍 `AppCurves.monotonic`：epoch 倒退（从较早的检查点续跑、文件是追加的）时丢掉之前跑过又重跑的那一段。GNEP 维持原样（用第一列）。
- 曲线脚本最后的 `sed '/^keys=/!s/^/row=/'`：除了 keys= 行都加 row= 前缀。
- 画图：点数超过 400 时等间隔抽样；loss 用对数坐标。
- 实测：一台工作站上 579 行的 loss.out 第一次传 72 KB / 0.07 s，之后没有新行时只传 131 字节。

### 文件没变时不重读（2026-10-19）

- GPUMD、VASP、VASP NEB、LAMMPS 和自定义规则的分支，在读文件之前先算签名：`stat -c '%Y.%s'`（修改时间.大小）这种软件会读的全部文件，拼成一行（自定义规则用 `%n %Y %s`，带上 si_pick 选中的文件名）。输出 `sig=签名`。
- 引擎（`MonitorEngine.appCache`）按目录记住上一轮带签名的结果，下一轮把 `目录 → 签名` 传给 `AppDetectors.script`，生成 `si_same` 函数（`case` 比较，目录和签名都 shellQuote）。签名一样就只输出 `@@SI:DIR`、`kind=`、`same=1`，不再 tail / grep / awk；解析成 `AppProgress.unchanged`，`resolveApps` 换成上一轮的结果（修改时间也是上一轮的，所以"没有新输出"的判断不变）。
- 签名为空（stat 失败）时总是重读；自定义规则一改就清空缓存；任务结束时的最终状态（`finalStateScript`）和设置页的测试总是完整读取（`si_same` 恒为假）。
- NEP / GNEP 只读 `tail -n 1`，本来就很轻，不做这一步。
- 收益主要在文件长时间不更新的时候（VASP 一个离子步要很久、作业卡住、输出间隔比轮询间隔长）；NEB 每个像点都要 `tail -n 25000 OUTCAR`，省得最多。

### 新增一种软件（比如 VASP、LAMMPS）
1. 在 `AppProgress.Kind` 里加一个 case，补上 `title`、`unit`、`summary`；
2. 在 `AppDetectors.dirBody` 里加一个 `elif` 分支：用特征文件判断，输出 `@@SI:DIR`、`kind=…`、`total=…`、`last=…`、`mtime=…` 等 key=value；要读大文件的话照着 VASP 分支加上 `sg=…` / `si_same` / `sig=`（签名要覆盖这个分支读的所有文件）；
3. 在 `AppDetectors.parse` 的 `switch kind` 里解析这些字段；
4. 在 `SelfTest` 里加一条样例输出的解析测试，再用 `--final-state <alias> <目录>` 在真实目录上验证。
5. **只读文件末尾或小文件**（tail、awk、grep -c），不要整读大文件。
