# Windows / Linux 版（`cross/`）

> 设计风格、文案、代码规范见 `design-guide.md`（两个版本共用，含 Swift ↔ C# 文件对照）。

> Mac 版仍然是 Swift（`Sources/`），功能和显示**完全不变**。`cross/` 是另一套 C# 实现，给 Windows / Linux 用；在 macOS 上能编译、跑自检和预览，但不作为 Mac 的发布版本。

## 技术栈与目录

| 部分 | 技术 |
|---|---|
| 语言 / 运行时 | C# 13 + .NET 10（`brew install dotnet`），打包成自带运行时的单文件 |
| 界面 | Avalonia 11.3（Fluent 主题），**全部用代码构建，没有 XAML** |
| SSH | Linux：系统 ssh + ControlMaster（和 Mac 版一样）；Windows：内置 SSH.NET 长连接（Windows 自带 OpenSSH 不支持 ControlMaster） |

```
cross/
  Directory.Build.props        版本号（发布前和 Info.plist 一起改）
  src/ServerIsland.Core/       与界面无关：配置、远端脚本、解析、计算软件识别、轮询引擎、SSH、自检（Swift 版的逐文件移植）
    Ssh/                       SshCommon（PATH 补全、错误归类）、OpenSshTransport、ManagedSshTransport、SshConfig、KnownHosts
  src/ServerIsland.Desktop/    Avalonia 界面
    Island/                    IslandWindow（窗口、悬停、输入区域）、IslandSurface（形状 + 弹簧动画）、IslandViews / HostCardView / IslandPages（内容）、PetWindow（桌宠）
    Settings/                  设置窗口（通用 / 进度识别 / 单台服务器 / 添加服务器 / 规则编辑）
    Ui/                        Hue（配色，不能叫 Theme：会和 StyledElement.Theme 冲突）、Icons（代替 SF Symbols 的线条图标）、Kit（小部件）、PetView（Tako）
    Platform/                  Native（Win32 / X11 调用）、Services（终端、提示音、开机启动、通知）
    Preview/                   PreviewRenderer（--render-preview）、UiSmoke（--ui-smoke）
  scripts/publish.sh           打包 win-x64 / linux-x64（可指定 win-arm64 等）
  scripts/gen_icons.py         生成 exe 图标、托盘图标
```

## 常用命令

```bash
cd cross/src/ServerIsland.Desktop
dotnet build                                   # 编译
dotnet run -- --selftest                       # 自检：和 Swift 版对应的检查 + 跨平台版特有的检查（Mac 上 223 项）
dotnet run -- --render-preview <目录> [zh|en]   # 离屏渲染刘海各状态、各页面、设置窗口、图标表
dotnet run -- --ui-smoke <目录>                 # 在真实窗口上走一遍界面并截图（Windows 真机上也能用）
dotnet run -- --demo                           # 演示模式（不连接服务器；详情 / 历史 / 分区也有假数据）
SERVERISLAND_SSH=managed dotnet run -- --diagnose <别名> slurm+   # 用内置 SSH 对真实服务器只读采集一次
cross/scripts/publish.sh                       # → build/ServerIsland-<版本>-win-x64.zip、-linux-x64.tar.gz
```

- 在 macOS 上运行时，配置放在 `~/Library/Application Support/ServerIsland-Avalonia/`，**不会动 Swift 版的配置**；也可以用 `SERVERISLAND_CONFIG_DIR` 指定。
- Windows：配置 `%APPDATA%\ServerIsland\config.json`，日志 `%LOCALAPPDATA%\ServerIsland\Logs\serverisland.log`；Linux：`~/.config/ServerIsland/`、`~/.local/state/ServerIsland/`。配置文件格式和 Mac 版一致（键名、枚举值、UUID 大写、日期存成距 2001 年的秒数），可以直接拷过去用。

## 和 Swift 版保持一致的约定

- **换行一律 LF**（`.gitattributes` 里 `* text=auto eol=lf`）：C# 原始字符串会原样保留源文件的换行，Windows 默认 `core.autocrlf=true` 时内嵌的 bash 脚本会带 `\r`。2026-10-18 第一次在 Windows CI 上跑自检时就因此失败了 39 项（在 Mac 上把源码转成 CRLF 可以复现）。两种 SSH 传输和自检里的 bash 调用都会再把 `\r\n` 换成 `\n`，作为第二道保险。
- **远端脚本逐字一致**：所有脚本用 C# 原始字符串字面量（`"""…"""`）书写，`\0`、`\n` 保持字面。验证方法：`.build/debug/ServerIsland --dump-scripts <目录>`（列表在 `Sources/ServerIsland/App/ReferenceScripts.swift`，和 C# 的 `SelfTest.ReferenceScripts()` 一一对应），再运行 `SERVERISLAND_SCRIPT_REF=<目录> dotnet run -- --selftest`，32 个脚本应全部"和 Swift 版脚本一致"。新增脚本时两边的列表一起加。**改 Swift 版的远端脚本时，两边要一起改。**
- 尺寸常量（`IslandLayout`）、配色（`Hue`）、Tako 网格（`PetSprite`）、文案（`L(zh, en)`）都照搬 Swift 版；用来匹配服务器输出的字符串不翻译。
- 引擎在界面线程上运行，`await` 之后回到界面线程（对应 Swift 的 `@MainActor`）。

## Windows / Linux 特有的设计

- **显示方式**（设置 → 通用 → 显示方式）：
  - 灵动岛：主屏幕顶部正中的虚拟刘海（没有硬件刘海，中间空隙宽 0）；"从不显示"时只留一条 96×7 的把手。
  - 桌面小章鱼：Tako 待在桌面上（可拖动，位置存在 `petX/petY`），徽章显示运行数 / 排队数 / 异常；面板浮在它旁边（`IslandLayout.Spec(floating: true)`，四角圆角，没有耳朵）。
  - 两种方式都有托盘图标：点击打开并固定面板；右键菜单有各服务器状态、刷新、显示方式、设置、退出。
- **点击穿透**：窗口固定为最大展开尺寸，只把形状所在矩形设为输入区域（Windows `SetWindowRgn`，Linux XShape 输入区域），其余部分的点击落到下面的窗口。macOS 上（只用于试用）每 30 毫秒读一次光标位置，切换 `ignoresMouseEvents`（`Platform/MacNative.cs`），同时用硬件刘海的真实位置、层级盖过菜单栏。
- **不抢焦点**：Windows 上加 `WS_EX_NOACTIVATE`，只有"全部任务"页（搜索框）允许激活。
- **悬停**：鼠标事件 + 每 0.25 秒用系统光标位置核对一次（`Native.CursorPosition`，Windows / X11），防止菜单关闭后收不到 PointerExited。
- **内置 SSH（`ManagedSshTransport`）**：
  - 每台服务器一条长连接（密码登录的按"保持连接"时长过期），轮询只开新通道；
  - 读 `~/.ssh/config`（Host 通配、Include、HostName、User、Port、IdentityFile、ProxyJump 多跳、StrictHostKeyChecking、UserKnownHostsFile、HostKeyAlias）；
  - `known_hosts`：明文 / 哈希 / `[host]:port` 都支持；没见过的指纹后台轮询时报"需要确认主机指纹"，用户点"登录…"确认后写入；**指纹变了直接拒绝**；
  - 有口令的密钥、密码、keyboard-interactive（动态口令）都在登录对话框里输入；密钥口令只存在内存里；
  - 认证方式：密码 / 密钥 + 密码会先要密码（或用记住的密码），连同密钥、keyboard-interactive 一起交给 SSH.NET，它会按服务器的 partial success 一步步走完（"密钥 + 密码"就是先密钥后密码）；服务器只接受 `password` 方式时再问一次密码，**密钥照样带上**；
  - 手动填写的服务器：以 `~/.ssh/config` 里 `Host *` 的设置为底，再用填写的地址、用户、端口、私钥覆盖；
  - ServerIsland 里设的跳板机：先拿到跳板机的连接（已登录就复用，否则按它自己的认证方式连上并保存），再用 `ForwardedPortLocal` 经它转发；多级时递归。报错按 OpenSSH 的格式写成 `user@地址: Permission denied (方法).`，`SshCommon.JumpProblem` 据此指出是哪一跳；
  - 记住密码：`CredentialStore`（Windows 凭据管理器，目标名 `ServerIsland:<UUID>`；Linux `secret-tool`，没有它时界面提示不能记住；macOS 只为开发用 `security`）。登录成功后才按勾选保存 / 删除；轮询时连接断了且记住了密码，就在后台用它重连（失败后 10 分钟内不再试）；
  - 报错用 OpenSSH 的措辞，`SshCommon.Classify` 两种实现通用；脚本前加 `LC_ALL=C`（系统 ssh 通过 SendEnv 达到同样效果）；
  - 不支持 ssh-agent、`~/.ssh/config` 里的 ProxyCommand：这种情况在设置里改用"系统 ssh"，或改用 ServerIsland 里的跳板机。
- **系统 ssh（`OpenSshTransport`，Linux 默认）**：和 Mac 版一样用私有 ssh 配置（`SshHosts.Render`，`-F`）、按服务器区分的 socket、askpass 会话目录和静默自动登录；askpass 读 `SERVERISLAND_ASKPASS_HOSTID` 决定弹窗标题和能否记住。Windows 自带的 OpenSSH 不支持 ControlMaster，那里不写私有配置，手动 / 跳板机走内置 SSH（终端里打开时拼成 `-p / -i / -J`）。
- **通知**：Linux 用 `notify-send`；Windows 未打包的程序发不了原生通知，提醒靠刘海弹窗 + 提示音（设置里不显示"系统通知"开关）。
- **开机启动**：Windows 注册表 `HKCU\…\Run`；Linux `~/.config/autostart/serverisland.desktop`。
- **睡眠唤醒**：没有跨平台通知，引擎每 10 秒看一次时钟，跳变超过 30 秒就当作刚唤醒，4 秒后重新轮询。
- **外接副屏（2026-10-31，v0.5.0）**：和 Mac 版同一套协议和设置项（`usbDisplay`、`displayLockSync`、`deadlines`、`weatherCity`…，配置文件两边互通）。`DisplayLink.cs` 在界面线程上跑（计时器用 `await Task.Delay`，串口读线程把行 Post 回来）；只有 **USB**（`System.IO.Ports`，打开前 `DtrEnable=true`、`RtsEnable=false`，否则会复位板子）。找串口：Windows 读注册表 `USB\VID_303A…\Device Parameters\PortName`，Linux 在 `/sys/class/tty/*/device` 往上找 `idVendor`，macOS 试用版用 `/dev/cu.usbmodem*` 靠握手过滤。锁屏检测（`Platform/Services.cs` 的 `LockState`）：Windows 看 `LogonUI.exe`，Linux 用 `loginctl show-session $XDG_SESSION_ID -p LockedHint`，每 3 秒一次。系统日历只有 Mac 版能读，这里只发截止日期。Agent 钩子调用 `Environment.ProcessPath`（Windows 上命令用双引号），程序挪了位置要在设置里点"更新"；`--agent-event` 在 `Program.Main` 最前面处理，不打印任何东西。AI 用量用 `Microsoft.Data.Sqlite` 只读打开 `~/.cc-switch/cc-switch.db`。**蓝牙只有 Windows**：`Platform/WinBle.cs`（WinRT：广播过滤服务 UUID → 没配对就自定义配对 `ProvidePin`，弹 `PromptDialog` 让用户输入副屏上的 6 位码 → 订阅 TX 通知 → 写 RX 时按 `GattSession.MaxPduSize − 3` 切块、带回应写入、同类快照只留最新），通过 `DisplayLink.BleFactory` 接入（`IDisplayBle`），USB 优先。为此 Desktop 项目在 `-r win-*` 或在 Windows 上编译时目标框架换成 `net10.0-windows10.0.19041.0`（`EnableWindowsTargeting`，在 Mac 上用 `dotnet build -r win-x64` 检查能否编译）。Linux 蓝牙（BlueZ）还没做。Windows / Linux 真机上的串口、蓝牙配对、锁屏检测、钩子都未验证（Mac 上已用真板子测过 USB 握手和推送）。
- **手机推送（2026-11-08，v0.5.0）**：企业微信群机器人，和 Mac 版同一套设置项（`phonePush`，配置文件互通）和消息文本（`--push-preview` 两边逐字一致，改了格式要对比一次）。`Core/PhonePush.cs` 在界面线程上跑（`HttpClient`，`await Task.Delay` 做 10 秒合并和限速），key 存 `CredentialStore.SaveSecret("wecom-webhook")`：Windows 凭据管理器、Linux 的 secret-tool（没有 secret-tool 时保存失败，设置页会提示）。Agent 提醒：`DisplayLink.AgentsWanted`（副屏连着或推送要 Agent）为真时读会话。Windows / Linux 真机上保存 key 和发送都未验证。
- **打包**：未签名，Windows 首次运行 SmartScreen 会提示"Windows 已保护你的电脑"，点"更多信息 → 仍要运行"。

## 开发流程

1. **分支**：两个版本的日常改动都在 `dev` 分支上做（原来叫 `avalonia`，2026-10-18 改名）；测好（加上 CI 通过）之后合并进 `main`。
2. **改核心逻辑**（远端脚本、解析、识别规则、提醒判断）时两边一起改：先改 Swift 版，再照着改 `cross/src/ServerIsland.Core/` 里同名的文件，最后跑两边的自检，并做一次脚本逐字比对（见上文）。
3. **改界面**：`dotnet run -- --render-preview <目录> zh` 看预览图，`dotnet run -- --ui-smoke <目录>` 在真实窗口上走一遍。和 Mac 版一样，悬停、长按、菜单这些真机交互要请用户确认。
4. **提交前**：`dotnet build` 没有警告、`--selftest` 全部通过；推送后看 GitHub Actions 的 `Cross-platform build`（Windows / Linux 的自检、界面走查截图、安装包都在 Artifacts 里）。
5. **发布**：`Resources/Info.plist` 和 `cross/Directory.Build.props` 里的版本号一起改 → `scripts/make_dmg.sh`（Mac）+ `cross/scripts/publish.sh`（Windows / Linux）→ 在 GitHub 上新建 Release，把 `.dmg`、`win-x64.zip`、`linux-x64.tar.gz` 作为附件上传。编译产物不进 git。
6. **在 Mac 上试用跨平台版**：`cross/scripts/build_mac_preview.sh --run`（会先退出 Swift 版；配置在 `ServerIsland-Avalonia` 目录，第一次可以从 Swift 版的 config.json 拷过去）。切回 Swift 版：退出 Preview（菜单栏图标 → 退出），再打开 `/Applications/ServerIsland.app`。

## 哪些文件进 GitHub

| 进仓库 | 不进仓库（.gitignore） |
|---|---|
| `cross/src/**`（源代码、`Assets/` 里的图标和提示音）、`cross/scripts/`、`cross/packaging/`、`cross/README.md`、`cross/Directory.Build.props` | `cross/**/bin/`、`cross/**/obj/`（编译中间文件） |
| `references/cross-platform.md`、`AGENTS.md`、`.github/workflows/cross.yml` | `build/`（DMG、zip、tar.gz、Preview.app） |
| | `references/private/`（本机环境、用户偏好） |

## 决策记录：两个版本并存（2026-10-17）

讨论过"把 Mac 版也改成 C#，只维护一套代码"，**决定不改**，Mac 版继续用 Swift。当时在维护者的 Mac 上实测：

| | Swift 版 | 跨平台版（Mac 上试用） |
|---|---|---|
| App 体积 | 几 MB | 约 112 MB（带 .NET 运行时） |
| 内存 | 约 45 MB | 约 69 MB |
| 空闲 CPU | 约 1% | 3–4%（待优化，见下） |
| 刘海、点击穿透、菜单、通知、登录启动 | 原生 | 靠 Objective-C 调用补上，菜单 / 字体不是原生，没有系统通知 |

理由：Mac 版是主力、已经发布（README、官网、宣传视频都是它）；两边真正要同步的只有核心逻辑（约 2500 行，改得不频繁，而且有逐字比对的自检兜底）；跨平台版还没在 Windows / Linux 上长期验证。

**以后可以重新考虑的信号**：核心逻辑频繁改动、两边同步明显吃力；或者跨平台版在 Windows / Linux 上稳定一段时间、在 Mac 上的体验不输 Swift 版。到时候也可以先走一步折中：把远端脚本从代码里抽成两边共用的文本模板，重复的部分只剩解析。

## 已知问题 / 待办

- 空闲 CPU 偏高（Mac 上实测 3–4%）：可能来自 30 毫秒的光标轮询（只在 macOS）、每个 Tako 和小圈的计时器、每秒的走字；Windows / Linux 上还没测。优化方向：只在需要时开计时器、降低轮询频率、合并成一个共享计时器。
- Windows 上没有系统通知（未打包的程序发不了原生 Toast）。
- 内置 SSH 不支持 ssh-agent、ProxyCommand。
- 只在主屏幕显示，没有做多显示器的选择。

## 踩过的坑

1. `Theme` 在控件里会解析成 `StyledElement.Theme`（ControlTheme），所以配色类叫 `Hue`。
2. 继承 `Button` 的控件要写 `StyleKeyOverride => typeof(Button)`，否则拿不到 Fluent 样式。
3. `IslandSurface.SetTarget` 必须 `InvalidateMeasure`：只重新排列会沿用旧宽度（曾经让详情页按 96 宽测量，文字高度变成 0）。
4. `ArrangeOverride` 里不能用 `Bounds`（那时还是旧值），要用 `finalSize`。
5. `Kit.Row(...)` 里 `null` 表示跳过，弹性空白用 `Kit.Spacer`（曾经把条件项的 null 当成空白，导致文字只拿到一半宽度）。
6. `--ui-smoke` 里展开面板要先固定（Pinned），否则光标核对会立刻把它收起。

## 验证状态（2026-10-17）

- 用户已在 Windows 真机（Intel x64）上运行，能正常使用；修了"桌宠切回灵动岛后位置跑到屏幕中间"（`IslandSurface.AnchorBottom` 没复位，`--ui-smoke` 第 8 步专门检查这个）。
- Mac 上试用版：贴合硬件刘海、层级盖过菜单栏、点击穿透都已实现（`MacNative.cs`），悬停和点击需要用户在真机上确认。

- 已验证：自检 87 项（含 16 个脚本与 Swift 版逐字一致）；离屏预览 20 张（中英文、设置浅色 / 深色）；macOS 上真实窗口 `--ui-smoke` 8 步无异常；内置 SSH 对两台真实服务器（一台 22 端口的单机 Slurm 工作站、一台自定义端口 + IdentityFile + sacct 的 Slurm 集群）只读采集成功，结果与系统 ssh 一致。
- **未验证（需要在真机上确认）**：Windows / Linux 上的实际显示（透明窗口、输入区域、不抢焦点、托盘、DPI 缩放）、提示音、开机启动、`notify-send`、Linux 上的 SSH_ASKPASS 登录弹窗、内置 SSH 的密码 / 动态口令 / ProxyJump 登录。
