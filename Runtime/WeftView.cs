using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Weft
{
    /// <summary>
    /// A weft page in the game: a View of the runtime that runs the page every frame, hands its messages to the game,
    /// takes the game's events, view models and anchors, and draws into a render texture on Unity's render thread.
    /// A full-screen View follows the screen's size, gets the mouse and is drawn over everything (in OnGUI); otherwise
    /// it is a texture of its size for the game to show (<see cref="Texture"/> with <see cref="WeftComposite.Material"/>)
    /// and takes the pointer events the game gives it. The View with the keyboard focus (a press on its page's elements, or
    /// <see cref="Focus"/>) reads the keyboard, as a browser's page gets it; the keys the page does not use are the game's
    /// (<see cref="KeyUsed(string)"/>). Every View's page sees the gamepads; on a page with spatial navigation
    /// (<see cref="WeftPage.SpatialNavigation"/>) the focused View's page moves its focus with them, and what it used of
    /// them is not the game's (<see cref="GamepadUsed"/>).
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

        [Tooltip("Read the gamepads: the page sees them (navigator.getGamepads()), and on a page with spatial navigation the " +
                 "View with the focus moves the page's focus with the D-pad and the left stick, A and B (what the page used " +
                 "of them is not the game's: GamepadUsed).")]
        [SerializeField] bool gamepads = true;

        [Tooltip("Log the page's diagnostics (exceptions, missing images, console) to Unity's console.")]
        [SerializeField] bool logDiagnostics = true;

        [Tooltip("Which page elements claim the pointer for PointerOverPage: any non-root element (Element), or only elements declaring a non-default cursor (Cursor).")]
        [SerializeField] WeftPointerRouting pointerRouting = WeftPointerRouting.Element;

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
        // The version of each gamepad slot's state the page last got (WeftGamepadInput), and of each gamepad what the
        // page has (it used their press: WeftGamepadButton bits, the left stick's directions past them) and what of
        // that came up in the frame below.
        internal readonly int[] gamepadVersions = new int[WeftNative.GamepadSlots];
        readonly uint[] gamepadHas = new uint[WeftNative.GamepadSlots];
        readonly uint[] gamepadReleased = new uint[WeftNative.GamepadSlots];
        int gamepadReleasedFrame = -1;

        /// <summary>The page's messages (weft.engine.send and weft.engine.call), during the View's Update.</summary>
        public event Action<WeftMessage> MessageReceived;

        /// <summary>The page's diagnostics, during the View's Update.</summary>
        public event Action<WeftDiagnostic> DiagnosticReceived;

        /// <summary>What the View draws into (sRGB-encoded, premultiplied, top row first); null while disabled.</summary>
        public RenderTexture Texture => surface?.Texture;

        /// <summary>Whether the mouse is over the page according to <see cref="PointerRouting"/>: the game leaves such clicks to it.</summary>
        public bool PointerOverPage { get; private set; }

        /// <summary>The CSS cursor under the mouse ("auto" off the page's elements).</summary>
        public string Cursor { get; private set; } = "auto";

        /// <summary>Which page elements claim the pointer for <see cref="PointerOverPage"/>.</summary>
        public WeftPointerRouting PointerRouting
        {
            get => pointerRouting;
            set => pointerRouting = value;
        }

        /// <summary>The loaded page's folder name (or first script name when loaded from source without a folder).</summary>
        public string PageName { get; private set; } = string.Empty;

        /// <summary>Script/handler/frame exceptions and console errors logged since the page loaded.</summary>
        public int ErrorCount { get; private set; }

        /// <summary>Scene diagnostics, dropped diagnostics and console warnings logged since the page loaded.</summary>
        public int WarningCount { get; private set; }

        internal WeftNative.ViewHandle Handle => view;
        internal bool IsOpen => created;

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

        /// <summary>Whether the View reads the gamepads (its page sees them; with spatial navigation they move its focus).</summary>
        public bool Gamepads
        {
            get => gamepads;
            set
            {
                // Turned on again, the page gets every gamepad's state anew.
                if (value && !gamepads)
                {
                    Array.Clear(gamepadVersions, 0, gamepadVersions.Length);
                }
                gamepads = value;
            }
        }

        internal bool ReadsGamepads => created && gamepads;

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
                                (next.WebPlatform ? WeftNative.PageWebPlatform : 0) |
                                (next.SpatialNavigation ? WeftNative.PageSpatialNavigation : 0),
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
                    PageName = PageNameOf(next);
                    ErrorCount = 0;
                    WarningCount = 0;
                    var result = WeftNative.weft_unity_view_load(view, &desc);
                    // The new page gets every gamepad's state.
                    Array.Clear(gamepadVersions, 0, gamepadVersions.Length);
                    // A page that throws loads empty; its error is a diagnostic of the next advance.
                    if (result != WeftNative.Result.ScriptError)
                    {
                        WeftRuntime.Check(result, "loading the page");
                    }
                    WeftDebugTools.OnPageLoaded(this);
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

        /// <summary>
        /// Sets an item of the View's <c>localStorage</c> (shared across page loads on this View; restore before <see cref="Load(WeftPage)"/>).
        /// Pass <paramref name="remove"/> = true (or null value) to remove the key.
        /// </summary>
        public bool SetStorageItem(string key, string value, bool remove = false)
        {
            if (!created || string.IsNullOrEmpty(key))
            {
                return false;
            }
            var keyLength = WeftRuntime.Utf8(key, ref nameBuffer);
            var valueLength = remove || value == null ? 0 : WeftRuntime.Utf8(value, ref valueBuffer);
            fixed (byte* k = nameBuffer)
            fixed (byte* v = valueBuffer)
            {
                return WeftNative.weft_unity_view_set_storage_item(view, k, (UIntPtr)keyLength,
                    remove || value == null ? null : v, (UIntPtr)valueLength) == WeftNative.Result.Ok;
            }
        }

        /// <summary>Removes every item from the View's <c>localStorage</c>.</summary>
        public bool ClearStorage()
        {
            return created && WeftNative.weft_unity_view_clear_storage(view) == WeftNative.Result.Ok;
        }

        /// <summary>
        /// Reads the View's <c>localStorage</c> when its revision changed since <paramref name="revision"/> (0 reads unconditionally).
        /// Updates <paramref name="revision"/> and populates <paramref name="outItems"/>, returning true when items were updated.
        /// </summary>
        public bool GetStorage(ref ulong revision, Dictionary<string, string> outItems)
        {
            if (!created || outItems == null)
            {
                return false;
            }
            var items = new Dictionary<string, string>();
            ulong current = 0;
            WeftNative.StorageItemFn callback = (_, key, keySize, value, valueSize) =>
            {
                items[WeftNative.Text(key, keySize)] = WeftNative.Text(value, valueSize);
            };
            var result = WeftNative.weft_unity_view_get_storage(view, revision, callback, IntPtr.Zero, &current);
            GC.KeepAlive(callback);
            if (result != WeftNative.Result.Ok || (revision != 0 && current == revision))
            {
                return false;
            }
            revision = current;
            outItems.Clear();
            foreach (var pair in items)
            {
                outItems[pair.Key] = pair.Value;
            }
            return true;
        }

        internal bool Evaluate(string source, out string text)
        {
            text = string.Empty;
            if (!created || string.IsNullOrEmpty(source))
            {
                return false;
            }
            var sourceBytes = WeftRuntime.Utf8(source);
            var ok = false;
            var captured = string.Empty;
            WeftNative.TextFn callback = (_, succeeded, bytes, size) =>
            {
                ok = succeeded != 0;
                captured = WeftNative.Text(bytes, size);
            };
            fixed (byte* s = sourceBytes)
            {
                var result = WeftNative.weft_unity_view_evaluate(view, s, (UIntPtr)sourceBytes.Length, callback, IntPtr.Zero);
                GC.KeepAlive(callback);
                if (result != WeftNative.Result.Ok)
                {
                    return false;
                }
            }
            text = captured;
            return ok;
        }

        internal bool IsFrameAnimated()
        {
            if (!created)
            {
                return false;
            }
            var info = new WeftNative.ViewFrameInfo { StructSize = (uint)sizeof(WeftNative.ViewFrameInfo) };
            return WeftNative.weft_unity_view_frame_info(view, &info) == WeftNative.Result.Ok && info.Animated != 0;
        }

        static string PageNameOf(WeftPage source)
        {
            if (!string.IsNullOrEmpty(source.Base))
            {
                var normalized = source.Base.Replace('\\', '/');
                var dir = normalized.EndsWith("/", StringComparison.Ordinal)
                    ? normalized.TrimEnd('/')
                    : Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
                var folder = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(folder))
                {
                    return folder;
                }
                return normalized;
            }
            if (source.Scripts.Count > 0 && !string.IsNullOrEmpty(source.Scripts[0].Name))
            {
                return Path.GetFileNameWithoutExtension(source.Scripts[0].Name);
            }
            return "page";
        }

        /// <summary>
        /// A pointer event at a point in the View's logical px; true when a listener prevented its default action. The
        /// modifiers held (Shift extends a text field's selection to a press) are the keyboard's unless given.
        /// </summary>
        public bool Pointer(WeftPointerKind kind, Vector2 position, WeftButtons buttons = WeftButtons.None, Vector2 wheel = default,
            WeftModifiers? modifiers = null)
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
                Modifiers = (uint)((modifiers ?? WeftKeyboardInput.Modifiers()) &
                                   (WeftModifiers.Shift | WeftModifiers.Control | WeftModifiers.Alt | WeftModifiers.Meta)),
                WheelX = wheel.x,
                WheelY = wheel.y,
            };
            uint prevented = 0;
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            WeftNative.weft_unity_view_pointer(view, &e, &prevented);
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            return prevented != 0;
        }

        /// <summary>
        /// Where text input goes now: the page's focused text field, while the View has the keyboard. The View turns the
        /// input method on and off and places its candidate window by it for the keys it reads (Keyboard on).
        /// </summary>
        public WeftTextInput TextInput
        {
            get
            {
                if (!created)
                {
                    return default;
                }
                var input = new WeftNative.TextInput {StructSize = (uint)sizeof(WeftNative.TextInput)};
                if (WeftNative.weft_unity_view_text_input(view, &input) != WeftNative.Result.Ok)
                {
                    return default;
                }
                return new WeftTextInput(input.Active != 0, input.Composing != 0, input.Password != 0,
                    new Rect(input.CaretX, input.CaretY, input.CaretWidth, input.CaretHeight),
                    new Rect(input.FieldX, input.FieldY, input.FieldWidth, input.FieldHeight));
            }
        }

        /// <summary>
        /// An input method's composition at the page's focused text field, as Chrome takes one (compositionstart,
        /// compositionupdate, beforeinput, input): text replaces the composition (or, as one starts, the selection), the
        /// caret at cursor (a UTF-16 index in text; its end when negative). Empty text cancels the composition. Whether a
        /// text field the user may edit took it. For an input method the game reads itself (Keyboard off).
        /// </summary>
        public bool Compose(string text, int cursor = -1)
        {
            if (!created)
            {
                return false;
            }
            text ??= string.Empty;
            var length = WeftRuntime.Utf8(text, ref valueBuffer);
            var at = cursor < 0 || cursor >= text.Length ? length : System.Text.Encoding.UTF8.GetByteCount(text.Substring(0, cursor));
            fixed (byte* t = valueBuffer)
            {
                return ComposeFromDevice(t, length, at);
            }
        }

        /// <summary>
        /// An input method commits text at the page's focused text field: the composition ends with it; without one it
        /// goes in as typed text does, without keys. Whether a text field the user may edit took it.
        /// </summary>
        public bool CommitText(string text)
        {
            if (!created || string.IsNullOrEmpty(text))
            {
                return false;
            }
            var length = WeftRuntime.Utf8(text, ref valueBuffer);
            fixed (byte* t = valueBuffer)
            {
                return CommitFromDevice(t, length);
            }
        }

        internal bool ComposeFromDevice(byte* text, int size, int cursor)
        {
            uint taken = 0;
            return created && WeftNative.weft_unity_view_composition(view, text, (UIntPtr)size, (uint)cursor, (uint)cursor, &taken) ==
                WeftNative.Result.Ok && taken != 0;
        }

        internal bool CommitFromDevice(byte* text, int size)
        {
            uint taken = 0;
            return created && WeftNative.weft_unity_view_commit_text(view, text, (UIntPtr)size, &taken) == WeftNative.Result.Ok &&
                   taken != 0;
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
        /// key its value, by default what the system's layout types with it (<see cref="WeftKeys.KeyValue"/>); ime: a key
        /// an input method took (it goes down as "Process", keyCode 229, with no default action). For keys the game gives
        /// the View itself (Keyboard off).
        /// </summary>
        public WeftInputResult Key(WeftKeyKind kind, string code, string key = null, WeftModifiers modifiers = WeftModifiers.None,
            bool repeat = false, bool ime = false)
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
                result = SendKey(kind == WeftKeyKind.Down, k, keyLength, c, codeLength, modifiers, repeat, ime);
            }
            var index = WeftKeys.IndexOf(code);
            if (kind == WeftKeyKind.Down)
            {
                lastDown = index;
                lastDownCode = index < 0 ? code : null;
            }
            if (result != WeftInputResult.None || kind == WeftKeyKind.Down && ime)
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
        /// Whether the page used the key this frame: a listener prevented its keydown, keyup or text, weft acted on it
        /// (moved the focus, scrolled, clicked, edited a text field) or the input method took it for the page's text field.
        /// The game leaves such a key alone, as Chrome keeps the keys a page used from the browser. The View reads the
        /// keyboard before the scripts' Update, so it is known there.
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

        /// <summary>
        /// Whether the page has a gamepad's button, or its left stick: on a page with spatial navigation it used the
        /// press (the arrow key, Enter or Escape it stands for moved the focus, clicked or blurred, or a listener prevented it), and
        /// the button is still down or came up this frame. The game leaves it alone, as it leaves the keys the page used
        /// (KeyUsed). gamepad is the slot, 0 to 3: the page's navigator.getGamepads() index.
        /// </summary>
        public bool GamepadUsed(int gamepad, WeftGamepadButton button)
        {
            if ((uint)gamepad >= WeftNative.GamepadSlots || (uint)button > (uint)WeftGamepadButton.LeftStick)
            {
                return false;
            }
            // The left stick is a key for each direction it may go.
            var bits = button == WeftGamepadButton.LeftStick ? WeftGamepadInput.LeftStickMask : 1u << (int)button;
            return (gamepadHas[gamepad] & bits) != 0 ||
                   gamepadReleasedFrame == Time.frameCount && (gamepadReleased[gamepad] & bits) != 0;
        }

        // The gamepads' states that changed (WeftGamepadInput; slots[i] is the slot of states[i]), with what went down and
        // came up in each slot this frame: what the page had that came up is its own until the frame ends, and what went
        // down with a state whose keys the page used is its own until it comes up.
        internal void GamepadsFromDevice(WeftNative.GamepadState* states, int* slots, int count, uint* wentDown, uint* cameUp)
        {
            var frame = Time.frameCount;
            if (gamepadReleasedFrame != frame)
            {
                Array.Clear(gamepadReleased, 0, gamepadReleased.Length);
                gamepadReleasedFrame = frame;
            }
            for (var i = 0; i < count; ++i)
            {
                var s = slots[i];
                gamepadReleased[s] |= gamepadHas[s] & cameUp[s];
                gamepadHas[s] &= ~cameUp[s];
            }
            var handled = stackalloc uint[WeftNative.GamepadSlots];
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            var result = WeftNative.weft_unity_view_gamepads(view, states, (uint)count, handled);
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            if (result != WeftNative.Result.Ok)
            {
                return;
            }
            for (var i = 0; i < count; ++i)
            {
                if (handled[i] != 0)
                {
                    gamepadHas[slots[i]] |= wentDown[slots[i]];
                }
            }
        }

        // A key of the keyboard's (WeftKeys' index) for the focused View: its value as the system's layout gives it.
        internal void KeyFromDevice(int index, bool down, WeftModifiers modifiers, bool repeat, bool ime = false)
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
                result = SendKey(down, key, keyLength, c, code.Length, modifiers, repeat, ime);
            }
            if (down)
            {
                lastDown = index;
                lastDownCode = null;
            }
            // A key the input method took (for the page's text field) is the page's too.
            if (result != WeftInputResult.None || down && ime)
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
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            var result = WeftNative.weft_unity_view_text(view, text, (UIntPtr)size, &handled) == WeftNative.Result.Ok
                ? (WeftInputResult)handled
                : WeftInputResult.None;
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            if (result != WeftInputResult.None)
            {
                Used(lastDown, lastDownCode);
            }
            return result;
        }

        static readonly byte[] unidentified = System.Text.Encoding.ASCII.GetBytes("Unidentified");

        WeftInputResult SendKey(bool down, byte* key, int keyLength, byte* code, int codeLength, WeftModifiers modifiers, bool repeat,
            bool ime = false)
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
                Flags = (down && repeat ? WeftNative.KeyRepeat : 0) | (ime ? WeftNative.KeyIme : 0),
            };
            uint handled = 0;
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            var result = WeftNative.weft_unity_view_key(view, &e, &handled) == WeftNative.Result.Ok ? (WeftInputResult)handled : WeftInputResult.None;
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            return result;
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
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            var result = WeftNative.weft_unity_view_hit_test(view, position.x, position.y, &hit);
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            if (result != WeftNative.Result.Ok)
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
            WeftDebugTools.Register(this);
            time = 0;
            appliedScale = scale;
            surface = new WeftSurface();
            surface.Resize(pixels.x, pixels.y);
            pageFocus = true;
            ApplyFocus();
            WeftKeyboardInput.Attach();
            reading = true;
            Array.Clear(gamepadVersions, 0, gamepadVersions.Length);
            WeftGamepadInput.Attach(this);
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
            WeftDebugTools.Unregister(this);
            if (created && WeftRuntime.IsRunning)
            {
                WeftNative.weft_unity_view_destroy(view);
            }
            if (reading)
            {
                WeftKeyboardInput.Detach();
                reading = false;
            }
            WeftGamepadInput.Detach(this);
            Array.Clear(gamepadHas, 0, gamepadHas.Length);
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
                PointerOverPage = pointerRouting == WeftPointerRouting.Cursor ? hit.Interactive : hit.Element;
                Cursor = hit.Cursor;
            }
            Advance();
        }

        void LateUpdate()
        {
            if (created)
            {
                var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
                surface.Draw(view, scale);
                if (started != 0)
                {
                    WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
                }
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
            var started = WeftDebugTools.IsMeasuring ? Stopwatch.GetTimestamp() : 0;
            var result = WeftNative.weft_unity_view_advance(view, time, &messages, &diagnostics);
            if (started != 0)
            {
                WeftDebugTools.AddGameCost(this, Stopwatch.GetTimestamp() - started);
            }
            var label = string.IsNullOrEmpty(PageName) ? name : PageName;
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
                switch (diagnostic.Kind)
                {
                    case WeftDiagnosticKind.Console:
                        if (logDiagnostics)
                        {
                            Debug.Log($"weft [{label}]: {diagnostic.Text}", this);
                        }
                        break;
                    case WeftDiagnosticKind.ConsoleWarning:
                    case WeftDiagnosticKind.Scene:
                        ++WarningCount;
                        if (logDiagnostics)
                        {
                            Debug.LogWarning($"weft [{label}]: {diagnostic.Text}", this);
                        }
                        break;
                    default:
                        ++ErrorCount;
                        if (logDiagnostics)
                        {
                            Debug.LogError($"weft [{label}]: {diagnostic.Text}", this);
                        }
                        break;
                }
                DiagnosticReceived?.Invoke(diagnostic);
            }
            var dropped = WeftNative.weft_unity_view_dropped_diagnostics(view);
            if (dropped > 0)
            {
                WarningCount += (int)dropped;
                if (logDiagnostics)
                {
                    Debug.LogWarning($"weft [{label}]: {dropped} diagnostics dropped past the view's bound", this);
                }
            }
            if (result == WeftNative.Result.Faulted)
            {
                ++ErrorCount;
                Debug.LogError($"weft [{label}]: a resource limit ended the page; load a page again", this);
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
