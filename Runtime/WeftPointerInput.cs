using System;
using UnityEngine;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Weft
{
    // The mouse as pointer events of a View over the whole screen, in its logical px (top-left origin): a move when it
    // moves, a down or up for each button that changes (MouseEvent.buttons after it), a wheel in px (100 a notch,
    // positive down and right, as a browser's deltaY), and a leave when it leaves the screen. Read from the Input System
    // when the project uses it, else from the Input Manager; without either there is no input.
    internal sealed class WeftPointerInput
    {
        const float WheelPixelsPerNotch = 100;
        static readonly WeftButtons[] Each = {WeftButtons.Left, WeftButtons.Right, WeftButtons.Middle};

        Vector2 last = new Vector2(float.NaN, float.NaN);
        WeftButtons buttons;
        bool inside;

        public bool Inside => inside;
        public Vector2 Position => last;

        public void Poll(float scale, Action<WeftPointerKind, Vector2, WeftButtons, Vector2> send)
        {
            if (!Read(out var screen, out var held, out var notches))
            {
                return;
            }
            var position = new Vector2(screen.x, Screen.height - screen.y) / scale;
            var within = screen.x >= 0 && screen.y >= 0 && screen.x < Screen.width && screen.y < Screen.height;
            if (!within)
            {
                if (inside)
                {
                    inside = false;
                    send(WeftPointerKind.Leave, last, buttons, Vector2.zero);
                }
                return;
            }
            if (!inside || position != last)
            {
                inside = true;
                last = position;
                send(WeftPointerKind.Move, position, buttons, Vector2.zero);
            }
            foreach (var button in Each)
            {
                if ((held & button) != (buttons & button))
                {
                    buttons ^= button;
                    send((held & button) != 0 ? WeftPointerKind.Down : WeftPointerKind.Up, position, buttons, Vector2.zero);
                }
            }
            if (notches != Vector2.zero)
            {
                send(WeftPointerKind.Wheel, position, buttons, new Vector2(notches.x, -notches.y) * WheelPixelsPerNotch);
            }
        }

        // The mouse in screen px (bottom-left origin), its buttons, and its wheel in notches (y up).
        static bool Read(out Vector2 screen, out WeftButtons held, out Vector2 notches)
        {
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            if (mouse == null)
            {
                screen = default;
                held = WeftButtons.None;
                notches = default;
                return false;
            }
            screen = mouse.position.ReadValue();
            held = (mouse.leftButton.isPressed ? WeftButtons.Left : 0) | (mouse.rightButton.isPressed ? WeftButtons.Right : 0) |
                   (mouse.middleButton.isPressed ? WeftButtons.Middle : 0);
            notches = mouse.scroll.ReadValue();
            if (InputSystem.settings.scrollDeltaBehavior == InputSettings.ScrollDeltaBehavior.KeepPlatformSpecificInputRange)
            {
                // Windows reports 120 a notch.
                notches /= 120;
            }
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (!Input.mousePresent)
            {
                screen = default;
                held = WeftButtons.None;
                notches = default;
                return false;
            }
            screen = Input.mousePosition;
            held = (Input.GetMouseButton(0) ? WeftButtons.Left : 0) | (Input.GetMouseButton(1) ? WeftButtons.Right : 0) |
                   (Input.GetMouseButton(2) ? WeftButtons.Middle : 0);
            notches = Input.mouseScrollDelta;
            return true;
#else
            screen = default;
            held = WeftButtons.None;
            notches = default;
            return false;
#endif
        }
    }
}
