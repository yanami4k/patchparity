# setup —— 在一台全新的 Windows 主机上搭建 patchparity

本目录的脚本把一台空白的 Windows 主机变成能跑完整验证的工作区：装工具链、按锁定提交取被测样例并打环境补丁、
发布 patchparity、取 Pigeon 冻结副本、为 `pigeon eval` 生成 MCP 配置。目标主机是阿里云香港的 Windows 云主机，
本机或其他 Windows 机器同样适用。

整套流程首次约需两到三小时，大部分时间在下载、安装与 Unity 首次导入工程，人手操作只有第 1、2、4 步。

| 脚本 | 作用 | 需要管理员 |
|---|---|---|
| `install-toolchain.ps1` | Git、Node、VS 生成工具（MSVC）、.NET SDK、Unity Hub、Unity 编辑器与 IL2CPP 模块 | 是（全部已装时否） |
| `prepare-workspace.ps1` | 被测样例、注入代码、测试与发布、Pigeon、HybridCLR 安装 | 否 |
| `new-eval-run.ps1` | 为一次 `pigeon eval` 跑批建输出目录并写入 MCP 配置 | 否 |
| `common.ps1` | 锁定的版本、下载地址、目录布局，由以上脚本引入 | — |

各项版本都锁定在 `common.ps1` 里：Klotho `e29c489`（裁决 002）、Pigeon `65ff318`（裁决 011）、
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

根目录取 `D:\pp` 这样的短路径：`pigeon eval` 在输出目录下开每题的工作树，路径会多出约 85 个字符。
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
3. 克隆 Pigeon 并检出 `65ff318`，`npm ci`，把 `setup/pigeon/deepseek-stream-fn.mjs` 放进副本的 `local/`。
   冻结副本的已跟踪文件一个不改（裁决 011）。
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

## 6. 接 Pigeon

```powershell
powershell -ExecutionPolicy Bypass -File D:\pp\patchparity\setup\new-eval-run.ps1 -Name smoke
```

它做三件事：

1. 在 `D:\pp\e\smoke` 下写两份 MCP 配置。风险档取 A：`list_scenarios` 为 read，其余 6 个为 write，跑批要加 `--yolo`（裁决 015）。
2. 按 `cases/cases.json` 在 `D:\pp\patchparity\eval\tasks` 下生成题目目录，一道题一个子目录。题目目录含本机绝对路径，
   不入库，每次重新生成。
3. 打印 `pigeon eval` 的完整命令。

先只跑一道题，把命令里的题目目录换成某一道题的子目录即可：

```powershell
$env:DEEPSEEK_API_KEY = '<你的 key>'   # 只设在当前进程，不写进任何文件
node D:\pp\pigeon\src\cli\index.ts eval D:\pp\patchparity\eval\tasks\safe-item-lifetime --out D:\pp\e\smoke `
  --runs 1 --conditions none --skill D:\pp\e\smoke\unused-skills `
  --stream-fn D:\pp\pigeon\local\deepseek-stream-fn.mjs --provider deepseek --model deepseek-v4-flash --yolo
```

`pigeon eval` 不接受 `--concurrency`，传了会报未知参数退出；它本身逐题串行。`--runs 1 --conditions none --skill <任意路径>`
都必须显式给。

一道题的过程：Agent 照题面依次调用 patchparity 的工具，服务端每完成一步就把结果记进该题工作树里的
`patchparity-ledger.json`；Agent 收工后，Pigeon 运行题目目录里的 `verify.mjs`，它调用 `patchparity judge`，用与命令行验证
同一套规则从台账判定，并与预期比对。判定不采信 Agent 的说法（裁决 009），预期也不写在题面里。

长时间跑批时，离开前**断开**远程桌面（直接关掉窗口），不要**注销**：断开后会话里的进程继续运行，注销会结束它们。
云主机专门给 patchparity 用，`--yolo` 放行的是这台机器上的全部权限，不要在上面放别的重要东西。
Pigeon 的 `--sandbox` 用不上：它把工作区放进 Linux 容器，而 Unity 与 MCP 服务端必须在这台 Windows 主机上。

结果看 `D:\pp\e\smoke\results.jsonl`：每题一行，其中收着 judge 输出的 JSON 摘要（判定、首个分歧 tick 与分歧层、各步耗时、
母包基线是否命中缓存）。服务端那份台账另存在 `D:\pp\artifacts\sessions\<会话号>\` 下，工作树判完分就会被删。

## 常见问题

- **批处理没有产出就退出**：先查 Unity 许可证，再看对应的 `.log`。
- **报"工程被另一个 Unity 进程占用"**：有残留的编辑器，`Get-Process Unity` 找到后结束它。
- **报"工作区被另一个会话占用"**：同一时刻只允许一个会话改动被测样例（裁决 014）。等它结束，或结束那个 `patchparity-mcp.exe`。
- **报"玩法源码有来源不明的改动"**：有人绕过工具直接改了 `Assets/Brawler/`。确认无用后在 `D:\pp\vendor\Klotho` 里
  `git checkout -- Samples/Brawler/Assets/Brawler` 恢复（裁决 015）。
- **Pigeon 下 MCP 工具一个都没有**：headless 与 eval 不打印 MCP 启动失败，去输出目录 `.pigeon\sessions\` 下的账本里查
  `run.started` 的 `mcpServers`。Unity 若因缺环境变量失败，在 `.mcp.json` 的 `env` 里补上。
