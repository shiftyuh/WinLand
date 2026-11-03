<#
.SYNOPSIS
    把本仓库的 WinIsland（含插件）构建并安装到本机，建好开始菜单 / 桌面快捷方式。

.DESCRIPTION
    这个应用是 **Windows App SDK 自包含（self-contained）** 的（见 WinIsland.csproj 的
    WindowsAppSDKSelfContained=true），所以不需要另装 Windows App Runtime，也不需要管理员权限：
    「安装」= 把构建输出复制到一个固定目录 + 建快捷方式。

    以后改了代码，重新跑一次这个脚本就是「重新安装」，不需要重新下载源码。

.PARAMETER Configuration
    Release（默认）或 Debug。

.PARAMETER InstallDir
    安装目录，默认 %LOCALAPPDATA%\Programs\WinIsland（每用户安装，不需要 UAC）。

.PARAMETER NoBuild
    跳过构建，只复制现有输出（快速重装）。

.PARAMETER NoShortcut
    不建快捷方式。

.PARAMETER Launch
    安装完成后启动应用。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -Launch

.EXAMPLE
    # 改了代码之后：重新构建 + 覆盖安装 + 重启
    powershell -ExecutionPolicy Bypass -File tools\install-local.ps1 -Launch
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$InstallDir = "",
    [string]$DotnetPath = "",
    [switch]$NoBuild,
    [switch]$NoShortcut,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot 'WinIsland'
$buildOutputDir = Join-Path $projectDir "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
$publishDir = Join-Path $projectDir "bin\publish\$Configuration"
if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\WinIsland' }

# dotnet 不一定在 PATH 上（本机 SDK 在 D:\deepseek\_tools\dotnet）：逐级回退找一个**带 SDK 目录**的
# （PATH 里的 C:\Program Files\dotnet\dotnet.exe 可能只装了运行时、没有 SDK，选错就会报 "No .NET SDKs were found"）
function Resolve-Dotnet([string]$preferred) {
    $candidates = @()
    if ($preferred) { $candidates += $preferred }
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += 'D:\deepseek\_tools\dotnet\dotnet.exe'
    if ($env:USERPROFILE) { $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe') }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    foreach ($candidate in $candidates) {
        if (-not $candidate -or -not (Test-Path $candidate)) { continue }
        if (Test-Path (Join-Path (Split-Path $candidate) 'sdk')) { return $candidate }
    }
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw "找不到带 SDK 的 dotnet：请先装 .NET 10 SDK，或用 -DotnetPath 指定 dotnet.exe"
}

# ---- 1. 自包含发布 ----
# 注意：csproj 里的 WindowsAppSDKSelfContained=true 只让 **Windows App SDK** 自包含；
# .NET 本体仍是框架依赖（普通 dotnet build 的产物要求机器上装 .NET 10 Desktop 运行时）。
# 所以安装走 dotnet publish --self-contained true，把 .NET 运行时一起打进去，
# 目标机器不装 .NET 也能双击运行。
if (-not $NoBuild) {
    $dotnet = Resolve-Dotnet $DotnetPath
    Write-Host "发布 $Configuration（自包含，含 .NET 运行时）…" -ForegroundColor Cyan
    Push-Location $projectDir
    try {
        $publishArgs = @('publish', $projectDir, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
            '-p:PublishReadyToRun=false', '-o', $publishDir, '-v', 'm')
        $output = & $dotnet @publishArgs 2>&1 | Out-String
        $code = $LASTEXITCODE

        # DSH 沙箱会拒读 %APPDATA%\Roaming\NuGet（目录带 deny ACE）；把 APPDATA/USERPROFILE 临时指到工作区再试一次
        if ($code -ne 0 -and $output -match 'NuGet\.Config|NuGet\.Config|AppData\\Roaming\\NuGet') {
            Write-Host "  检测到用户 NuGet 配置不可读，改用工作区临时 APPDATA 重试…" -ForegroundColor Yellow
            $tempProfile = Join-Path $repoRoot '.dotnet-temp-profile'
            New-Item -ItemType Directory -Force -Path (Join-Path $tempProfile 'appdata'), (Join-Path $tempProfile 'home') | Out-Null
            $savedAppData = $env:APPDATA
            $savedUserProfile = $env:USERPROFILE
            $savedHome = $env:HOME
            try {
                $env:APPDATA = Join-Path $tempProfile 'appdata'
                $env:USERPROFILE = Join-Path $tempProfile 'home'
                $env:HOME = $env:USERPROFILE
                $output = & $dotnet @publishArgs 2>&1 | Out-String
                $code = $LASTEXITCODE
            } finally {
                $env:APPDATA = $savedAppData
                $env:USERPROFILE = $savedUserProfile
                $env:HOME = $savedHome
            }
        }

        if ($code -ne 0) {
            Write-Host $output -ForegroundColor DarkGray
            throw "发布失败（exit $code）"
        }
    } finally {
        Pop-Location
    }
}

$sourceExe = Join-Path $publishDir 'WinIsland.exe'
if (-not (Test-Path $sourceExe)) {
    throw "找不到发布输出：$sourceExe（先去掉 -NoBuild 发布一次，或检查 Configuration）"
}

# 插件目录不在 publish 的默认输出项里：从构建输出补过去（宿主扫描的是 <安装目录>\plugins）
$buildPlugins = Join-Path $buildOutputDir 'plugins'
$publishPlugins = Join-Path $publishDir 'plugins'
if (Test-Path $buildPlugins) {
    & robocopy $buildPlugins $publishPlugins /E /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "复制插件失败（robocopy exit $LASTEXITCODE）" }
}

# ---- 2. 停掉正在运行的实例（否则文件被占用，复制会失败）----
$running = Get-Process -Name 'WinIsland' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "停止正在运行的 WinIsland（$($running.Count) 个进程）…" -ForegroundColor Yellow
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 800
}

# ---- 3. 复制到安装目录 ----
Write-Host "安装到 $InstallDir …" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# 刻意不用 /MIR（镜像会删掉用户自己丢进 plugins\ 的插件）：只覆盖同名文件，多余文件保留
& robocopy $publishDir $InstallDir /E /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
$copyCode = $LASTEXITCODE
if ($copyCode -ge 8) { throw "复制失败（robocopy exit $copyCode）" }

$installedExe = Join-Path $InstallDir 'WinIsland.exe'
if (-not (Test-Path $installedExe)) { throw "复制后找不到 $installedExe" }

# ---- 4. 快捷方式 ----
if (-not $NoShortcut) {
    $shell = New-Object -ComObject WScript.Shell
    $targets = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        [Environment]::GetFolderPath('Desktop')
    )
    foreach ($dir in $targets) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }
        $linkPath = Join-Path $dir 'WinIsland.lnk'
        $link = $shell.CreateShortcut($linkPath)
        $link.TargetPath = $installedExe
        $link.WorkingDirectory = $InstallDir
        $link.IconLocation = "$installedExe,0"
        $link.Description = 'WinIsland 灵动岛（自编译版）'
        $link.Save()
        Write-Host "快捷方式：$linkPath" -ForegroundColor DarkGray
    }
}

# ---- 5. 插件检查（宿主启动时扫描 <安装目录>\plugins）----
$pluginsDir = Join-Path $InstallDir 'plugins'
if (Test-Path $pluginsDir) {
    $pluginIds = Get-ChildItem $pluginsDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'plugin.json') } |
        Select-Object -ExpandProperty Name
    if ($pluginIds) { Write-Host "随包插件：$($pluginIds -join '、')" -ForegroundColor DarkGray }
}

Write-Host ""
Write-Host "安装完成：$installedExe" -ForegroundColor Green
Write-Host "设置与日志：$(Join-Path $env:LOCALAPPDATA 'WinIsland')" -ForegroundColor DarkGray

# ---- 6. 启动 ----
if ($Launch) {
    Write-Host "启动 WinIsland …" -ForegroundColor Cyan
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir
}
