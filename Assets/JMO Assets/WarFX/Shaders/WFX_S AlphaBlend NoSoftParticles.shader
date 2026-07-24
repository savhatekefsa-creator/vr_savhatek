// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// Orijinal CG/built-in shader Unity 6 URP'de cizim uretmiyordu; ayni isim ve
// property'lerle, ayni blend + fragman formuluyle HLSL'e cevrildi. Boylece
// malzemeler/prefablar/GUID'ler HIC degismeden calisir. Soft-particle dallari
// atlandi: SOFTPARTICLES_ON anahtari zaten hicbir zaman set edilmiyordu.

Shader "WFX/Alpha Blended (No Soft Particles)"
{
Properties
{
    _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
    _MainTex ("Particle Texture", 2D) = "white" {}
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend SrcAlpha OneMinusSrcAlpha
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
            return 2.0 * i.color * _TintColor * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
        }
        ENDHLSL
    }
}
Fallback Off
}
