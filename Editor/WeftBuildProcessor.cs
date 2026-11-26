using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Weft.Editor
{
    /// <summary>
    /// Selects between the Dev (WEFT_DEVTOOLS=ON) and Release (WEFT_DEVTOOLS=OFF) builds of weft.dll when packaging
    /// Windows x64 Standalone players: Development builds and builds opting in via <see cref="WeftBuildSettings.DevToolsInRelease"/>
    /// (or <c>-WeftDevToolsInRelease</c>) ship Dev; Release players swap in the stripped Release DLL automatically.
    /// </summary>
    public sealed class WeftBuildProcessor : IPostprocessBuildWithReport
    {
        public int callbackOrder => 100;

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.StandaloneWindows64)
            {
                return;
            }
            var isDevelopment = (report.summary.options & BuildOptions.Development) != 0;
            var keepDevTools = isDevelopment || WeftBuildSettings.DevToolsInRelease || HasCliFlag("-WeftDevToolsInRelease");
            if (keepDevTools)
            {
                Debug.Log("Weft: packaged dev weft.dll (DevTools available on demand).");
                return;
            }
            var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(WeftBuildProcessor).Assembly);
            if (packageInfo == null)
            {
                return;
            }
            var releaseDll = Path.Combine(packageInfo.resolvedPath, "Runtime", "Plugins~", "Release", "x86_64", "weft.dll");
            if (!File.Exists(releaseDll))
            {
                Debug.LogWarning(
                    $"Weft: release DLL not found at {releaseDll}; run Scripts~/BuildWeft.ps1 (without -SkipRelease) to build the stripped Release weft.dll.");
                return;
            }
            var exePath = report.summary.outputPath;
            var buildDir = Path.GetDirectoryName(exePath) ?? string.Empty;
            var dataDir = Path.Combine(buildDir, Path.GetFileNameWithoutExtension(exePath) + "_Data");
            var candidates = new[]
            {
                Path.Combine(dataDir, "Plugins", "x86_64", "weft.dll"),
                Path.Combine(dataDir, "Plugins", "weft.dll"),
            };
            foreach (var dst in candidates)
            {
                if (File.Exists(dst))
                {
                    File.Copy(releaseDll, dst, true);
                    Debug.Log($"Weft: packaged stripped Release weft.dll (no DevTools, no WS2_32.dll) -> {dst}");
                    return;
                }
            }
            Debug.LogWarning($"Weft: could not locate weft.dll under {dataDir}/Plugins to swap with Release build.");
        }

        static bool HasCliFlag(string flag)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; ++i)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
