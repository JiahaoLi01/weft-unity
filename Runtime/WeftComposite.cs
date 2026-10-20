using UnityEngine;

namespace Weft
{
    /// <summary>
    /// Draws a View's texture as a browser shows a page: weft's pixels are sRGB-encoded, premultiplied and top row first,
    /// so they are drawn upright with premultiplied blending, decoded to linear in a project of linear colour space.
    /// </summary>
    public static class WeftComposite
    {
        static Material material;
        static readonly int Decode = Shader.PropertyToID("_Decode");

        /// <summary>The material (Hidden/Weft/Composite) for a View's texture: on a UI RawImage, a quad, or Graphics.DrawTexture.</summary>
        public static Material Material
        {
            get
            {
                if (!material)
                {
                    var shader = Resources.Load<Shader>("WeftComposite");
                    material = new Material(shader) { name = "Weft Composite", hideFlags = HideFlags.HideAndDontSave };
                }
                material.SetFloat(Decode, QualitySettings.activeColorSpace == ColorSpace.Linear ? 1 : 0);
                return material;
            }
        }

        /// <summary>Draws the texture over a screen rectangle (GUI coordinates); call it in OnGUI's repaint.</summary>
        public static void DrawOnScreen(Rect screen, Texture texture)
        {
            // IMGUI draws with sRGB writes off in linear colour space (on Direct3D 11 and 12), so that its colours show
            // as they are authored: the page's colours, decoded to linear, are encoded again as they are written.
            var linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            var encoding = GL.sRGBWrite;
            if (linear)
            {
                GL.sRGBWrite = true;
            }
            Graphics.DrawTexture(screen, texture, Material);
            if (linear)
            {
                GL.sRGBWrite = encoding;
            }
        }

        /// <summary>Draws the texture over what the target holds (decoded for linear colour space when decode says so).</summary>
        public static void Blit(Texture texture, RenderTexture target, bool decode)
        {
            var m = Material;
            m.SetFloat(Decode, decode ? 1 : 0);
            Graphics.Blit(texture, target, m);
        }
    }
}
