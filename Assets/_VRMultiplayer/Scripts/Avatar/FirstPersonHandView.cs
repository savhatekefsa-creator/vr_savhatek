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
        GameObject _avatar;      // WeaponHandWeld calisma aninda buraya ekleniyor
        bool _left;
        WeaponHandWeld _weld;
        AvatarIKController _ik;
        float _weight;                                    // 0 = kumanda, 1 = silah ankraji
        Vector3 _lastAnchor;
        Quaternion _gripDelta = Quaternion.identity;      // tutusun ele verdigi donus duzeltmesi

        /// <summary>
        /// Iki kumanda tasiyicisinin altina el gorselini kurar. Yalnizca SAHIP
        /// icin cagrilir; aga hic girmez, uzak istemcilerde hic yaratilmaz.
        /// </summary>
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

            float side = left ? 1f : -1f;   // sag elin basparmagi -x'te (govde ortasina dogru)
            Piece(pose, "Palm", new Vector3(0f, 0f, 0.01f), new Vector3(0.075f, 0.028f, 0.095f), new Color(0.62f, 0.60f, 0.58f));
            Piece(pose, "Thumb", new Vector3(side * 0.042f, 0.004f, 0.028f), new Vector3(0.024f, 0.022f, 0.052f), new Color(0.85f, 0.45f, 0.15f));
            Piece(pose, "Fingers", new Vector3(0f, -0.002f, 0.082f), new Vector3(0.068f, 0.022f, 0.062f), new Color(0.25f, 0.65f, 0.80f));

            var view = root.AddComponent<FirstPersonHandView>();
            view._carrier = carrier;
            view._pose = pose.transform;
            view._avatar = avatar;
            view._left = left;
        }

        void LateUpdate()
        {
            if (_pose == null || _carrier == null) return;

            // Weld calisma aninda WeaponGrip tarafindan avatara EKLENIYOR, o yuzden
            // bir kere bulup onbellege almak yetmez - yoksa her karede tekrar bak.
            if (_weld == null && _avatar != null)
                _weld = _avatar.GetComponentInChildren<WeaponHandWeld>();

            // ANA EL silah tutarken silahin kabza ankrajina oturur. Silah zaten ana
            // kumandanin ucunda oldugu icin bu, kumandadan sapma DEMEK DEGIL -
            // cihazda olculdu: her silahta el<->kumanda 0-7 mm.
            //
            // DESTEK ELI kasten haric: profillerdeki destek rayi NOKTA-ray
            // (baslangic == bitis), yani el silah uzerinde tek bir sabit noktaya
            // cakiliyordu. Cihaz olcumu bunun bedelini gosterdi - gorsel el,
            // oyuncunun gercek elinden 12 silahta 81-212 mm uzaga isinlaniyor ve
            // 40-159 derece donuyordu. Kullanicinin mutlak kurali ("kumanda
            // neredeyse EL ORADA") bunu yener: destek eli kumandada kalir. Iki elle
            // tutarken oyuncunun eli zaten kundagin uzerindedir, dolayisiyla dogal
            // gorunur. Avatarin (uzak oyuncularin gordugu) destek eli WeaponHandWeld
            // ile silaha kaynakli kalmaya devam eder - orasi degismedi.
            bool welded = _weld != null
                       && _weld.TryGetHandAnchor(_left, out _lastAnchor, out bool isSupport)
                       && !isSupport;

            // DONUS de silaha gore olmali: Quest'in grip pose ileri ekseni nisan
            // hattindan ~56 derece asagida (olculmus kalibrasyon), yani ham kumanda
            // donusu kullanilirsa avuc ve basparmak silahi SARMAZ.
            //
            // Bilek hedefinin donusunu dogrudan alamayiz - o, avatarin bilek kemigi
            // konvansiyonunda. Bunun yerine FARKI aliyoruz: weld'in bileğe verdigi
            // donus ile ayni bilegin silahsiz (yalnizca kumandadan) alacagi donus
            // arasindaki delta. Delta dunya uzayinda bir duzeltmedir, iki taraf da
            // ayni kemik cercevesinde oldugu icin konvansiyon sadelesir. Onu
            // kumandanin donusune uygulayinca gorsel dogru sarilir.
            if (welded)
            {
                if (_ik == null && _avatar != null) _ik = _avatar.GetComponentInChildren<AvatarIKController>();
                if (_ik != null && _weld.TryGetWristTarget(_left, out _, out Quaternion wristRot))
                    _gripDelta = wristRot * Quaternion.Inverse(_ik.ControllerWristRotation(_left));
            }

            float target = welded ? 1f : 0f;
            if (Application.isPlaying && BlendSeconds > 0f)
                _weight = Mathf.MoveTowards(_weight, target, Time.deltaTime / BlendSeconds);
            else
                _weight = target;   // editor/olcum: geciste takilip kalmayalim

            if (_weight <= 0f)
            {
                // Bos el: tasiyiciya BIREBIR yapisik, ara islem yok.
                _pose.localPosition = Vector3.zero;
                _pose.localRotation = Quaternion.identity;
                return;
            }

            // Duzeltme de agirlikla harmanlanir: agirlik 0'a dusunce donus tam
            // olarak kumandanin donusudur.
            _pose.SetPositionAndRotation(
                Vector3.Lerp(_carrier.position, _lastAnchor, _weight),
                Quaternion.Slerp(_carrier.rotation, _gripDelta * _carrier.rotation, _weight));

            LogDiagnostic(welded);
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
            float gripAngle = Quaternion.Angle(Quaternion.identity, _gripDelta);
            string wname = "-";
            Vector3 wpos = Vector3.zero;
            if (_weld != null && _weld.TryGetHeldWeapon(_left, out Transform wt) && wt != null)
            {
                wname = wt.name;
                wpos = wt.position;
            }
            Debug.Log(string.Format(
                "[FPEl] {0} agirlik={1:0.00} el->ankraj={2:0.0}mm el->kumanda={3:0.0}mm " +
                "tutus_duzeltme={4:0.0}deg silah={5} silah->ankraj={6:0.0}mm",
                _left ? "SOL" : "SAG", _weight, toAnchor, toCarrier, gripAngle, wname,
                Vector3.Distance(wpos, _lastAnchor) * 1000f));
        }

        static void Piece(GameObject parent, string name, Vector3 localPos, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;

            // CreatePrimitive collider'la geliyor. Elin uzerinde collider kalirsa
            // silah yakalama ve fizik bundan etkilenir - kaldiriliyor.
            // Object.Destroy oyun modu DISINDA ertelenir ve hic calismaz (editor
            // olcum kosumunda collider hayatta kaliyordu), o yuzden moda gore secim.
            var col = go.GetComponent<Collider>();
            if (col != null)
            {
                if (Application.isPlaying) Object.Destroy(col);
                else Object.DestroyImmediate(col);
            }

            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = scale;

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = MakeMaterial(color);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
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
