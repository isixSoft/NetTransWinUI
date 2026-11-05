# NetTrans 本地调试环境搭建指南（Windows 11）

> 目标：从一台全新的 Windows 11 机器，到能在 Visual Studio 2022 里断点调试
> WinUI 界面和下载内核（NetTrans.Kernel），并能用命令行跑全部 11 个测试套件。
>
> 配套脚本：`scripts/Setup-LocalDebug.ps1`（D 盘空间不够时把缓存类目录搬到
> E 盘，默认干跑，见文末「磁盘空间紧张怎么办」）。

---

## 0. 先看全局：14 个工程，谁能编、在哪编

`NetTrans.sln` 共 14 个工程，按「构建位置」分三类：

| 工程 | 类型 / TFM | 能否用纯 `dotnet` CLI 编 | 说明 |
| --- | --- | --- | --- |
| **NetTrans** | WinUI 3 桌面应用，`net8.0-windows10.0.19041.0`（另有 `net10.0-windows10.0.19041.0` 双目标） | ❌ 仅 Windows（VS 2022 或 SDK+Windows App SDK 工具链） | Unpackaged（`WindowsPackageType=None`）、`SelfContained=true`、`WindowsAppSDKSelfContained=true`；平台只有 x86/x64/ARM64 |
| **NetTrans.Kernel** | 纯 `net8.0` 类库，零 NuGet | ✅ 任何平台 | 下载内核，零 UI 引用，边界由 csproj 保证 |
| **TestKit** | `net8.0` 类库，零 NuGet | ✅ 任何平台 | 共享断言基类（`That`/`Equal`/`Check`），被 7 个套件继承 |
| **tests/ 下 11 个套件** | 全部 `net8.0` 控制台 Exe，零 NuGet | ✅ 任何平台 | 不是 xUnit，**不能用 `dotnet test`**，用 `dotnet <name>.dll` 直跑 |

11 个测试套件清单：

```
EndToEndTests  ExternalEngineTests  FileManagementTests  FtpE2ETests
FuzzTests      NetworkTests         ProxyTests           ScenarioTests
SetupTests     StressTests          UiLogicTests
```

结论：**不想装 VS 也能干活**——Kernel 和全部测试在 Linux/macOS/Windows 的
`dotnet` CLI 下都能构建运行（仓库就是在无 Windows 工具链的环境下编写的）。
只有 WinUI 主工程非 Windows + VS 不可。

---

## 1. 安装环境（一次性）

按顺序装以下三样（PowerShell，`winget` 可用；没有 winget 就手动下安装包）：

### 1.1 .NET 8 SDK（必装）

```powershell
winget install Microsoft.DotNet.SDK.8
```

- 版本 8.0.4xx 及以上即可。
- Kernel、TestKit、11 个测试套件只依赖它。
- `net10.0-windows10.0.19041.0` 这个 TFM 是可选的：不装 .NET 10 SDK 也能用，
  但构建时要用 `-f` 明确指定 net8.0 目标（见下文），否则会尝试构建双目标。

### 1.2 Visual Studio 2022（调试 WinUI / 内核必装）

VS 2022 17.8+（VS 2026 也可以）。最小安装两个工作负载：

```powershell
winget install Microsoft.VisualStudio.2022.Community --override "--quiet --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.Workload.UniversalBuildTools --includeRecommended"
```

- **.NET 桌面开发**（`ManagedDesktop`）：含 .NET 调试器、MSBuild。
- **Windows 应用程序开发**（`UniversalBuildTools`）：含 WinUI 3 / Windows App
  SDK 的 C# 模板与构建任务。
- 建议在「单个组件」里确认勾选 **Windows 10 SDK (10.0.19041.0)**——主工程的
  `TargetPlatformMinVersion` 是 10.0.17763，目标平台 10.0.19041。

### 1.3 Windows App SDK 运行时（**不需要单独安装**）

`NetTrans.csproj` 已固定：

```xml
<PackageReference Include="Microsoft.WindowsAppSDK" Version="2.2.0" />
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
<SelfContained>true</SelfContained>
<WindowsPackageType>None</WindowsPackageType>
```

即：Windows App SDK 以 NuGet 包形式随构建还原、自带 .NET 运行时、Unpackaged
（不需要 MSIX 包，也不需要开发者模式）。装好 1.1 和 1.2 之后**无需**再装
Windows App SDK 安装器或运行时。

### 1.4 验证

```powershell
dotnet --version          # 8.0.x
dotnet --list-sdks
git clone <你的仓库> NetTrans ; cd NetTrans   # 或已有代码则直接 cd
dotnet build NetTrans.Kernel/NetTrans.Kernel.csproj   # 第一个验证构建
```

---

## 2. 构建

在仓库根目录（`NetTrans.sln` 所在处）：

```powershell
:: 内核（任何平台、任何目录都能编）
dotnet build NetTrans.Kernel\NetTrans.Kernel.csproj

:: 全部测试套件（逐个构建，见下节）
:: WinUI 主工程（仅 Windows；必须 -f 指定 TFM、-r 指定 RID）
dotnet build NetTrans\NetTrans.csproj -f net8.0-windows10.0.19041.0 -r win-x64
```

注意：

- **不要**直接 `dotnet build NetTrans.sln`（不带参数）：它会尝试同时构建
  `net10.0-windows10.0.19041.0` 目标，没装 .NET 10 SDK 会失败。带目标构建：

  ```powershell
  dotnet build NetTrans.sln -f net8.0-windows10.0.19041.0 -r win-x64
  ```

- RID 写 `win-x64`，**不是** `win10-x64`（.NET 8+ 用 portable RID graph）。
- 平台没有 Any CPU：解决方案只定义了 x86 / x64 / ARM64。
- 首次构建 WinUI 工程会比较慢：`SelfContained` + `WindowsAppSDKSelfContained`
  会把 .NET 运行时包和 Windows App SDK 全部拉进 NuGet 缓存（GB 级）。

在 Visual Studio 里：打开 `NetTrans.sln` → 配置选 **Debug**、平台选 **x64** →
直接 F5 即可（构建由 VS 接管）。

---

## 3. 跑测试（11 个套件，全部可复制）

每个套件都是普通 net8.0 控制台程序：共享的 `TestKit` 提供 throw 风格断言，
跑完输出一行汇总，**退出码非 0 = 有失败**。两种跑法：

### 3.1 单个套件

```powershell
# 跑法 A：构建 + dll 直跑（推荐，最快）
dotnet build tests\NetworkTests\NetworkTests.csproj
dotnet tests\NetworkTests\bin\Debug\net8.0\NetworkTests.dll

# 跑法 B：dotnet run（等价）
dotnet run --project tests\NetworkTests
```

### 3.2 一键跑全部 11 个（PowerShell）

```powershell
$suites = @(
  'EndToEndTests','ExternalEngineTests','FileManagementTests','FtpE2ETests',
  'FuzzTests','NetworkTests','ProxyTests','ScenarioTests','SetupTests',
  'StressTests','UiLogicTests'
)
$failed = @()
foreach ($s in $suites) {
  dotnet build "tests\$s\$s.csproj" -v q --nologo
  if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED: $s" -ForegroundColor Red; $failed += $s; continue }
  Write-Host "=== $s ===" -ForegroundColor Cyan
  dotnet "tests\$s\bin\Debug\net8.0\$s.dll"
  if ($LASTEXITCODE -ne 0) { Write-Host "TEST FAILED: $s" -ForegroundColor Red; $failed += $s }
}
if ($failed) { Write-Host ("未通过: " + ($failed -join ', ')) -ForegroundColor Red } else { Write-Host '全部通过' -ForegroundColor Green }
```

各套件覆盖内容速查：`UiLogicTests`（TaskManager 镜像/VM/设置）、
`NetworkTests`（协议探测/驱动，本地 HTTP 服务器）、`FileManagementTests`
（保存路径/断点生命周期）、`EndToEndTests`（本地服务器全链路多段下载、
SHA-256、续传、HLS/DASH）、`SetupTests`（AppSettings→KernelOptions）、
`ProxyTests`（SOCKS5 RFC1929 握手）、`ExternalEngineTests`（外部引擎槽）、
`FtpE2ETests` / `FuzzTests` / `StressTests` / `ScenarioTests` 同理按名索骥。

---

## 4. 调试内核（NetTrans.Kernel）

内核是纯 net8.0 库，测试套件通过 `ProjectReference` 直接引用它，所以断点
「开箱即用」：

### 4.1 方式一：VS 里 F5 测试工程（推荐）

1. 打开 `NetTrans.sln`。
2. 解决方案资源管理器 → 右键 `tests\NetworkTests`（或任何想调的套件）→
   **设为启动项目**。
3. 在 `NetTrans.Kernel` 的任意源码处打断点（如 `RealDownloadKernel.cs`、
   `Transfer/TransferSession.cs`、`Protocol/Drivers/HttpDriver.cs`）。
4. 按 **F5**。VS 会先构建该套件的依赖链（含 Kernel），再启动控制台并命中断点。

> 没有 `dotnet test` / 测试资源管理器可用——套件不是 xUnit，这是刻意的
> 零 NuGet 设计。断点 + F5 + 控制台输出就是本仓库的调试回路。

### 4.2 方式二：命令行跑 dll，VS 附加进程

```powershell
dotnet tests\EndToEndTests\bin\Debug\net8.0\EndToEndTests.dll
```

VS 菜单：**调试 → 附加到进程** → 类型选「托管 (.NET Core for .NET 5+)」→
选中命令行里那个 `dotnet.exe`（多个 dotnet.exe 时，用任务管理器「命令行」列
确认哪个加载了目标 dll）→ 附加后断点同样生效。

### 4.3 调试技巧

- 内核入口：`RealDownloadKernel`（优先级调度、tick、SHA-256 校验、断点续传），
  协议面在 `Protocol/`（detect → probe → plan），传输面在 `Transfer/`。
- 断点续传状态文件是目标文件旁的 `.nettrans.json`，排查续传问题时直接看它。
- 想模拟网络侧失败/慢速，优先看 `Transfer/SpeedLimiter.cs` 与
  `Transfer/MirrorSourceSet.cs`（镜像降级）。

---

## 5. 调试 WinUI 主工程（NetTrans）

1. **启动项目**：右键 `NetTrans` → 设为启动项目。
2. **平台**：工具栏配置下拉选 **Debug / x64**（没有 Any CPU，别选错）。
3. **调试目标**：保持默认的「项目」即可——工程是 Unpackaged
   （`WindowsPackageType=None`），F5 直接启动 exe，**无需部署 MSIX**。
4. **F5**。首次 F5 前的构建较慢（SelfContained 运行时还原），之后很快。
5. 想在没有网络的情况下看 UI 动效：`App.xaml.cs` 里的 `UseRealKernel` 开关切
   回 `false`（用定时器驱动的 `SimulatedDownloadKernel`，种子数据与设计稿一致）；
   调真实下载逻辑时再切回 `true`。

调试目标注意：不要选「已安装的应用包」之类目标（那是打包应用的工作流）；
本工程 XAML 热重载可用，但改 `Interop/`（Win32 子类化）后建议重启调试会话。

---

## 6. 常见坑清单

| # | 坑 | 症状 / 解法 |
| --- | --- | --- |
| 1 | `AllowUnsafeBlocks` | WinUI 工程的 `NativeMethods` 用 `[LibraryImport]`，源生成桩是 unsafe 代码；Kernel 曾同理。csproj 已修。若换分支遇到 `CS0227`，检查对应 csproj 是否带 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` |
| 2 | 平台选了 Any CPU | 解决方案只定义 x86/x64/ARM64，WinUI 工程在 Any CPU 下无法构建/启动 → 工具栏选 **x64** |
| 3 | 非 Windows 构建 WinUI | `net8.0-windows10.0.19041.0` + WinUI 目标只能在 Windows 编。Linux/macOS 只编 Kernel + tests（仓库即如此维护） |
| 4 | `dotnet build` 未指定 TFM | 双目标 TFM（net8.0/net10.0）没装 .NET 10 SDK 会报 NETSDK1045 之类 → 一律 `-f net8.0-windows10.0.19041.0 -r win-x64` |
| 5 | RID 写错 | 用 `win-x64`，不是 `win10-x64`（.NET 8 起 portable graph） |
| 6 | 试图 `dotnet test` | 测试不是 xUnit/MSTest，`dotnet test` 无效 → 用 `dotnet <name>.dll` 直跑，退出码判成败 |
| 7 | 首次构建巨慢/占盘大 | SelfContained + WindowsAppSDKSelfContained 往 NuGet 缓存塞 GB 级运行时包。D 盘/用户目录紧张时用 `scripts/Setup-LocalDebug.ps1` 把缓存搬到 E 盘 |
| 8 | 首次 XAML 编译报错 | 仓库是在无 Windows 工具链环境下编写的，预期有 typo 级 XAML/C# 错误；重点先看 `Resources/Styles/Shadows.xaml`（CommunityToolkit.WinUI.Media 阴影 API）和 `Interop/`（Win32 互调），README 已预警 |
| 9 | VS 加载慢/扩展异常 | 删 `%LOCALAPPDATA%\Microsoft\VisualStudio\<实例id>\ComponentModelCache` 即自动重建（搬迁脚本会 junction 该缓存，不影响此操作） |
| 10 | `%TEMP%` 被占用导致搬迁跳过 | 搬 `%TEMP%` 前关闭 VS/浏览器等大占用程序；脚本遇改名失败会整项跳过并回退，不丢数据 |
| 11 | 续传不生效 | 检查目标文件旁 `.nettrans.json` 是否被删；size/ETag 校验不过会拒绝续传（防损坏），属预期行为 |
| 12 | BT/eD2k 任务失败 | 未配置外部引擎（`KernelOptions.EngineRunner` 或命令模板）时会「诚实失败」而非静默回退，属设计行为；配置方法见 README |

---

## 7. 磁盘空间紧张怎么办（D 盘 → E 盘）

配套脚本 `scripts/Setup-LocalDebug.ps1`（PowerShell 5.1+，需以管理员运行），
把一组**纯缓存类**目录从源盘搬到 E 盘并在原路径建 junction，程序无感知：

```powershell
# 1) 先干跑（默认就是干跑，只打印将做什么 + 预估体积，不改任何文件）
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-LocalDebug.ps1

# 2) 确认无误后实际执行
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-LocalDebug.ps1 -Apply

# 3) 需要回滚时（删 junction，.bak 复原，E 盘副本保留）
powershell -ExecutionPolicy Bypass -File .\scripts\Setup-LocalDebug.ps1 -Restore
```

要点：全程零删除（只复制→校验→改名 `.bak`→建 junction）；清单只含 NuGet
包缓存、NuGet HTTP 缓存、`~/.dotnet`、VS 组件缓存、`%TEMP%`、pip/npm 缓存等，
绝不触碰文档/下载/桌面、Windows、Program Files、WindowsApps 与
`ProgramData\Package Cache`。详细条目、风险声明与 `-MinFreeGB` 语义见脚本头注释。
