using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>How the trigger maps to shots: one per press, or a sustained burst.</summary>
    public enum FireMode
    {
        Semi,
        Auto,
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
        [Tooltip("Silah GameObject adi bunu ICERIYORSA eslesir (Equals eslesmezse bakilir).")]
        public string weaponNameContains = "";

        [Header("Kabza cipasi (silah-lokal kumanda pozu — yakalama araciyla uretilir)")]
        [Tooltip("Ana elin kavradigi nokta, silah-lokal.")]
        public Vector3 gripLocalPosition;
        [Tooltip("Kabza cipasinin silah-lokal yonelimi (euler). Kumandanin GERCEK tutus egimi — namluya paralel olmak zorunda degil.")]
        public Vector3 gripLocalEuler;

        [Tooltip("Namlunun silah-LOKAL yonu (Muzzle forward). Iki elli nisan BU ekseni hedefe hizalar; kabza cipasi egik yakalanmis olsa bile namlu dogru doner.")]
        public Vector3 barrelLocalDirection = Vector3.forward;

        [Header("Ana el (kabza)")]
        public HandPose mainHand = HandPose.Defaults(true);

        [Header("Destek rayi (kundak; silah-lokal dogru parcasi)")]
        public Vector3 supportRailLocalStart;
        public Vector3 supportRailLocalEnd;
        [Tooltip("Kumanda raydan bu kadar uzaklasirsa destek eli otomatik birakilir (metre).")]
        public float supportBreakDistance = 0.30f;
        public HandPose supportHand = HandPose.Defaults(false);

        [Header("Iki elli nisan filtresi")]
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
        [Tooltip("Izin capi (m). 0 = hic iz birakma.")]
        public float impactSize = 0.06f;

        [Header("Haptik")]
        [Tooltip("Atis aninda ates eden kumandanin titresim siddeti (0..1).")]
        public float hapticAmplitude = 0.7f;
        [Tooltip("Titresim suresi (s).")]
        public float hapticDuration = 0.08f;
        [Tooltip("Destek eli takiliysa o kumandaya giden hafif titresim (0 = kapali).")]
        public float supportHapticAmplitude = 0.35f;

        [Header("Opsiyonel basit collider degisimi (bos = collider'lara dokunulmaz)")]
        public BoxSpec[] simpleColliders = new BoxSpec[0];

        /// <summary>Static hand pose: wrist offset + five finger curls (ISDK Fingers Freedom:
        /// locked curls, index optionally Free = driven by the trigger axis).</summary>
        [System.Serializable]
        public struct HandPose
        {
            [Tooltip("Bilek kemiginin cipaya gore lokal pozisyonu (ana el: kabza; destek: ray noktasi).")]
            public Vector3 wristLocalPosition;
            [Tooltip("Bilek kemiginin cipaya gore lokal yonelimi (euler).")]
            public Vector3 wristLocalEuler;

            [Range(0f, 1f)] public float thumbCurl;
            [Range(0f, 1f)] public float indexCurl;
            [Range(0f, 1f)] public float middleCurl;
            [Range(0f, 1f)] public float ringCurl;
            [Range(0f, 1f)] public float pinkyCurl;

            [Tooltip("Isaret parmagi Free: tetik 0..1, indexCurl'den indexTriggerMaxCurl'e surer.")]
            public bool indexFollowsTrigger;

            [Tooltip("Tetik TAM cekiliyken isaret parmaginin kivrim tavani (0..1). Tetik cekisi kucuk bir harekettir — 1.0 tam yumruk yapar. 0 birakilirsa 1 sayilir (eski assetler).")]
            [Range(0f, 1f)] public float indexTriggerMaxCurl;

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
