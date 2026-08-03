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

        // Yazinin altindaki YANIP SONEN pusula oku. Ayri bir dunya-uzayi gostergesi
        // (SpawnRouteGuide) zaten var ama o zeminde duruyor ve oyuncunun bakis acisina
        // bagli; bu ok yazinin hemen altinda, KAFAYA KILITLI — kacirilmasi mumkun degil.
        Transform _arrow;
        Material _arrowMat;

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
            // FONT SART (bkz. UITheme.DefaultFont): bu olmadan olum/dogum yazisi Quest'te
            // hic cizilmiyordu — oyuncu neden olu bekledigini goremiyordu.
            UITheme.ApplyFont(_text);
            _text.characterSize = 0.06f;
            _text.fontSize = 60;
            _text.anchor = TextAnchor.MiddleCenter;
            _text.alignment = TextAlignment.Center;
            _text.color = Color.white;
            t.transform.localScale = Vector3.one * 0.16f;

            // Ok HUD elemani: gri perdenin (kuyruk 3000, kafaya 0.12 m) USTUNDE cizilsin ve
            // duvar tarafindan ortulmesin — bu yuzden overlay malzeme + yuksek kuyruk.
            _arrow = UITheme.MakeShape(transform, "Direction Arrow", UIMesh.Arrow(), Color.white, 3050);
            _arrowMat = _arrow.GetComponent<MeshRenderer>().sharedMaterial;

            gameObject.SetActive(false);
        }

        /// <summary>PlayerHUD'daki ozel font secimi burada da gecerli olsun (bos = varsayilan).
        /// Materyal de degismeli: yalnizca tm.font yazmak yaziyi ESKI fontun atlasiyla cizmeye
        /// devam ettirir (yanlis glifler / bos kutu).</summary>
        public void SetFont(Font f)
        {
            if (f == null || _text == null) return;
            _text.font = f;
            var mr = _text.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = f.material;
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

            // Perde hepsinin ONUNDE (katman 0) — vignette/hasar flasiyla z-cakismasin.
            // Mesafe HeadOverlay'den: sabit 0.45 m durbun merceginin tam sinirindaydi
            // (WeaponScope 0.45 m'den yakinda aciliyor), yani durbune bakan olu oyuncu
            // perdeyi hic gormeyebiliyordu.
            _veil.SetPositionAndRotation(
                head.position + head.forward * HeadOverlay.Distance(HeadOverlay.Veil), head.rotation);

            // Yazi biraz daha uzakta — VR'da cok yakin metin okunmaz.
            _text.transform.position = head.position + head.forward * 1.2f;
            _text.transform.rotation = Quaternion.LookRotation(_text.transform.position - head.position);

            UpdateArrow(head);
        }

        /// <summary>
        /// Yazinin altindaki pusula oku: hedefin BAGIL yonunu gosterir.
        /// ileri = yukari, sag = saga, ARKA = asagi.
        ///
        /// Neden ekran duzleminde (pusula) ve neden dunya yonunde bir 3B ok degil: goz
        /// hizasinda duran yatay bir ok, hedef tam onde ya da tam arkadayken kameraya UCUNDAN
        /// bakildigi icin cizgiye doner ve yon okunamaz. Ekran duzleminde donen ok hicbir acida
        /// bozulmaz.
        ///
        /// Yerdeki rota/ok (bkz. <see cref="SpawnRouteGuide"/>) mekansal ipucu verir; bu ok ise
        /// GARANTI gorunur — oyuncu zaten bu yaziyi okuyor.
        /// </summary>
        void UpdateArrow(Transform head)
        {
            if (_arrow == null) return;

            if (_zone == null)
            {
                if (_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(false);
                return;
            }
            if (!_arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(true);

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 to = _zone.transform.position - head.position; to.y = 0f;
            float bearing = to.sqrMagnitude > 0.0025f
                ? Vector3.SignedAngle(fwd, to.normalized, Vector3.up)
                : 0f;

            // Yazinin bir tik altinda, ayni duzlemde.
            Vector3 pos = _text.transform.position - _text.transform.up * 0.115f;
            _arrow.position = pos;

            // Panel donusu + ekran duzleminde bearing kadar cevir. Ok mesh'i +X'e baktigi icin
            // once 90 derece ile yukari cevriliyor (bearing 0 = ileri = yukari).
            _arrow.rotation = _text.transform.rotation * Quaternion.Euler(0f, 0f, 90f - bearing);
            _arrow.localScale = Vector3.one * 0.085f;

            Color c = _zone.TeamColor;
            float blink = 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(Time.time * 1.6f * Mathf.PI * 2f));
            UITheme.SetMaterialColor(_arrowMat, new Color(c.r, c.g, c.b, blink));
        }
    }
}
