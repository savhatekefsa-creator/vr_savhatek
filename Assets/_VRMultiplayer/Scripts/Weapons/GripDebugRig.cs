using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// FAZ 0 OLCUM ARACI — hicbir davranisi degistirmez, sadece gorunur kilar.
    ///
    /// "Silah 15 derece yamuk duruyor" bir his; bu bilesen onu sayiya cevirir. Uc sey yapar:
    ///
    /// 1) EKSEN CUBUKLARI: kumanda cipalarina (HandGrabber'in LeftAnchor/RightAnchor'i) ve
    ///    tutulan silahin namlu eksenine gercek GameObject cubuklari koyar (kirmizi=X,
    ///    yesil=Y, mavi=Z; namlu=sari). Gizmo DEGIL — Quest build'inde gorunur.
    ///
    /// 2) IKI AYRI SAYI. Bunlari karistirmamak kritik:
    ///    - HAM (kumanda -> silah): silahin donusunun kumanda cipasina gore sapmasi. Bu,
    ///      kodun uyguladigi <c>rotOffset</c>'in ta kendisi (bkz. HandGrabber.SnapRotOffset /
    ///      profilin gripLocalEuler'i). Silah basina SABIT olmali; degisiyorsa kaynak
    ///      belirsiz demektir. Faz 1 bu sayiyi duzeltir.
    ///    - NISAN HATASI (baktigin yer -> namlu): 3 m onundeki isaretcinin merkezine dogal
    ///      tutusla nisan alip olculur. Oyuncunun HISSETTIGI hata budur.
    ///
    /// 3) 0.3 TESTLERI: kaynagi ayirmak icin weld ve parmak pozlayiciyi cihazda, build
    ///    almadan kapatip acar (mod dongusu).
    ///
    /// KURULUM: NetworkPlayer prefab'inin KOKUNE ekle (HandGrabber'in oldugu obje).
    /// Sahiplik kontrolu icerdedir; baskasinin avatarinda hicbir sey cizmez.
    ///
    /// TUSLAR (carpismayi onlemek icin CIFT tus / akor — tek tuslar TeamSelector,
    /// RoomScanSync, CalibrationManager ve ConstructorPlacer tarafindan kullaniliyor):
    ///   A + X (iki elin primary'si)      -> test modunu ilerlet
    ///   B + Y kisa bas                   -> o anki olcumu TABLOYA yaz (satiri dondurur)
    ///   B + Y 1.5 sn BASILI TUT          -> tabloyu .md dosyasina kaydet
    ///
    /// DOSYA: <c>&lt;persistentDataPath&gt;/GripOlcum/grip-olcum-NNN.md</c>. Editorde (Link ile
    /// oynarken) ayrica projenin kok klasorundeki <c>GripOlcum/</c> altina bir kopya yazilir —
    /// Assets'in DISINA, yoksa her kayit bir asset import'u tetiklerdi.
    ///
    /// SEVKIYAT ONCESI: <see cref="showOverlay"/> kapali olmali (varsayilan acik — bu bir
    /// olcum dali icin yazildi).
    /// </summary>
    [DefaultExecutionOrder(200)] // weld (110) ve poser (100) yazdiktan SONRA olc: son poz bu
    public class GripDebugRig : MonoBehaviour
    {
        public enum TestMode
        {
            Normal = 0,      // her sey acik
            WeldOff = 1,     // WeaponHandWeld kapali  -> bozulma geciyorsa weld/IK catismasi
            FingersOff = 2,  // parmak pozlayici kapali -> geciyorsa curl
            WeldAndFingersOff = 3,
        }

        [Header("Gorunurluk")]
        [Tooltip("Paneli ve eksen cubuklarini goster. SEVKIYATTA KAPAT.")]
        public bool showOverlay = true;
        [Tooltip("Eksen cubuklarinin boyu (metre).")]
        public float axisLength = 0.10f;
        [Tooltip("Cubuk kalinligi (metre).")]
        public float axisThickness = 0.004f;
        [Tooltip("Nisan isaretcisinin kafadan uzakligi (metre). Baktigin yere kilitlidir: " +
                 "'silah baktigim yere baksin' tanimini olculebilir yapar.")]
        public float aimMarkerDistance = 3f;

        [Header("Olcum")]
        [Tooltip("Panelin tazelenme araligi (s). Okumak icin; kare basina hassasiyet gerekmez.")]
        public float refreshInterval = 0.1f;
        [Tooltip("El bu esikten yavas hareket ediyorsa olcum 'sakin' sayilir ve ortalamaya " +
                 "katilir (m/s). Savrulurken alinan ornek tabloyu kirletir.")]
        public float steadySpeed = 0.15f;

        [Header("Test modu (cihazda A+X ile de degistirilebilir)")]
        public TestMode mode = TestMode.Normal;

        [Header("Kayit")]
        [Tooltip("Uretilen dosyanin adi (sonuna -001, -002 ... eklenir).")]
        public string fileName = "grip-olcum";
        [Tooltip("B+Y bu kadar sure basili tutulunca dosya yazilir (s). Kisa basis satiri dondurur.")]
        public float saveHoldSeconds = 1.5f;

        // ─── Tablo satiri: silah basina biriken sakin-ornek ortalamasi ────────────────────
        class Row
        {
            public string weapon;
            public bool leftHand;
            public string barrelSource;
            public int samples;
            public float yaw, pitch, roll, total;      // HAM (kumanda -> silah), ortalama
            public float aimYaw, aimPitch, aimRoll, aimTotal; // NISAN hatasi, ortalama
            public bool frozen;                         // B+Y ile donduruldu
        }

        HandGrabber _grabber;
        readonly List<Row> _rows = new List<Row>();
        readonly Dictionary<string, Row> _byName = new Dictionary<string, Row>();
        readonly StringBuilder _sb = new StringBuilder(512);

        TextMesh _panel;
        Transform _leftAxes, _rightAxes, _weaponAxes, _barrelRod, _aimMarker;
        float _nextRefresh;
        Vector3 _prevLeftPos, _prevRightPos;
        bool _prevChordMode, _prevChordRecord;
        float _recHeldSince;
        bool _savedThisHold;
        string _saveStatus = "";

        WeaponHandWeld _weld;
        ProceduralFingerPoser _poser;
        WeaponGripTuner _tuner;

        /// <summary>Tuner acikken bu arac TUSLARI BIRAKIR ve panelini kapatir; yalnizca eksen
        /// cubuklari kalir. Ikisi de A+X akorunu dinliyor: birlikte aciklarken her kayitta
        /// test modu da degisir (el silahtan kopar) ve iki panel ust uste biner. Kullaniciya
        /// "ikisini birden acma" demek yerine cakismayi burada cozuyoruz — cubuklar ayar
        /// sirasinda zaten gerekli, kumandanin gercek eksenini gormeden ne ortaladigini
        /// bilemezsin.</summary>
        bool TunerActive => _tuner != null && _tuner.tuning;

        void Awake()
        {
            _grabber = GetComponent<HandGrabber>();
            _tuner = GetComponent<WeaponGripTuner>();
        }

        void OnDisable() => TearDown();

        void LateUpdate()
        {
            // Her istemci HER avatari simule eder; baskasininki de panel acsa panelller
            // ust uste binerdi. Yalnizca giydigin avatar rapor verir.
            var netObj = GetComponent<NetworkObject>();
            if (!showOverlay || _grabber == null || (netObj != null && !netObj.IsOwner))
            {
                TearDown();
                return;
            }

            EnsureVisuals();
            ReadChords();
            ApplyTestMode();

            Transform lA = _grabber.LeftAnchor, rA = _grabber.RightAnchor;
            PlaceTriad(_leftAxes, lA);
            PlaceTriad(_rightAxes, rA);
            PlaceAimMarker();

            var held = FindHeldWeapon(out bool leftHand);
            Transform anchor = leftHand ? lA : rA;

            // Sakinlik: savrulan elden alinan ornek tabloyu kirletir (notlardaki
            // "ornek kirliligi" dersi). Hiz cipanin kendi hareketinden olculur.
            bool steady = false;
            if (anchor != null)
            {
                Vector3 prev = leftHand ? _prevLeftPos : _prevRightPos;
                float dt = Mathf.Max(Time.deltaTime, 1e-4f);
                steady = ((anchor.position - prev).magnitude / dt) < steadySpeed;
            }
            if (lA != null) _prevLeftPos = lA.position;
            if (rA != null) _prevRightPos = rA.position;

            if (held != null && anchor != null)
                Measure(held, anchor, leftHand, steady);
            else
                HideWeaponVisuals();

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + refreshInterval;
                Redraw(held, leftHand, steady);
            }
        }

        // ─── Olcum ────────────────────────────────────────────────────────────────────────

        void Measure(GrabbableObject held, Transform anchor, bool leftHand, bool steady)
        {
            Transform w = held.transform;
            Vector3 barrelLocal = BarrelLocalDirection(held, out string source);
            Vector3 barrel = (w.rotation * barrelLocal).normalized;
            Vector3 top = w.up; // yazarlama sozlesmesi: silah +Y = ust ray

            _weaponAxes.gameObject.SetActive(true);
            _barrelRod.gameObject.SetActive(true);
            PlaceTriad(_weaponAxes, w);
            _barrelRod.SetPositionAndRotation(w.position, Quaternion.LookRotation(barrel, top));

            // ── HAM: namlu, kumanda cipasinin eksenlerine gore nerede duruyor? ──
            // Bu ucluyu HandGrabber dogrudan uretiyor; Faz 1'in duzeltecegi sayi bu.
            Vector3 bInAnchor = anchor.InverseTransformDirection(barrel);
            float yaw = Mathf.Atan2(bInAnchor.x, bInAnchor.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(bInAnchor.y, -1f, 1f)) * Mathf.Rad2Deg;
            float roll = SignedRoll(barrel, top, anchor.up);
            float total = Vector3.Angle(anchor.forward, barrel);

            // ── NISAN HATASI: baktigin noktaya gore namlu nerede? ──
            // Isaretci kafaya kilitli, yani "baktigim yere nisan aliyorum" tanimi.
            float aYaw = 0f, aPitch = 0f, aRoll = 0f, aTotal = 0f;
            if (_aimMarker != null)
            {
                Vector3 muzzlePos = MuzzlePosition(w, barrel);
                Vector3 want = (_aimMarker.position - muzzlePos).normalized;
                if (want.sqrMagnitude > 1e-6f)
                {
                    // Sapmayi oyuncunun cercevesinde ver: yaw = saga/sola, pitch = yukari/asagi.
                    Quaternion frame = Quaternion.LookRotation(want, Vector3.up);
                    Vector3 bInAim = Quaternion.Inverse(frame) * barrel;
                    aYaw = Mathf.Atan2(bInAim.x, bInAim.z) * Mathf.Rad2Deg;
                    aPitch = Mathf.Asin(Mathf.Clamp(bInAim.y, -1f, 1f)) * Mathf.Rad2Deg;
                    aRoll = SignedRoll(barrel, top, Vector3.up); // yatiklik: dunya yukarisina gore
                    aTotal = Vector3.Angle(want, barrel);
                }
            }

            if (!steady) return;

            var row = GetRow(held.name, leftHand, source);
            if (row.frozen) return;

            // Kosan ortalama: tek kare degil, sakin orneklerin ortalamasi. Titremeyi eler,
            // "bazen 5 bazen 15" sorusuna tek bir sayiyla cevap verir.
            int n = ++row.samples;
            row.yaw += (yaw - row.yaw) / n;
            row.pitch += (pitch - row.pitch) / n;
            row.roll += (roll - row.roll) / n;
            row.total += (total - row.total) / n;
            row.aimYaw += (aYaw - row.aimYaw) / n;
            row.aimPitch += (aPitch - row.aimPitch) / n;
            row.aimRoll += (aRoll - row.aimRoll) / n;
            row.aimTotal += (aTotal - row.aimTotal) / n;
        }

        /// <summary>Silahin yatikligi: namluya dik duzlemde, referans "yukari" ile silahin
        /// ust rayi arasindaki isaretli aci.</summary>
        static float SignedRoll(Vector3 barrel, Vector3 weaponTop, Vector3 reference)
        {
            Vector3 a = Vector3.ProjectOnPlane(reference, barrel);
            Vector3 b = Vector3.ProjectOnPlane(weaponTop, barrel);
            if (a.sqrMagnitude < 1e-6f || b.sqrMagnitude < 1e-6f) return 0f;
            return Vector3.SignedAngle(a, b, barrel);
        }

        /// <summary>Namlunun silah-LOKAL yonu. Oncelik sirasi NetworkWeapon.Fire ile AYNI
        /// olmali, yoksa "gorunen namlu" ile "atisin ciktigi eksen" ayrisir ve yanlis seyi
        /// olcersin: profil ekseni > Muzzle child > silahin +Z'si.</summary>
        static Vector3 BarrelLocalDirection(GrabbableObject held, out string source)
        {
            var grip = held.GetComponent<WeaponGrip>();
            var profile = grip != null ? grip.Profile : null;
            if (profile != null && profile.barrelLocalDirection.sqrMagnitude > 1e-6f)
            {
                source = "profil";
                return profile.barrelLocalDirection.normalized;
            }

            var muzzle = held.transform.Find("Muzzle");
            if (muzzle != null)
            {
                source = "Muzzle";
                return held.transform.InverseTransformDirection(muzzle.forward).normalized;
            }

            source = "varsayim +Z"; // profilsiz + muzzle'siz: bu silah zaten tahminle hizalaniyor
            return Vector3.forward;
        }

        static Vector3 MuzzlePosition(Transform w, Vector3 barrel)
        {
            var muzzle = w.Find("Muzzle");
            return muzzle != null ? muzzle.position : w.position + barrel * 0.25f;
        }

        Row GetRow(string weapon, bool leftHand, string source)
        {
            if (!_byName.TryGetValue(weapon, out Row r))
            {
                r = new Row { weapon = weapon };
                _byName[weapon] = r;
                _rows.Add(r);
            }
            r.leftHand = leftHand;
            r.barrelSource = source;
            return r;
        }

        GrabbableObject FindHeldWeapon(out bool leftHand)
        {
            leftHand = false;
            var nm = NetworkManager.Singleton;
            if (nm == null) return null;
            ulong me = nm.LocalClientId;

            var list = GrabbableObject.Active;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                if (g == null || !g.IsHeld || g.HolderClientId != me) continue;
                leftHand = g.HolderHand == 0;
                return g;
            }
            return null;
        }

        // ─── 0.3 testleri ─────────────────────────────────────────────────────────────────

        void ApplyTestMode()
        {
            // Ayar yaparken weld ve parmaklar HER ZAMAN acik olmali: kapali weld ile
            // hizaladigin tutus, weld acilinca baska yere oturur.
            if (TunerActive) mode = TestMode.Normal;

            if (_weld == null) _weld = GetComponentInChildren<WeaponHandWeld>(true);
            if (_poser == null) _poser = GetComponentInChildren<ProceduralFingerPoser>(true);

            bool weldOn = mode != TestMode.WeldOff && mode != TestMode.WeldAndFingersOff;
            bool fingersOn = mode != TestMode.FingersOff && mode != TestMode.WeldAndFingersOff;

            // HER KARE yazilir, bir kez degil: WeaponHandWeld.SetHand kendini yeniden
            // enable ediyor (silahi her kavradiginda), tek seferlik kapatma tutmaz.
            if (_weld != null && _weld.enabled != weldOn) _weld.enabled = weldOn;
            if (_poser != null && _poser.enabled != fingersOn) _poser.enabled = fingersOn;
        }

        void ReadChords()
        {
            if (TunerActive) return;

            // Akor (iki elden ayni anda): tek tuslarin hepsi baska sistemlerde kullaniliyor.
            bool modeChord = XRButtons.Button(XRNode.LeftHand, CommonUsages.primaryButton)
                          && XRButtons.Button(XRNode.RightHand, CommonUsages.primaryButton);
            bool recChord = XRButtons.Button(XRNode.LeftHand, CommonUsages.secondaryButton)
                         && XRButtons.Button(XRNode.RightHand, CommonUsages.secondaryButton);

            if (modeChord && !_prevChordMode)
                mode = (TestMode)(((int)mode + 1) % 4);

            // B+Y: kisa bas = satiri dondur, BASILI TUT = dosyaya yaz. Ayni akorda iki is
            // olmasinin sebebi tus kitligi — tek tuslarin hepsi baska sistemlerde dolu.
            if (recChord && !_prevChordRecord)
            {
                _recHeldSince = Time.unscaledTime;
                _savedThisHold = false;
            }
            else if (recChord && !_savedThisHold &&
                     Time.unscaledTime - _recHeldSince >= saveHoldSeconds)
            {
                _savedThisHold = true; // uzun basis: kaydet, birakista donduRma yapma
                SaveMarkdown();
            }
            else if (!recChord && _prevChordRecord && !_savedThisHold)
            {
                // Kisa basis, BIRAKISTA islenir: aksi halde uzun basis once satiri
                // dondurur, sonra dosyayi yazardi — iki is tek hareketle karisirdi.
                var held = FindHeldWeapon(out _);
                if (held != null && _byName.TryGetValue(held.name, out Row r))
                    r.frozen = !r.frozen;
            }

            _prevChordMode = modeChord;
            _prevChordRecord = recChord;
        }

        // ─── .md kaydi ────────────────────────────────────────────────────────────────────

        void SaveMarkdown()
        {
            if (_rows.Count == 0)
            {
                _saveStatus = "KAYIT YOK: tablo bos";
                return;
            }

            string md = BuildMarkdown();
            // Konsola da bas: Editorde/Link ile oynarken dosyayi aramaya bile gerek kalmaz,
            // cihazda ise logcat'ten okunabilir.
            Debug.Log("[GripDebugRig] olcum tablosu\n" + md);

            try
            {
                string path = WriteCopy(Path.Combine(Application.persistentDataPath, "GripOlcum"), md);
#if UNITY_EDITOR
                // Proje KOKUNE, Assets'in DISINA: Assets altina yazmak her kayitta bir
                // asset import'u ve domain reload riski demek olurdu.
                var root = Directory.GetParent(Application.dataPath);
                if (root != null) WriteCopy(Path.Combine(root.FullName, "GripOlcum"), md);
#endif
                _saveStatus = "KAYDEDILDI: " + Path.GetFileName(path);
            }
            catch (System.Exception e)
            {
                // Android'de depolama yazimi reddedilebilir; arac sessizce basarili
                // gorunmemeli, yoksa olcumun kaydedildigini sanip veriyi kaybedersin.
                _saveStatus = "KAYIT HATASI: " + e.GetType().Name;
                Debug.LogError("[GripDebugRig] kayit basarisiz: " + e);
            }
        }

        /// <summary>Klasore bir sonraki bos numarali dosyayi yazar ve tam yolu dondurur.
        /// Ustune yazmaz: her olcum oturumu ayri dosya, kaza ile veri kaybi olmaz.</summary>
        string WriteCopy(string dir, string md)
        {
            Directory.CreateDirectory(dir);
            string path;
            int n = 1;
            do
            {
                path = Path.Combine(dir, $"{fileName}-{n:D3}.md");
                n++;
            } while (File.Exists(path) && n < 1000);

            File.WriteAllText(path, md, new UTF8Encoding(false));
            return path;
        }

        string BuildMarkdown()
        {
            var sb = new StringBuilder(1024);
            sb.Append("# Grip olcum tablosu\n\n");
            sb.Append("- tarih: ").Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            sb.Append("- test modu: ").Append(ModeLabel(mode)).Append('\n');
            sb.Append("- cihaz: ").Append(SystemInfo.deviceModel).Append('\n');
            sb.Append("- kumandalar: ").Append(DeviceName(XRNode.LeftHand))
              .Append(" / ").Append(DeviceName(XRNode.RightHand)).Append('\n');
            sb.Append("- nisan isaretcisi: kafadan ").Append(aimMarkerDistance.ToString("F1")).Append(" m\n\n");

            sb.Append("HAM = kumanda cipasi -> silah (kodun uyguladigi offset; silah basina SABIT olmali).\n");
            sb.Append("NISAN = baktigin yer -> namlu (oyuncunun hissettigi hata).\n");
            sb.Append("Aci birimi derece. yaw +: saga, pitch +: yukari, roll +: saat yonu.\n\n");

            sb.Append("| silah | el | namlu | ornek | HAM | ham yaw | ham pitch | ham roll | NISAN | nis yaw | nis pitch | nis roll |\n");
            sb.Append("|---|---|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|\n");
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                sb.Append("| ").Append(r.weapon)
                  .Append(" | ").Append(r.leftHand ? "sol" : "sag")
                  .Append(" | ").Append(r.barrelSource)
                  .Append(" | ").Append(r.samples)
                  .Append(" | ").Append(r.total.ToString("F1"))
                  .Append(" | ").Append(Deg(r.yaw))
                  .Append(" | ").Append(Deg(r.pitch))
                  .Append(" | ").Append(Deg(r.roll))
                  .Append(" | ").Append(r.aimTotal.ToString("F1"))
                  .Append(" | ").Append(Deg(r.aimYaw))
                  .Append(" | ").Append(Deg(r.aimPitch))
                  .Append(" | ").Append(Deg(r.aimRoll))
                  .Append(" |\n");
            }

            sb.Append("\n## Nasil okunur\n\n");
            sb.Append("- HAM sayilari silahtan silaha FARKLI -> her silahin kendi hizalama hatasi var; ");
            sb.Append("suclu HandGrabber.SnapRotOffset'in bounds tahmini. Cozum: Faz 1.1 (silah basina `Grip` child).\n");
            sb.Append("- HAM sayilari butun silahlarda AYNI -> ortak kumanda grip-pose kaymasi. ");
            sb.Append("Cozum: Faz 1.3 (tek kalibrasyon quaternion'i), silah basina ugrasma.\n");
            sb.Append("- roll sifirdan uzak ve silahtan silaha zipliyor -> Quaternion.FromToRotation'in ");
            sb.Append("tanimsiz roll'u dogrulanmis demektir.\n");
            sb.Append("- namlu suutunu 'varsayim +Z' olan silah profilsiz VE Muzzle'siz: tamamen tahminle hizalaniyor.\n");
            return sb.ToString();
        }

        static string DeviceName(XRNode node)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid ? dev.name : "-";
        }

        // ─── Cizim ────────────────────────────────────────────────────────────────────────

        void Redraw(GrabbableObject held, bool leftHand, bool steady)
        {
            if (_panel == null) return; // tuner acik: panel onda
            _sb.Clear();
            _sb.Append("GRIP DEBUG   mod: ").Append(ModeLabel(mode)).Append('\n');

            if (held == null)
            {
                _sb.Append("<silah tutulmuyor>\n");
            }
            else
            {
                _byName.TryGetValue(held.name, out Row cur);
                _sb.Append(held.name).Append("  (").Append(leftHand ? "SOL" : "SAG").Append(" el)");
                if (cur != null)
                {
                    _sb.Append("  namlu: ").Append(cur.barrelSource);
                    if (cur.frozen) _sb.Append("  [DONDU]");
                    _sb.Append('\n');
                    _sb.Append(steady ? "  ornek aliniyor (" : "  ELI SABIT TUT (");
                    _sb.Append(cur.samples).Append(")\n");
                    _sb.Append("HAM  yaw ").Append(Deg(cur.yaw))
                       .Append(" pitch ").Append(Deg(cur.pitch))
                       .Append(" roll ").Append(Deg(cur.roll))
                       .Append("  = ").Append(cur.total.ToString("F1")).Append("\n");
                    _sb.Append("NISAN yaw ").Append(Deg(cur.aimYaw))
                       .Append(" pitch ").Append(Deg(cur.aimPitch))
                       .Append(" roll ").Append(Deg(cur.aimRoll))
                       .Append("  = ").Append(cur.aimTotal.ToString("F1")).Append("\n");
                }
                else _sb.Append('\n');
            }

            if (_rows.Count > 0)
            {
                _sb.Append("\nTABLO (ham / nisan)\n");
                for (int i = 0; i < _rows.Count; i++)
                {
                    var r = _rows[i];
                    _sb.Append(r.frozen ? "* " : "  ")
                       .Append(r.weapon).Append("  ")
                       .Append(r.total.ToString("F1")).Append("  y").Append(Deg(r.yaw))
                       .Append(" p").Append(Deg(r.pitch)).Append(" r").Append(Deg(r.roll))
                       .Append("   |  ").Append(r.aimTotal.ToString("F1")).Append('\n');
                }
            }

            _sb.Append("\nA+X mod  |  B+Y kisa: dondur  |  B+Y basili: .md kaydet");
            if (!string.IsNullOrEmpty(_saveStatus)) _sb.Append('\n').Append(_saveStatus);
            _panel.text = _sb.ToString();
            _panel.color = mode == TestMode.Normal ? Color.cyan : Color.yellow;
        }

        static string Deg(float v) => (v >= 0f ? "+" : "") + v.ToString("F1");

        static string ModeLabel(TestMode m)
        {
            switch (m)
            {
                case TestMode.WeldOff: return "WELD KAPALI";
                case TestMode.FingersOff: return "PARMAK KAPALI";
                case TestMode.WeldAndFingersOff: return "WELD+PARMAK KAPALI";
                default: return "NORMAL";
            }
        }

        // ─── Gorsel kurulum ───────────────────────────────────────────────────────────────

        void EnsureVisuals()
        {
            if (TunerActive)
            {
                // Panel tuner'in; cubuklar bizde kalir.
                if (_panel != null) { Destroy(_panel.gameObject); _panel = null; }
            }
            else if (_panel == null)
            {
                _panel = UI.HeadFollowPanel.Create("Grip Debug", "", Color.cyan);
                // Goz hizasinda durursa nisan aldigin yeri kapatir — asagi ve uzaga al.
                var follow = _panel.GetComponent<UI.HeadFollowPanel>();
                follow.distance = 1.8f;
                follow.heightOffset = -0.45f;
            }
            if (_leftAxes == null) _leftAxes = BuildTriad("Axes L");
            if (_rightAxes == null) _rightAxes = BuildTriad("Axes R");
            if (_weaponAxes == null) _weaponAxes = BuildTriad("Axes Weapon");
            if (_barrelRod == null) _barrelRod = BuildRod("Barrel Rod", Vector3.forward,
                new Color(1f, 0.9f, 0.1f), axisLength * 3f).transform;
            if (_aimMarker == null) _aimMarker = BuildAimMarker();
        }

        Transform BuildTriad(string name)
        {
            var root = new GameObject(name).transform;
            BuildRod("X", Vector3.right, Color.red, axisLength).transform.SetParent(root, false);
            BuildRod("Y", Vector3.up, Color.green, axisLength).transform.SetParent(root, false);
            BuildRod("Z", Vector3.forward, Color.blue, axisLength).transform.SetParent(root, false);
            return root;
        }

        GameObject BuildRod(string name, Vector3 dir, Color color, float length)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            // Debug gorseli fizige HIC karismamali: silahi itmesin, kavramayi tetiklemesin.
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.localPosition = dir * (length * 0.5f);
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, dir);
            go.transform.localScale = new Vector3(axisThickness, axisThickness, length);
            go.GetComponent<Renderer>().sharedMaterial = UI.UITheme.CreateLitMaterial(color);
            return go;
        }

        Transform BuildAimMarker()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Aim Marker";
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);
            go.transform.localScale = Vector3.one * 0.06f;
            go.GetComponent<Renderer>().sharedMaterial =
                UI.UITheme.CreateLitMaterial(new Color(1f, 0.2f, 0.5f));
            return go.transform;
        }

        static void PlaceTriad(Transform triad, Transform target)
        {
            if (triad == null) return;
            bool on = target != null;
            if (triad.gameObject.activeSelf != on) triad.gameObject.SetActive(on);
            if (on) triad.SetPositionAndRotation(target.position, target.rotation);
        }

        void PlaceAimMarker()
        {
            var head = XRRigReference.HeadOrCamera;
            if (head == null || _aimMarker == null) return;
            // Kafaya kilitli: "silah BAKTIGIM yere baksin" tanimini olculebilir yapar.
            _aimMarker.position = head.position + head.forward * aimMarkerDistance;
        }

        void HideWeaponVisuals()
        {
            if (_weaponAxes != null && _weaponAxes.gameObject.activeSelf)
                _weaponAxes.gameObject.SetActive(false);
            if (_barrelRod != null && _barrelRod.gameObject.activeSelf)
                _barrelRod.gameObject.SetActive(false);
        }

        void TearDown()
        {
            // Kapatirken weld/poser'i ACIK birak: debug araci oyunu bozulmus halde birakmamali.
            if (_weld != null) _weld.enabled = true;
            if (_poser != null) _poser.enabled = true;

            if (_panel != null) { Destroy(_panel.gameObject); _panel = null; }
            DestroyIf(ref _leftAxes);
            DestroyIf(ref _rightAxes);
            DestroyIf(ref _weaponAxes);
            DestroyIf(ref _barrelRod);
            DestroyIf(ref _aimMarker);
        }

        static void DestroyIf(ref Transform t)
        {
            if (t != null) Destroy(t.gameObject);
            t = null;
        }
    }
}
