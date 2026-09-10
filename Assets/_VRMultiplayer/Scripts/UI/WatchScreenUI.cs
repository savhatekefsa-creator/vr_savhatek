using System;
using TMPro;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Kol saati ekranı: WatchScreen plane'inin yüzeyine saat, pusula, pil, can (%) ve mermi (∞)
    /// basar. Metinler bir "Face" kökü altında, o da bu objenin (WatchScreen) çocuğu olarak
    /// oluşturulur; WatchScreen kol kemiğine bağlı olduğundan ekran kolla birlikte hareket eder.
    ///
    /// WatchScreen objesine Add Component ile eklenir. Yerleşim/açı/ölçek Inspector'dan CANLI
    /// ayarlanır (Scene'de görünür). Can, avatar oyuncuya bağlıysa gerçek PlayerHealth'ten;
    /// bağlı değilse placeholder (%100) gösterilir. Pil sabit, saat/pusula gerçek, mermi ∞.
    /// </summary>
    public class WatchScreenUI : MonoBehaviour
    {
        [Header("Yüzey yerleşimi (WatchScreen'e göre — ince ayar)")]
        public Vector3 faceLocalPosition = new Vector3(0f, 0f, 0.001f);
        public Vector3 faceLocalEuler = Vector3.zero;
        [Tooltip("Ekran içeriğinin genel ölçeği. Plane boyutuna göre gözle ayarla.")]
        public float faceScale = 0.02f;
        [Tooltip("İçeriği X/Y'de ayrı ayrı esnetir (1,1 = bozulmasız). Saat kadranı genişse X'i büyüt ki yanlarda boşluk kalmasın.")]
        public Vector2 faceStretch = Vector2.one;

        [Header("Sabit değer")]
        /// <summary>Pil yuzdesi icin YEDEK deger. Cihaz gercek pili bildiriyorsa
        /// (bkz. <see cref="BatteryPercent"/>) bu kullanilmaz; yalnizca masaustu
        /// Editor gibi pili olmayan ortamlarda ekranda bir sey gorunsun diye durur.</summary>
        [Range(0, 100)] public int batteryPercent = 84;

        [Header("Güncelleme")]
        public float refreshHz = 4f;

        static readonly Color ScreenText  = new Color(0.93f, 0.97f, 0.95f);
        static readonly Color Muted       = new Color(0.56f, 0.72f, 0.66f);
        static readonly Color HealthColor = new Color(0.25f, 0.75f, 0.5f); // tek renk (gradient yok)
        static readonly Color ScreenBg    = new Color(0.035f, 0.055f, 0.05f);   // ekran zemini
        static readonly Color Bezel       = new Color(0.09f, 0.14f, 0.13f);     // cerceve (derinlik)
        static readonly Color TopStripCol = new Color(0.02f, 0.035f, 0.033f);   // ust bar cukur ton
        static readonly Color Divider     = new Color(0.14f, 0.24f, 0.22f);     // blok ayirici cizgi
        static readonly Color BevelHi     = new Color(0.18f, 0.30f, 0.28f);     // ust parlak (derinlik)
        static readonly Color BevelLo     = new Color(0.00f, 0.00f, 0.00f);     // alt golge (derinlik)
        static readonly Color BarGroove   = new Color(0.015f, 0.03f, 0.028f);   // bar oluk (cukur)
        static readonly Color Accent      = new Color(0.40f, 0.80f, 0.72f);     // teal vurgu (pusula)

        // Can bari. Eski degerler icerik alani 1.5 x 0.95 iken secilmisti ve bar ekranin
        // yalnizca sol yarisini kapliyordu (sag yari mermi blokuna ayrilmisti). Yeni duzen
        // satir tabanli, dolayisiyla bar TAM GENISLIGE yayilir.
        const float BarWidth = 1.44f;
        const float BarHeight = 0.055f;
        const float BarLeftX = -0.72f;
        const float BarY = 0f;
        const float BarZ = 0f;

        // Katmanlar AYNI duzlemde (z=0) durur; hangisinin uste cizilecegini renderQueue belirler.
        // Boylece saatin minik olceginde (z farklari mikrometreye dusunce) z-fighting olmaz.
        const int QueueBase = 3000;

        PlayerHealth _health;
        PlayerIdentity _identity;   // K/Ö sayaci icin
        HandGrabber _grabber;
        Transform _face;
        TMP_Text _clock, _compass, _battery, _healthT, _ammo, _kd;
        Transform _healthBar;
        float _nextRefresh;

        // Genel olcek x esnetme. Saatin kadran orani icerigin oranindan farkliysa X/Y ile duzeltilir.
        Vector3 FaceScaleVec => new Vector3(faceScale * faceStretch.x, faceScale * faceStretch.y, faceScale);

        void Start()
        {
            _health = GetComponentInParent<PlayerHealth>(); // avatar bağlıysa bulur, değilse null
            _identity = GetComponentInParent<PlayerIdentity>(); // öldürme/ölme sayacı
            _grabber = GetComponentInParent<HandGrabber>(); // elindeki silahın mermisi için
            Build();
        }

        void Update()
        {
            if (_face != null)
            {
                _face.localPosition = faceLocalPosition;
                _face.localRotation = Quaternion.Euler(faceLocalEuler);
                _face.localScale = FaceScaleVec;
            }

            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + 1f / Mathf.Max(refreshHz, 1f);
                Refresh();
            }
        }

        void Refresh()
        {
            _clock.text = DateTime.Now.ToString("HH:mm");
            _compass.text = CompassText();
            _battery.text = BatteryPercent() + "%";

            int pct = _health != null
                ? Mathf.RoundToInt(100f * _health.Health.Value / PlayerHealth.MaxHealth)
                : 100; // placeholder (avatar bağlanınca gerçek)
            _healthT.text = pct + "%";
            if (_healthBar != null)
            {
                float r = Mathf.Clamp01(pct / 100f);
                _healthBar.localScale = new Vector3(BarWidth * r, BarHeight, 1f);
                _healthBar.localPosition = new Vector3(BarLeftX + BarWidth * r * 0.5f, BarY, BarZ);
            }

            // SONSUZ SEMBOLU AYRI OLCEKLENIR. "∞" glifi rakamlarin ucte biri yuksekligindedir;
            // ayni fontSize'da okunmaz hale geliyordu. Okunacak bir SAYI degil taninacak bir
            // SEMBOL oldugu icin buyutmek duzeni bozmaz — tek karakter, dar. Ayni sey
            // "silah yok" cizgisi icin de gecerli.
            string ammo = AmmoText();
            _ammo.text = ammo;
            _ammo.fontSize = (ammo == "∞" || ammo == NoWeaponAmmo)
                           ? TextFontSize * 2.2f : TextFontSize;

            // Kisisel skor. Mac/tur mantigi YOK — yalnizca bu oturumdaki sayac.
            _kd.text = _identity != null
                ? "K " + _identity.Kills.Value + "   Ö " + _identity.Deaths.Value
                : "K 0   Ö 0";
        }

        /// <summary>
        /// Gozlugun GERCEK pil yuzdesi. Bu alan bugune kadar Inspector'daki sabit 84'u
        /// gosteriyordu — yani ekranin en dar yerinde uydurma bir sayi, uc okunabilir bilgiyle
        /// yer yarisiyordu. Quest bir Android cihaz oldugu icin deger dogrudan alinabiliyor.
        ///
        /// Pili olmayan ortamlarda (masaustu Editor) API -1 doner; o zaman Inspector'daki
        /// yedege dusulur.
        /// </summary>
        int BatteryPercent()
        {
            float lvl = SystemInfo.batteryLevel;      // 0..1, bilinmiyorsa -1
            return lvl < 0f ? batteryPercent : Mathf.RoundToInt(lvl * 100f);
        }

        /// <summary>Elde silah yokken mermi alanina yazilan sey. Sayi DEGIL, "veri yok"
        /// isareti — 0 yazmak "sarjorun bos" demek olurdu ve o baska bir durum.</summary>
        const string NoWeaponAmmo = "---";

        /// <summary>Elindeki silah mermi sayıyorsa gerçek sayı; saymıyorsa ∞; elin bossa
        /// <see cref="NoWeaponAmmo"/>.</summary>
        string AmmoText()
        {
            var w = HeldWeapon();
            // ELDE SILAH YOKKEN SONSUZ YAZMAK MANTIK HATASIYDI: "sinirsiz mermin var" demek
            // oluyordu, oysa ortada silah bile yok. Iki durum ayrildi:
            //   silah YOK          -> cizgi (bilgi yok)
            //   silah var, saymiyor -> sonsuz (gercekten sinirsiz)
            if (w == null) return NoWeaponAmmo;
            if (!w.UsesAmmo) return "∞";
            if (w.IsReloading) return "···";
            return w.Ammo.ToString();
        }

        NetworkWeapon HeldWeapon()
        {
            if (_grabber == null) return null;
            // Unity'nin sahte-null'ı yüzünden ?? kullanılmaz; == null operatör aşırı yüklemesi
            // yok edilmiş objeyi de yakalar.
            var g = _grabber.HeldRight;
            if (g == null) g = _grabber.HeldLeft;
            return g != null ? g.GetComponent<NetworkWeapon>() : null;
        }

        string CompassText()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return "N 0°";
            Vector3 f = head.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) return "N 0°";
            float h = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            if (h < 0f) h += 360f;
            string[] d = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return d[Mathf.RoundToInt(h / 45f) % 8] + " " + Mathf.RoundToInt(h) + "°";
        }

        // ------------------------------------------------------------- kurulum

        void Build()
        {
            _face = new GameObject("Face").transform;
            _face.SetParent(transform, false);
            _face.localPosition = faceLocalPosition;
            _face.localRotation = Quaternion.Euler(faceLocalEuler);
            _face.localScale = FaceScaleVec;

            // --- YERLESIM: KADRANIN KENDI ORANINA GORE ---
            //
            // NEDEN DEGISTI: ekran artik saatin GERCEK kadranina oturuyor (bkz. WristWatch.DialFace)
            // ve kadran 3.5 x 2.8 cm. Eski duzen 6.3 x 4.1 cm'lik bir ekran icin yapilmisti;
            // oldugu gibi kuculunce yazilar 0.8-1.1 mm'ye dustu. Olculdu: ~25 cm bakis
            // mesafesinde bu 0.2-0.25 derece eder, VR'da rahat okuma esigi ise kabaca 0.5-1
            // derece. Yani ekran dogru yerdeydi ama okunmuyordu.
            //
            // NOT: dikey kenar sonradan 2.60 -> 2.40 cm'e cekildi (cihazda istendi).
            // Yatay kenara DOKUNULMADI; yalnizca Y degerleri 0.938 ile olceklendi, yani
            // satir aralari %6.2 daraldi. YAZI BOYUTLARI AYNI KALDI — faceStretch ile
            // ezmek daha kolay olurdu ama o glifleri de yassiltir ve TMP'ye gecmenin
            // amacini (keskin yazi) bozardi.
            //
            // IKI DEGISIKLIK: (1) icerik kadranin ENINI de kullaniyor — 0.95 -> 1.19 birim,
            // cunku eski oran 1.5:0.95 idi ve kadranin 2.8 cm'lik eninde 0.5 cm bos kaliyordu.
            // (2) sol/sag ikiye bolme birakildi; duzen SATIR tabanli oldu, boylece her satir
            // tam genisligi kullanir ve yazilar 2-4.5 mm'ye cikar.
            var frame = MakeQuad(_face, "Frame", Bezel, 0);          // ince cerceve
            frame.localScale = new Vector3(1.57f, 1.178f, 1f);       // kadranin eni eksi 2 mm

            var bg = MakeQuad(_face, "Bg", ScreenBg, 1);             // ekran zemini
            bg.localScale = new Vector3(1.50f, 1.116f, 1f);

            var top = MakeQuad(_face, "TopStrip", TopStripCol, 2);   // ust bar (cukur ton)
            top.localScale = new Vector3(1.50f, 0.206f, 1f);
            top.localPosition = new Vector3(0f, 0.441f, 0f);

            // Bevel: ust parlak + alt golge (derinlik hissi)
            Line("BevelTop", new Vector3(0f,  0.563f, 0f), new Vector3(1.50f, 0.014f, 1f), BevelHi, 3);
            Line("BevelBot", new Vector3(0f, -0.563f, 0f), new Vector3(1.50f, 0.014f, 1f), BevelLo, 3);

            // Satir ayiricilari. Dikey ayirici (VDiv) KALKTI: sol/sag bolme, genisligin yariya
            // dusmesi demekti ve bu ekranda yazi boyutunu yaristiran asil sey oydu.
            Line("HDiv1", new Vector3(0f,  0.338f, 0f), new Vector3(1.50f, 0.014f, 1f), Divider, 4);
            Line("HDiv2", new Vector3(0f, -0.141f, 0f), new Vector3(1.50f, 0.014f, 1f), Divider, 4);

            // Can barinin olugu + dolu kismi
            var groove = MakeQuad(_face, "HealthGroove", BarGroove, 5);
            groove.localScale = new Vector3(BarWidth + 0.04f, BarHeight + 0.035f, 1f);
            groove.localPosition = new Vector3(BarLeftX + BarWidth * 0.5f, BarY, 0f);

            _healthBar = MakeQuad(_face, "HealthBar", HealthColor, 9);
            _healthBar.localScale = new Vector3(BarWidth, BarHeight, 1f);
            _healthBar.localPosition = new Vector3(BarLeftX + BarWidth * 0.5f, BarY, BarZ);

            // --- Yazilar (renderQueue en yuksek: her zaman en ustte) ---
            // Boyutlar OLCULEREK secildi: bu olcekte 1 birim `size` ~11.2 mm harf yuksekligi
            // veriyor. Birincil bilgi (can, mermi) 4-4.5 mm, ikincil bilgi 2-2.5 mm.
            // HER SATIRDA IKI OGE. Uc oge denendi ve OLCULDU: ust satirda saat+pusula+pil
            // yan yana durunca ucu de 1.7-2.3 mm'ye sikisiyordu, yani ikisi okuma esiginin
            // altinda kaliyordu. Ikiye dusunce ayni satirda 2.7 mm'ye cikiyorlar.
            //
            // "MERMİ" etiketi KALDIRILDI: 3.5 cm'lik bir ekranda her etiket, yanindaki
            // DEGERIN boyutundan calar. Can barinin altindaki buyuk sayi zaten mermidir.
            // UST SATIR EN UZUN OLASI METNE GORE OLCULENDI, gorunen metne gore degil.
            // Pusula "N 0°" degil "NW 359°" olabilir — 4 yerine 7 karakter. 0.26'da o hal
            // saatin uzerine biniyordu; 0.25'te 1.5 mm bosluk kaliyor. Bu satiri buyutmenin
            // tek yolu pusuladan derece SAYISINI atmak (2 karaktere duser, ~1.6x buyur).
            _clock   = MakeText(_face, "00:00",     new Vector3(-0.72f,  0.441f, 0f), TextAnchor.MiddleLeft,  ScreenText,  0.25f);
            _compass = MakeText(_face, "N 0°",      new Vector3( 0.72f,  0.441f, 0f), TextAnchor.MiddleRight, Accent,      0.25f);
            _healthT = MakeText(_face, "100%",      new Vector3(-0.72f,  0.178f, 0f), TextAnchor.MiddleLeft,  HealthColor, 0.40f);
            _kd      = MakeText(_face, "K 0   Ö 0", new Vector3( 0.72f,  0.178f, 0f), TextAnchor.MiddleRight, Muted,       0.18f);
            // ALT SATIR EN KOTU DURUMA GORE: mermi "120" olabilir. 0.44'te genisligi 0.987
            // birime cikip pilin uzerine 0.060 biniyordu (olculdu). 0.40 + pil 0.23 ile
            // aralarinda 1.7 mm bosluk kaliyor ve ikisi de okuma esiginin uzerinde.
            // MERMI ETIKETI GERI GELDI, ama YAN YANA degil UST USTE. Yan yana koymak sayinin
            // boyutundan calıyordu; ustune koyunca ikisi de yerini koruyor ve "bu sayi nedir"
            // sorusu kalmiyor. Cihazda "ya mermi sembolu gelsin ya ustte MERMI yazsin altta
            // sayisi" dendi — ikincisi.
            MakeText(_face, "MERMİ",                new Vector3(-0.72f, -0.202f, 0f), TextAnchor.MiddleLeft,  Muted,       0.17f);
            _ammo    = MakeText(_face, "∞",         new Vector3(-0.72f, -0.427f, 0f), TextAnchor.MiddleLeft,  ScreenText,  0.30f);
            _battery = MakeText(_face, "84%",       new Vector3( 0.72f, -0.328f, 0f), TextAnchor.MiddleRight, Muted,       0.23f);

            Refresh();
        }

        // Ince dikdortgen: blok ayirici cizgi ya da bevel.
        void Line(string name, Vector3 pos, Vector3 scale, Color color, int order)
        {
            var q = MakeQuad(_face, name, color, order);
            q.localScale = scale;
            q.localPosition = pos;
        }

        /// <summary>
        /// Ekran yazisi. TextMesh yerine TMP: eski yol fontu SABIT bir piksel boyutunda
        /// (fontSize 72) bir atlasa rasterize ediyordu, saat ise goze 20-30 cm mesafede duruyor;
        /// yakinlasinca yazi cozunurlugu bitiyor ve bulaniyordu. TMP isaretli mesafe alani (SDF)
        /// kullanir, yani kenarlar shader'da yeniden kurulur ve her mesafede keskin kalir.
        ///
        /// BOYUT DEGISMEDI. Olculdu: eski ayarlarla ("100%", characterSize 0.1, fontSize 72)
        /// yazi 1.84 x 0.80 yerel birim kapliyordu; TMP'de ayni olcuyu <see cref="TextFontSize"/>
        /// veriyor (dogrulandi: 1.8414 x 0.8044). Boylece bu adim YALNIZCA cizim kalitesini
        /// degistirir, yerlesimi degil — boyut/duzen ayari ayri bir adim.
        /// </summary>
        TMP_Text MakeText(Transform parent, string text, Vector3 pos, TextAnchor anchor, Color color, float size)
        {
            var go = new GameObject("T_" + text);
            go.transform.SetParent(parent, false);

            var tmp = go.AddComponent<TextMeshPro>();
            var rt = tmp.rectTransform;
            rt.localPosition = pos;
            rt.localScale = Vector3.one * size;

            // ANKRAJ: TextMesh'te cipa noktasi dogrudan transform orijiniydi. TMP ise yaziyi bir
            // RectTransform'un ICINE dizer; ayni davranisi almak icin hizalama ile PIVOT birlikte
            // ayarlanmali. Pivot, rect'in hizalandigi kenarini orijine getirir — yalnizca
            // hizalamayi ayarlayip pivotu birakmak butun yazilari rect genisligi kadar kaydirirdi.
            rt.pivot = PivotOf(anchor);
            rt.sizeDelta = RectSize;

            tmp.font = FontAsset;
            tmp.fontSize = TextFontSize;
            tmp.text = text;
            tmp.color = color;
            tmp.alignment = AlignOf(anchor);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;   // rect yalnizca hizalama icin
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            // Materyal KUYRUK BASINA paylasilir (UITheme.ApplyFont ile ayni desen): ekrandaki
            // alti yazi tek materyal kullanir, alti ayri kopya uretmez.
            tmp.fontSharedMaterial = QueuedFontMaterial(QueueBase + 20);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }
            return tmp;
        }

        /// <summary>TMP fontSize 1 birimi kac YEREL birim yukseklik verir. Olculdu
        /// (SavHaTek_SDF, 90pt ornekleme): 0.11172. Buradan TextFontSize turetildi.</summary>
        const float TmpUnitsPerFontSize = 0.11172f;

        /// <summary>Eski TextMesh ile AYNI fiziksel boyutu veren TMP fontSize'i (0.8044 / 0.11172).
        /// Sabit gomulu degil, olculerek bulundu; font asset'i yeniden uretilirse
        /// TmpUnitsPerFontSize ile birlikte yeniden olculmeli.</summary>
        const float TextFontSize = 7.2f;

        /// <summary>Yazi rect'i. Kirpma YAPMAZ (NoWrap + Overflow) — yalnizca hizalamanin
        /// dayandigi cerceve. En uzun metin ("K 0   O 0") ~4.1 birim, bol pay birakiliyor.</summary>
        static readonly Vector2 RectSize = new Vector2(20f, 4f);

        /// <summary>Font asset yolu (Resources). Menu ile LiberationSans.ttf'ten uretildi:
        /// ASCII + Turkce + derece + sonsuz = 116 glif, TEK atlas, Static populasyon.
        /// Static onemli: cihazda calisma aninda glif rasterize edilmez.</summary>
        const string FontAssetPath = "Fonts/SavHaTek_SDF";

        TMP_FontAsset _font;

        TMP_FontAsset FontAsset
        {
            get
            {
                if (_font != null) return _font;
                _font = Resources.Load<TMP_FontAsset>(FontAssetPath);
                if (_font == null)
                {
                    // Yedek: TMP'nin varsayilani. DIKKAT — onda sonsuz (U+221E) ve Turkce
                    // i-noktasiz/g-yumusak/s-cedilla YOKTUR, o karakterler kutu cikar.
                    _font = TMP_Settings.defaultFontAsset;
                    Debug.LogWarning("[Saat] Font asset yok: Resources/" + FontAssetPath +
                                     " — TMP varsayilanina dusuldu, bazi karakterler eksik cizilir.");
                }
                return _font;
            }
        }

        Material QueuedFontMaterial(int queue)
        {
            var fa = FontAsset;
            if (fa == null) return null;
            // PAYLASILAN materyal (UITheme onbellegi: font + kuyruk basina TEK). Eskiden burada
            // `new Material(fa.material)` vardi ve bu bilesen HER UZAK OYUNCUNUN avatarinda
            // kuruluyor (NetworkVRPlayer saati yalnizca IsOwner dalinda, yani KENDI avatarinda
            // kapatiyor) — yani oyuncu basina benzersiz bir yazi materyali, hicbiri batch'lenmiyor
            // ve hicbiri yok edilmiyordu. Dosyanin kendi yorumu "materyal kuyruk basina
            // PAYLASILIR" diyordu; kod bunu yapmiyordu.
            return VRMultiplayer.UI.UITheme.SharedFontMaterial(fa, queue);
        }

        /// <summary>TextAnchor -> rect pivotu. Cagri yerleri yalnizca Middle* kullaniyor;
        /// digerleri guvenli sekilde ortalanir.</summary>
        static Vector2 PivotOf(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft:  return new Vector2(0f, 0.5f);
                case TextAnchor.MiddleRight: return new Vector2(1f, 0.5f);
                default:                     return new Vector2(0.5f, 0.5f);
            }
        }

        static TextAlignmentOptions AlignOf(TextAnchor a)
        {
            switch (a)
            {
                case TextAnchor.MiddleLeft:  return TextAlignmentOptions.Left;
                case TextAnchor.MiddleRight: return TextAlignmentOptions.Right;
                default:                     return TextAlignmentOptions.Center;
            }
        }

        // order = cizim sirasi (buyuk = uste). Katmanlar ayni duzlemde durdugu icin
        // derinlik yerine renderQueue kullaniyoruz; ZWrite kapali ki birbirlerini kesmesinler.
        static Transform MakeQuad(Transform parent, string name, Color color, int order)
        {
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            q.name = name;
            var col = q.GetComponent<Collider>();
            if (col != null) Destroy(col);
            q.transform.SetParent(parent, false);
            var m = new Material(VRMultiplayer.UI.UITheme.SafeUnlitShader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            m.renderQueue = QueueBase + order;
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            // Quad'lar oyuncuya ozel renkte oldugu icin paylasilamaz; ama artik SAHIPLENIR:
            // avatar yok olunca (oyuncu ciktiginda) materyal de yok olur. Eskiden OnDestroy
            // yoktu ve her cikan oyuncudan geriye materyaller kaliyordu.
            VRMultiplayer.UI.OwnedMaterial.Attach(q, m);
            return q.transform;
        }
    }
}
