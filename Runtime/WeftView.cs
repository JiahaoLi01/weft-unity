using System;
using UnityEngine;

namespace Weft
{
    /// <summary>
    /// A weft page in the game: a View of the runtime that runs the page every frame, hands its messages to the game,
    /// takes the game's events, view models and anchors, and draws into a render texture on Unity's render thread.
    /// A full-screen View follows the screen's size, gets the mouse and is drawn over everything (in OnGUI); otherwise
    /// it is a texture of its size for the game to show (<see cref="Texture"/> with <see cref="WeftComposite.Material"/>)
    /// and takes the pointer events the game gives it.
    /// </summary>
    [AddComponentMenu("Weft/Weft View")]
    public sealed unsafe class WeftView : MonoBehaviour
    {
        [Tooltip("The page's folder, relative to StreamingAssets (or absolute): its page.json, or page.js and page.css. " +
                 "Empty: no page until Load is called.")]
        [SerializeField] string page = string.Empty;

        [Tooltip("Device px per CSS px: 2 draws the page twice as large and sharp. The page's logical size is the texture's divided by it.")]
        [Min(0.25f)]
        [SerializeField] float scale = 1;

        [Tooltip("Follow the screen's size, take the mouse and draw over everything. Off: a texture of the size below.")]
        [SerializeField] bool fullScreen = true;

        [Tooltip("The texture's size in px when the View is not full-screen.")]
        [SerializeField] Vector2Int size = new Vector2Int(1280, 720);

        [Tooltip("Order among full-screen Views: higher draws later (on top), as GUI.depth lower.")]
        [SerializeField] int order = 0;

        [Tooltip("Log the page's diagnostics (exceptions, missing images) as warnings.")]
        [SerializeField] bool logDiagnostics = true;

        WeftNative.ViewHandle view;
        bool created;
        WeftSurface surface;
        readonly WeftPointerInput pointer = new WeftPointerInput();
        Action<WeftPointerKind, Vector2, WeftButtons, Vector2> sendPointer;
        double time;
        WeftPage loaded;

        /// <summary>The page's messages (weft.engine.send and weft.engine.call), during the View's Update.</summary>
        public event Action<WeftMessage> MessageReceived;

        /// <summary>The page's diagnostics, during the View's Update.</summary>
        public event Action<WeftDiagnostic> DiagnosticReceived;

        /// <summary>What the View draws into (sRGB-encoded, premultiplied, top row first); null while disabled.</summary>
        public RenderTexture Texture => surface?.Texture;

        /// <summary>Whether the mouse is over an element of the page (not its root): the game leaves such clicks to it.</summary>
        public bool PointerOverPage { get; private set; }

        /// <summary>The CSS cursor under the mouse ("auto" off the page's elements).</summary>
        public string Cursor { get; private set; } = "auto";

        public float Scale
        {
            get => scale;
            set => scale = Mathf.Max(0.25f, value);
        }

        public bool FullScreen
        {
            get => fullScreen;
            set => fullScreen = value;
        }

        /// <summary>Order among full-screen Views: higher draws on top.</summary>
        public int Order
        {
            get => order;
            set => order = value;
        }

        /// <summary>The texture's size in px when not full-screen.</summary>
        public Vector2Int Size
        {
            get => size;
            set => size = new Vector2Int(Mathf.Max(1, value.x), Mathf.Max(1, value.y));
        }

        /// <summary>The page's size in CSS px.</summary>
        public Vector2 LogicalSize => (Vector2)PixelSize / scale;

        Vector2Int PixelSize => fullScreen ? new Vector2Int(Screen.width, Screen.height) : size;

        /// <summary>Loads the page of a folder (relative to StreamingAssets, or absolute).</summary>
        public void Load(string directory)
        {
            page = directory;
            Load(WeftPage.FromDirectory(directory));
        }

        /// <summary>Replaces the View's page; its pending events and the page's messages are dropped, its open calls closed.</summary>
        public void Load(WeftPage next)
        {
            loaded = next ?? throw new ArgumentNullException(nameof(next));
            if (!created)
            {
                return;
            }
            var scripts = new WeftNative.Script[next.Scripts.Count];
            var names = new byte[next.Scripts.Count][];
            var sources = new byte[next.Scripts.Count][];
            for (var i = 0; i < next.Scripts.Count; ++i)
            {
                names[i] = WeftRuntime.Utf8(next.Scripts[i].Name);
                sources[i] = WeftRuntime.Utf8(next.Scripts[i].Source);
            }
            var handles = new System.Runtime.InteropServices.GCHandle[names.Length * 2];
            try
            {
                for (var i = 0; i < names.Length; ++i)
                {
                    handles[2 * i] = System.Runtime.InteropServices.GCHandle.Alloc(names[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                    handles[2 * i + 1] = System.Runtime.InteropServices.GCHandle.Alloc(sources[i], System.Runtime.InteropServices.GCHandleType.Pinned);
                    scripts[i] = new WeftNative.Script
                    {
                        Name = (byte*)handles[2 * i].AddrOfPinnedObject(),
                        NameSize = (UIntPtr)names[i].Length,
                        Source = (byte*)handles[2 * i + 1].AddrOfPinnedObject(),
                        SourceSize = (UIntPtr)sources[i].Length,
                    };
                }
                var stylesheet = WeftRuntime.Utf8(next.Stylesheet);
                var keyframes = WeftRuntime.Utf8(next.Keyframes);
                var root = WeftRuntime.Utf8(next.Root);
                var baseBytes = WeftRuntime.Utf8(next.Base);
                fixed (WeftNative.Script* s = scripts)
                fixed (byte* css = stylesheet)
                fixed (byte* frames = keyframes)
                fixed (byte* r = root)
                fixed (byte* b = baseBytes)
                {
                    var desc = new WeftNative.PageDesc
                    {
                        StructSize = (uint)sizeof(WeftNative.PageDesc),
                        Flags = next.CrpgBridge ? WeftNative.PageCrpgBridge : 0,
                        Scripts = s,
                        ScriptCount = (uint)scripts.Length,
                        Stylesheet = css,
                        StylesheetSize = (UIntPtr)stylesheet.Length,
                        Keyframes = frames,
                        KeyframesSize = (UIntPtr)keyframes.Length,
                        Root = root.Length > 0 ? r : null,
                        RootSize = (UIntPtr)root.Length,
                        Base = b,
                        BaseSize = (UIntPtr)baseBytes.Length,
                    };
                    var result = WeftNative.weft_unity_view_load(view, &desc);
                    // A page that throws loads empty; its error is a diagnostic of the next advance.
                    if (result != WeftNative.Result.ScriptError)
                    {
                        WeftRuntime.Check(result, "loading the page");
                    }
                }
            }
            finally
            {
                foreach (var handle in handles)
                {
                    if (handle.IsAllocated)
                    {
                        handle.Free();
                    }
                }
            }
        }

        /// <summary>An event for the page's weft.engine.on(name) handlers, delivered at its next advance; json is its value.</summary>
        /// <param name="latest">Replace an undelivered event of the name instead of queueing another.</param>
        /// <returns>False when the View is not running or its queue is full.</returns>
        public bool Emit(string name, string json = "null", bool latest = false)
        {
            if (!created)
            {
                return false;
            }
            var nameBytes = WeftRuntime.Utf8(name);
            var value = WeftRuntime.Utf8(string.IsNullOrEmpty(json) ? "null" : json);
            fixed (byte* n = nameBytes)
            fixed (byte* v = value)
            {
                return WeftNative.weft_view_emit(WeftRuntime.Native, view, n, (UIntPtr)nameBytes.Length, WeftNative.ValueJson, v,
                    (UIntPtr)value.Length, latest ? WeftNative.EmitLatest : 0) == WeftNative.Result.Ok;
            }
        }

        /// <summary>Merges fields (a JSON object of field names to values) into the page's weft.engine.model(model).</summary>
        public bool SetModel(string model, string fieldsJson)
        {
            if (!created)
            {
                return false;
            }
            var modelBytes = WeftRuntime.Utf8(model);
            var fields = WeftRuntime.Utf8(fieldsJson);
            fixed (byte* m = modelBytes)
            fixed (byte* f = fields)
            {
                return WeftNative.weft_view_set_model(WeftRuntime.Native, view, m, (UIntPtr)modelBytes.Length, WeftNative.ValueJson, f,
                    (UIntPtr)fields.Length) == WeftNative.Result.Ok;
            }
        }

        /// <summary>Settles the page's weft.engine.call: resolves its promise with the value, or rejects it.</summary>
        /// <returns>False for a call that is not open (completed, timed out, from an earlier page).</returns>
        public bool CompleteCall(ulong callId, bool succeeded, string json = "null")
        {
            if (!created)
            {
                return false;
            }
            var value = WeftRuntime.Utf8(string.IsNullOrEmpty(json) ? "null" : json);
            fixed (byte* v = value)
            {
                return WeftNative.weft_view_complete_call(WeftRuntime.Native, view, callId, succeeded ? 1u : 0u, WeftNative.ValueJson, v,
                    (UIntPtr)value.Length) == WeftNative.Result.Ok;
            }
        }

        /// <summary>Replaces all of the View's anchors; the frame drawn next places the anchored elements by them.</summary>
        public bool WriteAnchors(ReadOnlySpan<WeftAnchor> anchors)
        {
            if (!created)
            {
                return false;
            }
            fixed (WeftAnchor* a = anchors)
            {
                return WeftNative.weft_view_write_anchors(WeftRuntime.Native, view, a, (UIntPtr)anchors.Length) == WeftNative.Result.Ok;
            }
        }

        /// <summary>A pointer event at a point in the View's logical px; true when a listener prevented its default action.</summary>
        public bool Pointer(WeftPointerKind kind, Vector2 position, WeftButtons buttons = WeftButtons.None, Vector2 wheel = default)
        {
            if (!created)
            {
                return false;
            }
            var e = new WeftNative.PointerEvent
            {
                StructSize = (uint)sizeof(WeftNative.PointerEvent),
                Kind = (uint)kind,
                X = position.x,
                Y = position.y,
                Buttons = (uint)buttons,
                WheelX = wheel.x,
                WheelY = wheel.y,
            };
            uint prevented = 0;
            WeftNative.weft_unity_view_pointer(view, &e, &prevented);
            return prevented != 0;
        }

        /// <summary>What is under a point in the View's logical px.</summary>
        public WeftHit HitTest(Vector2 position)
        {
            if (!created)
            {
                return new WeftHit(false, "auto");
            }
            var hit = new WeftNative.Hit {StructSize = (uint)sizeof(WeftNative.Hit)};
            if (WeftNative.weft_unity_view_hit_test(view, position.x, position.y, &hit) != WeftNative.Result.Ok)
            {
                return new WeftHit(false, "auto");
            }
            var cursor = System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)hit.Cursor) ?? "auto";
            return new WeftHit(hit.Element != 0, cursor);
        }

        void OnEnable()
        {
            WeftRuntime.EnsureStarted();
            var pixels = PixelSize;
            var desc = new WeftNative.ViewDesc
            {
                StructSize = (uint)sizeof(WeftNative.ViewDesc),
                LogicalWidth = pixels.x / scale,
                LogicalHeight = pixels.y / scale,
                DeviceScale = scale,
            };
            WeftNative.ViewHandle handle;
            WeftRuntime.Check(WeftNative.weft_unity_view_create(&desc, &handle), "creating a View");
            view = handle;
            created = true;
            time = 0;
            surface = new WeftSurface();
            surface.Resize(pixels.x, pixels.y);
            if (loaded != null)
            {
                Load(loaded);
            }
            else if (!string.IsNullOrEmpty(page))
            {
                Load(page);
            }
        }

        void OnDisable()
        {
            if (created && WeftRuntime.IsRunning)
            {
                WeftNative.weft_unity_view_destroy(view);
            }
            created = false;
            surface?.Dispose();
            surface = null;
            PointerOverPage = false;
        }

        void Update()
        {
            if (!created)
            {
                return;
            }
            if (!WeftRuntime.IsRunning)
            {
                // The runtime stopped under the View (WeftRuntime.Stop): it draws nothing more.
                created = false;
                return;
            }
            Fit();
            if (fullScreen)
            {
                sendPointer ??= SendPointer;
                pointer.Poll(scale, sendPointer);
                var hit = pointer.Inside ? HitTest(pointer.Position) : new WeftHit(false, "auto");
                PointerOverPage = hit.Element;
                Cursor = hit.Cursor;
            }
            Advance();
        }

        void LateUpdate()
        {
            if (created)
            {
                surface.Draw(view, scale);
            }
        }

        void OnGUI()
        {
            if (!fullScreen || !created || Event.current.type != EventType.Repaint || !surface.Texture)
            {
                return;
            }
            GUI.depth = -order;
            WeftComposite.DrawOnScreen(new Rect(0, 0, Screen.width, Screen.height), surface.Texture);
        }

        void SendPointer(WeftPointerKind kind, Vector2 position, WeftButtons buttons, Vector2 wheel)
        {
            Pointer(kind, position, buttons, wheel);
        }

        // The texture and the page at the View's size.
        void Fit()
        {
            var pixels = PixelSize;
            var texture = surface.Texture;
            if (texture && texture.width == pixels.x && texture.height == pixels.y)
            {
                return;
            }
            surface.Resize(pixels.x, pixels.y);
            WeftNative.weft_unity_view_resize(view, pixels.x / scale, pixels.y / scale, scale);
        }

        void Advance()
        {
            // weft's clock only goes forward.
            time = Math.Max(time, Time.unscaledTimeAsDouble);
            uint messages = 0, diagnostics = 0;
            var result = WeftNative.weft_unity_view_advance(view, time, &messages, &diagnostics);
            for (uint i = 0; i < diagnostics; ++i)
            {
                uint kind;
                byte* text;
                UIntPtr textSize;
                if (WeftNative.weft_unity_view_diagnostic(view, i, &kind, &text, &textSize) != WeftNative.Result.Ok)
                {
                    continue;
                }
                var diagnostic = new WeftDiagnostic((WeftDiagnosticKind)kind, WeftNative.Text(text, textSize));
                if (logDiagnostics)
                {
                    Debug.LogWarning($"weft ({name}): {diagnostic.Kind}: {diagnostic.Text}", this);
                }
                DiagnosticReceived?.Invoke(diagnostic);
            }
            if (result == WeftNative.Result.Faulted)
            {
                Debug.LogError($"weft ({name}): a resource limit ended the page; load a page again", this);
            }
            for (uint i = 0; i < messages; ++i)
            {
                WeftNative.Message m;
                if (WeftNative.weft_unity_view_message(view, i, &m) != WeftNative.Result.Ok)
                {
                    continue;
                }
                MessageReceived?.Invoke(new WeftMessage(WeftNative.Text(m.Name, m.NameSize), WeftNative.Text(m.Json, m.JsonSize), m.CallId));
            }
        }
    }
}
