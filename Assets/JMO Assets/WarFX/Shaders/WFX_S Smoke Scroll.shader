// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// "WFX/Scroll/Smoke"un URP karsiligi — patlama sonrasi YER DUMANI icin kullanilir.
// Built-in orijinali URP'de dogru cizmiyordu (bkz. "WFX_S Particle Add A8.shader").
//
// Blend DstColor SrcAlpha = KOYULTAN karisim: additive'in aksine bu ekrani
// karartabilir, yani gercek KOYU/SIYAH duman ancak boyle elde edilir. Notr deger
// 0.5'tir; maskenin (doku alfasi x parcacik alfasi) sifir oldugu yerde fragman
// 0.5'e lerp'lenir ve o piksel degismeden kalir. Doku dikeyde kaydirilarak duman
// "kabariyor" hissi verir. Orijinal formul birebir korundu; _Time -> _Time.y
// (orijinaldeki cikarilmis _Time float'i .x'e denk gelip kaydirmayi 20 kat
// yavaslatiyordu).

Shader "WFX/Scroll/Smoke"
{
Properties
{
    _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
    _MainTex ("Texture", 2D) = "white" {}
    _ScrollSpeed ("Scroll Speed", Float) = 2.0
}
SubShader
{
    Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "PreviewType"="Plane" }
    Blend DstColor SrcAlpha
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
            half mask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv).a * i.color.a;

            float2 scrolled = i.uv;
            scrolled.y -= fmod(_Time.y * _ScrollSpeed, 1);
            half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, scrolled);
            tex.rgb *= i.color.rgb * _TintColor.rgb;
            tex.a = mask;
            tex = lerp(half4(0.5, 0.5, 0.5, 0.5), tex, mask);
            return tex;
        }
        ENDHLSL
    }
}
Fallback Off
}
