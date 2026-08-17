using UnityEngine;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// Birinci sahis eli. Avatar iskeletinden TAMAMEN ayri: el gorseli dogrudan
    /// kumanda tasiyicisinin ALTINA parent'lanir.
    ///
    /// Sart: kumanda neredeyse el ORADADIR. Sinir yok - kumanda 2 m otede yerdeyse
    /// el de oradadir. Bos elde bu garanti hicbir koddan degil, parent-child
    /// iliskisinden gelir. Kol IK'si (uzak oyuncularin gordugu) bundan bagimsiz
    /// calisir; orada kural terstir - kol uzayamaz (bkz. <see cref="ArmReach"/>).
    ///
    /// TEK istisna: silah tutulurken el silahin uzerine oturur. Ozellikle DESTEK
    /// ELI kundakta kaymali ama kundaktan AYRILMAMALI - weld'in ray izdusumu
    /// bunu zaten hesapliyor, burada onu takip ediyoruz. Bu bir erisim siniri
    /// DEGIL (silah zaten kumandanin ucunda), sadece elin silaha yapismasi.
    ///
    /// Hiyerarsi ve neden boyle:
    ///   tasiyici (DUZGUN OLMAYAN olcek 0.08/0.045/0.13)
    ///   +- FP_HandView   <- olcegi tersleyen dugum; donusu HEP identity
    ///      +- Pose       <- poz burada surulur (weld takibi)
    ///         +- Palm / Thumb / Fingers
    /// Ters olcek ile serbest donus AYNI dugumde olursa mesh makaslanir
    /// (S * R * S_inv, S duzgun degilse ortonormal degildir). Ters olcek
    /// dugumunun donusu identity kaldigi surece S * S_inv sadelesir ve alttaki
    /// Pose dugumu serbestce dondurulebilir.
    ///
    /// Gorsel su an YER TUTUCU: "el dogru yerde mi, dogru mu donuyor" sorusunu
    /// cevaplamak icin kod ile uretilen basit bloklar. Gercek el modeli ayri is.
    /// </summary>
    [DefaultExecutionOrder(120)]   // HandGrabber (silahi tasir) ve WeaponHandWeld (110) SONRASI
    public class FirstPersonHandView : MonoBehaviour
    {
        public const string ObjectName = "FP_HandView";
        public const string DotName = "KumandaNoktasi";

        // Beyaz nokta: sapma bu degerin altindayken hic gorunmez (normal nisan alirken
        // gorus temiz kalsin), ustunde opaklik dogrusal olarak 1'e cikar. Ust sinir
        // simdilik sabit; cihaz olcumunden sonra profildeki kopma mesafesine baglanacak.
        // Meta XR Core SDK el modeli (lisans: Oculus SDK License - Meta onayli cihazlar
        // icin serbest, hedefimiz Quest). "L"/"R" sona eklenir.
        const string ModelPath = "FPHands/Meta/OculusHand_";
        const string GloveBodyMat = "FPHands/Meta/M_FPGlove_Govde";
        // M_FPGlove_Uc (uc bogum malzemesi) su an KULLANILMIYOR - bkz. BuildHandModel.

        // Meta'nin eli anatomik olarak GERCEK boyutta (bilek->uc ~190 mm, olcek 1.000);
        // ortada olcek hatasi yok. Ama VR'da gercek boyutlu el sik sik kucuk algilanir,
        // ustelik kolu/mansetı olmayan bir el daha da kucuk okunur. Cihazda ayarlanacak
        // tek dokunus noktasi burasi. Olcek "Hand" dugumune uygulanir - FP_HandView koku
        // ters-olcek dugumudur, oraya dokunmak makaslama kuralini bozar.
        // DENEME 2026-08-13: 1.20 -> 1.10. Sebebi olculdu: Meta'nin eli anatomik olarak
        // ZATEN dogru boyutta (bilek->orta uc 190 mm = gercek yetiskin eli), 1.20 onu
        // 228 mm'ye cikariyordu (%20 buyuk) ve silahlar buna gore kucuk okunuyordu -
        // oysa silahlarin coğunun boyu dogru (Rifle 1 %100, Smg 1 %102, tabancalar %107).
        // 1.10 -> el 209 mm.
        //
        // ONCEKI DEGERLER, geri donmek gerekirse: 1.00 (ham model, VR'da kucuk algilandi),
        // 1.10 (ilk ayar), 1.20 (kullanici "hala kucuk" dedi, 2026-08-12).
        // Tutus verisini ETKILEMEZ: bilek offseti ve parmak kivrimlari olcekten bagimsiz;
        // yalnizca parmaklarin kabzayi sarma sikiligi bir tik degisir.
        const float HandScale = 1.10f;

        // Elin kumandaya gore ince ayari. SIFIR = yalnizca OpenXR grip cerçevesi.
        // Spec dogru cerceveyi verir ama son 10-20 dereceyi veremez: dogru durus
        // kumandanin FIZIKSEL sekline ve gercek elin sapi nasil kavradigina bagli.
        // O yuzden bu iki sayi cihazda ayarlanip buraya islenir.
        // YALNIZ SAG EL: sol el aynalanarak turetilir (bkz. BuildHandModel).
        // ===================== INCE AYAR — TEK DOKUNUS NOKTASI =====================
        // Asagidaki ALTI sayi disinda elin durusuna dokunmaya gerek yok.
        //
        // Eksenler KUMANDANIN ham eksenleri DEGIL, senin GORDUGUN yonler. Ham grip
        // eksenleri sezgisel degil (cihazda olculdu: grip +Z ~ dunya yukarisi,
        // grip +Y ~ dunya gerisi, grip +X ~ dunya sagi), o yuzden cevrim burada
        // yapiliyor ve disaridan "ileri / yukari / ice" diye konusuluyor.
        //
        // Hepsi SAG ELE gore. Sol el WeaponGripMath.MirrorX ile aynalanir - ayri
        // sayi YOK, dolayisiyla iki el asla ayrisamaz.
        // Kumanda notr tutulurken: parmaklar ILERI, basparmak YUKARI, avuc ICE.

        const float OffsetForward = 0f;   // metre, + ileri (parmaklarin gosterdigi yon)
        const float OffsetUp      = 0f;   // metre, + yukari (basparmagin oldugu taraf)
        const float OffsetInward  = 0f;   // metre, + govde ortasina dogru

        const float TweakYaw   = 0f;      // derece, + eli yukari eksende disa cevirir
        const float TweakPitch = -10f;    // derece, + parmak uclarini ASAGI indirir
                                          // (isaret olculdu, tahmin degil: Unity'de +X
                                          // ekseninde arti donus burnu asagi egiyor.)
                                          // Rest durusta orta parmak ucu 2.49 derece asagi
                                          // bakiyordu; -2.5 onu tam duzluyor ama cihazda
                                          // HALA asagi goruldu (sap elde zaten one-asagi
                                          // egik duruyor), -10 ile ucu 6.8 derece yukarida.
        const float TweakRoll  = 40f;     // derece, + avuc icini asagi dondurur
                                          // Temel durusta avuc 40.3 derece YUKARI yatikti
                                          // (olculdu); 40 ile 0.3 dereceye iner, yani avuc
                                          // dupeduz ICE bakar. Cihazda 20 denendi, az geldi.

        // TEMEL DONUS - buna dokunma, cihaz gozlemlerinden HESAPLANDI (tahmin degil):
        // parmaklar tam (0,-1,0)'a, basparmak (0,0,1)'e gidiyor. Ince ayar icin
        // yukaridaki Tweak* degerlerini kullan.
        static readonly Vector3 BaseWristEuler = new Vector3(0f, 220.3f, 209.9f);

        // ============================================================================
        // INCE AYAR YALNIZCA KUMANDAYA GORE DURAN ELE UYGULANIR.
        //
        // Bu ayirim sart, cihazda bedeli goruldu: Tweak* elin KUMANDAYA gore
        // duzeltmesidir, ama el modelinin uzerine gomulunce silah tutulurken de
        // yasiyordu. Weld bilegi kabzanin ankrajina koyuyor, el o bilegin etrafinda
        // 41 derece donmus kaliyor - olculdu: parmak bogumlari 79 mm, parmak uclari
        // 157 mm, basparmak ucu 114 mm kayiyor. Cihazda "el kabzanin saginda kaliyor,
        // silaha degmiyor" diye goruldu.
        //
        // Cozum: ince ayar Pose dugumune tasindi. Bos elde Pose'a yaziliyor; silah
        // tutulurken Pose'u weld suruyor, dolayisiyla ayar KENDILIGINDEN devre disi
        // kaliyor. El modelinin uzerinde yalnizca TEMEL cerceve hizasi kaliyor.
        // ============================================================================

        // Gorunen yonlerden kumandanin ham eksenlerine cevrim.
        static Vector3 WristOffsetPosition =>
            new Vector3(-OffsetInward, -OffsetForward, OffsetUp);

        /// <summary>Yalnizca ince ayar donusu (temel cerceve hizasi HARIC).</summary>
        static Quaternion TweakRotation =>
            Quaternion.AngleAxis(TweakYaw, Vector3.forward)
            * Quaternion.AngleAxis(TweakPitch, Vector3.right)
            * Quaternion.AngleAxis(TweakRoll, Vector3.down);

        const float DotDiameter = 0.02f;
        const float DotFadeStart = 0.03f;
        const float DotFadeEnd = 0.25f;

        // OpenXR grip pose zaten avucun icinde oturur, o yuzden tasiyiciya gore
        // kaydirma sifir. Elin silaha gore yerini ayarlamak gerekirse tek dokunus
        // noktasi burasi.
        static readonly Vector3 PalmLocalOffset = Vector3.zero;

        // Silaha yapisma/birakma tek karede olursa el ziplar. WeaponHandWeld
        // avatarin bilegini ayni sebeple harmanliyor (0.12 s) - FP eli de oyle.
        // Agirlik TAM 0 oldugunda poz kumandaya birebir esitlenir, yani bos elde
        // hicbir gecikme/yumusatma YOKTUR; harman yalnizca gecis aninda yasar.
        const float BlendSeconds = 0.12f;

        Transform _carrier;      // kumanda tasiyicisi
        Transform _pose;         // surulen dugum
        MeshRenderer _dot;       // kumandanin gercek yerini gosteren beyaz nokta
        MaterialPropertyBlock _dotMpb;
        GameObject _avatar;      // WeaponHandWeld calisma aninda buraya ekleniyor
        bool _left;
        WeaponHandWeld _weld;
        FirstPersonFingerCurl _curl;   // silah pozunu buraya bildiriyoruz
        float _weight;                                    // 0 = kumanda, 1 = silah ankraji
        Vector3 _lastAnchor;

        /// <summary>
        /// Iki kumanda tasiyicisinin altina el gorselini kurar. Yalnizca SAHIP
        /// icin cagrilir; aga hic girmez, uzak istemcilerde hic yaratilmaz.
        /// </summary>
        /// <summary>
        /// Bu kumanda tasiyicisi altindaki BIRINCI SAHIS bilek kemigi (yoksa null).
        ///
        /// Disaridan kullanimi: atolyede ayarlanan pozlar BILEGE goredir, ama oyundaki bazi
        /// tuketiciler (orn. bomba pimi) ag el CIPASINA baglanir. Ikisi ayni sey degil —
        /// cipa kumandanin ham pozu, bilek ise el modelinin oturdugu yer. Ayarlanan degerin
        /// oyunda birebir cikmasi icin cevrimin bilekten yapilmasi gerekiyor.
        ///
        /// UZAK OYUNCUDA NULL DONER: FP eli yalnizca sahipte kurulur. Cagiran taraf o durumda
        /// kendi yedegine dusmeli.
        /// </summary>
        public static Transform FindWrist(Transform carrier, bool left)
            => FindBone(carrier, left ? "b_l_wrist" : "b_r_wrist");

        /// <summary>Bu tasiyicinin altindaki FP el modelinde ada gore kemik (yoksa null).</summary>
        public static Transform FindBone(Transform carrier, string boneName)
        {
            if (carrier == null) return null;
            var root = carrier.Find(ObjectName);
            if (root == null) return null;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == boneName) return t;
            return null;
        }

        public static void Attach(Transform leftCarrier, Transform rightCarrier, GameObject avatar)
        {
            Build(leftCarrier, true, avatar);
            Build(rightCarrier, false, avatar);
        }

        static void Build(Transform carrier, bool left, GameObject avatar)
        {
            if (carrier == null) return;
            if (carrier.Find(ObjectName) != null) return;   // ikinci el takilmasin

            var root = new GameObject(ObjectName);
            root.transform.SetParent(carrier, false);
            root.transform.localRotation = Quaternion.identity;   // DEGISTIRME (bkz. sinif notu)

            // Tasiyicilar eski "basit el kupu"ndan kalma duzgun olmayan bir olcek
            // tasiyor. Tasiyicinin olcegini prefabta duzeltmek caziptir ama
            // YAPILMAMALI: HandGrabber silah tutus offsetlerini
            // anchor.InverseTransformPoint ile cozuyor, yani olcek tutus
            // kalibrasyonunun icinde. Burada tersliyoruz.
            Vector3 s = carrier.lossyScale;
            root.transform.localScale = new Vector3(
                Mathf.Approximately(s.x, 0f) ? 1f : 1f / s.x,
                Mathf.Approximately(s.y, 0f) ? 1f : 1f / s.y,
                Mathf.Approximately(s.z, 0f) ? 1f : 1f / s.z);
            // localPosition ust dugumun olceginden gecer; offset sifir oldugu icin
            // fark etmiyor, sifirdan farkli verilecekse olcege bolunmeli.
            root.transform.localPosition = PalmLocalOffset;

            var pose = new GameObject("Pose");
            pose.transform.SetParent(root.transform, false);

            // Kumandanin GERCEK yeri. Pose'un KARDESI olmali: Pose silaha kayiyor,
            // nokta kumandada kalmali. Root zaten tam kumandanin uzerinde ve donusu
            // identity oldugu icin nokta hicbir sey yazilmadan yerinde durur.
            var dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            dot.name = DotName;
            var dcol = dot.GetComponent<Collider>();
            if (dcol != null)
            {
                if (Application.isPlaying) Object.Destroy(dcol);
                else Object.DestroyImmediate(dcol);
            }
            dot.transform.SetParent(root.transform, false);
            dot.transform.localPosition = Vector3.zero;
            dot.transform.localRotation = Quaternion.identity;
            dot.transform.localScale = Vector3.one * DotDiameter;
            var dmr = dot.GetComponent<MeshRenderer>();
            dmr.sharedMaterial = GhostMaterial();
            dmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            dmr.receiveShadows = false;
            dmr.enabled = false;   // sapma olmadan gorunmez

            BuildHandModel(pose.transform, left);

            // KOL SAATI — yalnizca SOL elde ve yalnizca GERCEK oyuncuda.
            // avatar == null demek "bu el bir tezgah maketi" demek (atolyenin sahte elleri
            // ayni kurulumdan geciyor); orada saat hem gereksiz hem kafa karistirici olurdu,
            // ustelik WatchScreenUI'nin okudugu can/mermi kaynaklari da yok.
            if (left && avatar != null)
            {
                var hand = pose.transform.Find("Hand");
                if (hand != null) UI.WristWatch.Attach(hand, true);
            }

            var view = root.AddComponent<FirstPersonHandView>();
            view._carrier = carrier;
            view._pose = pose.transform;
            view._dot = dmr;
            view._avatar = avatar;
            view._left = left;
        }

        // Saydam varyant KAYNAK malzeme basina bir kez uretilir (MaterialGhost) ve iki
        // el paylasir. Opaklik el basina degistigi icin malzeme kopyalanmaz -
        // MaterialPropertyBlock kullanilir; malzeme ZATEN saydam oldugundan MPB'nin
        // yapamadigi sey (render modu degistirmek) gerekmiyor.
        static Material _dotSource, _dotGhost;

        static Material GhostMaterial()
        {
            if (_dotGhost != null) return _dotGhost;
            if (_dotSource == null)
            {
                _dotSource = MakeMaterial(Color.white);
                _dotSource.name = "M_KumandaNoktasi";
            }
            _dotGhost = MaterialGhost.Of(_dotSource) ?? _dotSource;
            return _dotGhost;
        }

        void LateUpdate()
        {
            if (_pose == null || _carrier == null) return;

            // Weld calisma aninda WeaponGrip tarafindan avatara EKLENIYOR, o yuzden
            // bir kere bulup onbellege almak yetmez - yoksa her karede tekrar bak.
            if (_weld == null && _avatar != null)
                _weld = _avatar.GetComponentInChildren<WeaponHandWeld>();

            // Silah tutarken el, silahin uzerindeki ankraja TAM GUCLE oturur ve orada
            // kalir - kumanda geri cekilse bile. Kumandanin gercek yeri beyaz noktayla
            // gosterilir, belli mesafeyi asinca tutus KOPAR (HandGrabber).
            //
            // Bu, "el her zaman kumandada" kuralinin bilincli istisnasi: asil ihtiyac
            // elin kumandada olmasi degil, oyuncunun gercek elini KAYBETMEMESIYDI -
            // onu beyaz nokta karsiliyor. Agirlik SOLMAZ; kopana kadar yapisik kalir
            // (sapmayla soldurma cihazda reddedilmisti, tekrarlanmiyor).
            bool welded = _weld != null && _weld.TryGetHandAnchor(_left, out _lastAnchor, out _);

            // Parmak pozu silahin profilinden gelsin: authored tutus pozu TEK KAYNAK,
            // avatarin elleri de ayni profili kullaniyor. Profil cevrilmis poz tasimiyorsa
            // surucu kendi prosedurel kivrimina devam eder.
            if (_curl == null && _pose != null) _curl = _pose.GetComponentInChildren<FirstPersonFingerCurl>(true);
            if (_curl != null)
            {
                WeaponGripProfile heldProfile;
                bool heldSupport;
                if (welded && _weld.TryGetHandProfile(_left, out heldProfile, out heldSupport))
                    _curl.SetWeaponPose(heldProfile, heldSupport);
                else
                    _curl.ClearWeaponPose();
            }

            // ELIN SILAHTAKI YERI AUTHORED VERI. Onceden avatarin bilek hedefi ile
            // kumandadan gelen bilek donusu arasindaki FARK aliniyor ve kumandanin
            // donusune uygulaniyordu; bu, FP elini avatarin bilek konvansiyonuna
            // baglıyordu ve cihazda el kabzanin yanina dusuyordu. Artik el dogrudan
            // SILAHIN cipa cercevesine oturuyor, uzerine profildeki fpWristLocal*
            // offseti biniyor - o offset de oyun icindeki Silah Atolyesi'nde GORULEREK
            // ayarlaniyor. Boylece atolyede gordugun poz ile oyundaki poz ayni yoldan
            // uretiliyor, arada cevrim yok.
            Quaternion anchorRot = Quaternion.identity;
            Vector3 fpWristPos = Vector3.zero;
            Quaternion fpWristRot = Quaternion.identity;
            if (welded)
            {
                _weld.TryGetHandAnchor(_left, out _lastAnchor, out anchorRot, out bool sup);
                WeaponGripProfile prof;
                bool supportRole, mirrored;
                if (_weld.TryGetHandProfile(_left, out prof, out supportRole, out mirrored) && prof != null)
                {
                    var hp = supportRole ? prof.supportHand : prof.mainHand;
                    fpWristPos = hp.fpWristLocalPosition;
                    fpWristRot = hp.FpWristRotation;
                    // Silah ters elle tutuluyorsa cipa aynalanmis geliyor; offset de
                    // aynalanmali, yoksa el ters durur.
                    if (mirrored)
                    {
                        fpWristPos = WeaponGripMath.MirrorX(fpWristPos);
                        fpWristRot = WeaponGripMath.MirrorX(fpWristRot);
                    }
                }
            }

            float target = welded ? 1f : 0f;
            if (Application.isPlaying && BlendSeconds > 0f)
                _weight = Mathf.MoveTowards(_weight, target, Time.deltaTime / BlendSeconds);
            else
                _weight = target;   // editor/olcum: geciste takilip kalmayalim

            // Ince ayar BOS ELIN duzeltmesi: Pose'a yaziliyor, silah tutulurken asagida
            // Pose'u weld surdugu icin kendiliginden devre disi kaliyor.
            Quaternion freeRot = TweakRotation;
            Vector3 freePos = WristOffsetPosition;
            if (_left)
            {
                freeRot = Weapons.WeaponGripMath.MirrorX(freeRot);
                freePos = Weapons.WeaponGripMath.MirrorX(freePos);
            }

            if (_weight <= 0f)
            {
                // Bos el: tasiyiciya BIREBIR yapisik, yalnizca ince ayar var.
                _pose.localPosition = freePos;
                _pose.localRotation = freeRot;
                UpdateDot(0f);
                return;
            }

            // Sapma: elin oturdugu ankraj ile kumandanin GERCEK yeri arasindaki mesafe.
            // Nokta bunu gorunur kilar - oyuncu elinin nerede oldugunu kaybetmesin ve
            // kopmanin yaklastigini gorsun.
            UpdateDot(Vector3.Distance(_carrier.position, _lastAnchor));

            // Silah tarafi: cipa + authored FP bilek offseti. Bos-el tarafi: kumanda +
            // ince ayar. Ikisi agirlikla harmanlanir, gecis yumusak.
            Vector3 weldPos = _lastAnchor + anchorRot * fpWristPos;
            Quaternion weldRot = anchorRot * fpWristRot;
            _pose.SetPositionAndRotation(
                // Offset metre cinsinden: tasiyicinin DUZGUN OLMAYAN olcegi degil,
                // onu tersleyen kok dugum uzerinden dunyaya cevriliyor.
                Vector3.Lerp(_pose.parent.TransformPoint(freePos), weldPos, _weight),
                Quaternion.Slerp(_carrier.rotation * freeRot, weldRot, _weight));

            LogDiagnostic(welded);
        }

        /// <summary>
        /// Beyaz noktayi sapmaya gore surer. Nokta zaten kumandanin uzerinde duruyor
        /// (root'un cocugu, yerel konumu sifir) - burada yalnizca gorunurlugu ayarlanir.
        /// Opaklik icin malzeme KOPYALANMAZ: paylasimli saydam varyant + property block.
        /// </summary>
        void UpdateDot(float deviation)
        {
            if (_dot == null) return;

            float a = Mathf.InverseLerp(DotFadeStart, DotFadeEnd, deviation);
            if (a <= 0f)
            {
                if (_dot.enabled) _dot.enabled = false;
                return;
            }

            if (!_dot.enabled) _dot.enabled = true;
            if (_dotMpb == null) _dotMpb = new MaterialPropertyBlock();
            _dot.GetPropertyBlock(_dotMpb);
            // Kopmaya yaklastikca beyazdan uyari tonuna kayar.
            Color c = Color.Lerp(Color.white, new Color(1f, 0.55f, 0.25f), a);
            c.a = Mathf.Lerp(0.25f, 0.95f, a);
            _dotMpb.SetColor("_BaseColor", c);
            _dotMpb.SetColor("_Color", c);
            _dot.SetPropertyBlock(_dotMpb);
        }

        // TESHIS: cihazda "el silaha oturmuyor" derdini pikselden degil SAYIDAN
        // okuyabilmek icin. Yalnizca silah tutulurken ve saniyede bir yaziyor, yani
        // normal oyunda gurultu yapmaz. adb logcat ile kablodan canli okunur:
        //   adb logcat -s Unity:I | findstr FPEl
        // Dert cozulunce bu blok silinebilir.
        float _nextLog;

        void LogDiagnostic(bool welded)
        {
            if (!welded || Time.time < _nextLog) return;
            _nextLog = Time.time + 1f;

            float toAnchor = Vector3.Distance(_pose.position, _lastAnchor) * 1000f;
            float toCarrier = Vector3.Distance(_pose.position, _carrier.position) * 1000f;
            // Elin cipadan authored offseti + ROL ve AYNALAMA - "el ters duruyor" turu
            // sikayetlerde once bunlara bakilir, ikisi de gozle secilemiyor.
            float gripAngle = 0f;
            string role = "-";
            if (_weld != null && _weld.TryGetHandProfile(_left, out var dp, out bool ds, out bool dm) && dp != null)
            {
                gripAngle = Quaternion.Angle(Quaternion.identity,
                    (ds ? dp.supportHand : dp.mainHand).FpWristRotation);
                role = (ds ? "destek" : "ana") + (dm ? "/AYNALI" : "");
            }
            string wname = "-";
            Vector3 wpos = Vector3.zero;
            if (_weld != null && _weld.TryGetHeldWeapon(_left, out Transform wt) && wt != null)
            {
                wname = wt.name;
                wpos = wt.position;
            }
            Debug.Log(string.Format(
                "[FPEl] {0} rol={7} agirlik={1:0.00} el->ankraj={2:0.0}mm el->kumanda={3:0.0}mm " +
                "tutus_duzeltme={4:0.0}deg silah={5} silah->ankraj={6:0.0}mm",
                _left ? "SOL" : "SAG", _weight, toAnchor, toCarrier, gripAngle, wname,
                Vector3.Distance(wpos, _lastAnchor) * 1000f, role));
        }

        /// <summary>
        /// Meta XR Core SDK'nin el modelini kurar. Model KENDI rig'iyle kullaniliyor -
        /// avatarin iskeletine oturtulmuyor. Daha once o yol denendi ve mesh'i mahvetti:
        /// avatarin parmaklari anatomik olandan %34 uzun, mesh gerilip incelmisti.
        /// Burada bilek dogrudan Pose'a oturur, gerisi modelin kendi kemiklerinde kalir.
        /// </summary>
        static void BuildHandModel(Transform pose, bool left)
        {
            var prefab = Resources.Load<GameObject>(ModelPath + (left ? "L" : "R"));
            if (prefab == null)
            {
                Debug.LogWarning("[FirstPersonHandView] El modeli bulunamadi: " + ModelPath + (left ? "L" : "R"));
                return;
            }

            var go = Object.Instantiate(prefab, pose, false);
            go.name = "Hand";
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one;

            var smr = go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr == null) return;

            // Modelin duruşunu kumanda cercevesine hizala: parmaklar +z, elin sirti +y.
            // Sabit euler gommek yerine kemiklerden HESAPLANIYOR - model yeniden import
            // edilirse veya Meta duruşu degistirirse kendiliginden dogru kalir.
            string pre = left ? "b_l_" : "b_r_";
            Transform wrist = null, mid = null, thumb = null, idx = null, pky = null;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == pre + "wrist") wrist = t;
                else if (t.name == pre + "middle1") mid = t;
                else if (t.name == pre + "thumb1") thumb = t;
                else if (t.name == pre + "index1") idx = t;
                else if (t.name == pre + "pinky1") pky = t;
            }
            if (wrist != null && mid != null && thumb != null && idx != null && pky != null)
            {
                // Boyut once: olcek bilegi de oynatir, konum duzeltmesi ONDAN SONRA
                // yapilmali.
                go.transform.localScale = Vector3.one * HandScale;

                // HEDEF DURUS: OpenXR grip pose. Spec'te "ileri" ekseni, kapali elin
                // dort parmaginin olusturdugu TUPUN ekseni - yani sapin uzandigi yon.
                // Onceki surum ACIK ELIN PARMAK YONUNU oraya hizaliyordu; ikisi arasinda
                // 72 derece var, cihazda "dort parmak yukari bakiyor" diye goruldu.
                //
                // Tup ekseni = parmak mentese ekseni = Cross(parmak yonu, avuc disi).
                // avucDisi el-liligi zaten tasiyor (sol elde ham carpim tersine bakar).
                //
                // ISARET ELLE VERILIYOR ve bu SART: LookRotation ayna-esdeger degildir,
                // aynalanmis iki girdiden AYNI donusu uretir. Isaret konmazsa iki elin
                // avucu da ayni yone bakar - bu oturumda tam olarak bu hata iki kez
                // yasandi. Isaretin dogrulugu OpenXR'in kuraliyla sabitleniyor:
                // "avuc normali sol avuctan disari, SAG avuctan iceri", yani sag el
                // avucu -x'e bakmali. Olculdu: ayna testi uc eksende de 0.00 derece.
                //
                // BUTUN HESAP POSE'UN YEREL UZAYINDA. Ilk surum dunya uzayinda
                // hesapliyordu ve elin ic hizasi KURULUM ANINDAKI TASIYICI DONUSUNE
                // bagli cikiyordu (olculdu: tasiyiciyi 90 cevir, el 90 farkli otur).
                // Oyunda tasiyici spawn'da ~identity oldugu icin fark edilmedi; Silah
                // Atolyesi elleri TEZGAHA DONUK silahin altina kurunca el, kullanicinin
                // o an baktigi yone gore hizalandi ve oyunda ters durdu - kullanicinin
                // butun titiz ayari bosa gitti. Yerel uzayda hesap, tasiyici donusunden
                // bagimsizdir ve identity-tasiyici sonucunu her yerde birebir uretir.
                System.Func<Vector3, Vector3> toLocal = wp => pose.InverseTransformPoint(wp);
                Vector3 wristL = toLocal(wrist.position);
                Vector3 fingers = (toLocal(mid.position) - wristL).normalized;
                Vector3 rawCross = Vector3.Cross(fingers, (toLocal(idx.position) - toLocal(pky.position)).normalized).normalized;
                Vector3 palmOut = left ? -rawCross : rawCross;
                Vector3 tube = Vector3.Cross(fingers, palmOut).normalized;
                if (left) tube = -tube;
                Vector3 thumbDir = (toLocal(thumb.position) - wristL).normalized;
                Vector3 up = (thumbDir - tube * Vector3.Dot(thumbDir, tube)).normalized;
                if (up.sqrMagnitude > 1e-6f)
                {
                    // TEMEL cerceve hizasi. INCE AYAR BURAYA GIRMEZ - o Pose dugumune
                    // uygulaniyor ki silah tutulurken devre disi kalsin (bkz. sinif basi:
                    // ayarin silah pozuna sizmasi cihazda eli kabzanin 8-16 cm yanina
                    // atiyordu). Yalniz SAG EL icin yazilir, sol el aynalanir.
                    Quaternion offRot = Quaternion.Euler(BaseWristEuler);
                    if (left) offRot = Weapons.WeaponGripMath.MirrorX(offRot);

                    go.transform.localRotation = offRot
                        * Quaternion.Inverse(Quaternion.LookRotation(tube, up))
                        * go.transform.localRotation;
                }

                // Bilek tam Pose orijinine gelsin (modelin kokunde olmayabilir).
                // Donus degistikten SONRA olculur - bilek donusle birlikte tasindi.
                go.transform.localPosition -= toLocal(wrist.position);
            }

            // Askeri boyama: tek renk, askerin kendi eldiven dokusundan olculdu.
            //
            // BOYANMIS MESH KULLANILMIYOR (Glove_Meta_R/L). O mesh yalnizca ucgenleri
            // iki alt-mesh'e ayirmak icin uretilmisti (uc bogumlar koyu olsun diye) ama
            // basparmakta koyu, zikzak kenarli bir leke birakiyor. Olculdu: vertex,
            // normal, tangent, kemik agirligi ve ucgen sarimi orijinalle BIREBIR ayni;
            // leke iki alt-mesh'e AYNI malzeme verildiginde bile duruyor, Meta'nin
            // orijinal mesh'inde ise hic yok. Uc bogum koyulugu kozmetikti, doku isi
            // yapilinca dokudan gelecek - o zamana kadar orijinal mesh.
            var body = Resources.Load<Material>(GloveBodyMat);
            if (body != null)
            {
                var mats = new Material[smr.sharedMesh != null ? smr.sharedMesh.subMeshCount : 1];
                for (int i = 0; i < mats.Length; i++) mats[i] = body;
                smr.sharedMaterials = mats;
            }

            smr.updateWhenOffscreen = true;   // el goruse yakin, hatali kirpilmasin
            smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.receiveShadows = false;

            // Bilek acik bir mesh (el bilekten kesik). Tek yuzlu cizilirse icine
            // bakildiginda delik gibi okunuyor - avatarin eldiveninde de ayni dert vardi.
            MaterialDoubleSided.Apply(smr);

            // Parmaklari grip/tetikten kivir. Kemikler modelin kendi rig'inde oldugu icin
            // surucu de burada, elin uzerinde duruyor.
            var curl = go.AddComponent<FirstPersonFingerCurl>();
            curl.Init(left, pose.GetComponentInParent<NetworkVRPlayer>());
        }

        static Material MakeMaterial(Color color)
        {
            // Projedeki konvansiyon: URP shader'i bulunamazsa yerlesik olana dus,
            // boylece build'de shader elenirse el gorunmez olmaz.
            var sh = Shader.Find("Universal Render Pipeline/Lit")
                  ?? Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            return m;
        }
    }
}
