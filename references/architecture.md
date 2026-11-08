# 架构

## 总体数据流

```
ConfigStore (config.json)
     │  settings.hosts[...]
     ▼
MonitorEngine ── 每台主机一个 Task 循环 ──► SSHRunner.run(host, script)
     │                                          │  /usr/bin/ssh + ControlMaster
     │                                          ▼
     │                                    远端 bash（stdin 读脚本）
     │                                          │  @@SI: 分段输出
     │◄──────── RemoteScript.parse → build() ◄──┘
     │  snapshots[hostID]: HostSnapshot（指标、GPU、磁盘、jobs）
     │  detectEvents() → IslandEvent → emit（弹窗 / 提示音 / 系统通知）
     ▼
SwiftUI（IslandRootView、SettingsView），通过 @ObservedObject 订阅 engine 和 store
```

- **启动**（`App/main.swift`）：先处理命令行开关（askpass、`--render-preview`、`--selftest`、`--diagnose`、`--detail`、`--partitions`、`--final-state`），然后才是普通 App 的启动流程。
- **`AppDelegate`**：
  - 创建 `ConfigStore`、`MonitorEngine`、`SettingsWindowController`、`IslandController`；
  - 菜单栏图标（`NSStatusItem`）；
  - 系统通知（`UNUserNotificationCenter`）。开启了提示音时，系统通知不再另外出声。

## 配置（`Models/Config.swift`）

- `AppSettings`：全局设置，包括 `hosts`、`defaultInterval`、`autoRefresh`、`collapsedStyle`、`islandAlerts`、`systemNotifications`、`soundEnabled`、`soundVolume`、`arrayFirstFailureAlert`、`diskWarnThreshold`、`language`。
- `HostConfig`：单台服务器。
  - 基本：`alias`（ssh 别名）、`displayName`、`kind`（slurm / direct）、`auth`（key / password / keyPassword / interactive）、`enabled`、`interval`（nil 表示跟随全局）。
  - 手动连接：`hostName`（非空表示手动填写，此时 `alias` 只用于显示）、`port`、`user`、`identityFile`；`jumpHostID`（另一台 ServerIsland 服务器做跳板机，可多级）；`rememberPassword`（密码存进系统钥匙串，默认关）。
  - `auth` 的原始值和旧版兼容：旧版的“密码 / 动态口令”就是 `interactive`（现在叫“动态口令”）。`password` / `keyPassword` 才能记住密码。
  - 显示：`showCPU/Memory/GPU/Disk`、`watchJobs`、`readProgressFiles`、`processFilter/processExclude`、`watched`（监听的 PID）。
  - 提醒与操作：`muted`、`allowCancel`、`paused`。
  - 连接：`keepAlive`、`reuseConnection`、`importShellPath`、`commands`（`CommandSet`：shell、preamble、squeue、sacct、gpu、gpuApps、processes、progressGlob、disk）。
  - 计算软件：`detectApps`、`appProcessNames`、`slurmAlsoProcesses`（单机 Slurm / PBS 时同时显示直接运行的进程；默认关，"自动检测类型"也不会打开它，只在提示里说一声——有调度系统时默认只看作业）。
- **每个 `init(from:)` 都用 `decodeIfPresent ?? 默认值`**，旧配置文件可以平滑升级。新增字段时照这个写法来。
  - 特例：旧版的默认 squeue 格式没有 `%F|%K` 两列，读取时会自动升级。
- `ConfigStore`：`settings` 改动后 0.5 秒防抖保存；同时更新 `Lang.setting`。

## 设置的导入导出（`Models/ConfigTransfer.swift`，C# 版 `ConfigTransfer.cs`）

- 导出就是把当前设置按 config.json 的格式写到用户选的文件（`ServerIsland-settings-<日期>.json`）；两个版本格式相同，可以互相导入。密码在钥匙串 / 凭据库里，不导出。
- 导入要求顶层有 `hosts`（否则随便一个 JSON 都能解成默认设置）。两种方式：**替换全部设置**（先把当前设置备份成配置目录里的 `config.before-import-<时间>.json`；保留本机自己的东西：显示器，C# 版还有显示方式、SSH 实现、小章鱼位置、提醒过的版本）和**只添加新的服务器**（按 ID 或连接目标 别名 + 地址 + 用户 + 端口 去重）。两种都会去掉指向不存在服务器的跳板机和关注。

## 轮询引擎（`Monitor/MonitorEngine.swift`）

- `sync()`：1.2 秒防抖，随后进入 `syncNow()`，比较每台主机的新旧配置。
  - 只改了 `displayName / muted / allowCancel` 时，不重连；
  - 只改了 `paused` 时：暂停就停止循环，恢复就立刻刷一次；
  - 全局 `autoRefresh` 或 `defaultInterval` 变了：重新安排所有主机；
  - 其余改动：`restart(host, pollNow: true)`。
- `restart(host, pollNow:)`：取消旧循环。`autoPolls(host)`（全局自动刷新开启且未暂停）为 false 时，只刷一次（如果 `pollNow`）就停。
  - 首次启动时，暂停的主机**完全不连接**。
- 循环里：
  - 睡眠标记（`isAsleep`）最多保持 2 分钟，作为错过唤醒通知时的保险；
  - 失败退避：`base × 2^(n-1)`，最多 15 分钟。
- `pollOnce`：`RemoteScript.build`（带上 `appCache` 里的文件签名；距上次查磁盘满 `diskInterval` 才带磁盘段）→ `SSHRunner.run` → `classify`（ssh 本身的错误：超时、拒绝、需要登录…）→ `RemoteScript.parse` → `build()` → 写入快照 → `detectEvents` → `initializeArrays`。
- `build()` 的顺序：
  1. CPU（两次 `/proc/stat` 采样做差）、内存、GPU、磁盘（这一轮没查时沿用上次）；
  2. 任务：Slurm 用 squeue；直连用 ps（加上计算软件识别到的 pid）；Slurm 且 `slurmAlsoProcesses` 时，再加上不属于任何作业的进程；
  3. 手动监听的 PID：用启动时间（`startedAt` 与 etimes 推算值相差 30 秒以内）识别 PID 被复用的情况；
  4. 计算软件进度合并到对应任务；用两次采样估算速度和剩余时间；输出长时间（≥ max(10 分钟, 5 × 轮询间隔)）不更新时标出"没有新输出"；
  5. 进度文件合并（`~/.serverisland/progress/*.json`）；
  6. Slurm 数组作业合并成一行（`Parsers.groupArrays`），再用 `arrayTracks` 补上已完成和失败的计数。

## 事件识别（`detectEvents`）

第一次成功采样后才开始判断事件。比较上一次和这一次的快照：
- **普通 Slurm 作业消失**：用 sacct 查最终状态。如果查到的状态还是 RUNNING / COMPLETING，20 秒后再查一次。
  - 查不到 sacct，但识别到了计算软件：重新读一次输出文件的最终状态（`AppDetectors.finalStateScript`），据此判断"完成"还是"提前结束"；
  - 既没有 sacct、也没有计算软件信息：发 `info`"已离开队列"，**不报成功**。
- **数组作业**：
  - 排队范围的编号变化（如 `123_[7-99]` 变成 `123_[8-99]`）不算消失；
  - 运行中的元素消失：用一次 sacct 计入成功或失败；
  - 第一次出现失败元素时可提醒一次（`arrayFirstFailureAlert`）；
  - 整个数组都消失后，汇总提醒一次。
- **直连进程消失**：运行 ≥ 30 秒、或者是手动监听的，才算数。有计算软件信息的，先重读最终状态：到达目标是"完成"，否则是"提前结束"（failed）。
- 排队变运行：发 `started`（不响提示音）。
- 磁盘超过阈值时提醒一次，回落 3% 后才会重新提醒。
- 同一次轮询里超过 2 个事件时合并成一条（`flush`）。合并前先按提醒设置过滤（`alertable` / `AppSettings.allowsAlert`）：任务事件带 `jobID`（数组是主编号），按种类开关（`alertStarted / alertFinished / alertFailed / alertUnknown`，对应 started / finished / failed / info）和范围（`alertScope`：all / starred）判断；没有 `jobID` 的服务器级提醒（磁盘）总是放行；手动监听的进程在"只提醒关注的任务"时也放行。被过滤的事件写进 DebugLog。
- **关注**（`AppSettings.starredJobs`：主机 ID + 任务号，数组按主编号）：存在全局设置里，改动不会触发重连。`ingest` 在事件识别之后、且任务列表读全了（`listed`）时，把这台服务器上已经不在列表里的关注去掉（`pruneStarred`），所以关注的任务结束时的提醒照常判断，之后自动取消关注；squeue 出错的那一轮不动。显示时关注的任务排在前面（`starredFirst`，只在界面上排，不改快照顺序）。
- `emit`：静音的主机直接丢弃；之后依次是系统通知、提示音（节流 1.2 秒）、刘海弹窗队列（每条 5 秒）。
- **勿扰时段**（`quietHours`、`quietStart` / `quietEnd`：一天里的分钟数，起点晚于终点表示跨午夜，相等时不算；`AppSettings.isQuiet`）：`emit` 照常发系统通知（不出声，AppDelegate / App.cs 判断），之后直接返回——不响铃、不进刘海弹窗队列；历史记录和日志照常。展开面板头部显示一个月亮图标。
- 每个事件都写入 `DebugLog`（`~/Library/Logs/ServerIsland/serverisland.log`），用于排查"为什么没提醒"。

## 按需操作（`MonitorEngine+Actions.swift`）

这些都只在用户点开时才查，不参与轮询：
- `fetchDetail`：Slurm 用 `scontrol show job -o`，查不到时改用 sacct；进程读 `/proc/<pid>/{cmdline,cwd,fd/1,fd/2}`。日志取末尾 40 行，详情页打开期间每 4 秒刷新。
- `cancel`：执行前在服务器上再次核对，对不上就不执行，返回 ABORT：
  - Slurm：作业名一致、属于自己；
  - 进程：属于自己，且已运行时长与预期相差 20 秒以内。
- `fetchHistory`：本机的 `HistoryStore`（`history.json`，保留 7 天、每台最多 500 条，带工作目录）为主；Slurm 再用 `sacct -S now-24hours`（带 WorkDir 列，老版本不认识时去掉重查）补上最近 24 小时，同一作业以 sacct 为准、目录取有的那边（`mergeHistory`）。sacct 失败一次就记入 `sacctUnsupported`。
- `active-jobs.json`：每台服务器上次看到的运行 / 排队任务（编号、名称、目录、看到的时间），任务列表读全了（squeue / 进程列表的命令都成功）才更新。启动后第一次连上时，不在了的任务补记到历史（Slurm 用 sacct 查结果，查不到记成"已结束"，结束时间按上次看到的时间；不弹提醒）。演示、自检、`--diagnose` 的引擎不读写这两个文件。
- `fetchPartitions`（`Partitions.swift`）：`sinfo` 按节点逐个汇总到分区，统计空闲加速卡（GPU / DCU 等）和 CPU；结果缓存 60 秒。
- `topProcesses`、`watch/unwatch`、`toggleMute`、`setPaused`、`setHostInterval`、`setGlobalInterval`、`setAutoRefresh`、`setKind`、`login`、`checkEnvironment`。

## 计费（`Models/Billing.swift`、`Monitor/MonitorEngine+Billing.swift`，2026-11-05）

- 设置：`AppSettings.billingEnabled`（默认关）、`currency`；每台服务器的 `HostConfig.prices`（`PartitionPrice`：分区、计价方式、单价、说明）。只算 Slurm / PBS（直连服务器没有调度系统的分配记录）。
- `billingActive(host)`：打开了计费、是调度系统、至少一个分区单价 > 0。不满足时 `allocQuery` 返回 nil，远端脚本不多任何东西。
- 轮询：`allocQuery` 给出已经知道资源的作业（`known`，默认 squeue 时脚本在同一轮里查新出现的运行中作业）和上一轮看到、还没查的作业（`query`，自定义 squeue 时用）→ `RemoteScript.build(alloc:)` 多一段 `alloc` → `build` 把结果合进 `allocCache` 并挂到 `RemoteJob.alloc` 上；任务列表读全后清掉已经不在的作业。`RemoteJob` 还多了 `partition`、`nodes`（来自 squeue），`HistoryStore.ActiveJob` 一起存下来。
- 记账：`recordCost` 在作业结束的地方调用（`detectEvents` 的 Slurm / PBS 作业、`handleArrays` 的每个元素、`recordEndedWhileAway` 的非数组作业），资源和时长优先用 sacct / `qstat -x` 的最终值。账本 `CostLedger`（`cost-ledger.json`）。
- 汇总：`costEntriesWithRunning` = 账本 + 正在跑的任务"到上次刷新为止"的费用（不写进账本），所以今天 / 本月 / 每天跟着每次刷新更新；`CostSummary` 按每条记录实际运行的时间段摊到各天各月（跨零点的任务两天各算一部分）。
- 显示：`liveCost`（运行中：已花多少、每小时多少；数组 = 已结束元素的账 + 运行中元素）、`totalBurnRate`。刘海头部的钱币按钮（打开计费才有）→ 费用页（`CostSummary`：今天 / 本月 / 上月、本月每天、按服务器和分区、最贵的作业）；详情页多一行"费用"；历史页每行和标题显示费用。设置侧栏的"计费"页填单价、读取分区。
- 费用页底部和设置页都写明"估算，实际以服务器供应商的账单为准"（`Billing.disclaimer`）。
- 自检 `SelfTestBilling`；`--diagnose <别名> slurm` 末尾用临时单价只读地走一遍（不改设置、不写账本）。

## 刘海窗口（`Notch/IslandController.swift`）

- 一个固定大小的 `NSPanel`（`IslandLayout.panelSize`，比最大展开状态还大），放在 `.statusBar + 8` 层，`canJoinAllSpaces`。
- 用全局 + 本地两个 `mouseMoved` 监听做悬停判断：
  - 收起状态下，鼠标进入形状区域 0.14 秒后展开；
  - 展开状态下，鼠标离开形状 + 12pt 的范围后收起：普通页面延迟 0.25 秒，二级页面延迟 0.7 秒；
  - 固定（📌）时不收起；菜单打开期间（`NSMenu` 的 tracking 通知）也不收起。
- **点击穿透**：只有鼠标在形状内时 `ignoresMouseEvents = false`，透明区域的点击会落到下面的窗口。
- `canBecomeKey` 只在"全部任务"页返回 true（那里有搜索框），平时不抢键盘焦点。
- 收起时页面回到 `.list`，并取消固定。

## 检查更新（`Shared/UpdateChecker.swift`，C# 版 `UpdateChecker.cs`）

- `GET https://api.github.com/repos/zhyan0603/ServerIsland/releases/latest`（不含草稿和预发布），`tag_name` 去掉 v 后按数字逐段比较；安装包按后缀选（Mac `.dmg`、Windows `win-x64.zip`、Linux `linux-x64.tar.gz`），找不到就打开 Release 页面。更新说明取正文里的列表项（跳过 HTML 和标题），最多 4 行。
- 自动检查：`autoCheckUpdates`（默认开），启动 20 秒后一次、之后每 24 小时一次；同一个新版本只在刘海提醒一次（Mac 记在 UserDefaults 的 `notifiedUpdateVersion`，C# 记在设置的 `notifiedUpdateVersion`）。调试构建没有版本号，不检查。
- Release 的安装包文件名要保持 `ServerIsland-<版本>.dmg` / `-win-x64.zip` / `-linux-x64.tar.gz`。同时记下附件的 `digest`（GitHub 自动给出的 `sha256:…`）和 `size`。

## 自动更新（`Shared/UpdateInstaller.swift`，C# 版 `UpdateInstaller.cs`，2026-10-19）

点"更新到 v…"（设置 → 通用，或菜单栏 / 托盘菜单）后：下载 → 核对 SHA-256 → 替换 → 重新打开。没用 Sparkle：不需要额外的签名密钥、appcast 和发布步骤，Release 照旧上传三个附件就行；完整性由 GitHub 给的 SHA-256 保证（和用浏览器下载同样可信）。任何一步失败都不动旧程序，设置页显示原因并改为"下载 v…"按钮。

- **做不到时直接提示、改为下载**（`unsupportedReason`）：附件没有 digest；调试构建 / `dotnet run`；Mac 上 App 不在可写的文件夹里（在 DMG 里运行、被"App 转移"、没有权限）；Windows / Linux 上程序所在文件夹不可写；Mac 上的 C# 试用版。
- **Mac**：`URLSession` 下载到 `~/Library/Caches/ServerIsland/Update/` → 核对 SHA-256 → `hdiutil attach -nobrowse -readonly` → `ditto` 复制出 App → 检查包名、版本号（必须等于要装的版本）、`codesign --verify --deep --strict` → 去掉 `com.apple.quarantine`（所以更新后不会再被"仍要打开"拦一次）→ 卸载 DMG。然后写出 `swap.sh` 并启动它，App 退出；脚本等旧进程退出（最多 30 秒）→ 旧 App 改名为同一文件夹里的 `.ServerIsland-old-xxxx.app` → 新 App 移进来（失败就改回去）→ `open` 新版 → 删掉旧版和临时目录。
- **Windows / Linux**：`HttpClient` 下载到临时目录（`ServerIsland-update-<用户名>`，带进度）→ 核对 SHA-256 → 解压（zip / tar.gz）→ 检查里面有 `ServerIsland.exe` / `ServerIsland` → 把程序文件夹里同名的旧文件改名成 `.old`、新文件移进来（Windows 不让覆盖正在运行的 exe，但允许改名；中途失败就改回去）→ 启动新程序（`--after-update <旧 PID>`），旧程序退出。新程序先等旧进程退出再抢"只运行一个"的锁；启动时删掉 `.old` 和临时目录（`CleanupOld`）。Linux 上新程序补上可执行权限。
- **ad-hoc 签名的副作用（Mac）**：每次编译出来的签名都不同，更新后系统可能再问一次"是否允许访问钥匙串"（记住了密码的服务器），点"始终允许"即可。以后有了 Developer ID（方案 B）就不会了。
- 测试：两个版本都有 `--test-update <目录>`，在临时目录里用假的安装包走一遍（校验 / 版本号 / 包名不对时拒绝、替换正在运行的程序、等旧进程退出、清理）；Mac 版可以再加 `<真实的.dmg> <版本号>` 对正式安装包做一次核对和检查。CI（`cross.yml`）在 Windows 和 Linux 上都跑 `--test-update`。

## SSH 与登录（`SSH/`）

- 连接复用：`ControlPath=/tmp/serverisland-<uid>/%C`（unix socket 路径有 104 字节上限，所以放在短路径下）。
  - `key` 认证：`ControlMaster=auto`，`ControlPersist=15m`；
  - 其它认证（`password` / `keyPassword` / `interactive`）：`ControlMaster=no`，只复用登录时建立的主连接。
  - 手动填写 / 经跳板机的机器，socket 名前面加上自己 ID 的前 8 位（`ab12cd34-%C`）：不同跳板机后面可能是同一个内网地址，`%C` 只按地址、端口、用户算，会撞在一起。
- `BatchMode=yes`：后台绝不等待密码输入。
- **手动填写的服务器和跳板机（`SSHHosts.swift`）**：生成一份 App 私有的 ssh 配置 `/tmp/serverisland-<uid>/ssh_config`，只在这些机器上用 `ssh -F` 指定；普通别名机器不加 `-F`，行为和以前完全一样。
  - 手动填写的机器别名是 `si-<UUID>`，块里写 `HostName / User / Port / IdentityFile`（有私钥时加 `IdentitiesOnly yes`）；
  - 设了跳板机的机器在块里写 `ProxyCommand env SERVERISLAND_ASKPASS_HOSTID=<跳板机ID> /usr/bin/ssh -F <同一份配置> -o ControlMaster=no -o ControlPath=<跳板机的 socket> -W '[%h]:%p' <跳板机目标>`：
    内层 ssh 读的还是这份配置，所以多级跳板自然一跳一跳接下去；跳板机已经登录时 `-W` 直接走它的主连接，不用再输密码；`%%C` 留给内层 ssh 展开；
  - 别名机器设了跳板机时，块名就是别名本身，只加 ProxyCommand，其它设置仍来自 `~/.ssh/config`（ssh 取第一次出现的值，ProxyCommand / ProxyJump 也是先到先得）；
  - 配置末尾 `Match all` + `Include ~/.ssh/config` + `Include /etc/ssh/ssh_config`（`-F` 会跳过系统配置，所以要手动加回来）。**用户的 `~/.ssh/config` 一个字都不改**；
  - 设置一变就重写（内容相同不写）；每次用之前检查文件还在不在（/tmp 可能被清）。自检和预览渲染（`ConfigStore(persist: false)`）不写这个文件，免得覆盖正在运行的 App。
- **跳板机出错时的提示**：开着 `ControlPersist` 时，OpenSSH 把 ProxyCommand 的 stderr 丢到 /dev/null，跳板机出错什么提示都没有。`SSHRunner.run` 遇到"255 + stderr 为空 + 有跳板机"时，不复用连接再握手一次（`true`，不跑脚本）拿到真正的错误。`jumpProblem` 按错误里的 `user@地址:` 找出是哪一跳，提示"跳板机 X 需要登录 / 连接失败 / 连不上这台服务器"。
- 密钥被拒时（`keyFailureHint`）：`Permission denied (password).` 这类不含 `publickey` 的列表说明密钥已经通过（partial success），提示改成"密钥 + 密码"。
- 弹窗登录（`AskPass.swift`）：
  - 启动 `ssh -fN -o ControlMaster=yes`，环境变量设置 `SSH_ASKPASS=<App 自身可执行文件>`、`SSH_ASKPASS_REQUIRE=force`、`SERVERISLAND_ASKPASS=1`、`SERVERISLAND_ASKPASS_HOSTID`、`SERVERISLAND_ASKPASS_SESSION`（这次登录的临时目录）；
  - App 以 askpass 身份被调用时，按提示语分类（`AskPass.kind`：主机指纹 / 私钥口令 / 动态口令 / 密码 / 用户名），弹出 NSAlert，把输入写到 stdout。经跳板机时每一跳的 ssh 环境里是那一跳的 ID，弹窗写明是哪台机器；
  - **记住密码**（`Keychain.swift`，登录钥匙串里的通用密码，服务名 `ServerIsland`，账户是服务器 UUID）：只有 `password` / `keyPassword` 的密码提示可以记；有记住的密码就直接答，并在会话目录里留个 `used-<ID>` 标记——同一次登录里再被问，说明密码不对了，改为弹窗（带“记住密码”勾选框，勾选后写钥匙串，结果写 `remember-<ID>` 让 App 同步设置）；
  - **自动重新登录**：轮询发现 `needsLogin` 时，对记住了密码的机器（先远端的跳板机，再自己）在后台登录一次（`SERVERISLAND_ASKPASS_SILENT=1`：askpass 只用钥匙串、绝不弹窗）；同一台机器 10 分钟内最多一次，主连接还活着就不登录；
  - 后台主连接会继承 stderr，所以 stderr 写到临时文件而不是管道，否则永远读不到 EOF；
  - 保活参数：`ControlPersist` 来自 `keepAlive`，`ServerAliveInterval=30`，`ServerAliveCountMax=6`。
- 终端登录：生成一个 `.command` 脚本，用 `NSWorkspace.open` 打开（带上 `-F` 和私有目标）。
- `classify`：把 ssh 退出码 255 的 stderr 翻译成 `needsLogin` 或 `error`。
