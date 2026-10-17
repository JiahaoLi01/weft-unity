using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Weft
{
    // A View's render texture and its draws: each draw queues the View's latest frame and draws it into the texture on
    // Unity's render thread (a plugin event), clearing it first. The texture is 8-bit BGRA UNORM (RGBA where BGRA cannot
    // be a render target; weft's Vulkan backend draws BGRA only), as weft writes it: sRGB-encoded, premultiplied, its
    // top row first.
    internal sealed class WeftSurface : IDisposable
    {
        readonly CommandBuffer commands = new CommandBuffer { name = "Weft" };
        RenderTexture texture;
        IntPtr native;

        public RenderTexture Texture => texture;

        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (texture && texture.width == width && texture.height == height)
            {
                return;
            }
            Release();
            var format = SystemInfo.IsFormatSupported(GraphicsFormat.B8G8R8A8_UNorm, GraphicsFormatUsage.Render)
                ? GraphicsFormat.B8G8R8A8_UNorm
                : GraphicsFormat.R8G8B8A8_UNorm;
            var desc = new RenderTextureDescriptor(width, height, format, GraphicsFormat.None)
            {
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false,
            };
            texture = new RenderTexture(desc)
            {
                name = "Weft View",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.Create();
            // Asked once: the call waits for the render thread.
            native = texture.GetNativeTexturePtr();
        }

        // Queues the View's latest frame and the event that draws it; false when the View has no frame yet.
        public bool Draw(WeftNative.ViewHandle view, float scale)
        {
            if (!texture || native == IntPtr.Zero)
            {
                return false;
            }
            var id = WeftNative.weft_unity_queue_draw(view, native, (uint)texture.width, (uint)texture.height, scale, 1);
            if (id == 0)
            {
                return false;
            }
            commands.Clear();
            commands.IssuePluginEventAndData(WeftNative.weft_unity_render_event(), WeftNative.weft_unity_render_event_id(), (IntPtr)id);
            Graphics.ExecuteCommandBuffer(commands);
            return true;
        }

        public void Dispose()
        {
            Release();
            commands.Release();
        }

        void Release()
        {
            if (texture)
            {
                texture.Release();
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(texture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
            texture = null;
            native = IntPtr.Zero;
        }
    }
}
