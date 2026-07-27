using System.Collections;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Vurulunca gorusun KENARLARINDA kirmizi bir flas patlatir ve hasarin GELDIGI YONU
    /// belli eder. Ust uste binen dort katman:
    ///
    ///   1) Darbe tonu     — TUM ekranda cok dusuk alfali kirmizi, yalnizca ilk ~0.25 sn.
    ///                       Dokusuz (RespawnGuide perdesiyle ayni, cihazda KANITLI yol);
    ///                       vurus anini kacirilamaz yapar.
    ///   2) Kenar halkasi  — tum kenarlarda yanar, her hasarda. "Vuruldum" geri bildirimi.
    ///   3) Kenar yayi     — kaynak yonunde, halkanin USTUNE biner; o taraf digerlerinden
    ///                       belirgin sekilde daha kirmizi olur (sagdan yersen sag kenar).
    ///   4) Merkez kamasi  — ekranin ortasina dogru, kaynak yonunde dar bir yay; kenara
    ///                       bakmadan "nereden yedim" sorusunu tek bakista okutur.
    ///
    /// QUEST FOV DERSI: quad kafanin 0.5 m onunde ve yari genisligi 1 m oldugundan doku
    /// uv'si goz sapmasina atan(uv/0.5) ile esler: uv 0.26 ≈ 28°, uv 0.5 ≈ 45°, uv 1 ≈ 63°.
    /// Quest lensinin gorunur alani merkezden ~±45-50°. Ilk surumde parlaklik uv 0.45'te
    /// (42°) basliyordu ve gucunu 55°+'ta topluyordu — efekt cihazda LENSIN DISINA
    /// ciziliyordu, "calisiyor ama gorunmuyor"du. Dokular artik ~28°'de baslar; nisan
    /// koridoru (±25°) yine temiz kalir. Kenar esiklerini buyutmeden once bu esleme ile
    /// hangi aciya dustugunu hesapla.
    ///
    /// Katmanlar ortak bir zarfla (hizli yukselis + yumusak sonus) surulur, kenar halkasi
    /// sonerken hafifce nabiz atar ve disa dogru genisler; merkez kamasi da disa kayarak
    /// soner. Ust uste gelen hasarlarda siddet birikir — art arda vurulunca ekran daha
    /// kirmizi olur. Siddet ayrica hasar MIKTARINA gore olceklenir: siyrik ile tam isabet
    /// ayni gorunmez.
    ///
    /// Yon, hasar yolunun kendisinden gelir: PlayerHealth.LocalDamageFrom sahibin
    /// istemcisinde aticinin namlu noktasiyla tetiklenir (eskiden yon, baska sistemin tracer
    /// LineRenderer'larini sahneden kazimakla TAHMIN ediliyordu — iskalayip yakinimizdan
    /// gecen izler yanlis yon gosterebiliyordu). Kaynak bilinmiyorsa (dev hasari gibi)
    /// yonlu katmanlar kapali kalir, ton + halka yine yanar.
    ///
    /// Flas sonerken oyuncu donerse yonlu katmanlar kaynaga gore kayar — cunku bearing her
    /// karede yeniden hesaplanir.
    /// </summary>
    public class DamageDirectionFlash : MonoBehaviour
    {
        [Tooltip("Flasin tam parlakliga cikma suresi (saniye). Kisa tutulur: vurus ANINDA hissedilmeli.")]
        public float attackDuration = 0.06f;
        [Tooltip("Tam parlakliktan tamamen sonmeye kadar gecen sure (saniye).")]
        public float fadeDuration = 0.75f;

        [Tooltip("Vurus anindaki tam-ekran darbe tonunun opakligi (kisa surer). Kolokasyon uyarisi: oyuncu gercek odada yuruyor, bunu fazla yukseltme.")]
        public float impactTintAlpha = 0.22f;
        [Tooltip("Darbe tonunun sonme suresi (saniye) — toplam sureden bagimsiz, cok kisa.")]
        public float impactTintDuration = 0.25f;
        [Tooltip("Tum kenarlarda yanan halkanin en parlak anindaki opakligi.")]
        public float edgeAlpha = 0.7f;
        [Tooltip("Kaynak yonundeki kenar yayinin opakligi — halkanin USTUNE eklenir.")]
        public float directionalAlpha = 0.95f;
        [Tooltip("Merkeze yakin yon gostergesinin opakligi.")]
        public float centerAlpha = 0.85f;
        [Tooltip("Kenar halkasinin sonerken atan nabzinin siddeti (0 = nabizsiz).")]
        public float pulseAmount = 0.12f;

        static readonly Color FlashColor = new Color(1f, 0.12f, 0.08f);

        // Quad'in taban olcegi (gorus alanini kaplar) ve kafadan uzakligi. LowHealthVignette
        // 0.52'de duruyor — bu katmanlar onun ONUNDE kalmali, yoksa vignette flasi keser.
        const float BaseScale = 2f;
        const float HeadDistance = 0.5f;

        sealed class Layer
        {
            public Transform quad;
            public Material mat;
            public Texture2D tex; // null = dokusuz (darbe tonu)
        }

        Layer _tint;     // tam-ekran darbe tonu (dokusuz)
        Layer _ring;     // tum kenarlar
        Layer _arc;      // kaynak yonunde kenar
        Layer _wedge;    // kaynak yonunde merkez gostergesi

        Vector3 _sourcePos;
        bool _directional;
        float _t = -1f;      // -1: kapali
        float _intensity;    // 0-1, hasar miktarindan + birikimden

        float TotalDuration => attackDuration + fadeDuration;

        void Awake()
        {
            // Katmanlar arkadan one dogru siralanir: alfa harmanlamada cizim sirasi onemli,
            // ZWrite kapali oldugu icin hem renderQueue hem kucuk bir Z kaydirmasi verilir.
            _tint = CreateLayer("Flash Tint", GlowKind.None, queueOffset: 0, zOffset: 0.004f);
            _ring = CreateLayer("Flash Ring", GlowKind.Ring, queueOffset: 1, zOffset: 0f);
            _arc = CreateLayer("Flash Arc", GlowKind.EdgeArc, queueOffset: 2, zOffset: -0.004f);
            _wedge = CreateLayer("Flash Wedge", GlowKind.CenterWedge, queueOffset: 3, zOffset: -0.008f);
        }

        void OnEnable() => PlayerHealth.LocalDamageFrom += FlashFrom;
        void OnDisable() => PlayerHealth.LocalDamageFrom -= FlashFrom;

        void OnDestroy()
        {
            // Calisma aninda uretilen malzeme/dokular sahneyle birlikte toplanmaz.
            DestroyLayer(_tint);
            DestroyLayer(_ring);
            DestroyLayer(_arc);
            DestroyLayer(_wedge);
        }

        static void DestroyLayer(Layer l)
        {
            if (l == null) return;
            if (l.mat != null) Destroy(l.mat);
            if (l.tex != null) Destroy(l.tex);
        }

        /// <summary>
        /// Can dususunde cagrilan YEDEK yol: kisa sure sunucunun kaynak-nokta RPC'sini bekler
        /// (geldiyse yonlu flas zaten basladi); gelmezse yonsuz kenar halkasi oynatir. Dev
        /// hasari gibi kaynaksiz hasarlar boyle gorunur.
        /// </summary>
        public void TriggerFromRecentShot(Vector3 bodyPos, int amount = 20)
        {
            // Yonlu flas zaten oynuyorsa (RPC can dususunden once yetisti) dokunma.
            if (_t >= 0f && _directional) return;
            StopAllCoroutines();
            StartCoroutine(ResolveRoutine(bodyPos, amount));
        }

        /// <summary>
        /// Belirli bir dunya noktasindan gelmis gibi yonlu flas oynatir. PlayerHealth olayina
        /// baglidir; test/onizleme icin elle de cagrilabilir.
        /// </summary>
        public void FlashFrom(Vector3 worldSourcePos, int amount = 20)
        {
            StopAllCoroutines();
            Begin(worldSourcePos, true, amount);
        }

        IEnumerator ResolveRoutine(Vector3 bodyPos, int amount)
        {
            // Kaynak-nokta RPC'si can dususuyle ayni tick'te gelmeyebilir; kisa sure bekle.
            // Gelirse FlashFrom bu korutini zaten durdurup yonlu flasi baslatir.
            float deadline = Time.time + 0.15f;
            while (Time.time < deadline)
            {
                if (_t >= 0f && _directional) yield break; // RPC yetisti
                yield return null;
            }
            Begin(bodyPos, false, amount);
        }

        void Begin(Vector3 source, bool directional, int amount)
        {
            // Hasar miktari siddeti olcekler; taban 0.45 birakilir ki en hafif siyrik bile
            // gorulebilsin. ~40 ve ustu hasar tam siddet demektir.
            float add = Mathf.Lerp(0.45f, 1f, Mathf.InverseLerp(5f, 40f, amount));

            // Onceki flas hala gorunuyorsa siddeti uzerine biner: art arda vurulan oyuncunun
            // ekrani giderek kizarir, her isabet sifirdan baslamaz.
            float carry = _t >= 0f ? _intensity * Envelope(_t) * 0.5f : 0f;
            _intensity = Mathf.Clamp01(add + carry);

            _sourcePos = source;
            _directional = directional;
            _t = 0f;

            _tint.quad.gameObject.SetActive(true);
            _ring.quad.gameObject.SetActive(true);
            _arc.quad.gameObject.SetActive(directional);
            _wedge.quad.gameObject.SetActive(directional);
        }

        // Hizli yukselis, yumusak sonus. Vurus anindaki "carpma" hissi attack'in kisaligindan,
        // rahatsiz etmemesi ise release'in karesel sonmesinden gelir.
        float Envelope(float t)
        {
            if (t < attackDuration) return attackDuration <= 0f ? 1f : t / attackDuration;
            float k = 1f - (t - attackDuration) / Mathf.Max(0.01f, fadeDuration);
            return k <= 0f ? 0f : k * k;
        }

        void LateUpdate()
        {
            if (_t < 0f) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            _t += Time.deltaTime;
            if (_t >= TotalDuration)
            {
                _t = -1f;
                _intensity = 0f;
                _tint.quad.gameObject.SetActive(false);
                _ring.quad.gameObject.SetActive(false);
                _arc.quad.gameObject.SetActive(false);
                _wedge.quad.gameObject.SetActive(false);
                return;
            }

            transform.SetPositionAndRotation(head.position + head.forward * HeadDistance, head.rotation);

            float env = Envelope(_t) * _intensity;
            float age = _t / TotalDuration;

            // Darbe tonu: yalnizca ilk impactTintDuration icinde, hizla soner.
            float tintK = Mathf.Clamp01(1f - _t / Mathf.Max(0.05f, impactTintDuration));
            ApplyLayer(_tint, env * impactTintAlpha * tintK, BaseScale);

            // Kenar halkasi: hafif nabiz + sonerken disa dogru genisleme.
            float pulse = 1f + pulseAmount * Mathf.Sin(_t * 18f);
            ApplyLayer(_ring, env * edgeAlpha * pulse, BaseScale * (1f + 0.06f * age));

            if (_directional)
            {
                // Kaynagin kafaya gore yatay yonu; dokunun parlak (ust) kenari o yone cevrilir:
                // on = ust, sag = sag, arka = alt kenar. Her karede yeniden hesaplanir, boylece
                // flas sonerken oyuncu donerse gosterge kaynakta kalir.
                Vector3 local = head.InverseTransformPoint(_sourcePos);
                float bearing = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                var rot = Quaternion.Euler(0f, 0f, -bearing);
                _arc.quad.localRotation = rot;
                _wedge.quad.localRotation = rot;

                ApplyLayer(_arc, env * directionalAlpha, BaseScale);
                // Merkez gostergesi disa dogru kayarak soner — vurusun "gelis" yonunu vurgular.
                ApplyLayer(_wedge, env * centerAlpha, BaseScale * (1f + 0.15f * age));
            }
        }

        void ApplyLayer(Layer l, float alpha, float scale)
        {
            UITheme.SetMaterialColor(l.mat, new Color(FlashColor.r, FlashColor.g, FlashColor.b, Mathf.Clamp01(alpha)));
            l.quad.localScale = new Vector3(scale, scale, 1f);
        }

        // ------------------------------------------------------------- kurulum / doku

        Layer CreateLayer(string name, GlowKind kind, int queueOffset, float zOffset)
        {
            var tex = kind == GlowKind.None ? null : MakeGlowTexture(kind);

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localPosition = new Vector3(0f, 0f, zOffset);
            q.transform.localScale = new Vector3(BaseScale, BaseScale, 1f);

            var mat = UITheme.CreateTransparentMaterial(FlashColor);
            if (tex != null)
            {
                if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
                if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);
            }
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + queueOffset;
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            q.SetActive(false);

            return new Layer { quad = q.transform, mat = mat, tex = tex };
        }

        enum GlowKind
        {
            None,          // dokusuz tam-ekran ton
            Ring,          // tum kenarlarda halka
            EdgeArc,       // tek yonde kenar yayi
            CenterWedge,   // merkeze yakin dar yay
        }

        // Merkezi seffaf, istenen bolgede parlak doku. Ring/EdgeArc kenardan disa dogru
        // koyulasir; CenterWedge ise merkezle kenar arasinda dar bir bantta yanar.
        //
        // ACI ESLEMESI (0.5 m'de 2x2 quad): uv 0.26 ≈ 28°, uv 0.5 ≈ 45° (Quest lens kenari),
        // uv 1.0 ≈ 63°. Esikler Quest'in gorunur konisine gore secildi — bkz. sinif dokusu.
        static Texture2D MakeGlowTexture(GlowKind kind)
        {
            const int S = 256;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    Vector2 v = new Vector2(x - (S - 1) * 0.5f, y - (S - 1) * 0.5f) / (S * 0.5f);
                    float mag = v.magnitude;

                    float alpha;
                    if (kind == GlowKind.CenterWedge)
                    {
                        // Merkez ile kenar arasinda, ~28°'de (uv 0.28) yumusak bir bant.
                        // Nisan koridorunu (±25°) kapatmayacak kadar disarida, lens kenarindan
                        // (45°) ayirt edilecek kadar iceride.
                        float band = Mathf.Clamp01(1f - Mathf.Abs(mag - 0.28f) / 0.10f);
                        alpha = band * band;
                    }
                    else
                    {
                        // Kenar katmanlari: ~28°'den (uv 0.26) disa dogru artan parlaklik;
                        // lens kenarinda (45°, uv 0.5) alfanin ~%30'u dolmus olur. Ilk surum
                        // 42°'de basliyordu — Quest'te lens disinda kaliyordu.
                        float radial = Mathf.Clamp01((mag - 0.26f) / 0.5f);
                        alpha = Mathf.Pow(radial, 1.7f);
                    }

                    if (kind != GlowKind.Ring)
                    {
                        float angle = Mathf.Abs(Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg); // 0 = ust
                        float span = kind == GlowKind.CenterWedge ? 35f : 75f;
                        float angular = Mathf.Clamp01(1f - angle / span);
                        alpha *= angular * angular;
                    }

                    px[y * S + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
                }
            }

            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
