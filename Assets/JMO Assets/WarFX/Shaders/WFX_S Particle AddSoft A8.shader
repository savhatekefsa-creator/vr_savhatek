// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// "WFX/Additive (Soft) Alpha8"in URP karsiligi. Built-in orijinali URP'de dogru
// cizmiyordu (gerekce icin bkz. "WFX_S Particle Add A8.shader" basligi).
//
// "Soft additive" = Blend OneMinusDstColor One: zaten aydinlik piksellerde katki
// azalir, boylece duz additive gibi beyaza DOYMAZ. Aydinlik gunduz sahnesinde
// patlama parlamalarinin rengini korumasi bu yuzden daha kolaydir.
// Doku Alpha8'dir (sekil alfa kanalinda); renk parcacigin kendi renginden gelir.

Shader "WFX/Additive (Soft) Alpha8"
{
Properties
{
    _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
    _MainTex ("Particle Texture (alpha)", 2D) = "white" {}
    _InvFade ("Soft Particles Factor", Range(0.01,3.0)) = 1.0
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend OneMinusDstColor One
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
        float _InvFade;
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
            return 2.0 * i.color * _TintColor * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a;
        }
        ENDHLSL
    }
}
Fallback Off
}
