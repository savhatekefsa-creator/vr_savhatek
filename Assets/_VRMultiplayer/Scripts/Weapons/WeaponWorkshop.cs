using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using VRMultiplayer.UI;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// SILAH ATOLYESI — tutusu OYUN ICINDE, GORREREK ayarlama tezgahi (Faz 1: bilek yeri).
    ///
    /// Neden var: tutus ayari bugune kadar ya editorde AVATARIN eli uzerinde ya da koda
    /// yazilan sayilarla yapiliyordu. Ikisi de oyuncunun gordugu eli ayarlamiyor - iki rig'in
    /// bilek konvansiyonu ve parmak oranlari farkli - ve cihazda el kabzanin yanina dusuyordu.
    /// Burada ayarlanan sey OYUNDAKI elin ta kendisi, uygulanan formul de runtime'in
    /// kullandigi formulun ayni: cipa + profildeki fpWristLocal* offseti.
    ///
    /// Akis: silah sec -> "elleri koy" -> panelden oklarla yerlestir -> kaydet.
    /// Eller DONUK durur; kendi kumandan serbest kalir, tezgahin etrafinda donup avuc
    /// tarafindan da bakabilirsin. Kumandayi tutarken kendi avucunun icini goremezsin.
    ///
    /// GIRDI TEK: lazer imlec + tetik. Akor (A+X gibi) yok - yanlislikla basmak ve ne
    /// kadar degistigini gorememek eski aracin en buyuk derdiydi.
    ///
    /// KAYIT: profil bir ScriptableObject. Editorde Play modunda yapilan degisiklik asset'i
    /// GERCEKTEN degistirir. Cihazda ise yalnizca calisan uygulamada yasar; o yuzden her
    /// kayit ayrica persistentDataPath/GripOlcum/atolye.md dosyasina yazilir ve editorde
    /// menu 52 ile projeye islenir. Panel, aktarilmayi bekleyen silah sayisini gosterir.
    /// </summary>
    public class WeaponWorkshop : MonoBehaviour
    {
        [Tooltip("Atolyeyi ac. Sevkiyatta KAPALI olmali.")]
        public bool open;

        [Tooltip("Tezgahin oyuncunun onunde duracagi mesafe (metre).")]
        public float benchDistance = 0.75f;
        [Tooltip("Tezgah yuksekligi, goz hizasindan asagi (metre).")]
        public float benchDrop = 0.35f;

        const string WeaponDir = "WeaponPrefabs";
        const float FineMove = 0.001f, CoarseMove = 0.005f;
        const float FineTurn = 1f, CoarseTurn = 5f;
        /// <summary>Panel bu mesafeden uzakta kalirsa kendiliginden onune gelir.</summary>
        const float SummonDistance = 3f;

        GameObject[] _weapons;
        int _index = -1;
        GameObject _weapon;
        WeaponGripProfile _profile;

        /// <summary>Tezgahtaki bir el: sahte tasiyici + surulen dugum + parmak surucusu.
        /// IKI EL birden konuyor - tutus tek elle degerlendirilemiyor, destek eli ana ele
        /// gore duruyor.</summary>
        class Hand
        {
            public Transform carrier;
            public Transform pose;
            public FirstPersonFingerCurl curl;
            public TextMesh label;
        }

        Hand _right, _leftHand;
        bool _handsPlaced;
        /// <summary>Panelin su an DUZENLEDIGI el. Etiketler hangisinin hangisi oldugunu
        /// gosteriyor; secili olanin etiketi vurgulu.</summary>
        bool _editLeft;

        WorkshopPanel _panel;
        VRPointer _pointer;
        bool _coarse;
        readonly HashSet<string> _unsaved = new HashSet<string>();

        /// <summary>
        /// Silah secildigi andaki DISK HALI - "kayitliya don" bunu geri yukler.
        /// Bilek VE parmaklar birlikte saklanir: once yalnizca bilek geri aliniyordu ve
        /// parmaklari bozunca donulecek bir yer yoktu.
        /// </summary>
        struct Snapshot
        {
            public Vector3 pos, euler;
            public float[] curls;

            public static Snapshot Of(WeaponGripProfile.HandPose hp) => new Snapshot
            {
                pos = hp.fpWristLocalPosition,
                euler = hp.fpWristLocalEuler,
                // KOPYA: diziyi paylasirsak "geri al" degistirdigimiz diziyi geri yukler.
                curls = hp.HasFpCurls ? (float[])hp.fpCurls.Clone() : null,
            };

            public WeaponGripProfile.HandPose Into(WeaponGripProfile.HandPose hp)
            {
                hp.fpWristLocalPosition = pos;
                hp.fpWristLocalEuler = euler;
                hp.fpCurls = curls != null ? (float[])curls.Clone() : null;
                return hp;
            }
        }

        Snapshot _savedMain, _savedSupport;

        void OnDisable() => Teardown();

        void Update()
        {
            // MOD SECILMEDEN ACILMAZ. Uygulama acilista mod paneliyle basliyor ve o panel
            // kendi lazer imlecini kurup kapaninca yok ediyor; atolyeyi sahne basinda
            // acmak iki paneli ayni imlec icin yaristirir. Ayrica tezgah, oyuncu daha
            // haritaya girmeden yerlestirilmis olurdu.
            if (open && _panel == null && AppMode.Current != AppMode.Mode.None) Build();
            if (!open && _panel != null) { Teardown(); return; }
            if (_panel == null) return;

            // Imlec baska bir UI tarafindan yok edilmis olabilir - her karede tazele.
            if (_pointer == null) _pointer = AcquirePointer();

            // KAYBOLMAYA KARSI EMNIYET: oyuncu spawn olur, isinlanir, harita kurulur -
            // panel geride kalirsa "onume getir" dugmesine de basamaz, cunku dugme o
            // panelin uzerinde. Uzak kalirsa kendiliginden onune gelir.
            if (Vector3.Distance(Head.position, _panel.transform.position) > SummonDistance)
                PlaceBench();

            _panel.Tick(_pointer);
            DriveHands();
        }

        VRPointer AcquirePointer()
        {
            var found = FindObjectOfType<VRPointer>();
            if (found != null) return found;
            var go = new GameObject("WorkshopPointer");
            return go.AddComponent<VRPointer>();
        }

        // ------------------------------------------------------------------ kurulum
        void Build()
        {
            _weapons = Resources.LoadAll<GameObject>(WeaponDir);
            if (_weapons == null || _weapons.Length == 0)
            {
                Debug.LogWarning("[WeaponWorkshop] " + WeaponDir + " altinda silah prefabi yok");
                open = false;
                return;
            }

            var panelGo = new GameObject("WorkshopPanel");
            _panel = panelGo.AddComponent<WorkshopPanel>();
            _panel.Host = this;
            _panel.BuildUI();
            PlaceBench();

            _pointer = AcquirePointer();
            Select(0);
        }

        void Teardown()
        {
            RemoveHands();
            if (_weapon != null) Destroy(_weapon);
            if (_panel != null) Destroy(_panel.gameObject);
            _panel = null;
            _weapon = null;
            _profile = null;
            _index = -1;
        }

        /// <summary>
        /// Tezgahi ve paneli oyuncunun ONUNE tasir. Ayri bir dugme olmasi sart: harita
        /// aynı sahneye veri olarak kuruluyor, oyuncu spawn oluyor, kalibrasyon oluyor,
        /// isinlaniyor - tezgahi bir kez yerlestirip birakmak onu duvarin icinde veya
        /// haritanin obur ucunda birakirdi.
        /// </summary>
        /// <summary>Kafa referansi: rig'in kendi kaydi, yoksa MainCamera. Camera.main'e
        /// dogrudan guvenmiyoruz - etiket kaymasi tezgahi dunya orijinine atardi ve panel
        /// oyuncunun goremeyecegi bir yerde kalirdi.</summary>
        Transform Head => XRRigReference.HeadOrCamera != null
            ? XRRigReference.HeadOrCamera
            : (Camera.main != null ? Camera.main.transform : transform);

        public void PlaceBench()
        {
            if (_panel == null) return;
            var head = Head;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;

            _panel.Bench = head.position + flat * benchDistance + Vector3.down * benchDrop;
            _panel.BenchForward = flat;

            // Panel tezgahin SOLUNDA ve biraz gerisinde: silahla ust uste binmesin,
            // ikisine ayni anda bakabilesin.
            var t = _panel.transform;
            t.position = head.position + flat * (benchDistance - 0.10f)
                       + Vector3.down * (benchDrop * 0.2f) - head.right * 0.72f;
            t.rotation = Quaternion.LookRotation(t.position - head.position, Vector3.up);

            if (_weapon != null)
                _weapon.transform.SetPositionAndRotation(_panel.Bench, BenchRotation());
        }

        /// <summary>
        /// Tezgahtaki silahin durusu: NAMLU her silahta ayni yone (oyuncuya dogru) bakar.
        ///
        /// Once modelin kendi +Z ekseni kullaniliyordu ve namlu yonu modelden modele
        /// degisiyordu: 13 silahta namlu (0,0,-1) oldugu icin dogru duruyor, ama Smg1 ve
        /// bombalarda (0,0,+1), HK416'da (-1,0,0), Paintball'da (+1,0,0) - o silahlar
        /// tezgahta ters/yan duruyordu. Kullanici tam da o silahlari ayarlamadan birakti.
        /// Namlu ekseni profilde zaten yazili; sunumu ona baglamak dorduyu de duzeltir.
        ///
        /// Ayarlanan veriyi ETKILEMEZ: el, silaha GORE yerlestiriliyor - tezgahtaki durus
        /// yalnizca senin nasil gordugundur.
        /// </summary>
        Quaternion BenchRotation()
        {
            Vector3 fwd = _panel != null ? _panel.BenchForward : Vector3.forward;
            Vector3 barrel = _profile != null && _profile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? _profile.barrelLocalDirection.normalized
                : Vector3.forward;
            return Quaternion.FromToRotation(barrel, -fwd);
        }

        // ------------------------------------------------------------------ silah
        public string WeaponName => _weapon != null ? _weapon.name : "-";
        public bool HasProfile => _profile != null;
        public bool HandsPlaced => _handsPlaced;
        public bool Coarse { get => _coarse; set => _coarse = value; }
        public int UnsavedCount => _unsaved.Count;

        public void Step(int delta)
        {
            if (_weapons == null || _weapons.Length == 0) return;
            Select(((_index + delta) % _weapons.Length + _weapons.Length) % _weapons.Length);
        }

        void Select(int i)
        {
            if (_weapon != null) Destroy(_weapon);
            _index = i;
            _weapon = Instantiate(_weapons[i]);
            _weapon.name = _weapons[i].name;
            StripRuntime(_weapon);

            _profile = WeaponGripBinder.FindProfile(_weapon.name);
            _weapon.transform.SetPositionAndRotation(_panel.Bench, BenchRotation());
            if (_profile != null)
            {
                _savedMain = Snapshot.Of(_profile.mainHand);
                _savedSupport = Snapshot.Of(_profile.supportHand);
            }
            if (_handsPlaced) PlaceHands();
        }

        /// <summary>Tezgahtaki silah bir MAKET: fizigi, agi ve kavranmasi kapatilir.
        /// Acik kalirsa silah dusuyor, oyuncunun eline yapisiyor ve olcum bozuluyor.</summary>
        static void StripRuntime(GameObject go)
        {
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) { rb.isKinematic = true; rb.detectCollisions = false; }
            foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && !(mb is Renderer)) mb.enabled = false;
        }

        // ------------------------------------------------------------------ eller
        public void ToggleHands()
        {
            if (_handsPlaced) RemoveHands();
            else PlaceHands();
        }

        void PlaceHands()
        {
            RemoveHands();
            if (_weapon == null) return;
            _right = MakeHand(false);
            _leftHand = MakeHand(true);
            _handsPlaced = true;
        }

        Hand MakeHand(bool left)
        {
            var carrier = new GameObject(left ? "WorkshopHand_L" : "WorkshopHand_R");
            carrier.transform.SetParent(_weapon.transform, false);

            // OYUNDAKI el kurulumunun ta kendisi: ayni prefab, ayni olcek, ayni poz yolu.
            // Attach iki eli birden kurar; istemedigimiz tarafi burada yok ediyoruz ki
            // her el kendi tasiyicisinda, kendi cipasinda dursun.
            FirstPersonHandView.Attach(left ? carrier.transform : null,
                                       left ? null : carrier.transform, null);
            foreach (var v in carrier.GetComponentsInChildren<FirstPersonHandView>(true))
                v.enabled = false;                       // pozu biz suruyoruz

            var root = carrier.transform.Find(FirstPersonHandView.ObjectName);
            var h = new Hand
            {
                carrier = carrier.transform,
                pose = root != null ? root.Find("Pose") : null,
                curl = carrier.GetComponentInChildren<FirstPersonFingerCurl>(),
            };

            var dot = root != null ? root.Find(FirstPersonHandView.DotName) : null;
            if (dot != null) dot.gameObject.SetActive(false);   // atolyede kumanda noktasi anlamsiz

            // ELLER DUZ BASLAR. Kivrilmis bir elde neyin dogru neyin yanlis oldugu
            // secilemiyor; kivrimi parmak parmak sen veriyorsun.
            if (h.curl != null) h.curl.ApplyFlat();

            h.label = UI.UITheme.MakeText(carrier.transform, left ? "SOL" : "SAG",
                left ? UI.UITheme.AccentPurple : UI.UITheme.AccentCyan, 0.022f);
            return h;
        }

        void RemoveHands()
        {
            if (_right != null && _right.carrier != null) Destroy(_right.carrier.gameObject);
            if (_leftHand != null && _leftHand.carrier != null) Destroy(_leftHand.carrier.gameObject);
            _right = null; _leftHand = null;
            _handsPlaced = false;
        }

        /// <summary>Eli her karede cipaya + authored offsete oturtur. Formul RUNTIME ile
        /// AYNI (bkz. FirstPersonHandView): cipa + cipaDonusu * fpWristLocal*.</summary>
        void DriveHands()
        {
            if (!_handsPlaced || _weapon == null || _profile == null) return;
            Drive(_right, false);
            Drive(_leftHand, true);
        }

        void Drive(Hand h, bool left)
        {
            if (h == null || h.pose == null) return;

            // ANA EL kabza cipasinda, DESTEK EL kundak rayinin ortasinda. Iki elin
            // cipasi ayri; ikisini de ayni yere koymak destek elini kabzaya yigardi.
            Vector3 localAnchor = left ? SupportAnchorLocal() : _profile.gripLocalPosition;
            Vector3 anchor = _weapon.transform.TransformPoint(localAnchor);
            Quaternion anchorRot = _weapon.transform.rotation * _profile.GripLocalRotation;

            var hp = left ? _profile.supportHand : _profile.mainHand;
            h.pose.SetPositionAndRotation(anchor + anchorRot * hp.fpWristLocalPosition,
                                          anchorRot * hp.FpWristRotation);

            if (h.curl != null)
            {
                if (hp.HasFpCurls) h.curl.ApplyFingerCurls(hp.fpCurls, 1f);
                else h.curl.ApplyFlat();
            }

            if (h.label != null)
            {
                h.label.transform.position = h.pose.position + Vector3.up * 0.10f;
                var head = Head;
                h.label.transform.rotation = Quaternion.LookRotation(
                    h.label.transform.position - head.position, Vector3.up);
                bool sel = left == _editLeft;
                h.label.text = (left ? "SOL" : "SAG") + (sel ? "  ◀ duzenleniyor" : "");
                h.label.color = sel
                    ? (left ? UI.UITheme.AccentPurple : UI.UITheme.AccentCyan)
                    : UI.UITheme.TextDim;
            }
        }

        /// <summary>
        /// Destek elinin cipasi: profildeki kundak rayinin ORTASI - oyunun (weld)
        /// kullandigi degerin ta kendisi.
        ///
        /// UYDURMA YEDEK YOK. Ilk surumde ray (0,0,0) ise "kabza + namlu*0.18" diye
        /// bir yedek uyduruyordum; oyun rayi oldugu gibi kullandigi icin kullanicinin
        /// Pistol 2'de yaptigi butun destek-eli ayari yanlis cipaya gore yazildi ve
        /// cihazda el ile silah arasinda bosluk kaldi. Kural: aracin gosterdigi HER SEY
        /// oyunun formulunden gelmeli - veri bozuksa (ray kurulmamissa) bozuklugu
        /// GOSTER, gizleme; kullanici eli silahin orijininde gorur ve rayin eksik
        /// oldugunu hemen anlar.
        /// </summary>
        Vector3 SupportAnchorLocal()
            => (_profile.supportRailLocalStart + _profile.supportRailLocalEnd) * 0.5f;

        // ------------------------------------------------------------------ ayar
        /// <summary>Okla itme. Eksenler SILAHIN cercevesinde: +ileri namlu yonu,
        /// +sag kabzanin sagi, +yukari silahin ustu.</summary>
        /// <summary>Panelin duzenledigi el (SAG/SOL dugmesi bunu degistirir).</summary>
        public bool EditLeft { get => _editLeft; set => _editLeft = value; }

        public void Nudge(int axis, int sign)
        {
            if (_profile == null) return;
            float m = _coarse ? CoarseMove : FineMove;
            var hp = _editLeft ? _profile.supportHand : _profile.mainHand;
            var p = hp.fpWristLocalPosition;
            switch (axis)
            {
                case 0: p.z += sign * m; break;   // ileri/geri
                case 1: p.x += sign * m; break;   // sag/sol
                case 2: p.y += sign * m; break;   // yukari/asagi
            }
            hp.fpWristLocalPosition = p;
            Write(hp);
        }

        public void Turn(int axis, int sign)
        {
            if (_profile == null) return;
            float d = _coarse ? CoarseTurn : FineTurn;
            var hp = _editLeft ? _profile.supportHand : _profile.mainHand;
            var e = hp.fpWristLocalEuler;
            switch (axis)
            {
                case 0: e.y += sign * d; break;   // yaw
                case 1: e.x += sign * d; break;   // pitch
                case 2: e.z += sign * d; break;   // roll
            }
            hp.fpWristLocalEuler = e;
            Write(hp);
        }

        /// <summary>Bir parmagin kivrimini degistirir (0 bas .. 4 serce).</summary>
        public void Curl(int finger, int sign)
        {
            if (_profile == null) return;
            var hp = _editLeft ? _profile.supportHand : _profile.mainHand;
            if (!hp.HasFpCurls) hp.fpCurls = new float[5];
            float step = _coarse ? 0.10f : 0.02f;
            hp.fpCurls[Mathf.Clamp(finger, 0, 4)] =
                Mathf.Clamp01(hp.fpCurls[Mathf.Clamp(finger, 0, 4)] + sign * step);
            Write(hp);
        }

        public float CurlOf(int finger)
        {
            if (_profile == null) return 0f;
            var hp = _editLeft ? _profile.supportHand : _profile.mainHand;
            return hp.HasFpCurls ? hp.fpCurls[Mathf.Clamp(finger, 0, 4)] : 0f;
        }

        /// <summary>HandPose bir STRUCT: profildeki alana geri yazilmazsa degisiklik kaybolur.</summary>
        void Write(WeaponGripProfile.HandPose hp)
        {
            if (_editLeft) _profile.supportHand = hp; else _profile.mainHand = hp;
            Touch();
        }

        void Touch()
        {
            if (_profile != null) _unsaved.Add(_profile.name);
#if UNITY_EDITOR
            if (_profile != null) UnityEditor.EditorUtility.SetDirty(_profile);
#endif
        }

        /// <summary>Silahi sectigin andaki (yani diskteki) hale doner - IKI EL birden,
        /// bilek ve parmaklar dahil. Kaydetmek gibi bunun da gizli bir kipi yok.</summary>
        public void Revert()
        {
            if (_profile == null) return;
            _profile.mainHand = _savedMain.Into(_profile.mainHand);
            _profile.supportHand = _savedSupport.Into(_profile.supportHand);
            Touch();
        }

        public string ValueText()
        {
            if (_profile == null) return "profil yok";
            var hp = _editLeft ? _profile.supportHand : _profile.mainHand;
            var p = hp.fpWristLocalPosition * 1000f;
            var e = hp.fpWristLocalEuler;
            return string.Format("ileri {0,5:F0}  sag {1,5:F0}  yukari {2,5:F0}   (mm)\n" +
                                 "yaw  {3,5:F0}  pitch {4,4:F0}  roll {5,5:F0}   (derece)",
                                 p.z, p.x, p.y, e.y, e.x, e.z);
        }

        /// <summary>Profil dosyaya yazilir; editorde asset zaten degisti, cihazda bu dosya
        /// adb ile cekilip menu 52 ile islenir.</summary>
        /// <summary>
        /// IKI ELI BIRDEN yazar. Once yalnizca duzenlenen eli yaziyordu ve bu cihazda
        /// is kaybettirdi: kullanici sag eli ayarlayip panel SOL'dayken kaydetti, sag
        /// elin ayari dosyaya hic girmedi. Kaydetmenin "hangi el secili" gibi gizli bir
        /// durumu olmamali - tek dugme, her seyi yazar.
        /// </summary>
        public string Save()
        {
            if (_profile == null) return "profil yok";
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "GripOlcum");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "atolye.md");
                File.AppendAllText(path,
                    Line(_profile.mainHand, "main") + "\n" +
                    Line(_profile.supportHand, "support") + "\n",
                    new UTF8Encoding(false));
                _unsaved.Remove(_profile.name);
                return "kaydedildi (sag+sol): " + _profile.name;
            }
            catch (System.Exception e)
            {
                return "KAYIT HATASI: " + e.Message;
            }
        }

        string Line(WeaponGripProfile.HandPose hp, string role)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            string curls = hp.HasFpCurls
                ? string.Join(",", System.Array.ConvertAll(hp.fpCurls, v => v.ToString("F3", ic)))
                : "";
            return string.Format("{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8}",
                _profile.name, role,
                hp.fpWristLocalPosition.x.ToString("F5", ic),
                hp.fpWristLocalPosition.y.ToString("F5", ic),
                hp.fpWristLocalPosition.z.ToString("F5", ic),
                hp.fpWristLocalEuler.x.ToString("F3", ic),
                hp.fpWristLocalEuler.y.ToString("F3", ic),
                hp.fpWristLocalEuler.z.ToString("F3", ic),
                curls);
        }
    }
}
