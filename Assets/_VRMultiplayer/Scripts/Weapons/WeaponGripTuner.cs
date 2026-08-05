using System.IO;
using System.Text;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// CANLI TUTUS AYARI (dev) — H3VR'deki gibi kabzayi kumandanin uzerine oturtmak icin.
    ///
    /// Mevcut <see cref="WeaponGripCaptureTool"/> bunun TERSINI yapar: silahi sabitleyip elini
    /// ona goturursun, arac ELININ NEREDE OLDUGUNU kaydeder — yani bilegin o anki sapmasi
    /// profile yazilir. Dongunun kapanmama sebebi bu. Burada silah elinde DURURKEN silahi
    /// kumandanin etrafinda oynatirsin; kaydedilen sey senin bilegin degil, senin BEGENDIGIN
    /// hizalama olur. Envanter sistemi de engel degil: silahin havada asili kalmasi gerekmiyor.
    ///
    /// NASIL CALISIR: profilin iki alanini canli degistirir ve HandGrabber.FollowProfiled bir
    /// sonraki karede yeni pozu uygular — ayrica bir "onizleme" yolu yok, gordugun sey gercek
    /// calisma yolunun ta kendisi.
    ///   gripLocalEuler    -> silahin kumandaya gore ACISI
    ///   gripLocalPosition -> silahin kumandaya gore KONUMU
    ///
    /// TUSLAR (sol cubuk bostaydi; sag cubuk silah carkina bagli, ona dokunmuyoruz):
    ///   sol cubuk sol/sag       -> yaw    (kumandanin YUKARI ekseni etrafinda)
    ///   sol cubuk ileri/geri    -> pitch  (kumandanin SAG ekseni etrafinda)
    ///   B basili + sol cubuk    -> roll (sol/sag) ve ileri-geri konum (ileri/geri)
    ///   A + X akoru, 1 sn basili-> KAYDET
    ///   B + Y akoru             -> oturum basindaki degerlere GERI AL
    ///
    /// SADECE SAG EL. Profiller main=SAG olarak yazarlanir; sol elde HandGrabber degerleri
    /// aynalayarak okur (WeaponGripMath.MirrorX), yani sol elle yapilan ayar depolanan degere
    /// ters isaretle yansir ve kafa karistirir.
    ///
    /// DIKKAT: profil bir ScriptableObject. Editorde oynarken yapilan degisiklik ASSET'I
    /// GERCEKTEN DEGISTIRIR (play mode'dan cikinca geri gelmez). Oturum basindaki degerler
    /// saklanir; B+Y ile geri alinabilir ve her kayit dosyaya "onceki" satirini da yazar.
    ///
    /// <see cref="GripDebugRig"/> ile AYNI ANDA ACMA — A+X akorunu ikisi de dinliyor. Ama o
    /// aracin eksen cubuklarini acik birakmak faydali: kumandanin gercek eksenini gormeden
    /// neyi ortaladigini bilemezsin.
    /// </summary>
    [DefaultExecutionOrder(200)] // HandGrabber (10) pozu yazdiktan sonra oku/goster
    public class WeaponGripTuner : MonoBehaviour
    {
        [Header("Ac/kapa")]
        [Tooltip("Ayar modu. Build almadan once ISARETLE; sevkiyatta kapali olmali.")]
        public bool tuning;

        [Header("Adim buyuklugu")]
        [Tooltip("Cubugu tam ittiginde saniyede kac derece donsun.")]
        public float degreesPerSecond = 12f;
        [Tooltip("Cubugu tam ittiginde saniyede kac metre kaysin.")]
        public float metersPerSecond = 0.05f;
        [Tooltip("Bu esigin altindaki cubuk hareketi yok sayilir (bosluk).")]
        public float stickDeadzone = 0.2f;

        [Header("Kayit")]
        public string fileName = "grip-tune";

        HandGrabber _grabber;
        TextMesh _panel;
        WeaponHandWeld _weld;
        GripDebugRig _rig;

        // Oturum basi anlik goruntusu: hangi profil, hangi degerlerle basladi.
        WeaponGripProfile _tuned;
        Vector3 _origPos;
        Vector3 _origEuler;

        bool _prevSaveChord, _prevResetChord;
        float _saveChordSince;
        bool _savedThisHold;
        string _status = "";
        readonly StringBuilder _sb = new StringBuilder(256);

        void Awake()
        {
            _grabber = GetComponent<HandGrabber>();
            _rig = GetComponent<GripDebugRig>();
        }

        void OnDisable() => ClosePanel();

        void LateUpdate()
        {
            var netObj = GetComponent<NetworkObject>();
            if (!tuning || _grabber == null || (netObj != null && !netObj.IsOwner))
            {
                ClosePanel();
                return;
            }

            if (_panel == null)
            {
                _panel = UI.HeadFollowPanel.Create("Grip Tuner", "", Color.green);
                var follow = _panel.GetComponent<UI.HeadFollowPanel>();
                follow.distance = 1.8f;
                follow.heightOffset = -0.45f;
            }

            var held = FindHeldWeapon(out bool leftHand);
            var profile = held != null ? held.GetComponent<WeaponGrip>()?.Profile : null;

            if (held == null) { Show("<silah tutulmuyor>"); return; }
            if (leftHand) { Show(held.name + "\nSOL elde — ayar SADECE SAG elle yapilir"); return; }
            if (profile == null)
            {
                // Profilsiz silah zaten SnapRotOffset'in tahminiyle hizalaniyor; ayarlanacak
                // veri alani yok. Once profil olusturulmali.
                Show(held.name + "\nPROFIL YOK — bu silah tahminle hizalaniyor,\nayarlanacak alan yok");
                return;
            }

            Track(profile);
            Nudge(profile, held);
            RefreshWeld(profile, held);
            HandleChords(profile, held);

            _sb.Clear();
            _sb.Append("GRIP TUNER — ").Append(held.name).Append('\n');
            AppendAim();
            _sb.Append("\naci   ").Append(V(Wrap(profile.gripLocalEuler))).Append('\n');
            _sb.Append("konum ").Append(V(profile.gripLocalPosition)).Append('\n');
            _sb.Append("A+X basili: KAYDET  |  B+Y: geri al");
            if (!string.IsNullOrEmpty(_status)) _sb.Append('\n').Append(_status);
            _panel.text = _sb.ToString();
        }

        /// <summary>
        /// Ayarin TEK hedefi: sari namlu cubugunu pembe isaretciye dogrultmak. "Silahin mavi
        /// ekseni ile kumandanin mavi ekseni ust uste gelsin" diye bir hedef YOK — silahin
        /// +Z'si namlu degil, kumandanin +Z'si de nisan hatti degil, ve gercek bir kabza
        /// namluya gore zaten egimlidir. O yuzden panelde eslestirilecek eksen degil,
        /// KUCULTULECEK bir sayi gosteriyoruz; yon ipuclari da cubugu hangi tarafa itecegini
        /// sOyluyor ki VR'da isaret arastirmak zorunda kalmayasin.
        /// </summary>
        void AppendAim()
        {
            if (_rig == null || !_rig.showOverlay)
            {
                _sb.Append("(eksen rig'i kapali — GripDebugRig.showOverlay'i ac)\n");
                return;
            }
            if (!_rig.HasAim) { _sb.Append("(nisan olcumu yok)\n"); return; }

            _sb.Append("NAMLU HEDEFE: ").Append(_rig.AimTotal.ToString("F1")).Append("  <- kucult\n");
            // Isaretler Nudge()'daki donme yonlerinden turetildi:
            //   cubuk saga  -> silah saga doner  -> yaw buyur
            //   cubuk ileri -> namlu asagi iner  -> pitch kucul
            //   B+cubuk saga-> ust ray sola yatar -> roll buyur
            Hint("yaw  ", _rig.AimYaw, "cubugu SOLA", "cubugu SAGA");
            Hint("pitch", _rig.AimPitch, "cubugu ILERI", "cubugu GERI");
            Hint("roll ", _rig.AimRoll, "B + cubugu SOLA", "B + cubugu SAGA");
        }

        void Hint(string label, float value, string whenPositive, string whenNegative)
        {
            _sb.Append("  ").Append(label).Append(' ').Append(Deg(value));
            _sb.Append(Mathf.Abs(value) < 0.5f ? "  tamam" : "  -> " + (value > 0f ? whenPositive : whenNegative));
            _sb.Append('\n');
        }

        static string Deg(float v) => (v >= 0f ? "+" : "") + v.ToString("F1");

        /// <summary>Baska bir silaha gecildiginde yeni profilin baslangic degerlerini sakla —
        /// "geri al" her zaman ELDEKI silahin oturum basi degerine doner.</summary>
        void Track(WeaponGripProfile profile)
        {
            if (_tuned == profile) return;
            _tuned = profile;
            _origPos = profile.gripLocalPosition;
            _origEuler = profile.gripLocalEuler;
            _status = "";
        }

        void Nudge(WeaponGripProfile profile, GrabbableObject held)
        {
            Vector2 stick = Stick(XRNode.LeftHand);
            if (stick.magnitude < stickDeadzone) return;
            bool modifier = XRButtons.Button(XRNode.RightHand, CommonUsages.secondaryButton); // B

            float dt = Time.deltaTime;
            Quaternion grip = profile.GripLocalRotation;

            if (!modifier)
            {
                // Cubuk = donme. Delta KUMANDANIN cercevesinde tanimli, cunku ayarlarken
                // dusundugun sey "silahi kumandaya gore soldan saga dondur".
                //   weaponRot = anchor.rotation * Inverse(gripRot)
                // olduguna gore, weaponRot'a kumanda cercevesinde delta eklemek demek:
                //   gripRot' = gripRot * Inverse(delta)
                Quaternion delta =
                    Quaternion.AngleAxis(stick.x * degreesPerSecond * dt, Vector3.up) *
                    Quaternion.AngleAxis(stick.y * degreesPerSecond * dt, Vector3.right);
                profile.gripLocalEuler = (grip * Quaternion.Inverse(delta)).eulerAngles;
            }
            else
            {
                // B + cubuk: sol/sag = roll, ileri/geri = kumanda ekseninde derinlik.
                Quaternion delta = Quaternion.AngleAxis(stick.x * degreesPerSecond * dt, Vector3.forward);
                profile.gripLocalEuler = (grip * Quaternion.Inverse(delta)).eulerAngles;

                // Konum: weaponPos = anchor.position - weaponRot * Scale(lossyScale, gripLocal).
                // Silahi kumanda cercevesinde d kadar oteleyebilmek icin
                //   gripLocal' = gripLocal - (gripRot * d) / lossyScale
                // (weaponRot^-1 * anchor.rotation sadelesip gripRot'a esit oluyor).
                Vector3 d = Vector3.forward * (stick.y * metersPerSecond * dt);
                Vector3 scale = held.transform.lossyScale;
                Vector3 delta3 = grip * d;
                profile.gripLocalPosition -= new Vector3(
                    SafeDiv(delta3.x, scale.x), SafeDiv(delta3.y, scale.y), SafeDiv(delta3.z, scale.z));
            }
        }

        static float SafeDiv(float a, float b) => Mathf.Abs(b) < 1e-5f ? a : a / b;

        /// <summary>
        /// Weld, cipa degerlerini tutus BASINDA bir kez cozup onbellege aliyor
        /// (WeaponHandWeld.SetHand) — profil kavrama sirasinda degisince silah yeni poza
        /// gider ama bilek eski cipada kalir ve el silahtan kayar. Ayar yaparken bu, ayarin
        /// kendisi bozukmus gibi gorunurdu. Her karede yeniden kurarak onbellegi tazeliyoruz;
        /// SetHand devam eden blend rampasini korudugu icin bu bir pop yaratmaz.
        ///
        /// Ana el = SAG (mirrored: false, isSupport: false) — tuner zaten sol eli reddediyor.
        /// Destek eli (sol) slotuna dokunulmaz, onu WeaponGrip yonetmeye devam eder.
        /// </summary>
        void RefreshWeld(WeaponGripProfile profile, GrabbableObject held)
        {
            if (_weld == null) _weld = GetComponentInChildren<WeaponHandWeld>(true);
            if (_weld != null)
                _weld.SetHand(false, held.transform, profile, false, false);
        }

        void HandleChords(WeaponGripProfile profile, GrabbableObject held)
        {
            bool save = XRButtons.Button(XRNode.LeftHand, CommonUsages.primaryButton)
                     && XRButtons.Button(XRNode.RightHand, CommonUsages.primaryButton);
            bool reset = XRButtons.Button(XRNode.LeftHand, CommonUsages.secondaryButton)
                      && XRButtons.Button(XRNode.RightHand, CommonUsages.secondaryButton);

            if (save && !_prevSaveChord) { _saveChordSince = Time.unscaledTime; _savedThisHold = false; }
            else if (save && !_savedThisHold && Time.unscaledTime - _saveChordSince >= 1f)
            {
                _savedThisHold = true;
                Save(profile, held.name);
            }

            if (reset && !_prevResetChord)
            {
                profile.gripLocalPosition = _origPos;
                profile.gripLocalEuler = _origEuler;
                _status = "GERI ALINDI";
            }

            _prevSaveChord = save;
            _prevResetChord = reset;
        }

        void Save(WeaponGripProfile profile, string weaponName)
        {
            string md = BuildMarkdown(profile, weaponName);
            Debug.Log("[GripTuner]\n" + md);

            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "GripOlcum");
                Directory.CreateDirectory(dir);
                string path;
                int n = 1;
                do { path = Path.Combine(dir, $"{fileName}-{n:D3}.md"); n++; }
                while (File.Exists(path) && n < 1000);
                File.WriteAllText(path, md, new UTF8Encoding(false));
                _status = "KAYDEDILDI: " + Path.GetFileName(path);
            }
            catch (System.Exception e)
            {
                _status = "DOSYA HATASI: " + e.GetType().Name;
                Debug.LogError("[GripTuner] dosya yazilamadi: " + e);
            }

#if UNITY_EDITOR
            // Editorde (Link ile oynarken) profil asset'i dogrudan diske yazilir — dosyayi
            // cekip elle yapistirmaya gerek kalmaz. Cihazda bu satirlar derlenmez.
            UnityEditor.EditorUtility.SetDirty(profile);
            UnityEditor.AssetDatabase.SaveAssets();
            _status += " + asset yazildi";
#endif
            // Yeni taban: bundan sonraki "geri al" kaydedilen degere doner.
            _origPos = profile.gripLocalPosition;
            _origEuler = profile.gripLocalEuler;
        }

        string BuildMarkdown(WeaponGripProfile profile, string weaponName)
        {
            var sb = new StringBuilder(512);
            sb.Append("# Grip ayari — ").Append(weaponName).Append("\n\n");
            sb.Append("- tarih: ").Append(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append('\n');
            sb.Append("- profil: ").Append(profile.name).Append('\n');
            sb.Append("- kumanda: ").Append(DeviceName(XRNode.RightHand)).Append("\n\n");
            sb.Append("Profile yapistirilacak degerler:\n\n```\n");
            sb.Append("  gripLocalPosition: ").Append(Y(profile.gripLocalPosition)).Append('\n');
            sb.Append("  gripLocalEuler: ").Append(Y(profile.gripLocalEuler)).Append('\n');
            sb.Append("```\n\n");
            // Silahlar arasi karsilastirma icin: 0..360 degil, +-180.
            sb.Append("Okunabilir aci (+-180): ").Append(V(Wrap(profile.gripLocalEuler))).Append("\n\n");
            sb.Append("Oturum basindaki degerler (geri donmek istersen):\n\n```\n");
            sb.Append("  gripLocalPosition: ").Append(Y(_origPos)).Append('\n');
            sb.Append("  gripLocalEuler: ").Append(Y(_origEuler)).Append('\n');
            sb.Append("```\n");
            return sb.ToString();
        }

        // WeaponGripProfile .asset dosyasinin bekledigi bicim — satir dogrudan yapistirilabilir.
        static string Y(Vector3 v) => $"{{x: {v.x}, y: {v.y}, z: {v.z}}}";

        /// <summary>Ekranda gosterilen aci. Quaternion.eulerAngles 0..360 dondurur, yani -5
        /// derece 355 diye gorunur — silahlar arasi karsilastirma tam da bunun yuzunden
        /// bozulurdu ("hepsinde pitch +20 mi?" sorusunu 355 ile cevaplayamazsin).</summary>
        static Vector3 Wrap(Vector3 e) => new Vector3(Wrap(e.x), Wrap(e.y), Wrap(e.z));
        static float Wrap(float d)
        {
            d %= 360f;
            if (d > 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        static string V(Vector3 v) => $"{v.x,7:F2} {v.y,7:F2} {v.z,7:F2}";

        static string DeviceName(XRNode node)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid ? dev.name : "-";
        }

        static Vector2 Stick(XRNode node)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            return dev.isValid && dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v)
                ? v : Vector2.zero;
        }

        GrabbableObject FindHeldWeapon(out bool leftHand)
        {
            leftHand = false;
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return null;
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

        void Show(string text)
        {
            if (_panel != null) _panel.text = "GRIP TUNER\n" + text;
        }

        void ClosePanel()
        {
            if (_panel != null) { Destroy(_panel.gameObject); _panel = null; }
        }
    }
}
