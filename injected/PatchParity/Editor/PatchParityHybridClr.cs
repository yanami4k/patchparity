// patchparity 注入：HybridCLR 的命令行入口（安装、生成、编译热更程序集）。
// 通过反射调用 HybridCLR.Editor 的 API，避免在包未就绪时编译失败。
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace PatchParity.EditorTools
{
    public static class PatchParityHybridClr
    {
        // -executeMethod PatchParity.EditorTools.PatchParityHybridClr.ReportStatus
        public static void ReportStatus()
        {
            var lines = new System.Collections.Generic.List<string>();
            var asm = FindEditorAssembly();
            lines.Add($"hybridclr.editor.assembly={(asm == null ? "未找到" : asm.GetName().Name)}");

            if (asm != null)
            {
                var controllerType = asm.GetType("HybridCLR.Editor.Installer.InstallerController");
                lines.Add($"InstallerController={(controllerType == null ? "未找到" : "存在")}");
                if (controllerType != null)
                {
                    object controller = Activator.CreateInstance(controllerType);
                    foreach (var name in new[] { "PackageVersion", "InstalledLibil2cppVersion", "Il2CppBranch", "HasInstalledHybridCLR" })
                    {
                        var prop = controllerType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        if (prop != null)
                        {
                            object value = null;
                            try { value = prop.GetValue(controller); } catch (Exception e) { value = "读取失败: " + e.Message; }
                            lines.Add($"{name}={value}");
                        }
                    }
                }
            }

            string report = string.Join("\n", lines);
            UnityEngine.Debug.Log("[patchparity][hybridclr]\n" + report);
            WriteReport("hybridclr-status.txt", report);
            EditorApplication.Exit(asm == null ? 5 : 0);
        }

        // -executeMethod PatchParity.EditorTools.PatchParityHybridClr.Install
        public static void Install()
        {
            var asm = FindEditorAssembly();
            if (asm == null)
            {
                UnityEngine.Debug.LogError("[patchparity] 未找到 HybridCLR.Editor 程序集");
                EditorApplication.Exit(5);
                return;
            }

            var controllerType = asm.GetType("HybridCLR.Editor.Installer.InstallerController");
            object controller = Activator.CreateInstance(controllerType);
            var install = controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "InstallDefaultHybridCLR" && m.GetParameters().Length == 0);
            if (install == null)
            {
                var names = string.Join(", ", controllerType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Select(m => m.Name).Distinct());
                UnityEngine.Debug.LogError("[patchparity] 未找到无参 InstallDefaultHybridCLR，可用方法: " + names);
                WriteReport("hybridclr-install.txt", "methods=" + names);
                EditorApplication.Exit(6);
                return;
            }

            install.Invoke(controller, null);
            var hasInstalled = controllerType.GetProperty("HasInstalledHybridCLR", BindingFlags.Public | BindingFlags.Instance);
            object installed = hasInstalled?.GetValue(controller);
            string report = $"HasInstalledHybridCLR={installed}";
            UnityEngine.Debug.Log("[patchparity][hybridclr] " + report);
            WriteReport("hybridclr-install.txt", report);
            EditorApplication.Exit(installed is bool b && b ? 0 : 7);
        }

        private static Assembly FindEditorAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "HybridCLR.Editor");
        }

        private static void WriteReport(string fileName, string content)
        {
            string dir = ReadArg("-patchparityOutput") ?? "PatchParityArtifacts";
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, fileName), content + "\n");
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
