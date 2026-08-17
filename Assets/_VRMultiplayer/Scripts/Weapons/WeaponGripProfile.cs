using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>How the trigger maps to shots: one per press, sustained fire, or a fixed burst
    /// per press.</summary>
    public enum FireMode
    {
        Semi,
        Auto,
        Burst,
    }

    /// <summary>
    /// Data-driven static grip pose for one weapon type — this project's ScriptableObject
    /// equivalent of ISDK's HandGrabPose + Fingers Freedom + BoxGrabSurface. A weapon matches a
    /// profile by name (Equals beats Contains); a weapon with NO matching profile keeps today's
    /// pivot-snap behaviour bit-for-bit. Authoring convention for every local-space value here:
    /// weapon +Z = barrel forward, +Y = top rail.
    /// </summary>
    [CreateAssetMenu(menuName = "VR Multiplayer/Weapon Grip Profile", fileName = "WeaponGripProfile")]
    public class WeaponGripProfile : ScriptableObject
    {
        [Header("Eslestirme (Equals > Contains; ikisi de bossa profil hic eslesmez)")]
        [Tooltip("Silah GameObject adi TAM olarak buysa eslesir (oncelikli).")]
        public string weaponNameEquals = "";
        [Tooltip("Silah GameObject adi bunu ICERIYORSA eslesir (Equals eslesmezse bakilir). BUNU DOLDUR: yalnizca Equals kullanirsan silahin sahnedeki kopyalari (\"Weapon_Pistol (1)\") profille ESLESMEZ ve sessizce eski pivot-snap davranisina duser — poz, tepme, ates modu hicbiri calismaz.")]
        public string weaponNameContains = "";

        [Header("Kabza cipasi (silah-lokal kumanda pozu — yakalama araciyla uretilir)")]
        [Tooltip("Ana elin kavradigi nokta, silah-lokal.")]
        public Vector3 gripLocalPosition;
        [Tooltip("Kabza cipasinin silah-lokal yonelimi (euler). Kumandanin GERCEK tutus egimi — namluya paralel olmak zorunda degil.")]
        public Vector3 gripLocalEuler;

        [Tooltip("Namlunun silah-LOKAL yonu (Muzzle forward). Iki elli nisan BU ekseni hedefe hizalar; kabza cipasi egik yakalanmis olsa bile namlu dogru doner.")]
        public Vector3 barrelLocalDirection = Vector3.forward;

        [Header("Atolye tezgahi sunumu (namlusu OLMAYAN nesneler icin)")]
        // NEDEN AYRI ALANLAR: tezgah durusu bugune kadar barrelLocalDirection'dan turetiliyordu
        // (bkz. WeaponWorkshop.BenchRotation). Tufekte bu dogru — namlu nesnenin uzun eksenidir
        // ve tek eksen sunumu yeterince belirler. BOMBADA NAMLU YOKTUR: uc bombanin da
        // barrelLocalDirection'i (0,0,1) yazili ve bu deger hicbir seyi tarif etmiyor. Sonuc
        // olculdu — kol/pim halkasi uc bombada UC AYRI yone bakiyordu (G1 -Z, G2 +X+Z, G3 +X-Z),
        // yani ayni tezgahta ucu de baska turlu duruyordu.
        //
        // IKI EKSEN sunumu TAM belirler ve roll'u tanimsiz birakmaz. Ikisi de bos ise
        // (varsayilan) eski namlu kurali gecerlidir — dokunulmamis 16 silahin durusu birebir
        // korunur.
        //
        // SUNUM VERIYI ETKILEMEZ: el silaha GORE yerlestiriliyor (WeaponWorkshop.Drive:
        // cipa = weapon.TransformPoint(...)), yani buradaki degerler yalnizca senin nasil
        // gordugunu degistirir, kaydedilen tutusa dokunmaz.
        [Tooltip("Tezgahta YUKARI bakacak silah-lokal eksen (bombada govde ekseni: gobek->tapa). Sifir = eski namlu kurali.")]
        public Vector3 benchUpLocal = Vector3.zero;
        [Tooltip("Tezgahta OYUNCUYA bakacak silah-lokal eksen (bombada emniyet kolu / pim halkasi tarafi). Sifir = eski namlu kurali.")]
        public Vector3 benchFrontLocal = Vector3.zero;

        /// <summary>Tezgah sunumu bu profilde ACIKCA yazili mi? Iki eksen de dolu ve
        /// birbirine paralel DEGIL olmali (paralel olurlarsa LookRotation cozulemez).</summary>
        public bool HasBenchPose =>
            benchUpLocal.sqrMagnitude > 1e-6f && benchFrontLocal.sqrMagnitude > 1e-6f &&
            Vector3.Cross(benchUpLocal.normalized, benchFrontLocal.normalized).sqrMagnitude > 1e-4f;

        [Header("Savas config (SUNUCU-OTORITER; doluysa asagidaki eski ates/tepme/sarjor alanlarini ezer)")]
        [Tooltip("Bu silahin savas ayarlari. Bos = asagidaki eski alanlar gecerli (davranis degismez).")]
        public WeaponCombatConfig combat;

        [Header("Ana el (kabza)")]
        public HandPose mainHand = HandPose.Defaults(true);

        [Header("Destek rayi (kundak; silah-lokal dogru parcasi)")]
        public Vector3 supportRailLocalStart;
        public Vector3 supportRailLocalEnd;
        [Tooltip("KULLANILMIYOR (2026-08-17). Kopma esigi artik silah basina degil TEK yerden " +
                 "geliyor: HandGrabber.SupportBreakReach. Sebep: bu alan 19 profilin hepsinde " +
                 "0.30 idi ve zaten HandGrabber'daki tabanla eziliyordu — silah basina ayar " +
                 "izlenimi veren ama hicbir sey yapmayan bir alandi. Gercekten silah basina " +
                 "kopma esigi gerekirse burasi yeniden baglanabilir.")]
        public float supportBreakDistance = 0.30f;
        public HandPose supportHand = HandPose.Defaults(false);

        [Header("Iki elli nisan filtresi")]
        [Tooltip("Destek elinin namluyu ne kadar yonettigi. 1 = tam sanal dipcik (tufek), " +
                 "0 = namluyu YALNIZ ana el yonetir, destek eli gorsel olarak tutunur ama " +
                 "nisani cevirmez (tabanca). Ara degerler harmanlanir.")]
        [Range(0f, 1f)] public float twoHandAimWeight = 1f;
        [Tooltip("Bu acinin altindaki el titremesi namluya HIC yansimaz (derece).")]
        public float aimDeadzoneDegrees = 0.75f;
        [Tooltip("Deadzone bitiminden tam takibe yumusak gecis bandi (derece).")]
        public float aimSoftKneeDegrees = 1.5f;
        [Tooltip("Takip filtresinin yarilanma suresi (ms).")]
        public float aimHalfLifeMs = 45f;

        [Header("Opsiyonel ates/namlu override")]
        [Tooltip("Isaretliyse fireInterval/range asagidaki degerlerle ezilir.")]
        public bool overrideFire;
        public float fireInterval = 0.18f;
        public float range = 60f;
        [Tooltip("Silahta Muzzle child'i yoksa bu lokal noktada olusturulur (isaretliyse).")]
        public bool createMuzzleIfMissing;
        public Vector3 muzzleLocalPosition;

        [Header("Ates modu")]
        [Tooltip("Semi: tik basina tek atis. Auto: tetik basili tutuldukca fireInterval araliginda tarar (overrideFire ile aralik verilmeli).")]
        public FireMode fireMode = FireMode.Semi;

        // Kick alanlari 0 = tepme yok: dokunulmamis bir profil (Paintball) eski davranisini
        // birebir korur, WeaponRecoil hic eklenmez.
        [Header("Tepme (recoil) — aci: derece, mesafe: DUNYA metresi")]
        [Tooltip("Atis basina namlunun yukari kalkisi (derece).")]
        public float kickPitchPerShot;
        [Tooltip("Atis basina rastgele +-yaw sekmesi (derece).")]
        public float kickYawJitter;
        [Tooltip("Atis basina namlu ekseninin tersine geri itilme (dunya metresi).")]
        public float kickBackMeters;
        [Tooltip("Birikmis tirmanis tavani (derece).")]
        public float maxAccumPitch = 8f;
        [Tooltip("Birikmis geri itilme tavani (dunya metresi).")]
        public float maxAccumBack = 0.04f;
        [Tooltip("Ates SURERKEN tepmenin sonme yarilanma suresi (s).")]
        public float recoilDecayHalfLife = 0.12f;
        [Tooltip("Tetik BIRAKILINCA nisan hattina toparlanma yarilanma suresi (s).")]
        public float recoilRestDecayHalfLife = 0.07f;
        [Tooltip("Iki elli tutusta tepme ve sapmaya uygulanan carpan.")]
        public float supportRecoilMultiplier = 0.55f;

        [Header("Sapma (bloom) — derece")]
        [Tooltip("Dinlenmedeki isabet konisi yari-acisi.")]
        public float spreadBase;
        [Tooltip("Her atisin koniye ekledigi buyume.")]
        public float spreadPerShot;
        [Tooltip("Koninin ulasabilecegi en genis yari-aci.")]
        public float spreadMax = 3f;
        [Tooltip("Ates kesilince koninin daralma yarilanma suresi (s).")]
        public float spreadDecayHalfLife = 0.18f;

        [Header("Ates izi (tracer)")]
        [Tooltip("Iz cizgisinin rengi. Gercek 5.56 izli fisegi turuncu-kirmizi yanar.")]
        public Color tracerColor = new Color(1f, 0.45f, 0.12f);
        [Tooltip("Izin ucus hizi (m/s). Gercek mermi ~900 m/s'de gozle takip edilemez; 200-350 arasi hem hizli hem gorunur. 0 = aninda tam boy cizgi (eski davranis).")]
        public float tracerSpeed = 260f;
        [Tooltip("Ucan iz parcasinin uzunlugu (m).")]
        public float tracerLength = 2.5f;
        [Tooltip("Iz cizgisinin kalinligi (m).")]
        public float tracerWidth = 0.03f;
        [Tooltip("Namlu alevinin parlama suresi (s).")]
        public float flashDuration = 0.035f;

        [Header("Mermi izi (carptigi yerde kalan delik)")]
        [Tooltip("Izin rengi. Koyu = kursun deligi; parlak renk = boya lekesi.")]
        public Color impactColor = new Color(0.03f, 0.03f, 0.04f, 1f);
        [Tooltip("Izin capi (m) — yuvarlak disk. Kursun deligi kucuk olmali; iz cizgisi kalinligi civari iyi bir taban. 0 = hic iz birakma.")]
        public float impactSize = 0.022f;

        [Header("Haptik")]
        [Tooltip("Atis aninda ates eden kumandanin titresim siddeti (0..1).")]
        public float hapticAmplitude = 0.7f;
        [Tooltip("Titresim suresi (s).")]
        public float hapticDuration = 0.08f;
        [Tooltip("Destek eli takiliysa o kumandaya giden hafif titresim (0 = kapali).")]
        public float supportHapticAmplitude = 0.35f;

        [Header("Sarjor (0 = mermi sayilmaz, silah sinirsiz ates eder)")]
        [Tooltip("Bir sarjordeki mermi sayisi. 0 birakilirsa mermi sistemi HIC devreye girmez — profilsiz silahin bugunku davranisi aynen korunur.")]
        public int magazineSize;
        [Tooltip("Yedek sarjor sayisi. -1 = sinirsiz (mevcut ayar). 0 = hic yedek yok, sarjor bitince silah susar.")]
        public int spareMagazines = -1;
        [Tooltip("Sarjor degisiminin suresi (s). Bu sure boyunca silah ates edemez.")]
        public float reloadDuration = 1.4f;

        [Header("Sarjor degistirme hareketi (silahi asagi savur, sonra geri kaldir)")]
        [Tooltip("Hareketin sayilmasi icin gereken en dusuk dikey hiz (m/s). Silahi YAVASCA indirip kaldirmak sayilmaz — kazara sarjor degisimini onleyen ana filtre budur. Buyutursen hareket sertlesir.")]
        public float reloadFlickSpeed = 1.3f;
        [Tooltip("Asagi ve yukari fazlarin her birinin kat etmesi gereken en az dikey yol (m). Kucuk titremeleri eler.")]
        public float reloadFlickTravel = 0.25f;
        [Tooltip("Asagi + yukari hareketin tamamlanmasi gereken sure (s). Asilirsa iptal — 'silahi indirdim ve oylece durdum' sarjor degistirmez.")]
        public float reloadFlickWindow = 0.8f;

        [Header("Opsiyonel basit collider degisimi (bos = collider'lara dokunulmaz)")]
        public BoxSpec[] simpleColliders = new BoxSpec[0];

        /// <summary>
        /// Editorde ELLE verilmis parmak pozu: 15 eklemin lokal rotasyonu (sira icin bkz.
        /// <see cref="HandPoseBones"/>). Prosedurel curl'un yapisal limiti yok — parmak yayilmasi,
        /// her parmagin kabzada farkli derinlikte durmasi, basparmagin avuc ustunden capraz
        /// gecmesi, hepsi temsil edilebilir. Ayrica hicbir eksen tahmini yok: rig'in kemik
        /// eksenleri ne kadar tuhaf olursa olsun kaydedilen poz aynen geri gelir.
        ///
        /// Sag ve sol el AYRI yazilir: parmak rotasyonlarini aynalamak rig'in sol/sag kemik
        /// eksenlerinin gercekten simetrik olmasina bagli ve bu garanti edilemez.
        /// </summary>
        [System.Serializable]
        public struct FingerPose
        {
            [Tooltip("15 eklem lokal rotasyonu. Bos = bu el icin eski curl davranisi.")]
            public Quaternion[] joints;

            [Tooltip("Tetik TAM cekiliyken isaret parmaginin 3 bogumu. Bos = isaret parmagi sabit kalir.")]
            public Quaternion[] indexPulled;

            /// <summary>
            /// AYNI pozun BIRINCI SAHIS eli icin karsiligi: 15 eklemin dinlenmeden sapmasi,
            /// FP rig'inin kendi kemik uzayinda.
            ///
            /// Neden ayri alan: yukaridaki quaternion'lar AVATARIN humanoid kemiklerinde yazildi,
            /// FP eli ise Meta'nin Generic rig'ini kullaniyor. Lokal rotasyon iki rig arasinda
            /// dogrudan TASINMAZ - kemiklerin dinlenme yonelimleri farkli.
            ///
            /// Tasima yolu: sapmanin EKSENI ve ACISI. Eksen once avatar kemiginin dinlenme
            /// cercevesinden EL cercevesine (parmak yonu / avuc normali / yan eksen) cikarilir,
            /// oradan FP kemiginin dinlenme cercevesine indirilir; aci aynen kalir. Boylece
            /// yalnizca katlanma degil parmak YAYILMASI ve basparmagin gercek donus yonu de
            /// korunur. Olculdu: sapmayi tek mentese acisina indirgeyen ilk surumde parmak
            /// uclari 25-45 mm, basparmak 105 mm kayiyordu. Cevrim editorde bir kez
            /// yapilir (menu 50), runtime yalnizca uygular.
            /// </summary>
            [Tooltip("FP rig'inde 15 eklemin DINLENMEDEN sapmasi. Uretim: menu 50.")]
            public Quaternion[] fpJoints;

            [Tooltip("Tetik TAM cekiliyken FP eli icin isaret parmaginin 3 sapmasi.")]
            public Quaternion[] fpIndexPulledJoints;

            /// <summary>
            /// <see cref="fpJoints"/> ELLE mi pozlandi (atolyedeki parmak kipi), yoksa
            /// avatardan mi CEVRILDI (menu 50)?
            ///
            /// NEDEN BAYRAK GEREKLI: iki kaynak ayni alani doldurdugu halde GUVENILIRLIKLERI
            /// esit degil. Cevrilmis poz olculdu ve yetersiz bulundu; bu yuzden elle yazilan
            /// <see cref="HandPose.fpCurls"/> onun ONUNE gecirildi. Elle POZLANAN eklemler ise
            /// fpCurls'ten de iyidir - fpCurls parmak basina TEK sayidir, bu ise eklem basina
            /// tam rotasyon. Bayrak olmadan ikisi ayirt edilemez ve elle yapilan poz, parmak
            /// basina tek sayiya duserdi.
            ///
            /// Oncelik (bkz. FirstPersonFingerCurl.LateUpdate):
            ///   fpJointsAuthored  >  fpCurls  >  fpJoints (cevrilmis)  >  prosedurel
            /// </summary>
            [Tooltip("fpJoints atolyede ELLE pozlandi (cevrilmis degil). fpCurls'un onune gecer.")]
            public bool fpJointsAuthored;

            /// <summary>Elle pozlanmis, uygulanabilir bir eklem seti var mi?</summary>
            public bool HasAuthoredFpJoints => fpJointsAuthored && HasFpJoints;

            public bool HasPose => joints != null && joints.Length == HandPoseBones.JointCount;
            public bool HasIndexPulled => indexPulled != null && indexPulled.Length == HandPoseBones.IndexJointCount;
            public bool HasFpJoints => fpJoints != null && fpJoints.Length == HandPoseBones.JointCount;
            public bool HasFpIndexPulled => fpIndexPulledJoints != null && fpIndexPulledJoints.Length == HandPoseBones.IndexJointCount;
        }

        /// <summary>Static hand pose: wrist offset + five finger curls (ISDK Fingers Freedom:
        /// locked curls, index optionally Free = driven by the trigger axis).</summary>
        [System.Serializable]
        public struct HandPose
        {
            [Tooltip("Bilek kemiginin cipaya gore lokal pozisyonu (ana el: kabza; destek: ray noktasi).")]
            public Vector3 wristLocalPosition;
            [Tooltip("Bilek kemiginin cipaya gore lokal yonelimi (euler).")]
            public Vector3 wristLocalEuler;

            /// <summary>
            /// BIRINCI SAHIS elinin cipaya gore yeri. Avatarin bilek offset'inden AYRI
            /// tutuluyor cunku iki rig'in bilek konvansiyonu ayni degil: FP eli avatarin
            /// degerini miras alinca cihazda kabzanin yanina dusuyordu.
            ///
            /// Sifir = FP bilegi tam kabza cipasinda, silahin kendi yoneliminde. Ayar
            /// oyun icindeki Silah Atolyesi panelinden yapilir (gorerek), buraya yazilir.
            /// </summary>
            public Vector3 fpWristLocalPosition;
            public Vector3 fpWristLocalEuler;

            public Quaternion FpWristRotation => Quaternion.Euler(fpWristLocalEuler);

            /// <summary>
            /// FP eli icin PARMAK BASINA kivrim (0..1; sira: bas, isaret, orta, yuzuk, serce).
            /// Silah Atolyesi'nde elle yaziliyor.
            ///
            /// Neden fpJoints'in yaninda ayri bir alan: fpJoints avatardan CEVRILEN pozdur ve
            /// cevrim guvenilir cikmadi (yuzuk parmaginda 17-19 derece sistematik sapma).
            /// Buradaki degerler cevrilmiyor - dogrudan FP rig'inin kendi mentese kuralina
            /// (FingerCurlMath) uygulaniyor, yani gordugun sey yazdigin sey. Doluysa
            /// fpJoints'in ONUNE gecer.
            /// </summary>
            public float[] fpCurls;

            public bool HasFpCurls => fpCurls != null && fpCurls.Length == 5;

            [Range(0f, 1f)] public float thumbCurl;
            [Range(0f, 1f)] public float indexCurl;
            [Range(0f, 1f)] public float middleCurl;
            [Range(0f, 1f)] public float ringCurl;
            [Range(0f, 1f)] public float pinkyCurl;

            [Tooltip("Isaret parmagi Free: tetik 0..1, indexCurl'den indexTriggerMaxCurl'e surer.")]
            public bool indexFollowsTrigger;

            [Tooltip("Tetik TAM cekiliyken isaret parmaginin kivrim tavani (0..1). Tetik cekisi " +
                     "kucuk bir harekettir — 1.0 tam yumruk yapar. 0 birakilirsa 1 sayilir " +
                     "(eski assetler).\n\n" +
                     "ELLE POZLANMIS tutuslarda (atolye parmak kipi) anlami: tetigin tabana " +
                     "EKLEYECEGI kivrimin olcegi. Tetik parmak animasyonunun ayar dugmesi budur — " +
                     "olculdu, 0.55'te ~15 derece, 1.00'de ~30 derece ek kapanma.")]
            [Range(0f, 1f)] public float indexTriggerMaxCurl;

            [Header("Authored parmak pozu (doluysa yukaridaki curl'lerin yerine gecer)")]
            [Tooltip("Bu tutus SAG elle yapildiginda kullanilir.")]
            public FingerPose rightFingers;
            [Tooltip("Bu tutus SOL elle yapildiginda kullanilir.")]
            public FingerPose leftFingers;

            /// <summary>Pozu tutan FIZIKSEL ele gore sec — aynalama gerekmez, iki el ayri yazilir.</summary>
            public FingerPose Fingers(bool left) => left ? leftFingers : rightFingers;

            /// <summary>Curl by finger id: 0 thumb, 1 index, 2 middle, 3 ring, 4 pinky.</summary>
            public float Curl(int finger)
            {
                switch (finger)
                {
                    case 0: return thumbCurl;
                    case 1: return indexCurl;
                    case 2: return middleCurl;
                    case 3: return ringCurl;
                    default: return pinkyCurl;
                }
            }

            /// <summary>Seed values from the hand-tuning session (proximal 40/inter 45/distal 25
            /// era): a firm but not buried wrap, thumb resting.</summary>
            public static HandPose Defaults(bool indexFollowsTrigger) => new HandPose
            {
                thumbCurl = 0f,
                indexCurl = 0.55f,
                middleCurl = 0.75f,
                ringCurl = 0.8f,
                pinkyCurl = 0.85f,
                indexFollowsTrigger = indexFollowsTrigger,
                indexTriggerMaxCurl = 1f,
            };
        }

        [System.Serializable]
        public struct BoxSpec
        {
            public Vector3 center;
            public Vector3 size;
        }

        /// <summary>Match strength for a weapon name: 2 exact, 1 contains, 0 none.</summary>
        public int MatchScore(string weaponName)
        {
            if (string.IsNullOrEmpty(weaponName)) return 0;
            if (!string.IsNullOrEmpty(weaponNameEquals) && weaponName == weaponNameEquals) return 2;
            if (!string.IsNullOrEmpty(weaponNameContains) && weaponName.Contains(weaponNameContains)) return 1;
            return 0;
        }

        public Quaternion GripLocalRotation => Quaternion.Euler(gripLocalEuler);
    }
}
