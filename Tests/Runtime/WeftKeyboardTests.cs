using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    // The keyboard and the focus: the keyboard's reading (given keys by the tests in place of the devices) at the page of
    // the View with the focus, what the page used (KeyUsed), the focus a press gives and takes and the focus moving
    // between Views, repeats at the system's rate, modifiers and lock keys, the window's focus, and the keys the game gives
    // a View that does not read the keyboard.
    public sealed class WeftKeyboardTests
    {
        // Two buttons and a scroll container; the page logs its keyboard, focus and scroll events.
        const string Script =
            "const root = document.getElementById('root');" +
            "const log = s => weft.engine.send('log', s);" +
            "for (const id of ['b1', 'b2']) {" +
            "  const b = root.appendChild(document.createElement('button'));" +
            "  b.className = id;" +
            "  for (const type of ['focus', 'blur', 'click']) b.addEventListener(type, () => log(type + ':' + id));" +
            "}" +
            "const s = root.appendChild(document.createElement('div'));" +
            "s.className = 's';" +
            "s.appendChild(document.createElement('div')).className = 'tall';" +
            "s.addEventListener('scroll', () => log('scroll:' + s.scrollTop));" +
            "document.addEventListener('keydown', e => {" +
            "  log('keydown:' + e.code + ':' + e.key + (e.repeat ? ':repeat' : '') + (e.shiftKey ? ':shift' : ''));" +
            "  if (e.code === 'KeyW') e.preventDefault();" +
            "});" +
            "document.addEventListener('keyup', e => log('keyup:' + e.code));" +
            "document.addEventListener('keypress', e => log('keypress:' + e.key));" +
            "window.addEventListener('focus', () => log('window:focus'));" +
            "window.addEventListener('blur', () => log('window:blur'));";

        const string Css =
            "#root{position:relative;width:100%;height:100%;margin:0}" +
            ".b1{position:absolute;left:0;top:0;width:16px;height:16px}" +
            ".b2{position:absolute;left:16px;top:0;width:16px;height:16px}" +
            ".s{position:absolute;left:32px;top:0;width:32px;height:32px;overflow:auto}" +
            ".tall{height:200px}";

        readonly List<GameObject> hosts = new List<GameObject>();
        readonly Dictionary<WeftView, List<string>> logs = new Dictionary<WeftView, List<string>>();
        double clock;

        [SetUp]
        public void SetUp()
        {
            WeftKeyboardInput.ReadDevices = false;
            WeftKeyboardInput.Clock = () => clock;
            WeftKeyboardInput.Locks = () => WeftModifiers.None;
            clock = 0;
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
            WeftKeyboardInput.Restore();
        }

        WeftView Make(bool keyboard = true)
        {
            var host = new GameObject("Weft View");
            host.SetActive(false);
            hosts.Add(host);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(64, 32);
            view.Keyboard = keyboard;
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
            var page = WeftPage.FromSource(Script, Css);
            page.ForgivingCss = true;
            view.Load(page);
            host.SetActive(true);
            return view;
        }

        // The page's events since the last call.
        List<string> Log(WeftView view)
        {
            var log = new List<string>(logs[view]);
            logs[view].Clear();
            return log;
        }

        static void Down(string code)
        {
            WeftKeyboardInput.Enqueue(true, WeftKeys.IndexOf(code));
        }

        static void Up(string code)
        {
            WeftKeyboardInput.Enqueue(false, WeftKeys.IndexOf(code));
        }

        static void Press(string code, string text = null)
        {
            Down(code);
            if (text != null)
            {
                WeftKeyboardInput.Enqueue(text);
            }
            Up(code);
        }

        static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; ++i)
            {
                yield return null;
            }
        }

        static void Press(WeftView view, Vector2 at)
        {
            view.Pointer(WeftPointerKind.Move, at);
            view.Pointer(WeftPointerKind.Down, at, WeftButtons.Left);
            view.Pointer(WeftPointerKind.Up, at);
        }

        [Test]
        public void KeysAndTheirCodes()
        {
            Assert.That(WeftKeys.Code(KeyCode.Return), Is.EqualTo("Enter"));
            Assert.That(WeftKeys.Code(KeyCode.A), Is.EqualTo("KeyA"));
            Assert.That(WeftKeys.Code(KeyCode.Alpha1), Is.EqualTo("Digit1"));
            Assert.That(WeftKeys.Code(KeyCode.KeypadMinus), Is.EqualTo("NumpadSubtract"));
            Assert.That(WeftKeys.Code(KeyCode.LeftMeta), Is.EqualTo("MetaLeft"));
            Assert.That(WeftKeys.Code(KeyCode.F15), Is.EqualTo("F15"));
            Assert.That(WeftKeys.Code(KeyCode.Mouse0), Is.Null, "a mouse button is no key");
            Assert.That(WeftKeys.KeyValue("Enter"), Is.EqualTo("Enter"));
            Assert.That(WeftKeys.KeyValue("Numpad1"), Is.EqualTo("End"), "Num Lock off");
            Assert.That(WeftKeys.KeyValue("Numpad1", WeftModifiers.NumLock), Is.EqualTo("1"));
            Assert.That(WeftKeys.KeyValue("NoSuchKey"), Is.EqualTo("Unidentified"));
        }

        [UnityTest]
        public IEnumerator TabMovesTheFocusAndEnterClicks()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            Press("Tab");
            yield return null;
            Assert.That(view.KeyUsed("Tab"), "Tab moved the focus: the page used it");
            Assert.That(view.KeyUsed(KeyCode.Tab));
            Press("Enter");
            yield return null;
            Assert.That(view.KeyUsed(KeyCode.Return));
            Assert.That(Log(view), Is.EqualTo(new[]
            {
                "window:focus", "keydown:Tab:Tab", "focus:b1", "keyup:Tab", "keydown:Enter:Enter", "keypress:Enter", "click:b1",
                "keyup:Enter",
            }));
            yield return null;
            Assert.That(view.KeyUsed("Tab"), Is.False, "only in the frame it was used in");
        }

        [UnityTest]
        public IEnumerator KeysThePageLeavesAreTheGames()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            var a = WeftKeys.KeyValue("KeyA");
            Press("KeyA", a);
            yield return null;
            Assert.That(view.KeyUsed("KeyA"), Is.False, "a letter the page only heard");
            var w = WeftKeys.KeyValue("KeyW");
            Press("KeyW", w);
            yield return null;
            Assert.That(view.KeyUsed(KeyCode.W), "a key the page prevented");
            Assert.That(Log(view), Is.EqualTo(new[]
            {
                "window:focus", $"keydown:KeyA:{a}", $"keypress:{a}", "keyup:KeyA", $"keydown:KeyW:{w}", "keyup:KeyW",
            }), "the prevented key's text is dropped");
        }

        [UnityTest]
        public IEnumerator APressGivesAndTakesTheFocus()
        {
            var view = Make();
            yield return Frames(2);
            Assert.That(view.HasFocus, Is.False, "a View starts without the focus");
            Press(view, new Vector2(8, 8));
            Assert.That(view.HasFocus, "a press on the page's elements gives it");
            Assert.That(WeftView.FocusedView, Is.SameAs(view));
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[] {"window:focus", "focus:b1", "click:b1"}));
            Press(view, new Vector2(8, 24));
            Assert.That(view.HasFocus, Is.False, "a press beside them is the game's, and takes it");
            Assert.That(WeftView.FocusedView, Is.Null);
            Press("Tab");
            yield return null;
            Assert.That(view.KeyUsed("Tab"), Is.False, "keys are the game's");
            Assert.That(Log(view), Is.EqualTo(new[] {"blur:b1", "window:blur"}));
        }

        [UnityTest]
        public IEnumerator TheFocusMovesBetweenViews()
        {
            var first = Make();
            var second = Make();
            yield return Frames(2);
            first.Focus();
            Press("Tab");
            yield return null;
            second.Focus();
            Assert.That(first.HasFocus, Is.False);
            Assert.That(second.HasFocus);
            Press("Tab");
            yield return null;
            Assert.That(Log(first), Is.EqualTo(new[] {"window:focus", "keydown:Tab:Tab", "focus:b1", "keyup:Tab", "blur:b1", "window:blur"}));
            Assert.That(Log(second), Is.EqualTo(new[] {"window:focus", "keydown:Tab:Tab", "focus:b1", "keyup:Tab"}));
        }

        [UnityTest]
        public IEnumerator KeysRepeatAtTheSystemsRate()
        {
            var view = Make();
            yield return Frames(2);
            WeftKeyboardInput.RepeatDelay = 0.5;
            WeftKeyboardInput.RepeatInterval = 0.1;
            view.Focus();
            clock = 10;
            Down("ArrowDown");
            yield return null;
            // The delay, then one a tenth of a second; a keyup ends it.
            foreach (var time in new[] {10.45, 10.5, 10.6, 10.75})
            {
                clock = time;
                yield return null;
            }
            Up("ArrowDown");
            clock = 11;
            yield return null;
            clock = 12;
            yield return null;
            Assert.That(Log(view).FindAll(s => s.StartsWith("key")), Is.EqualTo(new[]
            {
                "keydown:ArrowDown:ArrowDown", "keydown:ArrowDown:ArrowDown:repeat", "keydown:ArrowDown:ArrowDown:repeat",
                "keydown:ArrowDown:ArrowDown:repeat", "keyup:ArrowDown",
            }));
        }

        [UnityTest]
        public IEnumerator KeysScrollWhereThePressWas()
        {
            var view = Make();
            yield return Frames(2);
            Press(view, new Vector2(48, 16));
            yield return null;
            Press("ArrowDown");
            yield return null;
            Assert.That(view.KeyUsed("ArrowDown"), "the scroll container the press was in scrolled a line");
            // Space scrolls at its keypress: the text is the key's.
            Press("Space", " ");
            yield return null;
            Assert.That(view.KeyUsed("Space"), "Space's text scrolled a page");
            yield return Frames(2);
            var log = Log(view);
            Assert.That(log, Does.Contain("scroll:40").And.Contain("scroll:68"), string.Join(",", log));
        }

        [UnityTest]
        public IEnumerator ModifiersAndLockKeys()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            var shifted = WeftKeys.KeyValue("KeyA", WeftModifiers.Shift);
            Down("ShiftLeft");
            Press("KeyA");
            Up("ShiftLeft");
            yield return null;
            WeftKeyboardInput.Locks = () => WeftModifiers.CapsLock;
            var capital = WeftKeys.KeyValue("KeyA", WeftModifiers.CapsLock);
            Press("KeyA");
            yield return null;
            Assert.That(Log(view), Is.EqualTo(new[]
            {
                "window:focus", "keydown:ShiftLeft:Shift:shift", $"keydown:KeyA:{shifted}:shift", "keyup:KeyA", "keyup:ShiftLeft",
                $"keydown:KeyA:{capital}", "keyup:KeyA",
            }));
        }

        [UnityTest]
        public IEnumerator TheWindowsFocus()
        {
            var view = Make();
            yield return Frames(2);
            view.Focus();
            Press("Tab");
            yield return null;
            view.SendMessage("OnApplicationFocus", false);
            yield return null;
            view.SendMessage("OnApplicationFocus", true);
            yield return null;
            Assert.That(view.HasFocus, "the View keeps the game's focus");
            Assert.That(Log(view), Is.EqualTo(new[]
            {
                "window:focus", "keydown:Tab:Tab", "focus:b1", "keyup:Tab", "blur:b1", "window:blur", "window:focus", "focus:b1",
            }));
        }

        [UnityTest]
        public IEnumerator TheGameGivesAViewItsKeys()
        {
            var view = Make(keyboard: false);
            yield return Frames(2);
            Press(view, new Vector2(8, 8));
            Assert.That(view.HasFocus, Is.False, "a View that does not read the keyboard takes no focus");
            yield return null;
            view.Focus();
            Press("Tab");
            yield return null;
            Assert.That(view.KeyUsed("Tab"), Is.False, "nor does it get the keyboard's keys");
            Assert.That(view.Key(WeftKeyKind.Down, "Tab"), Is.EqualTo(WeftInputResult.Default));
            Assert.That(view.KeyUsed("Tab"));
            Assert.That(view.Key(WeftKeyKind.Up, "Tab"), Is.EqualTo(WeftInputResult.None));
            Assert.That(view.Key(WeftKeyKind.Down, "KeyX", "x"), Is.EqualTo(WeftInputResult.None));
            Assert.That(view.Text("x"), Is.EqualTo(WeftInputResult.None));
            view.Key(WeftKeyKind.Up, "KeyX", "x");
            Assert.That(view.Key(WeftKeyKind.Down, "KeyW", "w"), Is.EqualTo(WeftInputResult.Prevented));
            Assert.That(view.Text("w"), Is.EqualTo(WeftInputResult.Default), "the text of a key the page prevented is dropped");
            view.Key(WeftKeyKind.Up, "KeyW", "w");
            yield return null;
            // The press focused the button without its focus event: the page had no focus yet (Blink fires it when the
            // window gets the focus).
            Assert.That(Log(view), Is.EqualTo(new[]
            {
                "click:b1", "window:focus", "focus:b1", "keydown:Tab:Tab", "blur:b1", "focus:b2", "keyup:Tab", "keydown:KeyX:x",
                "keypress:x", "keyup:KeyX", "keydown:KeyW:w", "keyup:KeyW",
            }));
        }
    }
}
