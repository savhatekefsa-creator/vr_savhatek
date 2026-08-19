using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// "BU HARITADA ASAGI DUSULUR" ilani. Sahnede bu bilesen YOKSA dusme sistemi tamamen
    /// oludur — <see cref="PlayerHealth"/>'in dusme bolumu ilk satirda doner.
    ///
    /// NEDEN OPT-IN, NEDEN GLOBAL BIR KURAL DEGIL. Bu projenin her sahnesinde sanal zemin YOK:
    /// constructor odalarinda yurunen sey GERCEK zemindir, harita zemin uretmez ve sahnede
    /// zemin collider'i bulunmaz (bkz. <see cref="AprilTagCalibration"/> icindeki "oyuncuyu
    /// boslukta birakiyordu" notu). "Altimda bir sey yoksa dus" diye global bir kural orada
    /// HERKESI ilk karede oldururdu. Bileseni sahneye koymak, "burada gercekten bir ucurum
    /// var" demenin acik yoludur.
    ///
    /// OLCUT DERINLIK, YOKLUK DEGIL. Bir noktanin bosluk sayilmasi icin altinda hicbir sey
    /// olmamasi gerekmiyor — RooftopArena'da catidan cikinca 44,8 m asagida asfalt, arabalar
    /// ve sokak lambalari var. Soru "altimda bir sey var mi" degil, "altimdaki ilk kati yuzey
    /// yurunen seviyeden ne kadar asagida": <see cref="maxStepDown"/> kadari basamaktir,
    /// otesi ucurumdur. Olculen harita bu ayrimi kolaylastiriyor — catilar y=0, altindaki ilk
    /// yuzey 17 m asagida; arada karar verilecek gri bir bant yok.
    ///
    /// Kurulum: Tools > VR Multiplayer > 47.
    /// </summary>
    [DisallowMultipleComponent]
    public class FallHazard : MonoBehaviour
    {
        [Tooltip("Yurunen yuzeyin dunya yuksekligi (m). RooftopArena'da uc catinin ustu de tam " +
                 "y = 0, yani gercek oda zemini hizasi.")]
        public float walkableLevel = 0f;

        [Tooltip("Yurunen seviyeden bu kadar asagisi hala BASAMAKTIR (m); otesi ucurum. " +
                 "Kenarlar, alcak parapetler ve koprunun 0,47 m'lik kalinligi bu bandin icinde " +
                 "kalmali. Buyutursen gercek ucurumlar basamak sayilmaya baslar, kucultursen " +
                 "sekmeli bir cati parcasi sebepsiz oldurur.")]
        public float maxStepDown = 1.5f;

        [Tooltip("Ayak sondasinin yaricapi (m). Sonda tek isin DEGIL, merkez + dort yon: iki " +
                 "kopru tahtasi arasindaki bir santimlik catlak 'bosluk' diye okunmasin diye. " +
                 "Ayagin gercek genisligi zaten bu civarda.")]
        public float footRadius = 0.2f;

        [Tooltip("Boslugun KESINTISIZ bu kadar surmesi gerekir (sn), yoksa dusus baslamaz. " +
                 "Sifir yapma: bu oyunda kafa boslugun uzerine cikabilir (kenardan asagi bakmak) " +
                 "ama beden catida durur. Kisa bir pay, 'kenara egildim' ile 'kenardan cektim' " +
                 "arasindaki farki koruyan tek sey.")]
        public float graceSeconds = 0.4f;

        [Tooltip("Yercekimi (m/sn^2). Gercek dusus istendigi icin gercek deger; catidan asfalta " +
                 "44,8 m bu degerle tam 3,02 saniye eder.")]
        public float gravity = 9.81f;

        [Tooltip("EMNIYET TAVANI (sn). Altinda hicbir yuzey bulunamayan bir yerden dusulurse " +
                 "oyuncu sonsuza kadar dusmesin diye. Normal dususlerde kullanilmaz — sure " +
                 "carpma noktasindan hesaplanir.")]
        public float maxFallSeconds = 3.5f;

        [Tooltip("Carpmadan sonra dipte kalinan sure (sn). Sifir yaparsan oyuncu yere carptigini " +
                 "GORMEDEN yukari doner ve dusus yarim kalmis gibi hissedilir.")]
        public float groundHoldSeconds = 0.5f;

        [Tooltip("Bir dusus bittikten sonra kontrolun yeniden kurulmasi icin gecen sure (sn).\n\n" +
                 "NE ISE YARIYOR: oyuncu dususten sonra tam ciktigi noktaya, yani BOSLUGUN " +
                 "USTUNE geri geliyor. Olduyse sorun yok — olu oyuncu dusmez. Ama olmediyse " +
                 "(isinma fazi, ya da yeni dirilmis olup dokunulmazligi varsa) ayni karede " +
                 "yeniden duserdi. Bu sure ona catiya geri adim atacak zamani verir.")]
        public float refallCooldownSeconds = 3f;

        [Tooltip("Inis yuzeyi ararken bakilan azami derinlik (m). Haritanin en dibi 44,8 m " +
                 "asagida; pay birakilmis bir deger yeter.")]
        public float landingProbeMetres = 300f;

        /// <summary>Sahnedeki ilan; yoksa null — dusme sistemi kapali demektir.</summary>
        public static FallHazard Instance { get; private set; }

        /// <summary>
        /// Dusus surerken tag kalibrasyonunun DIKEY duzeltmesini susturur.
        ///
        /// NEDEN GEREKLI: <see cref="AprilTagCalibration"/> rig'i her tespitte tag'in OLCULEN
        /// konumuna gore duzeltiyor ve varsayilan olarak dikeyi de duzeltiyor (correctVertical).
        /// Rig 20 m asagidayken tag de 20 m asagida olculur, yani duzeltme oyuncuyu dusus
        /// ortasinda yukari cekmeye calisir — saniyede 1-3 kez.
        ///
        /// YATAY DUZELTME KAPANMIYOR, ve bu bilincli: rig duz asagi indigi icin tag'in X/Z
        /// olcumu degismez, oradaki duzeltme dogru kalir. Dusus sirasinda drift telafisini
        /// tamamen kapatmak icin bir sebep yok.
        /// </summary>
        public static bool SuppressVerticalCalibration;

        // Domain reload kapaliyken statikler oyunlar arasi tasinir; ikinci Play'de "hala
        // dusuyor" sanip dikey kalibrasyonu sonsuza kadar kapali birakirdi.
        // (CalibrationManager.ResetStatics ile ayni gerekce.)
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Instance = null;
            SuppressVerticalCalibration = false;
        }

        /// <summary>Altinda hicbir yuzey bulunamayan bir dususun kat edecegi mesafe (m) —
        /// emniyet tavanina denk gelen serbest dusus yolu.</summary>
        public float MaxFallDistance => 0.5f * gravity * maxFallSeconds * maxFallSeconds;

        void OnEnable()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[FallHazard] Sahnede birden fazla ilan var — ilki gecerli. " +
                                 "Fazlasini sil, yoksa hangi esiklerin isledigi belirsiz kalir.");
                return;
            }
            Instance = this;

            // Sahada "dusme calisiyor mu" sorusunun tek satirlik cevabi. Konsolda bu satir
            // yoksa sistem hic kurulmamistir ve baska hicbir sey aranmamalidir.
            if (Application.isPlaying)
                Debug.Log($"[FallHazard] Ucurum ilani aktif — yurunen seviye {walkableLevel:0.00} m, " +
                          $"ucurum esigi {walkableLevel - maxStepDown:0.00} m, pay {graceSeconds:0.00} sn.");
        }

        void OnDisable()
        {
            if (Instance == this) Instance = null;
        }

        // ------------------------------------------------------------------ sorgu

        /// <summary>Ayni anda degerlendirilen isabet tavani. Bir sondanin altinda bundan fazla
        /// yuzey varsa zaten en ustteki karari verir.</summary>
        const int MaxHits = 16;

        static readonly RaycastHit[] _hits = new RaycastHit[MaxHits];

        /// <summary>Sonda deseni: merkez + dort yon, <see cref="footRadius"/> ile olceklenir.</summary>
        static readonly Vector2[] FootSamples =
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f), new Vector2(-1f, 0f),
            new Vector2(0f, 1f), new Vector2(0f, -1f),
        };

        /// <summary>
        /// Bu kafa konumunun altinda BASILABILIR bir yuzey var mi?
        ///
        /// Bes sondadan BIRI bile zemin buluyorsa cevap evet. "Hepsi bulmali" deseydik kenarda
        /// duran oyuncu — ayaginin yarisi catida, yarisi bosluktan sarkiyor — olurdu; oysa
        /// gercek hayatta orada durulur.
        /// </summary>
        public bool HasGroundUnder(Vector3 headPos)
        {
            float floor = walkableLevel - maxStepDown;

            // Sorgu KAFANIN BIRAZ USTUNDEN baslar, sabit bir yukseklikten degil: oyuncu bir
            // sandigin uzerine ciktiysa sabit baslangic sandigin ICINDE kalir ve sonda yanlis
            // yuzeyi bulur. Kafanin ustunden baslamak "altimda ne var" sorusunu tam olarak
            // sorar — ustumdeki kopru ya da sacak hesaba girmez.
            float top = Mathf.Max(headPos.y, walkableLevel) + 0.2f;
            float distance = top - floor;
            if (distance <= 0f) return true;

            for (int i = 0; i < FootSamples.Length; i++)
            {
                Vector3 o = new Vector3(
                    headPos.x + FootSamples[i].x * footRadius,
                    top,
                    headPos.z + FootSamples[i].y * footRadius);

                float y;
                if (TryHighestSolidBelow(o, distance, out y) && y >= floor)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Bu kafa konumundan dusuldugunde NEREYE inilir? Dusus suresi ve mesafesi bundan
        /// hesaplanir — sabit bir sayidan degil, cunku "gercekte nasil dusuyorsa" demek
        /// altinda ne varsa ona inmek demek: sokaga 44,8 m, alcak bina kusagina 18,6 m.
        /// </summary>
        public bool TryFindLanding(Vector3 headPos, out float landingY)
        {
            float top = Mathf.Max(headPos.y, walkableLevel) + 0.2f;
            if (TryHighestSolidBelow(new Vector3(headPos.x, top, headPos.z),
                                     landingProbeMetres, out landingY))
                return landingY < walkableLevel - maxStepDown;

            landingY = 0f;
            return false;
        }

        /// <summary>Baslangic noktasinin altindaki EN YUKSEK kati yuzeyin yuksekligi.
        ///
        /// <see cref="WorldSolids.IsSolid"/> ile eleniyor, ki kural her yerde ayni olsun:
        /// trigger'lar (oyuncu hitbox'lari, <see cref="FootSurface"/> kaplamalari) ve
        /// rigidbody'si olanlar (yere birakilmis silah, yuvarlanan bomba) zemin sayilmaz.
        /// Yere atilmis bir tufegin oyuncuyu ucurumun uzerinde tasimasi tam da bu filtrenin
        /// engelledigi sey.</summary>
        static bool TryHighestSolidBelow(Vector3 origin, float distance, out float groundY)
        {
            groundY = float.NegativeInfinity;
            bool found = false;

            int n = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, distance,
                                            ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                if (!WorldSolids.IsSolid(_hits[i].collider)) continue;
                float y = _hits[i].point.y;
                if (found && y <= groundY) continue;
                groundY = y;
                found = true;
            }
            return found;
        }

        void OnDrawGizmosSelected()
        {
            // Yurunen seviye ve ucurum esigi: iki yatay duzlem, 20 m'lik bir kare parcasi.
            Gizmos.color = new Color(0.3f, 0.9f, 0.4f, 0.9f);
            DrawLevel(walkableLevel);
            Gizmos.color = new Color(0.95f, 0.35f, 0.25f, 0.9f);
            DrawLevel(walkableLevel - maxStepDown);
        }

        void DrawLevel(float y)
        {
            Vector3 c = new Vector3(transform.position.x, y, transform.position.z);
            Gizmos.DrawWireCube(c, new Vector3(20f, 0.001f, 20f));
        }
    }
}
