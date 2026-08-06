using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Elenen oyuncuyu kendi dogum bolgesine goturen ZEMIN ROTASI: takim renginde, hedefe
    /// dogru akan bir cizgi ve her donus noktasinda bir ok.
    ///
    /// NEDEN HEDEFE BAKAN OK DEGIL: bu oyunda iki dogum bolgesi AYRI ODALARDA ve aralarinda
    /// kapili bir duvar var. Hedefi dogrudan gosteren bir ok duvari deler ve oyuncuyu duvara
    /// yollar. Cozum yolu <see cref="NavMesh"/> uzerinden hesaplamak:
    /// <see cref="NavMeshPath.corners"/> huni (funnel) algoritmasiyla uretilir ve ARDISIK IKI
    /// KOSE BIRBIRINI HER ZAMAN GORUR. Yani rotanin hicbir parcasi tanim geregi duvarin
    /// icinden gecemez.
    ///
    /// NEDEN ZEMINDE: oyuncu olurken gercek odada yuruyor ve ekraninda gri perde var. Yere
    /// cizilen rota bakisi yurume yonunde tutar; havada duran bir ok basi kaldirtir ve
    /// ayaginin altini gostermez. Passthrough'da da AR navigasyonunun dogal dili budur.
    ///
    /// DERINLIK TESTLI cizilir — HUD panellerinin aksine. Kosenin arkasinda kalan kisim
    /// duvarin ardinda kaybolsun: "buradan dolasacaksin" hissini asil veren sey odur.
    ///
    /// IKI MOD, otomatik secilir:
    ///  - ROTA: yurunebilir alan varsa ve hedefe TAM yol cikiyorsa, zeminde akan cizgi.
    ///  - YON OKU: yol yoksa (NavMesh bake edilmemis, bolge yurunebilir alanin disinda, ya da
    ///    iki oda birbirine bagli degil) oyuncunun onunde hedefi gosteren KISA bir ok. Yon
    ///    kus ucusudur; oyuncu duvari kendi dolasir. Ok bilerek KISA tutulur — uzun duz bir
    ///    cizgi "tam buradan yuru" der ve duvari deldiginde sacma durur, kisa ok ise yalnizca
    ///    "o taraf" der. Ok AYAK DIBINE YAKIN durur ve bakisi takip eder; gorus hatti
    ///    engelliyse engelin berisine cekilir (bkz. <see cref="DrawArrow"/>).
    ///
    /// Boylece ozellik bugun de calisir; sahne duzelince rota kendiliginden devreye girer.
    ///
    /// YEREL ve sahibe ozeldir: <see cref="PlayerHUD"/> uretir, herkes yalnizca kendi
    /// rotasini gorur. Sahneye/prefaba dokunmaz.
    /// </summary>
    public class SpawnRouteGuide : MonoBehaviour
    {
        // Yol her karede degil, bu araliklarla hesaplanir. "Oyuncu hareket etmedikce hesaplama"
        // gibi ek bir kisit DENENDI ve KALDIRILDI: oyuncu dururken hedef degistiginde (takim
        // atamasi geldiginde) rota eski hedefe kilitli kaliyordu. Kazanci 4 Hz'de tek bir
        // CalculatePath'ti — dogruluga degmez.
        [Tooltip("Yol kac saniyede bir yeniden hesaplanir.")]
        public float refreshInterval = 0.25f;

        [Tooltip("Cizgi kalinligi (metre).")]
        public float lineWidth = 0.07f;
        [Tooltip("Zeminden yukseklik — z-fighting olmasin.")]
        public float groundOffset = 0.035f;
        [Tooltip("Akan tirelerin dunyadaki araligi (metre).")]
        public float dashSpacing = 0.45f;
        [Tooltip("Tirelerin akma hizi (metre/saniye).")]
        public float flowSpeed = 0.9f;

        [Tooltip("Oyuncunun ayagi dibinden baslamasin diye rotanin bu kadari kirpilir (metre).")]
        public float startTrim = 0.35f;
        [Tooltip("Bolgeye bu mesafede tamamen sonumlenir (metre).")]
        public float fadeDistance = 1.6f;

        [Header("Yon oku (yol bulunamadiginda)")]
        [Tooltip("Ok oyuncuya bu mesafeden yakin durmaz (metre). Asagi bakildiginda okun " +
                 "gelecegi yer: ayaklarin biraz onu.")]
        public float arrowNearDistance = 0.85f;
        [Tooltip("Ok bu mesafeden uzaga GITMEZ (metre). Buyutme: uzak ok duvarin ardina " +
                 "dusup tamamen kayboluyor — bkz. DrawArrow.")]
        public float arrowFarDistance = 2.4f;
        [Tooltip("Okun boyu (metre).")]
        public float arrowSize = 0.8f;
        [Tooltip("Yanip sonme hizi (saniyedeki tam donus).")]
        public float blinkHz = 1.6f;

        [Range(0f, 1f)] public float lineAlpha = 0.85f;

        const int MaxChevrons = 6;

        // Olum perdesi (RespawnGuide) saydam kuyruk 3000'de ve kafaya 0.12 m'de duruyor; ayni
        // kuyrukta saydamlar mesafeye gore siralandigi icin gosterge onun ALTINDA kalip gri
        // perdeyle solardi. Kuyrugu yukseltmek yalnizca CIZIM SIRASINI degistirir, derinlik
        // testini DEGISTIRMEZ: duvarlar (opak, derinlik yazan) gostergeyi ortmeye devam eder.
        const int Queue = 3050;

        LineRenderer _line;
        Material _lineMat;
        readonly List<Transform> _chevrons = new List<Transform>();
        readonly List<Material> _chevronMats = new List<Material>();
        Transform _arrow;
        Material _arrowMat;

        NavMeshPath _path;
        readonly List<Vector3> _points = new List<Vector3>();

        bool _active;
        byte _team;
        float _nextRefresh;
        float _flow;
        float _visible;          // 0..1 sonumleme
        bool _warnedNoNavMesh;

        static Texture2D _dash;

        void Awake()
        {
            _path = new NavMeshPath();

            // Serit YERE YATIK dursun: LineAlignment.TransformZ seridi transformun ILERI
            // eksenine bakacak sekilde cevirir, o yuzden ileri = +Y yapiliyor.
            transform.rotation = Quaternion.Euler(-90f, 0f, 0f);

            var go = new GameObject("Route Line");
            go.transform.SetParent(transform, false);
            _line = go.AddComponent<LineRenderer>();
            _line.useWorldSpace = true;
            _line.alignment = LineAlignment.TransformZ;
            _line.textureMode = LineTextureMode.Stretch;
            _line.numCapVertices = 2;
            _line.numCornerVertices = 2;
            _line.widthMultiplier = lineWidth;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;

            // Overlay DEGIL: bu cizgi duvarin arkasinda kalmali (bkz. sinif notu).
            _lineMat = UITheme.CreateTransparentMaterial(Color.white);
            _lineMat.renderQueue = Queue;
            SetTexture(_lineMat, DashTexture);
            _line.sharedMaterial = _lineMat;

            for (int i = 0; i < MaxChevrons; i++)
            {
                var c = UITheme.MakeShape(transform, "Chevron " + i, UIMesh.Play(), Color.white);
                var mr = c.GetComponent<MeshRenderer>();
                // Chevron da derinlik testli olmali; MakeShape overlay veriyor, degistiriyoruz.
                var m = UITheme.CreateTransparentMaterial(Color.white);
                m.renderQueue = Queue;
                // Cift tarafli: ucgenin sarim yonune bagimli kalmayalim, yerde her acidan gorunsun.
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                mr.sharedMaterial = m;
                _chevronMats.Add(m);
                c.gameObject.SetActive(false);
                _chevrons.Add(c);
            }

            var arrow = UITheme.MakeShape(transform, "Heading Arrow", UIMesh.Arrow(), Color.white);
            _arrowMat = UITheme.CreateTransparentMaterial(Color.white);
            _arrowMat.renderQueue = Queue;
            if (_arrowMat.HasProperty("_Cull")) _arrowMat.SetFloat("_Cull", 0f);
            arrow.GetComponent<MeshRenderer>().sharedMaterial = _arrowMat;
            arrow.gameObject.SetActive(false);
            _arrow = arrow;

            gameObject.SetActive(false);
        }

        /// <summary>Her kare <see cref="PlayerHUD"/> tarafindan beslenir.</summary>
        /// <param name="waiting">Oyuncu olu / dogum bekliyor mu?</param>
        /// <param name="team">Takimi (0 = henuz secilmedi).</param>
        /// <param name="inZone">Sunucuya gore cemberin icinde mi?</param>
        public void SetState(bool waiting, byte team, bool inZone)
        {
            _team = team;
            bool guiding = waiting && team != 0 && !inZone;

            // Yurunebilir alan yoksa SESSIZ kalmiyoruz: gosterge yon okuna duser ve bu bilincli
            // bir dususmedir, gelistirici eksik bake'i gorebilmeli. Uyari erken cikar
            // (asagidaki erken donusten ONCE), yoksa durum hic degismedigi icin hic yazilmazdi.
            if (guiding && !RoomNavMesh.Loaded && !_warnedNoNavMesh)
            {
                _warnedNoNavMesh = true;
                Debug.LogWarning("[SpawnRouteGuide] Yurunebilir alan yok — zemin rotasi yerine " +
                                 "YON OKU gosteriliyor. Tam rota icin Tools > VR Multiplayer > 23 " +
                                 "ile NavMesh bake al.");
            }

            // Bolgenin ICINDE gosterge anlamsiz — oyuncu zaten varmis, geri sayimi izliyor.
            // NavMesh SART DEGIL: yol cikmazsa yon okuna dusulur.
            bool want = guiding;
            if (_active == want) return;

            _active = want;
            if (want)
            {
                gameObject.SetActive(true);
                _nextRefresh = 0f;
            }
            // Kapanisi LateUpdate yapar: sonumlenerek kaybolsun, birden yok olmasin.
        }

        void LateUpdate()
        {
            // Sonumleme: acilirken belirir, kapanirken soner ve sonra tamamen kapanir.
            _visible = Mathf.MoveTowards(_visible, _active ? 1f : 0f, Time.deltaTime / 0.25f);
            if (_visible <= 0f)
            {
                if (gameObject.activeSelf && !_active) gameObject.SetActive(false);
                return;
            }

            var zone = TeamSpawnZone.For(_team);
            Transform head = XRRigReference.HeadOrCamera;
            if (zone == null || head == null) { Hide(); return; }

            Vector3 from = head.position;
            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + refreshInterval;
                Recalculate(from, zone.transform.position);
            }

            // Hedefe yaklasinca sonumlensin: dibinde cizgi/ok cizmek gorusu kirletir.
            float dist = zone.HorizontalDistance(from);
            float near = Mathf.Clamp01((dist - zone.radius) / Mathf.Max(0.1f, fadeDistance));
            float a = _visible * near * lineAlpha;
            if (a <= 0.01f) { Hide(); return; }

            Color c = _team == 2 ? PlayerIdentity.TeamBColor : PlayerIdentity.TeamAColor;
            c = Color.Lerp(c, Color.white, 0.25f);   // zeminde biraz daha parlak okunsun

            // TAM yol varsa rota, yoksa yon oku. Karar her karede yeniden verilir: oyuncu
            // yurunebilir alana girdiginde gosterge kendiliginden rotaya terfi eder.
            if (_points.Count >= 2)
            {
                HideArrow();
                Draw(c, a);
            }
            else
            {
                HideRoute();
                DrawArrow(zone, head, c, a);
            }
        }

        void Hide()
        {
            HideRoute();
            HideArrow();
        }

        void HideRoute()
        {
            if (_line != null) _line.enabled = false;
            for (int i = 0; i < _chevrons.Count; i++)
                if (_chevrons[i].gameObject.activeSelf) _chevrons[i].gameObject.SetActive(false);
        }

        void HideArrow()
        {
            if (_arrow != null && _arrow.gameObject.activeSelf) _arrow.gameObject.SetActive(false);
        }

        /// <summary>
        /// Yedek gosterge: zeminde duran, hedefi gosteren yanip sonen ok.
        ///
        /// Konum BAKIS yonunde, DONUS hedefe dogru: boylece ok her zaman gorunur kalir ve hedef
        /// arkadayken geriyi gosterir — "arkani don" mesaji bedavaya gelir. Okun hedefin
        /// YONUNDE konumlandirilmasi denenirse hedef arkadayken ok da arkada kalir ve oyuncu
        /// hicbir sey gormez.
        ///
        /// MESAFE BAKISI TAKIP EDER — iki eski yaklasim da tek basina calismiyordu:
        ///  - SABIT 1.1 m: kafa zeminden ~1.5 m yukarida oldugu icin ok goz hizasinin 54 derece
        ///    altina, Quest 3'un dikey gorus alaninin (merkezden ~48 derece) DISINA dusuyordu.
        ///  - SABIT ACI (25 derece): mesafeyi ~3.2 m'ye itiyordu ve ODA OLCEGINDE BU COK UZAK.
        ///    Ok derinlik testli ciziliyor (bkz. sinif notu), yani duvarin ardina dusen ok
        ///    kayboluyor — gosterge var ama gorunmuyor.
        ///
        /// Simdi ok, BAKISIN ZEMINE DUSTUGU NOKTAYA konuyor: oyuncu asagi baktikca ok
        /// ayaklarinin onune gelir (<see cref="arrowNearDistance"/>), duz baktikca gorus
        /// alaninin alt kenarina dogru acilir (<see cref="arrowFarDistance"/>). Sabit acinin
        /// cozdugu sey (her boyda gorunur olmak) korunuyor, cunku bakis zaten goz yuksekligini
        /// hesaba katiyor; kazanilan sey ise okun oyuncunun BAKTIGI yerde olmasi.
        ///
        /// GORUS HATTI GERCEKTEN OLCULUYOR: aday nokta ile goz arasi engelliyse ok engelin
        /// BERISINE cekilir (bkz. <see cref="VisibleSpan"/>). Eski kod bunun yerine goz
        /// hizasinda YATAY bir isin atiyordu — okun uzandigi cizgi o degil, dolayisiyla alcak
        /// bir siper ya da kapi bosuklu bir duvar yanlis cevap veriyordu.
        /// </summary>
        void DrawArrow(TeamSpawnZone zone, Transform head, Color c, float alpha)
        {
            Vector3 toTarget = zone.transform.position - head.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.0025f) { HideArrow(); return; }
            toTarget.Normalize();

            Vector3 fwd = head.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            float floorY = zone.transform.position.y;
            float dist = GazeGroundDistance(head, floorY);

            Vector3 eye = head.position;
            Vector3 target = eye + fwd * dist;
            target.y = floorY + groundOffset;

            // Engel varsa okun tamamini berisine tasi. Oran korunuyor: aday nokta gozden
            // duz bir cizgi uzerinde oldugu icin gorulebilen kismin orani, YATAY mesafenin
            // de ayni orani demek.
            float span = Vector3.Distance(eye, target);
            float visible = VisibleSpan(eye, target, span);
            if (visible < span && span > 0.01f)
            {
                dist = Mathf.Max(arrowNearDistance * 0.55f, dist * (visible / span));
                target = eye + fwd * dist;
                target.y = floorY + groundOffset;
            }

            if (!_arrow.gameObject.activeSelf)
            {
                _arrow.gameObject.SetActive(true);
                _arrow.position = target;   // ilk karede uzaktan kaymasin
            }

            // Sonumlu takip: kafayla birlikte sicramasin.
            float k = 1f - Mathf.Exp(-8f * Time.deltaTime);
            _arrow.position = Vector3.Lerp(_arrow.position, target, k);

            // Yuzu YUKARI baksin (ileri = +Y), sonra ucu hedefe cevrilsin.
            _arrow.rotation = Quaternion.LookRotation(Vector3.up, toTarget) * Quaternion.Euler(0f, 0f, 90f);

            // Uzaklikla birlikte buyut: uzaktaki ok yakindakiyle ayni DUNYA boyunda cizilirse
            // gorunurde kucucuk kalir. Olcek GORUNEN buyuklugu sabit tutmaya calisiyor.
            float s = arrowSize * Mathf.Clamp(dist / 2.0f, 0.55f, 1.6f);
            _arrow.localScale = new Vector3(s, s, 1f);

            // Belirgin YANIP SONME (istenen buydu): silik bir nabiz degil, acik/kapali salinim.
            float blink = 0.35f + 0.65f * (0.5f + 0.5f * Mathf.Sin(Time.time * blinkHz * Mathf.PI * 2f));
            UITheme.SetMaterialColor(_arrowMat, new Color(c.r, c.g, c.b, alpha * blink));
        }

        /// <summary>
        /// Bakisin ZEMINE dustugu yatay mesafe, <see cref="arrowNearDistance"/> ..
        /// <see cref="arrowFarDistance"/> arasina kirpilmis.
        ///
        /// Kafa duz ya da YUKARI bakiyorsa zeminle kesisim yoktur (ya da arkada kalir); o
        /// durumda en uzak degere sabitlenir — ok gorus alaninin alt kenarinda bekler ve
        /// oyuncu basini birazcik egdiginde onune gelir.
        /// </summary>
        float GazeGroundDistance(Transform head, float floorY)
        {
            Vector3 gaze = head.forward;                       // normalize (Transform.forward)
            float drop = head.position.y - floorY;

            // -0.05: neredeyse duz bakista bolme patlar ve mesafe firlar; o bant "duz bakiyor".
            if (gaze.y > -0.05f || drop <= 0.05f) return arrowFarDistance;

            float t = drop / -gaze.y;                          // isinin zemini deldigi parametre
            float horizontal = new Vector2(gaze.x, gaze.z).magnitude * t;
            return Mathf.Clamp(horizontal, arrowNearDistance, arrowFarDistance);
        }

        // Gorus hatti sorgusunun tamponu. RaycastAll her cagrida dizi ayirirdi; bu metot
        // dogum bekleyen oyuncuda her kare kosuyor.
        static readonly RaycastHit[] _losHits = new RaycastHit[16];

        /// <summary>Zemine bu kadar kala isin kesilir — yoksa isin hedef noktada ZEMININ
        /// KENDISINI vurur ve ok kendi durdugu yeri "engel" sanip surekli geri kacar.</summary>
        const float FloorMargin = 0.25f;

        /// <summary>Engelin oyuncuya bu kadar berisine cekilir. Okun KENDI boyu var (~0.8 m):
        /// merkezi duvarin tam dibine oturursa uc kisim duvarin ayak izine girer ve orada
        /// kirpilir. Tamamen kurtarmak icin okun yarisi kadar geri cekmek gerekirdi, o da
        /// oyuncuyu gereksiz yere geriden yonlendirirdi — bu ara deger yeterli.</summary>
        const float BlockerMargin = 0.30f;

        /// <summary>
        /// Gozden <paramref name="target"/> noktasina KAC METRE gorulebiliyor. Engel yoksa
        /// <paramref name="span"/> aynen doner.
        ///
        /// OYUNCUNUN KENDI GOVDESI ENGEL SAYILMAZ: isin gozden cikiyor, yani oyuncunun kendi
        /// vurus bolgelerinin (<see cref="HitZone"/>) TAM ICINDEN basliyor. Filtrelenmezse
        /// her karede "hemen onumde duvar var" cevabi gelir ve ok ayaklarina yapisir.
        /// (Ele alinmis silah oyuncu kokune bagliysa o da elenir; bagli degilse zaten kucuk
        /// bir cisim ve isin ondan asagi dogru gidiyor.)
        /// </summary>
        static float VisibleSpan(Vector3 eye, Vector3 target, float span)
        {
            float probe = span - FloorMargin;
            if (probe <= 0.05f) return span;

            Vector3 dir = (target - eye) / span;
            int n = Physics.RaycastNonAlloc(eye, dir, _losHits, probe,
                                            Physics.AllLayers, QueryTriggerInteraction.Ignore);
            if (n <= 0) return span;

            Transform self = PlayerIdentity.Local != null ? PlayerIdentity.Local.transform : null;

            // RaycastNonAlloc SIRALI DEGIL — en yakin engeli elle ariyoruz.
            float nearest = -1f;
            for (int i = 0; i < n; i++)
            {
                var col = _losHits[i].collider;
                if (col == null) continue;
                if (self != null && col.transform.IsChildOf(self)) continue;

                float d = _losHits[i].distance;
                if (d <= 0.01f) continue;                     // isinin ciktigi noktadaki carpisma
                if (nearest < 0f || d < nearest) nearest = d;
            }

            if (nearest < 0f) return span;
            return Mathf.Max(0.1f, nearest - BlockerMargin);
        }

        // ------------------------------------------------------------------ yol

        void Recalculate(Vector3 from, Vector3 to)
        {
            _points.Clear();

            // Iki ucu da yurunebilir alana OTURT: kafa 1.7 m yukarida, bolge merkezi de
            // zeminin birkac cm ustunde olabilir.
            if (!NavMesh.SamplePosition(from, out var a, 3f, NavMesh.AllAreas)) return;
            if (!NavMesh.SamplePosition(to, out var b, 3f, NavMesh.AllAreas)) return;

            if (!NavMesh.CalculatePath(a.position, b.position, NavMesh.AllAreas, _path)) return;

            // YALNIZCA tam yol cizilir. Kismi yol hedefe ULASMIYOR demektir; cizmek oyuncuyu
            // cikmaz bir kosede birakirdi.
            if (_path.status != NavMeshPathStatus.PathComplete) return;

            var corners = _path.corners;
            if (corners.Length < 2) return;

            // Bas kirpma: cizgi ayaginin dibinden degil, bir adim onunden baslasin.
            float trim = startTrim;
            int start = 0;
            Vector3 first = corners[0];
            while (start < corners.Length - 1)
            {
                float seg = Vector3.Distance(first, corners[start + 1]);
                if (seg > trim)
                {
                    first = Vector3.Lerp(first, corners[start + 1], trim / seg);
                    break;
                }
                trim -= seg;
                start++;
                first = corners[start];
            }

            _points.Add(Lift(first));
            for (int i = start + 1; i < corners.Length; i++) _points.Add(Lift(corners[i]));
        }

        Vector3 Lift(Vector3 p) => new Vector3(p.x, p.y + groundOffset, p.z);

        // ------------------------------------------------------------------ cizim

        void Draw(Color c, float alpha)
        {
            _line.enabled = true;
            _line.positionCount = _points.Count;
            for (int i = 0; i < _points.Count; i++) _line.SetPosition(i, _points[i]);
            _line.widthMultiplier = lineWidth;

            float length = 0f;
            for (int i = 1; i < _points.Count; i++)
                length += Vector3.Distance(_points[i - 1], _points[i]);

            // Tireler DUNYA olceginde sabit kalsin: UV 0..1 tum cizgiye yayildigi icin
            // tekrar sayisi uzunluga gore ayarlanir, yoksa kisa yolda tireler devlesirdi.
            float tiling = Mathf.Max(1f, length / Mathf.Max(0.05f, dashSpacing));
            _flow -= flowSpeed * Time.deltaTime / Mathf.Max(0.05f, dashSpacing);
            _flow = Mathf.Repeat(_flow, 1f);

            SetTiling(_lineMat, tiling, _flow);
            UITheme.SetMaterialColor(_lineMat, new Color(c.r, c.g, c.b, alpha));

            DrawChevrons(c, alpha);
        }

        // Donus noktalarina ok: akan tireler "ne tarafa" sorusunu cevaplar, chevron ise
        // "burada donuyorsun" der.
        void DrawChevrons(Color c, float alpha)
        {
            int used = 0;
            for (int i = 1; i < _points.Count - 1 && used < _chevrons.Count; i++)
            {
                Vector3 dir = _points[i + 1] - _points[i];
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0004f) continue;

                var t = _chevrons[used];
                if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
                t.position = _points[i] + Vector3.up * 0.002f;
                // Ucgen XY duzleminde ve +X'e bakiyor. Yuzu YUKARI baksin diye ileri = +Y
                // (asagi verilirse ucgen zemine sirtini doner, ustten gorunmez); sonra yerel
                // Z etrafinda 90 derece ile ucun yol yonune gelir.
                t.rotation = Quaternion.LookRotation(Vector3.up, dir.normalized)
                           * Quaternion.Euler(0f, 0f, 90f);
                t.localScale = new Vector3(lineWidth * 2.6f, lineWidth * 2.6f, 1f);
                UITheme.SetMaterialColor(_chevronMats[used], new Color(c.r, c.g, c.b, alpha));
                used++;
            }

            for (int i = used; i < _chevrons.Count; i++)
                if (_chevrons[i].gameObject.activeSelf) _chevrons[i].gameObject.SetActive(false);
        }

        // ------------------------------------------------------------------ doku

        // URP/Unlit _BaseMap, yedek zincirdeki eski shader'lar _MainTex kullanir.
        static void SetTexture(Material m, Texture t)
        {
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", t);
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", t);
        }

        static void SetTiling(Material m, float tiling, float offset)
        {
            var s = new Vector2(tiling, 1f);
            var o = new Vector2(offset, 0f);
            if (m.HasProperty("_BaseMap")) { m.SetTextureScale("_BaseMap", s); m.SetTextureOffset("_BaseMap", o); }
            if (m.HasProperty("_MainTex")) { m.SetTextureScale("_MainTex", s); m.SetTextureOffset("_MainTex", o); }
        }

        /// <summary>Yumusak kenarli tire deseni — kaydirildiginda "akan ok" hissi verir.</summary>
        static Texture2D DashTexture
        {
            get
            {
                if (_dash != null) return _dash;

                const int W = 64;
                _dash = new Texture2D(W, 1, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Repeat,
                    filterMode = FilterMode.Bilinear,
                };
                for (int x = 0; x < W; x++)
                {
                    float t = x / (float)W;
                    float rise = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.00f, 0.14f, t));
                    float fall = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.62f, 0.48f, t));
                    _dash.SetPixel(x, 0, new Color(1f, 1f, 1f, rise * fall));
                }
                _dash.Apply();
                return _dash;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => _dash = null;
    }
}
