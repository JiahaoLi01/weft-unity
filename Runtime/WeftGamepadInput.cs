using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace Weft
{
    // The gamepads, for the Views that read them (WeftView.Gamepads): up to four in the Gamepad API's standard layout,
    // read at the start of each frame's Update after the keyboard, and given to each View when one's state changed (all
    // of them again after the View loads a page), as a browser gives its pages the gamepads. Read from the Input System
    // when the project uses it (Gamepad.all, each in the first free slot as it comes), else from XInput on Windows (its
    // four users, as Chrome reads them; a missing one is looked for again once a second, as XInput is slow to say none
    // is there). On a page with spatial navigation weft makes the D-pad and the left stick, A and B the arrow keys, Enter
    // and Escape of the View with the focus; what the page used of them is its own until it comes up
    // (WeftView.GamepadUsed).
    internal static unsafe class WeftGamepadInput
    {
        const int Slots = WeftNative.GamepadSlots;
        const int Buttons = WeftNative.GamepadButtons;
        const int Axes = WeftNative.GamepadAxes;
        const int MaxId = 128;
        // Chrome's id of an XInput gamepad, and its trigger's press point (XINPUT_GAMEPAD_TRIGGER_THRESHOLD of 255).
        internal const string XInputId = "Xbox 360 Controller (XInput STANDARD GAMEPAD)";
        const float TriggerPressed = 30 / 255f;
        // How far the left stick goes before it is an arrow key (weft's rule), and the bits past the buttons' of the
        // directions it is then (up, down, left, right), each held as a key of its own.
        const float StickArrow = 0.5f;
        const int StickBits = WeftNative.GamepadButtons;
        internal const uint LeftStickMask = 0xFu << StickBits;

        // A slot's latest state, and how many times it changed (0: no gamepad was ever in it).
        sealed class Pad
        {
            public bool Connected;
            public string Id = string.Empty;
            public int IdSize;
            public readonly float[] Buttons = new float[WeftGamepadInput.Buttons];
            public readonly float[] Axes = new float[WeftGamepadInput.Axes];
            public uint Pressed;
            public int Version;
        }

        static readonly Pad[] pads = {new Pad(), new Pad(), new Pad(), new Pad()};
        // Each slot's id in UTF-8 at slot * MaxId, which the states point into while they are given.
        static readonly byte[] ids = new byte[Slots * MaxId];
        // The inputs each slot holds (the buttons pressed by WeftGamepadButton bits, and the left stick's direction past
        // half) after this frame's reading and before it.
        static readonly uint[] held = new uint[Slots];
        static readonly uint[] heldBefore = new uint[Slots];
        static readonly float[] buttons = new float[Buttons];
        static readonly float[] axes = new float[Axes];
        static readonly List<WeftView> views = new List<WeftView>();

        // What the tests replace: whether the devices are read (else SetState gives the states).
        internal static bool ReadDevices = true;

        internal static void Attach(WeftView view)
        {
            if (!views.Contains(view))
            {
                views.Add(view);
            }
        }

        internal static void Detach(WeftView view)
        {
            views.Remove(view);
        }

        // At the start of each frame's Update, after the keyboard.
        internal static void Tick()
        {
            if (views.Count == 0)
            {
                return;
            }
            if (ReadDevices)
            {
                Poll();
            }
            for (var s = 0; s < Slots; ++s)
            {
                heldBefore[s] = held[s];
                held[s] = Held(pads[s]);
            }
            Send();
        }

        static uint Held(Pad pad)
        {
            if (!pad.Connected)
            {
                return 0;
            }
            float x = pad.Axes[0], y = pad.Axes[1];
            if (Math.Max(Math.Abs(x), Math.Abs(y)) <= StickArrow)
            {
                return pad.Pressed;
            }
            var direction = Math.Abs(x) > Math.Abs(y) ? x < 0 ? 2 : 3 : y < 0 ? 0 : 1;
            return pad.Pressed | 1u << (StickBits + direction);
        }

        // The states that changed since each View last got them, in one call a View.
        static void Send()
        {
            var batch = stackalloc WeftNative.GamepadState[Slots];
            var slots = stackalloc int[Slots];
            var wentDown = stackalloc uint[Slots];
            var cameUp = stackalloc uint[Slots];
            for (var s = 0; s < Slots; ++s)
            {
                wentDown[s] = held[s] & ~heldBefore[s];
                cameUp[s] = heldBefore[s] & ~held[s];
            }
            fixed (byte* id = ids)
            {
                for (var v = 0; v < views.Count; ++v)
                {
                    var view = views[v];
                    if (!view.ReadsGamepads)
                    {
                        continue;
                    }
                    var count = 0;
                    for (var s = 0; s < Slots; ++s)
                    {
                        var pad = pads[s];
                        if (view.gamepadVersions[s] == pad.Version)
                        {
                            continue;
                        }
                        view.gamepadVersions[s] = pad.Version;
                        ref var state = ref batch[count];
                        state = new WeftNative.GamepadState
                        {
                            StructSize = (uint)sizeof(WeftNative.GamepadState),
                            Index = (uint)s,
                            Connected = pad.Connected ? 1u : 0u,
                            Pressed = pad.Pressed,
                            Id = id + s * MaxId,
                            IdSize = (UIntPtr)pad.IdSize,
                        };
                        for (var i = 0; i < Buttons; ++i)
                        {
                            state.Buttons[i] = pad.Buttons[i];
                        }
                        for (var i = 0; i < Axes; ++i)
                        {
                            state.Axes[i] = pad.Axes[i];
                        }
                        slots[count++] = s;
                    }
                    if (count > 0)
                    {
                        view.GamepadsFromDevice(batch, slots, count, wentDown, cameUp);
                    }
                }
            }
        }

        // A slot's gamepad now: its id, its buttons' values (0 to 1) with those pressed, its axes (-1 to 1, y down).
        static void Store(int slot, string id, float[] values, uint pressed, float[] sticks)
        {
            var pad = pads[slot];
            var changed = !pad.Connected || pad.Pressed != pressed || !ReferenceEquals(pad.Id, id) && pad.Id != id;
            for (var i = 0; i < Buttons && !changed; ++i)
            {
                changed = pad.Buttons[i] != values[i];
            }
            for (var i = 0; i < Axes && !changed; ++i)
            {
                changed = pad.Axes[i] != sticks[i];
            }
            if (!changed)
            {
                return;
            }
            if (!ReferenceEquals(pad.Id, id) && pad.Id != id)
            {
                pad.Id = id;
                pad.IdSize = Utf8Id(id, slot);
            }
            pad.Connected = true;
            pad.Pressed = pressed;
            Array.Copy(values, pad.Buttons, Buttons);
            Array.Copy(sticks, pad.Axes, Axes);
            ++pad.Version;
        }

        static void Disconnect(int slot)
        {
            var pad = pads[slot];
            if (!pad.Connected)
            {
                return;
            }
            pad.Connected = false;
            pad.Pressed = 0;
            Array.Clear(pad.Buttons, 0, Buttons);
            Array.Clear(pad.Axes, 0, Axes);
            ++pad.Version;
        }

        // An id in UTF-8 at the slot's place, cut to 128 bytes at a character's start.
        static int Utf8Id(string id, int slot)
        {
            var bytes = Encoding.UTF8.GetBytes(id ?? string.Empty);
            var size = Math.Min(bytes.Length, MaxId);
            while (size < bytes.Length && size > 0 && (bytes[size] & 0xC0) == 0x80)
            {
                --size;
            }
            Array.Copy(bytes, 0, ids, slot * MaxId, size);
            return size;
        }

        static float Unit(float value)
        {
            return float.IsNaN(value) ? 0 : Mathf.Clamp01(value);
        }

        static float Axis(float value)
        {
            return float.IsNaN(value) ? 0 : Mathf.Clamp(value, -1, 1);
        }

        // The tests' gamepad in a slot (with ReadDevices off): the buttons of pressed all the way down, the axes.
        internal static void SetState(int slot, bool connected, uint pressed = 0, float[] sticks = null, string id = XInputId)
        {
            if (!connected)
            {
                Disconnect(slot);
                return;
            }
            for (var i = 0; i < Buttons; ++i)
            {
                buttons[i] = (pressed >> i & 1) != 0 ? 1 : 0;
            }
            for (var i = 0; i < Axes; ++i)
            {
                axes[i] = sticks != null && i < sticks.Length ? Axis(sticks[i]) : 0;
            }
            Store(slot, id, buttons, pressed, axes);
        }

        static void Poll()
        {
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            PollInputSystem();
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            PollXInput();
#endif
        }

#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
        // The Input System's device in each slot (0, InputDevice.InvalidDeviceId: none), and the slot's id.
        static readonly int[] devices = new int[Slots];
        static readonly string[] deviceIds = new string[Slots];

        static void PollInputSystem()
        {
            for (var s = 0; s < Slots; ++s)
            {
                if (devices[s] != InputDevice.InvalidDeviceId &&
                    !(InputSystem.GetDeviceById(devices[s]) is Gamepad gone && gone.added && gone.enabled))
                {
                    devices[s] = InputDevice.InvalidDeviceId;
                    Disconnect(s);
                }
            }
            var all = Gamepad.all;
            for (var i = 0; i < all.Count; ++i)
            {
                var gamepad = all[i];
                if (!gamepad.enabled)
                {
                    continue;
                }
                var s = Array.IndexOf(devices, gamepad.deviceId);
                if (s < 0)
                {
                    s = Array.IndexOf(devices, InputDevice.InvalidDeviceId);
                    if (s < 0)
                    {
                        continue;
                    }
                    devices[s] = gamepad.deviceId;
                    deviceIds[s] = Id(gamepad);
                }
                FromInputSystem(gamepad, buttons, out var pressed, axes);
                Store(s, deviceIds[s], buttons, pressed, axes);
            }
        }

        // Chrome's id: its XInput one for an XInput pad, else the product's name and the standard mapping.
        internal static string Id(Gamepad gamepad)
        {
            if (gamepad is UnityEngine.InputSystem.XInput.XInputController)
            {
                return XInputId;
            }
            var product = gamepad.description.product;
            return (string.IsNullOrEmpty(product) ? gamepad.displayName : product) + " (STANDARD GAMEPAD)";
        }

        // A gamepad of the Input System in the standard layout: the face buttons by place (south A, east B, west X,
        // north Y), the triggers pressed from 30 of 255 as Chrome's XInput ones, the other buttons from half; the sticks
        // as the device gives them (no dead zone, as Chrome's), y down.
        internal static void FromInputSystem(Gamepad gamepad, float[] values, out uint pressed, float[] sticks)
        {
            values[0] = gamepad.buttonSouth.ReadValue();
            values[1] = gamepad.buttonEast.ReadValue();
            values[2] = gamepad.buttonWest.ReadValue();
            values[3] = gamepad.buttonNorth.ReadValue();
            values[4] = gamepad.leftShoulder.ReadValue();
            values[5] = gamepad.rightShoulder.ReadValue();
            values[6] = gamepad.leftTrigger.ReadValue();
            values[7] = gamepad.rightTrigger.ReadValue();
            values[8] = gamepad.selectButton.ReadValue();
            values[9] = gamepad.startButton.ReadValue();
            values[10] = gamepad.leftStickButton.ReadValue();
            values[11] = gamepad.rightStickButton.ReadValue();
            values[12] = gamepad.dpad.up.ReadValue();
            values[13] = gamepad.dpad.down.ReadValue();
            values[14] = gamepad.dpad.left.ReadValue();
            values[15] = gamepad.dpad.right.ReadValue();
            values[16] = 0;
            pressed = 0;
            for (var i = 0; i < Buttons; ++i)
            {
                values[i] = Unit(values[i]);
                if (values[i] >= (i == 6 || i == 7 ? TriggerPressed : 0.5f))
                {
                    pressed |= 1u << i;
                }
            }
            var left = gamepad.leftStick.ReadUnprocessedValue();
            var right = gamepad.rightStick.ReadUnprocessedValue();
            sticks[0] = Axis(left.x);
            sticks[1] = Axis(-left.y);
            sticks[2] = Axis(right.x);
            sticks[3] = Axis(-right.y);
        }
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        [StructLayout(LayoutKind.Sequential)]
        internal struct XInputState
        {
            public uint PacketNumber;
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short ThumbLX;
            public short ThumbLY;
            public short ThumbRX;
            public short ThumbRY;
        }

        // XInput's button of each standard button (none for the triggers); the guide button only XInputGetStateEx reads.
        static readonly ushort[] xinputButtons =
        {
            0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0, 0, 0x0020, 0x0010, 0x0040, 0x0080, 0x0001, 0x0002, 0x0004,
            0x0008, 0x0400,
        };

        // When each missing user is looked for again; whether XInput, or its XInputGetStateEx, is missing.
        static readonly double[] lookAgain = new double[Slots];
        static bool noXInput;
        static bool noStateEx;

        static void PollXInput()
        {
            var now = Time.realtimeSinceStartupAsDouble;
            for (var user = 0; user < Slots; ++user)
            {
                if (!pads[user].Connected && now < lookAgain[user])
                {
                    continue;
                }
                if (!XInput((uint)user, out var state, out var guide))
                {
                    lookAgain[user] = now + 1;
                    Disconnect(user);
                    continue;
                }
                var pressed = FromXInput(state, guide, buttons, axes);
                Store(user, XInputId, buttons, pressed, axes);
            }
        }

        static bool XInput(uint user, out XInputState state, out bool guide)
        {
            state = default;
            guide = false;
            if (noXInput)
            {
                return false;
            }
            try
            {
                if (!noStateEx)
                {
                    try
                    {
                        guide = true;
                        return XInputGetStateEx(user, out state) == 0;
                    }
                    catch (EntryPointNotFoundException)
                    {
                        noStateEx = true;
                        guide = false;
                    }
                }
                return XInputGetState(user, out state) == 0;
            }
            catch (DllNotFoundException)
            {
                noXInput = true;
                return false;
            }
        }

        // Chrome's XInput gamepad (XInputDataFetcherWin): A, B, X, Y, the shoulders, the triggers (their value of 255,
        // pressed from 30), back, start, the stick presses, the D-pad, the guide button when XInputGetStateEx reads it;
        // the sticks from -32768..32767 to -1..1, y down. The buttons pressed.
        internal static uint FromXInput(in XInputState x, bool guide, float[] values, float[] sticks)
        {
            uint pressed = 0;
            for (var i = 0; i < Buttons; ++i)
            {
                bool on;
                if (i == 6 || i == 7)
                {
                    var trigger = i == 6 ? x.LeftTrigger : x.RightTrigger;
                    values[i] = trigger / 255f;
                    on = trigger >= 30;
                }
                else
                {
                    on = (i < 16 || guide) && (x.Buttons & xinputButtons[i]) != 0;
                    values[i] = on ? 1 : 0;
                }
                if (on)
                {
                    pressed |= 1u << i;
                }
            }
            sticks[0] = Normalize(x.ThumbLX);
            sticks[1] = -Normalize(x.ThumbLY);
            sticks[2] = Normalize(x.ThumbRX);
            sticks[3] = -Normalize(x.ThumbRY);
            return pressed;
        }

        static float Normalize(short value)
        {
            return Mathf.Clamp((value + 32768f) / 32767.5f - 1, -1, 1);
        }

        [DllImport("xinput1_4.dll", EntryPoint = "#100")]
        static extern uint XInputGetStateEx(uint user, out XInputState state);

        [DllImport("xinput1_4.dll")]
        static extern uint XInputGetState(uint user, out XInputState state);
#endif

        // A play session without a domain reload keeps static state.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            views.Clear();
            ReadDevices = true;
            for (var s = 0; s < Slots; ++s)
            {
                pads[s] = new Pad();
                held[s] = heldBefore[s] = 0;
#if WEFT_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
                devices[s] = InputDevice.InvalidDeviceId;
                deviceIds[s] = null;
#elif UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                lookAgain[s] = 0;
#endif
            }
        }
    }
}
