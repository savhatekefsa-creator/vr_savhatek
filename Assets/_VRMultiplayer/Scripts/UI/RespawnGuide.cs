using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Elenen (ya da henuz oyuna girmemis) oyuncunun ekrani: gorusu grilestiren yari saydam bir
    /// perde + kendi takim bolgesine yonlendiren metin + bolgedeyken geri sayim.
    ///
    /// GORUSU KAPATMAZ, bilerek. Bu oyun kolokasyonlu: olu oyuncu dogum bolgesine GERCEK odada
    /// yuruyecek. Ekrani karartmak onu gercek bir engele ya da baska bir oyuncuya carptirir.
    /// Bu yuzden perde acik gri ve dusuk alfali — "oldun" hissini verir ama yol gostermeye
    /// devam eder. <see cref="alpha"/> degerini yukseltirken bunu hatirla.
    ///
    /// Gercek doygunluk-dusurme (desaturation) bir post-process Volume ister; projede hic
    /// post-process yok, o yuzden en ucuz ve build'de guvenli yol olan gri quad kullanildi
    /// (ayni desen: <see cref="LowHealthVignette"/>, <see cref="DamageDirectionFlash"/>).
    ///
    /// <see cref="PlayerHUD"/> tarafindan olusturulur ve her kare <see cref="SetState"/> ile
    /// beslenir. Sahnedeki <see cref="TeamSpawnZone"/> halkasinin vurgusunu da bu surer.
    /// </summary>
    public class RespawnGuide : MonoBehaviour
    {
        [Tooltip("Gri perdenin opakligi. YUKSELTIRKEN DIKKAT: oyuncu bu ekranla gercek odada yuruyor.")]
        [Range(0f, 0.85f)] public float alpha = 0.55f;

        static readonly Color VeilColor = new Color(0.45f, 0.46f, 0.48f);

        Transform _veil;
        Material _veilMat;
        TextMesh _text;

        bool _active;
        TeamSpawnZone _zone;

        void Awake()
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Respawn Veil";
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            q.transform.SetParent(transform, false);
            q.transform.localScale = new Vector3(2.4f, 2.4f, 1f);
            _veilMat = UITheme.CreateTransparentMaterial(VeilColor);
            q.GetComponent<MeshRenderer>().sharedMaterial = _veilMat;
            _veil = q.transform;

            var t = new GameObject("Respawn Text");
            t.transform.SetParent(transform, false);
            _text = t.AddComponent<TextMesh>();
            _text.characterSize = 0.06f;
            _text.fontSize = 60;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.color = Color.white;
            t.transform.localScale = Vector3.one * 0.16f;

            gameObject.SetActive(false);
        }

        /// <summary>PlayerHUD'daki ozel font secimi burada da gecerli olsun (bos = varsayilan).</summary>
        public void SetFont(Font f)
        {
            if (f != null && _text != null) _text.font = f;
        }

        void OnDestroy()
        {
            // Perde kapanirken sahnedeki halka vurgulu kalmasin.
            if (_zone != null) _zone.SetLocalState(false, 0f);
        }

        /// <summary>
        /// Her kare PlayerHUD tarafindan cagrilir.
        /// </summary>
        /// <param name="waiting">Oyuncu olu / dogum bekliyor mu?</param>
        /// <param name="team">Oyuncunun takimi (0 = henuz secilmedi).</param>
        /// <param name="inZone">Sunucuya gore cemberin icinde mi?</param>
        /// <param name="progress01">Dogum geri sayiminin 0..1 ilerlemesi.</param>
        /// <param name="holdSeconds">Toplam bekleme suresi — kalan saniyeyi yazmak icin.</param>
        public void SetState(bool waiting, byte team, bool inZone, float progress01, float holdSeconds)
        {
            // Takim secilmeden perde ACILMAZ: o asamada TeamSelector'un kendi paneli onde duruyor,
            // ustune bir de gri perde binerse yazi okunmaz.
            bool want = waiting && team != 0;
            if (_active != want)
            {
                _active = want;
                gameObject.SetActive(want);
                if (!want && _zone != null)
                {
                    _zone.SetLocalState(false, 0f);
                    _zone = null;
                }
            }
            if (!want) return;

            _zone = TeamSpawnZone.For(team);
            _zone?.SetLocalState(true, progress01);

            UITheme.SetMaterialColor(_veilMat,
                new Color(VeilColor.r, VeilColor.g, VeilColor.b, alpha));

            if (_zone == null)
            {
                // Bolge kurulmamis; PlayerHealth guvenlik agiyla zamanli dogum yapiyor.
                _text.text = "YENIDEN DOGULUYOR\n" + Remaining(progress01, holdSeconds);
            }
            else if (inZone)
            {
                _text.text = "DOGUM BOLGESINDESIN\nBEKLE: " + Remaining(progress01, holdSeconds);
            }
            else
            {
                float d = _zone.HorizontalDistance(HeadPosition());
                _text.text = $"TAKIM BOLGENE GIT\n{d:0.0} m";
            }
        }

        static string Remaining(float progress01, float holdSeconds)
        {
            float left = Mathf.Max(0f, holdSeconds * (1f - Mathf.Clamp01(progress01)));
            return Mathf.CeilToInt(left) + " sn";
        }

        static Vector3 HeadPosition()
        {
            var rig = XRRigReference.Instance;
            if (rig != null && rig.head != null) return rig.head.position;
            return Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        }

        void LateUpdate()
        {
            if (!_active) return;

            var rig = XRRigReference.Instance;
            Transform head = rig != null && rig.head != null ? rig.head
                           : (Camera.main != null ? Camera.main.transform : null);
            if (head == null) return;

            // Perde vignette/hasar flasindan daha ONDE dursun ki onlarla z-cakismasin.
            _veil.SetPositionAndRotation(head.position + head.forward * 0.45f, head.rotation);

            // Yazi biraz daha uzakta — VR'da cok yakin metin okunmaz.
            _text.transform.position = head.position + head.forward * 1.2f;
            _text.transform.rotation = Quaternion.LookRotation(_text.transform.position - head.position);
        }
    }
}
