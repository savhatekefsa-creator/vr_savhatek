using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// NAMLU DUVARDAYSA TETIK KILITLI. Oyuncu oda-olceginde yurudugu icin silahini duvarin
    /// icine/arkasina sokabiliyor; namlu obur tarafa gecince mermi engelsiz cikiyor ve
    /// siperin arkasindan tarama yapilabiliyordu. Bu bilesen silahin uzerinde durur,
    /// <see cref="NetworkWeapon"/> her karede <see cref="IsBlocked"/> diye sorar.
    ///
    /// UC AYRI OLCUM (herhangi biri tutarsa kilit):
    ///   1) NAMLU UCU ICERIDE  — namlu agzi bir duvarin govdesinin icinde (kalin duvar).
    ///   2) NAMLU DUVARI DELMIS — namlu ucu ile silah govdesi ARASINDA duvar var. Asil hile
    ///      budur: ince duvarda namlu ucu obur tarafta bos havada durur, 1. olcum gormez.
    ///   3) NAMLU DUVARA DAYALI — namlu onunde <see cref="minClearance"/> kadar bosluk yok.
    ///      (0 yapilirsa bu olcum kapanir.)
    ///
    /// NEDEN ISIN KAYNAGI NetworkWeapon.GetAimRay: ates de nisan da durbun de o metottan okur.
    /// Kilit ayri bir "namlu tahmini" kullansaydi, kilidin baktigi yer ile merminin ciktigi yer
    /// ayrisirdi — kilit ya bos yere kapanir ya da acigi kacirirdi.
    ///
    /// KURULUM: silah PREFABININ kokune ekle (NetworkWeapon ile ayni GameObject). Baska hicbir
    /// sey gerekmez; alan doldurmak zorunda degilsin, varsayilanlar calisir durumda.
    /// </summary>
    [RequireComponent(typeof(NetworkWeapon))]
    [DisallowMultipleComponent]
    public class MuzzleWallBlock : MonoBehaviour
    {
        [Tooltip("Duvar sayilacak katmanlar. Projede ozel katman olmadigi icin varsayilan HEPSI; " +
                 "silahin kendisi, oyuncular ve tasinabilir propler WorldSolids'te elenir.")]
        public LayerMask solidMask = ~0;

        [Tooltip("Namlu ucunun 'icerideyim' testinde kullandigi kure yaricapi (m). Buyutmek " +
                 "kilidi erken devreye sokar.")]
        public float muzzleRadius = 0.04f;

        [Tooltip("Namlu ucundan silah govdesine dogru taranan mesafe (m). Bu aralikta duvar " +
                 "varsa namlu duvari DELMIS demektir. Uzun namlulu silahlarda buyutulebilir.")]
        public float barrelBackDistance = 0.35f;

        [Tooltip("Namlunun onunde olmasi gereken en az bosluk (m). 0 = 'duvara dayali' olcumu kapali.")]
        public float minClearance = 0.05f;

        [Tooltip("Kilit devreye girdigi AN kisa bir titresim. Oyuncu silahin bozuldugunu " +
                 "sanmasin, 'buradan atamiyorum'u hissetsin.")]
        public bool hapticOnBlock = true;

        [Tooltip("Kac karede bir olculur. 1 = her kare. Silah sayisi arttikca 2 yapilabilir; " +
                 "20 ms'lik gecikme tarama hilesine yetmez.")]
        [Range(1, 4)] public int checkEveryNthFrame = 1;

        /// <summary>Su an ates KILITLI mi? NetworkWeapon her karede bunu okur.</summary>
        public bool IsBlocked { get; private set; }

        /// <summary>Ayni anda degerlendirilen collider tavani (namlu ucu kuresi cok kucuk).</summary>
        const int MaxOverlap = 8;

        static readonly Collider[] _overlap = new Collider[MaxOverlap];
        static readonly RaycastHit[] _rayHits = new RaycastHit[16];

        NetworkWeapon _weapon;
        GrabbableObject _grab;
        bool _prevBlocked;
        int _frame;

        void Awake()
        {
            _weapon = GetComponent<NetworkWeapon>();
            _grab = GetComponent<GrabbableObject>();
        }

        void Update()
        {
            // Yerde duran silah icin olcmeye gerek yok.
            if (_grab == null || !_grab.IsHeld)
            {
                IsBlocked = false;
                _prevBlocked = false;
                return;
            }

            if (checkEveryNthFrame > 1 && (++_frame % checkEveryNthFrame) != 0) return;

            // DIKKAT: bu HER kopyada calisir, yalnizca tutan istemcide degil. Sunucudaki kopya
            // da olcsun diye boyle: FireServerRpc otoriteyi buradan okur ve sunucunun KENDI
            // replike transformundan cikan sonucu kullanir. Istemci kilidi atlatsa bile
            // sunucudaki bu deger atisi keser.
            _weapon.GetAimRay(out Vector3 origin, out Vector3 dir);
            IsBlocked = Evaluate(origin, dir);

            // Titresim SADECE silahi fiilen tutan oyuncuya. Bu kontrol olmasaydi host, karsi
            // takimdan biri namlusunu duvara soktugunda kendi kumandasini titretirdi.
            if (IsBlocked && !_prevBlocked && hapticOnBlock && HoldingLocally())
                BuzzHolder();
            _prevBlocked = IsBlocked;
        }

        bool HoldingLocally()
        {
            var nm = _grab.NetworkManager;
            return nm != null && _grab.HolderClientId == nm.LocalClientId;
        }

        /// <summary>Verilen namlu ucu + yon icin kilit gerekli mi? Sunucu da AYNI metodu
        /// cagirir — istemci ile sunucu farkli geometri kullansaydi mesru atislar yenirdi.</summary>
        public bool Evaluate(Vector3 origin, Vector3 dir)
        {
            // 1) Namlu ucu bir duvarin ICINDE mi?
            int n = Physics.OverlapSphereNonAlloc(origin, muzzleRadius, _overlap, solidMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (WorldSolids.IsSolid(_overlap[i], transform)) return true;

            // 2) Namlu ucu ile silah govdesi arasinda duvar var mi? (asil hile)
            // Isini GERIYE degil, geriden ILERI atiyoruz: RaycastNonAlloc sirali dondurmez,
            // ama tek bir kati temas bile yeter — yon secimi sadece okunurluk icin.
            if (barrelBackDistance > 0f)
            {
                Vector3 back = origin - dir * barrelBackDistance;
                if (AnySolidAlong(back, dir, barrelBackDistance)) return true;
            }

            // 3) Namlu duvara DAYALI mi? (onunde nefes alacak kadar bosluk yok)
            if (minClearance > 0f && AnySolidAlong(origin, dir, minClearance)) return true;

            return false;
        }

        /// <summary>Verilen isin parcasi uzerinde KATI geometri var mi?</summary>
        bool AnySolidAlong(Vector3 from, Vector3 dir, float distance)
        {
            int n = Physics.RaycastNonAlloc(from, dir, _rayHits, distance, solidMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (WorldSolids.IsSolid(_rayHits[i].collider, transform)) return true;
            return false;
        }

        void BuzzHolder()
        {
            byte hand = _grab.HolderHand;
            if (hand == GrabbableObject.NoHand) return;
            var dev = InputDevices.GetDeviceAtXRNode(hand == 0 ? XRNode.LeftHand : XRNode.RightHand);
            if (dev.isValid) dev.SendHapticImpulse(0, 0.35f, 0.05f);
        }
    }
}
