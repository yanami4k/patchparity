# 配置脚本共用的版本、下载地址与目录布局。由其他脚本以点号引入，不单独运行。
#
# 目录布局（以 patchparity 克隆的上一级为根，缺省 D:\pp）：
#   <根>\patchparity        本仓库
#   <根>\vendor\Klotho      被测样例，锁定提交加环境补丁与注入代码
#   <根>\pigeon             Pigeon 锁定副本
#   <根>\tools\dotnet       .NET SDK（不进系统 PATH）
#   <根>\tools\python       Python（不进系统 PATH），只给 Inspect AI 用
#   <根>\inspect-venv       Inspect AI 的虚拟环境（裁决 021）
#   <根>\Unity\<版本>       Unity 编辑器
#   <根>\bin\mcp, bin\cli   自包含发布的 MCP 服务端与命令行
#   <根>\artifacts          产物与母包基线缓存
#   <根>\e\<名字>\<题号>    patchparity eval 每道题的工作目录
#   <根>\downloads          安装包
# 根路径要短：每道题的工作目录下还有 Pigeon 的会话存档与 MCP 服务端的产物，Windows 有路径长度上限。

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
# Pigeon 锁定点：裁决 020（原冻结于 65ff318，裁决 011；之后的版本移除了 pigeon eval，跑题改由 patchparity eval）
$PigeonRepo = 'https://github.com/yanami4k/pigeon.git'
$PigeonCommit = '5e70ca2'
# Unity：版本号与 changeset 取自环境补丁里的 ProjectVersion.txt
$UnityVersion = '6000.3.18f1'
$UnityChangeset = '5ebeb53e4c07'
$DotnetSdkVersion = '8.0.425'
# HybridCLR：环境补丁里包清单锁定的版本，安装后写进 libil2cpp-version.txt
$HybridClrVersion = '8.14.1'
$NodeVersion = '24.12.0'   # Pigeon 的 package.json 要求 >=22.19.0 <25
# Python：3.12 的最后一个带 Windows 安装包的版本；Inspect AI 要求 3.10 以上
$PythonVersion = '3.12.10'
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
    Python      = "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-amd64.exe"
}

# ── 目录 ──────────────────────────────────────────────────
$Paths = @{
    Downloads  = Join-Path $Root 'downloads'
    Dotnet     = Join-Path $Root 'tools\dotnet'
    DotnetExe  = Join-Path $Root 'tools\dotnet\dotnet.exe'
    PythonDir  = Join-Path $Root 'tools\python'
    PythonExe  = Join-Path $Root 'tools\python\python.exe'
    InspectVenv = Join-Path $Root 'inspect-venv'
    InspectExe = Join-Path $Root 'inspect-venv\Scripts\inspect.exe'
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

# 一批题的公共准备：生成题目目录、写 MCP 服务端的启动定义。run-eval.ps1 与 run-inspect.ps1 共用
function Initialize-EvalBatch([string]$Name, [string]$Only) {
    $cliExe = Join-Path $Paths.CliBin 'patchparity.exe'
    $streamFn = Join-Path $Paths.Pigeon 'local\deepseek-stream-fn.mjs'
    foreach ($required in @($Paths.McpExe, $cliExe, $Paths.UnityExe, $Paths.Project, $streamFn)) {
        if (-not (Test-Path $required)) { throw "找不到 $required，请先运行 install-toolchain.ps1 与 prepare-workspace.ps1" }
    }

    $out = Join-Path $Root "e\$Name"
    New-Item -ItemType Directory -Force $out | Out-Null

    # MCP 服务端的启动定义，复制成每道题工作目录里的 .mcp.json。服务端的工作目录就是题目的工作目录，路径一律写绝对路径
    $server = [ordered]@{
        command = $Paths.McpExe
        args    = @(
            '--unity', $Paths.UnityExe,
            '--project', $Paths.Project,
            '--artifacts', $Paths.Artifacts,
            '--repo', $Paths.VendorRepo,
            '--cache', $Paths.Cache
        )
    }
    $mcpConfig = Join-Path $out 'mcp-launch.json'
    # 不带 BOM 的 UTF-8：PowerShell 5.1 的 Out-File 会加 BOM，Pigeon 按 JSON 解析时不认
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllText($mcpConfig, ([ordered]@{ mcpServers = [ordered]@{ patchparity = $server } } | ConvertTo-Json -Depth 6), $utf8)

    # 题目按本机路径现场生成（含改动用例与命令行程序的绝对路径），不入库；内容是确定的，续跑时重新生成不影响已有结果
    $tasks = Join-Path $PatchParityRoot 'eval\tasks'
    $generate = @((Join-Path $PatchParityRoot 'eval\generate-tasks.mjs'), '--cli', $cliExe, '--out', $tasks)
    if ($Only) { $generate += @('--only', $Only) }
    # 输出交给控制台，不混进函数的返回值
    Invoke-Native 'node' $generate | Out-Host

    return [pscustomobject]@{ Out = $out; Tasks = $tasks; McpConfig = $mcpConfig; CliExe = $cliExe; StreamFn = $streamFn }
}
