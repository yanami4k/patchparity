# setup —— 在一台全新的 Windows 主机上搭建 patchparity

本目录的脚本把一台空白的 Windows 主机变成能跑完整验证的工作区：装工具链、按锁定提交取被测样例并打环境补丁、
发布 patchparity、取 Pigeon 锁定副本，之后可以让 Agent 成批跑题。目标主机是阿里云香港的 Windows 云主机，
本机或其他 Windows 机器同样适用。

整套流程首次约需两到三小时，大部分时间在下载、安装与 Unity 首次导入工程，人手操作只有第 1、2、4 步。

| 脚本 | 作用 | 需要管理员 |
|---|---|---|
| `install-toolchain.ps1` | Git、Node、VS 生成工具（MSVC）、.NET SDK、Unity Hub、Unity 编辑器与 IL2CPP 模块 | 是（全部已装时否） |
| `prepare-workspace.ps1` | 被测样例、注入代码、测试与发布、Pigeon、HybridCLR 安装 | 否 |
| `run-eval.ps1` | 生成题目，交给 `patchparity eval` 让 Agent 逐题做完并判分 | 否 |
| `common.ps1` | 锁定的版本、下载地址、目录布局，由以上脚本引入 | — |

各项版本都锁定在 `common.ps1` 里：Klotho `e29c489`（裁决 002）、Pigeon `5e70ca2`（裁决 020）、
Unity `6000.3.18f1`、.NET SDK `8.0.425`、Node `24.12.0`。下载地址于 2026-10-10 逐个核实可用。

## 1. 开云主机

- **地域**：中国香港。整套环境要从 GitHub 取 Klotho、UniTask、HybridCLR 与 Pigeon，大陆地域访问不稳定。
- **镜像**：Windows Server 2022 数据中心版，中英文均可（脚本带 UTF-8 BOM，英文系统也能正确显示中文）。
- **规格**：通用型 8 vCPU、32 GiB 内存或以上。内存是瓶颈：15.2 GB 的机器上单个 IL2CPP 构建就接近吃满。
- **磁盘**：系统盘 100 GB；另挂一块 200 GB 的数据盘作为 `D:`。编辑器约 10 GB，工程导入与 HybridCLR 约 5 GB，
  每个缓存的母包与每次整包重编各占数 GB。
- **网络**：安全组只对自己的公网 IP 开放 3389（远程桌面），其余入方向全部关闭。
- **计费**：按量付费，不用时关机。各家对"关机后是否继续计费"的规则不同，以阿里云控制台说明为准；磁盘始终计费。

开好后用远程桌面登录，在"磁盘管理"里把数据盘初始化并分配为 `D:`。

## 2. 取代码并装工具链

仓库是公开的，不需要登录 GitHub。主机上此时还没有 git，先下载 zip 包，用其中的脚本装好工具链，再正式克隆。
以下命令在**管理员** PowerShell 中执行：

```powershell
New-Item -ItemType Directory -Force D:\pp | Out-Null
Invoke-WebRequest https://github.com/yanami4k/patchparity/archive/refs/heads/main.zip -OutFile D:\pp\patchparity.zip -UseBasicParsing
Expand-Archive D:\pp\patchparity.zip D:\pp\bootstrap
Move-Item D:\pp\bootstrap\patchparity-main D:\pp\patchparity
Remove-Item -Recurse D:\pp\bootstrap, D:\pp\patchparity.zip

# 下载的文件带"来自网络"标记，不解除的话执行策略会拦下脚本
Get-ChildItem -Recurse D:\pp\patchparity | Unblock-File
powershell -ExecutionPolicy Bypass -File D:\pp\patchparity\setup\install-toolchain.ps1
```

**香港与中国大陆地域下载不到 Unity**：Unity 的下载地址按来访 IP 分流，这两个地域的请求会被重定向到
`download.unitychina.cn`，那里只有中国版，没有国际版的 `6000.3.18f1`（阿里云香港实测 404）。脚本会在这一步停下并说明原因。
此时从能访问国际站的机器下载下面两个安装包，放到 `D:\pp\downloads\` 下（文件名不变），再重跑 `install-toolchain.ps1`，
它见到已有的安装包就直接用：

- `https://download.unity3d.com/download_unity/5ebeb53e4c07/Windows64EditorInstaller/UnitySetup64-6000.3.18f1.exe`（约 4.2 GB）
- `https://download.unity3d.com/download_unity/5ebeb53e4c07/TargetSupportInstaller/UnitySetup-Windows-IL2CPP-Support-for-Editor-6000.3.18f1.exe`（约 250 MB）

脚本结束时若提示需要重启，先重启再继续。随后把 zip 解出的目录换成真正的 git 克隆（新开一个 PowerShell，
让刚装好的 git 进入 PATH）：

```powershell
Remove-Item -Recurse -Force D:\pp\patchparity
git clone https://github.com/yanami4k/patchparity.git D:\pp\patchparity
```

根目录取 `D:\pp` 这样的短路径：每道题的工作目录下还有 Pigeon 的会话存档，路径层次深，Windows 有路径长度上限。
不要经目录联接或 `subst` 盘符访问它——Pigeon 的入口经这类路径调用时会静默不执行、退出码为 0。

## 3. 激活 Unity 许可证（只能手工）

打开 Unity Hub，登录 Unity 账号，在偏好设置的许可证页添加个人版许可证。不需要用 Hub 安装编辑器，
编辑器已装在 `D:\pp\Unity\6000.3.18f1`。个人版的使用条件以 Unity 当前条款为准。

许可证没激活时，后面的批处理会直接退出、不产出任何结果，`prepare-workspace.ps1` 会把这种情况报出来。

## 4. 搭工作区

普通 PowerShell 即可：

```powershell
powershell -ExecutionPolicy Bypass -File D:\pp\patchparity\setup\prepare-workspace.ps1
```

依次完成：

1. 按锁定提交只取 Klotho 的那一个提交，打 `injected/environment/environment.patch`，复制注入代码与 `HybridCLRSettings.asset`，
   并检查 `cases/` 里每份改动用例都能施加。被测样例的 `core.autocrlf` 设为 `true`，与制作补丁时的工作区一致。
2. 跑两套测试，把 MCP 服务端与命令行发布成自包含程序（`D:\pp\bin\mcp`、`D:\pp\bin\cli`）。
   自包含是因为 Pigeon 只把白名单里的环境变量传给 MCP 服务端，`DOTNET_ROOT` 不在其中（裁决 015）。
3. 克隆 Pigeon 并检出 `5e70ca2`（已有的克隆里没有这个提交时先取回），`npm ci`，把 `setup/pigeon/deepseek-stream-fn.mjs`
   放进副本的 `local/`。锁定副本的已跟踪文件一个不改（裁决 011、020）。
4. 用 Unity 打开工程并运行 HybridCLR 安装器。第一次打开要导入整个工程，可能要半小时以上。

每一步都先查是否已完成，中途失败修好原因后重跑即可。只想检查前三步时加 `-SkipUnity`。

## 5. 先用命令行跑通一例

接 Pigeon 之前，先确认整条判定链路在这台主机上成立。不经过 Agent，结果完全确定：

```powershell
D:\pp\bin\cli\patchparity.exe verify `
  --unity D:\pp\Unity\6000.3.18f1\Editor\Unity.exe `
  --project D:\pp\vendor\Klotho\Samples\Brawler `
  --repo D:\pp\vendor\Klotho `
  --artifacts D:\pp\artifacts\cli `
  --cache D:\pp\artifacts\baseline-cache `
  --case D:\pp\patchparity\cases\safe-item-lifetime.patch --name safe-item-lifetime `
  --touched ItemComponent --expect safe
```

预期：判定为等价，确定性自检与覆盖自检均通过，打印"与预期相符"，退出码 0。冷启动要建母包基线与整包重编，
本机实测约 25 分钟；同一缓存目录第二次运行会命中母包基线。请把各步耗时记下来，云主机的成本结构要据此更新。

## 6. 让 Agent 跑题

```powershell
$env:DEEPSEEK_API_KEY = '<你的 key>'   # 只设在当前进程，不写进任何文件
powershell -ExecutionPolicy Bypass -File D:\pp\patchparity\setup\run-eval.ps1 -Name smoke -Only safe-item-lifetime
```

`run-eval.ps1` 做三件事：

1. 按 `cases/cases.json` 在 `D:\pp\patchparity\eval\tasks` 下生成题目目录，一道题一个子目录（题面、预算、验证脚本）。
   题目目录含本机绝对路径，不入库，每次重新生成。`-Only` 只生成 id 以此开头的题，不给则全部生成。
2. 写一份 MCP 服务端的启动定义 `D:\pp\e\smoke\mcp-launch.json`。
3. 调 `patchparity eval` 逐题跑。每道题建一个独立的工作目录 `D:\pp\e\smoke\<题号>`，放进 `.mcp.json` 与风险档
   `.pigeon\settings.json`（风险档 A：`list_scenarios` 为 read，其余 6 个为 write，裁决 015），再用
   `pigeon run --root <工作目录> --yolo --trust-config --json` 起 Agent，题面经标准输入送入。
   会话检索、派 worker、联网、推送记忆与钩子一律关掉：Agent 只凭题面与 patchparity 的工具做题，题与题之间也看不到彼此的会话。

一道题的过程：Agent 照题面依次调用 patchparity 的工具，服务端每完成一步就把结果记进工作目录里的 `patchparity-ledger.json`；
Agent 收工后（正常结束、撞上轮次或墙钟上限都一样），跑题器在工作目录里运行题目的 `verify.mjs`，它调用 `patchparity judge`，
用与命令行验证同一套规则从台账判定，并与预期比对。判定不采信 Agent 的说法（裁决 009），预期也不写在题面里。

结果：

- `D:\pp\e\smoke\summary.csv`：每题一行，判分、judge 判定、可观测性、场景、首个分歧 tick、Agent 终态、轮次、token、花费、用时，
  以及调用过的非 patchparity 工具。
- `D:\pp\e\smoke\<题号>\result.json`：同上，另有 judge 的完整摘要（各步耗时、分歧层、母包基线是否命中缓存）与逐个工具的调用次数。
  同一目录下还有 Agent 与验证脚本的输出、Pigeon 的会话存档（`.pigeon\state\sessions\`）。
- 服务端那份台账另存在 `D:\pp\artifacts\sessions\<时间戳>\` 下。

同一个 `-Name` 重跑即续跑：已有 `result.json` 的题跳过，没有的（中途退出的残局）整个重来。

长时间跑批时，离开前**断开**远程桌面（直接关掉窗口），不要**注销**：断开后会话里的进程继续运行，注销会结束它们。
云主机专门给 patchparity 用，`--yolo` 放行的是这台机器上的全部权限，不要在上面放别的重要东西。
Pigeon 的 `--sandbox` 用不上：它把工作区放进 Linux 容器，而 Unity 与 MCP 服务端必须在这台 Windows 主机上。

经 SSH 遥控时，跑批不能挂在 SSH 会话下（断网会卡死输出管道），key 又不能落盘。做法是经 WMI 起一个不依附会话的进程，
key 只放进它的环境：

```powershell
$env:DEEPSEEK_API_KEY = [Console]::In.ReadLine()   # 由 SSH 的标准输入送进来
$vars = Get-ChildItem Env: | ForEach-Object { "$($_.Name)=$($_.Value)" }
$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ EnvironmentVariables = [string[]]$vars; ShowWindow = [uint16]0 }
$cmd = 'cmd.exe /c powershell -NoProfile -ExecutionPolicy Bypass -File D:\pp\patchparity\setup\run-eval.ps1 -Name s7 > D:\pp\logs\eval-s7.log 2>&1'
Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd; CurrentDirectory = 'D:\pp'; ProcessStartupInformation = $startup }
$env:DEEPSEEK_API_KEY = $null
```

S5、S6 的结果是用 Pigeon `65ff318` 的 `pigeon eval` 跑的；之后的 Pigeon 移除了它，跑题改由 `patchparity eval`（裁决 020）。

## 常见问题

- **批处理没有产出就退出**：先查 Unity 许可证，再看对应的 `.log`。
- **报"工程被另一个 Unity 进程占用"**：有残留的编辑器，`Get-Process Unity` 找到后结束它。
- **报"工作区被另一个会话占用"**：同一时刻只允许一个会话改动被测样例（裁决 014）。等它结束，或结束那个 `patchparity-mcp.exe`。
- **报"玩法源码有来源不明的改动"**：有人绕过工具直接改了 `Assets/Brawler/`。确认无用后在 `D:\pp\vendor\Klotho` 里
  `git checkout -- Samples/Brawler/Assets/Brawler` 恢复（裁决 015）。
- **Agent 没有调用任何 patchparity 工具**：看题目工作目录里的 `agent.err.txt` 与 `.pigeon\state\sessions\` 下会话账本里
  `run.started` 的 MCP 服务状态。Unity 若因缺环境变量失败，在 `mcp-launch.json` 的服务定义里加 `env`。
- **Pigeon 开跑前报配置错误**：`pigeon run` 发现旧布局的配置文件（如 `.pigeon\mcp.json`）会拒绝启动，提示运行 `pigeon migrate-config`；
  跑题器写的是新布局，不会触发。
