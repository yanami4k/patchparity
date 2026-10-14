<#
.SYNOPSIS
跑一批题：按本机路径生成题目，交给 patchparity eval 逐题让 Agent 做完并判分。

.DESCRIPTION
DEEPSEEK_API_KEY 须已在当前进程的环境里；脚本不读写任何存放 key 的文件。

每道题一个独立的工作目录 <根>\e\<名字>\<题号>，各带一套 Pigeon 的治理根（MCP 启动定义、风险档、会话存档），
会话检索关掉：题与题之间看不到彼此的会话（裁决 019、020）。同一名字重跑即续跑，已有结果的题跳过。

跑批要几分钟到几小时，不要挂在 SSH 会话下：断网会卡死输出管道。远程桌面里前台运行后断开（不要注销），
或经 WMI 起一个不依附会话的进程，见 README。

汇总在 <根>\e\<名字>\summary.csv；每题的判分、judge 判定、Agent 用量与工具调用在 <题号>\result.json。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,16}$')]
    [string]$Name,

    # 只跑 id 以此开头的题，例如 search- 只跑场景搜索类；不给则跑全部
    [string]$Only = '',

    # 经 setup/pigeon/deepseek-stream-fn.mjs 调用的模型；S5、S6 的结果都是 deepseek-v4-flash
    [string]$Model = 'deepseek-v4-flash',

    # 推理档位。S5、S6 是在关闭思考时跑的（Pigeon 65ff318）；5e70ca2 不给档位时对这个模型取 high，行为与花费都不同
    [string]$Thinking = 'off'
)

. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $env:DEEPSEEK_API_KEY) { throw '当前进程的环境里没有 DEEPSEEK_API_KEY' }

# 命令行以 UTF-8 输出中文；输出被重定向到日志时，PowerShell 默认按控制台代码页转码，日志里就成了乱码
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$batch = Initialize-EvalBatch -Name $Name -Only $Only

# 模型由 stream-fn 模块按这个环境变量选；传给 Pigeon 的 --model 只是身份占位
$env:PATCHPARITY_DEEPSEEK_MODEL = $Model

Write-Step "patchparity eval → $($batch.Out)"
& $batch.CliExe eval --tasks $batch.Tasks --out $batch.Out --mcp-config $batch.McpConfig --pigeon $Paths.Pigeon `
    --stream-fn $batch.StreamFn --provider deepseek --model $Model --thinking $Thinking
# 0 全部判分通过 / 1 有题不通过：都是正常跑完；只有用法错误（2）与异常（9）才算脚本失败
$code = $LASTEXITCODE
if ($code -ne 0 -and $code -ne 1) { throw "patchparity eval 失败，退出码 $code" }
