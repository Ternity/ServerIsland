# FORK-NOTES.md — 给 AI agent / 新维护者看的本分支说明

> 这个仓库是 **Ternity 维护的非官方 Windows 修复分支**（unofficial fork），不是上游。
> **动手改代码前先读完本文**，再读上游的 `AGENTS.md` 和 `references/`（那些文档对本仓库同样有效，本文只写"和上游不一样的地方"）。

- 上游：<https://github.com/zhyan0603/ServerIsland>，基线 **v0.5.0**（commit `1e6393a7`），GPL-3.0
- 本分支：<https://github.com/Ternity/ServerIsland>，版本 **0.5.1**，同样 **GPL-3.0**
- 只改了 **Windows 侧行为**（`cross/` 里的 C# 版）；Swift 的 Mac 版一行没动，也不在本分支发布
- 本分支的 Release 只发 Windows 的 `ServerIsland-<版本>-win-x64.zip`

---

## 1. 为什么要这个分支（根因，已实测）

上游 v0.5.0 的 Windows 版有个**必现但难查**的 bug：程序跑一段时间后，屏幕顶部的灵动岛"消失"，
双击 exe 没反应，只有任务管理器结束进程再开才恢复。

实测结论（不是渲染问题，不是崩溃）：

1. 进程活着、UI 线程正常、`IsWindowVisible = True`、DWM 未 cloak、窗口 region 正确、内容也画好了（`PrintWindow` 能看到章鱼和数字）；
2. 但窗口**已经不在「置顶（topmost）」的 z 序带里**：权威 z 序里岛是 index 93，而盖住它的 Edge 窗口是 index 30；
3. 岛自身的 exstyle 仍然带着 `WS_EX_TOPMOST(0x8)` —— **"样式位在、带没了"**；
4. `WindowFromPoint(胶囊中心)` 返回的是那个普通窗口；
5. 一条 `SetWindowPos(hwnd, HWND_TOPMOST, ... SWP_NOMOVE|SWP_NOSIZE|SWP_NOACTIVATE)` 就让岛立刻出现，**不需要重绘**；
6. 上游源码里 `Topmost = true` 只在 `IslandWindow` 构造函数出现一次（`IslandWindow.cs:46`），
   全仓库 `grep SetWindowPos|HWND_TOPMOST|SWP_` **0 命中**，即程序**没有任何"必要时重新置顶"的路径**，
   一旦被系统降级就永远回不来；
7. `Native.ChangeExStyle`（`Native.cs`）直接写 `GWL_EXSTYLE` 且不跟 `SetWindowPos(SWP_FRAMECHANGED)`，
   是"位在带失"最可疑的来源（切页 / 打开窗口时都会走）；
8. 第二个实例抢 Mutex 失败直接 `return 0`（`Program.cs`），所以**双击 exe 对已在运行的实例毫无影响**。

诱因（未完全定论，按可能性）：Modern Standby 唤醒（日志显示每天多次"系统已唤醒（时钟跳变）"）>
三屏混合 DPI 的拓扑变化 > Explorer/DWM 异常。要复现，见第 5 节。

---

## 2. 相对上游改了什么

| 文件 | 改动 | 为什么 |
|---|---|---|
| `cross/src/ServerIsland.Desktop/Platform/WinTopmost.cs` 🆕 | `Raise` / `FlushStyles` / `IsCoveredByNormalWindow` | 把"重新置顶"和"是否掉出置顶带"做成两个 Windows 专属原语，P/Invoke 全收在这一个文件 |
| `cross/src/ServerIsland.Desktop/Platform/WinSingleInstance.cs` 🆕 | 命名事件 `Create` / `Listen` / `RaiseExisting` | 第二个实例通知第一个实例"把岛显出来"，然后自己退出 |
| `Platform/Native.cs` | 加 `EnsureTopmost` / `IsTopmostLost` 两个转发；`ChangeExStyle` 写完补 `FlushStyles` | 共享代码只做 platform-guarded 转发，实现全在 win 专属文件 |
| `Island/IslandWindow.cs` | 4 个时机重申置顶（`OnOpened`、`UpdateActivation`、`Screens.Changed`）+ 新增 `RestoreVisible` / `GuardTopmost` + 2 秒 `_topTimer` | 覆盖"建窗口 / 写 exstyle / 换屏 / 任意原因被降级" |
| `Program.cs` | 单实例失败时 `WinSingleInstance.RaiseExisting()`，成功时 `Create()`；`AppInfo.Repo/Issues` 指向本仓库 | 双击 exe 能把岛叫回来；更新/反馈地址不再指上游 |
| `Core/UpdateChecker.cs` | `ApiUrl` 指向本仓库 | **不改就会被上游 0.5.x 自动覆盖掉本分支的补丁** |
| `cross/Directory.Build.props` / `Resources/Info.plist` | 版本 0.5.1 | 发版与自动更新比较的依据 |
| `.github/workflows/release.yml` 🆕 | 推 `v*` tag → 构建 + 自检 + 打包 + 发 Release | 上游 CI 只上传 artifact，不发版 |
| `README.md` / `README.en.md` / `CHANGELOG.md` | 非官方分支声明 + 修改内容 + 日期 | GPLv3 §5(a)/§6 的要求，别删 |

---

## 3. 不变量（破坏任何一条都会让 bug 复发或引入新问题）

1. **平台相关实现只放 `Platform/Win*.cs`**；共享文件里只允许加"非 Windows 直接返回"的转发（`OperatingSystem.IsWindows()`）。
   上游还有 mac/Linux 构建，不要在共享代码里直接调 Win32。
2. **判断"掉没掉出置顶带"绝不能用 `WS_EX_TOPMOST` 位**（故障态就是位还在），也不要用 `GetWindow(hwnd, GW_HWNDPREV)`
   （实测会返回自己的 IME 子窗口）。用 `WindowFromPoint` + `GetAncestor(GA_ROOT)` + 对方是否 topmost。
3. **重新置顶必须带 `SWP_NOACTIVATE | SWP_NOMOVE | SWP_NOSIZE`**：岛是 `WS_EX_NOACTIVATE` 的装饰窗口，抢焦点会打断用户打字。
4. **两个"别动"开关必须留着**：`!IsVisible`（桌宠模式下窗口本来就隐藏）、`State.MenusOpen > 0`
   （此时抬 z 序会把岛自己的弹出菜单压到岛下面）。
5. **改完 `GWL_EXSTYLE` 必须跟一次 `SetWindowPos(SWP_FRAMECHANGED)`**（`WinTopmost.FlushStyles`），否则"位在带失"会再来。
6. **不要用 `Topmost = false; Topmost = true;` 代替** `SetWindowPos`：Avalonia 的 styled property 值不变就不下发，
   翻转瞬间窗口真的会掉进普通带（可能闪一下），而且窗口不可见时无效。
7. **单实例转发要轻**：第二个实例只发一个命名事件就退出，不建窗口、不抢焦点。
8. **发版必须守约定**（`UpdateChecker` 依赖它们）：tag = `v<Version>` 且与 `cross/Directory.Build.props` 一致；
   资产名以 `win-x64.zip` 结尾；Release **不能是** draft / prerelease；zip 里必须有 `ServerIsland.exe`；
   GitHub 自动算的 digest 必须在（没有 digest 的附件程序不会自动安装）。
9. **仓库是公开的**：代码、文档、提交信息里不要写本机用户名 / 主机别名 / 私有路径 / 服务器信息（上游 `AGENTS.md` 铁律 8）。
   `bin/`、`obj/` 不要提交。
10. **不要删改 GPL 声明**：根 `LICENSE`、包内 `LICENSE.txt`、README 顶部的非官方分支声明、`CHANGELOG` 的 fork 条目。

---

## 4. 代码地图（本分支新增/改动的关键位置）

```
cross/src/ServerIsland.Desktop/
  Platform/WinTopmost.cs        Raise() / FlushStyles() / IsCoveredByNormalWindow()
  Platform/WinSingleInstance.cs Create() / Listen() / RaiseExisting()
  Platform/Native.cs            EnsureTopmost() / IsTopmostLost() / ChangeExStyle()
  Island/IslandWindow.cs        OnOpened 末尾、UpdateActivation()、Screens.Changed、
                                RestoreVisible()、GuardTopmost()、_topTimer(2 s)
  Program.cs                    Main() 的单实例分支；AppInfo.Repo / Issues
cross/src/ServerIsland.Core/UpdateChecker.cs   ApiUrl
.github/workflows/release.yml                  打 tag 即发版
```

排查/验证用的脚本**不在仓库里**（避免把本机信息和临时脚本带进来），在维护者本机的分析目录里：
`repro-demote.ps1`（制造"掉出置顶带 + 被覆盖"）、`Restore-Island`/`Watch-Island`（手动/常驻救岛）、以及原始证据与截图。

---

## 5. 构建、验证、发版

```bash
# 构建（Windows，需要 .NET 10 SDK；本机的 SDK 不在 C:\Program Files\dotnet，那里只有 runtime）
dotnet build cross/src/ServerIsland.Desktop -c Release

# 自检（CI 与本地都用它当门禁；退出码 0 = 通过）
dotnet run --project cross/src/ServerIsland.Desktop -c Release --no-build -- --selftest

# 界面走查（真实窗口 + 截图，输出 OK）
dotnet run --project cross/src/ServerIsland.Desktop -c Release --no-build -- --ui-smoke <临时目录>

# 打包（单文件、自包含；产物 build/ServerIsland-<版本>-win-x64.zip，内含 exe + LICENSE.txt）
bash cross/scripts/publish.sh win-x64
```

**回归验证（必须过，否则就是这个 bug 回来了）**

| 验证 | 做法 | 期望 |
|---|---|---|
| A 掉出置顶能自愈 | 运行程序后把窗口 `SetWindowPos(HWND_NOTOPMOST)`，再把一个最大化窗口抬到最前 | **≤2 秒内岛自己回来** |
| B 双击能叫出来 | 同上制造故障后双击 exe | 岛出现，且进程仍只有 1 个 |
| C 不抢焦点 | 期间在别的窗口打字 | 焦点不变 |
| D 菜单不被压 | 打开岛里的弹出菜单等 2 秒 | 菜单仍在上层 |
| E 无闪烁 | 盯 1 分钟 | 没有周期性重绘/抖动（已置顶时守卫直接返回） |
| F 桌宠模式 | 切桌宠 | 面板贴着小章鱼；隐藏时不强行弹出 |

**发版**

```bash
# 1) 改版本号：cross/Directory.Build.props 的 <Version>（正式发版时同时改 Resources/Info.plist）
# 2) 提交并打 tag（release.yml 会构建、自检、打包并发 Release）
git tag v0.5.2 && git push origin main v0.5.2
```

CI 排错技巧：`GET /repos/<owner>/<repo>/actions/runs/<run_id>/jobs` 能看到每个 step 的成败；
**编译错误的行号和原因在 `/repos/<owner>/<repo>/check-runs/<job_id>/annotations` 里**（未认证也能读公开仓库，
日志下载则需要 token）。

---

## 6. 和上游同步

```bash
git fetch upstream            # upstream = https://github.com/zhyan0603/ServerIsland.git
git merge upstream/dev        # 上游日常在 dev，main 是发布分支
```

冲突面很小：只有 `Program.cs`、`Island/Native.cs`、`Island/IslandWindow.cs` 三处（另外 `README`、
`CHANGELOG`、`Directory.Build.props` 的版本号和 fork 声明会各改一次，保留本分支的写法即可）。
合并后**必须重跑第 5 节全部回归**，再 bump 版本号发版（版本号一定要比上游当前版本大，避免和上游撞号）。

---

## 7. 已知边界（没做的事，别误以为已修）

- 只加固了 **Windows**。macOS 用 `NSWindow.level`，理论上同样会被降级，但本分支不发布 mac 版，没动。
- 上游另外两个隐患**没有修**（本分支只记录）：
  ① `Native.SetInputRegion` 在 Windows 上用 `SetWindowRgn` 裁的是**可见区域**，而 `Reposition()` 不重算 region，
  DPI/换屏后形状可能被裁没；② `IslandSurface.StartAnimation` 的 `_animating` 是无超时闩锁，丢一帧就永久冻结
  （UI 线程异常还被 `App.cs` 里 `e.Handled = true` 吞掉）。出现"形状卡住/变形"时查这两处，不是 z 序问题。
- **程序内自动更新要求仓库公开**：`UpdateChecker` 用未认证的 GitHub API，仓库一旦转私有就只会显示"还没有发布任何版本"，
  只能手动下载 zip 覆盖。
- 本分支不发布 macOS / Linux 包。
