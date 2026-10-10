<#
.SYNOPSIS
为一次 pigeon eval 跑批建输出目录，写入按本机路径生成的 MCP 配置。

.DESCRIPTION
pigeon eval 把 --out 输出目录当治理根，两份 MCP 配置都从这里读（裁决 015）：
  <输出目录>\.mcp.json          MCP 服务端怎么启动，路径一律绝对路径
  <输出目录>\.pigeon\mcp.json   工具风险档，取 A：list_scenarios 为 read，其余 6 个为 write
风险档 A 要配合 --yolo；wait_job 与 compare_runs 配成 write，它们返回的证据才会收进回执（裁决 006、015）。

输出目录放在 <根>\e 下：路径要短，Pigeon 在其下开每题的工作树，约多出 85 个字符。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_-]{1,16}$')]
    [string]$Name
)

. (Join-Path $PSScriptRoot 'common.ps1')

$cliExe = Join-Path $Paths.CliBin 'patchparity.exe'
foreach ($required in @($Paths.McpExe, $cliExe, $Paths.UnityExe, $Paths.Project, $Paths.Pigeon)) {
    if (-not (Test-Path $required)) { throw "找不到 $required，请先运行 install-toolchain.ps1 与 prepare-workspace.ps1" }
}

$out = Join-Path $Root "e\$Name"
if (Test-Path $out) { throw "$out 已存在。同一输出目录重跑会续跑未完成的题；要新开一批请换个名字" }
New-Item -ItemType Directory -Force (Join-Path $out '.pigeon') | Out-Null

# 服务端的工作目录是每题的工作树，题目结束连同其中的文件一起删掉，所以路径不能写相对路径
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
$launch = [ordered]@{ mcpServers = [ordered]@{ patchparity = $server } }

$tiers = [ordered]@{ list_scenarios = [ordered]@{ tier = 'read' } }
foreach ($tool in @('build_package', 'build_patch', 'revert_patch', 'run_scenario', 'wait_job', 'compare_runs')) {
    $tiers[$tool] = [ordered]@{ tier = 'write' }
}
$governance = [ordered]@{
    version = 1
    servers = [ordered]@{ patchparity = [ordered]@{ defaultTier = 'write'; tools = $tiers } }
}

# 不带 BOM 的 UTF-8：PowerShell 5.1 的 Out-File 会加 BOM，Pigeon 按 JSON 解析时不认
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $out '.mcp.json'), ($launch | ConvertTo-Json -Depth 6), $utf8)
[IO.File]::WriteAllText((Join-Path $out '.pigeon\mcp.json'), ($governance | ConvertTo-Json -Depth 6), $utf8)

# 题目目录按本机路径现场生成（含改动用例与命令行程序的绝对路径），不入库；判分由其中的 verify.mjs 调 patchparity judge
$tasks = Join-Path $PatchParityRoot 'eval	asks'
Invoke-Native 'node' @((Join-Path $PatchParityRoot 'eval\generate-tasks.mjs'), '--cli', $cliExe, '--out', $tasks)

$streamFn = Join-Path $Paths.Pigeon 'local\deepseek-stream-fn.mjs'
$cli = Join-Path $Paths.Pigeon 'src\cli\index.ts'

Write-Step "输出目录 $out"
Write-Host '  已写入 .mcp.json 与 .pigeon\mcp.json'
Write-Host "  题目目录 $tasks（给其下某一道题的目录即只跑那一题）"
Write-Host ''
Write-Host '  跑批命令（DEEPSEEK_API_KEY 只设在当前进程，不写进文件）：'
Write-Host ''
Write-Host "  `$env:DEEPSEEK_API_KEY = '<你的 key>'"
Write-Host "  node $cli eval $tasks --out $out --runs 1 --conditions none ``"
Write-Host "    --skill $(Join-Path $out 'unused-skills') --stream-fn $streamFn ``"
Write-Host '    --provider deepseek --model deepseek-v4-flash --yolo'
Write-Host ''
Write-Host '  不要加 --concurrency：pigeon eval 不认这个参数，本身就逐题串行（裁决 015）。'
