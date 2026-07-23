// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// Orijinal CG/built-in shader Unity 6 URP'de cizim uretmiyordu; ayni isim ve
// property'lerle, ayni blend + fragman formuluyle HLSL'e cevrildi. Boylece
// malzemeler/prefablar/GUID'ler HIC degismeden calisir. Soft-particle dallari
// atlandi: SOFTPARTICLES_ON anahtari zaten hicbir zaman set edilmiyordu.

Shader "WFX/Scroll/Additive"
{
Properties
{
    _MainTex ("Looped Texture + Alpha Mask", 2D) = "white" {}
    _ScrollSpeed ("Scroll Speed", Float) = 2.0
    _InvFade ("Soft Particles Factor", Range(0.01,3.0)) = 1.0
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend One One
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
        float _ScrollSpeed;
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
            half4 prev = (SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a * i.color.a).xxxx;
            float2 uv2 = i.uv;
            uv2.y -= fmod(_Time.x * _ScrollSpeed, 1);
            prev.rgb *= SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, uv2).rgb * i.color.rgb;
            return prev;
        }
        ENDHLSL
    }
}
Fallback Off
}
