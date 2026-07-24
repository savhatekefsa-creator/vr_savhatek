// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// "WFX/Scroll/Alpha Blended"in URP karsiligi — SIS BOMBASININ ana duman shaderi.
// Built-in orijinali URP'de dogru cizmiyordu (bkz. "WFX_S Particle Add A8 URP").
//
// Isleyis: dokunun ALFA kanali maskeyi (dumanin sekli), RGB kanali ise KAYAN dokuyu
// verir — UV dikeyde zamanla kaydirilinca duman "kabariyor" gibi gorunur. Orijinal
// formul birebir korundu:
//     alpha = i.color.a * tex(uv).a          (maske: kaydirilmamis UV)
//     rgb   = i.color.rgb * tex(uv_kaydirilmis).rgb
// _Time.y kullanilir (URP'de _Time.x = t/20, .y = t saniye; orijinaldeki cikarilmis
// _Time float'i .x'e denk geldiginden kaydirma 20 kat yavas kalirdi).

Shader "WFX/Scroll/Alpha Blended"
{
Properties
{
    _MainTex ("Looped Texture + Alpha Mask", 2D) = "white" {}
    _InvFade ("Soft Particles Factor", Range(0.01,3.0)) = 1.0
    _ScrollSpeed ("Scroll Speed", Float) = 2.0
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend SrcAlpha OneMinusSrcAlpha
    ColorMask RGB
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
        float _InvFade;
        float _ScrollSpeed;
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
            half4 outC;
            outC.a = i.color.a * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a;

            float2 scrolled = i.uv;
            scrolled.y -= fmod(_Time.y * _ScrollSpeed, 1);
            outC.rgb = i.color.rgb * SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, scrolled).rgb;
            return outC;
        }
        ENDHLSL
    }
}
Fallback Off
}
