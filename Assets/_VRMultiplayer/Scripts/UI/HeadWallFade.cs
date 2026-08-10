using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// ODA-OLCEGI HILESININ KAPISI: oyuncu gercek hayatta yuruyup kafasini sanal bir duvarin
    /// icine sokunca ekran karartilir; kafasini geri cekince acilir. Kafa geometrinin ICINDEYSE
    /// tam siyah, YAKININDAYSA mesafeyle orantili kararma.
    ///
    /// NEDEN GEREKLI: VR'da oyuncunun kafasi fiziksel olarak durdurulamaz — kumandayla degil
    /// gercek adimiyla ilerliyor. Yani "duvara carpip durmak" diye bir sey yok, kafa duvarin
    /// obur tarafina gecer ve kamera siperin arkasini bedavaya gorur. Cozum kafayi durdurmak
    /// degil, GORUNTUYU kesmek.
    ///
    /// TASARIM NOTLARI:
    ///  - Kararma HIZLI, acilma YAVAS (bkz. fadeInSpeed / fadeOutSpeed). Ters olsaydi oyuncu
    ///    kafasini ritmik olarak duvara sokup cikararak aradaki gri karelerden bilgi sizdirirdi.
    ///  - ZEMIN VE TAVAN sayilmaz (bkz. ignoreFloorAndCeiling): oyuncu comelince/yere yatinca
    ///    kafa zemine yaklasir, o bir hile degil mesru bir siper alma hareketidir.
    ///  - Sorgu QueryTriggerInteraction.Ignore ile yapilir; oyuncu hitbox'lari trigger oldugu
    ///    icin baska bir oyuncuya sokulmak ekrani karartmaz (bkz. <see cref="WorldSolids"/>).
    ///
    /// KURULUM: sahneye elle koymak gerekmez — PlayerHUD.BuildHud digerleri gibi yaratir.
    /// </summary>
    public class HeadWallFade : MonoBehaviour
    {
        [Tooltip("Kararmanin BASLADIGI mesafe (m). Kafa bir duvara bundan yakinsa ekran soluklasir. " +
                 "ASIL AYAR DUGMESI BU: buyutursen siperin dibinde duran oyuncu da rahatsiz olur, " +
                 "kucultursen kafa duvara iyice girmeden kararma baslamaz.")]
        public float fadeStartDistance = 0.25f;

        [Tooltip("TAM SIYAH mesafesi (m). Kafa duvara bundan yakinsa ya da icindeyse ekran kapanir.")]
        public float fullBlackDistance = 0.10f;

        [Tooltip("Mesafe kac kademede olculur (her kademe bir kure sorgusu). 3 yeterli: " +
                 "yumusatma kademeleri zaten surekli bir rampaya cevirir.")]
        [Range(2, 5)] public int blackoutSteps = 3;

        [Tooltip("En yuksek opaklik. 1 = tam siyah. Test icin 0.9 yapip arkasini gorebilirsin.")]
        [Range(0f, 1f)] public float maxAlpha = 1f;

        [Tooltip("Kararma hizi (birim/sn). YUKSEK tutulur: kafa duvara girer girmez kesilsin.")]
        public float fadeInSpeed = 14f;

        [Tooltip("Acilma hizi (birim/sn). Kararmadan yavas olmali; goz yormasin diye yumusak.")]
        public float fadeOutSpeed = 6f;

        [Tooltip("Duvar sayilacak katmanlar. Projede ozel katman olmadigi icin varsayilan HEPSI; " +
                 "asil eleme WorldSolids'te (trigger + Rigidbody kurallari). Bir 'Duvar' katmani " +
                 "acilirsa burayi daraltmak sorguyu ucuzlatir.")]
        public LayerMask solidMask = ~0;

        [Tooltip("Zemin ve tavan kararmayi tetiklemesin (comelme/yere yatma mesru hareket).")]
        public bool ignoreFloorAndCeiling = true;

        [Tooltip("Kac karede bir olculur. 1 = her kare. 2 yapmak sorguyu yariya indirir; " +
                 "yumusatma zaten arada calistigi icin gorsel fark olusmaz.")]
        [Range(1, 4)] public int checkEveryNthFrame = 1;

        /// <summary>Dikeye bu kadar yakin temaslar zemin/tavan sayilir (dot esigi).</summary>
        const float VerticalDot = 0.75f;

        /// <summary>En genis kademenin (fadeStartDistance) alfa carpani. Duvar menzilin ucundayken
        /// ekran tamamen kapanmaz, sadece "yaklasiyorsun" der.</summary>
        const float MinStepAlpha = 0.3f;

        /// <summary>Bunun altindaki alfada quad tamamen kapatilir (bos draw call olmasin).</summary>
        const float MinVisibleAlpha = 0.004f;

        /// <summary>Ayni anda degerlendirilen collider tavani. Kafanin 35 cm'lik kuresine bundan
        /// fazlasi girerse zaten tam kararma bolgesindeyizdir.</summary>
        const int MaxOverlap = 16;

        static readonly Collider[] _hits = new Collider[MaxOverlap];

        Transform _quad;
        Material _mat;
        float _alpha;       // ekranda GORUNEN alfa (yumusatilmis)
        float _target;      // olcumun istedigi alfa
        int _frame;

        /// <summary>Su an ekran ne kadar kapali (0-1). Baska sistemler (orn. nisan yardimi,
        /// dusman isaretcileri) "kor durumdayim" bilgisini buradan okuyabilir.</summary>
        public float Blackout => _alpha;

        void Awake()
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = "Wall Fade Quad";
            Destroy(q.GetComponent<Collider>());
            q.transform.SetParent(transform, false);
            q.transform.localScale = new Vector3(2f, 2f, 1f);
            _mat = UITheme.CreateTransparentMaterial(Color.black);
            q.GetComponent<MeshRenderer>().sharedMaterial = _mat;
            _quad = q.transform;
            q.SetActive(false);
        }

        void LateUpdate()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            if (checkEveryNthFrame <= 1 || (++_frame % checkEveryNthFrame) == 0)
                _target = Measure(head.position);

            // Yumusatma: kararmaya hizli, acilmaya yavas kosar. Anlik gecis VR'da rahatsiz
            // eder; ayrica duvar kenarinda titreyen bir olcum ekrani caktirirdi.
            float speed = _target > _alpha ? fadeInSpeed : fadeOutSpeed;
            _alpha = Mathf.MoveTowards(_alpha, _target, speed * Time.deltaTime);

            bool visible = _alpha > MinVisibleAlpha;
            if (_quad.gameObject.activeSelf != visible) _quad.gameObject.SetActive(visible);
            if (!visible) return;

            // Her seyin ONUNDE: durbun mercegi, hasar flasi, olum perdesi dahil. Kararma bir
            // gorsel efekt degil bir KAPI — ustune hicbir sey cizilmemeli.
            transform.SetPositionAndRotation(
                head.position + head.forward * HeadOverlay.Distance(HeadOverlay.WallFade),
                head.rotation);
            UITheme.SetMaterialColor(_mat, new Color(0f, 0f, 0f, _alpha));
        }

        /// <summary>Hedef alfa: kafanin cevresinde KUCULEN yaricaplarla kure sorgusu yapilir,
        /// temas eden EN KUCUK yaricap ne kadar kucukse kararma o kadar koyudur.
        ///
        /// NEDEN ClosestPoint ILE MESAFE OLCMUYORUZ: convex olmayan MeshCollider'da (oda
        /// taramasinin zemini, dekor mesh'leri) ClosestPoint calismaz ve sinir kutusuna dusen
        /// her cozum "oyuncu odanin ortasinda ama kutunun icinde" diyerek ekrani KALICI
        /// karartabilirdi. Kure sorgusu ise ucgen mesh'lerde de dogru calisir: yalnizca gercek
        /// bir ucgen kureyi kesiyorsa temas bildirir. BoxCollider gibi hacimli sekillerde de
        /// kure merkezi icerideyken temas bildirir, yani "kafa duvarin icinde" durumu da
        /// ayni sorgudan cikar.
        ///
        /// MALIYET: acik alanda TEK sorgu — en genis yaricap issa hemen 0 doneriz. Ek sorgular
        /// yalnizca oyuncu fiilen bir duvarin dibindeyken yapilir.</summary>
        float Measure(Vector3 headPos)
        {
            float outer = Mathf.Max(fadeStartDistance, fullBlackDistance + 0.001f);
            float inner = Mathf.Min(fullBlackDistance, outer - 0.001f);

            // En genis kademe: burada temas yoksa hicbir dar kademede de olamaz.
            if (!AnyBlockingSolid(headPos, outer)) return 0f;

            // Dardan genise yuru: temas eden ILK (en dar) kademe alfayi belirler. En genis
            // kademe yukarida zaten sorgulandi, dongu onu tekrarlamaz.
            for (int s = 0; s < blackoutSteps - 1; s++)
            {
                float t = s / (float)(blackoutSteps - 1);
                float r = Mathf.Lerp(inner, outer, t);
                if (AnyBlockingSolid(headPos, r))
                    return maxAlpha * (1f - t * (1f - MinStepAlpha));
            }

            // Hicbir dar kademe tutmadi: duvar menzilin ucunda, en hafif kararma.
            return maxAlpha * MinStepAlpha;
        }

        /// <summary>Verilen yaricapta kararmayi HAK EDEN bir kati collider var mi?
        /// (zemin/tavan temaslari ayiklanir)</summary>
        bool AnyBlockingSolid(Vector3 headPos, float radius)
        {
            int n = Physics.OverlapSphereNonAlloc(headPos, radius, _hits, solidMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < n; i++)
            {
                var col = _hits[i];
                if (!WorldSolids.IsSolid(col)) continue;

                if (ignoreFloorAndCeiling &&
                    WorldSolids.TryContactDirection(col, headPos, out Vector3 to) &&
                    Mathf.Abs(Vector3.Dot(to, Vector3.up)) > VerticalDot)
                    continue;   // altimda zemin / ustumde tavan: comelmek hile degil

                return true;
            }
            return false;
        }
    }
}
