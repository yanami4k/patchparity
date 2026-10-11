<#
.SYNOPSIS
在装好工具链的主机上搭出可以跑验证的工作区。

.DESCRIPTION
依次完成：
  1. 被测样例：按锁定提交取 Klotho，打环境补丁，复制注入代码与 HybridCLR 设置。
  2. patchparity：跑两套测试，把 MCP 服务端与命令行发布成自包含程序。
  3. Pigeon：克隆并检出冻结点，安装依赖，放入 DeepSeek 的 stream-fn 模块。
  4. HybridCLR：用 Unity 打开工程并运行安装器。第一次打开要导入整个工程，耗时较长。
每一步先查是否已完成，已完成即跳过，可以反复运行。不需要管理员权限。

第 4 步需要 Unity 许可证已激活。只想检查前三步时加 -SkipUnity。
#>
[CmdletBinding()]
param(
    [switch]$SkipUnity,
    [switch]$SkipTests,
    # 本机演练用：从本地仓库取被测样例与 Pigeon，省去下载。云主机上不要给
    [string]$KlothoSource = '',
    [string]$PigeonSource = ''
)

. (Join-Path $PSScriptRoot 'common.ps1')

function Git-In([string]$dir, [string[]]$arguments) {
    Invoke-Native 'git' (@('-C', $dir) + $arguments)
}

function Test-GitIn([string]$dir, [string[]]$arguments) {
    # PowerShell 5.1 在 Stop 模式下把重定向的原生 stderr 当成终止错误，这里只看退出码
    $ErrorActionPreference = 'Continue'
    & git -C $dir @arguments 2>&1 | Out-Null
    return $LASTEXITCODE -eq 0
}

Write-Step '检查工具链'
foreach ($tool in @('git', 'node', 'npm')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "找不到 $tool，请先运行 install-toolchain.ps1" }
}
if (-not (Test-Path $Paths.DotnetExe)) { throw "找不到 $($Paths.DotnetExe)，请先运行 install-toolchain.ps1" }
if (-not $SkipUnity -and -not (Test-Path $Paths.UnityExe)) { throw "找不到 $($Paths.UnityExe)，请先运行 install-toolchain.ps1" }
New-Item -ItemType Directory -Force $Paths.SetupLogs | Out-Null
Write-Host "  根目录 $Root"

# ── 1. 被测样例 ───────────────────────────────────────────
Write-Step "被测样例 Klotho @ $KlothoCommit"
$repo = $Paths.VendorRepo
if (-not (Test-Path (Join-Path $repo '.git'))) {
    New-Item -ItemType Directory -Force $repo | Out-Null
    Git-In $repo @('init', '-q')
    # 换行设置必须与环境补丁、改动用例生成时一致：补丁按 autocrlf=true 的工作区制作（见 injected/environment）
    Git-In $repo @('config', 'core.autocrlf', 'true')
    Git-In $repo @('remote', 'add', 'origin', $(if ($KlothoSource) { $KlothoSource } else { $KlothoRepo }))
    # 只取锁定的那一个提交，不取历史
    Git-In $repo @('fetch', '-q', '--depth', '1', 'origin', $KlothoCommit)
    Git-In $repo @('checkout', '-q', 'FETCH_HEAD')
}
$head = (& git -C $repo rev-parse HEAD).Trim()
if ($head -ne $KlothoCommit) { throw "被测样例的 HEAD 是 $head，不是锁定的 $KlothoCommit" }
if ((& git -C $repo config core.autocrlf) -ne 'true') { throw '被测样例的 core.autocrlf 须为 true' }

$envPatch = Join-Path $PatchParityRoot 'injected\environment\environment.patch'
if (Test-GitIn $repo @('apply', '--reverse', '--check', $envPatch)) {
    Write-Host '  环境补丁已施加'
} else {
    Git-In $repo @('apply', $envPatch)
    Write-Host '  环境补丁已施加（本次）'
}

# 注入代码只覆盖不镜像：Unity 生成的 .meta 留在原处，删掉会让资产 GUID 变化
Copy-Item -Recurse -Force (Join-Path $PatchParityRoot 'injected\PatchParity') (Join-Path $Paths.Project 'Assets')
Copy-Item -Force (Join-Path $PatchParityRoot 'injected\environment\ProjectSettings\HybridCLRSettings.asset') `
    (Join-Path $Paths.Project 'ProjectSettings')
Write-Host '  注入代码与 HybridCLR 设置已复制'

# 改动用例都必须能施加在搭好的样例上；施加不了说明环境与制作用例时不一致
foreach ($case in Get-ChildItem (Join-Path $PatchParityRoot 'cases') -Filter '*.patch') {
    if (-not (Test-GitIn $repo @('apply', '--check', $case.FullName))) {
        throw "改动用例 $($case.Name) 施加不上，被测样例的环境与制作用例时不一致"
    }
}
Write-Host '  全部改动用例都能施加'

# ── 2. patchparity ────────────────────────────────────────
Write-Step 'patchparity：测试与发布'
$dotnet = $Paths.DotnetExe
if (-not $SkipTests) {
    foreach ($tests in @('tests\PatchParity.Pipeline.Tests', 'tests\PatchParity.Mcp.Tests', 'tests\PatchParity.Cli.Tests')) {
        Invoke-Native $dotnet @('test', (Join-Path $PatchParityRoot $tests), '--nologo', '-v', 'q')
    }
}
# 自包含发布：Pigeon 只把白名单里的环境变量传给 MCP 服务端，DOTNET_ROOT 不在其中，依赖框架的发布会找不到运行时
foreach ($pair in @(@('src\PatchParity.Mcp\PatchParity.Mcp.csproj', $Paths.McpBin), @('src\PatchParity.Cli\PatchParity.Cli.csproj', $Paths.CliBin))) {
    Invoke-Native $dotnet @('publish', (Join-Path $PatchParityRoot $pair[0]), '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-o', $pair[1], '--nologo', '-v', 'q')
}
if (-not (Test-Path $Paths.McpExe)) { throw "发布后找不到 $($Paths.McpExe)" }
Write-Host "  MCP 服务端 $($Paths.McpExe)"

# ── 3. Pigeon ─────────────────────────────────────────────
Write-Step "Pigeon @ $PigeonCommit"
$pigeon = $Paths.Pigeon
if (-not (Test-Path (Join-Path $pigeon '.git'))) {
    Invoke-Native 'git' @('clone', '-q', $(if ($PigeonSource) { $PigeonSource } else { $PigeonRepo }), $pigeon)
}
$pigeonHead = (& git -C $pigeon rev-parse --short=7 HEAD).Trim()
if ($pigeonHead -ne $PigeonCommit) {
    Git-In $pigeon @('checkout', '-q', '--detach', $PigeonCommit)
}
# 冻结副本不改任何已跟踪文件（裁决 011）
if (-not (Test-GitIn $pigeon @('diff', '--quiet', 'HEAD'))) { throw 'Pigeon 副本有改动，冻结副本不得修改' }

if (-not (Test-Path (Join-Path $pigeon 'node_modules'))) {
    Push-Location $pigeon
    try { Invoke-Native 'npm' @('ci', '--no-audit', '--no-fund') } finally { Pop-Location }
}
# stream-fn 放在副本之内，模块才能解析到副本里的依赖；local/ 不进副本的 git，否则每条结果都被标成 dirty
$local = Join-Path $pigeon 'local'
New-Item -ItemType Directory -Force $local | Out-Null
Copy-Item -Force (Join-Path $PatchParityRoot 'setup\pigeon\deepseek-stream-fn.mjs') $local
$exclude = Join-Path $pigeon '.git\info\exclude'
if (-not ((Get-Content $exclude -ErrorAction SilentlyContinue) -contains 'local/')) {
    Add-Content -Path $exclude -Value 'local/'
}
Write-Host "  Pigeon $((& git -C $pigeon log -1 --format='%h %s').Trim())"

# ── 4. HybridCLR ──────────────────────────────────────────
if ($SkipUnity) {
    Write-Step '跳过 Unity 相关步骤（-SkipUnity）'
} else {
    Write-Step 'HybridCLR：打开工程并运行安装器（第一次要导入整个工程，可能要半小时以上）'
    # 以 HybridCLR 自己写下的版本文件判断是否装好。安装器的 HasInstalledHybridCLR 在这个版本里读出来是空的
    # （本机与云主机的安装报告都如此，而安装其实成功了），不能拿它当依据
    $versionFile = Join-Path $Paths.Project 'HybridCLRData\LocalIl2CppData-WindowsEditor\il2cpp\libil2cpp\hybridclr\generated\libil2cpp-version.txt'
    function Test-HybridClrInstalled {
        (Test-Path $versionFile) -and ((Get-Content $versionFile -Raw).Trim() -eq $HybridClrVersion)
    }
    if (Test-HybridClrInstalled) {
        Write-Host '  已安装'
    } else {
        $report = Join-Path $Paths.SetupLogs 'hybridclr-install.txt'
        $log = Join-Path $Paths.SetupLogs 'hybridclr-install.log'
        Remove-Item -Force $report -ErrorAction SilentlyContinue
        Start-Process -FilePath $Paths.UnityExe -ArgumentList @(
            '-batchmode', '-nographics', '-accept-apiupdate',
            '-projectPath', "`"$($Paths.Project)`"",
            '-executeMethod', 'PatchParity.EditorTools.PatchParityHybridClr.Install',
            '-patchparityOutput', "`"$($Paths.SetupLogs)`"",
            '-logFile', "`"$log`""
        ) | Out-Null

        # 以产物文件为完成信号：Unity 启动后可能换进程继续工作，最初的进程先退出（见路线图命令行注意事项）
        $deadline = (Get-Date).AddMinutes(90)
        $idleSince = $null
        while (-not (Test-Path $report)) {
            if ((Get-Date) -gt $deadline) { throw "等待 90 分钟仍未完成，见日志 $log" }
            if (Get-Process -Name Unity -ErrorAction SilentlyContinue) {
                $idleSince = $null
            } elseif (-not $idleSince) {
                $idleSince = Get-Date
            } elseif (((Get-Date) - $idleSince).TotalSeconds -gt 60) {
                $tail = if (Test-Path $log) { (Get-Content $log -Tail 30) -join "`n" } else { '（没有日志）' }
                throw "Unity 已退出但没有产出安装报告。最常见的原因是许可证未激活。日志末尾：`n$tail"
            }
            Start-Sleep -Seconds 10
        }
        if (-not (Test-HybridClrInstalled)) {
            $found = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { '没有版本文件' }
            throw "HybridCLR 安装未成功：期望版本 $HybridClrVersion，实际 $found，见日志 $log"
        }
        Write-Host '  完成'
    }
}

Write-Step '工作区就绪'
Write-Host '  下一步见 setup\README.md：先用命令行跑一例完整验证，确认整条链路在这台主机上成立，再接 Pigeon。'
