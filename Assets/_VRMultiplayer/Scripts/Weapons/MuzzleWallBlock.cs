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

        [Tooltip("Kilit devreye girdigi AN kisa bir titresim (namlu duvara girer girmez, tetige " +
                 "basmadan). Tetik cekilince verilen AYRI ve daha guclu titresim icin bkz. " +
                 "NetworkWeapon.BlockedFire.")]
        public bool hapticOnBlock = true;

        [Tooltip("Namlunun duvara girdigi noktada kirmizi uyari halkasi. Duvarin OYUNCU " +
                 "tarafindaki yuzune konur ve geometrinin ustune cizilir, yani namlu tamamen " +
                 "icerideyken bile gorunur.")]
        public bool showBlockMarker = true;

        [Tooltip("Kilitliyken lazer nisani sonsun. Kilitli silahin hala nisan gostermesi " +
                 "'ates edebilirim' yanilgisi veriyor.")]
        public bool suppressLaser = true;

        [Tooltip("Uyari halkasinin capi (m).")]
        public float markerSize = 0.05f;

        [Tooltip("Kac karede bir olculur. 1 = her kare. Silah sayisi arttikca 2 yapilabilir; " +
                 "20 ms'lik gecikme tarama hilesine yetmez.")]
        [Range(1, 4)] public int checkEveryNthFrame = 1;

        /// <summary>Su an ates KILITLI mi? NetworkWeapon her karede bunu okur.</summary>
        public bool IsBlocked { get; private set; }

        /// <summary>Namlunun duvara girdigi nokta (duvarin oyuncu tarafindaki yuzu).
        /// Yalnizca <see cref="IsBlocked"/> true iken anlamli.</summary>
        public Vector3 BlockPoint { get; private set; }

        /// <summary>Uyari halkasinin rengi — klasik "yasak" kirmizisi.</summary>
        static readonly Color MarkerColor = new Color(1f, 0.15f, 0.12f, 0.85f);

        /// <summary>Ayni anda degerlendirilen collider tavani (namlu ucu kuresi cok kucuk).</summary>
        const int MaxOverlap = 8;

        static readonly Collider[] _overlap = new Collider[MaxOverlap];
        static readonly RaycastHit[] _rayHits = new RaycastHit[16];

        NetworkWeapon _weapon;
        GrabbableObject _grab;
        UI.LaserSight _laser;
        Transform _marker;
        bool _prevBlocked;
        int _frame;

        void Awake()
        {
            _weapon = GetComponent<NetworkWeapon>();
            _grab = GetComponent<GrabbableObject>();
        }

        void Update()
        {
            // Yerde duran silah icin olcmeye gerek yok. Ama geri bildirimleri KAPATMAK gerekir:
            // namlusu duvardayken birakilan silahin halkasi sahnede asili kalir, lazeri de
            // sonuk dogar.
            if (_grab == null || !_grab.IsHeld)
            {
                IsBlocked = false;
                _prevBlocked = false;
                ApplyLaser(false);
                ApplyMarker(false);
                return;
            }

            // Olcum throttle'lanabilir; halka ve lazer HER kare guncellenir, yoksa isaret
            // silahin arkasindan seke seke gelir.
            if (checkEveryNthFrame > 1 && (++_frame % checkEveryNthFrame) != 0)
            {
                bool held = HoldingLocally();
                ApplyLaser(IsBlocked && held);
                ApplyMarker(IsBlocked && held);
                return;
            }

            // DIKKAT: bu HER kopyada calisir, yalnizca tutan istemcide degil. Sunucudaki kopya
            // da olcsun diye boyle: FireServerRpc otoriteyi buradan okur ve sunucunun KENDI
            // replike transformundan cikan sonucu kullanir. Istemci kilidi atlatsa bile
            // sunucudaki bu deger atisi keser.
            _weapon.GetAimRay(out Vector3 origin, out Vector3 dir);
            IsBlocked = Evaluate(origin, dir);

            // Geri bildirimlerin TAMAMI sadece silahi fiilen tutan oyuncuya. Bu kontrol
            // olmasaydi host, karsi takimdan biri namlusunu duvara soktugunda kendi kumandasini
            // titretir ve kendi ekraninda halka gorurdu.
            bool mine = HoldingLocally();

            if (IsBlocked && !_prevBlocked && hapticOnBlock && mine)
                BuzzHolder();

            ApplyLaser(IsBlocked && mine);
            ApplyMarker(IsBlocked && mine);

            _prevBlocked = IsBlocked;
        }

        void ApplyLaser(bool blocked)
        {
            if (!suppressLaser) return;
            if (_laser == null) _laser = GetComponent<UI.LaserSight>();
            // Lazer spawn'da WeaponLaserBinder tarafindan ekleniyor; ilk karelerde henuz
            // olmayabilir, o yuzden her seferinde arayip null'a tahammul ediyoruz.
            if (_laser != null) _laser.Suppressed = blocked;
        }

        void ApplyMarker(bool blocked)
        {
            if (!showBlockMarker)
            {
                if (_marker != null) _marker.gameObject.SetActive(false);
                return;
            }

            if (!blocked)
            {
                if (_marker != null && _marker.gameObject.activeSelf)
                    _marker.gameObject.SetActive(false);
                return;
            }

            if (_marker == null) BuildMarker();
            if (!_marker.gameObject.activeSelf) _marker.gameObject.SetActive(true);

            // Kameraya donuk dursun: halka egik bir duvarda elips gibi gorunmesin, her acidan
            // ayni okunsun. Duvarin normaline dikmek daha "fiziksel" olurdu ama tam tepeden
            // bakildiginda cizgiye donerdi.
            var cam = Camera.main;
            _marker.position = BlockPoint;
            if (cam != null)
                _marker.rotation = Quaternion.LookRotation(BlockPoint - cam.transform.position, Vector3.up);
        }

        void BuildMarker()
        {
            var go = new GameObject("~NamluKilitIsareti");
            _marker = go.transform;

            // ArcMesh + overlay materyal: ikisi de UITheme'de hazir. Overlay renderQueue'su
            // halkanin duvarin ICINDEN de gorunmesini saglar — namlu tamamen gomuluyken
            // isaret duvarin arkasinda kalsaydi hicbir ise yaramazdi.
            var mesh = UI.UITheme.ArcMesh(0.34f, 0.5f, 0f, 360f, 24);
            var ring = UI.UITheme.MakeShape(_marker, "Halka", mesh, MarkerColor, 4000);
            ring.localScale = Vector3.one * markerSize;
        }

        bool HoldingLocally()
        {
            var nm = _grab.NetworkManager;
            return nm != null && _grab.HolderClientId == nm.LocalClientId;
        }

        /// <summary>Verilen namlu ucu + yon icin kilit gerekli mi? Yan urun olarak
        /// <see cref="BlockPoint"/>'i de doldurur (uyari halkasinin duracagi yer).
        /// Sunucudaki kopya da bunu Update'ten cagirir ve FireServerRpc sonucu oradan okur —
        /// istemci ile sunucu farkli geometri kullansaydi mesru atislar yenirdi.</summary>
        public bool Evaluate(Vector3 origin, Vector3 dir)
        {
            // 1) Namlu ucu bir duvarin ICINDE mi? Isaret namlu ucunun kendisi.
            int n = Physics.OverlapSphereNonAlloc(origin, muzzleRadius, _overlap, solidMask,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (WorldSolids.IsSolid(_overlap[i], transform)) { BlockPoint = origin; return true; }

            // 2) Namlu ucu ile silah govdesi arasinda duvar var mi? (asil hile)
            // Isini GERIYE degil, geriden ILERI atiyoruz: boylece bulunan EN YAKIN temas
            // duvarin OYUNCU TARAFINDAKI yuzu olur — uyari halkasi da orada durmali, yoksa
            // duvarin arkasinda kalir ve oyuncu hicbir sey gormez.
            if (barrelBackDistance > 0f)
            {
                Vector3 back = origin - dir * barrelBackDistance;
                if (NearestSolidAlong(back, dir, barrelBackDistance, out Vector3 hit))
                { BlockPoint = hit; return true; }
            }

            // 3) Namlu duvara DAYALI mi? (onunde nefes alacak kadar bosluk yok)
            if (minClearance > 0f && NearestSolidAlong(origin, dir, minClearance, out Vector3 ahead))
            { BlockPoint = ahead; return true; }

            return false;
        }

        /// <summary>Isin parcasi uzerindeki EN YAKIN kati temas. RaycastNonAlloc mesafeye gore
        /// SIRALAMAZ, o yuzden en yakini elle ariyoruz (SpawnRouteGuide'da ayni desen).</summary>
        bool NearestSolidAlong(Vector3 from, Vector3 dir, float distance, out Vector3 point)
        {
            int n = Physics.RaycastNonAlloc(from, dir, _rayHits, distance, solidMask,
                QueryTriggerInteraction.Ignore);

            float best = float.PositiveInfinity;
            point = from;
            for (int i = 0; i < n; i++)
            {
                if (!WorldSolids.IsSolid(_rayHits[i].collider, transform)) continue;
                if (_rayHits[i].distance >= best) continue;
                best = _rayHits[i].distance;
                point = _rayHits[i].point;
            }
            return !float.IsPositiveInfinity(best);
        }

        void OnDestroy()
        {
            // Halka silahin ALTINDA degil, sahne kokunde duruyor (duvara yapissin diye) —
            // silah yok edilince onu kimse toplamaz.
            if (_marker != null) Destroy(_marker.gameObject);
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
