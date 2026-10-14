<#
.SYNOPSIS
用 Inspect AI 跑一批题：每题跑若干次（epoch），统计通过率、pass@k、pass^k 与平均花费，打包可视化日志。

.DESCRIPTION
Agent 仍是 Pigeon，Inspect 负责组织题目、重复跑、统计与展示（裁决 021）。题目与判分规则与 run-eval.ps1 同一份。
DEEPSEEK_API_KEY 须已在当前进程的环境里；脚本不读写任何存放 key 的文件。

产物都在 <根>\e\<名字> 下：
  logs\      Inspect 日志（JSON 格式），本机装了 inspect-ai 时可用 inspect view --log-dir 打开
  viewer\    打包好的静态查看器，用任意静态文件服务器即可浏览（不能直接双击 index.html，浏览器不允许本地页面读日志文件）
  <题号>\epoch-<n>\   每次运行的工作目录：Pigeon 会话存档、Agent 与验证脚本的输出、服务端台账

跑批要几分钟到几小时，不要挂在 SSH 会话下（断网会卡死输出管道），见 README。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,16}$')]
    [string]$Name,

    # 只跑 id 以此开头的题，例如 search- 只跑场景搜索类；不给则跑全部
    [string]$Only = '',

    # 每题跑几次。大于 1 时另报 pass@k（k 次里至少一次通过）与 pass^k（k 次全过）
    [ValidateRange(1, 20)]
    [int]$Epochs = 1,

    # 经 setup/pigeon/deepseek-stream-fn.mjs 调用的模型；S5、S6 的结果都是 deepseek-v4-flash
    [string]$Model = 'deepseek-v4-flash',

    # 推理档位。S5、S6 是在关闭思考时跑的；Pigeon 5e70ca2 不给档位时对这个模型取 high
    [string]$Thinking = 'off'
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $env:DEEPSEEK_API_KEY) { throw '当前进程的环境里没有 DEEPSEEK_API_KEY' }
if (-not (Test-Path $Paths.InspectExe)) { throw "找不到 $($Paths.InspectExe)，请先运行 install-toolchain.ps1 与 prepare-workspace.ps1" }

# 输出含中文；被重定向到日志时，PowerShell 默认按控制台代码页转码，日志里就成了乱码
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$env:PYTHONIOENCODING = 'utf-8'

$batch = Initialize-EvalBatch -Name $Name -Only $Only

# 模型由 stream-fn 模块按这个环境变量选；传给 Pigeon 的 --model 只是身份占位
$env:PATCHPARITY_DEEPSEEK_MODEL = $Model

$logs = Join-Path $batch.Out 'logs'
$taskArgs = @(
    '-T', "tasks=$($batch.Tasks)",
    '-T', "out=$($batch.Out)",
    '-T', "pigeon=$($Paths.Pigeon)",
    '-T', "stream_fn=$($batch.StreamFn)",
    '-T', "mcp_config=$($batch.McpConfig)",
    '-T', "epochs=$Epochs",
    '-T', "thinking=$Thinking",
    '-T', "model_id=$Model"
)
if ($Only) { $taskArgs += @('-T', "only=$Only") }

# Inspect 在 Windows 上会把临时目录的路径截成 \Users\...（丢了盘符），再按当前盘解析：临时目录须与工作所在的盘一致
$tmp = Join-Path $Root 'tmp'
New-Item -ItemType Directory -Force $tmp | Out-Null
$env:TEMP = $tmp
$env:TMP = $tmp

Write-Step "inspect eval → $logs"
# --max-samples 1：被测工程只有一份，同一时刻只能验证一处改动（裁决 014）。--model none：Agent 由 Pigeon 自己调用模型。
# 任务文件给相对路径：Inspect 用 pathlib 的 glob 匹配任务文件，Python 3.12 不接受绝对路径的模式（云上实测）
Push-Location $PatchParityRoot
try {
    & $Paths.InspectExe eval 'eval/inspect/patchparity_task.py' `
        --model none --max-samples 1 --display plain --log-dir $logs --log-format json @taskArgs
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}

# 打包静态查看器：在输出目录下用相对路径打包，临时目录已指到同一个盘
Write-Step "打包查看器 → $(Join-Path $batch.Out 'viewer')"
if (Test-Path $logs) {
    Push-Location $batch.Out
    try {
        & $Paths.InspectExe view bundle --log-dir logs --output-dir viewer --overwrite
    } finally {
        Pop-Location
    }
}

if ($code -ne 0) { throw "inspect eval 失败，退出码 $code" }
