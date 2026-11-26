using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Weft.Editor
{
    /// <summary>
    /// Project-level Weft build settings stored in <c>ProjectSettings/WeftSettings.json</c>
    /// (Project Settings &gt; Weft). Controls whether Release player builds keep the Dev weft.dll (with DevTools)
    /// instead of shipping the stripped Release weft.dll.
    /// </summary>
    public static class WeftBuildSettings
    {
        const string FilePath = "ProjectSettings/WeftSettings.json";

        [Serializable]
        sealed class Data
        {
            public bool devToolsInRelease;
        }

        static Data cached;

        /// <summary>
        /// When true, non-Development Windows x64 player builds ship the Dev weft.dll (with Chrome DevTools dormant until requested).
        /// Default is false: Release builds ship the stripped Release weft.dll with zero DevTools code or WS2_32.dll dependency.
        /// </summary>
        public static bool DevToolsInRelease
        {
            get => Load().devToolsInRelease;
            set
            {
                var data = Load();
                if (data.devToolsInRelease == value)
                {
                    return;
                }
                data.devToolsInRelease = value;
                Save(data);
            }
        }

        static Data Load()
        {
            if (cached != null)
            {
                return cached;
            }
            if (File.Exists(FilePath))
            {
                try
                {
                    cached = JsonUtility.FromJson<Data>(File.ReadAllText(FilePath)) ?? new Data();
                    return cached;
                }
                catch
                {
                }
            }
            cached = new Data();
            return cached;
        }

        static void Save(Data data)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(FilePath, JsonUtility.ToJson(data, true) + "\n");
        }

        [SettingsProvider]
        static SettingsProvider CreateProvider()
        {
            return new SettingsProvider("Project/Weft", SettingsScope.Project)
            {
                label = "Weft",
                keywords = new[] { "weft", "devtools", "ui", "release", "shipping" },
                guiHandler = _ =>
                {
                    EditorGUILayout.Space(8);
                    EditorGUILayout.LabelField("Windows x64 Player Packaging", EditorStyles.boldLabel);
                    DevToolsInRelease = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "Include DevTools in Release Builds",
                            "By default, Development builds and Editor use the Dev weft.dll while Release builds swap in the stripped Release weft.dll (no DevTools, no WS2_32.dll). Enable this (or pass -WeftDevToolsInRelease on the command line) to keep DevTools available in Release players."),
                        DevToolsInRelease);
                },
            };
        }
    }
}
