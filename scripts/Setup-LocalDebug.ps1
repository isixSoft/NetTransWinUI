<#
.SYNOPSIS
    NetTrans 本地调试环境辅助脚本：把源盘上「纯缓存类」目录备份（移动）到目标盘，
    并在原路径建立 NTFS junction，程序无感知；支持 -Restore 一键回滚。

.DESCRIPTION
    适用场景：D 盘空间不够（.NET/VS 的缓存动辄 GB 级），需要把可再生的缓存目录
    搬到空间充足的其他盘。脚本流程（对清单中每一项）：

        复制(robocopy) -> 校验(文件数+字节数) -> 原目录改名 *.bak ->
        在原路径建 junction 指向新位置 -> 记录状态文件

    任何一步失败都会回退（*.bak 改回原名），绝不留下半成品。

    【内置「可移动」清单】（仅缓存类，逐条注明作用与为何可移动）：

      1. NuGetPackages      NuGet 全局包缓存（%USERPROFILE%\.nuget\packages，
                            或 NUGET_PACKAGES 环境变量所指目录）。
                            作用：dotnet restore / VS 还原后的包解压缓存，是
                            .NET 开发占盘大头（NetTrans 的 WinUI 工程为
                            SelfContained，还原体积尤其大）。
                            为何可移动：纯缓存，删掉也会在下次 restore 时重新
                            下载重建；junction 后 NuGet/VS 沿原路径读写，无感知。
      2. NuGetHttpCache     %LOCALAPPDATA%\NuGet（含 v3-cache HTTP 缓存、
                            plugins-cache 凭据插件缓存、scratch）。
                            为何可移动：纯下载/插件缓存，可随时重建；用户级
                            NuGet.Config 在 %APPDATA%\NuGet，不在此目录。
      3. DotnetCliHome      %USERPROFILE%\.dotnet（CLI 首启标记、遥测、状态，
                            以及 `dotnet tool install -g` 的全局工具）。
                            为何可移动：SDK/运行时本体在 Program Files，不在
                            这里；junction 后 PATH 中 ~\.dotnet\tools 仍有效。
      4. VsComponentModelCache  %LOCALAPPDATA%\Microsoft\VisualStudio\*\ComponentModelCache
                            （各 VS 实例的 MEF 组件模型缓存）。
                            为何可移动：典型缓存——损坏即自动重建，VS 官方
                            排障步骤就是删掉它。
      5. VsInstallerDownloadCache  %ProgramData%\Microsoft\VisualStudio\Packages
                            （VS 安装器的安装包下载缓存）。【可选，默认关闭，
                            需 -IncludeOptional】
                            为何可移动：只是下载缓存（官方 --nocache 即不保留）。
                            默认关闭是因为 VS 安装器对它有校验行为、风险略高；
                            junction 不删数据、可随时 -Restore，故列为可选项。
                            注意：绝不碰 %ProgramData%\Package Cache——那是 VS
                            安装器修复/卸载所需数据，动了会破坏 VS。
      6. UserTemp           用户临时目录（%TEMP%，通常 %LOCALAPPDATA%\Temp）。
                            为何可移动：按定义即可丢弃；被占用的文件复制时会
                            跳过、改名失败则整项跳过回退，不丢任何数据。
      7. PipCache           %LOCALAPPDATA%\pip\cache（pip 的 wheel/HTTP 下载
                            缓存）。为何可移动：纯下载缓存，可重建。
      8. NpmCache           %LOCALAPPDATA%\npm-cache（npm 的 _cacache 包缓存）。
                            为何可移动：`npm cache verify` 可重建，纯缓存。

    【绝不包含】（安全边界）：用户的 文档/下载/桌面/图片/视频 等任何个人目录；
    C:\Windows；Program Files（含 Program Files\WindowsApps）；任何程序的安装
    目录；%ProgramData%\Package Cache；任何非缓存性质的数据目录。

    【风险声明】
      * 本脚本全程零删除：只做 复制 / 校验 / 改名 .bak / 建 junction / 记日志。
        空间不会立刻释放——*.bak 仍占着源盘，请观察几天确认一切正常后，
        自行手动删除 *.bak 才真正腾出空间。-Restore 回滚后 E 盘副本同样保留
        不动，也由你自行删除。
      * junction 需要写目标目录权限，且 ProgramData 相关项需要管理员权限，
        因此 -Apply / -Restore 要求以管理员身份运行（脚本自检）。
      * 执行前请关闭 Visual Studio、VS Installer 与 dotnet 进程；%TEMP% 若被
        占用会整项跳过并回退，不丢数据。
      * 建议第一次一定先干跑（默认即是），看清清单与预估体积再加 -Apply。

.PARAMETER Drive
    源盘盘符（默认 D:）。清单中只有「位于该盘」的目录会被处理，用于保证
    不动其他盘上的东西。注意：若这些缓存实际都在 C 盘（最常见），请用
    -Drive C:，或加 -All 处理清单中所有已存在的项。

.PARAMETER TargetDrive
    目标盘盘符（默认 E:）。数据新位置为 <TargetDrive>\NetTransCacheMove\。

.PARAMETER MinFreeGB
    目标盘安全阈值（GB，默认 10）：开始前目标盘剩余空间必须不低于该值；
    每搬一项前还要求搬完该项后剩余空间仍不低于该值，否则跳过该项。

.PARAMETER Apply
    实际执行搬移。缺省 = 干跑（只打印计划与预估体积，不改任何文件）。

.PARAMETER Restore
    回滚模式：删除 junction（只删联接本身，绝不删 E 盘数据），把 *.bak
    改回原名。

.PARAMETER IncludeOptional
    连同默认关闭的可选项（VsInstallerDownloadCache）一起处理。

.PARAMETER All
    不做源盘过滤，处理清单中所有已存在的项。

.PARAMETER StateFile
    状态文件路径（默认与脚本同目录的 NetTransCacheMove.state.json），
    记录已成功的搬移，供 -Restore 精确回滚。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1
    # 默认干跑：只打印将做什么 + 预估体积，不改动任何文件

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -Apply
    # 实际执行（默认 D: -> E:，目标盘至少保留 10GB）

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -Apply -Drive D: -TargetDrive E: -MinFreeGB 20
    # 自定义源盘 / 目标盘 / 阈值

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -Apply -IncludeOptional
    # 连同可选项（VS 安装器下载缓存）一起搬

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -Apply -WhatIf
    # 标准 -WhatIf 双保险：即使加了 -Apply 也只打印不执行

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -Restore
    # 回滚：删 junction、*.bak 复原；E 盘副本保留，确认后自行删除

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Setup-LocalDebug.ps1 -All
    # 不做源盘过滤，列出/处理清单中所有已存在的项

.NOTES
    兼容 Windows PowerShell 5.1+（同样适用于 PowerShell 7+）。
    日志：与脚本同目录的 Setup-LocalDebug_<时间戳>.log。
    配套文档：docs/LocalDebugSetup.md（NetTrans 本地调试环境搭建指南）。
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [ValidatePattern('^[A-Za-z]:\\?$')]
    [string]$Drive = 'D:',

    [ValidatePattern('^[A-Za-z]:\\?$')]
    [string]$TargetDrive = 'E:',

    [ValidateRange(1, 2048)]
    [int]$MinFreeGB = 10,

    [switch]$Apply,

    [switch]$Restore,

    [switch]$IncludeOptional,

    [switch]$All,

    [string]$StateFile = ''
)

# ---------------------------------------------------------------------------
# 全局设置
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$script:ScriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$script:LogPath = Join-Path $script:ScriptDir ("Setup-LocalDebug_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
$script:StatePath = if ($StateFile -ne '') { $StateFile } else { Join-Path $script:ScriptDir 'NetTransCacheMove.state.json' }

function Write-Log {
    param([string]$Message, [string]$Level = 'INFO')
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] [{1,-5}] {2}" -f (Get-Date), $Level, $Message
    $colors = @{ INFO = 'Gray'; OK = 'Green'; WARN = 'Yellow'; ERROR = 'Red'; STEP = 'Cyan' }
    $color = 'White'
    if ($colors.ContainsKey($Level)) { $color = $colors[$Level] }
    Write-Host $line -ForegroundColor $color
    Add-Content -LiteralPath $script:LogPath -Value $line -Encoding UTF8
}

function ConvertTo-DriveRoot {
    param([string]$Value)
    $v = $Value.Trim().TrimEnd('\')
    if ($v.Length -eq 1) { $v = $v + ':' }
    return $v.ToUpper()
}

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DriveFreeBytes {
    param([string]$DriveRoot)
    $name = $DriveRoot.TrimEnd(':')
    $psd = Get-PSDrive -Name $name -ErrorAction Stop
    return [int64]$psd.Free
}

function Format-GB {
    param([int64]$Bytes)
    if ($Bytes -lt 0) { $Bytes = 0 }
    return ('{0:N2} GB' -f ($Bytes / 1GB))
}

function Test-ReparsePoint {
    param([string]$LiteralPath)
    try {
        $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
        return (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    } catch {
        return $false
    }
}

# ---------------------------------------------------------------------------
# 内置「可移动」清单（仅缓存类；Purpose=作用，Why=为何可移动）
# ---------------------------------------------------------------------------
function Get-MovableItems {
    $nugetPackages = Join-Path $env:USERPROFILE '.nuget\packages'
    if ($env:NUGET_PACKAGES -and $env:NUGET_PACKAGES.Trim() -ne '') {
        try { $nugetPackages = [System.IO.Path]::GetFullPath($env:NUGET_PACKAGES) } catch { }
    }

    $userTemp = Join-Path $env:LOCALAPPDATA 'Temp'
    if ($env:TEMP -and $env:TEMP.Trim() -ne '') {
        try { $userTemp = [System.IO.Path]::GetFullPath($env:TEMP) } catch { }
    }

    $items = @()

    $items += [pscustomobject]@{
        Key = 'NuGetPackages'; Path = $nugetPackages; Optional = $false; PartialOk = $false
        Purpose = 'NuGet 全局包缓存：dotnet restore / VS 还原后的包解压缓存，.NET 开发占盘大头（WinUI 工程为 SelfContained，还原体积尤其大）。'
        Why = '纯缓存：删除后下次 restore 会重新下载重建；junction 后 NuGet/VS 沿原路径读写，无感知。'
    }
    $items += [pscustomobject]@{
        Key = 'NuGetHttpCache'; Path = (Join-Path $env:LOCALAPPDATA 'NuGet'); Optional = $false; PartialOk = $false
        Purpose = 'NuGet 本地缓存目录：v3-cache（HTTP 缓存）、plugins-cache（凭据插件缓存）、scratch。'
        Why = '纯下载/插件缓存，可随时重建；用户级 NuGet.Config 在 %APPDATA%\NuGet，不在此目录。'
    }
    $items += [pscustomobject]@{
        Key = 'DotnetCliHome'; Path = (Join-Path $env:USERPROFILE '.dotnet'); Optional = $false; PartialOk = $false
        Purpose = 'dotnet CLI 状态目录：首启标记、遥测、CLI 状态，以及 dotnet tool install -g 的全局工具（~\.dotnet\tools）。'
        Why = 'SDK/运行时本体在 Program Files，不在这里；junction 后 PATH 中 ~\.dotnet\tools 仍有效。'
    }
    $items += [pscustomobject]@{
        Key = 'VsComponentModelCache'; Path = (Join-Path $env:LOCALAPPDATA 'Microsoft\VisualStudio\*\ComponentModelCache'); Optional = $false; PartialOk = $false
        Purpose = 'Visual Studio 各实例的 MEF 组件模型缓存（扩展/项目加载加速）。'
        Why = '典型缓存：损坏即自动重建，VS 官方排障步骤就是删掉它。'
    }
    $items += [pscustomobject]@{
        Key = 'VsInstallerDownloadCache'; Path = (Join-Path $env:ProgramData 'Microsoft\VisualStudio\Packages'); Optional = $true; PartialOk = $false
        Purpose = 'Visual Studio 安装器的安装包下载缓存。【可选项，默认关闭，需 -IncludeOptional】'
        Why = '只是下载缓存（官方 --nocache 即不保留）。默认关闭因 VS 安装器对它有校验行为、风险略高；junction 不删数据、可随时 -Restore。绝不碰 %ProgramData%\Package Cache（修复/卸载所需）。'
    }
    $items += [pscustomobject]@{
        Key = 'UserTemp'; Path = $userTemp; Optional = $false; PartialOk = $true
        Purpose = '用户临时目录（%TEMP%）：安装器、编译器、VS 中间输出等。'
        Why = '按定义即可丢弃；被占用文件复制时跳过，改名失败则整项跳过回退，不丢数据。'
    }
    $items += [pscustomobject]@{
        Key = 'PipCache'; Path = (Join-Path $env:LOCALAPPDATA 'pip\cache'); Optional = $false; PartialOk = $false
        Purpose = 'pip 的 wheel/HTTP 下载缓存。'
        Why = '纯下载缓存，可重建。'
    }
    $items += [pscustomobject]@{
        Key = 'NpmCache'; Path = (Join-Path $env:LOCALAPPDATA 'npm-cache'); Optional = $false; PartialOk = $false
        Purpose = 'npm 的包下载缓存（_cacache）。'
        Why = 'npm cache verify 可重建，纯缓存。'
    }

    return $items
}

# 解析清单项的实际路径（支持通配符），返回存在的目录数组
function Resolve-ItemMatches {
    param([pscustomobject]$Item)
    $found = @()
    if ($Item.Path.Contains('*') -or $Item.Path.Contains('?')) {
        $found = @(Get-Item -Path $Item.Path -Force -ErrorAction SilentlyContinue | Where-Object { $_.PSIsContainer })
    } else {
        if (Test-Path -LiteralPath $Item.Path -PathType Container) {
            $found = @(Get-Item -LiteralPath $Item.Path -Force)
        }
    }
    return @($found | ForEach-Object { $_.FullName })
}

# 统计目录：文件数 + 字节数（跳过无权限/被锁定项，与 robocopy 行为一致）
function Get-DirStats {
    param([string]$LiteralPath)
    $files = 0
    $bytes = [int64]0
    try {
        $m = Get-ChildItem -LiteralPath $LiteralPath -Recurse -Force -File -ErrorAction SilentlyContinue |
             Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue
        if ($m) {
            $files = [int]$m.Count
            if ($m.Sum) { $bytes = [int64]$m.Sum }
        }
    } catch {
        return @{ Files = -1; Bytes = [int64]-1 }
    }
    return @{ Files = $files; Bytes = $bytes }
}

# robocopy 复制整棵目录树；返回退出码（<8 = 成功，>=8 = 有失败项）
function Invoke-CopyTree {
    param([string]$Source, [string]$Dest)
    Write-Log ("robocopy `"$Source`" -> `"$Dest`"（/E /XJ /MT:8，不跟随嵌套 junction）") 'STEP'
    & robocopy.exe $Source $Dest /E /COPY:DAT /DCOPY:DAT /R:2 /W:2 /XJ /MT:8 /NFL /NDL /NP /BYTES 2>&1 |
        ForEach-Object { Write-Log ("  [robocopy] " + $_) }
    return $LASTEXITCODE
}

# 建 junction：优先 New-Item，失败回退 mklink /J
function New-DirectoryJunction {
    param([string]$LinkPath, [string]$TargetPath)
    try {
        $null = New-Item -ItemType Junction -Path $LinkPath -Value $TargetPath -ErrorAction Stop
    } catch {
        Write-Log ("New-Item 建 junction 失败，回退 mklink /J：{0}" -f $_.Exception.Message) 'WARN'
        & cmd.exe /c mklink /J "$LinkPath" "$TargetPath" 2>&1 | ForEach-Object { Write-Log ("  [mklink] " + $_) }
        if ($LASTEXITCODE -ne 0) { return $false }
    }
    return (Test-ReparsePoint -LiteralPath $LinkPath)
}

# 删 junction：cmd rmdir 只删联接本身，对非空真实目录会直接失败（天然安全）
function Remove-DirectoryJunction {
    param([string]$LinkPath)
    & cmd.exe /c rmdir "$LinkPath" 2>&1 | ForEach-Object { Write-Log ("  [rmdir] " + $_) }
    if ($LASTEXITCODE -ne 0) {
        try { [System.IO.Directory]::Delete($LinkPath, $false) } catch { return $false }
    }
    return (-not (Test-Path -LiteralPath $LinkPath))
}

# 状态文件：记录已成功的搬移，供 -Restore 精确回滚
function Save-State {
    param([object[]]$Entries)
    if (@($Entries).Count -eq 0) {
        if (Test-Path -LiteralPath $script:StatePath) {
            Remove-Item -LiteralPath $script:StatePath -Force
            Write-Log ("状态文件已清空删除：{0}" -f $script:StatePath) 'INFO'
        }
        return
    }
    @($Entries) | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:StatePath -Encoding UTF8
    Write-Log ("状态文件已更新：{0}" -f $script:StatePath) 'INFO'
}

function Load-State {
    if (-not (Test-Path -LiteralPath $script:StatePath)) { return @() }
    try {
        $raw = Get-Content -LiteralPath $script:StatePath -Raw -Encoding UTF8 | ConvertFrom-Json
        return @($raw)
    } catch {
        Write-Log ("状态文件损坏，忽略（将回退为按清单扫描）：{0}" -f $_.Exception.Message) 'WARN'
        return @()
    }
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

if ($Apply -and $Restore) {
    Write-Log '参数 -Apply 与 -Restore 不能同时使用。' 'ERROR'
    exit 1
}

$srcDrive = ConvertTo-DriveRoot -Value $Drive
$dstDrive = ConvertTo-DriveRoot -Value $TargetDrive
$dstRoot = Join-Path ($dstDrive + '\') 'NetTransCacheMove'

Write-Log '================================================================' 'STEP'
Write-Log 'Setup-LocalDebug — 缓存类目录搬移（复制 -> 校验 -> .bak -> junction）' 'STEP'
Write-Log '================================================================' 'STEP'
Write-Log ("模式            : {0}" -f $(if ($Restore) { '回滚 (-Restore)' } elseif ($Apply) { '实际执行 (-Apply)' } else { '干跑（默认，未加 -Apply）' })) 'INFO'
Write-Log ("源盘过滤        : {0}{1}" -f $srcDrive, $(if ($All) { '（-All：不过滤）' })) 'INFO'
Write-Log ("目标盘/目录     : {0}" -f $dstRoot) 'INFO'
Write-Log ("目标盘阈值      : {0} GB（-MinFreeGB）" -f $MinFreeGB) 'INFO'
Write-Log ("包含可选项      : {0}" -f $(if ($IncludeOptional) { '是' } else { '否（VsInstallerDownloadCache 默认跳过）' })) 'INFO'
Write-Log ("管理员身份      : {0}" -f $(if (Test-IsAdmin) { '是' } else { '否' })) 'INFO'
Write-Log ("PowerShell 版本 : {0}" -f $PSVersionTable.PSVersion) 'INFO'
Write-Log ("日志文件        : {0}" -f $script:LogPath) 'INFO'

if ($Restore) {
    # ------------------------------- 回滚 -----------------------------------
    if (-not (Test-IsAdmin)) {
        Write-Log '回滚需要写 ProgramData 等系统位置，请以管理员身份运行 PowerShell 后重试。' 'ERROR'
        exit 1
    }

    $state = Load-State
    $entries = @()
    foreach ($e in $state) {
        if ($e.PSObject.Properties['Source'] -and $e.Source) {
            $bak = if ($e.PSObject.Properties['Bak'] -and $e.Bak) { $e.Bak } else { ($e.Source + '.bak') }
            $entries += [pscustomobject]@{ Key = $e.Key; Source = $e.Source; Bak = $bak }
        }
    }

    if ($entries.Count -eq 0) {
        Write-Log '状态文件为空/不存在，回退为按内置清单扫描（支持通配符路径）。' 'WARN'
        foreach ($item in (Get-MovableItems)) {
            if ($item.Optional -and -not $IncludeOptional) { continue }
            foreach ($src in (Resolve-ItemMatches -Item $item)) {
                $entries += [pscustomobject]@{ Key = $item.Key; Source = $src; Bak = ($src + '.bak') }
            }
        }
    }

    if ($entries.Count -eq 0) {
        Write-Log '没有发现任何 junction / .bak，无需回滚。' 'OK'
        exit 0
    }

    $remaining = @()
    $okCount = 0; $failCount = 0
    foreach ($e in $entries) {
        Write-Log ("[{0}] 检查回滚：{1}" -f $e.Key, $e.Source) 'STEP'

        if (-not $PSCmdlet.ShouldProcess($e.Source, ("删除 junction 并把 {0} 改回原名" -f $e.Bak))) { continue }

        $hardFail = $false
        if (Test-Path -LiteralPath $e.Source) {
            if (Test-ReparsePoint -LiteralPath $e.Source) {
                if (Remove-DirectoryJunction -LinkPath $e.Source) {
                    Write-Log ("[{0}] 已删除 junction（E 盘数据未动）：{1}" -f $e.Key, $e.Source) 'OK'
                } else {
                    Write-Log ("[{0}] 删除 junction 失败：{1}" -f $e.Key, $e.Source) 'ERROR'
                    $hardFail = $true
                }
            } else {
                Write-Log ("[{0}] {1} 已是真实目录（junction 可能已被手动删除），不删除任何内容。" -f $e.Key, $e.Source) 'WARN'
            }
        }

        if (-not $hardFail) {
            if (Test-Path -LiteralPath $e.Bak) {
                if (Test-Path -LiteralPath $e.Source) {
                    Write-Log ("[{0}] 无法把 {1} 改回原名：{2} 已存在内容。请确认后手动合并（脚本不会覆盖/删除）。" -f $e.Key, $e.Bak, $e.Source) 'ERROR'
                    $hardFail = $true
                } else {
                    try {
                        Rename-Item -LiteralPath $e.Bak -NewName (Split-Path -Leaf $e.Source) -ErrorAction Stop
                        Write-Log ("[{0}] 已还原：{1} -> {2}" -f $e.Key, $e.Bak, $e.Source) 'OK'
                        $okCount++
                    } catch {
                        Write-Log ("[{0}] 还原失败：{1}" -f $e.Key, $_.Exception.Message) 'ERROR'
                        $hardFail = $true
                    }
                }
            } elseif (-not (Test-Path -LiteralPath $e.Source)) {
                Write-Log ("[{0}] {1} 与 {2} 均不存在，无事可做。" -f $e.Key, $e.Source, $e.Bak) 'WARN'
            } else {
                $okCount++
            }
        }

        if ($hardFail) { $failCount++; $remaining += $e }
    }

    if (-not $WhatIfPreference) {
        Save-State -Entries $remaining
    }
    $endLevel = 'OK'
    if ($failCount -gt 0) { $endLevel = 'WARN' }
    Write-Log ("回滚完成：成功 {0} 项，失败 {1} 项（失败项已留在状态文件，可重跑 -Restore）。" -f $okCount, $failCount) $endLevel
    Write-Log 'E 盘上的副本（NetTransCacheMove）保留未动，确认一切正常后可自行删除。' 'INFO'
    if ($failCount -gt 0) { exit 2 } else { exit 0 }
}

# --------------------------- 干跑 / 实际执行 --------------------------------
$items = Get-MovableItems
$plan = @()

foreach ($item in $items) {
    if ($item.Optional -and -not $IncludeOptional) {
        $plan += [pscustomobject]@{ Key = $item.Key; Source = $item.Path; Dest = ''; Status = 'SKIP'; Reason = '可选项未启用（-IncludeOptional）'; Files = 0; Bytes = [int64]0 }
        continue
    }

    $matched = Resolve-ItemMatches -Item $item
    if ($matched.Count -eq 0) {
        $plan += [pscustomobject]@{ Key = $item.Key; Source = $item.Path; Dest = ''; Status = 'SKIP'; Reason = '目录不存在（本机未安装/未生成）'; Files = 0; Bytes = [int64]0 }
        continue
    }

    foreach ($src in $matched) {
        $isReparse = Test-ReparsePoint -LiteralPath $src
        if ($isReparse) {
            $plan += [pscustomobject]@{ Key = $item.Key; Source = $src; Dest = ''; Status = 'SKIP'; Reason = '已是 junction/链接（幂等，跳过）'; Files = 0; Bytes = [int64]0 }
            continue
        }

        $onDrive = $All -or $src.ToLower().StartsWith(($srcDrive + '\').ToLower())
        if (-not $onDrive) {
            $plan += [pscustomobject]@{ Key = $item.Key; Source = $src; Dest = ''; Status = 'SKIP'; Reason = "不在源盘 $srcDrive 上（可用 -Drive 或 -All 调整）"; Files = 0; Bytes = [int64]0 }
            continue
        }

        if ($item.Key -eq 'VsComponentModelCache') {
            $safe = [regex]::Replace((Split-Path -Path (Split-Path -Path $src -Parent) -Leaf), '[^A-Za-z0-9._\-]', '_')
            $dest = Join-Path (Join-Path $dstRoot $item.Key) $safe
        } else {
            $dest = Join-Path $dstRoot $item.Key
        }

        if (Test-Path -LiteralPath $dest) {
            $plan += [pscustomobject]@{ Key = $item.Key; Source = $src; Dest = $dest; Status = 'SKIP'; Reason = '目标位置已存在（不覆盖 E 盘已有数据）'; Files = 0; Bytes = [int64]0 }
            continue
        }

        Write-Log ("统计体积：{0}（大目录可能需要一两分钟）" -f $src) 'INFO'
        $stats = Get-DirStats -LiteralPath $src
        if ($stats.Files -lt 0) {
            $plan += [pscustomobject]@{ Key = $item.Key; Source = $src; Dest = $dest; Status = 'SKIP'; Reason = '无法读取目录内容（权限？）'; Files = 0; Bytes = [int64]0 }
            continue
        }

        $plan += [pscustomobject]@{ Key = $item.Key; Source = $src; Dest = $dest; Status = 'READY'; Reason = ''; Files = $stats.Files; Bytes = $stats.Bytes; Purpose = $item.Purpose; Why = $item.Why }
    }
}

# 打印计划
Write-Log '----------------------------------------------------------------' 'STEP'
Write-Log ('搬移计划（内置清单共 {0} 项：7 项默认 + 1 项可选；逐条「作用 / 为何可移动」说明见脚本头注释与本清单 Purpose/Why 字段）：' -f $items.Count) 'STEP'
Write-Log '----------------------------------------------------------------' 'STEP'
$totalReady = [int64]0
$readyCount = 0
foreach ($p in $plan) {
    if ($p.Status -eq 'READY') {
        $readyCount++
        $totalReady += $p.Bytes
        Write-Log ("[将搬移] {0}  文件数 {1}  体积 {2}" -f $p.Key, $p.Files, (Format-GB -Bytes $p.Bytes)) 'OK'
        Write-Log ("         {0}" -f $p.Source) 'OK'
        Write-Log ("      -> {0}" -f $p.Dest) 'OK'
        Write-Log ("    作用       : {0}" -f $p.Purpose) 'INFO'
        Write-Log ("    为何可移动 : {0}" -f $p.Why) 'INFO'
    } else {
        Write-Log ("[跳过  ] {0}  {1}" -f $p.Key, $p.Reason) 'WARN'
        Write-Log ("         {0}" -f $p.Source) 'WARN'
    }
}
Write-Log '----------------------------------------------------------------' 'STEP'

if ($readyCount -eq 0) {
    Write-Log ("没有 READY 项。常见原因：缓存都在其他盘上（默认过滤 {0}）——请用 -Drive C: 或 -All 重新查看。" -f $srcDrive) 'WARN'
    if ($Apply) {
        Write-Log '未执行任何搬移操作，退出。' 'OK'
    } else {
        Write-Log '干跑结束，未改动任何文件。' 'OK'
    }
    exit 0
}

Write-Log ("READY 合计：{0} 项，约 {1}" -f $readyCount, (Format-GB -Bytes $totalReady)) 'STEP'

if (-not $Apply) {
    Write-Log '干跑结束（未改动任何文件）。确认无误后加 -Apply 实际执行；回滚用 -Restore。' 'OK'
    exit 0
}

# ------------------------------- 实际执行 -----------------------------------
if (-not (Test-IsAdmin)) {
    Write-Log '实际执行需要建 junction 并写 ProgramData 等位置，请以管理员身份运行 PowerShell 后重试。' 'ERROR'
    exit 1
}

if ($srcDrive -eq $dstDrive) {
    Write-Log '源盘与目标盘相同，没有搬移意义。' 'ERROR'
    exit 1
}

try {
    $dstFree = Get-DriveFreeBytes -DriveRoot $dstDrive
} catch {
    Write-Log ("目标盘 {0} 不可用：{1}" -f $dstDrive, $_.Exception.Message) 'ERROR'
    exit 1
}
Write-Log ("目标盘 {0} 剩余空间：{1}" -f $dstDrive, (Format-GB -Bytes $dstFree)) 'INFO'
if ($dstFree -lt ($MinFreeGB * 1GB)) {
    Write-Log ("目标盘剩余空间低于阈值 {0} GB，中止（可调 -MinFreeGB）。" -f $MinFreeGB) 'ERROR'
    exit 1
}

$null = New-Item -ItemType Directory -Path $dstRoot -Force

$moved = 0; $failed = 0; $skipped = 0
$stateEntries = @(Load-State)

foreach ($p in ($plan | Where-Object { $_.Status -eq 'READY' })) {

    if (-not $PSCmdlet.ShouldProcess($p.Source, ("复制到 {0} 并以 junction 替换原目录（原目录改名 .bak，绝不删除）" -f $p.Dest))) {
        $skipped++
        continue
    }

    Write-Log ("==== [{0}] 开始：{1}" -f $p.Key, $p.Source) 'STEP'

    # 空间预检：搬完该项后目标盘仍须 >= MinFreeGB
    try { $freeNow = Get-DriveFreeBytes -DriveRoot $dstDrive } catch { $freeNow = [int64]0 }
    if (($freeNow - $p.Bytes) -lt ($MinFreeGB * 1GB)) {
        Write-Log ("[{0}] 目标盘空间不足（需 {1}，搬后剩余将低于 {2} GB），跳过该项。" -f $p.Key, (Format-GB -Bytes $p.Bytes), $MinFreeGB) 'WARN'
        $skipped++
        continue
    }

    # 1) 复制（不删除、不改动源）
    $rc = Invoke-CopyTree -Source $p.Source -Dest $p.Dest
    if ($rc -ge 8) {
        Write-Log ("[{0}] robocopy 报告失败（exit={1}）。源目录未做任何改动，该项跳过；E 盘上的不完整副本可留待手动清理。" -f $p.Key, $rc) 'ERROR'
        $failed++
        continue
    }

    # 2) 校验：文件数 + 字节数
    $srcStats = Get-DirStats -LiteralPath $p.Source
    $dstStats = Get-DirStats -LiteralPath $p.Dest
    $same = ($srcStats.Files -eq $dstStats.Files) -and ($srcStats.Bytes -eq $dstStats.Bytes)
    if (-not $same) {
        if ($p.PartialOk) {
            $ratio = 1.0
            if ($srcStats.Files -gt 0) { $ratio = $dstStats.Files / $srcStats.Files }
            if (($ratio -ge 0.9) -and ($dstStats.Bytes -ge ($srcStats.Bytes * 0.9))) {
                Write-Log ("[{0}] 校验有差异（多为被占用文件跳过：源 {1}/{2} 字节 vs 目标 {3}/{4} 字节）。该项允许部分成功（PartialOk），继续；未复制到的文件仍保留在 .bak 中。" -f $p.Key, $srcStats.Files, $srcStats.Bytes, $dstStats.Files, $dstStats.Bytes) 'WARN'
            } else {
                Write-Log ("[{0}] 校验差异过大（源 {1}/{2} vs 目标 {3}/{4}），放弃该项；源目录未做任何改动。" -f $p.Key, $srcStats.Files, $srcStats.Bytes, $dstStats.Files, $dstStats.Bytes) 'ERROR'
                $failed++
                continue
            }
        } else {
            Write-Log ("[{0}] 校验失败（源 {1}/{2} vs 目标 {3}/{4}），放弃该项；源目录未做任何改动，E 盘不完整副本可手动清理。" -f $p.Key, $srcStats.Files, $srcStats.Bytes, $dstStats.Files, $dstStats.Bytes) 'ERROR'
            $failed++
            continue
        }
    } else {
        Write-Log ("[{0}] 校验通过：文件数 {1}，字节数 {2} 完全一致。" -f $p.Key, $srcStats.Files, $srcStats.Bytes) 'OK'
    }

    # 3) 原目录改名 .bak（失败则源原样保留，跳过）
    $bakPath = $p.Source + '.bak'
    if (Test-Path -LiteralPath $bakPath) {
        Write-Log ("[{0}] {1} 已存在，为保护旧备份跳过该项（请先处理旧 .bak）。" -f $p.Key, $bakPath) 'ERROR'
        $failed++
        continue
    }
    $originalLeaf = Split-Path -Leaf $p.Source
    try {
        Rename-Item -LiteralPath $p.Source -NewName ($originalLeaf + '.bak') -ErrorAction Stop
        Write-Log ("[{0}] 原目录已改名：{1} -> {2}" -f $p.Key, $p.Source, $bakPath) 'OK'
    } catch {
        Write-Log ("[{0}] 改名失败（多半有进程占用，如 VS/dotnet 正在运行）：{1}。源目录原样保留，该项跳过。" -f $p.Key, $_.Exception.Message) 'ERROR'
        $failed++
        continue
    }

    # 4) 原路径建 junction；失败则回退（.bak 改回原名）
    if (New-DirectoryJunction -LinkPath $p.Source -TargetPath $p.Dest) {
        Write-Log ("[{0}] junction 已建立：{1} -> {2}" -f $p.Key, $p.Source, $p.Dest) 'OK'
        $moved++
        $stateEntries += [pscustomobject]@{
            Key = $p.Key; Source = $p.Source; Dest = $p.Dest; Bak = $bakPath; Time = (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
        }
        Save-State -Entries $stateEntries
    } else {
        Write-Log ("[{0}] junction 创建失败，回退：把 {1} 改回原名。" -f $p.Key, $bakPath) 'ERROR'
        try {
            Rename-Item -LiteralPath $bakPath -NewName $originalLeaf -ErrorAction Stop
            Write-Log ("[{0}] 回退成功，源目录已还原。" -f $p.Key) 'OK'
        } catch {
            Write-Log ("[{0}] 回退失败！请手动把 {1} 改名为 {2}。" -f $p.Key, $bakPath, $originalLeaf) 'ERROR'
        }
        $failed++
    }
}

Write-Log '================================================================' 'STEP'
Write-Log ("执行结束：成功 {0}，失败 {1}，跳过 {2}。" -f $moved, $failed, $skipped) 'STEP'
Write-Log ("日志文件：{0}" -f $script:LogPath) 'INFO'
if ($moved -gt 0) {
    Write-Log '注意：*.bak 仍占着源盘空间（本脚本绝不删除）。观察几天确认一切正常后，自行手动删除 *.bak 才真正释放空间。' 'WARN'
    Write-Log '如需回滚：powershell -ExecutionPolicy Bypass -File <本脚本> -Restore' 'INFO'
    Write-Log 'E 盘副本与状态文件位置：' 'INFO'
    Write-Log ("  目标目录：{0}" -f $dstRoot) 'INFO'
    Write-Log ("  状态文件：{0}" -f $script:StatePath) 'INFO'
}
if ($failed -gt 0) { exit 2 } else { exit 0 }
