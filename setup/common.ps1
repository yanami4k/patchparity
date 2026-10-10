# 配置脚本共用的版本、下载地址与目录布局。由其他脚本以点号引入，不单独运行。
#
# 目录布局（以 patchparity 克隆的上一级为根，缺省 D:\pp）：
#   <根>\patchparity        本仓库
#   <根>\vendor\Klotho      被测样例，锁定提交加环境补丁与注入代码
#   <根>\pigeon             Pigeon 冻结副本
#   <根>\tools\dotnet       .NET SDK（不进系统 PATH）
#   <根>\Unity\<版本>       Unity 编辑器
#   <根>\bin\mcp, bin\cli   自包含发布的 MCP 服务端与命令行
#   <根>\artifacts          产物与母包基线缓存
#   <根>\downloads          安装包
# 根路径要短：pigeon eval 的工作树路径约为输出目录长度加 85 个字符，Windows 有路径长度上限。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$PatchParityRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Root = Split-Path $PatchParityRoot -Parent

# ── 锁定的版本 ────────────────────────────────────────────
# patchparity 本身
$PatchParityRepo = 'https://github.com/yanami4k/patchparity.git'
# 被测样例：裁决 002
$KlothoRepo = 'https://github.com/xpTURN/Klotho.git'
$KlothoCommit = 'e29c4892c88db097f3ac0d135874c82e9b6726d6'
# Pigeon 冻结点：裁决 011（已确认在 GitHub main 的历史里）
$PigeonRepo = 'https://github.com/yanami4k/pigeon.git'
$PigeonCommit = '65ff318'
# Unity：版本号与 changeset 取自环境补丁里的 ProjectVersion.txt
$UnityVersion = '6000.3.18f1'
$UnityChangeset = '5ebeb53e4c07'
$DotnetSdkVersion = '8.0.425'
$NodeVersion = '24.12.0'   # Pigeon 的 .nvmrc
$GitVersionTag = 'v2.56.0.windows.2'
$GitInstallerName = 'Git-2.56.0.2-64-bit.exe'

# ── 下载地址（2026-10-10 逐个核实可用）────────────────────
$Urls = @{
    UnityEditor = "https://download.unity3d.com/download_unity/$UnityChangeset/Windows64EditorInstaller/UnitySetup64-$UnityVersion.exe"
    UnityIl2cpp = "https://download.unity3d.com/download_unity/$UnityChangeset/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-$UnityVersion.exe"
    UnityHub    = 'https://public-cdn.cloud.unity3d.com/hub/prod/UnityHubSetup-x64.exe'
    Node        = "https://nodejs.org/dist/v$NodeVersion/node-v$NodeVersion-x64.msi"
    DotnetInstall = 'https://dot.net/v1/dotnet-install.ps1'
    VsBuildTools = 'https://aka.ms/vs/17/release/vs_BuildTools.exe'
    Git         = "https://github.com/git-for-windows/git/releases/download/$GitVersionTag/$GitInstallerName"
}

# ── 目录 ──────────────────────────────────────────────────
$Paths = @{
    Downloads  = Join-Path $Root 'downloads'
    Dotnet     = Join-Path $Root 'tools\dotnet'
    DotnetExe  = Join-Path $Root 'tools\dotnet\dotnet.exe'
    UnityDir   = Join-Path $Root "Unity\$UnityVersion"
    UnityExe   = Join-Path $Root "Unity\$UnityVersion\Editor\Unity.exe"
    VendorRepo = Join-Path $Root 'vendor\Klotho'
    Project    = Join-Path $Root 'vendor\Klotho\Samples\Brawler'
    Pigeon     = Join-Path $Root 'pigeon'
    McpBin     = Join-Path $Root 'bin\mcp'
    McpExe     = Join-Path $Root 'bin\mcp\patchparity-mcp.exe'
    CliBin     = Join-Path $Root 'bin\cli'
    Artifacts  = Join-Path $Root 'artifacts'
    Cache      = Join-Path $Root 'artifacts\baseline-cache'
    SetupLogs  = Join-Path $Root 'artifacts\setup'
}

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==> $text" -ForegroundColor Cyan
}

# 外部命令失败一律中止：PowerShell 5.1 不会因为原生程序返回非零而抛错
function Invoke-Native([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$exe $($arguments -join ' ') 失败，退出码 $LASTEXITCODE"
    }
}

function Get-Download([string]$url, [string]$fileName) {
    New-Item -ItemType Directory -Force $Paths.Downloads | Out-Null
    $target = Join-Path $Paths.Downloads $fileName
    if (Test-Path $target) {
        Write-Host "  已下载：$target"
        return $target
    }
    Write-Host "  下载 $url"
    $partial = "$target.partial"

    # 优先用系统自带的 curl.exe：带重试与断点续传。Unity 编辑器安装包有好几个 GB，
    # Invoke-WebRequest 中途断线只能从头再来（本机演练实测断过）
    $curl = Join-Path $env:SystemRoot 'System32\curl.exe'
    if (Test-Path $curl) {
        # 断线重连由这里循环，每次从断点续传：curl 的 --retry-all-errors 要 7.71 以上，系统自带的版本不一定够
        for ($attempt = 1; $attempt -le 6; $attempt++) {
            & $curl --fail --location --silent --show-error --retry 5 --retry-delay 5 --continue-at - --output $partial $url
            if ($LASTEXITCODE -eq 0) {
                Move-Item $partial $target
                return $target
            }
            Write-Host "  第 $attempt 次下载中断（curl 退出码 $LASTEXITCODE），从断点续传"
            Start-Sleep -Seconds 10
        }
        throw "下载失败：$url。重跑脚本会从断点续传"
    }
    # 关掉进度条：PowerShell 5.1 画进度条会让大文件下载慢上数倍
    $global:ProgressPreference = 'SilentlyContinue'
    # 缺省为 SystemDefault 时不动，由系统决定协议：直接设成 Tls12 会关掉更新的协议，dotnet-install 的下载随之失败
    # （本机演练实测）。SystemDefault 数值为 0，按位或上 Tls12 同样等于只留 Tls12，所以只在老系统列出具体协议时才追加
    $protocol = [Net.ServicePointManager]::SecurityProtocol
    if ($protocol -ne [Net.SecurityProtocolType]::SystemDefault) {
        [Net.ServicePointManager]::SecurityProtocol = $protocol -bor [Net.SecurityProtocolType]::Tls12
    }
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    Move-Item $partial $target
    return $target
}
