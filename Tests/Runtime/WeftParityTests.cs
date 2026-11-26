using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Weft.Tests
{
    /// <summary>
    /// Feature parity tests matching weft-unreal: DevTools HTTP/WS serving, live View inspection &amp; formatting,
    /// console diagnostic severities, localStorage persistence, PointerRouting.Cursor, font manifests with
    /// unicodeRange, and shader effect manifest registration.
    /// </summary>
    [PrebuildSetup(typeof(LinearColourSpace))]
    public sealed class WeftParityTests
    {
        const int Width = 64;
        const int Height = 32;
        const string Root = "#root{position:relative;width:100%;height:100%;margin:0}";

        readonly List<GameObject> hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            WeftDebugTools.ShowOverlay(false);
            WeftDebugTools.StopDevTools();
            foreach (var host in hosts)
            {
                if (host)
                {
                    UnityEngine.Object.DestroyImmediate(host);
                }
            }
            hosts.Clear();
        }

        WeftView Make(string script, string css, string folder = "ParityPage", bool webPlatform = false, bool forgivingCss = true)
        {
            var host = new GameObject($"Weft_{folder}");
            host.SetActive(false);
            hosts.Add(host);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(Width, Height);
            var page = WeftPage.FromSource(script, Root + css, Path.Combine(Application.temporaryCachePath, folder));
            page.WebPlatform = webPlatform;
            page.ForgivingCss = forgivingCss;
            view.Load(page);
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

        static string HttpGet(int port, string path)
        {
            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = 3000;
                client.SendTimeout = 3000;
                client.Connect("127.0.0.1", port);
                using var stream = client.GetStream();
                var req = Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nConnection: close\r\n\r\n");
                stream.Write(req, 0, req.Length);
                using var ms = new MemoryStream();
                var buf = new byte[4096];
                int read;
                while ((read = stream.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, read);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        [UnityTest]
        public IEnumerator DevToolsBuildAndServe()
        {
            Assert.That(WeftRuntime.HasDevTools, Is.True, "Editor/PlayMode loads the Dev weft.dll");
            var view = Make(
                "const r = document.getElementById('root');" +
                "for (let i = 0; i < 3; ++i) r.appendChild(document.createElement('div')).className = 'b';",
                ".b{width:8px;height:8px}",
                "DevToolsServe");
            yield return Frames(3);

            Assert.That(WeftDebugTools.StartDevTools(string.Empty, out var err), Is.True, err);
            // Allow one frame so the running View advances and registers its target with the newly started DevTools server.
            yield return Frames(2);
            var port = WeftDebugTools.DevToolsPort;
            Assert.That(port, Is.GreaterThan(0));
            Assert.That(WeftDebugTools.DevToolsAddress, Is.EqualTo($"127.0.0.1:{port}"));

            var views = new List<WeftViewDebugInfo>();
            WeftDebugTools.GetViews(views);
            var info = views.Find(v => v.View == view);
            Assert.That(info.View, Is.EqualTo(view));
            Assert.That(info.Page, Is.EqualTo("DevToolsServe"));
            Assert.That(info.Elements, Is.GreaterThanOrEqualTo(4));
            Assert.That(info.DevToolsUrl, Is.EqualTo($"devtools://devtools/bundled/devtools_app.html?ws=127.0.0.1:{port}/devtools/page/{info.Target}"));

            var list = HttpGet(port, "/json/list");
            Assert.That(list, Does.StartWith("HTTP/1.1 200"));
            Assert.That(list, Does.Contain($"/devtools/page/{info.Target}\""));
            Assert.That(HttpGet(port, "/json/version"), Does.Contain("\"Browser\":\"Weft/"));

            Assert.That(WeftDebugTools.StartDevTools("0", out var alreadyErr), Is.False);
            Assert.That(alreadyErr, Does.Contain("already"));
            Assert.That(WeftDebugTools.StartDevTools("10.0.0.1:9230", out var badHostErr), Is.False);
            Assert.That(badHostErr, Does.Contain("is not"));
            Assert.That(WeftDebugTools.StartDevTools("70000", out _), Is.False);

            WeftDebugTools.StopDevTools();
            Assert.That(WeftDebugTools.DevToolsPort, Is.EqualTo(0));
            Assert.That(HttpGet(port, "/json/version"), Is.Empty);

            WeftDebugTools.GetViews(views);
            info = views.Find(v => v.View == view);
            Assert.That(info.DevToolsUrl, Is.Empty);
        }

        [UnityTest]
        public IEnumerator DebugViewsTableAndConsoleSeverity()
        {
            LogAssert.Expect(LogType.Error, new Regex("boom"));
            var diagnostics = new List<WeftDiagnostic>();
            var host = new GameObject("Weft_Console");
            host.SetActive(false);
            hosts.Add(host);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(Width, Height);
            view.DiagnosticReceived += diagnostics.Add;
            var page = WeftPage.FromSource(
                "console.log('info-line'); console.warn('warn-line'); console.error('boom');" +
                "document.getElementById('root').appendChild(document.createElement('div')).className = 'b';",
                Root + ".b{width:8px;height:8px;column-count:2}",
                Path.Combine(Application.temporaryCachePath, "ConsolePage"));
            page.WebPlatform = true;
            page.ForgivingCss = true;
            view.Load(page);
            host.SetActive(true);
            yield return Frames(3);

            Assert.That(diagnostics.Exists(d => d.Kind == WeftDiagnosticKind.Console && d.Text.Contains("info-line")));
            Assert.That(diagnostics.Exists(d => d.Kind == WeftDiagnosticKind.ConsoleWarning && d.Text.Contains("warn-line")));
            Assert.That(diagnostics.Exists(d => d.Kind == WeftDiagnosticKind.ConsoleError && d.Text.Contains("boom")));
            Assert.That(view.PageName, Is.EqualTo("ConsolePage"));
            Assert.That(view.ErrorCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(view.WarningCount, Is.GreaterThanOrEqualTo(2));

            var views = new List<WeftViewDebugInfo>();
            WeftDebugTools.GetViews(views);
            var lines = WeftDebugTools.FormatViews(views);
            Assert.That(lines.Count, Is.EqualTo(views.Count + 1));
            Assert.That(lines[0], Does.StartWith("View").And.Contain("Elements"));
            Assert.That(lines.Exists(l => l.Contains("ConsolePage")), Is.True);
        }

        [UnityTest]
        public IEnumerator LocalStorageRoundTripsAndPersistsAcrossReload()
        {
            var host = new GameObject("Weft_Storage");
            host.SetActive(false);
            hosts.Add(host);
            var view = host.AddComponent<WeftView>();
            view.FullScreen = false;
            view.Size = new Vector2Int(Width, Height);
            host.SetActive(true);

            Assert.That(view.SetStorageItem("profile", "hero"), Is.True);
            var page = WeftPage.FromSource(
                "const p = localStorage.getItem('profile');" +
                "localStorage.setItem('rank', p + ':S');",
                Root);
            page.WebPlatform = true;
            view.Load(page);
            yield return Frames(2);

            ulong revision = 0;
            var items = new Dictionary<string, string>();
            Assert.That(view.GetStorage(ref revision, items), Is.True);
            Assert.That(revision, Is.GreaterThan(0UL));
            Assert.That(items["profile"], Is.EqualTo("hero"));
            Assert.That(items["rank"], Is.EqualTo("hero:S"));

            // Unchanged revision returns false.
            Assert.That(view.GetStorage(ref revision, items), Is.False);

            // Persists across page reload on the same View.
            var messages = new List<WeftMessage>();
            view.MessageReceived += messages.Add;
            var page2 = WeftPage.FromSource("weft.engine.send('got', localStorage.getItem('rank'));", Root);
            page2.WebPlatform = true;
            view.Load(page2);
            yield return Frames(2);
            Assert.That(messages.Exists(m => m.Name == "got" && m.Json == "\"hero:S\""), Is.True);

            Assert.That(view.ClearStorage(), Is.True);
            Assert.That(view.GetStorage(ref revision, items), Is.True);
            Assert.That(items, Is.Empty);
        }

        [UnityTest]
        public IEnumerator PointerRoutingCursorVsElement()
        {
            var view = Make(
                "const r = document.getElementById('root');" +
                "const p = r.appendChild(document.createElement('div')); p.className = 'panel';" +
                "const b = r.appendChild(document.createElement('div')); b.className = 'btn';",
                ".panel{position:absolute;left:0;top:0;width:32px;height:32px;background:#333}" +
                ".btn{position:absolute;left:32px;top:0;width:32px;height:32px;background:#09f;cursor:pointer}");
            yield return Frames(3);

            var overPanel = view.HitTest(new Vector2(16, 16));
            var overButton = view.HitTest(new Vector2(48, 16));
            Assert.That(overPanel.Element, Is.True);
            Assert.That(overPanel.Interactive, Is.False);
            Assert.That(overButton.Element, Is.True);
            Assert.That(overButton.Interactive, Is.True);

            view.PointerRouting = WeftPointerRouting.Cursor;
            Assert.That(view.PointerRouting, Is.EqualTo(WeftPointerRouting.Cursor));
        }

        [UnityTest]
        public IEnumerator FontManifestWithUnicodeRangeAndEffectsManifestValidation()
        {
            // Restart runtime cleanly so fonts can be added before the first page loads.
            WeftRuntime.Stop();
            WeftRuntime.Start(systemFonts: true);

            var dir = Path.Combine(Application.temporaryCachePath, "WeftManifests");
            Directory.CreateDirectory(dir);
            var systemFont = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
            if (File.Exists(systemFont))
            {
                var localFont = Path.Combine(dir, "ui.ttf");
                File.Copy(systemFont, localFont, true);
                var fontsJson = Path.Combine(dir, "fonts.json");
                File.WriteAllText(fontsJson,
                    "{\"faces\":[{\"family\":\"GameUI\",\"path\":\"ui.ttf\",\"weight\":400,\"unicodeRange\":\"U+0020-007F\"}],\"fallback\":[\"GameUI\"]}");
                Assert.That(WeftRuntime.AddFonts(fontsJson), Is.EqualTo(1));
                // Calling AddFonts twice for the same manifest skips cleanly.
                Assert.That(WeftRuntime.AddFonts(fontsJson), Is.EqualTo(0));
            }

            // Effect manifest registration, deduplication and parameter validation.
            var effectsJson = Path.Combine(dir, "effects.json");
            File.WriteAllText(effectsJson,
                "{\"effects\":[{\"name\":\"valid_stub\",\"bytecode\":\"none\",\"parameters\":[{\"name\":\"amount\",\"type\":\"number\",\"initial\":\"0.5\"}]}]}");
            Assert.That(WeftRuntime.AddEffects(effectsJson), Is.EqualTo(1));
            Assert.That(WeftRuntime.AddEffects(effectsJson), Is.EqualTo(0), "duplicate effect name skipped");

            var badEffectsJson = Path.Combine(dir, "bad_effects.json");
            File.WriteAllText(badEffectsJson,
                "{\"effects\":[{\"name\":\"bad_param\",\"bytecode\":\"none\",\"parameters\":[{\"name\":\"x\",\"type\":\"bogus\",\"initial\":\"0\"}]}]}");
            Assert.That(WeftRuntime.AddEffects(badEffectsJson), Is.EqualTo(0), "invalid parameter type rejected");
            yield return null;
        }
    }
}
