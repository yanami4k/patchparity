<#
.SYNOPSIS
对若干道场景搜索题跑随机搜索基线，汇总成一张表（裁决 022）。

.DESCRIPTION
每道题调用 patchparity search-baseline：在公开的参数空间里随机取场景，数要试几次才让母包与整包重编不同。
两个包都从缓存取，须先用 measure-cases.ps1 实测过这些题。结果在 <根>\artifacts\search-baseline\ 下，
每题一个目录（summary.json 与命中那次的运行），汇总在 summary.csv。
#>
[CmdletBinding()]
param(
    # 逗号分隔的题号；不给则取 cases.json 里全部 search 为真的题
    [string]$Ids = '',
    [int]$Trials = 10,
    [int]$MaxAttempts = 8,
    [int]$Seed = 1
)

. (Join-Path $PSScriptRoot '..\setup\common.ps1')

$cli = Join-Path $Paths.CliBin 'patchparity.exe'
$out = Join-Path $Paths.Artifacts 'search-baseline'
New-Item -ItemType Directory -Force $out | Out-Null

$manifest = Get-Content (Join-Path $PatchParityRoot 'cases\cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$wanted = @($Ids -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$cases = @($manifest.cases | Where-Object {
    if ($wanted.Count -gt 0) { $wanted -contains $_.id } else { $_.PSObject.Properties.Name -contains 'search' -and $_.search }
})
if ($cases.Count -eq 0) { throw '没有要跑的题' }

$rows = @()
foreach ($case in $cases) {
    Write-Step "$($case.id)：随机搜索 $Trials 轮，每轮至多 $MaxAttempts 次"
    $log = Join-Path $out "$($case.id).log"
    $arguments = @(
        'search-baseline',
        '--unity', $Paths.UnityExe,
        '--project', $Paths.Project,
        '--repo', $Paths.VendorRepo,
        '--artifacts', $Paths.Artifacts,
        '--cache', $Paths.Cache,
        '--case', (Join-Path $PatchParityRoot "cases\$($case.patch)"),
        '--name', $case.id,
        '--trials', $Trials, '--max-attempts', $MaxAttempts, '--seed', $Seed
    )
    # 直接重定向到文件：命令行以 UTF-8 输出中文，经 PowerShell 管道会按控制台代码页转码成乱码；只等命令行本身（见 measure-cases.ps1）
    $process = Start-Process -FilePath $cli -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    $null = $process.Handle
    $process.WaitForExit()

    $summaryPath = Join-Path $out "$($case.id)\summary.json"
    if ($process.ExitCode -ne 0 -or -not (Test-Path $summaryPath)) {
        Write-Host "  失败，退出码 $($process.ExitCode)，日志 $log"
        $rows += [pscustomobject]@{ id = $case.id; category = $case.category; trials = $Trials; hits = ''; hit_rate = ''; mean_hit_attempt = ''; failed_runs = ''; exit = $process.ExitCode }
        continue
    }
    $s = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "  命中 $($s.hits) / $($s.trials)，命中时平均第 $($s.mean_hit_attempt) 次"
    $rows += [pscustomobject]@{
        id = $case.id; category = $case.category; trials = $s.trials; hits = $s.hits; hit_rate = $s.hit_rate
        mean_hit_attempt = $s.mean_hit_attempt; failed_runs = $s.failed_runs; exit = 0
    }
}

$csv = Join-Path $out 'summary.csv'
$rows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
$rows | Format-Table -AutoSize | Out-Host
Write-Step "汇总 $csv"
