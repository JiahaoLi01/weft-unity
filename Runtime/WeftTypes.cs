using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Weft
{
    /// <summary>A message of the page: weft.engine.send(name, value), or weft.engine.call (complete it by its call id).</summary>
    public readonly struct WeftMessage
    {
        public readonly string Name;
        /// <summary>The value as JSON text ("null" for none).</summary>
        public readonly string Json;
        /// <summary>The call's id for <see cref="WeftView.CompleteCall"/>; 0 for a message sent.</summary>
        public readonly ulong CallId;

        public WeftMessage(string name, string json, ulong callId)
        {
            Name = name;
            Json = json;
            CallId = callId;
        }

        public bool IsCall => CallId != 0;
    }

    public enum WeftDiagnosticKind
    {
        /// <summary>A script or task threw.</summary>
        Script = 1,
        /// <summary>A weft.engine handler or subscriber threw.</summary>
        Handler = 2,
        /// <summary>An animation frame callback threw.</summary>
        Frame = 3,
        /// <summary>The page's styles make no scene (the last frame stays), or an image it names is missing.</summary>
        Scene = 4,
    }

    public readonly struct WeftDiagnostic
    {
        public readonly WeftDiagnosticKind Kind;
        public readonly string Text;

        public WeftDiagnostic(WeftDiagnosticKind kind, string text)
        {
            Kind = kind;
            Text = text;
        }
    }

    [Flags]
    public enum WeftAnchorFlags : uint
    {
        Visible = 0,
        Hidden = 1,
        Behind = 2,
        Offscreen = 4,
    }

    /// <summary>
    /// Where the page's element with data-anchor="key" goes this frame: x, y in the View's logical px, scaled by
    /// scale, ordered by depth (far to near among siblings).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WeftAnchor
    {
        public ulong Key;
        public float X;
        public float Y;
        public float Scale;
        public float Depth;
        public WeftAnchorFlags Flags;
        uint reserved;

        public WeftAnchor(ulong key, float x, float y, float scale = 1, float depth = 0, WeftAnchorFlags flags = WeftAnchorFlags.Visible)
        {
            Key = key;
            X = x;
            Y = y;
            Scale = scale;
            Depth = depth;
            Flags = flags;
            reserved = 0;
        }
    }

    public enum WeftPointerKind : uint
    {
        Move = 0,
        Down = 1,
        Up = 2,
        Wheel = 3,
        /// <summary>The pointer left the View.</summary>
        Leave = 4,
    }

    /// <summary>MouseEvent.buttons: the buttons held after an event.</summary>
    [Flags]
    public enum WeftButtons : uint
    {
        None = 0,
        Left = 1,
        Right = 2,
        Middle = 4,
    }

    public enum WeftKeyKind : uint
    {
        Down = 0,
        Up = 1,
    }

    /// <summary>
    /// The modifiers held during a key event (KeyboardEvent.getModifierState): a modifier key's own keydown has its flag,
    /// its keyup not.
    /// </summary>
    [Flags]
    public enum WeftModifiers : uint
    {
        None = 0,
        Shift = 1,
        Control = 2,
        Alt = 4,
        Meta = 8,
        AltGraph = 16,
        CapsLock = 32,
        NumLock = 64,
    }

    /// <summary>What the page did with a key or the text it typed.</summary>
    [Flags]
    public enum WeftInputResult : uint
    {
        /// <summary>Nothing: the key is the game's, as Chrome gives the browser the keys a page did not use.</summary>
        None = 0,
        /// <summary>A listener prevented its default action.</summary>
        Prevented = 1,
        /// <summary>weft acted on it, or began to: it moved the focus, scrolled, clicked, or made a button :active.</summary>
        Default = 2,
    }

    /// <summary>
    /// Where text input goes (<see cref="WeftView.TextInput"/>): whether a text field the user may edit (an input of a text
    /// type, a textarea) has the page's focus while the View has the keyboard, whether an input method composes in it,
    /// whether it is a password field (where Chrome keeps input methods off), and its caret and border box in the View's
    /// logical px (top-left origin): an input method's candidate window goes below the caret.
    /// </summary>
    public readonly struct WeftTextInput
    {
        public readonly bool Active;
        public readonly bool Composing;
        public readonly bool Password;
        public readonly Rect Caret;
        public readonly Rect Field;

        public WeftTextInput(bool active, bool composing, bool password, Rect caret, Rect field)
        {
            Active = active;
            Composing = composing;
            Password = password;
            Caret = caret;
            Field = field;
        }
    }

    /// <summary>What is under a point of the View.</summary>
    public readonly struct WeftHit
    {
        /// <summary>Whether the topmost element taking pointer events is something other than the page's root.</summary>
        public readonly bool Element;
        /// <summary>That element's CSS cursor ("auto" when none declares one).</summary>
        public readonly string Cursor;

        public WeftHit(bool element, string cursor)
        {
            Element = element;
            Cursor = cursor;
        }

        /// <summary>Whether the page takes a click here: an element that shows something to interact with.</summary>
        public bool Interactive => Element && Cursor != "auto" && Cursor != "default" && Cursor != "none";
    }
}
