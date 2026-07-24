using System.Collections;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Vurulunca hasarin geldigi yonde, gorusun kenarinda kisa bir kirmizi parlama gosterir ve
    /// yaklasik 0.7 saniyede soner (eski tam ekran flasin yerine gecer). Flas sonerken oyuncu
    /// donerse parlama kaynaga gore kayar, boylece "nereden yedim" hissi verir.
    ///
    /// Yon, hasar yolunun kendisinden gelir: PlayerHealth.LocalDamageFrom sahibin istemcisinde
    /// aticinin namlu noktasiyla tetiklenir (eskiden yon, baska sistemin tracer
    /// LineRenderer'larini sahneden kazimakla TAHMIN ediliyordu — iskalayip yakinimizdan gecen
    /// izler yanlis yon gosterebiliyordu). Kaynak bilinmiyorsa tum kenarlarda halka yanar.
    /// </summary>
    public class DamageDirectionFlash : MonoBehaviour
    {
        [Tooltip("Flasin tamamen sonmesi icin gecen sure (saniye).")]
        public float fadeDuration = 0.7f;
        [Tooltip("Yonlu flasin en parlak anindaki opaklik.")]
        public float maxAlpha = 0.9f;
        [Tooltip("Yon bulunamazsa tum kenarlarda yanan halkanin opakligi.")]
        public float undirectedAlpha = 0.5f;

        static readonly Color FlashColor = new Color(1f, 0.12f, 0.08f);

        Transform _quad;
        Material _mat;
        Texture2D _arcTex, _ringTex;
        Vector3 _sourcePos;
        bool _directional;
        float _t = -1f; // -1: kapali

        void Awake()
        {
            _arcTex = MakeGlowTexture(arc: true);
            _ringTex = MakeGlowTexture(arc: false);

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Flash Quad";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localScale = new Vector3(2f, 2f, 1f);
            _mat = UITheme.CreateTransparentMaterial(FlashColor);
            q.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            _quad = q.transform;
            q.SetActive(false);
        }

        void OnEnable() => PlayerHealth.LocalDamageFrom += FlashFrom;
        void OnDisable() => PlayerHealth.LocalDamageFrom -= FlashFrom;

        /// <summary>
        /// Can dususunde cagrilan YEDEK yol: kisa sure sunucunun kaynak-nokta RPC'sini bekler
        /// (geldiyse yonlu flas zaten basladi); gelmezse yonsuz halka oynatir. Dev hasari gibi
        /// kaynaksiz hasarlar boyle gorunur.
        /// </summary>
        public void TriggerFromRecentShot(Vector3 bodyPos)
        {
            // Yonlu flas zaten oynuyorsa (RPC can dususunden once yetisti) dokunma.
            if (_t >= 0f && _directional) return;
            StopAllCoroutines();
            StartCoroutine(ResolveRoutine(bodyPos));
        }

        /// <summary>
        /// Belirli bir dunya noktasindan gelmis gibi yonlu flas oynatir (test/onizleme icin).
        /// </summary>
        public void FlashFrom(Vector3 worldSourcePos)
        {
            StopAllCoroutines();
            Begin(worldSourcePos, true);
        }

        IEnumerator ResolveRoutine(Vector3 bodyPos)
        {
            // Kaynak-nokta RPC'si can dususuyle ayni tick'te gelmeyebilir; kisa sure bekle.
            // Gelirse FlashFrom bu korutini zaten durdurup yonlu flasi baslatir.
            float deadline = Time.time + 0.15f;
            while (Time.time < deadline)
            {
                if (_t >= 0f && _directional) yield break; // RPC yetisti
                yield return null;
            }
            Begin(bodyPos, false);
        }

        void Begin(Vector3 source, bool directional)
        {
            _sourcePos = source;
            _directional = directional;
            _t = 0f;
            SetTexture(directional ? _arcTex : _ringTex);
            _quad.gameObject.SetActive(true);
        }

        void SetTexture(Texture2D tex)
        {
            if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", tex);
            if (_mat.HasProperty("_MainTex")) _mat.SetTexture("_MainTex", tex);
        }

        void LateUpdate()
        {
            if (_t < 0f) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            _t += Time.deltaTime;
            if (_t >= fadeDuration)
            {
                _t = -1f;
                _quad.gameObject.SetActive(false);
                return;
            }

            transform.SetPositionAndRotation(head.position + head.forward * 0.5f, head.rotation);
            if (_directional)
            {
                // Kaynagin kafaya gore yatay yonu; dokunun parlak (ust) kenari o yone cevrilir:
                // on = ust, sag = sag, arka = alt kenar.
                Vector3 local = head.InverseTransformPoint(_sourcePos);
                float bearing = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                _quad.localRotation = Quaternion.Euler(0f, 0f, -bearing);
            }
            else
            {
                _quad.localRotation = Quaternion.identity;
            }

            float k = 1f - _t / fadeDuration;
            float a = k * k * (_directional ? maxAlpha : undirectedAlpha);
            UITheme.SetMaterialColor(_mat, new Color(FlashColor.r, FlashColor.g, FlashColor.b, a));
        }

        // Merkezi seffaf, kenari parlak doku: arc=true ise sadece ust kenarda ~70 derecelik
        // bir yay, degilse tum kenarlarda halka.
        static Texture2D MakeGlowTexture(bool arc)
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    Vector2 v = new Vector2(x - (S - 1) * 0.5f, y - (S - 1) * 0.5f) / (S * 0.5f);
                    float radial = Mathf.Clamp01((v.magnitude - 0.45f) / 0.55f);
                    float alpha = radial * radial;
                    if (arc)
                    {
                        float angle = Mathf.Abs(Mathf.Atan2(v.x, v.y) * Mathf.Rad2Deg); // 0 = ust
                        float angular = Mathf.Clamp01(1f - angle / 70f);
                        alpha *= angular * angular;
                    }
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
