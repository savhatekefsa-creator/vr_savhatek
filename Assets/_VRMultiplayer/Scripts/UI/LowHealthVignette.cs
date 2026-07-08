using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Can dustukce gorus alaninin kenarlarinda beliren kirmizi bir vignette (kenar kararmasi)
    /// efekti. Can esik altina indikce yogunlasir ve nabiz gibi hafifce atar (can azaldikca
    /// hizlanir); oyuncuya bara bakmadan "kritik durumdayim" hissini verir. PlayerHUD her can
    /// degisiminde <see cref="SetHealthRatio"/> ile bunu besler.
    /// </summary>
    public class LowHealthVignette : MonoBehaviour
    {
        [Tooltip("Can bu oranin (0-1) altina dustugunde vignette gorunmeye baslar.")]
        public float threshold = 0.4f;
        [Tooltip("Can sifira yaklastigindaki en yuksek opaklik.")]
        public float maxAlpha = 0.6f;
        [Tooltip("Nabiz atisinin siddeti (0 = sabit, nabizsiz).")]
        public float pulseAmount = 0.25f;

        static readonly Color VignetteColor = new Color(0.8f, 0.05f, 0.03f);

        Transform _quad;
        Material _mat;
        Texture2D _tex;
        float _ratio = 1f;

        void Awake()
        {
            _tex = MakeVignetteTexture();

            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Vignette Quad";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localScale = new Vector3(2f, 2f, 1f);
            _mat = UITheme.CreateTransparentMaterial(VignetteColor);
            if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", _tex);
            if (_mat.HasProperty("_MainTex")) _mat.SetTexture("_MainTex", _tex);
            q.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            _quad = q.transform;
            q.gameObject.SetActive(false);
        }

        /// <summary>Guncel can oranini (0-1) bildirir.</summary>
        public void SetHealthRatio(float ratio)
        {
            _ratio = Mathf.Clamp01(ratio);
        }

        void LateUpdate()
        {
            // Esigin ustundeyse ya da oyuncu olmusse efekt kapali.
            bool active = _ratio > 0f && _ratio < threshold;
            if (_quad.gameObject.activeSelf != active) _quad.gameObject.SetActive(active);
            if (!active) return;

            // VR rig yoksa (Editor testi) ana kamera kafa yerine gecer.
            var rig = XRRigReference.Instance;
            Transform head = rig != null && rig.head != null ? rig.head
                           : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;

            // Hasar flasindan biraz geride dursun ki ust uste binince titreme olmasin.
            transform.SetPositionAndRotation(head.position + head.forward * 0.52f, head.rotation);

            // Can esikten sifira indikce siddet 0 -> 1 arasi artar.
            float severity = 1f - _ratio / threshold;
            float pulseSpeed = Mathf.Lerp(2.5f, 6f, severity); // can azaldikca nabiz hizlanir
            float pulse = 1f + pulseAmount * Mathf.Sin(Time.time * pulseSpeed);
            float a = Mathf.Clamp01(severity * maxAlpha * pulse);
            UITheme.SetMaterialColor(_mat, new Color(VignetteColor.r, VignetteColor.g, VignetteColor.b, a));
        }

        // Merkezi seffaf, kenarlara dogru opaklasan radyal vignette dokusu.
        static Texture2D MakeVignetteTexture()
        {
            const int S = 256;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    Vector2 v = new Vector2(x - (S - 1) * 0.5f, y - (S - 1) * 0.5f) / (S * 0.5f);
                    float alpha = Mathf.Clamp01((v.magnitude - 0.55f) / 0.55f);
                    alpha *= alpha; // yumusak gecis
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return tex;
        }
    }
}
