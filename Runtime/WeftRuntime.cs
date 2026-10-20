using System;
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

        /// <summary>The weft commit weft.dll was built from.</summary>
        public static string Build => Marshal.PtrToStringAnsi(WeftNative.weft_unity_build()) ?? string.Empty;

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
                WeftNative.weft_unity_stop();
            }
        }

        /// <summary>A face of a font file (a collection's by faceIndex) for CSS to match by family, weight and style.</summary>
        public static void AddFont(string family, string path, int weight = 400, bool italic = false, int faceIndex = 0)
        {
            var familyBytes = Utf8(family);
            var pathBytes = Utf8(path);
            fixed (byte* f = familyBytes)
            fixed (byte* p = pathBytes)
            {
                var face = new WeftNative.FontFace
                {
                    StructSize = (uint)sizeof(WeftNative.FontFace),
                    Weight = (uint)weight,
                    Italic = italic ? 1u : 0u,
                    FaceIndex = (uint)faceIndex,
                    Family = f,
                    FamilySize = (UIntPtr)familyBytes.Length,
                    Path = p,
                    PathSize = (UIntPtr)pathBytes.Length,
                };
                Check(WeftNative.weft_unity_add_font(&face), $"adding the font {path}");
            }
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
        }
    }
}
