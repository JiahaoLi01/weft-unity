using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Weft
{
    /// <summary>
    /// The keyboard's physical keys by their KeyboardEvent.code ("KeyA", "Enter", "ArrowLeft"), from the Input Manager's
    /// key codes (physical keys, as Unity's Use Physical Keys setting has them by default) and the Input System's keys,
    /// and the key values the system's keyboard layout gives them.
    /// </summary>
    public static unsafe class WeftKeys
    {
        // A key the Views read: its code, its Input Manager key code, its Input System key (by name: the package is
        // optional), the modifier it holds, and whether it types a character (the keys that do go down after the others
        // of a frame, before the frame's text).
        readonly struct Entry
        {
            public readonly string Code;
            public readonly KeyCode Legacy;
            public readonly string System;
            public readonly WeftModifiers Holds;
            public readonly bool Types;

            public Entry(string code, KeyCode legacy, string system, WeftModifiers holds = WeftModifiers.None, bool types = false)
            {
                Code = code;
                Legacy = legacy;
                System = system;
                Holds = holds;
                Types = types;
            }
        }

        static readonly Entry[] entries = Table();
        static readonly Dictionary<string, int> byCode = new Dictionary<string, int>();
        static readonly int[] byLegacy = new int[512];
        internal static readonly byte[][] Utf8 = new byte[entries.Length][];
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        static readonly int[] bySystem = new int[Keyboard.KeyCount + 1];
        internal static readonly Key[] SystemKeys = new Key[entries.Length];
#endif

        static WeftKeys()
        {
            for (var i = 0; i < byLegacy.Length; ++i)
            {
                byLegacy[i] = -1;
            }
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            for (var i = 0; i < bySystem.Length; ++i)
            {
                bySystem[i] = -1;
            }
#endif
            for (var i = 0; i < entries.Length; ++i)
            {
                byCode[entries[i].Code] = i;
                Utf8[i] = Encoding.UTF8.GetBytes(entries[i].Code);
                if ((int)entries[i].Legacy < byLegacy.Length)
                {
                    byLegacy[(int)entries[i].Legacy] = i;
                }
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                if (entries[i].System != null && Enum.TryParse(entries[i].System, out Key key) && (int)key < bySystem.Length)
                {
                    bySystem[(int)key] = i;
                    SystemKeys[i] = key;
                }
#endif
            }
        }

        /// <summary>The code of an Input Manager key code (null for one that is not a key the Views read).</summary>
        public static string Code(KeyCode key)
        {
            var index = IndexOf(key);
            return index < 0 ? null : entries[index].Code;
        }

        /// <summary>
        /// The KeyboardEvent.key the system's keyboard layout gives a physical key with the modifiers (Shift, AltGraph,
        /// CapsLock and NumLock count): the character it types ("a", "A", "!", "é"), "Dead" for a dead key, its name for a
        /// key that types nothing ("Enter", "ArrowLeft"), "Unidentified" when the layout types nothing with it or weft does
        /// not know the code.
        /// </summary>
        public static string KeyValue(string code, WeftModifiers modifiers = WeftModifiers.None)
        {
            var bytes = WeftRuntime.Utf8(code);
            var key = stackalloc byte[32];
            fixed (byte* c = bytes)
            {
                if (WeftNative.weft_keyboard_key(c, (UIntPtr)bytes.Length, (uint)modifiers, key) != WeftNative.Result.Ok)
                {
                    return "Unidentified";
                }
            }
            var length = 0;
            while (length < 31 && key[length] != 0)
            {
                ++length;
            }
            return Encoding.UTF8.GetString(key, length);
        }

        internal static int Count => entries.Length;

        internal static string CodeAt(int index) => entries[index].Code;

        internal static KeyCode LegacyAt(int index) => entries[index].Legacy;

        internal static WeftModifiers HoldsAt(int index) => entries[index].Holds;

        internal static bool TypesAt(int index) => entries[index].Types;

        internal static int IndexOf(string code)
        {
            return code != null && byCode.TryGetValue(code, out var index) ? index : -1;
        }

        internal static int IndexOf(KeyCode key)
        {
            return (int)key >= 0 && (int)key < byLegacy.Length ? byLegacy[(int)key] : -1;
        }

#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // Named apart from the other IndexOf: an assembly that calls those and does not reference the Input System
        // would need it to choose among them.
        internal static int IndexOfSystemKey(Key key)
        {
            return (int)key > 0 && (int)key < bySystem.Length ? bySystem[(int)key] : -1;
        }
#endif

        static Entry[] Table()
        {
            var keys = new List<Entry>
            {
                new Entry("ShiftLeft", KeyCode.LeftShift, "LeftShift", WeftModifiers.Shift),
                new Entry("ShiftRight", KeyCode.RightShift, "RightShift", WeftModifiers.Shift),
                new Entry("ControlLeft", KeyCode.LeftControl, "LeftCtrl", WeftModifiers.Control),
                new Entry("ControlRight", KeyCode.RightControl, "RightCtrl", WeftModifiers.Control),
                new Entry("AltLeft", KeyCode.LeftAlt, "LeftAlt", WeftModifiers.Alt),
                new Entry("AltRight", KeyCode.RightAlt, "RightAlt", WeftModifiers.Alt),
                new Entry("MetaLeft", KeyCode.LeftMeta, "LeftMeta", WeftModifiers.Meta),
                new Entry("MetaRight", KeyCode.RightMeta, "RightMeta", WeftModifiers.Meta),
                new Entry("CapsLock", KeyCode.CapsLock, "CapsLock"),
                new Entry("NumLock", KeyCode.Numlock, "NumLock"),
                new Entry("ScrollLock", KeyCode.ScrollLock, "ScrollLock"),
                new Entry("Escape", KeyCode.Escape, "Escape"),
                new Entry("Tab", KeyCode.Tab, "Tab"),
                new Entry("Enter", KeyCode.Return, "Enter"),
                new Entry("NumpadEnter", KeyCode.KeypadEnter, "NumpadEnter"),
                new Entry("Backspace", KeyCode.Backspace, "Backspace"),
                new Entry("Delete", KeyCode.Delete, "Delete"),
                new Entry("Insert", KeyCode.Insert, "Insert"),
                new Entry("Home", KeyCode.Home, "Home"),
                new Entry("End", KeyCode.End, "End"),
                new Entry("PageUp", KeyCode.PageUp, "PageUp"),
                new Entry("PageDown", KeyCode.PageDown, "PageDown"),
                new Entry("ArrowLeft", KeyCode.LeftArrow, "LeftArrow"),
                new Entry("ArrowRight", KeyCode.RightArrow, "RightArrow"),
                new Entry("ArrowUp", KeyCode.UpArrow, "UpArrow"),
                new Entry("ArrowDown", KeyCode.DownArrow, "DownArrow"),
                new Entry("PrintScreen", KeyCode.Print, "PrintScreen"),
                new Entry("Pause", KeyCode.Pause, "Pause"),
                new Entry("ContextMenu", KeyCode.Menu, "ContextMenu"),
            };
            for (var i = 1; i <= 15; ++i)
            {
                keys.Add(new Entry("F" + i, KeyCode.F1 + i - 1, i <= 12 ? "F" + i : null));
            }
            keys.Add(new Entry("Space", KeyCode.Space, "Space", types: true));
            keys.Add(new Entry("Backquote", KeyCode.BackQuote, "Backquote", types: true));
            keys.Add(new Entry("Minus", KeyCode.Minus, "Minus", types: true));
            keys.Add(new Entry("Equal", KeyCode.Equals, "Equals", types: true));
            keys.Add(new Entry("BracketLeft", KeyCode.LeftBracket, "LeftBracket", types: true));
            keys.Add(new Entry("BracketRight", KeyCode.RightBracket, "RightBracket", types: true));
            keys.Add(new Entry("Backslash", KeyCode.Backslash, "Backslash", types: true));
            keys.Add(new Entry("Semicolon", KeyCode.Semicolon, "Semicolon", types: true));
            keys.Add(new Entry("Quote", KeyCode.Quote, "Quote", types: true));
            keys.Add(new Entry("Comma", KeyCode.Comma, "Comma", types: true));
            keys.Add(new Entry("Period", KeyCode.Period, "Period", types: true));
            keys.Add(new Entry("Slash", KeyCode.Slash, "Slash", types: true));
            for (var i = 0; i <= 9; ++i)
            {
                keys.Add(new Entry("Digit" + i, KeyCode.Alpha0 + i, "Digit" + i, types: true));
            }
            for (var c = 'A'; c <= 'Z'; ++c)
            {
                keys.Add(new Entry("Key" + c, KeyCode.A + (c - 'A'), c.ToString(), types: true));
            }
            for (var i = 0; i <= 9; ++i)
            {
                keys.Add(new Entry("Numpad" + i, KeyCode.Keypad0 + i, "Numpad" + i, types: true));
            }
            keys.Add(new Entry("NumpadDecimal", KeyCode.KeypadPeriod, "NumpadPeriod", types: true));
            keys.Add(new Entry("NumpadDivide", KeyCode.KeypadDivide, "NumpadDivide", types: true));
            keys.Add(new Entry("NumpadMultiply", KeyCode.KeypadMultiply, "NumpadMultiply", types: true));
            keys.Add(new Entry("NumpadSubtract", KeyCode.KeypadMinus, "NumpadMinus", types: true));
            keys.Add(new Entry("NumpadAdd", KeyCode.KeypadPlus, "NumpadPlus", types: true));
            return keys.ToArray();
        }
    }
}
