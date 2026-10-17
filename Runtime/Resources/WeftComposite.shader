// A View's texture over what is drawn: weft's pixels are sRGB-encoded and premultiplied, top row first. Drawn with
// premultiplied blending; in a project of linear colour space (_Decode 1) the colours are decoded to linear first, so
// that the sRGB target encodes them again (blending then happens in linear, unlike a browser's, at translucent edges).
Shader "Hidden/Weft/Composite"
{
    Properties
    {
        _MainTex ("Page", 2D) = "black" {}
        _Decode ("Decode sRGB", Float) = 0
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float _Decode;

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.position = UnityObjectToClipPos(v.vertex);
                // weft's top row is the texture's first: Unity's bottom.
                o.uv = float2(v.texcoord.x, 1 - v.texcoord.y);
                return o;
            }

            float3 decode(float3 c)
            {
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                if (_Decode > 0.5 && c.a > 0)
                {
                    c.rgb = decode(saturate(c.rgb / c.a)) * c.a;
                }
                return c;
            }
            ENDCG
        }
    }
}
