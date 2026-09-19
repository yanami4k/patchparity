// patchparity 母包侧启动器：加载热更程序集并反射调用 harness 入口。
// 本程序集进母包（AOT），因此不得引用任何热更程序集（Brawler.*、PatchParity.Harness）。
// 依赖方向严格单向：母包 -> 反射 -> 热更。
using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PatchParity.Bootstrap
{
    public sealed class VerifyBootstrap : MonoBehaviour
    {
        // 热更程序集加载顺序：被依赖者在前
        private static readonly string[] HotUpdateDlls =
        {
            "Brawler.DataAssets.dll",
            "Brawler.ECS.dll",
            "PatchParity.Harness.dll",
        };

        private void Start()
        {
            string resultPath = ReadArg("-patchparityResult") ?? Path.Combine(Application.persistentDataPath, "patchparity-run.txt");
            string csvPath = ReadArg("-patchparityCsv") ?? Path.Combine(Application.persistentDataPath, "hashes.csv");
            string dllDir = ReadArg("-patchparityDlls") ?? Path.Combine(Application.streamingAssetsPath, "HotUpdateDlls");
            int ticks = ReadIntArg("-patchparityTicks", 600);
            int seed = ReadIntArg("-patchparitySeed", 42);

            string status;
            int exitCode;
            try
            {
                foreach (string dll in HotUpdateDlls)
                {
                    string path = Path.Combine(dllDir, dll);
                    byte[] bytes = File.ReadAllBytes(path);
                    Assembly.Load(bytes);
                }

                // 反射调用，避免母包静态引用热更类型
                var entryType = Type.GetType("PatchParity.Harness.VerifyEntry, PatchParity.Harness", throwOnError: true);
                var run = entryType.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                object summary = run.Invoke(null, new object[] { csvPath, seed, ticks, 512, 25 });
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

        private static int ReadIntArg(string name, int fallback)
        {
            string raw = ReadArg(name);
            return raw != null && int.TryParse(raw, out int value) ? value : fallback;
        }
    }
}
