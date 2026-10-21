using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Weft
{
    /// <summary>
    /// A page for a View: its scripts in order, its stylesheet and keyframes, the selector of its root, and the base its
    /// relative URLs (url() images) resolve against: the path of its stylesheet.
    /// </summary>
    public sealed class WeftPage
    {
        public readonly List<(string Name, string Source)> Scripts = new List<(string Name, string Source)>();
        public string Stylesheet = string.Empty;
        public string Keyframes = string.Empty;
        /// <summary>"#id" or a tag name; empty is "#root", the element the document's body starts with.</summary>
        public string Root = string.Empty;
        /// <summary>CRPG Framework's window.ue.sendEvent, __ue_dispatch and __vm_update over weft.engine.</summary>
        public bool CrpgBridge;
        /// <summary>
        /// The stylesheet and keyframes taken as browsers take CSS (@media, @supports, cascade layers, nesting, more
        /// selectors and values), what the scene cannot draw dropped with a diagnostic instead of failing the page, and
        /// a stylesheet's bounds those of an app's (8 MiB, 16384 rules) instead of 64 KiB and 256 rules.
        /// </summary>
        public bool ForgivingCss;
        /// <summary>The web platform pages written for browsers expect: console, localStorage, dataset, matchMedia (the focus and
        /// the keyboard are every page's).</summary>
        public bool WebPlatform;
        /// <summary>An absolute path (a file's: what follows its last '/' is no part of it); empty: url() images find nothing.</summary>
        public string Base = string.Empty;

        /// <summary>A page of one script and a stylesheet; base, when given, is where url() images resolve from.</summary>
        public static WeftPage FromSource(string script, string stylesheet = "", string baseDirectory = "")
        {
            var page = new WeftPage { Stylesheet = stylesheet ?? string.Empty };
            page.Scripts.Add(("page.js", script ?? string.Empty));
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                page.Base = Normalize(Path.Combine(baseDirectory, "page.css"));
            }
            return page;
        }

        /// <summary>
        /// The page of a folder (relative to StreamingAssets, or absolute): its page.json names the scripts, the
        /// stylesheet and keyframes files, the root, and whether the page uses the CRPG bridge, forgiving CSS
        /// ("forgivingCss") and the web platform ("webPlatform"); without one the folder's page.js, page.css and
        /// keyframes.css are the page.
        /// </summary>
        public static WeftPage FromDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("weft: a page needs a folder", nameof(directory));
            }
            if (!Path.IsPathRooted(directory))
            {
                directory = Path.Combine(Application.streamingAssetsPath, directory);
            }
            var page = new WeftPage();
            var manifestPath = Path.Combine(directory, "page.json");
            string stylesheet;
            if (File.Exists(manifestPath))
            {
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                {
                    throw new FormatException($"weft: {manifestPath} is not a JSON object");
                }
                foreach (var script in manifest.scripts ?? Array.Empty<string>())
                {
                    page.Scripts.Add((script, Read(directory, script)));
                }
                stylesheet = string.IsNullOrEmpty(manifest.stylesheet) ? "page.css" : manifest.stylesheet;
                if (!string.IsNullOrEmpty(manifest.stylesheet))
                {
                    page.Stylesheet = Read(directory, manifest.stylesheet);
                }
                if (!string.IsNullOrEmpty(manifest.keyframes))
                {
                    page.Keyframes = Read(directory, manifest.keyframes);
                }
                page.Root = manifest.root ?? string.Empty;
                page.CrpgBridge = manifest.crpgBridge;
                page.ForgivingCss = manifest.forgivingCss;
                page.WebPlatform = manifest.webPlatform;
            }
            else
            {
                if (!File.Exists(Path.Combine(directory, "page.js")))
                {
                    throw new FileNotFoundException($"weft: {directory} has neither page.json nor page.js");
                }
                page.Scripts.Add(("page.js", Read(directory, "page.js")));
                page.Stylesheet = ReadIfAny(directory, "page.css");
                page.Keyframes = ReadIfAny(directory, "keyframes.css");
                stylesheet = "page.css";
            }
            if (page.Scripts.Count == 0)
            {
                throw new FormatException($"weft: {manifestPath} names no script");
            }
            page.Base = Normalize(Path.Combine(directory, stylesheet));
            return page;
        }

        static string Read(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"weft: {path} is missing", path);
            }
            return File.ReadAllText(path);
        }

        static string ReadIfAny(string directory, string name)
        {
            var path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }

        static string Normalize(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }

        [Serializable]
        sealed class Manifest
        {
            public string[] scripts = null;
            public string stylesheet = null;
            public string keyframes = null;
            public string root = null;
            public bool crpgBridge = false;
            public bool forgivingCss = false;
            public bool webPlatform = false;
        }
    }
}
