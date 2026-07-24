// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// "WFX/Additive Alpha8"in URP karsiligi. Orijinali Built-in RP shaderiydi: fragman
// cikisi COLOR semantigi, tex2D ve _CameraDepthTexture'li soft-particle daliyla
// yazilmisti; URP'de bu pas gecerli bir cikti uretmeyip sabit-fonksiyon fallback'ine
// dusuyor ve DOKU ALFASI HIC UYGULANMIYORDU — patlama, kenarlari belli BEYAZ
// DIKDORTGENLER olarak ciziliyordu.
//
// Alpha8 varyantinin ayirt edici yani: mobil dokular (WFXM_T_* A8) renk tasimaz,
// sekil bilgisi yalniz ALFA kanalindadir. Bu yuzden RGBA degil .a orneklenir ve
// sonuc parcacigin kendi rengiyle boyanir — orijinal formulun birebir ayni:
//     2.0 * i.color * _TintColor * (tex.a * i.color.a * 2.0)
// Soft-particle dali diger URP portlariyla ayni gerekceyle atlandi (URP'de derinlik
// dokusu ayri bir pas/ayar ister; efektler onsuz da dogru gorunuyor).

Shader "WFX/Additive Alpha8"
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
            half a = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a;
            return 2.0 * i.color * _TintColor * (a * i.color.a * 2.0);
        }
        ENDHLSL
    }
}
Fallback Off
}
