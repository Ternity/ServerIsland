# 进度与待办

> 最近更新：2026-10-17。每次完成一轮工作后更新这一份。

## 版本

- App：0.2.0（`Resources/Info.plist` 和 `cross/Directory.Build.props` 一起改；2026-10-18 从 0.1.0 升上来，见 CHANGELOG.md）。GitHub 上最新的 Release 还是 v0.1.0，0.2.0 的三个安装包已打好但还没上传。
- git：https://github.com/zhyan0603/ServerIsland 。2026-10-17 重建了干净的历史（旧历史备份在仓库外的 `serverisland-history-backup-2026-10-17.bundle`），本仓库的提交作者用 GitHub 隐私邮箱。
- 安装包（2026-10-18）：`build/ServerIsland-0.2.0.dmg`（通用二进制，ad-hoc 签名）、`build/ServerIsland-0.2.0-win-x64.zip`、`build/ServerIsland-0.2.0-linux-x64.tar.gz`。
- 宣传视频：`promo/out/ServerIsland-v0.1.mp4`。

## 已完成（按轮次）

1. 骨架：SSH 轮询 + ControlMaster、Slurm 和直连两种类型、刘海 UI、设置页、logo、`build_app.sh`。
2. 修复演示数据泄漏和弹窗刷屏；进程排除规则；像素小章鱼 Tako 和 6 种心情；新 logo。
3. 任务详情和日志末尾、防误触的取消、监听 PID、磁盘提醒、24 小时历史、静音、提示音（成功和失败区分开）。
4. 修复"任务结束没提醒"（加了 DebugLog）；Job Array；动态口令弹窗登录和保活；固定面板；全部任务页；复制；自定义间隔；登录时启动；UI 美化。
5. 新 logo（像素字）；暂停单台服务器；刘海里的轮询菜单；只手动刷新。
6. NEP / GPUMD 识别；排队原因翻译；分区空闲资源；结束时重读最终状态。
7. PATH 补全和"环境"分组；单机 Slurm + 直接运行的进程；用 environ 映射作业；设置页按 ID 绑定；中英文；ⓘ 提示；修复标题栏重叠。
8. DMG 方案 A；宣传视频。
9. 本文档、AGENTS.md。
10. 每台服务器卡片标题显示自己的运行 / 排队数（`JobCountChips`）；修复"演示"徽章折行、演示数据里数组行没有英文。
11. 数量胶囊悬停说明"数组按元素算"；数组状态文字分段上色（运行蓝、排队灰、失败红，和进度条一致），进度条悬停显示图例；展开面板里合并空闲服务器（`foldIdleHosts`，默认开，全部空闲时不合并，收起面板后自动折回）。
12. VASP（MD 百分比；优化 / 单点只显示离子步 / 电子步）和 LAMMPS（最后一段 run）识别，对不上就不显示；自定义进度识别规则（设置 → 进度识别，可在真实目录上测试）；结束标记参与完成 / 提前结束判断；默认进程名加上 vasp_* 和 lmp*。自检增加到 59 项。
13. 详情页小曲线：NEP / GNEP loss（对数坐标，多列）和 VASP 结构优化离子步能量；增量读取，打开详情页时才读。新增 `--curve` 调试开关。自检 65 项。
14. 官网第一版（`site/`，GitHub Pages 工作流）；演示数据换成计算材料场景并去掉隐私信息；宣传视频同步修改并重新渲染；`--render-preview` 支持指定语言；`scripts/site_shots.sh`。
15. 改为开源（GPL-3.0，版权 Zihan Yan 和贡献者；README 重写成热门开源项目的样子，顶部是真实界面拼成的动图 `docs/demo-{en,zh}.gif`，由 `scripts/readme_assets.sh` 生成）：放弃收费方案；README 改成英文 + `README.zh-CN.md`，加 LICENSE、CONTRIBUTING；官网加 GitHub 链接和开源分区；私有文档挪到被忽略的 `references/private/`，公开文档里的真实主机名、路径都改成通用说法。
16. README 改成 GPUMDkit 的版式（logo + 链接行 + 徽章 + 简介）；原理图改成自己画的 SVG（中英文、深浅两版）；截图改成只有面板本身、透明背景（`SI_BARE=1 --render-preview`），整宽显示。
17. 准备发布 v0.1.0：设置 → 通用加了"点个 Star / 反馈问题"按钮；DMG 安装说明补上 macOS 15 的步骤；CHANGELOG.md。
18. Windows / Linux 版（`cross/`，当时在 avalonia 分支，现在是 dev，C# + Avalonia）：逐文件移植核心逻辑（远端脚本和 Swift 版逐字一致），虚拟刘海 + 桌面小章鱼 + 托盘，内置 SSH（SSH.NET，Windows 默认）；自检 71 项、`--render-preview`、`--ui-smoke`；CI 工作流 `cross.yml` 在 GitHub 的 Windows / Linux 机器上跑自检和界面走查并打包。用户已在 Windows 真机上试用，修了"桌宠切回灵动岛位置不对"。还可以打包成 `ServerIsland Preview.app` 在 Mac 上试用（`cross/scripts/build_mac_preview.sh`）。详见 `references/cross-platform.md`。
19. 连接方式（两个版本都做了）：认证方式分成"密钥 / 密码 / 密钥 + 密码 / 动态口令"（旧配置的 `interactive` 读成"动态口令"）；不写 `~/.ssh/config` 也能手动填地址、用户名、端口、私钥；跳板机（选另一台 ServerIsland 服务器，可多级，每一跳分别询问、分别报错，已登录的跳板机直接复用）；"记住密码"（默认关，只存系统钥匙串 / Windows 凭据管理器 / Linux Secret Service，断线后自动重新登录，10 分钟内最多一次）。系统 ssh 用 App 私有的 ssh 配置（`ssh -F`，不改 `~/.ssh/config`）。修了内置 SSH"只要密码"重试时丢掉密钥的问题。自检 Swift 88 项、C# 94 项；本机用户级 sshd 测过多级跳板、密钥通过后还要密码的提示、每一跳的报错归属和弹窗归属。

20. 2026-10-18（两个版本都做了）：PBS 集群（PBS Pro / OpenPBS / Torque，远端把 `qstat -f` 转换成 squeue 格式，复用 Slurm 的全部逻辑；qdel 取消、qstat -x 查结束状态、PBS_JOBID 映射进程）；每台服务器"只看这些目录"（按作业工作目录 / 进程当前目录过滤）；VASP 结构优化详情页加"最大力"曲线（OUTCAR 的 TOTAL-FORCE，虚线是 |EDIFFG|），和能量曲线并排；诊断日志保留天数（默认 7 天，启动时和每 6 小时清理）和"清除日志"按钮。Swift 版加了 `--dump-scripts`，两边 22 个脚本逐字一致。自检 Swift 109 项、C# 137 项。

21. 官网三个页面的页脚加了访问量统计（不蒜子 busuanzi，和 GPUMDkit 网站一样；脚本被拦截时不显示）。

22. 2026-10-18 检查更新（两个版本）：设置 → 通用 有"检查更新"按钮和"自动检查更新"开关（默认开，启动 20 秒后和每 24 小时一次）。读 GitHub `releases/latest`，按数字比较版本号；有新版本时刘海提醒一次（同一版本只提醒一次），菜单栏 / 托盘菜单多一项"下载新版本"，下载按钮直接指向当前平台的安装包（.dmg / win-x64.zip / linux-x64.tar.gz）。只提示，不自动替换程序；真正的应用内更新见想法池（Sparkle / Velopack，最好先有 Developer ID 签名）。开发用 `--check-update [假装的版本]`。自检 Swift 113 项、C# 119 项（加上逐字比较 141 项）。
- 数据目录说明：发布版的设置和日志目录从 0.1.0 起没变过（Mac `~/Library/Application Support/ServerIsland`、`~/Library/Logs/ServerIsland`；Windows `%APPDATA%\ServerIsland`；Linux `~/.config/ServerIsland`），升级不会丢设置。带 `-Avalonia` 的目录只有"在 Mac 上试用的跨平台版"会用，是为了不覆盖 Swift 版的设置。

23. 2026-10-18 VASP 力和 NEB（两个版本）：最大力优先用 VTST 的 `FORCES: max atom, RMS` 行，没有时按 POSCAR 的 Selective dynamics 去掉固定方向再算；EDIFFG 先从 OUTCAR 读，≥ 0 时显示"按能量收敛"；加了淡色"最低"线（CG 的试探步会让力上下跳）；C# 版图例重叠修好，并加了 `--curve`。NEB 识别（INCAR 的 IMAGES + 01/OSZICAR）：能垒图（能量曲线 + 每个像点的力柱 + EDIFFG 虚线）和能垒随离子步的变化，在真实算例上和 nebef.pl 一致（算例放在本地 `test/`，不进仓库；`SI_NEB_CASE=<目录>` 让自检打印真实结果）。自检 Swift 122 项、C# 151 项（23 个脚本逐字一致）。

24. 2026-10-18（两个版本）：分区页认出曙光的 DCU（Gres 里的 `dcu`，以及 `npu` / `mlu`），按种类显示"空闲 DCU"；私钥文件权限太宽（ssh 报 `bad permissions` 后忽略密钥）时直接提示 `chmod 600 <文件>`（Windows 提示改文件属性 → 安全），不再误报成"密钥登录失败，服务器也接受密码"。直连服务器的 DCU（`hy-smi` / `rocm-smi`）还没做。自检 Swift 124 项、C# 153 项。

25. 2026-10-18（两个版本）：悬停说明加快到 0.25 秒（Mac 改 NSInitialToolTipDelay，跨平台版改 ToolTip.ShowDelay）；设置 → 通用加"显示在 / 灵动岛显示在"（`islandScreen`：自动 / 主屏幕 / 某块显示器，没连接时退回自动）。Mac 的"自动"最后一步从 NSScreen.main（跟着聚焦窗口变，没有内建屏幕的 Mac 接两台显示器时会来回跳）改成主屏幕。数组进度条本来就是"整个数组完成了多少"，但排队元素只认 %K 带方括号的写法，不带时一整段范围被算成 1 个，进度条偏满；改成以编号里的 `_[...]` 为准。sacct 不可用时，悬停说明里注明完成数可能偏少。

26. 2026-10-18（两个版本）：历史记录。本机记录保留 7 天（以前 24 小时），设置 → 通用 → 文件与诊断 有"清除历史记录…"（只删已结束任务的记录，运行中任务的记忆保留），每条带工作目录（主轮询总是带 `workdirs` 段；sacct 历史加 WorkDir 列），历史页每条下面有目录、复制和"在终端打开"。页面标题改成"历史记录"，Slurm 把 sacct 的最近 24 小时和本机记录合并。记住上次看到的运行中任务（`active-jobs.json`），ServerIsland 退出期间结束的任务下次启动时补记（"已结束 · ServerIsland 未运行时结束"）。在一台单机 Slurm 工作站上只读 `--diagnose` 过，workdirs 段正常、一次采集 1.7 秒。

27. 2026-10-19（两个版本）：降低服务器开销。一个周期里同一份数据只取一次：Slurm 的 squeue 从 3 次减到 1 次（默认命令后面接上 `|@@SI:WD|%T|%Z`），PBS 的 qstat -f 从 3 次减到 1 次，自己的进程只遍历一遍 /proc，nvidia-smi 从 3 次减到 2 次；磁盘每 5 分钟查一次；计算软件识别按文件签名（修改时间 + 大小）跳过没变的目录。用本机的假环境（假 squeue / qstat / nvidia-smi / ps / pgrep、假 /proc）把新旧脚本的每一段输出逐行比较过，只多了 `sig=` 行；在一台单机 Slurm 工作站上用 `--diagnose` 只读对比，解析结果一致。直连服务器上带 SLURM_JOB_ID 的进程现在按 PID 显示进度（以前会被记到一个列表里没有的作业号上）。自检 Swift 130 项、C# 162 项（26 个脚本逐字一致）。第三档（自定义规则只看文件末尾、没有任务时自动放慢、锁屏时放慢）维护者决定暂时不做。

28. 2026-10-19（两个版本）：
   - **自动更新**：点"更新到 v…"后下载、核对 SHA-256（GitHub 的 digest）、替换、重新打开（见 `architecture.md`）。最后决定不用 Sparkle：不用管理签名密钥和 appcast，发布流程不变。`--test-update` 在临时目录里把两个版本的流程都走通；Mac 版还用真实的 v0.2.0 DMG 核对过。**还没在真实安装上从旧版更新到新版过**（要等下一个 Release；第一次只能手动装上带这个功能的版本），Windows 上替换正在运行的 exe 只由 CI 验证。
   - **服务器排序**：设置侧栏里拖动（Mac 原来就能拖，只是没提示）或右键"移到最上面 / 上移 / 下移 / 移到最下面"；C# 版新加了拖动。刘海按这个顺序显示。
   - **终端按钮**：刘海每台服务器卡片的标题栏加了终端图标，直接 ssh 登录（复用后台连接）。
   自检 Swift 131 项、C# 163 项。

29. 2026-10-21（两个版本）：关注任务、跨服务器全部任务页、更细的提醒设置。
   - 关注：任务行上的 ☆（悬停显示）、右键菜单、详情页头部星标；关注的任务在卡片和全部任务页里排在前面；存在 `starredJobs`，重启保留，任务结束后自动取消关注；数组只能关注整个数组。
   - 全部任务页改成跨服务器（头部列表按钮进入；卡片的"还有 N 个"进入时预选那台服务器）：搜索、状态、只看关注、按服务器筛选，按服务器分组；从这里打开详情再返回会回到这一页并保留筛选。
   - 设置 → 通用："刘海与提醒"拆成"刘海"和"提醒"两组；提醒组加"提醒哪些任务"（全部任务 / 只提醒关注的任务，默认全部）和"提醒这些事件"（开始运行 / 完成 / 失败 / 结果未知，默认全开）。静音优先；磁盘提醒不受范围影响；手动监听的进程照常提醒。
   自检 Swift 187 项、C# 191 项；远端脚本没有改动，不增加服务器开销。
30. 2026-10-21（两个版本）：勿扰时段（默认关，23:00–08:00；不响铃、不弹窗，系统通知不出声照发）、设置导入导出（替换前自动备份；Mac 和 Windows / Linux 版互通）。自检 Swift 198 项、C# 202 项。维护者决定不做签名公证（不花钱）和更多计算软件（ABACUS 等）。
31. 2026-10-31（Mac 版）：USB 副屏（固件和 `references/display-handoff.md` 暂不开源，只在维护者本机）。`firmware/` 是微雪 ESP32-S3-Touch-AMOLED-2.16 的固件（PlatformIO + Arduino + LVGL 8.4，思源黑体），Mac 端 `Monitor/DisplayLink.swift` 按 Espressif 厂商号找串口、握手后推送状态和提醒（协议 `firmware/PROTOCOL.md`）。设置 → 通用 → 扩展 → 外接副屏（默认关）。已用演示数据在真板子上走通，并用固件截图核对过中英文界面和提醒横幅。
   同日傍晚第二轮：自写 esp_lcd DMA 送屏 + 不压缩字体 + -O2，整屏重画从 115–145ms 降到 25–45ms；心跳内容没变不重建页面；移植 cc-usage-display 的 AI 用量（Mac 端 `Monitor/AIUsage.swift` 只读 CC Switch 数据库，副屏 `page_ai.cpp`）；三种时钟表盘；3×3 启动器。本机 /Applications 里的 App 已更新。
   同日晚上第三轮：Mac 端 `Monitor/AgentMonitor.swift`（Claude Code 钩子、OpenCode 插件、Codex 会话记录 → 副屏 Agents 页和提醒；设置里 `Settings/AgentHooksView.swift` 一键安装 / 卸载，自检里测了"只追加、卸载后恢复原样"）；AI 用量改为自动；副屏加日历、最近结束、每张 GPU / 磁盘、按键重新定义和测试页、提示音分类 + 音量、屏幕方向（固定 / 自动）。
   同日下午改成一个小系统（固件 0.2.0）：启动器、桌面时钟（RTC 断电保持）、ServerIsland 总览环 / 服务器列表 / 任务详情 / 提醒记录、资源仪表、计时器、系统设置、提示音（ES8311）、夜间熄屏、待机屏保；Mac 端多发了对时和任务详情字段。各页面都用固件截图和模拟触摸检查过。

32. 2026-10-31（两个版本，准备 v0.5.0）：外接副屏、Agent 提醒（Claude Code 钩子 / OpenCode 插件 / Codex 会话记录）、AI 用量（CC Switch）移植到 Windows / Linux 版（`cross/`）：USB 连接、板子设置双向同步、Wi-Fi 与热点表单、截止日期、天气、锁屏熄屏。蓝牙：Mac 和 Windows（WinRT，`WinBle.cs`，未在真机验证）；Linux 还没有。系统日历只有 Mac 版。版本号改为 0.5.0（Info.plist、Directory.Build.props），CHANGELOG 有未发布的草稿；官网链接发布时再改。自检 Swift 215 项、C# 223 项。
33. 2026-11-05（两个版本）：计费（估算费用）。设置侧栏新增"计费"页：总开关（默认关）、货币、每台 Slurm / PBS 服务器按分区填单价（按卡时 / 核时 / 节点时，"其他分区"兜底），可以从服务器读取分区（sinfo / qstat -Q，只读）。运行中作业每个只查一次分到的资源（scontrol 的 AllocTRES，PBS 直接从 qstat -f 取），结束时用 sacct 的 AllocTRES 记账（`cost-ledger.json`，记下当时的单价）。刘海头部多一个钱币按钮进费用页；详情页、历史页显示费用。维护者明确不要：预算提醒、补算开启前的作业、直连服务器计费。自检 Swift 261 项、C# 270 项，31 个脚本逐字一致；两台真实服务器只读核对过（见 remote-protocol.md 计费一节）。
   同日第二轮：单机工作站的 Slurm 没把 gres/gpu 写进 AccountingStorageTRES，AllocTRES 里没有卡，改成按申请的卡数（TresPerJob / TresPerNode × 节点数）兜底；费用页和设置页加上"估算，以供应商账单为准"的说明。自检 Swift 263 项、C# 272 项。
   同日第三轮：副屏（固件 0.4.10）信息表盘右下角的卡片在打开计费时改成费用（今天、本月、每小时），服务器列表显示每台每小时、任务详情和"最近结束"显示费用；默认 squeue 时新作业在同一轮就查资源（不用等下一轮）；今天 / 本月包括正在跑的任务到上次刷新为止的费用，按实际运行时间摊到各天；副屏设置里"共享 Wi-Fi 热点"默认不勾。自检 Swift 266 项、C# 275 项，32 个脚本逐字一致。

## 尚未验证（需要用户确认或等真实任务）

- USB 副屏（2026-10-31）：真实服务器数据下长时间运行、真手指的触摸和滑动手感、三个实体按键（长按）、提示音好不好听（没人听过）、调暗 / 夜间熄屏 / 唤醒、拔插重连、电池续航、90° / 270° 方向下的触摸、自动旋转的方向对不对（传感器坐标轴是猜的，可以校准）；Claude Code 钩子和 OpenCode 插件装上以后的真实事件（OpenCode 事件里的字段名没有文档，是按猜测写的）；蓝牙已打通，拔掉 USB 走蓝牙待长时间验证；Wi-Fi 共享热点手机端转发不通，暂时搁置。

- 导入导出的文件对话框、勿扰时段到点后的实际表现（Mac 真机；C# 的三选一对话框没有在 ui-smoke 里走过）。
- 关注相关的真实交互（2026-10-21）：行上悬停出现的 ☆ 能否点中、点星不会同时打开详情、右键菜单、全部任务页的服务器菜单和搜索框焦点（Mac 真机；C# 版只在 `--ui-smoke` 的真实窗口里走过一遍，Windows / Linux 真机未试）。

- 刘海的真实交互：悬停、长按取消、原生菜单（间隔菜单）、全部任务页的搜索框（能否获得键盘焦点）、ⓘ 悬停提示、切换语言。这些都没法自动截图。
- 动态口令登录：askpass 弹窗、多步验证、保活。用户的动态口令集群还没有加进来。
- 密码、密钥 + 密码的真实登录（本机 sshd 不能做密码验证）；"记住密码"弹窗勾选框、钥匙串写入后的自动重新登录；Windows 凭据管理器（只在 Mac 上编译过）；Linux 的 secret-tool；有用户的"密钥 + 密码"集群和跳板机用户的环境里实测。
- PBS：没有真实集群，只用 PBS Pro / Torque 的样例文本测过转换脚本（Mac 的 BSD awk；Linux CI 上是 mawk）。真实集群上的数组、`qstat -x`、Output_Path、qdel 都需要用户确认。
- 历史记录的"退出期间结束的任务补记"：只用临时目录的自检测过存取，还没在真实的"退出 App → 作业结束 → 再打开"流程里看过。
- 最大力曲线：已在一个真实的 CG 优化上核对过；VTST 和 Selective dynamics 只用构造的文件测过。
- NEB：只在一个本地算例上测过（读文件），还没在服务器上的运行中 NEB 任务上看过刘海和详情页。
- 真实的 VASP、LAMMPS 任务（只用假文件和 mawk 管道测试过），以及设置页"测试"按钮在真实服务器上的表现。
- 真实的 GPUMD 模拟任务，以及通过 Slurm 提交的真实 NEP 作业（只用假文件测试过 environ 映射）。
- 大型集群上真实的数组作业，以及数组首个失败提醒、汇总提醒。
- 登录时启动（需要把 App 放进"应用程序"文件夹后再试）。
- DMG 在另一台 Mac 上第一次打开时的体验；Intel Mac 上能否运行。
- Windows / Linux 版尚未验证的部分见 `references/cross-platform.md` 末尾（Linux 由用户自己在真机上测）。
- 宣传视频的配乐好不好听（只做过响度和结构的客观检查）。

## 已知限制

- Slurm 登录节点上看不到计算节点的 GPU 和进程，计算软件识别只能用作业的提交目录（`%Z`）。作业脚本 cd 进子目录时就识别不到（单机 Slurm 没有这个问题）。
- 没有开启作业记录的 Slurm：作业结束的结果只能依靠计算软件的输出判断，否则显示"已离开队列"。
- 刘海只显示在带刘海的内建屏幕上；没有刘海的屏幕显示一个虚拟刘海。没有做多显示器的选择。
34. 2026-11-08（两个版本）：手机推送（企业微信群机器人 Webhook）。设置侧栏新增"手机推送"页：粘贴 Webhook 地址或 key（只存钥匙串，账户 `wecom-webhook`）、发送测试消息；推送范围默认**只推送关注的任务**（维护者的想法：平时在电脑前看刘海，要离开或重要的任务点星），也可以选全部任务 / 和刘海提醒一样；完成、失败、结果未知、开始运行（默认关）、服务器警告、Agent（默认关，打开后副屏没连也会读 Agent 会话）；详细 / 简洁；勿扰时段不推送（可选失败照常推送）。消息是纯文本（微信插件里能显示），详细版带作业号、分区、资源、用时 / 上限、起止时间、失败原因、软件进度、最后一次采样、目录（只显示最后两层，home 后面的用户名去掉）、费用。10 秒内合并、每分钟最多 15 条、超长按字节截断、网络失败重试一次、key 无效时暂停。不额外查询服务器（详情来自生成事件时已有的数据）。`MonitorEngine.flush` 在按刘海设置过滤**之前**把整批事件交给 `PhonePush.consider`；事件上多了可选的 `push: PushInfo`。`--push-preview` 打印示例消息。维护者明确不要："只在离开电脑时推送"、"隐藏任务名"、日志末尾。自检 Swift 298 项。维护者用真实 key 在 Mac 上发过测试消息，正常。
   同日移植到 Windows / Linux 版：`Core/PhonePush.cs`（key 存凭据库，账户同样是 `wecom-webhook`：Windows 凭据管理器的目标名 `ServerIsland:wecom-webhook`，Linux 用 secret-tool）、`Desktop/Settings/PhonePushPage.cs`、事件上的 `PushInfo`、`DisplayLink.AgentsWanted`（副屏没连但推送要 Agent 时也读会话）。`--push-preview` 中英文输出和 Swift 版逐字一致；设置键名和 Mac 版一致（配置文件互通）。自检 C# 307 项，`--ui-smoke` 通过，`dotnet build -r win-x64` 通过。未验证：Windows / Linux 真机上保存 key（凭据库）和发送、真实作业结束时的推送。
   同日：App 加了主菜单（应用 / 编辑 / 窗口，`AppDelegate.makeMainMenu`，打开设置窗口时按当前语言重建）。之前没有编辑菜单，设置窗口里所有输入框的 ⌘V / ⌘C / ⌘A 都不起作用，只能右键粘贴。“退出”故意不配 ⌘Q，免得关设置窗口时误退出整个 App。

## 跨平台版待办
- USB 副屏的 C# 版（Windows / Linux）：照 `DisplayLink.swift` 和 `firmware/PROTOCOL.md` 做；注意打开串口时不能复位板子（保持 DTR、关 RTS）。

- 降低空闲 CPU（Mac 上试用版 3–4%），详见 `cross-platform.md` 的"已知问题"。
- Linux 真机测试（用户自己测）。
- 决定：长期保持 Swift（Mac）+ C#（Windows / Linux）两个版本，见 `cross-platform.md` 的"决策记录"。

## 想法池（用户尚未决定，参见 user-preferences.md）

副屏的后续计划（小系统、时钟、总览环、蓝牙 / WiFi）见 `firmware/ROADMAP.md`。

关注的任务卡住时提醒、失败时附带日志末尾几行、直连服务器的 DCU、跨服务器历史页（2026-10-21 提过，用户还没选）→ 竖屏或英文版宣传视频。签名公证、更多计算软件：用户决定不做。
