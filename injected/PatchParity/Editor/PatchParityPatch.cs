// patchparity 注入：补丁路径的唯一动作——只重编热更程序集，把 dll 替换进已有母包旁的目录。
// 母包（含 AOT 元数据、方法桥、裁剪结果）保持不动，这正是要验证的场景：
// 母包是旧的，热更 dll 是新的，二者能否等价于整包重编。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PatchParity.EditorTools
{
    public static class PatchParityPatch
    {
        // -executeMethod PatchParity.EditorTools.PatchParityPatch.CompileHotUpdate
        //   -patchparityTarget <母包目录>   把编译结果复制到该目录的 HotUpdateDlls 下（可省略，只编译不复制）
        //   -patchparityOutput <报告目录>
        public static void CompileHotUpdate()
        {
            EditorApplication.delayCall += CompileDeferred;
        }

        private static void CompileDeferred()
        {
            var report = new List<string>();
            try
            {
                var editorAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "HybridCLR.Editor");

                var compilerType = editorAsm.GetType("HybridCLR.Editor.Commands.CompileDllCommand");
                var compile = compilerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.Name == "CompileDllActiveBuildTarget" && m.GetParameters().Length == 0);

                var sw = System.Diagnostics.Stopwatch.StartNew();
                compile.Invoke(null, null);
                sw.Stop();
                report.Add($"compileSeconds={sw.Elapsed.TotalSeconds:F1}");

                // 热更清单以 HybridCLR 设置为准，避免与构建脚本的清单不一致
                var settingsType = editorAsm.GetType("HybridCLR.Editor.Settings.HybridCLRSettings");
                object settings = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                var names = (string[])settingsType.GetField("hotUpdateAssemblies", BindingFlags.Public | BindingFlags.Instance).GetValue(settings);
                report.Add("hotUpdateAssemblies=" + string.Join(",", names));

                string src = Path.Combine(Directory.GetCurrentDirectory(), "HybridCLRData", "HotUpdateDlls", "StandaloneWindows64");
                string target = ReadArg("-patchparityTarget");
                foreach (string name in names)
                {
                    string from = Path.Combine(src, name + ".dll");
                    if (!File.Exists(from))
                    {
                        report.Add("WARN 热更 dll 缺失: " + name);
                        continue;
                    }
                    report.Add($"dll={name}.dll size={new FileInfo(from).Length} sha256={Sha256(from)}");
                    if (target != null)
                    {
                        string dstDir = Path.Combine(target, "HotUpdateDlls");
                        Directory.CreateDirectory(dstDir);
                        File.Copy(from, Path.Combine(dstDir, name + ".dll"), overwrite: true);
                    }
                }
                report.Add("target=" + (target ?? "(none)"));

                Finish(report, 0);
            }
            catch (Exception e)
            {
                for (Exception cur = e; cur != null; cur = cur.InnerException)
                {
                    report.Add("ERROR " + cur.GetType().Name + ": " + cur.Message);
                    if (cur.InnerException == null && cur.StackTrace != null)
                    {
                        report.Add(cur.StackTrace);
                    }
                }
                Finish(report, 9);
            }
        }

        // dll 指纹用于判定"补丁确实换了内容"，也用于后续的缓存键
        private static string Sha256(string path)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").Substring(0, 16);
            }
        }

        private static void Finish(List<string> report, int exitCode)
        {
            string text = string.Join("\n", report);
            UnityEngine.Debug.Log("[patchparity][patch]\n" + text);
            string dir = ReadArg("-patchparityOutput") ?? DefaultOutputDir;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "patch-compile.txt"), text + "\n");
            EditorApplication.Exit(exitCode);
        }

        // 默认输出目录相对 Unity 工程，避免把本机路径写进代码；
        // 流水线一律显式传 -patchparityOutput。
        private static string DefaultOutputDir =>
            Path.Combine(Directory.GetCurrentDirectory(), "PatchParityArtifacts");

        private static string ReadArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
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
