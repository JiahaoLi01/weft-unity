using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    // The keyboard from the Input System (its test fixture's keyboard): keys from its state events and text from its text
    // events reach the focused View's page in the order they came, with the modifiers held; what the page used is known by
    // the Input System's keys.
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

        GameObject host;
        readonly List<string> log = new List<string>();

        public override void Setup()
        {
            base.Setup();
            WeftKeyboardInput.Locks = () => WeftModifiers.None;
        }

        public override void TearDown()
        {
            if (host)
            {
                Object.DestroyImmediate(host);
            }
            log.Clear();
            WeftKeyboardInput.Restore();
            base.TearDown();
        }

        WeftView Make()
        {
            host = new GameObject("Weft View");
            host.SetActive(false);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(64, 32);
            view.MessageReceived += m =>
            {
                if (m.Name == "log")
                {
                    log.Add(m.Json.Trim('"'));
                }
            };
            view.DiagnosticReceived += d => Assert.Fail($"{d.Kind}: {d.Text}");
            view.Load(WeftPage.FromSource(Script, Css));
            host.SetActive(true);
            return view;
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
    }
}
