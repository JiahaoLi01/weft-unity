using System;
using UnityEngine;

namespace Weft
{
    /// <summary>
    /// A weft page in the game: a View of the runtime that runs the page every frame, hands its messages to the game,
    /// takes the game's events, view models and anchors, and draws into a render texture on Unity's render thread.
    /// A full-screen View follows the screen's size, gets the mouse and is drawn over everything (in OnGUI); otherwise
    /// it is a texture of its size for the game to show (<see cref="Texture"/> with <see cref="WeftComposite.Material"/>)
    /// and takes the pointer events the game gives it. The View with the keyboard focus (a press on its page's elements, or
    /// <see cref="Focus"/>) reads the keyboard, as a browser's page gets it; the keys the page does not use are the game's
    /// (<see cref="KeyUsed(string)"/>).
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

        [Tooltip("Read the keyboard while the View has the focus (a press on its page's elements, or Focus()), as a browser's " +
                 "page gets it: the keys go to the page's focused element, and those it does not use are the game's (KeyUsed).")]
        [SerializeField] bool keyboard = true;

        [Tooltip("Log the page's diagnostics (exceptions, missing images) as warnings.")]
        [SerializeField] bool logDiagnostics = true;

        // The View with the keyboard focus, and whether the game's window has the system's.
        static WeftView focused;
        static bool applicationFocused = true;

        WeftNative.ViewHandle view;
        bool created;
        WeftSurface surface;
        readonly WeftPointerInput pointer = new WeftPointerInput();
        Action<WeftPointerKind, Vector2, WeftButtons, Vector2> sendPointer;
        double time;
        float appliedScale;
        WeftPage loaded;
        // What the calls of every frame encode into (names, JSON) and the last cursor's bytes and name: they allocate
        // nothing while what they carry fits and the cursor stays.
        byte[] nameBuffer;
        byte[] valueBuffer;
        readonly byte[] cursorBytes = new byte[32];
        int cursorLength = -1;
        string cursorName = "auto";
        // Whether the page was last told it has the focus (weft's Views start with it), and whether the View counts among
        // those the keyboard is read for (from its OnEnable to its OnDisable).
        bool pageFocus;
        bool reading;
        // The frame the page last used each key in (by WeftKeys' index), or a key of another code the game gave it; the
        // key the text that follows belongs to.
        readonly int[] usedFrames = NoFrames();
        string usedCode;
        int usedCodeFrame = -1;
        int lastDown = -1;
        string lastDownCode;

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

        /// <summary>Whether the View reads the keyboard while it has the focus; off, the game gives it keys (Key, Text).</summary>
        public bool Keyboard
        {
            get => keyboard;
            set => keyboard = value;
        }

        /// <summary>
        /// The View with the keyboard focus, whose page has it as a browser window's page has it (focus and blur, :focus);
        /// null when the game has it. A press on a page's elements gives it to their View when it reads the keyboard, one
        /// beside them takes it away.
        /// </summary>
        public static WeftView FocusedView => focused;

        public bool HasFocus => focused == this;

        /// <summary>
        /// The page's clock this frame, in seconds (it only goes forward): its frames run at it (performance.now() reads
        /// it in ms). Times the engine gives the page (when something began, for the page to carry on each frame) are on it.
        /// </summary>
        public double PageTime => Math.Max(time, Time.unscaledTimeAsDouble);

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
                        Flags = (next.CrpgBridge ? WeftNative.PageCrpgBridge : 0) | (next.ForgivingCss ? WeftNative.PageForgivingCss : 0) |
                                (next.WebPlatform ? WeftNative.PageWebPlatform : 0),
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
            var nameLength = WeftRuntime.Utf8(name, ref nameBuffer);
            var valueLength = WeftRuntime.Utf8(string.IsNullOrEmpty(json) ? "null" : json, ref valueBuffer);
            fixed (byte* n = nameBuffer)
            fixed (byte* v = valueBuffer)
            {
                return WeftNative.weft_view_emit(WeftRuntime.Native, view, n, (UIntPtr)nameLength, WeftNative.ValueJson, v,
                    (UIntPtr)valueLength, latest ? WeftNative.EmitLatest : 0) == WeftNative.Result.Ok;
            }
        }

        /// <summary>Merges fields (a JSON object of field names to values) into the page's weft.engine.model(model).</summary>
        public bool SetModel(string model, string fieldsJson)
        {
            if (!created)
            {
                return false;
            }
            var modelLength = WeftRuntime.Utf8(model, ref nameBuffer);
            var fieldsLength = WeftRuntime.Utf8(fieldsJson, ref valueBuffer);
            fixed (byte* m = nameBuffer)
            fixed (byte* f = valueBuffer)
            {
                return WeftNative.weft_view_set_model(WeftRuntime.Native, view, m, (UIntPtr)modelLength, WeftNative.ValueJson, f,
                    (UIntPtr)fieldsLength) == WeftNative.Result.Ok;
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
            var valueLength = WeftRuntime.Utf8(string.IsNullOrEmpty(json) ? "null" : json, ref valueBuffer);
            fixed (byte* v = valueBuffer)
            {
                return WeftNative.weft_view_complete_call(WeftRuntime.Native, view, callId, succeeded ? 1u : 0u, WeftNative.ValueJson, v,
                    (UIntPtr)valueLength) == WeftNative.Result.Ok;
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
            if (kind == WeftPointerKind.Down && keyboard)
            {
                // Before the page's mousedown, as a browser window gets the focus before the press that gave it.
                if (HitTest(position).Element)
                {
                    Focus();
                }
                else
                {
                    Blur();
                }
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

        /// <summary>Gives the View the keyboard focus (and takes it from the View that had it).</summary>
        public void Focus()
        {
            if (focused == this)
            {
                return;
            }
            var previous = focused;
            focused = this;
            if (previous)
            {
                previous.ApplyFocus();
            }
            ApplyFocus();
        }

        /// <summary>Gives the keyboard focus back to the game when the View has it.</summary>
        public void Blur()
        {
            if (focused == this)
            {
                focused = null;
                ApplyFocus();
            }
        }

        /// <summary>
        /// A key going down (or repeating) or up at the page's focused element (the body without one), with Chrome's
        /// default actions: Tab moves the focus, Enter and Space press buttons, the arrow and page keys scroll. code is
        /// KeyboardEvent.code ("KeyA", "Enter"; WeftKeys.Code and the Input System's Key.WeftCode give Unity's keys'),
        /// key its value, by default what the system's layout types with it (<see cref="WeftKeys.KeyValue"/>). For keys
        /// the game gives the View itself (Keyboard off).
        /// </summary>
        public WeftInputResult Key(WeftKeyKind kind, string code, string key = null, WeftModifiers modifiers = WeftModifiers.None,
            bool repeat = false)
        {
            if (!created || string.IsNullOrEmpty(code))
            {
                return WeftInputResult.None;
            }
            var codeLength = WeftRuntime.Utf8(code, ref nameBuffer);
            var keyLength = WeftRuntime.Utf8(string.IsNullOrEmpty(key) ? WeftKeys.KeyValue(code, modifiers) : key, ref valueBuffer);
            WeftInputResult result;
            fixed (byte* c = nameBuffer)
            fixed (byte* k = valueBuffer)
            {
                result = SendKey(kind == WeftKeyKind.Down, k, keyLength, c, codeLength, modifiers, repeat);
            }
            var index = WeftKeys.IndexOf(code);
            if (kind == WeftKeyKind.Down)
            {
                lastDown = index;
                lastDownCode = index < 0 ? code : null;
            }
            if (result != WeftInputResult.None)
            {
                Used(index, code);
            }
            return result;
        }

        /// <summary>
        /// Text a key typed, after its keydown: a keypress for each character (Space's scrolls a page outside a button).
        /// After a keydown the page used the text is dropped (Default), as Chrome drops it.
        /// </summary>
        public WeftInputResult Text(string text)
        {
            if (!created || string.IsNullOrEmpty(text))
            {
                return WeftInputResult.None;
            }
            var length = WeftRuntime.Utf8(text, ref valueBuffer);
            fixed (byte* t = valueBuffer)
            {
                return TextFromDevice(t, length);
            }
        }

        /// <summary>
        /// Whether the page used the key this frame: a listener prevented its keydown, keyup or text, or weft acted on it
        /// (moved the focus, scrolled, clicked). The game leaves such a key alone, as Chrome keeps the keys a page used from
        /// the browser. The View reads the keyboard before the scripts' Update, so it is known there.
        /// </summary>
        public bool KeyUsed(string code)
        {
            var index = WeftKeys.IndexOf(code);
            return index >= 0 ? usedFrames[index] == Time.frameCount : usedCodeFrame == Time.frameCount && usedCode == code;
        }

        /// <summary>Whether the page used an Input Manager key this frame.</summary>
        public bool KeyUsed(KeyCode key)
        {
            return KeyUsedAt(WeftKeys.IndexOf(key));
        }

        // Whether the page used a key (WeftKeys' index) this frame.
        internal bool KeyUsedAt(int index)
        {
            return index >= 0 && usedFrames[index] == Time.frameCount;
        }

        // A key of the keyboard's (WeftKeys' index) for the focused View: its value as the system's layout gives it.
        internal void KeyFromDevice(int index, bool down, WeftModifiers modifiers, bool repeat)
        {
            if (!created)
            {
                return;
            }
            var code = WeftKeys.Utf8[index];
            var key = stackalloc byte[32];
            WeftInputResult result;
            fixed (byte* c = code)
            {
                var keyLength = 0;
                if (WeftNative.weft_keyboard_key(c, (UIntPtr)code.Length, (uint)modifiers, key) == WeftNative.Result.Ok)
                {
                    while (keyLength < 31 && key[keyLength] != 0)
                    {
                        ++keyLength;
                    }
                }
                if (keyLength == 0)
                {
                    foreach (var b in unidentified)
                    {
                        key[keyLength++] = b;
                    }
                }
                result = SendKey(down, key, keyLength, c, code.Length, modifiers, repeat);
            }
            if (down)
            {
                lastDown = index;
                lastDownCode = null;
            }
            if (result != WeftInputResult.None)
            {
                Used(index, null);
            }
        }

        internal WeftInputResult TextFromDevice(byte* text, int size)
        {
            if (!created)
            {
                return WeftInputResult.None;
            }
            uint handled = 0;
            var result = WeftNative.weft_unity_view_text(view, text, (UIntPtr)size, &handled) == WeftNative.Result.Ok
                ? (WeftInputResult)handled
                : WeftInputResult.None;
            if (result != WeftInputResult.None)
            {
                Used(lastDown, lastDownCode);
            }
            return result;
        }

        static readonly byte[] unidentified = System.Text.Encoding.ASCII.GetBytes("Unidentified");

        WeftInputResult SendKey(bool down, byte* key, int keyLength, byte* code, int codeLength, WeftModifiers modifiers, bool repeat)
        {
            var e = new WeftNative.KeyEvent
            {
                StructSize = (uint)sizeof(WeftNative.KeyEvent),
                Kind = down ? 0u : 1u,
                Key = key,
                KeySize = (UIntPtr)keyLength,
                Code = code,
                CodeSize = (UIntPtr)codeLength,
                Modifiers = (uint)modifiers,
                Flags = down && repeat ? WeftNative.KeyRepeat : 0,
            };
            uint handled = 0;
            return WeftNative.weft_unity_view_key(view, &e, &handled) == WeftNative.Result.Ok ? (WeftInputResult)handled : WeftInputResult.None;
        }

        void Used(int index, string code)
        {
            if (index >= 0)
            {
                usedFrames[index] = Time.frameCount;
            }
            else if (code != null)
            {
                usedCode = code;
                usedCodeFrame = Time.frameCount;
            }
        }

        static int[] NoFrames()
        {
            var frames = new int[WeftKeys.Count];
            for (var i = 0; i < frames.Length; ++i)
            {
                frames[i] = -1;
            }
            return frames;
        }

        // The page has the focus while its View has the game's and the game's window has the system's.
        void ApplyFocus()
        {
            var has = focused == this && applicationFocused;
            if (!created || has == pageFocus)
            {
                return;
            }
            pageFocus = has;
            WeftNative.weft_unity_view_set_focus(view, has ? 1u : 0u);
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
            return new WeftHit(hit.Element != 0, CursorName(hit.Cursor));
        }

        // The cursor's name (NUL-terminated ASCII), the last one's string while its bytes are the last one's.
        string CursorName(byte* text)
        {
            var length = 0;
            while (length < cursorBytes.Length && text[length] != 0)
            {
                ++length;
            }
            var same = length == cursorLength;
            for (var i = 0; same && i < length; ++i)
            {
                same = text[i] == cursorBytes[i];
            }
            if (!same)
            {
                for (var i = 0; i < length; ++i)
                {
                    cursorBytes[i] = text[i];
                }
                cursorLength = length;
                cursorName = System.Text.Encoding.ASCII.GetString(text, length);
            }
            return cursorName;
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
            appliedScale = scale;
            surface = new WeftSurface();
            surface.Resize(pixels.x, pixels.y);
            pageFocus = true;
            ApplyFocus();
            WeftKeyboardInput.Attach();
            reading = true;
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
            if (reading)
            {
                WeftKeyboardInput.Detach();
                reading = false;
            }
            if (focused == this)
            {
                focused = null;
            }
            created = false;
            surface?.Dispose();
            surface = null;
            PointerOverPage = false;
        }

        void OnApplicationFocus(bool hasFocus)
        {
            if (applicationFocused != hasFocus)
            {
                applicationFocused = hasFocus;
                if (!hasFocus)
                {
                    WeftKeyboardInput.Release();
                }
            }
            ApplyFocus();
        }

        // A play session without a domain reload keeps static state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetFocus()
        {
            focused = null;
            applicationFocused = true;
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

        // The texture and the page at the View's size and scale.
        void Fit()
        {
            var pixels = PixelSize;
            var texture = surface.Texture;
            var sized = texture && texture.width == pixels.x && texture.height == pixels.y;
            if (sized && appliedScale == scale)
            {
                return;
            }
            if (!sized)
            {
                surface.Resize(pixels.x, pixels.y);
            }
            appliedScale = scale;
            WeftNative.weft_unity_view_resize(view, pixels.x / scale, pixels.y / scale, scale);
        }

        void Advance()
        {
            time = PageTime;
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
