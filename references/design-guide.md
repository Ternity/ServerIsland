# 设计风格与开发规范（两个版本共用）

> Mac 版（Swift，`Sources/`）和 Windows / Linux 版（C#，`cross/`）**看起来、用起来要是同一个 App**。新增或修改界面时，先看这里，再看两边的现有实现照着写。
> 尺寸、颜色、文案以 Swift 版为准；C# 版是它的移植，名字尽量一一对应（见文末对照表）。

## 一、整体风格

- **深色、精致、克制**：刘海 / 面板永远是纯黑底（`#000`），内容用白色的不同透明度分层，强调色只用下面 6 种。不做花哨的渐变背景、不加多余的边框和分隔线。
- **信息密度高但不挤**：一行一件事；次要信息用更淡的颜色而不是更小的字号；放不下就截断（名称用中间省略，路径用头部省略），悬停看全文。
- **一切确定**：刘海里所有行高、间距都是常量（`IslandLayout`），展开动画一次到位，不能"先渲染再测量"导致跳动。
- **像素小章鱼 Tako 是品牌**：logo、托盘图标、刘海、对话框、空状态都用同一张 16×14 网格（见下文"Tako"）。

## 二、配色（`Theme.swift` ↔ `Hue.cs`）

| 名称 | RGB（0–1） | 用途 |
|---|---|---|
| mint | 0.35, 0.91, 0.70 | 运行中、成功、真实进度、主强调色 |
| cyan | 0.36, 0.76, 1.00 | 链接 / 可点击文字、GPU、数组的"运行中" |
| amber | 1.00, 0.75, 0.30 | 排队、需要登录、警告、暂停 |
| coral | 1.00, 0.43, 0.43 | 失败、错误、危险操作 |
| blue | 0.45, 0.62, 1.00 | 已完成（历史）、info 提示 |
| violet | 0.66, 0.55, 1.00 | SLURM 徽章、数组编号、默认分区 |

文字与底色（白色的透明度）：正文 0.94、次要 0.58、更次要 0.36、分隔线 0.08、卡片底 0.055、进度条轨道 0.10。
- 进度条：真实进度用 mint→cyan 渐变；只有"已运行 / 时间上限"时用白色 0.55→0.85 渐变再乘 0.7（表示"只是时间，不是真实进度"）。
- 资源占用：< 60% mint，< 85% amber，其余 coral。
- 状态颜色函数：`Theme.color(for: JobState / HostHealth)` ↔ `Hue.For(...)`，不要在页面里另写一套。

**设置窗口**不用上面的深色：跟随系统浅色 / 深色，用系统控件（Mac：SwiftUI Form `.grouped`；C#：Fluent 主题 + `SettingsKit` 的 `Palette`）。分组标题前是彩色圆角方块图标（类似系统设置）。

## 三、字体与字号

- Mac：SF Rounded（`Theme.rounded`），数字用 `.monospacedDigit()`；等宽用 SF Mono。
- Windows / Linux：系统字体（`Hue.Sans`：Segoe UI Variable / 微软雅黑 / Noto Sans CJK……），等宽 `Hue.Mono`（Cascadia Mono / Consolas / DejaVu Sans Mono）。
- 字号直接照搬 Swift 版（单位相同）：卡片标题 13 semibold；任务名 12 medium；任务行右侧 10.5 medium；次要说明 10–11；徽章 8–9.5 bold；页面标题 13.5 semibold；弹窗标题 14 semibold。
- 作业号、PID、路径、命令、日志一律等宽字体。

## 四、形状与间距（`IslandLayout`，两边数值必须相同）

| 常量 | 值 | 说明 |
|---|---|---|
| wing | 62 | 收起时每侧小翅膀宽 |
| expandedMinWidth / toastMinWidth | 640 / 420 | 展开面板 / 弹窗最小宽度 |
| cardPad / cardHeader / sectionGap | 12 / 20 / 10 | 卡片内边距、头部高、分组间距 |
| metricsRow / jobRow / jobGap | 16 / 34 / 6 | 指标行、任务行、任务行间距 |
| maxJobs | 5 | 卡片里最多显示的任务数，多出的显示"还有 N 个" |
| pageHeight / maxContentHeight | 420 / 540 | 二级页面固定高度、面板内容最大高度（超过就滚动） |

圆角：刘海底部 26（展开）/ 22（弹窗）/ 10（收起），顶部"耳朵" 14 / 10 / 6；卡片 16；内层小卡片 10–12；胶囊按钮全圆角。
动画：形状尺寸用弹簧（response 0.44，damping 0.78）；内容在形状展开后约 0.1 秒开始淡入（0.28 秒，从 96% 放大到 100%）。
**改了卡片的行布局，必须同时改 `cardHeight` 和 `parts`（两个版本都要改）。**

## 五、交互规范

- **悬停展开**：鼠标进入 0.14 秒后展开；离开形状 + 12 的范围后收起（列表页 0.25 秒，二级页面 0.7 秒）。菜单打开期间、固定（📌）时不收起。展开时自动关掉正在显示的弹窗。
- **点击穿透**：刘海窗口比形状大，只有形状所在区域接收鼠标。
- **不抢焦点**：刘海窗口平时不成为活动窗口，只有"全部任务"页（搜索框）例外。
- **危险操作三道关**：设置里默认关闭"允许取消"→ 按住 1.5 秒（`HoldToConfirmButton` / `HoldToConfirm`）→ 再点一次确认（写明将执行的命令）→ 引擎在服务器上核对后才执行。
- **复制**：作业号、路径、命令旁边放复制按钮，点后 1.2 秒内显示对勾；任务行右键菜单可复制作业号 / 名称 / 完整命令。
- **说明文字**：设置项名称后面跟 ⓘ，悬停显示解释（`HelpLabel` / `SettingsKit.Label(title, help)`）。**不要在名称后面加括号写说明**；说明要短，不要写得太细。
- **提醒**：同一次轮询超过 2 个事件合并成一条；每条弹窗 5 秒，底部有倒计时细线；提示音 1.2 秒内只响一次；静音的服务器什么都不发。
- **空状态 / 加载中 / 出错**都要有：加载中用小圈 + 一句话；出错用 amber 三角 + 原因；空状态用 Tako + 一句话 + 引导按钮。

## 六、图标

- Mac 用 SF Symbols；Windows / Linux 用 `cross/.../Ui/Icons.cs` 里的线条图标（24×24 坐标，圆头描边，名字见 `--render-preview` 生成的 `icons.png`）。
- 新加图标时两边意思要一致，例如：

| SF Symbol | Icons.cs | 用途 |
|---|---|---|
| bolt.fill | bolt | 运行中数量、Slurm 作业 |
| hourglass | hourglass | 排队 |
| atom | atom | 识别到计算软件的任务 |
| square.stack.3d.up.fill | stack | 数组作业 |
| clock.arrow.circlepath | history | 最近 24 小时 |
| square.grid.3x3.fill | grid | 分区资源 |
| eye / eye.fill | eye / eyeFill | 监听进程 |
| star / star.fill | star / starFill | 关注任务（琥珀色） |
| list.bullet.rectangle | list | 全部任务（跨服务器） |
| server.rack | server | 全部任务页的服务器筛选 |
| pin / pin.fill | pin / pinFill | 固定面板 |
| gearshape.fill | gear | 设置 |
| key.fill / wifi.exclamationmark | key / wifiAlert | 需要登录 / 连接出错 |

## 七、Tako

- 网格、配色、6 种心情（idle / working / waiting / happy / sad / worried）和帧率见 `ui-and-i18n.md`。两个版本的 `PetSprite` 必须逐格一致。
- **改造型时要一起改**：`Shared/Pet.swift`、`cross/.../Ui/PetView.cs`、`scripts/gen_branding.py`、`cross/scripts/gen_icons.py`、`promo/index.html`。
- 心情由整体状态决定（`petMood` / `MonitorEngine.PetMood`）：有弹窗时跟着弹窗走，错误 → sad，需要登录 → worried，有运行 → working，只有排队 → waiting，否则 idle。

## 八、文案

- 全部界面文案用 `L("中文", "English")`，中英文成对写在调用处。
- **用来匹配服务器输出的字符串不翻译**（`"未找到"`、`"not found"`、squeue 前缀……）。
- 中文：口语、简短、不用"请您"；数字和英文两边加空格风格与现有文案一致；单位写"代 / 步 / 离子步"。英文：句首大写，按钮用 Title Case（"Log In…"、"Refresh Now"）。
- 要打开对话框的按钮文字以"…"结尾。
- 详情字段的 key（如 `L("工作目录", "Directory")`）会被用来查找，**取值和显示两处要用同一对写法**。

## 九、代码规范

**通用**
- 注释用中文，写"为什么"，不写"做了什么"；每个类型 / 文件开头一段说明。
- 远端操作必须**只读、轻量**：只读文件末尾或小文件；一次轮询只开一个 SSH 通道；格式对不上就当作没识别到，不显示错误的数字。
- 会改变服务器状态的操作（取消、kill）必须先在服务器上核对；作业号走 `safeSlurmID` / `SafeSlurmID`，PID 必须是纯数字，其它字符串一律 `shellQuote` / `ShellQuote`。
- 配置字段新增时，**两个版本都要能读写同一个 config.json**：Swift 用 `decodeIfPresent ?? 默认值`；C# 用属性初始值 + `IJsonOnDeserialized` 做旧值升级。键名 camelCase，枚举存 rawValue。
- 隐私：代码、文档、截图、测试数据里不能出现真实用户名、主机别名、路径、分区名，演示数据只用 `DemoData`。

**Swift（Mac 版）**
- 没有 Xcode：不能用 Previews、`@Observable`（用 `ObservableObject`）。改界面后用 `--render-preview` 看图。
- 引擎和界面都是 `@MainActor`。

**C#（Windows / Linux 版）**
- 界面全部用代码构建（不用 XAML），小部件在 `Ui/Kit.cs`、`Island/HostCardView.cs`、`Settings/SettingsKit.cs`，新界面优先复用它们。
- 远端脚本用原始字符串字面量 `"""…"""`（插值用 `$$"""…{{x}}…"""`），保证 `\0`、`\n` 是字面的两个字符；awk 里的制表符用 `⇥` 占位再替换。
- 引擎只在界面线程上使用；后台线程的回调（SSH 库的提示、计时器）要切回界面线程。
- 数据变化时整棵重建刘海内容；每秒走字的文字用 `Kit.LiveT`（注册到 `LiveScope`），不要为了走字重建。
- 布局：横向一行用 `Kit.Row`（`Kit.Spacer` 是弹性空白，`null` 表示跳过，`Fill(x)` 占满剩余）；名称需要截断时用 `ShrinkRow`。
- 配色类叫 `Hue`（`Theme` 会和 Avalonia 的属性冲突）；继承 Avalonia 控件要写 `StyleKeyOverride`。
- 平台相关代码只放在 `Platform/`（`Native.cs`：Win32 / X11；`MacNative.cs`：试用用的 macOS 调用；`Services.cs`：终端、提示音、开机启动、通知）。

## 十、Swift ↔ C# 文件对照

| Swift（`Sources/ServerIsland/`） | C#（`cross/src/`） |
|---|---|
| Models/Config.swift | ServerIsland.Core/Config.cs |
| Models/Snapshot.swift | ServerIsland.Core/Snapshot.cs |
| Models/ProgressRule.swift | ServerIsland.Core/ProgressRule.cs |
| Monitor/RemoteScript.swift | ServerIsland.Core/RemoteScript.cs |
| Monitor/Parsers.swift | ServerIsland.Core/Parsers.cs |
| Monitor/AppDetectors.swift | ServerIsland.Core/AppDetectors.cs |
| Monitor/AppCurves.swift | ServerIsland.Core/AppCurves.cs |
| Monitor/PendingReason.swift | ServerIsland.Core/PendingReason.cs |
| Monitor/Partitions.swift | ServerIsland.Core/Partitions.cs |
| Models/Billing.swift、Monitor/MonitorEngine+Billing.swift | ServerIsland.Core/Billing.cs、MonitorEngine.Billing.cs |
| Notch/CostPage.swift、Settings/BillingSettingsView.swift | ServerIsland.Desktop/Island/CostPage.cs、Settings/BillingPage.cs |
| App/SelfTestBilling.swift | ServerIsland.Core/SelfTestBilling.cs |
| Monitor/PhonePush.swift、Settings/PhonePushSettingsView.swift、App/SelfTestPush.swift（手机推送） | ServerIsland.Core/PhonePush.cs、Desktop/Settings/PhonePushPage.cs、Core/SelfTestPush.cs（消息文本两边逐字一致，可用 `--push-preview` 对比） |
| Monitor/HistoryStore.swift、Shared/DebugLog.swift | ServerIsland.Core/HistoryStore.cs |
| Monitor/DemoData.swift | ServerIsland.Core/DemoData.cs（另有详情 / 历史 / 分区的演示数据） |
| Monitor/MonitorEngine(+Actions).swift | ServerIsland.Core/MonitorEngine(.Actions).cs |
| Monitor/DisplayLink.swift（USB 副屏） | ServerIsland.Core/DisplayLink.cs（BLELink.swift → Windows：Desktop/Platform/WinBle.cs；Linux 还没有蓝牙） |
| Monitor/AgentMonitor.swift、AIUsage.swift、DisplayExtras.swift | ServerIsland.Core/AgentMonitor.cs、AIUsage.cs、DisplayExtras.cs（没有系统日历） |
| Settings/DisplaySettingsView.swift、AgentHooksView.swift、CampusNetworkSection.swift | ServerIsland.Desktop/Settings/DisplayPage.cs |
| SSH/SSHRunner.swift、AskPass.swift | ServerIsland.Core/Ssh/SshCommon.cs、OpenSshTransport.cs（+ Desktop/Dialogs.cs 的 AskPassApp） |
| SSH/SSHConfigParser.swift | ServerIsland.Core/Ssh/SshConfig.cs |
| —（Mac 版用系统 ssh） | ServerIsland.Core/Ssh/ManagedSshTransport.cs、KnownHosts.cs |
| App/SelfTest(Apps).swift | ServerIsland.Core/SelfTest(Apps).cs |
| Shared/Localization.swift、Format.swift | ServerIsland.Core/Lang.cs、Text.cs |
| Shared/Theme.swift、Components.swift | ServerIsland.Desktop/Ui/Hue.cs、Kit.cs、Icons.cs |
| Shared/Pet.swift | ServerIsland.Desktop/Ui/PetView.cs |
| Shared/UpdateChecker.swift、UpdateInstaller.swift | ServerIsland.Core/UpdateChecker.cs、UpdateInstaller.cs |
| Notch/IslandLayout.swift、NotchGeometry.swift | ServerIsland.Desktop/Island/IslandLayout.cs、IslandSurface.cs（NotchShape） |
| Notch/IslandController.swift | ServerIsland.Desktop/Island/IslandWindow.cs |
| Notch/IslandView.swift | ServerIsland.Desktop/Island/IslandViews.cs、IslandState.cs |
| Notch/HostCard.swift | ServerIsland.Desktop/Island/HostCardView.cs |
| Notch/IslandPages.swift、CurveChart.swift | ServerIsland.Desktop/Island/IslandPages.cs、CurveChart.cs |
| Settings/*.swift | ServerIsland.Desktop/Settings/*.cs |
| App/AppDelegate.swift、main.swift | ServerIsland.Desktop/App.cs、Program.cs |
| App/PreviewRenderer.swift | ServerIsland.Desktop/Preview/PreviewRenderer.cs、UiSmoke.cs |
| — | ServerIsland.Desktop/Island/PetWindow.cs（桌宠模式，只有跨平台版有） |
