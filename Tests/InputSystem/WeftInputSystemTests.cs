using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    // The keyboard from the Input System (its test fixture's keyboard): keys from its state events and text from its text
    // events reach the focused View's page in the order they came, with the modifiers held; what the page used is known by
    // the Input System's keys. Its input method follows the page's text field (on, its candidate window at the caret), and
    // its composition events and the text it commits reach the field.
    public sealed class WeftInputSystemTests : InputTestFixture
    {
        const string Script =
            "const root = document.getElementById('root');" +
            "const log = s => weft.engine.send('log', s);" +
            "const b = root.appendChild(document.createElement('button'));" +
            "b.className = 'b';" +
            "b.addEventListener('focus', () => log('focus'));" +
            "b.addEventListener('click', () => log('click'));" +
            "document.addEventListener('keydown', e => log('keydown:' + e.code + ':' + e.key + (e.shiftKey ? ':shift' : '')));" +
            "document.addEventListener('keyup', e => log('keyup:' + e.code));" +
            "document.addEventListener('keypress', e => log('keypress:' + e.key));";

        const string Css = "#root{position:relative;width:100%;height:100%;margin:0}.b{position:absolute;left:0;top:0;width:16px;height:16px}";

        const string FieldScript =
            "const root = document.getElementById('root');" +
            "const log = s => weft.engine.send('log', s);" +
            "const t = root.appendChild(document.createElement('input'));" +
            "document.addEventListener('keydown', e => log('keydown:' + e.key + ':' + e.keyCode + (e.isComposing ? ':composing' : '')));" +
            "document.addEventListener('keyup', e => log('keyup:' + e.code + (e.isComposing ? ':composing' : '')));" +
            "for (const type of ['compositionstart', 'compositionupdate', 'compositionend'])" +
            "  t.addEventListener(type, e => log(type + ':' + e.data));" +
            "t.addEventListener('input', e => log('input:' + e.inputType + ':' + t.value));";

        const string FieldCss =
            "#root{position:relative;width:100%;height:100%;margin:0}input{position:absolute;left:0;top:0;width:100px;height:20px}";

        GameObject host;
        readonly List<string> log = new List<string>();
        // What the View last told the keyboard's input method.
        bool imeEnabled;
        Vector2 imeCursor;

        public override void Setup()
        {
            base.Setup();
            WeftKeyboardInput.Locks = () => WeftModifiers.None;
        }

        public override unsafe void TearDown()
        {
            InputSystem.onDeviceCommand -= OnDeviceCommand;
            if (host)
            {
                Object.DestroyImmediate(host);
            }
            log.Clear();
            WeftKeyboardInput.Restore();
            // The fixture's gamepads go with it.
            for (var slot = 0; slot < 4; ++slot)
            {
                WeftGamepadInput.SetState(slot, false);
            }
            base.TearDown();
        }

        WeftView Make(string script = Script, string css = Css, bool fullScreen = false, bool spatialNavigation = false, int height = 32)
        {
            host = new GameObject("Weft View");
            host.SetActive(false);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = fullScreen;
            view.Size = new Vector2Int(64, height);
            view.MessageReceived += m =>
            {
                if (m.Name == "log")
                {
                    log.Add(m.Json.Trim('"'));
                }
            };
            view.DiagnosticReceived += d => Assert.Fail($"{d.Kind}: {d.Text}");
            var page = WeftPage.FromSource(script, css);
            page.ForgivingCss = true;
            page.SpatialNavigation = spatialNavigation;
            view.Load(page);
            host.SetActive(true);
            return view;
        }

        // The commands the View gives the keyboard's input method.
        unsafe void FollowInputMethod()
        {
            InputSystem.onDeviceCommand += OnDeviceCommand;
        }

        unsafe long? OnDeviceCommand(InputDevice device, InputDeviceCommand* command)
        {
            if (command->type == EnableIMECompositionCommand.Type)
            {
                imeEnabled = ((EnableIMECompositionCommand*)command)->imeEnabled;
            }
            else if (command->type == SetIMECursorPositionCommand.Type)
            {
                imeCursor = ((SetIMECursorPositionCommand*)command)->position;
            }
            return null;
        }

        static void Compose(Keyboard keyboard, string composition)
        {
            var e = IMECompositionEvent.Create(keyboard.deviceId, composition, InputState.currentTime);
            InputSystem.QueueEvent(ref e);
            InputSystem.Update();
        }

        [UnityTest]
        public IEnumerator KeysAndTextInTheOrderTheyCame()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var view = Make();
            yield return null;
            yield return null;
            view.Focus();
            Press(keyboard.tabKey);
            Release(keyboard.tabKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.Tab), "Tab moved the focus");
            var a = WeftKeys.KeyValue("KeyA");
            Press(keyboard.aKey);
            InputSystem.QueueTextEvent(keyboard, a[0]);
            InputSystem.Update();
            Release(keyboard.aKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.A), Is.False, "a letter the page only heard");
            var b = WeftKeys.KeyValue("KeyB", WeftModifiers.Shift);
            Press(keyboard.leftShiftKey);
            Press(keyboard.bKey);
            Release(keyboard.bKey);
            Release(keyboard.leftShiftKey);
            Press(keyboard.enterKey);
            Release(keyboard.enterKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.Enter), "Enter clicked the button");
            Assert.That(log, Is.EqualTo(new[]
            {
                "keydown:Tab:Tab", "focus", "keyup:Tab", $"keydown:KeyA:{a}", $"keypress:{a}", "keyup:KeyA",
                "keydown:ShiftLeft:Shift:shift", $"keydown:KeyB:{b}:shift", "keyup:KeyB", "keyup:ShiftLeft", "keydown:Enter:Enter",
                "keypress:Enter", "click", "keyup:Enter",
            }));
        }

        [UnityTest]
        public IEnumerator KeysGoNowhereWithoutTheFocus()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            var view = Make();
            yield return null;
            Press(keyboard.tabKey);
            Release(keyboard.tabKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.Tab), Is.False);
            Assert.That(log, Is.Empty, "the game has the keyboard");
            Assert.That(Key.NumpadMinus.WeftCode(), Is.EqualTo("NumpadSubtract"));
            Assert.That(Key.LeftMeta.WeftCode(), Is.EqualTo("MetaLeft"));
            Assert.That(Key.IMESelected.WeftCode(), Is.Null);
        }

        [UnityTest]
        public IEnumerator TheInputMethodComposesInTheTextField()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            FollowInputMethod();
            var view = Make(FieldScript, FieldCss, fullScreen: true);
            yield return null;
            yield return null;
            var at = new Vector2(10, 10);
            view.Pointer(WeftPointerKind.Move, at);
            view.Pointer(WeftPointerKind.Down, at, WeftButtons.Left);
            view.Pointer(WeftPointerKind.Up, at);
            yield return null;
            Assert.That(imeEnabled, "the input method is on for the text field");
            var caret = view.TextInput.Caret;
            Assert.That(caret.height, Is.GreaterThan(0));
            Assert.That(imeCursor, Is.EqualTo(new Vector2(caret.xMin, caret.yMax) * view.Scale), "its candidate window goes below the caret");
            // Pinyin: the key that starts a composition comes before its event; Space commits 你, its composition ending
            // before the text comes.
            Press(keyboard.nKey);
            Compose(keyboard, "n");
            Release(keyboard.nKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.N), "the input method took N");
            Press(keyboard.iKey);
            Compose(keyboard, "ni");
            Release(keyboard.iKey);
            yield return null;
            Assert.That(view.TextInput.Composing);
            Assert.That(imeCursor.x, Is.GreaterThan(caret.xMin * view.Scale), "the candidate window follows the caret");
            Press(keyboard.spaceKey);
            Compose(keyboard, string.Empty);
            InputSystem.QueueTextEvent(keyboard, '\u4f60');
            InputSystem.Update();
            Release(keyboard.spaceKey);
            yield return null;
            Assert.That(view.KeyUsed(Key.Space), "the input method took Space");
            Assert.That(view.TextInput.Composing, Is.False);
            Assert.That(log, Is.EqualTo(new[]
            {
                "keydown:Process:229", "compositionstart:", "compositionupdate:n", "input:insertCompositionText:n", "keyup:KeyN:composing",
                "keydown:Process:229:composing", "compositionupdate:ni", "input:insertCompositionText:ni", "keyup:KeyI:composing",
                "keydown:Process:229:composing", "compositionupdate:\u4f60", "input:insertCompositionText:\u4f60",
                "compositionend:\u4f60", "keyup:Space",
            }));
            view.Blur();
            yield return null;
            Assert.That(imeEnabled, Is.False, "the input method is off while the game has the keyboard");
        }

        // Three buttons one under the other with spatial navigation; the page logs its keys, focus and gamepad events,
        // and on 'probe' the first gamepad's left trigger and axes.
        const string GamepadScript =
            "const root = document.getElementById('root');" +
            "const log = s => weft.engine.send('log', s);" +
            "for (const id of ['a', 'b', 'c']) {" +
            "  const n = root.appendChild(document.createElement('button'));" +
            "  n.addEventListener('focus', () => log('focus:' + id));" +
            "}" +
            "document.addEventListener('keydown', e => log('keydown:' + e.key));" +
            "document.addEventListener('keyup', e => log('keyup:' + e.key));" +
            "addEventListener('gamepadconnected', e => log('connected:' + e.gamepad.index + ':' + e.gamepad.id));" +
            "addEventListener('gamepaddisconnected', e => log('disconnected:' + e.gamepad.index));" +
            "weft.engine.on('probe', () => { const g = navigator.getGamepads()[0];" +
            "  if (!g) return log('pad:none');" +
            "  const t = g.buttons[6];" +
            "  log('pad:' + t.pressed + ':' + t.value.toFixed(2) + ':' + g.axes.map(a => a.toFixed(2)).join(',')); });";

        const string GamepadCss =
            "#root{position:relative;width:100%;height:100%;margin:0}" +
            "button{display:block;width:40px;height:20px;margin:4px;border:0;padding:0}";

        // The Input System's gamepads: each in a slot as it comes, with Chrome's id for an XInput one; its D-pad and left
        // stick (y up there, down on the page) move the focus of the focused View's page with spatial navigation; a
        // trigger is pressed from 30 of 255; one that goes is disconnected.
        [UnityTest]
        public IEnumerator GamepadsOfTheInputSystem()
        {
            var xinput = InputSystem.AddDevice<UnityEngine.InputSystem.XInput.XInputController>();
            Assert.That(WeftGamepadInput.Id(xinput), Is.EqualTo(WeftGamepadInput.XInputId), "Chrome's id of an XInput gamepad");
            InputSystem.RemoveDevice(xinput);
            var gamepad = InputSystem.AddDevice<Gamepad>();
            var id = WeftGamepadInput.Id(gamepad);
            Assert.That(id, Does.EndWith(" (STANDARD GAMEPAD)"));
            var view = Make(GamepadScript, GamepadCss, false, true, 96);
            yield return null;
            yield return null;
            view.Focus();
            yield return null;
            log.Clear();
            Press(gamepad.dpad.down);
            yield return null;
            Assert.That(log, Is.EqualTo(new[] {"connected:0:" + id, "keydown:ArrowDown", "focus:a"}));
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.DpadDown));
            log.Clear();
            // The D-pad comes up as the stick goes right: ArrowDown's keyup before ArrowRight's keydown (nothing is to the
            // right). The D-pad's down and the stick's are both ArrowDown, held through a change from one to the other.
            Release(gamepad.dpad.down);
            Set(gamepad.leftStick, new Vector2(0.75f, 0.25f));
            yield return null;
            Assert.That(log, Is.EqualTo(new[] {"keyup:ArrowDown", "keydown:ArrowRight"}), "what comes up goes first");
            log.Clear();
            Set(gamepad.leftStick, new Vector2(0.25f, -0.75f));
            yield return null;
            Assert.That(log, Is.EqualTo(new[] {"keyup:ArrowRight", "keydown:ArrowDown", "focus:b"}), "the stick down");
            Assert.That(view.GamepadUsed(0, WeftGamepadButton.LeftStick));
            log.Clear();
            Set(gamepad.leftStick, new Vector2(0.25f, 0.25f));
            Set(gamepad.leftTrigger, 0.2f);
            yield return null;
            view.Emit("probe");
            yield return null;
            Assert.That(log, Is.EqualTo(new[] {"keyup:ArrowDown", "pad:true:0.20:0.25,-0.25,0.00,0.00"}),
                "the trigger pressed from 30 of 255; the stick's y down");
            log.Clear();
            InputSystem.RemoveDevice(gamepad);
            yield return null;
            Assert.That(log, Is.EqualTo(new[] {"disconnected:0"}));
        }
    }
}
