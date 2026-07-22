using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Flash bombasi korlugu: kafanin onune kilitli tam-ekran beyaz quad (LowHealthVignette
    /// ile ayni VR-guvenli desen — screen-space UI VR'da calismaz). Siddet YEREL kameraya gore
    /// hesaplanir: patlamaya bakiyorsan tam beyaz, sirtin donukse hafif; mesafe ve araya giren
    /// engel (LOS) siddeti dusurur. Tepe aninda aniden beyazlar, sonra ustel sonumlenir.
    /// GrenadeController patlama mesajinda <see cref="TriggerAt"/> cagirir — sahne kurulumu
    /// gerektirmez, ilk kullanimda kendini yaratir.
    /// </summary>
    public class FlashBlindEffect : MonoBehaviour
    {
        const float HoldSeconds = 0.25f;   // tepe parlaklikta bekleme
        const float MinVisibleAlpha = 0.004f;

        static FlashBlindEffect _instance;

        Transform _quad;
        Material _mat;
        float _peak;      // bu patlamanin tepe alfasi (0-1)
        float _sinceHit;  // tetikten beri gecen sure
        float _tau;       // sonumlenme sabiti (siddetle uzar)

        /// <summary>Her istemcide yerel cagrilir: pos'taki flash icin korluk uygula.</summary>
        public static void TriggerAt(Vector3 pos, float radius)
        {
            Transform head = Head();
            if (head == null) return;

            Vector3 to = pos - head.position;
            float dist = to.magnitude;
            if (dist > radius || dist < 0.01f) return;

            // Bakis acisi: tam bakista 1, sirt donukken taban 0.25 (isik her yerden sizar).
            float facing = Mathf.Max(0.25f, 0.5f + 0.5f * Vector3.Dot(head.forward, to / dist));
            float distF = 1f - dist / radius;
            // Siper: aradaki engel korlugu buyuk olcude keser ama tamamen yok etmez.
            float los = Physics.Linecast(head.position, pos, out _, ~0,
                QueryTriggerInteraction.Ignore) ? 0.3f : 1f;

            float intensity = Mathf.Clamp01(facing * distF * los);
            if (intensity <= 0.02f) return;

            Ensure();
            // Ust uste flashlarda en guclusu gecerli; sure sifirlanir.
            _instance._peak = Mathf.Max(_instance._peak, intensity);
            _instance._sinceHit = 0f;
            _instance._tau = Mathf.Lerp(0.35f, 1.4f, intensity); // tam yiyen ~4 sn kor kalir
        }

        static Transform Head()
        {
            var rig = XRRigReference.Instance;
            if (rig != null && rig.head != null) return rig.head;
            return Camera.main != null ? Camera.main.transform : null;
        }

        static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("~FlashBlind");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FlashBlindEffect>();
        }

        void Awake()
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Blind Quad";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localScale = new Vector3(2f, 2f, 1f);
            _mat = UITheme.CreateTransparentMaterial(Color.white);
            q.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            _quad = q.transform;
            q.gameObject.SetActive(false);
        }

        void LateUpdate()
        {
            if (_peak <= 0f) return;
            _sinceHit += Time.deltaTime;

            float a = _sinceHit <= HoldSeconds
                ? _peak
                : _peak * Mathf.Exp(-(_sinceHit - HoldSeconds) / _tau);

            if (a < MinVisibleAlpha)
            {
                _peak = 0f;
                if (_quad.gameObject.activeSelf) _quad.gameObject.SetActive(false);
                return;
            }

            Transform head = Head();
            if (head == null) return;

            if (!_quad.gameObject.activeSelf) _quad.gameObject.SetActive(true);
            // Vignette 0.52'de, hasar flasi 0.5'te — korluk hepsinin ONUNDE durur.
            transform.SetPositionAndRotation(head.position + head.forward * 0.48f, head.rotation);
            UITheme.SetMaterialColor(_mat, new Color(1f, 1f, 1f, a));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { _instance = null; }
    }
}
