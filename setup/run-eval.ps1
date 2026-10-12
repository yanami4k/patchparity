<#
.SYNOPSIS
跑一批 pigeon eval：生成题目与输出目录，交给 Agent 逐题跑完，再汇总每题的判分与 judge 的判定。

.DESCRIPTION
DEEPSEEK_API_KEY 须已在当前进程的环境里；脚本不读写任何存放 key 的文件。

-Isolate 让每道题单独用一个输出目录。Pigeon 给 Agent 开放了检索会话记录的内置工具（search_sessions、
read_session_entry），检索范围是输出目录下的全部会话：同一目录里逐题跑时，后面的题能翻到前面的题的工具返回，
包括它们找到的场景（S6 实测有 Agent 这样翻过，见裁决 019）。场景搜索类题目应当隔离。

跑批要几分钟到几小时，不要挂在 SSH 会话下：断网会卡死输出管道。远程桌面里前台运行后断开（不要注销），
或经 WMI 起一个不依附会话的进程，见 README。

汇总写到 <根>\e\<名字>-summary.csv。judge 的判定不在 results.jsonl 里，而在各题会话账本的 eval.verified 事件中。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,12}$')]
    [string]$Name,

    # 只跑 id 以此开头的题，例如 search- 只跑场景搜索类；不给则跑全部
    [string]$Only = '',

    # 每道题单独一个输出目录（<名字>-01、<名字>-02……），题与题之间看不到彼此的会话
    [switch]$Isolate,

    [string]$Model = 'deepseek-v4-flash'
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $env:DEEPSEEK_API_KEY) { throw '当前进程的环境里没有 DEEPSEEK_API_KEY' }

# Pigeon 以 UTF-8 输出中文；输出被重定向到日志时，PowerShell 默认按控制台代码页转码，日志里就成了乱码
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$cli = Join-Path $Paths.Pigeon 'src\cli\index.ts'
$streamFn = Join-Path $Paths.Pigeon 'local\deepseek-stream-fn.mjs'
$tasks = Join-Path $PatchParityRoot 'eval\tasks'
$newRun = Join-Path $PSScriptRoot 'new-eval-run.ps1'

$ids = @((Get-Content (Join-Path $PatchParityRoot 'cases\cases.json') -Raw -Encoding UTF8 | ConvertFrom-Json).cases |
    ForEach-Object { $_.id } | Where-Object { $_.StartsWith($Only, [StringComparison]::Ordinal) })
if ($ids.Count -eq 0) { throw "cases.json 里没有 id 以 '$Only' 开头的题" }

# 每批：生成题目与输出目录，再交给 pigeon eval。有题判分不过时 pigeon eval 照样以 0 退出，所以这里不按退出码中断
$batches = if ($Isolate) {
    $i = 0
    foreach ($id in $ids) { $i++; [pscustomobject]@{ Run = '{0}-{1:00}' -f $Name, $i; Only = $id } }
} else {
    [pscustomobject]@{ Run = $Name; Only = $Only }
}

$outs = @()
foreach ($batch in $batches) {
    # new-eval-run 的提示走 Write-Host（信息流 6），Out-Null 管不到
    & $newRun -Name $batch.Run -Only $batch.Only 6> $null | Out-Null
    $out = Join-Path $Root "e\$($batch.Run)"
    $outs += $out
    Write-Step "pigeon eval → $out"
    & node $cli eval $tasks --out $out --runs 1 --conditions none --skill (Join-Path $out 'unused-skills') `
        --stream-fn $streamFn --provider deepseek --model $Model --yolo
    Write-Host "  pigeon eval 退出码 $LASTEXITCODE"
}

$rows = foreach ($out in $outs) {
    $results = Join-Path $out 'results.jsonl'
    if (-not (Test-Path $results)) { Write-Warning "$out 没有 results.jsonl"; continue }
    foreach ($line in Get-Content $results -Encoding UTF8) {
        $r = $line | ConvertFrom-Json
        $details = $null
        $ledger = Join-Path $out ".pigeon\sessions\$($r.sessionId).jsonl"
        if (Test-Path $ledger) {
            foreach ($event in Get-Content $ledger -Encoding UTF8) {
                if ($event -match '"kind":"eval\.verified"') { $details = ($event | ConvertFrom-Json).payload.details }
            }
        }
        [pscustomobject]@{
            task       = $r.taskId
            grade      = $r.verdict
            verdict    = $details.verdict
            observable = $details.observable
            tick       = $details.first_divergent_tick
            scenario   = $details.scenario
            turns      = $r.turns
            minutes    = [math]::Round($r.wallMs / 60000, 1)
            tokens     = $r.usage.totalTokens
            usd        = [math]::Round($r.usage.cost.total, 4)
            out        = $out
        }
    }
}

$summary = Join-Path $Root "e\$Name-summary.csv"
$rows | Export-Csv -Path $summary -NoTypeInformation -Encoding UTF8
$rows | Format-Table task, grade, verdict, observable, tick, turns, minutes, usd -AutoSize | Out-Host
$passed = @($rows | Where-Object { $_.grade -eq 'pass' }).Count
Write-Step "判分通过 $passed / $(@($rows).Count)，汇总 $summary"
