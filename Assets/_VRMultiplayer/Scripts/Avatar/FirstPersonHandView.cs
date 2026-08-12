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
        const string GloveTipMat = "FPHands/Meta/M_FPGlove_Uc";

        // Meta'nin eli anatomik olarak GERCEK boyutta (bilek->uc ~190 mm, olcek 1.000);
        // ortada olcek hatasi yok. Ama VR'da gercek boyutlu el sik sik kucuk algilanir,
        // ustelik kolu/mansetı olmayan bir el daha da kucuk okunur. Cihazda ayarlanacak
        // tek dokunus noktasi burasi. Olcek "Hand" dugumune uygulanir - FP_HandView koku
        // ters-olcek dugumudur, oraya dokunmak makaslama kuralini bozar.
        const float HandScale = 1.10f;

        // Elin kumandaya gore ince ayari. SIFIR = yalnizca OpenXR grip cerçevesi.
        // Spec dogru cerceveyi verir ama son 10-20 dereceyi veremez: dogru durus
        // kumandanin FIZIKSEL sekline ve gercek elin sapi nasil kavradigina bagli.
        // O yuzden bu iki sayi cihazda ayarlanip buraya islenir.
        // YALNIZ SAG EL: sol el aynalanarak turetilir (bkz. BuildHandModel).
        static readonly Vector3 WristOffsetEuler = Vector3.zero;
        static readonly Vector3 WristOffsetPosition = Vector3.zero;

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
                UpdateDot(0f);
                return;
            }

            // Sapma: elin oturdugu ankraj ile kumandanin GERCEK yeri arasindaki mesafe.
            // Nokta bunu gorunur kilar - oyuncu elinin nerede oldugunu kaybetmesin ve
            // kopmanin yaklastigini gorsun.
            UpdateDot(Vector3.Distance(_carrier.position, _lastAnchor));

            // Duzeltme de agirlikla harmanlanir: agirlik 0'a dusunce donus tam
            // olarak kumandanin donusudur.
            _pose.SetPositionAndRotation(
                Vector3.Lerp(_carrier.position, _lastAnchor, _weight),
                Quaternion.Slerp(_carrier.rotation, _gripDelta * _carrier.rotation, _weight));

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
                Vector3 fingers = (mid.position - wrist.position).normalized;
                Vector3 rawCross = Vector3.Cross(fingers, (idx.position - pky.position).normalized).normalized;
                Vector3 palmOut = left ? -rawCross : rawCross;
                Vector3 tube = Vector3.Cross(fingers, palmOut).normalized;
                if (left) tube = -tube;
                Vector3 thumbDir = (thumb.position - wrist.position).normalized;
                Vector3 up = (thumbDir - tube * Vector3.Dot(thumbDir, tube)).normalized;
                if (up.sqrMagnitude > 1e-6f)
                {
                    go.transform.rotation = Quaternion.Inverse(Quaternion.LookRotation(tube, up)) * go.transform.rotation;

                    // Yazili duzeltme: cihazda ayarlanip buraya islenir. YALNIZ SAG EL
                    // icin yazilir, sol el aynalanarak turetilir - silah profillerinde
                    // de gecerli olan kural (bkz. WeaponGripTuner). Boylece simetri
                    // ayarin degil YAPININ garantisi olur; sol el icin ayri bir sayi yok.
                    Quaternion offRot = Quaternion.Euler(WristOffsetEuler);
                    Vector3 offPos = WristOffsetPosition;
                    if (left)
                    {
                        offRot = Weapons.WeaponGripMath.MirrorX(offRot);
                        offPos = Weapons.WeaponGripMath.MirrorX(offPos);
                    }
                    go.transform.localRotation = offRot * go.transform.localRotation;
                    go.transform.localPosition += offPos;
                }

                // Bilek tam Pose orijinine gelsin (modelin kokunde olmayabilir).
                go.transform.position += pose.position - wrist.position;
            }

            // Askeri boyama: govde + uc bogumlar ayri alt-mesh (renkler askerin kendi
            // eldiven dokusundan olculdu). Mesh FBX'inkiyle ayni vertex/bindpose setini
            // tasiyor, yalnizca ucgenler iki gruba ayrildi.
            var painted = Resources.Load<Mesh>(ModelPath.Replace("OculusHand_", "Glove_Meta_") + (left ? "L" : "R"));
            var body = Resources.Load<Material>(GloveBodyMat);
            var tip = Resources.Load<Material>(GloveTipMat);
            if (painted != null) smr.sharedMesh = painted;
            if (body != null && tip != null) smr.sharedMaterials = new[] { body, tip };

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
