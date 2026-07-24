// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// "WFX/Multiply Soft Tint"in URP karsiligi — sis bombasinin ikinci (koyultan) katmani.
// Built-in orijinali URP'de dogru cizmiyordu (bkz. "WFX_S Particle Add A8 URP").
//
// Blend DstColor SrcColor = "overlay" benzeri yumusak carpma. Notr deger 0.5'tir:
// fragman, dokunun gorunmez oldugu yerde (alpha 0) 0.5'e lerp'lenir ki o pikselde
// ekran DEGISMEDEN kalsin. Orijinal formul birebir korundu.

Shader "WFX/Multiply Soft Tint"
{
Properties
{
    _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
    _MainTex ("Texture", 2D) = "white" {}
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend DstColor SrcColor
    Cull Off ZWrite Off

    Pass
    {
        HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
        CBUFFER_START(UnityPerMaterial)
        float4 _MainTex_ST;
        half4 _TintColor;
        CBUFFER_END

        struct Attributes { float4 positionOS : POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };
        struct Varyings  { float4 positionCS : SV_POSITION; half4 color : COLOR; float2 uv : TEXCOORD0; };

        Varyings vert(Attributes v)
        {
            Varyings o;
            o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
            o.color = v.color;
            o.uv = TRANSFORM_TEX(v.uv, _MainTex);
            return o;
        }

        half4 frag(Varyings i) : SV_Target
        {
            half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            tex.rgb *= i.color.rgb * _TintColor.rgb;
            tex = lerp(half4(0.5, 0.5, 0.5, 0.5), tex, tex.a * i.color.a);
            return tex;
        }
        ENDHLSL
    }
}
Fallback Off
}
