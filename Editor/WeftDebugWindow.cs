using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace Weft.Editor
{
    /// <summary>
    /// Live inspector for Weft Views and Chrome DevTools: Window &gt; Weft &gt; Inspector.
    /// Shows every active View's page, logical size, scale, DOM element count, Game/Draw ms (avg / slowest over 1s),
    /// animation state, error/warning counts, and a one-click Copy address button for Chrome DevTools.
    /// </summary>
    public sealed class WeftDebugWindow : EditorWindow
    {
        const double RefreshSeconds = 0.5;

        readonly List<WeftViewDebugInfo> views = new List<WeftViewDebugInfo>();
        string listen = string.Empty;
        string error = string.Empty;
        Vector2 scroll;
        double nextRefresh;

        [MenuItem("Window/Weft/Inspector")]
        public static void Open()
        {
            var window = GetWindow<WeftDebugWindow>("Weft");
            window.minSize = new Vector2(760, 220);
            window.Show();
        }

        void OnEnable()
        {
            nextRefresh = 0;
            EditorApplication.update += Tick;
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
        }

        void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextRefresh)
            {
                return;
            }
            nextRefresh = EditorApplication.timeSinceStartup + RefreshSeconds;
            Refresh();
            Repaint();
        }

        void Refresh()
        {
            WeftDebugTools.GetViews(views);
        }

        void OnGUI()
        {
            GUILayout.Space(6);
            DrawToolbar();

            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            GUILayout.Space(4);
            DrawTable();

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                "Per-frame costs are measured only while this window or the on-screen overlay is visible.",
                EditorStyles.miniLabel);
            GUILayout.Space(4);
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("DevTools", EditorStyles.boldLabel, GUILayout.Width(64));
                listen = EditorGUILayout.TextField(listen, GUILayout.Width(145));

                var port = WeftDebugTools.DevToolsPort;
                var buttonLabel = port > 0 ? "Stop" : "Start";
                if (GUILayout.Button(buttonLabel, EditorStyles.toolbarButton, GUILayout.Width(58)))
                {
                    error = string.Empty;
                    if (port > 0)
                    {
                        WeftDebugTools.StopDevTools();
                    }
                    else if (!WeftDebugTools.StartDevTools(listen, out var why))
                    {
                        error = why;
                    }
                    Refresh();
                }

                GUILayout.Space(8);
                var status = !WeftRuntime.IsRunning
                    ? "Runtime idle (enter Play Mode)"
                    : port > 0
                        ? $"Listening on {WeftDebugTools.DevToolsAddress}  (chrome://inspect)"
                        : WeftRuntime.HasDevTools
                            ? "Off (empty or 0 picks first free port from 9230)"
                            : "Release DLL loaded (DevTools unavailable)";
                GUILayout.Label(status, EditorStyles.miniLabel, GUILayout.ExpandWidth(true));

                var overlay = WeftDebugTools.IsOverlayShown;
                var nextOverlay = GUILayout.Toggle(overlay, "On screen", EditorStyles.toolbarButton, GUILayout.Width(80));
                if (nextOverlay != overlay)
                {
                    WeftDebugTools.ShowOverlay(nextOverlay);
                }
            }
        }

        void DrawTable()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                Header("View", 54);
                Header("Page", 120, true);
                Header("Size", 82);
                Header("Scale", 44);
                Header("Elements", 62);
                Header("Game ms", 86);
                Header("Draw ms", 86);
                Header("Anim", 42);
                Header("Errors", 48);
                Header("Warn", 48);
                Header("DevTools", 96);
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (views.Count == 0)
            {
                GUILayout.Space(8);
                EditorGUILayout.LabelField("  No active Weft Views.", EditorStyles.centeredGreyMiniLabel);
            }
            else
            {
                var errorColor = new Color(1f, 0.35f, 0.3f);
                var warnColor = new Color(1f, 0.8f, 0.25f);
                for (var i = 0; i < views.Count; ++i)
                {
                    var v = views[i];
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        Cell(v.Target, 54);
                        Cell(string.IsNullOrEmpty(v.Page) ? "(no page)" : v.Page, 120, true);
                        Cell(string.Format(CultureInfo.InvariantCulture, "{0:0} x {1:0}", v.Size.x, v.Size.y), 82);
                        Cell(string.Format(CultureInfo.InvariantCulture, "{0:0.00}", v.DeviceScale), 44);
                        Cell(v.Elements >= 0 ? v.Elements.ToString(CultureInfo.InvariantCulture) : "-", 62);
                        Cell(string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00}", v.GameMs, v.GameMaxMs), 86);
                        Cell(string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00}", v.DrawMs, v.DrawMaxMs), 86);
                        Cell(v.Animated ? "yes" : "no", 42);
                        ColoredCell(v.Errors.ToString(CultureInfo.InvariantCulture), 48, v.Errors > 0 ? errorColor : (Color?)null);
                        ColoredCell(v.Warnings.ToString(CultureInfo.InvariantCulture), 48, v.Warnings > 0 ? warnColor : (Color?)null);

                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(v.DevToolsUrl)))
                        {
                            var tip = string.IsNullOrEmpty(v.DevToolsUrl)
                                ? "Start DevTools to copy the Chrome inspector URL."
                                : $"Paste into Chrome's address bar: {v.DevToolsUrl}";
                            if (GUILayout.Button(new GUIContent("Copy address", tip), GUILayout.Width(96)))
                            {
                                EditorGUIUtility.systemCopyBuffer = v.DevToolsUrl;
                            }
                        }
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        static void Header(string label, float width, bool expand = false)
        {
            if (expand)
            {
                GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.MinWidth(width), GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.Width(width));
            }
        }

        static void Cell(string text, float width, bool expand = false)
        {
            if (expand)
            {
                GUILayout.Label(text, GUILayout.MinWidth(width), GUILayout.ExpandWidth(true));
            }
            else
            {
                GUILayout.Label(text, GUILayout.Width(width));
            }
        }

        static void ColoredCell(string text, float width, Color? color)
        {
            var prev = GUI.contentColor;
            if (color.HasValue)
            {
                GUI.contentColor = color.Value;
            }
            GUILayout.Label(text, GUILayout.Width(width));
            GUI.contentColor = prev;
        }
    }
}
