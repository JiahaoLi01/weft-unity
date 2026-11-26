using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Weft
{
    /// <summary>A weft call that failed: weft's result and what it was doing.</summary>
    public sealed class WeftException : Exception
    {
        public WeftException(string what, string result) : base($"weft: {what} failed ({result})")
        {
        }
    }

    /// <summary>What Unity's render thread has drawn with weft.</summary>
    public struct WeftRenderStats
    {
        /// <summary>The graphics API weft draws with (Direct3D11, Direct3D12, Vulkan; Null before Unity's device starts).</summary>
        public UnityEngine.Rendering.GraphicsDeviceType Api;
        public ulong Draws;
        public ulong Failures;
        /// <summary>Why the last failed draw failed, empty when none did.</summary>
        public string LastFailure;
    }

    /// <summary>
    /// The weft runtime of the process: one for the Views of a play session. It runs the pages on a thread of its own
    /// (weft's pages need a deeper stack than Unity's main thread has); Views start it when nothing has, and it stops
    /// when the play session ends (leaving play mode, a domain reload, quitting), so the next session starts on a new one.
    /// Fonts are added after <see cref="Start"/> and before the first page loads.
    /// </summary>
    public static unsafe class WeftRuntime
    {
        static bool hooked;
        static readonly HashSet<string> fontManifests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> registeredEffects = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The weft commit weft.dll was built from.</summary>
        public static string Build => Marshal.PtrToStringAnsi(WeftNative.weft_unity_build()) ?? string.Empty;

        /// <summary>Whether the loaded weft.dll includes developer tools (Dev build vs Release build).</summary>
        public static bool HasDevTools
        {
            get
            {
                var info = new WeftNative.BuildInfo { StructSize = (uint)sizeof(WeftNative.BuildInfo) };
                return WeftNative.weft_get_build_info(&info) == WeftNative.Result.Ok &&
                       (info.Flags & WeftNative.BuildDevTools) != 0;
            }
        }

        public static bool IsRunning => WeftNative.weft_unity_runtime() != IntPtr.Zero;

        internal static IntPtr Native => WeftNative.weft_unity_runtime();

        /// <summary>Starts the runtime; systemFonts gives the pages Windows' installed fonts and their generic families.</summary>
        public static void Start(bool systemFonts = true, int maxViews = 0)
        {
            Hook();
            var result = WeftNative.weft_unity_start(systemFonts ? WeftNative.RuntimeSystemFonts : 0, (uint)Math.Max(0, maxViews));
            if (result == WeftNative.Result.Busy)
            {
                throw new InvalidOperationException("weft: the runtime runs already");
            }
            Check(result, "starting the runtime");
            fontManifests.Clear();
            registeredEffects.Clear();
            WeftDebugTools.OnRuntimeStarted();
        }

        /// <summary>Starts the runtime with the defaults when it is not running.</summary>
        public static void EnsureStarted()
        {
            if (!IsRunning)
            {
                Start();
            }
        }

        /// <summary>Destroys the runtime and its Views (WeftView components stop drawing until they are enabled again).</summary>
        public static void Stop()
        {
            if (IsRunning)
            {
                WeftDebugTools.OnRuntimeEnding();
                WeftNative.weft_unity_stop();
            }
            fontManifests.Clear();
            registeredEffects.Clear();
        }

        /// <summary>
        /// A face of a font file (a collection's by faceIndex) for CSS to match by family, weight and style.
        /// Optional <paramref name="unicodeRange"/> restricts code points as CSS @font-face unicode-range does.
        /// </summary>
        public static void AddFont(string family, string path, int weight = 400, bool italic = false, int faceIndex = 0,
            string unicodeRange = null)
        {
            var familyBytes = Utf8(family);
            var pathBytes = Utf8(Path.GetFullPath(path));
            var rangeBytes = Utf8(unicodeRange);
            fixed (byte* f = familyBytes)
            fixed (byte* p = pathBytes)
            fixed (byte* r = rangeBytes)
            {
                var face = new WeftNative.FontFace
                {
                    StructSize = (uint)sizeof(WeftNative.FontFace),
                    Weight = (uint)Mathf.Clamp(weight, 1, 1000),
                    Italic = italic ? 1u : 0u,
                    FaceIndex = (uint)Math.Max(0, faceIndex),
                    Family = f,
                    FamilySize = (UIntPtr)familyBytes.Length,
                    Path = p,
                    PathSize = (UIntPtr)pathBytes.Length,
                    UnicodeRange = rangeBytes.Length > 0 ? r : null,
                    UnicodeRangeSize = (UIntPtr)rangeBytes.Length,
                };
                Check(WeftNative.weft_unity_add_font(&face), $"adding the font {path}");
            }
        }

        /// <summary>
        /// Loads a font manifest JSON (relative to StreamingAssets, or absolute): faces (family, path, weight, italic,
        /// face, unicodeRange) and fallback families. Each manifest is loaded once per runtime session, before the first page loads.
        /// </summary>
        public static int AddFonts(string manifestFile)
        {
            if (!IsRunning || string.IsNullOrEmpty(manifestFile))
            {
                return 0;
            }
            var file = ResolvePath(manifestFile);
            if (fontManifests.Contains(file))
            {
                return 0;
            }
            if (!File.Exists(file))
            {
                Debug.LogWarning($"weft: font manifest not found: {file}");
                return 0;
            }
            FontManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<FontManifest>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"weft: {file} is not a valid JSON font manifest: {e.Message}");
                return 0;
            }
            if (manifest == null)
            {
                Debug.LogWarning($"weft: {file} is not a JSON object");
                return 0;
            }
            fontManifests.Add(file);
            var folder = Path.GetDirectoryName(file) ?? string.Empty;
            var added = 0;
            if (manifest.faces != null)
            {
                foreach (var entry in manifest.faces)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.family) || string.IsNullOrEmpty(entry.path))
                    {
                        continue;
                    }
                    try
                    {
                        AddFont(entry.family, Path.Combine(folder, entry.path), entry.weight > 0 ? entry.weight : 400,
                            entry.italic, entry.face, entry.unicodeRange);
                        ++added;
                    }
                    catch (WeftException e)
                    {
                        Debug.LogWarning($"weft: font {entry.family} ({entry.path}) not added: {e.Message}");
                    }
                }
            }
            if (manifest.fallback != null)
            {
                foreach (var family in manifest.fallback)
                {
                    if (string.IsNullOrEmpty(family))
                    {
                        continue;
                    }
                    try
                    {
                        AddFallbackFamily(family);
                    }
                    catch (WeftException e)
                    {
                        Debug.LogWarning($"weft: fallback font {family} not added: {e.Message}");
                    }
                }
            }
            return added;
        }

        /// <summary>
        /// Registers custom UI shader effects from a manifest JSON (relative to StreamingAssets, or absolute) for pages
        /// using <c>background-image: -weft-effect(name)</c>. Already-registered effect names are skipped.
        /// </summary>
        public static int AddEffects(string manifestFile)
        {
            if (!IsRunning || string.IsNullOrEmpty(manifestFile))
            {
                return 0;
            }
            var file = ResolvePath(manifestFile);
            if (!File.Exists(file))
            {
                Debug.LogWarning($"weft: effect manifest not found: {file}");
                return 0;
            }
            EffectManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<EffectManifest>(File.ReadAllText(file));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"weft: {file} is not a valid JSON effect manifest: {e.Message}");
                return 0;
            }
            if (manifest?.effects == null)
            {
                return 0;
            }
            var folder = Path.GetDirectoryName(file) ?? string.Empty;
            var added = 0;
            foreach (var entry in manifest.effects)
            {
                if (entry == null || string.IsNullOrEmpty(entry.name) || registeredEffects.Contains(entry.name))
                {
                    continue;
                }
                if (RegisterEffect(folder, entry))
                {
                    registeredEffects.Add(entry.name);
                    ++added;
                }
            }
            return added;
        }

        static bool RegisterEffect(string folder, EffectEntry entry)
        {
            var pins = new List<GCHandle>();
            byte* Pin(byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0)
                {
                    return null;
                }
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                pins.Add(handle);
                return (byte*)handle.AddrOfPinnedObject();
            }
            byte[] ReadOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            try
            {
                var paramCount = entry.parameters?.Length ?? 0;
                var parameters = new WeftNative.EffectParameter[paramCount];
                for (var i = 0; i < paramCount; ++i)
                {
                    var p = entry.parameters[i];
                    var pName = Utf8(p?.name);
                    var pInit = Utf8(p?.initial);
                    uint kind = (p?.type) switch
                    {
                        "number" => WeftNative.EffectNumber,
                        "vec2" => WeftNative.EffectVec2,
                        "vec3" => WeftNative.EffectVec3,
                        "vec4" => WeftNative.EffectVec4,
                        "color" => WeftNative.EffectColor,
                        _ => 0u,
                    };
                    parameters[i] = new WeftNative.EffectParameter
                    {
                        Name = Pin(pName),
                        NameSize = (UIntPtr)pName.Length,
                        Initial = Pin(pInit),
                        InitialSize = (UIntPtr)pInit.Length,
                        Type = kind,
                    };
                }
                var texCount = entry.textures?.Length ?? 0;
                var textures = new WeftNative.EffectTexture[texCount];
                for (var i = 0; i < texCount; ++i)
                {
                    var t = entry.textures[i];
                    var tName = Utf8(t?.name);
                    var tBytes = string.IsNullOrEmpty(t?.path) ? Array.Empty<byte>() : ReadOptional(Path.Combine(folder, t.path));
                    var format = (t?.format == "srgb" ? WeftNative.EffectTextureSrgb : WeftNative.EffectTextureData) |
                                 WeftNative.EffectTexturePng;
                    textures[i] = new WeftNative.EffectTexture
                    {
                        Name = Pin(tName),
                        NameSize = (UIntPtr)tName.Length,
                        Bytes = Pin(tBytes),
                        Size = (UIntPtr)tBytes.Length,
                        Format = format,
                    };
                }
                var prefix = string.IsNullOrEmpty(entry.bytecode) ? string.Empty : Path.Combine(folder, entry.bytecode);
                var dxbcEncoded = string.IsNullOrEmpty(prefix) ? Array.Empty<byte>() : ReadOptional(prefix + ".encoded.dxbc");
                var dxbcLinear = string.IsNullOrEmpty(prefix) ? Array.Empty<byte>() : ReadOptional(prefix + ".linear.dxbc");
                var spirvEncoded = string.IsNullOrEmpty(prefix) ? Array.Empty<byte>() : ReadOptional(prefix + ".encoded.spv");
                var spirvLinear = string.IsNullOrEmpty(prefix) ? Array.Empty<byte>() : ReadOptional(prefix + ".linear.spv");
                var nameBytes = Utf8(entry.name);
                fixed (WeftNative.EffectParameter* pPtr = parameters)
                fixed (WeftNative.EffectTexture* tPtr = textures)
                {
                    var desc = new WeftNative.EffectDesc
                    {
                        StructSize = (uint)sizeof(WeftNative.EffectDesc),
                        Animated = entry.animated ? 1u : 0u,
                        Name = Pin(nameBytes),
                        NameSize = (UIntPtr)nameBytes.Length,
                        Parameters = paramCount > 0 ? pPtr : null,
                        Textures = texCount > 0 ? tPtr : null,
                        ParameterCount = (uint)paramCount,
                        TextureCount = (uint)texCount,
                        DxbcEncoded = Pin(dxbcEncoded),
                        DxbcEncodedSize = (UIntPtr)dxbcEncoded.Length,
                        DxbcLinear = Pin(dxbcLinear),
                        DxbcLinearSize = (UIntPtr)dxbcLinear.Length,
                        SpirvEncoded = Pin(spirvEncoded),
                        SpirvEncodedSize = (UIntPtr)spirvEncoded.Length,
                        SpirvLinear = Pin(spirvLinear),
                        SpirvLinearSize = (UIntPtr)spirvLinear.Length,
                        Backdrop = entry.backdrop ? 1u : 0u,
                        BackdropMargin = entry.backdrop ? entry.backdropMargin : 0f,
                    };
                    var result = WeftNative.weft_unity_add_effect(&desc);
                    if (result != WeftNative.Result.Ok)
                    {
                        Debug.LogWarning($"weft: effect {entry.name} not added ({WeftNative.Describe(result)})");
                        return false;
                    }
                    return true;
                }
            }
            finally
            {
                foreach (var handle in pins)
                {
                    handle.Free();
                }
            }
        }

        static string ResolvePath(string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Application.streamingAssetsPath, path));
        }

        [Serializable]
        sealed class FontManifest
        {
            public FontEntry[] faces = null;
            public string[] fallback = null;
        }

        [Serializable]
        sealed class FontEntry
        {
            public string family = string.Empty;
            public string path = string.Empty;
            public string unicodeRange = string.Empty;
            public int weight = 400;
            public int face = 0;
            public bool italic = false;
        }

        [Serializable]
        sealed class EffectManifest
        {
            public EffectEntry[] effects = null;
        }

        [Serializable]
        sealed class EffectEntry
        {
            public string name = string.Empty;
            public string bytecode = string.Empty;
            public bool animated = false;
            public bool backdrop = false;
            public float backdropMargin = 0;
            public EffectParameterEntry[] parameters = null;
            public EffectTextureEntry[] textures = null;
        }

        [Serializable]
        sealed class EffectParameterEntry
        {
            public string name = string.Empty;
            public string type = string.Empty;
            public string initial = string.Empty;
        }

        [Serializable]
        sealed class EffectTextureEntry
        {
            public string name = string.Empty;
            public string path = string.Empty;
            public string format = string.Empty;
        }

        /// <summary>The family a CSS generic keyword (serif, sans-serif, monospace, system-ui, cursive, fantasy) names.</summary>
        public static void SetGenericFamily(string generic, string family)
        {
            var genericBytes = Utf8(generic);
            var familyBytes = Utf8(family);
            fixed (byte* g = genericBytes)
            fixed (byte* f = familyBytes)
            {
                Check(WeftNative.weft_unity_set_generic_family(g, (UIntPtr)genericBytes.Length, f, (UIntPtr)familyBytes.Length),
                    $"setting {generic} to {family}");
            }
        }

        /// <summary>A family tried for characters no family of an element's font-family list covers.</summary>
        public static void AddFallbackFamily(string family)
        {
            var familyBytes = Utf8(family);
            fixed (byte* f = familyBytes)
            {
                Check(WeftNative.weft_unity_add_fallback_family(f, (UIntPtr)familyBytes.Length), $"adding the fallback {family}");
            }
        }

        /// <summary>
        /// Shows a texture of the game's in the pages by name: an element whose CSS background-image is
        /// -weft-texture(name) draws it as CSS draws a background image (background-size: 100% 100% stretches it over the
        /// element), inside its rounded corners and under its borders, composited with the page. The texture holds
        /// linear, premultiplied RGBA: a camera's render texture in ARGBHalf (any graphics API), or a linear RGBA8 one.
        /// It may change every frame; render it before WeftView's LateUpdate draws the page, so that both show the same
        /// frame. null takes the name away; bind it again after the texture is created again (a resize). Stopping the
        /// runtime unbinds every name.
        /// </summary>
        public static void SetTexture(string name, Texture texture)
        {
            var nameBytes = Utf8(name);
            var native = texture ? texture.GetNativeTexturePtr() : IntPtr.Zero;
            var width = texture ? (uint)texture.width : 0;
            var height = texture ? (uint)texture.height : 0;
            fixed (byte* n = nameBytes)
            {
                Check(WeftNative.weft_unity_set_texture(n, (UIntPtr)nameBytes.Length, native, width, height), $"binding the texture {name}");
            }
        }

        public static WeftRenderStats RenderStats
        {
            get
            {
                WeftNative.RenderStats stats;
                WeftNative.weft_unity_render_get_stats(&stats);
                return new WeftRenderStats
                {
                    Api = stats.Renderer switch
                    {
                        2 => UnityEngine.Rendering.GraphicsDeviceType.Direct3D11,
                        18 => UnityEngine.Rendering.GraphicsDeviceType.Direct3D12,
                        21 => UnityEngine.Rendering.GraphicsDeviceType.Vulkan,
                        _ => UnityEngine.Rendering.GraphicsDeviceType.Null,
                    },
                    Draws = stats.Draws,
                    Failures = stats.Failures,
                    LastFailure = Marshal.PtrToStringAnsi((IntPtr)stats.LastFailure) ?? string.Empty,
                };
            }
        }

        internal static byte[] Utf8(string text)
        {
            return string.IsNullOrEmpty(text) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(text);
        }

        // UTF-8 of text into a buffer of the caller's, grown when text needs more: calls made every frame allocate
        // nothing once it is large enough. The bytes it wrote.
        internal static int Utf8(string text, ref byte[] buffer)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }
            var most = Encoding.UTF8.GetMaxByteCount(text.Length);
            if (buffer == null || buffer.Length < most)
            {
                buffer = new byte[Math.Max(most, 256)];
            }
            return Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
        }

        internal static void Check(WeftNative.Result result, string what)
        {
            if (result != WeftNative.Result.Ok)
            {
                throw new WeftException(what, WeftNative.Describe(result));
            }
        }

        // The ends of a play session stop the runtime.
        static void Hook()
        {
            if (hooked)
            {
                return;
            }
            hooked = true;
            Application.quitting += Stop;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.playModeStateChanged += OnPlayMode;
#endif
        }

#if UNITY_EDITOR
        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                Stop();
            }
        }
#endif

        // A play session without a domain reload keeps static state: the hooks are made again.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Unhook()
        {
            Application.quitting -= Stop;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= Stop;
            EditorApplication.playModeStateChanged -= OnPlayMode;
#endif
            hooked = false;
            fontManifests.Clear();
            registeredEffects.Clear();
        }
    }
}
