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
    internal static unsafe class WeftKeyboardInput
    {
        enum Kind : byte
        {
            Down,
            Up,
            Text,
        }

        // A key that went down or up (Value its WeftKeys index), or a UTF-16 unit of text.
        struct Pending
        {
            public Kind Kind;
            public int Value;
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

        // A View is disabled: the last stops the reading.
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
        }

        // The game's window lost the system's focus: the keys held go up without keyups, as a browser's page gets none
        // for them, and nothing repeats.
        internal static void Release()
        {
            Array.Clear(held, 0, held.Length);
            heldCount = 0;
            repeating = -1;
            head = count = 0;
        }

        internal static void Enqueue(bool down, int key)
        {
            Enqueue(down ? Kind.Down : Kind.Up, key);
        }

        internal static void Enqueue(string typed)
        {
            foreach (var unit in typed)
            {
                Enqueue(Kind.Text, unit);
            }
        }

        static void Enqueue(Kind kind, int value)
        {
            if (count == Capacity)
            {
                return;
            }
            pending[(head + count) % Capacity] = new Pending {Kind = kind, Value = value};
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
                if (next.Kind != Kind.Text)
                {
                    Take();
                    Press(next.Value, next.Kind == Kind.Down);
                    continue;
                }
                var length = 0;
                while (count > 0 && pending[head].Kind == Kind.Text)
                {
                    units[length++] = (char)Take().Value;
                }
                Type(length);
            }
            Repeat();
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

        static void Press(int key, bool down)
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
            Target()?.KeyFromDevice(key, down, Modifiers(), false);
        }

        static void Type(int length)
        {
            var view = Target();
            if (view == null || length == 0)
            {
                return;
            }
            var size = Encoding.UTF8.GetBytes(units, 0, length, text, 0);
            fixed (byte* t = text)
            {
                view.TextFromDevice(t, size);
            }
        }

        static void Repeat()
        {
            if (repeating < 0)
            {
                return;
            }
            var now = Clock();
            for (var n = 0; n < MostRepeats && now >= nextRepeat; ++n)
            {
                nextRepeat += RepeatInterval;
                Target()?.KeyFromDevice(repeating, true, Modifiers(), true);
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
            if (heldCount == 0 && !Input.anyKeyDown)
            {
                return;
            }
            for (var pass = 0; pass < 2; ++pass)
            {
                for (var i = 0; i < WeftKeys.Count; ++i)
                {
                    if (WeftKeys.TypesAt(i) == (pass == 1) && Input.GetKeyDown(WeftKeys.LegacyAt(i)))
                    {
                        Enqueue(Kind.Down, i);
                    }
                }
            }
            Enqueue(Input.inputString);
            for (var i = 0; i < WeftKeys.Count; ++i)
            {
                if (Input.GetKeyUp(WeftKeys.LegacyAt(i)))
                {
                    Enqueue(Kind.Up, i);
                }
            }
#endif
        }

#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // Before the Input System applies a keyboard's event: the keys it changes, and its text. Events the editor's
        // windows get (the game view without the focus) are not the game's.
        static void OnEvent(InputEventPtr e, InputDevice device)
        {
            if (!ReadDevices || !(device is Keyboard keyboard) || InputState.currentUpdateType == InputUpdateType.Editor)
            {
                return;
            }
            if (e.IsA<TextEvent>())
            {
                var c = TextEvent.From(e)->character;
                if (c >= 0x10000)
                {
                    Enqueue(Kind.Text, 0xD800 + ((c - 0x10000) >> 10));
                    Enqueue(Kind.Text, 0xDC00 + ((c - 0x10000) & 0x3FF));
                }
                else
                {
                    Enqueue(Kind.Text, c);
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
                    Enqueue(control.isPressed ? Kind.Up : Kind.Down, i);
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
        }
    }
}
