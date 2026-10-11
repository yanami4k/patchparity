<#
.SYNOPSIS
在一台全新的 Windows 主机上安装 patchparity 需要的全部工具链。

.DESCRIPTION
依次安装：Git、Node、VS 生成工具（MSVC，IL2CPP 构建要用）、.NET SDK、Unity Hub、Unity 编辑器与 Windows IL2CPP 模块。
每一项先查是否已装，已装即跳过，可以反复运行。需要管理员权限。

装完之后还有一步只能手工做：打开 Unity Hub 登录并激活许可证，见 setup\README.md。

本脚本放在 patchparity 仓库里，但运行它时主机上可能还没有 git。可以先通过远程桌面把 setup 目录整个拷到
主机上的 <根>\patchparity\setup 下运行，装好 git 后再在 <根>\patchparity 克隆仓库覆盖。
#>
[CmdletBinding()]
param(
    # 跳过某些项，例如已经手工装好的
    [string[]]$Skip = @()
)

. (Join-Path $PSScriptRoot 'common.ps1')

# 经 powershell -File 调用时，逗号分隔的列表不会被拆成数组，整串进来，这里自己拆
$Skip = @($Skip | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# 只在真要往系统里装东西时才要求管理员：全部已装、或只装 .NET SDK（装在根目录下）时不需要
function Assert-Admin {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw '这一步要往系统里安装组件，需要以管理员身份运行'
    }
}

$rebootNeeded = $false

function Update-SessionPath {
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Install-Exe([string]$installer, [string[]]$arguments, [int[]]$okCodes = @(0)) {
    Assert-Admin
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    if ($okCodes -notcontains $process.ExitCode) {
        throw "$installer 退出码 $($process.ExitCode)"
    }
    return $process.ExitCode
}

# ── Windows Server 需要的系统组件 ─────────────────────────
if ($Skip -notcontains 'MediaFoundation' -and (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue)) {
    Write-Step 'Windows Server 媒体基础组件（Unity 编辑器在 Windows Server 上依赖它）'
    $feature = Get-WindowsFeature -Name Server-Media-Foundation
    if ($feature -and -not $feature.Installed) {
        Assert-Admin
        $result = Install-WindowsFeature -Name Server-Media-Foundation
        if ($result.RestartNeeded -eq 'Yes') { $rebootNeeded = $true }
    } else {
        Write-Host '  已安装或不适用'
    }
}

# ── Git ───────────────────────────────────────────────────
if ($Skip -notcontains 'Git') {
    Write-Step "Git（$GitVersionTag）"
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Write-Host "  已安装：$(git --version)"
    } else {
        $installer = Get-Download $Urls.Git $GitInstallerName
        Install-Exe $installer @('/VERYSILENT', '/NORESTART', '/SP-', '/SUPPRESSMSGBOXES') | Out-Null
        Update-SessionPath
        Write-Host "  完成：$(git --version)"
    }
}

# ── Node ──────────────────────────────────────────────────
if ($Skip -notcontains 'Node') {
    Write-Step "Node $NodeVersion（Pigeon 直接以 Node 运行 TypeScript 源码）"
    $current = if (Get-Command node -ErrorAction SilentlyContinue) { (node --version) } else { '' }
    if ($current -eq "v$NodeVersion") {
        Write-Host "  已安装：$current"
    } else {
        $installer = Get-Download $Urls.Node "node-v$NodeVersion-x64.msi"
        Install-Exe 'msiexec.exe' @('/i', "`"$installer`"", '/qn', '/norestart') | Out-Null
        Update-SessionPath
        Write-Host "  完成：$(node --version)，npm $(npm --version)"
    }
}

# ── VS 生成工具（MSVC）────────────────────────────────────
if ($Skip -notcontains 'BuildTools') {
    Write-Step 'VS 生成工具与 MSVC（IL2CPP 把 C# 转成 C++ 后要用它编译）'
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $hasVc = (Test-Path $vswhere) -and
        [bool](& $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($hasVc) {
        Write-Host '  已安装'
    } else {
        $installer = Get-Download $Urls.VsBuildTools 'vs_BuildTools.exe'
        $code = Install-Exe $installer @(
            '--quiet', '--wait', '--norestart', '--nocache',
            '--add', 'Microsoft.VisualStudio.Workload.VCTools', '--includeRecommended'
        ) @(0, 3010)
        if ($code -eq 3010) { $rebootNeeded = $true }
        Write-Host '  完成'
    }
}

# ── .NET SDK ──────────────────────────────────────────────
if ($Skip -notcontains 'Dotnet') {
    Write-Step ".NET SDK $DotnetSdkVersion（装在 $($Paths.Dotnet)，不进系统 PATH）"
    $installed = (Test-Path $Paths.DotnetExe) -and ((& $Paths.DotnetExe --list-sdks) -match [regex]::Escape($DotnetSdkVersion))
    if ($installed) {
        Write-Host '  已安装'
    } else {
        $script = Get-Download $Urls.DotnetInstall 'dotnet-install.ps1'
        & $script -Version $DotnetSdkVersion -InstallDir $Paths.Dotnet -NoPath
        Write-Host "  完成：$(& $Paths.DotnetExe --version)"
    }
}

# ── Unity Hub ─────────────────────────────────────────────
if ($Skip -notcontains 'UnityHub') {
    Write-Step 'Unity Hub（只用来登录并激活许可证）'
    $hub = Join-Path $env:ProgramFiles 'Unity Hub\Unity Hub.exe'
    if (Test-Path $hub) {
        Write-Host '  已安装'
    } else {
        $installer = Get-Download $Urls.UnityHub 'UnityHubSetup-x64.exe'
        Install-Exe $installer @('/S') | Out-Null
        Write-Host '  完成'
    }
}

# ── Unity 编辑器与 IL2CPP 模块 ────────────────────────────
# Unity 按来访 IP 分流：中国大陆与香港的请求被重定向到 download.unitychina.cn，那里只有中国版，没有国际版的这个版本
# （阿里云香港实测 404）。下载不到时把安装包放进下载目录再重跑，Get-Download 见到已有文件就直接用
function Get-UnityInstaller([string]$url, [string]$fileName) {
    try {
        return Get-Download $url $fileName
    } catch {
        throw ("下载不到 $fileName。Unity 的下载地址在中国大陆与香港会被重定向到 download.unitychina.cn，" +
            "那里没有国际版 $UnityVersion。请从能访问国际站的机器下载 $url，放到 $($Paths.Downloads) 下，再重跑本脚本" +
            "（见 setup\README.md）。原始错误：$($_.Exception.Message)")
    }
}

if ($Skip -notcontains 'Unity') {
    Write-Step "Unity $UnityVersion（装在 $($Paths.UnityDir)）"
    if (Test-Path $Paths.UnityExe) {
        Write-Host '  编辑器已安装'
    } else {
        $installer = Get-UnityInstaller $Urls.UnityEditor "UnitySetup64-$UnityVersion.exe"
        # NSIS 安装器的 /D= 必须是最后一个参数，且路径不加引号
        Install-Exe $installer @('/S', "/D=$($Paths.UnityDir)") | Out-Null
        if (-not (Test-Path $Paths.UnityExe)) { throw "编辑器安装后仍找不到 $($Paths.UnityExe)" }
        Write-Host '  编辑器完成'
    }

    $il2cpp = Join-Path $Paths.UnityDir 'Editor\Data\PlaybackEngines\windowsstandalonesupport\Variations\win64_player_nondevelopment_il2cpp'
    if (Test-Path $il2cpp) {
        Write-Host '  IL2CPP 模块已安装'
    } else {
        $installer = Get-UnityInstaller $Urls.UnityIl2cpp "UnitySetup-Windows-IL2CPP-Support-for-Editor-$UnityVersion.exe"
        # 模块安装器的 /D 指向编辑器根目录（含 Editor 子目录的那一层），不是 Editor 本身
        Install-Exe $installer @('/S', "/D=$($Paths.UnityDir)") | Out-Null
        if (-not (Test-Path $il2cpp)) { throw "IL2CPP 模块安装后仍找不到 $il2cpp" }
        Write-Host '  IL2CPP 模块完成'
    }
}

Write-Step '工具链就绪'
if ($rebootNeeded) {
    Write-Host '  有组件要求重启。请先重启主机，再继续下一步。' -ForegroundColor Yellow
}
Write-Host '  下一步：打开 Unity Hub 登录并激活许可证（见 setup\README.md），然后运行 setup\prepare-workspace.ps1'
