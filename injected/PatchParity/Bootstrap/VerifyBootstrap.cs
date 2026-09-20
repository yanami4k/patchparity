// patchparity 母包侧启动器：加载热更程序集并反射调用 harness 入口。
// 本程序集进母包（AOT），因此不得引用任何热更程序集（Brawler.*、PatchParity.Harness）。
// 依赖方向严格单向：母包 -> 反射 -> 热更。
using System;
using System.IO;
using System.Reflection;
using HybridCLR;
using UnityEngine;

namespace PatchParity.Bootstrap
{
    public sealed class VerifyBootstrap : MonoBehaviour
    {
        // 默认加载清单，顺序为被依赖者在前。可由 -patchparityDllList 覆盖，
        // 母包发布后不可更新，故热更组成变化不应要求重打母包。
        //
        // 注意：热更程序集清单与加载清单不是一回事。表现层（Brawler.Manager、Brawler.View）
        // 被列为热更程序集只是为了把它们排除出母包——它们引用逻辑层，留在母包会让裁剪器失败。
        // 验证不使用它们，且它们依赖的类型已被母包裁掉，加载它们会抛 TypeLoadException。
        private const string DefaultDllList = "Brawler.DataAssets.dll,Brawler.ECS.dll,PatchParity.Harness.dll";

        private void Start()
        {
            string resultPath = ReadArg("-patchparityResult") ?? Path.Combine(Application.persistentDataPath, "patchparity-run.txt");
            string csvPath = ReadArg("-patchparityCsv") ?? Path.Combine(Application.persistentDataPath, "hashes.csv");
            string dllDir = ReadArg("-patchparityDlls") ?? Path.Combine(Application.streamingAssetsPath, "HotUpdateDlls");

            // 场景参数原样转发给热更侧，母包不解释其内容。
            // 母包发布后不可更新，若把参数写进调用签名，以后每加一个场景参数都要重打母包。
            string scenario = ReadArg("-patchparityScenario") ?? "";

            string status;
            int exitCode;
            try
            {
                // 先补充 AOT 元数据，再加载热更程序集，顺序不可颠倒。
                // 母包里的泛型方法（如 Frame.Add<T>）只编译了 AOT 侧用到的实例化；
                // 以热更类型作实参的实例化在母包里不存在，必须由解释器按裁剪前的元数据执行，
                // 否则运行到该处会抛 MissingMethodException: AOT generic method not instantiated in aot。
                // 待补清单由 HybridCLR 生成流程给出（AOTGenericReferences.PatchedAOTAssemblyList）。
                string aotDir = ReadArg("-patchparityAotDlls") ?? Path.Combine(dllDir, "..", "AotDlls");
                if (Directory.Exists(aotDir))
                {
                    foreach (string aot in Directory.GetFiles(aotDir, "*.dll"))
                    {
                        var code = RuntimeApi.LoadMetadataForAOTAssembly(File.ReadAllBytes(aot), HomologousImageMode.SuperSet);
                        if (code != LoadImageErrorCode.OK)
                        {
                            throw new InvalidOperationException("补充 AOT 元数据失败: " + Path.GetFileName(aot) + " -> " + code);
                        }
                    }
                }

                string[] dllList = (ReadArg("-patchparityDllList") ?? DefaultDllList)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string dll in dllList)
                {
                    string path = Path.Combine(dllDir, dll);
                    byte[] bytes = File.ReadAllBytes(path);
                    Assembly.Load(bytes);
                }

                // 反射调用，避免母包静态引用热更类型
                var entryType = Type.GetType("PatchParity.Harness.VerifyEntry, PatchParity.Harness", throwOnError: true);
                var run = entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                object summary = run.Invoke(null, new object[] { csvPath, scenario });
                status = "OK " + summary;
                exitCode = 0;
            }
            catch (Exception e)
            {
                var inner = e;
                while (inner.InnerException != null)
                {
                    inner = inner.InnerException;
                }
                status = "FAIL " + inner.GetType().Name + ": " + inner.Message + "\n" + inner.StackTrace;
                exitCode = 1;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
            File.WriteAllText(resultPath, status + "\n");
            Debug.Log("[patchparity] " + status);
            Application.Quit(exitCode);
        }

        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

    }
}
