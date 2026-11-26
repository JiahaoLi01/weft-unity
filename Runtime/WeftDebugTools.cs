using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Weft
{
    /// <summary>
    /// Live diagnostics, per-frame cost measurement, on-screen overlay and Chrome DevTools control for Weft Views.
    /// Zero per-frame overhead while neither <see cref="GetViews"/> nor <see cref="ShowOverlay"/> is active.
    /// Command-line flags: <c>-WeftDevTools[=[address:]port]</c>, <c>-WeftDebug</c>; environment: <c>WEFT_DEVTOOLS_LISTEN</c>.
    /// </summary>
    public static unsafe class WeftDebugTools
    {
        const double MeasureSeconds = 2.0;
        const double WindowSeconds = 1.0;

        sealed class Cost
        {
            public double Frame;
            public double Sum;
            public double Slowest;
            public float AverageMs;
            public float SlowestMs;

            public void EndFrame()
            {
                Sum += Frame;
                if (Frame > Slowest)
                {
                    Slowest = Frame;
                }
                Frame = 0;
            }

            public void EndWindow(int frames)
            {
                AverageMs = frames > 0 ? (float)(Sum / frames * 1000.0) : 0f;
                SlowestMs = (float)(Slowest * 1000.0);
                Sum = 0;
                Slowest = 0;
            }
        }

        sealed class ViewCosts
        {
            public readonly Cost Game = new Cost();
            public readonly Cost Draw = new Cost();
            public bool Animated;
            public int Elements = -1;
            public double ElementsCountedAt = -1e9;
        }

        static readonly List<WeftView> liveViews = new List<WeftView>();
        static readonly Dictionary<WeftView, ViewCosts> viewCosts = new Dictionary<WeftView, ViewCosts>();
        static readonly double tickToSeconds = 1.0 / Stopwatch.Frequency;

        static bool allInterfaces;
        static bool measuring;
        static bool overlayShown;
        static double measureUntil;
        static double windowStart;
        static int windowFrames;
        static WeftDebugRunner runner;

        internal static bool IsMeasuring => measuring;

        /// <summary>The port Weft DevTools listen on, or 0 when stopped.</summary>
        public static int DevToolsPort
        {
            get
            {
                if (!WeftRuntime.IsRunning)
                {
                    return 0;
                }
                var info = new WeftNative.DevToolsInfo { StructSize = (uint)sizeof(WeftNative.DevToolsInfo) };
                return WeftNative.weft_unity_devtools_get_info(&info) == WeftNative.Result.Ok ? (int)info.Port : 0;
            }
        }

        /// <summary>Listening address ("127.0.0.1:&lt;port&gt;" or "0.0.0.0:&lt;port&gt;"), or empty when stopped.</summary>
        public static string DevToolsAddress
        {
            get
            {
                var port = DevToolsPort;
                return port > 0 ? $"{(allInterfaces ? "0.0.0.0" : "127.0.0.1")}:{port}" : string.Empty;
            }
        }

        /// <summary>Whether the on-screen debug overlay table is currently shown.</summary>
        public static bool IsOverlayShown => overlayShown;

        /// <summary>
        /// Starts Chrome DevTools on <paramref name="listen"/> (<c>[127.0.0.1:|0.0.0.0:]port</c>; empty or 0 picks the
        /// first free port from 9230). Logs each loaded page's <c>devtools://</c> URL.
        /// </summary>
        public static bool StartDevTools(string listen = "")
        {
            return StartDevTools(listen, out _);
        }

        /// <summary>Starts Chrome DevTools and returns a descriptive reason in <paramref name="error"/> on failure.</summary>
        public static bool StartDevTools(string listen, out string error)
        {
            error = string.Empty;
            bool Fail(string why, out string err)
            {
                Debug.LogWarning($"Weft DevTools: {why}");
                err = why;
                return false;
            }
            if (!WeftRuntime.IsRunning)
            {
                return Fail("weft does not run here.", out error);
            }
            if (!ParseListen(listen ?? string.Empty, out var all, out var port))
            {
                return Fail($"\"{listen}\" is not [127.0.0.1:|0.0.0.0:]port.", out error);
            }
            if (DevToolsPort != 0)
            {
                return Fail($"they listen on {DevToolsAddress} already (StopDevTools first).", out error);
            }
            var desc = new WeftNative.DevToolsDesc
            {
                StructSize = (uint)sizeof(WeftNative.DevToolsDesc),
                Flags = all ? WeftNative.DevToolsAllInterfaces : 0u,
                Port = port,
            };
            var result = WeftNative.weft_unity_devtools_start(&desc);
            if (result != WeftNative.Result.Ok)
            {
                var why = result == WeftNative.Result.Busy
                    ? $"port {port} is taken."
                    : result == WeftNative.Result.Unavailable
                        ? "this weft.dll is weft's release build, without its developer tools."
                        : WeftNative.Describe(result);
                return Fail(why, out error);
            }
            allInterfaces = all;
            LogListening(DevToolsPort);
            return true;
        }

        /// <summary>Stops Chrome DevTools if currently listening.</summary>
        public static void StopDevTools()
        {
            if (WeftRuntime.IsRunning && DevToolsPort != 0)
            {
                WeftNative.weft_unity_devtools_stop();
                Debug.Log("Weft DevTools: stopped.");
            }
            allInterfaces = false;
        }

        /// <summary>Shows or hides the on-screen debug table of all active Weft Views.</summary>
        public static void ShowOverlay(bool show)
        {
            overlayShown = show;
            if (show)
            {
                KeepMeasuring();
            }
        }

        /// <summary>
        /// Populates <paramref name="results"/> with live diagnostics and per-frame timing for every active View.
        /// Calling this enables low-overhead cost measurement for a short window, turning it off automatically when idle.
        /// </summary>
        public static void GetViews(List<WeftViewDebugInfo> results)
        {
            if (results == null)
            {
                throw new ArgumentNullException(nameof(results));
            }
            results.Clear();
            PruneViews();
            if (liveViews.Count == 0 && !WeftRuntime.IsRunning)
            {
                return;
            }
            KeepMeasuring();
            var now = NowSeconds();
            var port = DevToolsPort;
            foreach (var view in liveViews)
            {
                if (!view || !view.IsOpen)
                {
                    continue;
                }
                var costs = CostsOf(view);
                if (now - costs.ElementsCountedAt >= WindowSeconds)
                {
                    costs.Elements = CountElements(view);
                    costs.ElementsCountedAt = now;
                }
                costs.Animated = view.IsFrameAnimated();
                var target = TargetOf(view);
                results.Add(new WeftViewDebugInfo
                {
                    View = view,
                    Page = view.PageName,
                    Target = target,
                    Size = view.LogicalSize,
                    DeviceScale = view.Scale,
                    Elements = costs.Elements,
                    Animated = costs.Animated,
                    GameMs = costs.Game.AverageMs,
                    GameMaxMs = costs.Game.SlowestMs,
                    DrawMs = costs.Draw.AverageMs,
                    DrawMaxMs = costs.Draw.SlowestMs,
                    Errors = view.ErrorCount,
                    Warnings = view.WarningCount,
                    DevToolsUrl = port > 0 ? UrlOf(target, port) : string.Empty,
                });
            }
        }

        /// <summary>Formats a list of View diagnostics as an aligned plain-text table (header first).</summary>
        public static List<string> FormatViews(IReadOnlyList<WeftViewDebugInfo> views)
        {
            var rows = BuildCells(views);
            var widths = new int[rows[0].Length];
            foreach (var row in rows)
            {
                for (var col = 0; col < row.Length; ++col)
                {
                    if (row[col].Length > widths[col])
                    {
                        widths[col] = row[col].Length;
                    }
                }
            }
            var lines = new List<string>(rows.Count);
            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                sb.Clear();
                for (var col = 0; col < row.Length; ++col)
                {
                    sb.Append(row[col]);
                    if (col + 1 < row.Length)
                    {
                        sb.Append(' ', widths[col] - row[col].Length + 2);
                    }
                }
                lines.Add(sb.ToString());
            }
            return lines;
        }

        // ===== Internal hooks =====

        internal static void Register(WeftView view)
        {
            if (view && !liveViews.Contains(view))
            {
                liveViews.Add(view);
            }
        }

        internal static void Unregister(WeftView view)
        {
            liveViews.Remove(view);
            viewCosts.Remove(view);
        }

        internal static void AddGameCost(WeftView view, long stopwatchTicks)
        {
            if (measuring && view)
            {
                CostsOf(view).Game.Frame += stopwatchTicks * tickToSeconds;
            }
        }

        internal static void OnPageLoaded(WeftView view)
        {
            var port = DevToolsPort;
            if (port > 0 && view && view.IsOpen)
            {
                LogUrl(view, port);
            }
        }

        internal static void OnRuntimeStarted()
        {
            allInterfaces = false;
            var args = Environment.GetCommandLineArgs();
            var startedFromCli = false;
            for (var i = 0; i < args.Length; ++i)
            {
                var arg = args[i];
                if (arg.Equals("-WeftDevTools", StringComparison.OrdinalIgnoreCase))
                {
                    StartDevTools();
                    startedFromCli = true;
                }
                else if (arg.StartsWith("-WeftDevTools=", StringComparison.OrdinalIgnoreCase))
                {
                    StartDevTools(arg.Substring("-WeftDevTools=".Length));
                    startedFromCli = true;
                }
                else if (arg.Equals("-WeftDebug", StringComparison.OrdinalIgnoreCase))
                {
                    ShowOverlay(true);
                }
            }
            if (!startedFromCli)
            {
                var port = DevToolsPort;
                if (port > 0)
                {
                    var env = Environment.GetEnvironmentVariable("WEFT_DEVTOOLS_LISTEN") ?? string.Empty;
                    allInterfaces = env.StartsWith("0.0.0.0", StringComparison.Ordinal);
                    LogListening(port);
                }
            }
        }

        internal static void OnRuntimeEnding()
        {
            overlayShown = false;
            StopMeasuring();
            allInterfaces = false;
            liveViews.Clear();
            viewCosts.Clear();
        }

        internal static void TickMeasure()
        {
            if (!measuring)
            {
                return;
            }
            var now = NowSeconds();
            var drawBuffer = stackalloc WeftNative.DrawTime[64];
            var drawCount = WeftNative.weft_unity_render_take_draw_times(drawBuffer, 64);
            PruneViews();
            foreach (var view in liveViews)
            {
                if (!view || !view.IsOpen)
                {
                    continue;
                }
                var costs = CostsOf(view);
                var handle = view.Handle;
                for (uint i = 0; i < drawCount; ++i)
                {
                    if (drawBuffer[i].Slot == handle.Slot && drawBuffer[i].Generation == handle.Generation)
                    {
                        costs.Draw.Frame += drawBuffer[i].Seconds;
                    }
                }
                costs.Game.EndFrame();
                costs.Draw.EndFrame();
            }
            ++windowFrames;
            if (now - windowStart >= WindowSeconds)
            {
                foreach (var view in liveViews)
                {
                    if (!view || !view.IsOpen)
                    {
                        continue;
                    }
                    var costs = CostsOf(view);
                    costs.Game.EndWindow(windowFrames);
                    costs.Draw.EndWindow(windowFrames);
                }
                windowStart = now;
                windowFrames = 0;
            }
            if (!overlayShown && now > measureUntil)
            {
                StopMeasuring();
            }
        }

        internal static string TitleLine(int viewCount)
        {
            var address = DevToolsAddress;
            var tools = string.IsNullOrEmpty(address) ? "off" : $"{address} (chrome://inspect)";
            return $"weft: {viewCount} views, ms per frame as avg / slowest (1s).  DevTools: {tools}";
        }

        internal static List<string[]> BuildCells(IReadOnlyList<WeftViewDebugInfo> views)
        {
            var rows = new List<string[]>(views.Count + 1)
            {
                new[] { "View", "Page", "Size", "Scale", "Elements", "Game ms", "Draw ms", "Animated", "Errors", "Warnings" },
            };
            for (var i = 0; i < views.Count; ++i)
            {
                var v = views[i];
                rows.Add(new[]
                {
                    v.Target ?? string.Empty,
                    string.IsNullOrEmpty(v.Page) ? "(no page)" : v.Page,
                    string.Format(CultureInfo.InvariantCulture, "{0:0}x{1:0}", v.Size.x, v.Size.y),
                    string.Format(CultureInfo.InvariantCulture, "{0:0.00}", v.DeviceScale),
                    v.Elements >= 0 ? v.Elements.ToString(CultureInfo.InvariantCulture) : "-",
                    string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00}", v.GameMs, v.GameMaxMs),
                    string.Format(CultureInfo.InvariantCulture, "{0:0.00} / {1:0.00}", v.DrawMs, v.DrawMaxMs),
                    v.Animated ? "yes" : "no",
                    v.Errors.ToString(CultureInfo.InvariantCulture),
                    v.Warnings.ToString(CultureInfo.InvariantCulture),
                });
            }
            return rows;
        }

        // ===== Helpers =====

        static ViewCosts CostsOf(WeftView view)
        {
            if (!viewCosts.TryGetValue(view, out var costs))
            {
                costs = new ViewCosts();
                viewCosts[view] = costs;
            }
            return costs;
        }

        static void PruneViews()
        {
            for (var i = liveViews.Count - 1; i >= 0; --i)
            {
                if (!liveViews[i])
                {
                    liveViews.RemoveAt(i);
                }
            }
        }

        static int CountElements(WeftView view)
        {
            if (!view || !view.IsOpen || string.IsNullOrEmpty(view.PageName))
            {
                return -1;
            }
            return view.Evaluate("document.querySelectorAll('*').length", out var text) &&
                   int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                ? count
                : -1;
        }

        static string TargetOf(WeftView view)
        {
            var h = view.Handle;
            return $"{h.Slot}-{h.Generation}";
        }

        static string UrlOf(string target, int port)
        {
            return $"devtools://devtools/bundled/devtools_app.html?ws=127.0.0.1:{port}/devtools/page/{target}";
        }

        static void LogUrl(WeftView view, int port)
        {
            var page = string.IsNullOrEmpty(view.PageName) ? "(no page)" : view.PageName;
            Debug.Log($"Weft DevTools: [{page}] {UrlOf(TargetOf(view), port)}", view);
        }

        static void LogListening(int port)
        {
            var host = allInterfaces ? "0.0.0.0" : "127.0.0.1";
            Debug.Log($"Weft DevTools: listening on {host}:{port}. Open a page's address below in Chrome, or chrome://inspect > Configure > 127.0.0.1:{port}.");
            PruneViews();
            foreach (var view in liveViews)
            {
                if (view && view.IsOpen && !string.IsNullOrEmpty(view.PageName))
                {
                    LogUrl(view, port);
                }
            }
        }

        static bool ParseListen(string listen, out bool all, out uint port)
        {
            all = false;
            port = 0;
            var address = "127.0.0.1";
            var portText = listen.Trim();
            var colon = portText.LastIndexOf(':');
            if (colon >= 0)
            {
                address = portText.Substring(0, colon);
                portText = portText.Substring(colon + 1);
            }
            if ((address != "127.0.0.1" && address != "0.0.0.0") || portText.Length > 5)
            {
                return false;
            }
            for (var i = 0; i < portText.Length; ++i)
            {
                if (portText[i] < '0' || portText[i] > '9')
                {
                    return false;
                }
            }
            var value = portText.Length == 0 ? 0 : int.Parse(portText, CultureInfo.InvariantCulture);
            if (value > 65535)
            {
                return false;
            }
            all = address == "0.0.0.0";
            port = (uint)value;
            return true;
        }

        static void KeepMeasuring()
        {
            var now = NowSeconds();
            if (!measuring)
            {
                measuring = true;
                windowStart = now;
                windowFrames = 0;
                WeftNative.weft_unity_render_set_measuring(1);
                EnsureRunner();
            }
            measureUntil = now + MeasureSeconds;
        }

        static void StopMeasuring()
        {
            measuring = false;
            WeftNative.weft_unity_render_set_measuring(0);
            if (runner)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(runner.gameObject);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(runner.gameObject);
                }
                runner = null;
            }
        }

        static void EnsureRunner()
        {
            if (runner || !Application.isPlaying)
            {
                return;
            }
            var go = new GameObject("Weft Debug Overlay") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            runner = go.AddComponent<WeftDebugRunner>();
        }

        static double NowSeconds()
        {
            return Stopwatch.GetTimestamp() * tickToSeconds;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState()
        {
            allInterfaces = false;
            measuring = false;
            overlayShown = false;
            liveViews.Clear();
            viewCosts.Clear();
            runner = null;
        }
    }

    internal sealed class WeftDebugRunner : MonoBehaviour
    {
        readonly List<WeftViewDebugInfo> views = new List<WeftViewDebugInfo>();
        GUIStyle headerStyle;
        GUIStyle cellStyle;
        GUIStyle errorStyle;
        GUIStyle warnStyle;
        Texture2D backdrop;

        void LateUpdate()
        {
            WeftDebugTools.TickMeasure();
        }

        void OnDestroy()
        {
            if (backdrop)
            {
                Destroy(backdrop);
                backdrop = null;
            }
        }

        void EnsureStyles()
        {
            if (headerStyle != null)
            {
                return;
            }
            backdrop = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            backdrop.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            backdrop.Apply();
            backdrop.hideFlags = HideFlags.HideAndDontSave;

            var fontSize = Mathf.Max(12, Mathf.RoundToInt(13f * Mathf.Max(1f, Screen.height / 1080f)));
            cellStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                alignment = TextAnchor.MiddleLeft,
                wordWrap = false,
                padding = new RectOffset(4, 4, 1, 1),
            };
            cellStyle.normal.textColor = Color.white;

            headerStyle = new GUIStyle(cellStyle);
            headerStyle.normal.textColor = new Color(0.72f, 0.76f, 0.82f, 1f);
            headerStyle.fontStyle = FontStyle.Bold;

            errorStyle = new GUIStyle(cellStyle);
            errorStyle.normal.textColor = new Color(1f, 0.35f, 0.3f, 1f);

            warnStyle = new GUIStyle(cellStyle);
            warnStyle.normal.textColor = new Color(1f, 0.85f, 0.3f, 1f);
        }

        void OnGUI()
        {
            if (!WeftDebugTools.IsOverlayShown || Event.current.type != EventType.Repaint)
            {
                return;
            }
            EnsureStyles();
            GUI.depth = -10000;
            WeftDebugTools.GetViews(views);
            var rows = WeftDebugTools.BuildCells(views);
            var heading = WeftDebugTools.TitleLine(views.Count);
            var colCount = rows[0].Length;
            var widths = new float[colCount];
            var gap = 12f;
            var lineH = cellStyle.CalcHeight(new GUIContent("Xg"), 200f) + 2f;

            for (var r = 0; r < rows.Count; ++r)
            {
                var style = r == 0 ? headerStyle : cellStyle;
                for (var c = 0; c < colCount; ++c)
                {
                    var w = style.CalcSize(new GUIContent(rows[r][c])).x;
                    if (w > widths[c])
                    {
                        widths[c] = w;
                    }
                }
            }
            var tableW = gap * (colCount - 1);
            for (var c = 0; c < colCount; ++c)
            {
                tableW += widths[c];
            }
            var headingW = cellStyle.CalcSize(new GUIContent(heading)).x;
            var pad = 10f;
            var originX = 16f;
            var originY = Mathf.Max(16f, Screen.height * 0.06f);
            var boxW = Mathf.Max(tableW, headingW) + pad * 2f;
            var boxH = lineH * (rows.Count + 1) + pad * 2f;

            GUI.DrawTexture(new Rect(originX - pad, originY - pad, boxW, boxH), backdrop);
            GUI.Label(new Rect(originX, originY, boxW - pad, lineH), heading, cellStyle);

            for (var r = 0; r < rows.Count; ++r)
            {
                var x = originX;
                var y = originY + lineH * (r + 1);
                for (var c = 0; c < colCount; ++c)
                {
                    var style = r == 0 ? headerStyle : cellStyle;
                    if (r > 0 && c >= 8 && rows[r][c] != "0")
                    {
                        style = c == 8 ? errorStyle : warnStyle;
                    }
                    GUI.Label(new Rect(x, y, widths[c] + 4f, lineH), rows[r][c], style);
                    x += widths[c] + gap;
                }
            }
        }
    }
}
