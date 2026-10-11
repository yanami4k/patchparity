<#
.SYNOPSIS
在云主机上逐题实测 cases/cases.json 里的改动用例：不经 Agent，直接用命令行 verify 跑完整验证。

.DESCRIPTION
用例的类别以实测为准（cases/README.md）：危险题复现不出预期的失败就改归边界，边界题记下实测结论。
本脚本只负责实测与记录，不改清单；结论由人核对日志后填进 cases.json。

每题的完整输出存为 <根>\artifacts\measure\<id>.log（命令行自己写的 UTF-8，不经 PowerShell 转码），
汇总追加到 <根>\artifacts\measure\summary.csv：题目、预期类别、退出码、判定、墙钟分钟。
母包基线按内容哈希缓存，第一题建好后其余题都会命中。
#>
[CmdletBinding()]
param(
    # 只跑这些题；缺省跑清单里的全部
    [string[]]$Ids = @()
)

. (Join-Path $PSScriptRoot '..\setup\common.ps1')

$Ids = @($Ids | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$cli = Join-Path $Paths.CliBin 'patchparity.exe'
if (-not (Test-Path $cli)) { throw "找不到 $cli，请先运行 setup\prepare-workspace.ps1" }

$out = Join-Path $Paths.Artifacts 'measure'
New-Item -ItemType Directory -Force $out | Out-Null
$summary = Join-Path $out 'summary.csv'
if (-not (Test-Path $summary)) {
    'id,category,exit,verdict,minutes' | Out-File -Encoding utf8 $summary
}

# 命令行 verify 的退出码（src/PatchParity.Cli/VerifyCommand.cs）
$verdicts = @{ 0 = 'equivalent'; 3 = 'divergent'; 4 = 'void'; 5 = 'patch_failed'; 6 = 'workspace_busy'; 7 = 'workspace_dirty'; 2 = 'usage'; 9 = 'error' }

$manifest = Get-Content (Join-Path $PatchParityRoot 'cases\cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($case in $manifest.cases) {
    if ($Ids.Count -gt 0 -and $Ids -notcontains $case.id) { continue }

    Write-Step "$($case.id)（预期 $($case.category)）"
    $arguments = @(
        'verify',
        '--unity', $Paths.UnityExe,
        '--project', $Paths.Project,
        '--repo', $Paths.VendorRepo,
        '--artifacts', (Join-Path $out "work-$($case.id)"),
        '--cache', $Paths.Cache,
        '--case', (Join-Path $PatchParityRoot "cases\$($case.patch)"),
        '--name', $case.id,
        '--expect', $case.category
    )
    if (@($case.touched).Count -gt 0) { $arguments += @('--touched', (@($case.touched) -join ',')) }

    $log = Join-Path $out "$($case.id).log"
    $started = Get-Date
    # 直接重定向到文件：命令行以 UTF-8 输出中文，经 PowerShell 管道会按控制台代码页转码成乱码
    $process = Start-Process -FilePath $cli -ArgumentList ($arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
    $verdict = if ($verdicts.ContainsKey($process.ExitCode)) { $verdicts[$process.ExitCode] } else { "exit$($process.ExitCode)" }

    "$($case.id),$($case.category),$($process.ExitCode),$verdict,$minutes" | Out-File -Append -Encoding utf8 $summary
    Write-Host "  $verdict（退出码 $($process.ExitCode)），$minutes 分钟，日志 $log"
}

Write-Step "汇总 $summary"
