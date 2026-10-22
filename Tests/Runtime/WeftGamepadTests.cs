using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    // The gamepads (given by the tests in place of the devices): every page sees them after a gesture, a new page and a
    // new View get them too; on a page with spatial navigation the focused View's D-pad and left stick, A and B are the
    // arrow keys, Enter and Escape, a held direction repeats, and what the page used is not the game's (GamepadUsed); a
    // View without the focus, or a page without spatial navigation, gets no keys. XInput's state reads as Chrome reads it.
    public sealed class WeftGamepadTests
    {
        // Three buttons one under the other; the page logs its keys, focus, clicks and gamepad events, and on 'probe' the
        // first gamepad: how many of its buttons are pressed, and its axes.
        const string Script =
            "const root = document.getElementById('root');" +
            "const log = s => weft.engine.send('log', s);" +
            "for (const id of ['a', 'b', 'c']) {" +
            "  const n = root.appendChild(document.createElement('button'));" +
            "  n.setAttribute('id', id);" +
            "  for (const type of ['focus', 'click']) n.addEventListener(type, () => log(type + ':' + id));" +
            "}" +
            "document.addEventListener('keydown', e => log('keydown:' + e.key + (e.repeat ? ':repeat' : '')));" +
            "document.addEventListener('keyup', e => log('keyup:' + e.key));" +
            "addEventListener('gamepadconnected', e => log('connected:' + e.gamepad.index + ':' + e.gamepad.id));" +
            "addEventListener('gamepaddisconnected', e => log('disconnected:' + e.gamepad.index));" +
            "weft.engine.on('probe', () => { const g = navigator.getGamepads()[0];" +
            "  log(g ? 'pad:' + g.buttons.filter(b => b.pressed).length + ':' + g.axes.join(',') : 'pad:none'); });";

        const string Css =
            "#root{position:relative;width:100%;height:100%;margin:0}" +
            "button{display:block;width:40px;height:20px;margin:4px;border:0;padding:0}";

        const uint A = 1u << (int)WeftGamepadButton.A;
        const uint B = 1u << (int)WeftGamepadButton.B;
        const uint DpadDown = 1u << (int)WeftGamepadButton.DpadDown;
        static readonly string Connected = "connected:0:" + WeftGamepadInput.XInputId;

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly Dictionary<WeftView, List<string>> logs = new Dictionary<WeftView, List<string>>();

        [SetUp]
        public void SetUp()
        {
            WeftKeyboardInput.ReadDevices = false;
            WeftGamepadInput.ReadDevices = false;
            for (var slot = 0; slot < 4; ++slot)
            {
                WeftGamepadInput.SetState(slot, false);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var host in hosts)
            {
                Object.DestroyImmediate(host);
            }
            hosts.Clear();
            logs.Clear();
            for (var slot = 0; slot < 4; ++slot)
            {
                WeftGamepadInput.SetState(slot, false);
            }
            WeftGamepadInput.ReadDevices = true;
            WeftKeyboardInput.Restore();
        }

        WeftView Make(bool spatialNavigation = true)
        {
            var host = new GameObject("Weft View");
            host.SetActive(false);
            hosts.Add(host);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(64, 96);
            var log = new List<string>();
            logs[view] = log;
            view.MessageReceived += m =>
            {
                if (m.Name == "log")
                {
                    log.Add(m.Json.Trim('"'));
                }
            };
            view.DiagnosticReceived += d => Assert.Fail($"{d.Kind}: {d.Text}");
            view.Load(Page(spatialNavigation));
            host.SetActive(true);
            return view;
        }

        static WeftPage Page(bool spatialNavigation)
        {
            var page = WeftPage.FromSource(Script, Css);
            page.ForgivingCss = true;
            page.SpatialNavigation = spatialNavigation;
            return page;
        }

        // The page's events since the last call.
        List<string> Log(WeftView view)
        {
            var log = new List<string>(logs[view]);
            logs[view].Clear();
            return log;
        }

        static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; ++i)
            {
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator GamepadsShowAfterAGesture()
        {
            var view = Make(false);
            yield return Frames(2);
            view.Focus();
            WeftGamepadInput.SetState(0, true, 0, new[] {0.25f, 0.5f, 0, 0});
            yield return null;
            view.Emit("probe");
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"pad:none"}), "none before a button is pressed or an axis is past half");
            WeftGamepadInput.SetState(0, true, A, new[] {0.25f, 0.5f, 0, 0});
            yield return null;
            view.Emit("probe");
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {Connected, "pad:1:0.25,0.5,0,0"}),
                "the gesture; a page without spatial navigation gets no keys");
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.A), Is.False);
            WeftGamepadInput.SetState(0, false);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"disconnected:0"}));
        }

        [UnityTest]
        public IEnumerator TheFocusedViewsGamepadMovesThePagesFocus()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            yield return null;
            Log(view);
            // The D-pad: ArrowDown focuses the first button (nothing had the focus); the page has the D-pad's down until
            // it comes up.
            WeftGamepadInput.SetState(0, true, DpadDown);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {Connected, "keydown:ArrowDown", "focus:a"}));
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown), "the page used the D-pad's down");
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.A), Is.False);
            Assert.That(view.GamepadUsed(1, WeftGamepadButton.DpadDown), Is.False, "another gamepad");
            yield return null;
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown), "while it is held");
            WeftGamepadInput.SetState(0, true);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"keyup:ArrowDown"}));
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown), "the frame it comes up");
            yield return null;
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown), Is.False, "then the game's again");
            // The left stick past half.
            WeftGamepadInput.SetState(0, true, 0, new[] {0.25f, 0.75f, 0, 0});
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"keydown:ArrowDown", "focus:b"}));
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.LeftStick));
            WeftGamepadInput.SetState(0, true, 0, new[] {0.25f, 0.25f, 0, 0});
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"keyup:ArrowDown"}));
            // A clicks the focused button, B blurs it.
            WeftGamepadInput.SetState(0, true, A);
            yield return null;
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.A), "A clicked");
            WeftGamepadInput.SetState(0, true, B);
            yield return null;
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.B), "B blurred");
            WeftGamepadInput.SetState(0, true);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"keydown:Enter", "click:b", "keyup:Enter", "keydown:Escape", "keyup:Escape"}));
        }

        [UnityTest]
        public IEnumerator AHeldDirectionRepeats()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            yield return null;
            Log(view);
            WeftGamepadInput.SetState(0, true, DpadDown);
            yield return null;
            yield return new WaitForSecondsRealtime(0.7f);
            yield return null;
            var log = Log(view);
            Assert.That(log.Count, Is.GreaterThanOrEqualTo(4), string.Join(" ", log));
            Assert.That(log.GetRange(0, 4), Is.EqualTo(new[] {Connected, "keydown:ArrowDown", "focus:a", "keydown:ArrowDown:repeat"}),
                "a repeat after half a second");
        }

        [UnityTest]
        public IEnumerator WithoutTheFocusTheGamepadHasNoKeys()
        {
            var view = Make();
            yield return Frames(2);
            WeftGamepadInput.SetState(0, true, DpadDown);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {Connected}));
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown), Is.False);
        }

        [UnityTest]
        public IEnumerator NewPagesAndViewsGetTheGamepads()
        {
            const string id = "Test Pad (STANDARD GAMEPAD)";
            var view = Make(false);
            yield return Frames(2);
            WeftGamepadInput.SetState(2, true, A, null, id);
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"connected:2:" + id}));
            view.Load(Page(false));
            yield return Frames(2);
            Assert.That(Log(view), Is.EqualTo(new[] {"connected:2:" + id}), "a page loaded again");
            var other = Make(false);
            yield return Frames(2);
            Assert.That(Log(other), Is.EqualTo(new[] {"connected:2:" + id}), "a View made after");
            view.Gamepads = false;
            WeftGamepadInput.SetState(2, false);
            yield return null;
            Assert.That(Log(view), Is.Empty, "a View that does not read the gamepads");
            Assert.That(Log(other), Is.EqualTo(new[] {"disconnected:2"}));
        }

#if !ENABLE_INPUT_SYSTEM && (UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN)
        [Test]
        public void XInputReadsAsChrome()
        {
            var state = new WeftGamepadInput.XInputState
            {
                Buttons = 0x1000 | 0x0001 | 0x0400 | 0x0020,
                LeftTrigger = 29,
                RightTrigger = 30,
                ThumbLX = -32768,
                ThumbLY = 32767,
                ThumbRX = 0,
                ThumbRY = -32768,
            };
            var buttons = new float[17];
            var axes = new float[4];
            var pressed = WeftGamepadInput.FromXInput(state, true, buttons, axes);
            Assert.That(pressed, Is.EqualTo(1u << 0 | 1u << 7 | 1u << 8 | 1u << 12 | 1u << 16),
                "A, the right trigger from 30 of 255, back, the D-pad's up, the guide button");
            Assert.That(buttons[0], Is.EqualTo(1));
            Assert.That(buttons[6], Is.EqualTo(29 / 255f), "the left trigger's value, not pressed");
            Assert.That(buttons[7], Is.EqualTo(30 / 255f));
            Assert.That(buttons[16], Is.EqualTo(1));
            Assert.That(axes[0], Is.EqualTo(-1), "left");
            Assert.That(axes[1], Is.EqualTo(-1), "up, y down");
            Assert.That(axes[2], Is.EqualTo(1.5e-5f).Within(1e-6f), "Chrome's rest: (0 + 32768) / 32767.5 - 1");
            Assert.That(axes[3], Is.EqualTo(1), "down");
            pressed = WeftGamepadInput.FromXInput(state, false, buttons, axes);
            Assert.That(pressed & 1u << 16, Is.Zero, "no guide button without XInputGetStateEx");
            Assert.That(buttons[16], Is.Zero);
        }
#endif
    }
}
