using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Elenen (ya da henuz oyuna girmemis) oyuncunun ekrani: KENAR vinyeti + oyunun giris
    /// ekraniyla ayni dilde tasarlanmis bir durum karti + hedefi gosteren pusula oku +
    /// bolgede beklerken dolan ilerleme halkasi.
    ///
    /// GORUSUN ORTASI ACIK KALIR, bilerek. Bu oyun kolokasyonlu: olu oyuncu dogum bolgesine
    /// GERCEK odada yuruyor. Eskiden tum gorusu kaplayan gri bir perde vardi; perde acik tonlu
    /// tutulmustu ama yine de tam ortayi orttugu icin hem prototip duruyor hem yururken
    /// gorusu kirletiyordu. Yerine KENAR vinyeti kondu: kenarlarda kararma, merkez tertemiz.
    /// Hem daha guvenli hem daha tasarlanmis.
    ///
    /// KARARTMAYI ARTIRMAK ISTERSEN DUR: bu ekranla oyuncu gercek bir odada yuruyor. Karanlik
    /// bir ekran onu gercek bir masaya ya da baska bir oyuncuya carptirir. Vinyet renginin
    /// alfasi (<see cref="alpha"/>) bu yuzden dusuk.
    ///
    /// Gercek doygunluk dusurme (desaturation) bir post-process Volume ister; projede hic
    /// post-process yok, tek efekt icin o yigini kurmaya degmez.
    ///
    /// <see cref="PlayerHUD"/> tarafindan olusturulur ve her kare <see cref="SetState"/> ile
    /// beslenir. Sahnedeki <see cref="TeamSpawnZone"/> halkasinin vurgusunu da bu surer.
    /// </summary>
    public class RespawnGuide : MonoBehaviour
    {
        [Tooltip("Kenar vinyetinin opakligi. YUKSELTIRKEN DIKKAT: oyuncu bu ekranla gercek " +
                 "odada yuruyor, merkez acik kalmali.")]
        [Range(0f, 0.9f)] public float alpha = 0.55f;

        [Tooltip("Kartin kafadan uzakligi (metre).")]
        public float cardDistance = 1.25f;
        [Tooltip("Kartin gorus merkezine gore ASAGI acisi (derece) — nisan hattini kapatmasin.")]
        [Range(5f, 35f)] public float cardAngle = 18f;

        // Olum = koyu kirmizi, ilk dogus = takim rengi. Oyuncu bakmadan hangi durumda
        // oldugunu ayirt eder.
        static readonly Color DeathTint = new Color(0.55f, 0.05f, 0.04f);

        // Giris ekraniyla (PlayerEntryPanel) ayni palet.
        static readonly Color CardFill  = new Color(0.027f, 0.047f, 0.071f, 0.93f);
        static readonly Color CardEdge  = new Color(0.40f, 0.80f, 0.72f, 0.55f);
        static readonly Color TitleDead = new Color(0.95f, 0.42f, 0.40f);
        static readonly Color TitleWait = new Color(0.45f, 0.88f, 0.84f);
        static readonly Color BodyCol   = new Color(0.80f, 0.86f, 0.90f);
        static readonly Color MutedCol  = new Color(0.48f, 0.56f, 0.62f);
        static readonly Color DistCol   = new Color(0.92f, 0.96f, 0.98f);
        static readonly Color RingDim   = new Color(0.20f, 0.28f, 0.33f);

        const int QCard = 3040, QText = 3052;   // olum vinyeti/perde 3000'de kalir, kart ustunde
        const float CardW = 0.50f, CardH = 0.29f;
        const int RingSegments = 48;

        Transform _vignette;
        Material _vignetteMat;

        Transform _card;
        TextMesh _title, _killer, _body, _distance, _count;
        Transform _arrow;
        Material _arrowMat;
        LineRenderer _ringBg, _ringFill;
        Material _ringBgMat, _ringFillMat;

        bool _active;
        bool _hasLived;          // bir kez canlandi mi? ilk dogus ile olumu bu ayirir
        string _killerName;      // beni en son kim oldurdu (bilinmiyorsa bos)
        TeamSpawnZone _zone;
        byte _team;
        Font _font;

        void Awake()
        {
            _vignette = UITheme.MakeVignetteQuad(transform, "Death Vignette", DeathTint, out _vignetteMat);

            _card = new GameObject("Status Card").transform;
            _card.SetParent(transform, false);
            BuildCard();

            PlayerHealth.KillReported += OnKillReported;
            gameObject.SetActive(false);
        }

        void OnDestroy()
        {
            PlayerHealth.KillReported -= OnKillReported;
            // Perde kapanirken sahnedeki halka vurgulu kalmasin.
            if (_zone != null) _zone.SetLocalState(false, 0f);
        }

        // Katil adi zaten sunucudan geliyor (kill paneli isinde acildi) — olum ekraninin en
        // cok merak edilen sorusuna bedavaya cevap veriyor.
        void OnKillReported(KillInfo info)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || info.VictimId != nm.LocalClientId) return;
            _killerName = info.Kind == 0 ? info.Killer : string.Empty;
        }

        void BuildCard()
        {
            UITheme.MakeRounded(_card, "Card Border", Vector2.zero,
                new Vector2(CardW, CardH), 0.018f, CardEdge, 0.004f, QCard);
            UITheme.MakeRounded(_card, "Card Fill", Vector2.zero,
                new Vector2(CardW - 0.005f, CardH - 0.005f), 0.016f, CardFill, 0.003f, QCard + 1);

            _title    = Text("", TitleDead, 0.046f, new Vector3(0f, 0.088f, 0f));
            _killer   = Text("", MutedCol,  0.022f, new Vector3(0f, 0.042f, 0f));
            _body     = Text("", BodyCol,   0.025f, new Vector3(0f, -0.006f, 0f));
            _distance = Text("", DistCol,   0.040f, new Vector3(0.035f, -0.086f, 0f));
            _count    = Text("", DistCol,   0.048f, new Vector3(0f, -0.070f, 0f));

            _arrow = UITheme.MakeShape(_card, "Direction Arrow", UIMesh.Arrow(), Color.white, QText);
            _arrowMat = _arrow.GetComponent<MeshRenderer>().sharedMaterial;
            _arrow.localPosition = new Vector3(-0.10f, -0.086f, 0f);
            _arrow.localScale = Vector3.one * 0.075f;

            // Halka: LineRenderer yayi — TeamSpawnZone zeminde ayni teknigi kullaniyor, mesh'i
            // her kare yeniden uretmeye gerek yok.
            _ringBg = Ring("Ring Bg", RingDim, 0.006f, out _ringBgMat);
            _ringFill = Ring("Ring Fill", TitleWait, 0.010f, out _ringFillMat);
            WriteArc(_ringBg, 1f);
        }

        TextMesh Text(string s, Color c, float h, Vector3 pos)
        {
            var tm = UITheme.MakeText(_card, s, c, h, TextAnchor.MiddleCenter, QText);
            tm.transform.localPosition = pos;
            return tm;
        }

        LineRenderer Ring(string name, Color c, float width, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_card, false);
            go.transform.localPosition = new Vector3(0f, -0.070f, 0f);
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.alignment = LineAlignment.TransformZ;
            lr.widthMultiplier = width;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            mat = UITheme.CreateOverlayMaterial(c);
            mat.renderQueue = QText;
            lr.sharedMaterial = mat;
            return lr;
        }

        static void WriteArc(LineRenderer lr, float fraction)
        {
            const float R = 0.052f;
            int used = Mathf.Max(2, Mathf.CeilToInt(RingSegments * Mathf.Clamp01(fraction)) + 1);
            lr.positionCount = used;
            for (int i = 0; i < used; i++)
            {
                // Tepeden baslar, saat yonunde dolar — geri sayim hissi boyle okunur.
                float a = Mathf.PI * 0.5f - (i / (float)RingSegments) * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(a) * R, Mathf.Sin(a) * R, 0f));
            }
        }

        /// <summary>PlayerHUD'daki ozel font secimi burada da gecerli olsun (bos = varsayilan).
        /// Materyal de degismeli: yalnizca tm.font yazmak yaziyi ESKI fontun atlasiyla cizmeye
        /// devam ettirir (yanlis glifler / bos kutu).</summary>
        public void SetFont(Font f)
        {
            if (f == null) return;
            _font = f;
            Apply(_title); Apply(_killer); Apply(_body); Apply(_distance); Apply(_count);
        }

        void Apply(TextMesh tm)
        {
            if (tm == null || _font == null) return;
            tm.font = _font;
            var mr = tm.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = _font.material;
        }

        /// <summary>Her kare PlayerHUD tarafindan cagrilir.</summary>
        /// <param name="waiting">Oyuncu olu / dogum bekliyor mu?</param>
        /// <param name="team">Oyuncunun takimi (0 = henuz secilmedi).</param>
        /// <param name="inZone">Sunucuya gore cemberin icinde mi?</param>
        /// <param name="progress01">Dogum geri sayiminin 0..1 ilerlemesi.</param>
        /// <param name="holdSeconds">Toplam bekleme suresi — kalan saniyeyi yazmak icin.</param>
        public void SetState(bool waiting, byte team, bool inZone, float progress01, float holdSeconds)
        {
            _team = team;

            // Takim secilmeden ekran ACILMAZ: o asamada oyuncu daha giris panelinde.
            bool want = waiting && team != 0;

            if (!waiting)
            {
                // Canlandi: bundan sonraki bekleyisler ILK DOGUS degil OLUM.
                _hasLived = true;
                _killerName = string.Empty;
            }

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

            Color tint = _hasLived ? DeathTint : TeamTint(team);
            UITheme.SetMaterialColor(_vignetteMat, new Color(tint.r, tint.g, tint.b, alpha));

            Fill(inZone, progress01, holdSeconds);
        }

        static Color TeamTint(byte team) =>
            team == 2 ? new Color(0.45f, 0.06f, 0.06f) : new Color(0.06f, 0.16f, 0.42f);

        // ------------------------------------------------------------------ metin

        void Fill(bool inZone, float progress01, float holdSeconds)
        {
            bool holding = inZone && _zone != null;

            _ringBg.enabled = holding;
            _ringFill.enabled = holding;
            _count.gameObject.SetActive(holding);
            _arrow.gameObject.SetActive(!holding);
            _distance.gameObject.SetActive(!holding);

            if (holding)
            {
                _title.text = "BÖLGENDESİN";
                _title.color = TitleWait;
                _killer.text = "";
                _body.text = "Hareketsiz bekle";

                WriteArc(_ringFill, Mathf.Clamp01(progress01));
                float left = Mathf.Max(0f, holdSeconds * (1f - Mathf.Clamp01(progress01)));
                _count.text = Mathf.CeilToInt(left).ToString();
                return;
            }

            if (_zone == null)
            {
                // Bolge kurulmamis; PlayerHealth guvenlik agiyla zamanli dogum yapiyor.
                _title.text = "YENİDEN DOĞULUYOR";
                _title.color = TitleWait;
                _killer.text = "";
                _body.text = "Bölge kurulmamış — bekle";
                _distance.text = Mathf.CeilToInt(
                    Mathf.Max(0f, holdSeconds * (1f - Mathf.Clamp01(progress01)))) + " sn";
                _arrow.gameObject.SetActive(false);
                return;
            }

            if (_hasLived)
            {
                _title.text = "ÖLDÜN";
                _title.color = TitleDead;
                _killer.text = string.IsNullOrEmpty(_killerName)
                    ? "" : "Seni " + _killerName + " öldürdü";
                _body.text = "Yeniden doğmak için takım bölgene git";
            }
            else
            {
                _title.text = "HAZIRLAN";
                _title.color = TitleWait;
                _killer.text = "";
                _body.text = "Doğmak için takım bölgene git";
            }

            _distance.text = _zone.HorizontalDistance(HeadPosition()).ToString("0.0") + " m";
        }

        static Vector3 HeadPosition()
        {
            var head = XRRigReference.HeadOrCamera;
            return head != null ? head.position : Vector3.zero;
        }

        // ------------------------------------------------------------------ yerlesim

        void LateUpdate()
        {
            if (!_active) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            // Vinyet kafaya kilitli. Mesafe HeadOverlay'den: sabit deger durbun merceginin
            // arkasinda kalabiliyordu. Quad olcegi mesafeyle AYNI oranda kuculur, yoksa
            // dokunun aci esleme kalibrasyonu (bkz. UITheme.VignetteTexture) kayar.
            float d = HeadOverlay.Distance(HeadOverlay.Veil);
            _vignette.SetPositionAndRotation(head.position + head.forward * d, head.rotation);
            float s = 2f * (d / 0.52f);
            _vignette.localScale = new Vector3(s, s, 1f);

            // Kart nisan hattinin ALTINDA: yururken onunu kapatmasin.
            Vector3 fwd = head.forward;
            Vector3 pos = head.position
                        + fwd * cardDistance
                        - head.up * (cardDistance * Mathf.Tan(cardAngle * Mathf.Deg2Rad));
            _card.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, head.up));

            UpdateArrow(head);
        }

        /// <summary>
        /// Pusula oku: hedefin BAGIL yonu — ileri = yukari, sag = saga, ARKA = asagi.
        ///
        /// Neden ekran duzleminde donen bir ok, neden dunya yonunde duran 3B bir ok degil:
        /// goz hizasinda duran yatay bir ok, hedef tam onde ya da tam arkadayken kameraya
        /// UCUNDAN goruldugu icin cizgiye doner ve yon okunamaz. Ekran duzleminde donen ok
        /// hicbir acida bozulmaz.
        /// </summary>
        void UpdateArrow(Transform head)
        {
            if (_arrow == null || !_arrow.gameObject.activeSelf || _zone == null) return;

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 to = _zone.transform.position - head.position; to.y = 0f;
            float bearing = to.sqrMagnitude > 0.0025f
                ? Vector3.SignedAngle(fwd, to.normalized, Vector3.up)
                : 0f;

            // Ok mesh'i +X'e bakiyor; 90 derece ile yukari cevrilir (bearing 0 = ileri).
            _arrow.localRotation = Quaternion.Euler(0f, 0f, 90f - bearing);

            Color c = _zone.TeamColor;
            float blink = 0.4f + 0.6f * (0.5f + 0.5f * Mathf.Sin(Time.time * 1.6f * Mathf.PI * 2f));
            UITheme.SetMaterialColor(_arrowMat, new Color(c.r, c.g, c.b, blink));
        }
    }
}
