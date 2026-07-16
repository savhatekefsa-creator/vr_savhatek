using System;
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
        static readonly Color BatteryCol  = new Color(0.45f, 0.85f, 0.55f);     // pil dolu

        const float BarWidth = 0.55f;
        const float BarHeight = 0.05f;
        const float BarLeftX = -0.64f;
        const float BarY = -0.22f;
        const float BarZ = 0f;

        // Katmanlar AYNI duzlemde (z=0) durur; hangisinin uste cizilecegini renderQueue belirler.
        // Boylece saatin minik olceginde (z farklari mikrometreye dusunce) z-fighting olmaz.
        const int QueueBase = 3000;

        PlayerHealth _health;
        HandGrabber _grabber;
        Transform _face;
        TextMesh _clock, _compass, _battery, _healthT, _ammo;
        Transform _healthBar;
        float _nextRefresh;

        // Genel olcek x esnetme. Saatin kadran orani icerigin oranindan farkliysa X/Y ile duzeltilir.
        Vector3 FaceScaleVec => new Vector3(faceScale * faceStretch.x, faceScale * faceStretch.y, faceScale);

        void Start()
        {
            _health = GetComponentInParent<PlayerHealth>(); // avatar bağlıysa bulur, değilse null
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
            _battery.text = batteryPercent + "%";

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

            _ammo.text = AmmoText();
        }

        /// <summary>Elindeki silah mermi sayıyorsa gerçek sayı; saymıyorsa (silahsızsın ya da
        /// o silahta şarjör kapalı) eskisi gibi ∞.</summary>
        string AmmoText()
        {
            var w = HeldWeapon();
            if (w == null || !w.UsesAmmo) return "∞";
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
            var rig = XRRigReference.Instance;
            Transform head = rig != null && rig.head != null ? rig.head
                           : (Camera.main != null ? Camera.main.transform : null);
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

            // --- Katmanlar: hepsi z=0 duzleminde, sira renderQueue ile (arkadan one) ---
            var frame = MakeQuad(_face, "Frame", Bezel, 0);          // ince cerceve
            frame.localScale = new Vector3(1.57f, 1.02f, 1f);

            var bg = MakeQuad(_face, "Bg", ScreenBg, 1);             // ekran zemini
            bg.localScale = new Vector3(1.5f, 0.95f, 1f);

            var top = MakeQuad(_face, "TopStrip", TopStripCol, 2);   // ust bar (cukur ton)
            top.localScale = new Vector3(1.5f, 0.2f, 1f);
            top.localPosition = new Vector3(0f, 0.375f, 0f);

            // Bevel: ust parlak + alt golge (derinlik hissi)
            Line("BevelTop", new Vector3(0f,  0.468f, 0f), new Vector3(1.5f, 0.012f, 1f), BevelHi, 3);
            Line("BevelBot", new Vector3(0f, -0.468f, 0f), new Vector3(1.5f, 0.012f, 1f), BevelLo, 3);

            // Blok ayirici cizgiler
            Line("HDiv", new Vector3(0f,  0.27f, 0f), new Vector3(1.5f,  0.014f, 1f), Divider, 4);  // ust bar / orta
            Line("VDiv", new Vector3(0f, -0.09f, 0f), new Vector3(0.014f, 0.72f, 1f), Divider, 4);  // sol / sag

            // Can barinin olugu + dolu kismi
            var groove = MakeQuad(_face, "HealthGroove", BarGroove, 5);
            groove.localScale = new Vector3(BarWidth + 0.03f, BarHeight + 0.035f, 1f);
            groove.localPosition = new Vector3(BarLeftX + BarWidth * 0.5f, BarY, 0f);

            BuildBatteryIcon(new Vector3(0.42f, 0.375f, 0f));

            _healthBar = MakeQuad(_face, "HealthBar", HealthColor, 9);
            _healthBar.localScale = new Vector3(BarWidth, BarHeight, 1f);
            _healthBar.localPosition = new Vector3(BarLeftX + BarWidth * 0.5f, BarY, BarZ);

            // --- Yazilar (renderQueue en yuksek: her zaman en ustte) ---
            _clock   = MakeText(_face, "00:00", new Vector3(-0.70f, 0.375f, 0f), TextAnchor.MiddleLeft,   ScreenText, 0.10f);
            _compass = MakeText(_face, "N 0°",  new Vector3( 0.00f, 0.375f, 0f), TextAnchor.MiddleCenter, Accent,     0.10f);
            _battery = MakeText(_face, "84%",   new Vector3( 0.71f, 0.375f, 0f), TextAnchor.MiddleRight,  ScreenText, 0.095f);
            _healthT = MakeText(_face, "100%",  new Vector3(-0.64f, 0.06f,  0f), TextAnchor.MiddleLeft,   HealthColor, 0.24f);
            MakeText(_face, "MERMI", new Vector3(0.38f, 0.10f, 0f), TextAnchor.MiddleCenter, Muted, 0.095f);
            _ammo = MakeText(_face, "∞", new Vector3(0.38f, -0.16f, 0f), TextAnchor.MiddleCenter, ScreenText, 0.34f);

            Refresh();
        }

        // Ince dikdortgen: blok ayirici cizgi ya da bevel.
        void Line(string name, Vector3 pos, Vector3 scale, Color color, int order)
        {
            var q = MakeQuad(_face, name, color, order);
            q.localScale = scale;
            q.localPosition = pos;
        }

        // Kucuk pil ikonu: govde + ic bosluk + dolu kisim + uc.
        void BuildBatteryIcon(Vector3 pos)
        {
            var body = MakeQuad(_face, "BatBody", Muted, 6);
            body.localScale = new Vector3(0.17f, 0.09f, 1f);
            body.localPosition = pos;

            var inner = MakeQuad(_face, "BatInner", ScreenBg, 7);
            inner.localScale = new Vector3(0.15f, 0.07f, 1f);
            inner.localPosition = pos;

            float f = Mathf.Clamp01(batteryPercent / 100f);
            var fill = MakeQuad(_face, "BatFill", BatteryCol, 8);
            fill.localScale = new Vector3(0.14f * f, 0.055f, 1f);
            fill.localPosition = pos + new Vector3(-0.07f + 0.14f * f * 0.5f, 0f, 0f);

            var nub = MakeQuad(_face, "BatNub", Muted, 6);
            nub.localScale = new Vector3(0.016f, 0.04f, 1f);
            nub.localPosition = pos + new Vector3(0.094f, 0f, 0f);
        }

        TextMesh MakeText(Transform parent, string text, Vector3 pos, TextAnchor anchor, Color color, float size)
        {
            var go = new GameObject("T_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * size;
            var tm = go.AddComponent<TextMesh>();
            // Unity 6 varsayilan TextMesh fontunu kaldirdi; elle ata yoksa yazi gorunmez.
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font != null)
            {
                tm.font = font;
                var mr = go.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    // Yazilar her zaman en ustte cizilsin (font materyalinin kopyasi, paylasilani bozmadan).
                    var tmat = new Material(font.material) { renderQueue = QueueBase + 20 };
                    mr.sharedMaterial = tmat;
                }
            }
            tm.text = text;
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.1f;
            tm.fontSize = 72;
            tm.color = color;
            return tm;
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
            var sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            if (m.HasProperty("_ZWrite")) m.SetInt("_ZWrite", 0);
            m.renderQueue = QueueBase + order;
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            return q.transform;
        }
    }
}
