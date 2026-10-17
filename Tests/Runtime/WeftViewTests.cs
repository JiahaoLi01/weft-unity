using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    // A View in a play session on the graphics API Unity runs (-force-d3d11, -force-d3d12, -force-vulkan): its pixels
    // read back from its texture, its messages, events, view models and calls, pointer input and hit tests, url() images.
    public sealed class WeftViewTests
    {
        const int Width = 64;
        const int Height = 32;
        const string Root = "#root{position:relative;width:100%;height:100%;margin:0}";

        GameObject host;
        WeftRenderStats before;
        readonly List<WeftMessage> messages = new List<WeftMessage>();
        readonly List<WeftDiagnostic> diagnostics = new List<WeftDiagnostic>();

        [TearDown]
        public void TearDown()
        {
            if (host)
            {
                Object.Destroy(host);
            }
            messages.Clear();
            diagnostics.Clear();
        }

        WeftView Make(string script, string css, string baseDirectory = "")
        {
            before = WeftRuntime.RenderStats;
            host = new GameObject("Weft View");
            host.SetActive(false);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(Width, Height);
            view.MessageReceived += messages.Add;
            view.DiagnosticReceived += diagnostics.Add;
            view.Load(WeftPage.FromSource(script, Root + css, baseDirectory));
            host.SetActive(true);
            return view;
        }

        static IEnumerator Frames(int count)
        {
            for (var i = 0; i < count; ++i)
            {
                yield return null;
            }
        }

        // The texture's texels by row in memory order (Texture2D row y is the render texture's row y).
        static Color32[] Read(RenderTexture texture)
        {
            var previous = RenderTexture.active;
            RenderTexture.active = texture;
            var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
            copy.Apply();
            RenderTexture.active = previous;
            var pixels = copy.GetPixels32();
            Object.Destroy(copy);
            return pixels;
        }

        static Color32 At(Color32[] pixels, int width, int x, int row)
        {
            return pixels[row * width + x];
        }

        static void Near(Color32 expected, Color32 actual, int tolerance, string what)
        {
            Assert.That(Mathf.Abs(expected.r - actual.r) <= tolerance && Mathf.Abs(expected.g - actual.g) <= tolerance &&
                        Mathf.Abs(expected.b - actual.b) <= tolerance && Mathf.Abs(expected.a - actual.a) <= tolerance,
                $"{what}: expected {expected}, got {actual}");
        }

        void NoDiagnostics()
        {
            Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.ConvertAll(d => $"{d.Kind}: {d.Text}")));
        }

        // The View's draws since it was made, on Unity's API, none failed.
        void Drawn()
        {
            var stats = WeftRuntime.RenderStats;
            Assert.That(stats.Api, Is.EqualTo(SystemInfo.graphicsDeviceType), "the API weft draws with");
            Assert.That(stats.Failures, Is.EqualTo(before.Failures), stats.LastFailure);
            Assert.That(stats.Draws, Is.GreaterThan(before.Draws));
        }

        [UnityTest]
        public IEnumerator DrawsThePageTopRowFirst()
        {
            var view = Make("document.getElementById('root').appendChild(document.createElement('div')).className = 'a';",
                ".a{position:absolute;left:0;top:0;width:32px;height:16px;background-color:#ff0000}");
            yield return Frames(4);
            NoDiagnostics();
            Drawn();
            var pixels = Read(view.Texture);
            Near(new Color32(255, 0, 0, 255), At(pixels, Width, 8, 4), 0, "the box at the page's top left");
            Near(new Color32(0, 0, 0, 0), At(pixels, Width, 48, 4), 0, "the page's top right, cleared");
            Near(new Color32(0, 0, 0, 0), At(pixels, Width, 8, 27), 0, "the page's bottom left, cleared");
        }

        [UnityTest]
        public IEnumerator CompositesUprightAndDecodes()
        {
            var view = Make(
                "const root = document.getElementById('root');" +
                "for (const name of ['a', 'b']) root.appendChild(document.createElement('div')).className = name;",
                ".a{position:absolute;left:0;top:0;width:32px;height:16px;background-color:rgb(128,128,128)}" +
                ".b{position:absolute;left:32px;top:0;width:32px;height:16px;background-color:rgba(255,255,255,0.5)}");
            yield return Frames(4);
            NoDiagnostics();
            var target = new RenderTexture(Width, Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            try
            {
                foreach (var decode in new[] {false, true})
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    GL.Clear(false, true, Color.clear);
                    RenderTexture.active = previous;
                    WeftComposite.Blit(view.Texture, target, decode);
                    var pixels = Read(target);
                    // Upright: the page's top is Unity's top (the last rows).
                    var top = Height - 1 - 4;
                    Near(decode ? new Color32(55, 55, 55, 255) : new Color32(128, 128, 128, 255), At(pixels, Width, 8, top), 1,
                        decode ? "grey decoded to linear" : "grey as weft encodes it");
                    Near(decode ? new Color32(128, 128, 128, 128) : new Color32(128, 128, 128, 128), At(pixels, Width, 48, top), 1,
                        "white at half opacity, premultiplied");
                    Near(new Color32(0, 0, 0, 0), At(pixels, Width, 8, 4), 0, "the page's bottom");
                }
            }
            finally
            {
                target.Release();
                Object.Destroy(target);
            }
        }

        [UnityTest]
        public IEnumerator MessagesEventsModelsAndCalls()
        {
            var view = Make(
                "weft.engine.on('ping', v => weft.engine.send('pong', {n: v.n + 1}));" +
                "weft.engine.model('player').subscribe(v => weft.engine.send('model', v));" +
                "weft.engine.on('ask', () => weft.engine.call('add', {a: 1, b: 2}).then(r => weft.engine.send('sum', r)," +
                " e => weft.engine.send('refused', e.message)));",
                string.Empty);
            yield return Frames(2);
            view.MessageReceived += m =>
            {
                if (m.Name == "add")
                {
                    Assert.That(m.IsCall);
                    Assert.That(m.Json, Is.EqualTo("{\"a\":1,\"b\":2}"));
                    view.CompleteCall(m.CallId, true, "3");
                }
            };
            Assert.That(view.Emit("ping", "{\"n\":41}"));
            Assert.That(view.SetModel("player", "{\"hp\":7,\"name\":\"兵\"}"));
            Assert.That(view.Emit("ask"));
            yield return Frames(4);
            NoDiagnostics();
            var names = messages.ConvertAll(m => m.Name);
            Assert.That(names, Does.Contain("pong").And.Contain("model").And.Contain("add").And.Contain("sum"), string.Join(",", names));
            Assert.That(messages.Find(m => m.Name == "pong").Json, Is.EqualTo("{\"n\":42}"));
            Assert.That(messages.Find(m => m.Name == "model").Json, Is.EqualTo("{\"hp\":7,\"name\":\"兵\"}"));
            Assert.That(messages.Find(m => m.Name == "sum").Json, Is.EqualTo("3"));
            Assert.That(messages.Find(m => m.Name == "pong").IsCall, Is.False);
        }

        [UnityTest]
        public IEnumerator PointerInputAndHitTests()
        {
            var view = Make(
                "const b = document.getElementById('root').appendChild(document.createElement('div'));" +
                "b.className = 'button';" +
                "b.addEventListener('click', e => weft.engine.send('clicked', {x: e.clientX, y: e.clientY}));",
                ".button{position:absolute;left:8px;top:8px;width:16px;height:16px;background-color:#00f;cursor:pointer}");
            yield return Frames(3);
            var over = view.HitTest(new Vector2(16, 16));
            Assert.That(over.Element, "an element under the button's centre");
            Assert.That(over.Cursor, Is.EqualTo("pointer"));
            Assert.That(over.Interactive);
            var off = view.HitTest(new Vector2(48, 16));
            Assert.That(off.Element, Is.False, "the root beside it");
            view.Pointer(WeftPointerKind.Move, new Vector2(16, 16));
            view.Pointer(WeftPointerKind.Down, new Vector2(16, 16), WeftButtons.Left);
            view.Pointer(WeftPointerKind.Up, new Vector2(16, 16));
            yield return Frames(2);
            NoDiagnostics();
            var clicked = messages.Find(m => m.Name == "clicked");
            Assert.That(clicked.Name, Is.EqualTo("clicked"), "a click on the button");
            Assert.That(clicked.Json, Is.EqualTo("{\"x\":16,\"y\":16}"));
        }

        [UnityTest]
        public IEnumerator ImagesByUrl()
        {
            var directory = Path.Combine(Application.temporaryCachePath, "WeftImages");
            Directory.CreateDirectory(directory);
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            image.SetPixels32(new[] {new Color32(0, 255, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 255, 0, 255)});
            image.Apply();
            File.WriteAllBytes(Path.Combine(directory, "green.png"), image.EncodeToPNG());
            Object.Destroy(image);
            var view = Make(
                "const root = document.getElementById('root');" +
                "for (const name of ['found', 'missing']) root.appendChild(document.createElement('div')).className = name;",
                ".found{position:absolute;left:0;top:0;width:16px;height:16px;background-image:url(green.png);background-size:100% 100%}" +
                ".missing{position:absolute;left:32px;top:0;width:16px;height:16px;background-image:url(missing.png)}",
                directory);
            yield return Frames(4);
            Drawn();
            var pixels = Read(view.Texture);
            Near(new Color32(0, 255, 0, 255), At(pixels, Width, 8, 8), 0, "the image the page names");
            Assert.That(diagnostics.Exists(d => d.Kind == WeftDiagnosticKind.Scene && d.Text.Contains("missing.png")),
                "a diagnostic of the missing image: " + string.Join("\n", diagnostics.ConvertAll(d => d.Text)));
        }

        [UnityTest]
        public IEnumerator ScriptErrorsAreDiagnostics()
        {
            Make("throw new Error('kaput');", string.Empty);
            yield return Frames(2);
            Assert.That(diagnostics.Exists(d => d.Kind == WeftDiagnosticKind.Script && d.Text.Contains("kaput")),
                string.Join("\n", diagnostics.ConvertAll(d => d.Text)));
        }

        [UnityTest]
        public IEnumerator FullScreenFollowsTheScreen()
        {
            host = new GameObject("Weft View");
            host.SetActive(false);
            var view = host.AddComponent<WeftView>();
            view.Scale = 2;
            view.DiagnosticReceived += diagnostics.Add;
            view.Load(WeftPage.FromSource(string.Empty, Root));
            host.SetActive(true);
            yield return Frames(3);
            NoDiagnostics();
            var screen = new Vector2Int(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height));
            Assert.That(view.Texture.width, Is.EqualTo(screen.x), "the texture is the screen's size");
            Assert.That(view.Texture.height, Is.EqualTo(screen.y));
            Assert.That(view.LogicalSize, Is.EqualTo((Vector2)screen / 2), "the page's size in CSS px at scale 2");
            Assert.That(view.PointerOverPage, Is.False, "an empty page takes no pointer");
        }

        [UnityTest]
        public IEnumerator RuntimeStartsAgain()
        {
            Make("document.getElementById('root').appendChild(document.createElement('div')).className = 'a';",
                ".a{position:absolute;left:0;top:0;width:64px;height:32px;background-color:#ff0000}");
            yield return Frames(3);
            Object.Destroy(host);
            yield return null;
            WeftRuntime.Stop();
            Assert.That(WeftRuntime.IsRunning, Is.False);
            var second = Make("document.getElementById('root').appendChild(document.createElement('div')).className = 'a';",
                ".a{position:absolute;left:0;top:0;width:64px;height:32px;background-color:#0000ff}");
            Assert.That(WeftRuntime.IsRunning);
            yield return Frames(4);
            NoDiagnostics();
            Drawn();
            Near(new Color32(0, 0, 255, 255), At(Read(second.Texture), Width, 8, 8), 0, "the second runtime's page");
        }
    }
}
