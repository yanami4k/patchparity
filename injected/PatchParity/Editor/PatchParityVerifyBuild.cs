// patchparity 注入：构建"无头验证包"。
// 验证包只含一个自建场景（挂 VerifyBootstrap），不含样例的游戏场景与表现层。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PatchParity.EditorTools
{
    public static class PatchParityVerifyBuild
    {
        private const string SceneDir = "Assets/PatchParity/Scenes";
        private const string ScenePath = SceneDir + "/Verify.unity";

        // 被验证的逻辑层，以及我们自己的 harness
        private static readonly string[] HotUpdateAssemblies =
        {
            "Brawler.ECS",
            "Brawler.DataAssets",
            "PatchParity.Harness",
            // 表现层并不参与验证，但 Unity 会把 Assets 下所有程序集编进母包；
            // 它们引用逻辑层，若留在母包会让裁剪器报 "Failed to resolve assembly: Brawler.ECS"。
            // 一并列为热更程序集即可把它们排除出母包，验证场景不依赖它们。
            "Brawler.Manager",
            "Brawler.View",
        };

        // -executeMethod PatchParity.EditorTools.PatchParityVerifyBuild.BuildVerifyPlayer
        public static void BuildVerifyPlayer()
        {
            EditorApplication.delayCall += BuildDeferred;
        }

        private static void BuildDeferred()
        {
            var report = new List<string>();
            try
            {
                ConfigureHotUpdateAssemblies(report);
                EnsureVerifyScene(report);

                // HybridCLR 的生成流程内部要跑一次构建来取裁剪后的 AOT 程序集，
                // 因此必须在正式构建之前执行；跳过它会导致母包缺少方法桥，热更程序集加载不了。
                if (ReadArg("-patchparitySkipGenerate") == null)
                {
                    var editorAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "HybridCLR.Editor");
                    var prebuild = editorAsm.GetType("HybridCLR.Editor.Commands.PrebuildCommand");
                    prebuild.GetMethod("GenerateAll", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                    report.Add("PrebuildCommand.GenerateAll=ok");
                }

                string output = ReadArg("-patchparityOutput") ?? Path.Combine(DefaultOutputDir, "verify");

                // HybridCLR 的生成流程内部会以"生成 VS 解决方案"的方式构建一次，
                // 留下的产物与正式构建的选项不一致时，Unity 会拒绝构建
                // （BuildFailedException: Build path contains a project previously built without ...）。
                // 因此正式构建前显式关掉该选项并清空输出目录。
                UnityEditor.WindowsStandalone.UserBuildSettings.createSolution = false;
                if (Directory.Exists(output))
                {
                    Directory.Delete(output, recursive: true);
                }
                Directory.CreateDirectory(output);

                PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.IL2CPP);

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.Combine(output, "Verify.exe"),
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.None,
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                BuildReport buildReport = BuildPipeline.BuildPlayer(options);
                sw.Stop();

                report.Add($"result={buildReport.summary.result}");
                report.Add($"seconds={sw.Elapsed.TotalSeconds:F1}");
                report.Add($"errors={buildReport.summary.totalErrors}");
                report.Add($"output={options.locationPathName}");

                if (buildReport.summary.result == BuildResult.Succeeded)
                {
                    CopyHotUpdateDlls(output, report);
                    CopyAotMetadataDlls(output, report);
                }

                Finish(report, buildReport.summary.result == BuildResult.Succeeded ? 0 : 4);
            }
            catch (Exception e)
            {
                for (Exception cur = e; cur != null; cur = cur.InnerException)
                {
                    report.Add("ERROR " + cur.GetType().Name + ": " + cur.Message);
                    if (cur.InnerException == null)
                    {
                        report.Add(cur.StackTrace ?? "");
                    }
                }
                Finish(report, 9);
            }
        }

        // 热更程序集清单由本脚本统一维护，避免与手工配置不一致
        private static void ConfigureHotUpdateAssemblies(List<string> report)
        {
            var editorAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "HybridCLR.Editor");
            var settingsType = editorAsm.GetType("HybridCLR.Editor.Settings.HybridCLRSettings");
            object settings = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            settingsType.GetField("hotUpdateAssemblies", BindingFlags.Public | BindingFlags.Instance).SetValue(settings, HotUpdateAssemblies);
            settingsType.GetField("enable", BindingFlags.Public | BindingFlags.Instance)?.SetValue(settings, true);
            settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
            report.Add("hotUpdateAssemblies=" + string.Join(",", HotUpdateAssemblies));
        }

        // 验证场景由代码生成：内容只有一个挂着 VerifyBootstrap 的对象，避免手工场景漂移
        private static void EnsureVerifyScene(List<string> report)
        {
            Directory.CreateDirectory(SceneDir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var go = new GameObject("PatchParityVerify");
            var bootstrapType = AppDomain.CurrentDomain.GetAssemblies()
                .First(a => a.GetName().Name == "PatchParity.Bootstrap")
                .GetType("PatchParity.Bootstrap.VerifyBootstrap");
            go.AddComponent(bootstrapType);
            EditorSceneManager.SaveScene(scene, ScenePath);
            report.Add("scene=" + ScenePath);
        }

        // 热更 dll 放进包旁的目录，运行时由启动器读取
        private static void CopyHotUpdateDlls(string output, List<string> report)
        {
            string src = Path.Combine(Directory.GetCurrentDirectory(), "HybridCLRData", "HotUpdateDlls", "StandaloneWindows64");
            string dst = Path.Combine(output, "HotUpdateDlls");
            Directory.CreateDirectory(dst);
            foreach (string name in HotUpdateAssemblies)
            {
                string from = Path.Combine(src, name + ".dll");
                if (!File.Exists(from))
                {
                    report.Add("WARN 热更 dll 缺失: " + from);
                    continue;
                }
                string to = Path.Combine(dst, name + ".dll");
                File.Copy(from, to, overwrite: true);
                report.Add($"dll={name}.dll size={new FileInfo(to).Length}");
            }
        }

        // 需要补充元数据的 AOT 程序集：母包里的泛型方法若以热更类型实例化，
        // 在母包中并不存在对应代码，必须由解释器按裁剪前的元数据执行。
        // 清单由 HybridCLR 生成流程写进 AOTGenericReferences.PatchedAOTAssemblyList，此处解析该文件，
        // 避免手工维护与生成结果脱节。
        private static void CopyAotMetadataDlls(string output, List<string> report)
        {
            string refsPath = Path.Combine(Directory.GetCurrentDirectory(), "Assets", "HybridCLRGenerate", "AOTGenericReferences.cs");
            if (!File.Exists(refsPath))
            {
                report.Add("WARN 未找到 AOTGenericReferences.cs，跳过 AOT 元数据复制");
                return;
            }

            var names = new List<string>();
            bool inList = false;
            foreach (string line in File.ReadAllLines(refsPath))
            {
                if (line.Contains("PatchedAOTAssemblyList"))
                {
                    inList = true;
                    continue;
                }
                if (!inList) continue;
                if (line.Contains("};")) break;
                int a = line.IndexOf('"');
                int b = line.LastIndexOf('"');
                if (a >= 0 && b > a) names.Add(line.Substring(a + 1, b - a - 1));
            }

            string src = Path.Combine(Directory.GetCurrentDirectory(), "HybridCLRData", "AssembliesPostIl2CppStrip", "StandaloneWindows64");
            string dst = Path.Combine(output, "AotDlls");
            Directory.CreateDirectory(dst);
            foreach (string name in names)
            {
                string from = Path.Combine(src, name);
                if (!File.Exists(from))
                {
                    report.Add("WARN AOT 元数据 dll 缺失: " + name);
                    continue;
                }
                File.Copy(from, Path.Combine(dst, name), overwrite: true);
            }
            report.Add("aotMetadata=" + string.Join(",", names));
        }

        private static void Finish(List<string> report, int exitCode)
        {
            string text = string.Join("\n", report);
            UnityEngine.Debug.Log("[patchparity][verify-build]\n" + text);
            string dir = ReadArg("-patchparityOutput") ?? Path.Combine(DefaultOutputDir, "verify");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "verify-build.txt"), text + "\n");
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
