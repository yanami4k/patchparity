// patchparity 注入的构建入口：供命令行调用，切换脚本后端并做整包构建计时。
// 本文件属于 patchparity 对被测样例的改造，不是 Klotho 上游代码。
using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PatchParity.EditorTools
{
    public static class PatchParityBuild
    {
        // 命令行：-executeMethod PatchParity.EditorTools.PatchParityBuild.BuildWindowsIl2cpp
        //         -patchparityOutput <输出目录>
        public static void BuildWindowsIl2cpp()
        {
            string output = ReadArg("-patchparityOutput") ?? Path.Combine(DefaultOutputDir, "baseline");
            Directory.CreateDirectory(output);

            var group = NamedBuildTarget.Standalone;
            var before = PlayerSettings.GetScriptingBackend(group);
            PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
            UnityEngine.Debug.Log($"[patchparity] 脚本后端 {before} -> {PlayerSettings.GetScriptingBackend(group)}");

            var scenes = new[] { "Assets/Brawler/Scenes/BrawlerScene.unity" };
            foreach (var scene in scenes)
            {
                if (!File.Exists(scene))
                {
                    UnityEngine.Debug.LogError($"[patchparity] 场景不存在: {scene}");
                    EditorApplication.Exit(3);
                    return;
                }
            }

            var options = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(output, "Brawler.exe"),
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            };

            var sw = Stopwatch.StartNew();
            BuildReport report = BuildPipeline.BuildPlayer(options);
            sw.Stop();

            var summary = report.summary;
            UnityEngine.Debug.Log(
                $"[patchparity] 构建结果={summary.result} 耗时={sw.Elapsed.TotalSeconds:F1}s " +
                $"产物大小={summary.totalSize / (1024f * 1024f):F1}MB 错误={summary.totalErrors} 警告={summary.totalWarnings}");

            File.WriteAllText(
                Path.Combine(output, "patchparity-build.txt"),
                $"result={summary.result}\nseconds={sw.Elapsed.TotalSeconds:F1}\nsizeMB={summary.totalSize / (1024f * 1024f):F1}\n" +
                $"errors={summary.totalErrors}\nwarnings={summary.totalWarnings}\nbackend=IL2CPP\nutc={DateTime.UtcNow:O}\n");

            EditorApplication.Exit(summary.result == BuildResult.Succeeded ? 0 : 4);
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
