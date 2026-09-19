# injected —— 注入被测样例的源码

本目录是 patchparity 注入到被测样例（xpTURN/Klotho 的 Samples/Brawler）中的全部代码，入库保存。被测样例本身按裁决 002 锁定 commit、以浅克隆放在 vendor/ 下，不入库。

## 还原步骤

1. 克隆样例并锁定 commit：

   ```
   git clone --depth 1 https://github.com/xpTURN/Klotho.git vendor/Klotho
   cd vendor/Klotho && git checkout e29c4892c88db097f3ac0d135874c82e9b6726d6
   ```

2. 在样例工程的包清单 `Samples/Brawler/Packages/manifest.json` 的 dependencies 中加入：

   ```
   "com.code-philosophy.hybridclr": "https://github.com/focus-creative-games/hybridclr_unity.git#v8.14.1"
   ```

3. 把本目录下的 `PatchParity/` 整体复制到 `Samples/Brawler/Assets/PatchParity/`。

4. 运行 HybridCLR 安装器（把改造版 il2cpp 复制进工程内的 HybridCLRData，不改动编辑器安装目录）：

   ```
   Unity.exe -batchmode -nographics -projectPath <Brawler> -executeMethod PatchParity.EditorTools.PatchParityHybridClr.Install
   ```

5. 生成并构建无头验证包（内部会先跑 HybridCLR 的完整生成流程）：

   ```
   Unity.exe -batchmode -nographics -projectPath <Brawler> -executeMethod PatchParity.EditorTools.PatchParityVerifyBuild.BuildVerifyPlayer -patchparityOutput <输出目录>
   ```

## 文件说明

| 路径 | 所属 | 作用 |
|---|---|---|
| `Bootstrap/VerifyBootstrap.cs` | 母包（AOT） | 加载热更 dll，反射调用 harness 入口，写结果并退出。不得引用任何热更程序集 |
| `Harness/VerifyEntry.cs` | 热更侧 | 构造引擎与模拟、注册玩法系统、初始化世界，逐 tick 输出状态哈希 CSV |
| `Editor/PatchParityVerifyBuild.cs` | 编辑器 | 配置热更清单、生成验证场景、跑 HybridCLR 生成流程、构建验证包、复制热更 dll |
| `Editor/PatchParityHotUpdate.cs` | 编辑器 | 单独的热更配置与生成入口，便于分步排查 |
| `Editor/PatchParityHybridClr.cs` | 编辑器 | HybridCLR 状态查询与安装入口 |
| `Editor/PatchParityBuild.cs` | 编辑器 | 完整游戏的整包构建入口，用于测量基线耗时 |
| `Scenes/Verify.unity` | 验证场景 | 由构建脚本生成，仅含一个挂载 VerifyBootstrap 的对象 |

## 注意

- 编辑器脚本一律通过反射调用 HybridCLR 的 API，避免包未就绪时本程序集编译失败。
- 涉及编译与构建的调用必须经 `EditorApplication.delayCall` 延迟执行，详见 docs/路线图.md 的命令行注意事项。
- `.meta` 文件未入库，Unity 会重新生成；若需要稳定的资产 GUID，后续再决定是否一并入库。
