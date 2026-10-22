using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using UnityEngine.LowLevel;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace Weft
{
    // The keyboard, for the View that has the focus and reads it (WeftView.FocusedView with Keyboard on): each key going
    // down, repeating and going up, and the text the keys type, in the order they came, at the start of each frame's
    // Update (before the game's scripts, so that they can ask WeftView.KeyUsed about the keys they read). Read from the
    // Input System when the project uses it (its events: a key's text follows its keydown), else from the Input Manager
    // (each frame's keydowns, the keys that type nothing first, then the frame's text, then its keyups). Keys repeat as
    // Windows repeats them: the last key pressed, while it is held, after the system's keyboard delay at its rate. Without
    // such a View the keys are only followed (the modifiers held).
    //
    // The input method: on while the page's focused text field takes text (WeftView.TextInput; off in a password field),
    // its candidate window below the field's caret (a full-screen View's; the game places it for a View it shows as a
    // texture). Its composition goes to the page as it changes, the text it commits as a commit (no keypresses, as Chrome
    // gives an input method's text), and the keys that go down while it composes as keys it took (key "Process"). The
    // Input Manager gives the composition (Input.compositionString) and its commit (the frame's text when a composition
    // went on), the Input System its composition events and the commit's text. The Input Manager's Input.inputString has
    // only what keys type in ASCII, so while a text field takes text the frame's text is that of IMGUI's key events
    // instead (an input method's commit, any character), which are taken from IMGUI's queue, as UGUI's input field takes
    // them while it has the keyboard.
    internal static unsafe class WeftKeyboardInput
    {
        enum Kind : byte
        {
            Down,
            Up,
            Text,
            // A UTF-16 unit of text an input method committed.
            Commit,
            // The input method's composition changed (the next of compositions).
            Compose,
        }

        // A key that went down or up (Value its WeftKeys index; Ime: while an input method composed), or a UTF-16 unit of
        // text.
        struct Pending
        {
            public Kind Kind;
            public int Value;
            public bool Ime;
        }

        const int Capacity = 256;
        // At most this many repeats a frame: a long frame does not make a burst of them.
        const int MostRepeats = 4;

        static readonly Pending[] pending = new Pending[Capacity];
        static int head;
        static int count;
        static readonly bool[] held = new bool[WeftKeys.Count];
        static int heldCount;
        static readonly int[] modifierKeys = ModifierKeys();
        static int repeating = -1;
        static double nextRepeat;
        static int attached;
        static readonly char[] units = new char[Capacity];
        static readonly byte[] text = new byte[Capacity * 3];
        // The input method: its compositions to give in order, the last read (the Input Manager's), whether one goes on
        // or ended since the last frame (the Input System's events), whether the View turned it on (and the Input
        // Manager's mode before), and where its candidate window was put.
        static readonly System.Collections.Generic.Queue<string> compositions = new System.Collections.Generic.Queue<string>();
        static string lastComposition = string.Empty;
        static bool composing;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        static bool committing;
#endif
        static bool inputMethodOn;
        static Vector2 candidates = new Vector2(float.NaN, float.NaN);
        // Whether the page's text field took text at the end of the last frame (the Input Manager's text then comes from
        // IMGUI's key events).
        static bool fieldActive;
#if !(WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM) && ENABLE_LEGACY_INPUT_MANAGER
        static readonly Event imguiEvent = new Event();
        static readonly StringBuilder eventText = new StringBuilder();
#endif
#if !(WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM) && ENABLE_LEGACY_INPUT_MANAGER
        static IMECompositionMode modeBefore;
#endif
        static byte[] compositionBytes = new byte[64];

        // What the tests replace: the clock, the system's repeat delay and interval (seconds), the lock keys' state, and
        // whether the keyboard's own keys are read.
        internal static Func<double> Clock = () => Time.realtimeSinceStartupAsDouble;
        internal static Func<WeftModifiers> Locks = SystemLocks;
        internal static double RepeatDelay = 0.5;
        internal static double RepeatInterval = 1 / 30.0;
        internal static bool ReadDevices = true;

        // A View is enabled: the first starts the reading (at the start of Update, and the Input System's events).
        internal static void Attach()
        {
            if (attached++ > 0)
            {
                return;
            }
            Install();
            SystemRepeat();
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            InputSystem.onEvent += OnEvent;
#endif
        }

        // A View is disabled: the last stops the reading (and gives the input method back as it was).
        internal static void Detach()
        {
            if (attached == 0 || --attached > 0)
            {
                return;
            }
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            InputSystem.onEvent -= OnEvent;
#endif
            Release();
            InputMethod(null);
        }

        // The game's window lost the system's focus: the keys held go up without keyups, as a browser's page gets none
        // for them, and nothing repeats.
        internal static void Release()
        {
            Array.Clear(held, 0, held.Length);
            heldCount = 0;
            repeating = -1;
            head = count = 0;
            compositions.Clear();
            lastComposition = string.Empty;
            composing = false;
            fieldActive = false;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            committing = false;
#endif
        }

        internal static void Enqueue(bool down, int key, bool ime = false)
        {
            Enqueue(down ? Kind.Down : Kind.Up, key, ime);
        }

        internal static void Enqueue(string typed, bool committed = false)
        {
            foreach (var unit in typed)
            {
                Enqueue(committed ? Kind.Commit : Kind.Text, unit);
            }
        }

        // The input method's composition now.
        internal static void EnqueueComposition(string composition)
        {
            if (count == Capacity)
            {
                return;
            }
            compositions.Enqueue(composition ?? string.Empty);
            Enqueue(Kind.Compose, 0);
        }

        static void Enqueue(Kind kind, int value, bool ime = false)
        {
            if (count == Capacity)
            {
                return;
            }
            pending[(head + count) % Capacity] = new Pending {Kind = kind, Value = value, Ime = ime};
            ++count;
        }

        // At the start of each frame's Update.
        internal static void Tick()
        {
            if (attached == 0)
            {
                return;
            }
            if (ReadDevices)
            {
                PollInputManager();
            }
            while (count > 0)
            {
                var next = pending[head];
                if (next.Kind == Kind.Compose)
                {
                    Take();
                    var composition = compositions.Count > 0 ? compositions.Dequeue() : string.Empty;
                    // A composition that ends as its text comes ends with the text (as Chrome's commit), not cancelled.
                    if (composition.Length > 0 || count == 0 || pending[head].Kind != Kind.Commit)
                    {
                        Compose(composition);
                    }
                    continue;
                }
                if (next.Kind != Kind.Text && next.Kind != Kind.Commit)
                {
                    Take();
                    Press(next.Value, next.Kind == Kind.Down, next.Ime || next.Kind == Kind.Down && Composes());
                    continue;
                }
                var length = 0;
                while (count > 0 && pending[head].Kind == next.Kind)
                {
                    units[length++] = (char)Take().Value;
                }
                Type(length, next.Kind == Kind.Commit);
            }
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            committing = false;
#endif
            Repeat();
            InputMethod(Target());
        }

        // Whether the input method's composition changes next, before another key goes down or text comes: the key that
        // went down was the input method's (the Input System's events give a key that starts a composition before it).
        static bool Composes()
        {
            for (var i = 0; i < count; ++i)
            {
                var next = pending[(head + i) % Capacity];
                if (next.Kind != Kind.Up)
                {
                    return next.Kind == Kind.Compose;
                }
            }
            return false;
        }

        static Pending Take()
        {
            var next = pending[head];
            head = (head + 1) % Capacity;
            --count;
            return next;
        }

        static WeftView Target()
        {
            var view = WeftView.FocusedView;
            return view && view.Keyboard ? view : null;
        }

        static void Press(int key, bool down, bool ime = false)
        {
            if (held[key] == down)
            {
                // A keyup of a key that went down before the reading started (or before the window lost the focus).
                return;
            }
            held[key] = down;
            heldCount += down ? 1 : -1;
            if (down)
            {
                repeating = key;
                nextRepeat = Clock() + RepeatDelay;
            }
            else if (repeating == key)
            {
                repeating = -1;
            }
            Target()?.KeyFromDevice(key, down, Modifiers(), false, ime);
        }

        static void Type(int length, bool committed)
        {
            var view = Target();
            if (view == null || length == 0)
            {
                return;
            }
            var size = Encoding.UTF8.GetBytes(units, 0, length, text, 0);
            fixed (byte* t = text)
            {
                if (committed)
                {
                    view.CommitFromDevice(t, size);
                }
                else
                {
                    view.TextFromDevice(t, size);
                }
            }
        }

        static void Compose(string composition)
        {
            var view = Target();
            if (view == null)
            {
                return;
            }
            var size = Encoding.UTF8.GetByteCount(composition);
            if (compositionBytes.Length < size)
            {
                compositionBytes = new byte[Math.Max(size, compositionBytes.Length * 2)];
            }
            Encoding.UTF8.GetBytes(composition, 0, composition.Length, compositionBytes, 0);
            fixed (byte* t = compositionBytes)
            {
                view.ComposeFromDevice(t, size, size);
            }
        }

        // The input method follows the page's focused text field: on while one takes text (not a password's), its
        // candidate window below the field's caret when the View is full-screen (Unity's IME positions are the screen's
        // px from its top left, as the View's).
        static void InputMethod(WeftView view)
        {
            var input = view != null ? view.TextInput : default;
            fieldActive = input.Active;
            var on = input.Active && !input.Password;
            var place = on && view.FullScreen;
            var caret = place ? new Vector2(input.Caret.xMin, input.Caret.yMax) * view.Scale : default;
            if (on != inputMethodOn)
            {
                candidates = new Vector2(float.NaN, float.NaN);
            }
            place &= caret != candidates;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null)
            {
                return;
            }
            if (on != inputMethodOn)
            {
                inputMethodOn = on;
                keyboard.SetIMEEnabled(on);
            }
            if (place)
            {
                candidates = caret;
                keyboard.SetIMECursorPosition(caret);
            }
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (on != inputMethodOn)
            {
                inputMethodOn = on;
                if (on)
                {
                    modeBefore = Input.imeCompositionMode;
                    Input.imeCompositionMode = IMECompositionMode.On;
                }
                else
                {
                    Input.imeCompositionMode = modeBefore;
                }
            }
            if (place)
            {
                candidates = caret;
                Input.compositionCursorPos = caret;
            }
#endif
        }

        static void Repeat()
        {
            if (repeating < 0)
            {
                return;
            }
            var now = Clock();
            // While the input method composes, it takes the repeats too.
            var ime = composing || lastComposition.Length > 0;
            for (var n = 0; n < MostRepeats && now >= nextRepeat; ++n)
            {
                nextRepeat += RepeatInterval;
                Target()?.KeyFromDevice(repeating, true, Modifiers(), true, ime);
            }
            if (now >= nextRepeat)
            {
                nextRepeat = now + RepeatInterval;
            }
        }

        // The modifiers held now, with Caps Lock and Num Lock as the system has them.
        internal static WeftModifiers Modifiers()
        {
            var modifiers = Locks();
            foreach (var key in modifierKeys)
            {
                if (held[key])
                {
                    modifiers |= WeftKeys.HoldsAt(key);
                }
            }
            return modifiers;
        }

        static int[] ModifierKeys()
        {
            var keys = new System.Collections.Generic.List<int>();
            for (var i = 0; i < WeftKeys.Count; ++i)
            {
                if (WeftKeys.HoldsAt(i) != WeftModifiers.None)
                {
                    keys.Add(i);
                }
            }
            return keys.ToArray();
        }

        static void PollInputManager()
        {
#if !(WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM) && ENABLE_LEGACY_INPUT_MANAGER
            // Unity makes a string of the composition and of the frame's text each time they are read: the composition
            // only while the input method is on (or composed), the text only when keys went down (or a text field takes
            // its text from IMGUI's events), so that a frame without input allocates nothing.
            var composition = inputMethodOn || lastComposition.Length > 0 ? Input.compositionString ?? string.Empty : string.Empty;
            if (heldCount == 0 && !Input.anyKeyDown && composition == lastComposition && !fieldActive)
            {
                return;
            }
            var typed = fieldActive ? EventText() : Input.inputString;
            // Keys that go down while the input method composes are its own.
            var ime = lastComposition.Length > 0 || composition.Length > 0;
            for (var pass = 0; pass < 2; ++pass)
            {
                for (var i = 0; i < WeftKeys.Count; ++i)
                {
                    if (WeftKeys.TypesAt(i) == (pass == 1) && Input.GetKeyDown(WeftKeys.LegacyAt(i)))
                    {
                        Enqueue(Kind.Down, i, ime);
                    }
                }
            }
            // The text of a frame after a composition is what the input method committed.
            Enqueue(typed, lastComposition.Length > 0);
            if (composition != lastComposition && (composition.Length > 0 || string.IsNullOrEmpty(typed)))
            {
                EnqueueComposition(composition);
            }
            lastComposition = composition;
            for (var i = 0; i < WeftKeys.Count; ++i)
            {
                if (Input.GetKeyUp(WeftKeys.LegacyAt(i)))
                {
                    Enqueue(Kind.Up, i);
                }
            }
#endif
        }

#if !(WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM) && ENABLE_LEGACY_INPUT_MANAGER
        // The characters of the frame's IMGUI key events, taken from its queue (the events go to no OnGUI then).
        static string EventText()
        {
            eventText.Clear();
            while (Event.PopEvent(imguiEvent))
            {
                if (imguiEvent.rawType == EventType.KeyDown && imguiEvent.character != 0)
                {
                    eventText.Append(imguiEvent.character);
                }
            }
            return eventText.ToString();
        }
#endif

#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // Before the Input System applies a keyboard's event: the keys it changes, and its text. Events the editor's
        // windows get (the game view without the focus) are not the game's.
        static void OnEvent(InputEventPtr e, InputDevice device)
        {
            if (!ReadDevices || !(device is Keyboard keyboard) || InputState.currentUpdateType == InputUpdateType.Editor)
            {
                return;
            }
            if (e.IsA<IMECompositionEvent>())
            {
                var composition = ((IMECompositionEvent*)e.data)->compositionString.ToString();
                committing |= composing && composition.Length == 0;
                composing = composition.Length > 0;
                EnqueueComposition(composition);
                return;
            }
            if (e.IsA<TextEvent>())
            {
                // Text while the input method composes, or after its composition ended since the last frame, is its
                // commit.
                var kind = composing || committing ? Kind.Commit : Kind.Text;
                var c = TextEvent.From(e)->character;
                if (c >= 0x10000)
                {
                    Enqueue(kind, 0xD800 + ((c - 0x10000) >> 10));
                    Enqueue(kind, 0xDC00 + ((c - 0x10000) & 0x3FF));
                }
                else
                {
                    Enqueue(kind, c);
                }
                return;
            }
            if (!e.IsA<StateEvent>() && !e.IsA<DeltaStateEvent>())
            {
                return;
            }
            for (var i = 0; i < WeftKeys.Count; ++i)
            {
                var key = WeftKeys.SystemKeys[i];
                if (key == Key.None)
                {
                    continue;
                }
                var control = keyboard[key];
                if (control.ReadValueFromEvent(e, out var value) && control.IsValueConsideredPressed(value) != control.isPressed)
                {
                    Enqueue(control.isPressed ? Kind.Up : Kind.Down, i, !control.isPressed && composing);
                }
            }
        }
#endif

        // The reading runs first in the frame's Update, before the scripts' Update.
        static void Install()
        {
            var loop = PlayerLoop.GetCurrentPlayerLoop();
            for (var i = 0; i < loop.subSystemList.Length; ++i)
            {
                if (loop.subSystemList[i].type != typeof(UnityEngine.PlayerLoop.Update))
                {
                    continue;
                }
                var systems = loop.subSystemList[i].subSystemList;
                var name = typeof(WeftKeyboardInput).FullName;
                // One of an earlier play session without a domain reload is put back with this one's.
                var kept = Array.FindAll(systems, s => s.type == null || s.type.FullName != name);
                var updated = new PlayerLoopSystem[kept.Length + 1];
                updated[0] = new PlayerLoopSystem {type = typeof(WeftKeyboardInput), updateDelegate = Tick};
                Array.Copy(kept, 0, updated, 1, kept.Length);
                loop.subSystemList[i].subSystemList = updated;
                PlayerLoop.SetPlayerLoop(loop);
                return;
            }
        }

        static WeftModifiers SystemLocks()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            return ((GetKeyState(VkCapital) & 1) != 0 ? WeftModifiers.CapsLock : WeftModifiers.None) |
                   ((GetKeyState(VkNumLock) & 1) != 0 ? WeftModifiers.NumLock : WeftModifiers.None);
#else
            return WeftModifiers.None;
#endif
        }

        // The system's keyboard delay (0 to 3: 250 ms to 1 s) and repeat rate (0 to 31: about 2.5 to 30 a second).
        static void SystemRepeat()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (SystemParametersInfo(SpiGetKeyboardDelay, 0, out var delay, 0))
            {
                RepeatDelay = 0.25 * (Math.Min(delay, 3u) + 1);
            }
            if (SystemParametersInfo(SpiGetKeyboardSpeed, 0, out var speed, 0))
            {
                RepeatInterval = 1 / (2.5 + Math.Min(speed, 31u) * 27.5 / 31);
            }
#endif
        }

        const int VkCapital = 0x14;
        const int VkNumLock = 0x90;
        const uint SpiGetKeyboardSpeed = 0x000A;
        const uint SpiGetKeyboardDelay = 0x0016;

        [DllImport("user32.dll")]
        static extern short GetKeyState(int key);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint update);

        // What the tests replaced, as it was.
        internal static void Restore()
        {
            Clock = () => Time.realtimeSinceStartupAsDouble;
            Locks = SystemLocks;
            ReadDevices = true;
            SystemRepeat();
        }

        // A play session without a domain reload keeps static state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            attached = 0;
            Release();
            Restore();
            inputMethodOn = false;
        }
    }
}
