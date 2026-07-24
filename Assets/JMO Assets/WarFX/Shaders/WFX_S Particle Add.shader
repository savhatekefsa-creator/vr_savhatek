// WarFX Shader — URP portu (orijinal: Jean Moreno, (c) 2015)
// Yerlesik "Particles/Additive" shaderinin karsiligi: Unity 6 URP'de o yerlesik shader
// ARTIK YOK, ona bagli malzemeler hic cizilmiyordu (alev/duman efektlerinin gorunmemesinin
// sebebi buydu). Ayni property'ler (_TintColor/_MainTex), ayni blend (One One) ve ayni
// fragman formulu kullanilir, boylece malzemeler tek referans degisimiyle calisir.
//
// "Alpha8" varyantindan farki: doku RGBA olarak ornekleniyor (masaustu alev/duman dokulari
// renk bilgisini RGB'de tasir; yalniz alpha okumak onlari duz renkli lekeye cevirir).
// Soft-particle dali diger portlarla ayni gerekcesiyle atlandi.

Shader "WFX/Additive"
{
Properties
{
    _TintColor ("Tint Color", Color) = (0.5,0.5,0.5,0.5)
    _MainTex ("Particle Texture", 2D) = "white" {}
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
            half4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uv);
            // ALFA ILE ONCARPMA: additive blend (One One) yalnizca RGB'yi kullanir, alfayi
            // gormezden gelir. WarFX dokularinin bir kisminda sekil RGB'de (alev: siyah zemin
            // uzerine parlak alev), bir kisminda ise ALFADA (duman: WFX_T_SmokeLoopAlpha'nin
            // RGB'si 122..205, yani HICBIR YERDE siyah degil — sekli alfa tasir). Ikincisinde
            // ham RGB eklemek quad'i bastan basa gri-beyaz doldurur; patlamada gorulen keskin
            // kenarli BEYAZ DIKDORTGENLER buydu. rgb *= a hepsini dogru isler: sekli alfada
            // olan doku maskelenir, alfasi 1 olan (alev) doku degismeden gecer.
            tex.rgb *= tex.a;
            return 2.0 * i.color * _TintColor * tex;
        }
        ENDHLSL
    }
}
Fallback Off
}
