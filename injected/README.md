# injected —— 注入被测样例的源码

本目录是 patchparity 注入到被测样例（xpTURN/Klotho 的 Samples/Brawler）中的全部代码，入库保存。被测样例本身按裁决 002 锁定 commit、以浅克隆放在 vendor/ 下，不入库。

## 还原步骤

整套还原已由 `setup/prepare-workspace.ps1` 自动完成，步骤与说明见 `setup/README.md`。手工还原时按下面的顺序：

1. 只取锁定的那一个提交，并把 `core.autocrlf` 设为 `true`（环境补丁与改动用例都按这样的工作区制作）：

   ```
   git init vendor/Klotho && cd vendor/Klotho
   git config core.autocrlf true
   git remote add origin https://github.com/xpTURN/Klotho.git
   git fetch --depth 1 origin e29c4892c88db097f3ac0d135874c82e9b6726d6
   git checkout FETCH_HEAD
   ```

2. 施加环境补丁：`git apply <patchparity>/injected/environment/environment.patch`。
   它包含 HybridCLR v8.14.1（`packages-lock.json` 锁到具体提交）、IL2CPP 脚本后端、编辑器版本号，
   以及 Unity 6000.3.18f1 首次打开工程时自动做的包升级。

3. 把本目录下的 `PatchParity/` 复制到 `Samples/Brawler/Assets/PatchParity/`，
   把 `environment/ProjectSettings/HybridCLRSettings.asset` 复制到 `Samples/Brawler/ProjectSettings/`。

4. 运行 HybridCLR 安装器（把改造版 il2cpp 复制进工程内的 HybridCLRData，不改动编辑器安装目录）：

   ```
   Unity.exe -batchmode -nographics -projectPath <Brawler> -executeMethod PatchParity.EditorTools.PatchParityHybridClr.Install -patchparityOutput <输出目录>
   ```

5. 生成并构建无头验证包（内部会先跑 HybridCLR 的完整生成流程）：

   ```
   Unity.exe -batchmode -nographics -projectPath <Brawler> -executeMethod PatchParity.EditorTools.PatchParityVerifyBuild.BuildVerifyPlayer -patchparityOutput <输出目录>
   ```

## 文件说明

| 路径 | 所属 | 作用 |
|---|---|---|
| `Bootstrap/VerifyBootstrap.cs` | 母包（AOT） | 加载热更 dll，反射调用 harness 入口，写结果并退出。不得引用任何热更程序集 |
| `Harness/VerifyEntry.cs` | 热更侧 | 构造引擎与模拟、启动世界、按种子派生输入序列，逐 tick 输出分层状态哈希 |
| `Harness/HarnessNetworkService.cs` | 热更侧 | 离线网络服务：给定玩家名单与世界随机种子（引擎默认取墙上时钟，必须显式覆盖） |
| `Editor/PatchParityVerifyBuild.cs` | 编辑器 | 配置热更清单、生成验证场景、跑 HybridCLR 生成流程、构建验证包、复制热更 dll |
| `Editor/PatchParityHotUpdate.cs` | 编辑器 | 单独的热更配置与生成入口，便于分步排查 |
| `Editor/PatchParityPatch.cs` | 编辑器 | 补丁路径的唯一动作：只重编热更程序集并替换进已有母包旁的目录 |
| `Editor/PatchParityHybridClr.cs` | 编辑器 | HybridCLR 状态查询与安装入口 |
| `Editor/PatchParityBuild.cs` | 编辑器 | 完整游戏的整包构建入口，用于测量基线耗时 |
| `Scenes/Verify.unity` | 验证场景 | 由构建脚本生成，仅含一个挂载 VerifyBootstrap 的对象 |
| `environment/environment.patch` | 被测样例 | 施加在锁定提交上的环境改动，见还原步骤第 2 步 |
| `environment/ProjectSettings/HybridCLRSettings.asset` | 被测样例 | HybridCLR 设置，含热更程序集清单 |

## 注意

- 编辑器脚本一律通过反射调用 HybridCLR 的 API，避免包未就绪时本程序集编译失败。
- 涉及编译与构建的调用必须经 `EditorApplication.delayCall` 延迟执行，详见 docs/路线图.md 的命令行注意事项。
- `.meta` 文件未入库，Unity 会重新生成；若需要稳定的资产 GUID，后续再决定是否一并入库。
- 母包启动器与热更 harness 之间的契约是 `Run(string csvPath, string options)`，options 为 `k=v;k=v` 形式。
  母包发布后不可更新，故场景参数一律经该字符串转发、由热更侧解析，新增参数不需要重打母包。
