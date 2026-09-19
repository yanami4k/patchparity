// patchparity 注入：配置热更程序集并驱动 HybridCLR 的生成与编译。
// 全部通过反射调用 HybridCLR.Editor，避免包缺失时本程序集编译失败。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PatchParity.EditorTools
{
    public static class PatchParityHotUpdate
    {
        // 逻辑层程序集：Klotho 侧已用 asmdef 的 noEngineReferences 保证不引用 UnityEngine
        private static readonly string[] HotUpdateAssemblyNames = { "Brawler.ECS", "Brawler.DataAssets" };

        // -executeMethod PatchParity.EditorTools.PatchParityHotUpdate.Configure
        public static void Configure()
        {
            var report = new List<string>();
            try
            {
                var settings = LoadSettings(out var settingsType);
                var field = settingsType.GetField("hotUpdateAssemblies", BindingFlags.Public | BindingFlags.Instance);
                field.SetValue(settings, HotUpdateAssemblyNames);

                var enableField = settingsType.GetField("enable", BindingFlags.Public | BindingFlags.Instance);
                enableField?.SetValue(settings, true);

                // 设置存放在 ProjectSettings/HybridCLRSettings.asset，须调用其自带的 Save
                var save = settingsType.GetMethod("Save", BindingFlags.Public | BindingFlags.Static);
                save?.Invoke(null, null);

                report.Add("enable=true");
                report.Add("hotUpdateAssemblies=" + string.Join(",", HotUpdateAssemblyNames));
                report.Add("settingsSaved=" + (save != null));
                Finish("hotupdate-configure.txt", report, 0);
            }
            catch (Exception e)
            {
                report.Add("ERROR " + e.GetType().Name + ": " + e.Message);
                Finish("hotupdate-configure.txt", report, 8);
            }
        }

        // -executeMethod PatchParity.EditorTools.PatchParityHotUpdate.GenerateAll
        // 生成 link.xml、AOT 泛型引用、方法桥等，并编译热更程序集。
        // 注意：编译 API 不能在命令行解析阶段同步调用（会导致编辑器崩溃），
        // 必须推迟到编辑器完成启动后执行，故这里只注册延迟回调。
        public static void GenerateAll()
        {
            EditorApplication.delayCall += GenerateAllDeferred;
        }

        private static void GenerateAllDeferred()
        {
            var report = new List<string>();
            try
            {
                var editorAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "HybridCLR.Editor");

                // 编译热更程序集
                var compilerType = editorAsm.GetType("HybridCLR.Editor.Commands.CompileDllCommand");
                var compile = compilerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "CompileDllActiveBuildTarget" && m.GetParameters().Length == 0);
                compile.Invoke(null, null);
                report.Add("CompileDllActiveBuildTarget=ok");

                // 官方一键流程：link.xml、方法桥、反向调用桩、AOT 泛型引用、裁剪后 AOT dll 等按正确顺序生成
                InvokeStaticNoArg(editorAsm, "HybridCLR.Editor.Commands.PrebuildCommand", "GenerateAll", report);

                var dllDir = Path.Combine(Directory.GetCurrentDirectory(), "HybridCLRData", "HotUpdateDlls");
                if (Directory.Exists(dllDir))
                {
                    foreach (var f in Directory.GetFiles(dllDir, "*.dll", SearchOption.AllDirectories))
                    {
                        report.Add("dll=" + f.Substring(Directory.GetCurrentDirectory().Length + 1).Replace('\\', '/') +
                                   " size=" + new FileInfo(f).Length);
                    }
                }
                else
                {
                    report.Add("WARN 热更 dll 输出目录不存在: " + dllDir);
                }

                Finish("hotupdate-generate.txt", report, 0);
            }
            catch (Exception e)
            {
                // 反射调用的异常包在 TargetInvocationException 里，逐层展开才看得到真正原因
                for (Exception cur = e; cur != null; cur = cur.InnerException)
                {
                    report.Add("ERROR " + cur.GetType().Name + ": " + cur.Message);
                    if (cur.InnerException == null && cur.StackTrace != null)
                    {
                        report.Add(cur.StackTrace);
                    }
                }
                Finish("hotupdate-generate.txt", report, 9);
            }
        }

        private static void InvokeStaticNoArg(Assembly asm, string typeName, string methodName, List<string> report)
        {
            var type = asm.GetType(typeName);
            if (type == null)
            {
                report.Add($"SKIP {typeName} 未找到");
                return;
            }
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == 0);
            if (method == null)
            {
                var names = string.Join(",", type.GetMethods(BindingFlags.Public | BindingFlags.Static).Select(m => m.Name).Distinct());
                report.Add($"SKIP {typeName}.{methodName} 未找到，可用: {names}");
                return;
            }
            method.Invoke(null, null);
            report.Add($"{typeName.Split('.').Last()}.{methodName}=ok");
        }

        private static object LoadSettings(out Type settingsType)
        {
            var editorAsm = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "HybridCLR.Editor");
            // 完整路径含 .Settings 命名空间
            settingsType = editorAsm.GetType("HybridCLR.Editor.Settings.HybridCLRSettings")
                ?? throw new InvalidOperationException("未找到 HybridCLR.Editor.Settings.HybridCLRSettings，实际类型: "
                    + string.Join(",", editorAsm.GetTypes().Where(t => t.Name.Contains("Settings")).Select(t => t.FullName)));
            var prop = settingsType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            return prop.GetValue(null);
        }

        private static void Finish(string fileName, List<string> report, int exitCode)
        {
            string text = string.Join("\n", report);
            UnityEngine.Debug.Log("[patchparity][hotupdate]\n" + text);
            string dir = ReadArg("-patchparityOutput") ?? "PatchParityArtifacts";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), text + "\n");
            EditorApplication.Exit(exitCode);
        }

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
