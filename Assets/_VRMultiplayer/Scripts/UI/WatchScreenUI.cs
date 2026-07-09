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

        [Header("Sabit değer")]
        [Range(0, 100)] public int batteryPercent = 84;

        [Header("Güncelleme")]
        public float refreshHz = 4f;

        static readonly Color ScreenText = new Color(0.93f, 0.97f, 0.95f);
        static readonly Color Muted = new Color(0.56f, 0.72f, 0.66f);
        static readonly Color HealthColor = new Color(0.25f, 0.75f, 0.5f); // tek renk (gradient yok)

        const float BarWidth = 0.55f;
        const float BarHeight = 0.05f;

        PlayerHealth _health;
        Transform _face;
        TextMesh _clock, _compass, _battery, _healthT, _ammo;
        Transform _healthBar;
        float _nextRefresh;

        void Start()
        {
            _health = GetComponentInParent<PlayerHealth>(); // avatar bağlıysa bulur, değilse null
            Build();
        }

        void Update()
        {
            if (_face != null)
            {
                _face.localPosition = faceLocalPosition;
                _face.localRotation = Quaternion.Euler(faceLocalEuler);
                _face.localScale = Vector3.one * faceScale;
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
                _healthBar.localPosition = new Vector3(-0.6f + BarWidth * r * 0.5f, -0.22f, 0f);
            }

            _ammo.text = "∞"; // ∞
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
            _face.localScale = Vector3.one * faceScale;

            // Koyu ekran zemini (plane'in gri yüzünü kapatır)
            var bg = MakeQuad(_face, "Bg", new Color(0.03f, 0.05f, 0.04f, 1f));
            bg.localScale = new Vector3(1.5f, 0.95f, 1f);
            bg.localPosition = new Vector3(0f, 0f, 0.001f);

            // Üst satir: saat | pusula | pil
            _clock   = MakeText(_face, "00:00", new Vector3(-0.66f, 0.34f, 0f), TextAnchor.MiddleLeft,   Muted, 0.12f);
            _compass = MakeText(_face, "N 0°", new Vector3(0f,  0.34f, 0f), TextAnchor.MiddleCenter, Muted, 0.12f);
            _battery = MakeText(_face, "84%",   new Vector3(0.66f,  0.34f, 0f), TextAnchor.MiddleRight,  Muted, 0.12f);

            // Sol: can (%) + bar
            _healthT = MakeText(_face, "100%", new Vector3(-0.6f, 0.05f, 0f), TextAnchor.MiddleLeft, HealthColor, 0.26f);
            _healthBar = MakeQuad(_face, "HealthBar", HealthColor);
            _healthBar.localScale = new Vector3(BarWidth, BarHeight, 1f);
            _healthBar.localPosition = new Vector3(-0.6f + BarWidth * 0.5f, -0.22f, 0f);

            // Sag: mermi
            MakeText(_face, "MERMI", new Vector3(0.45f, 0.1f, 0f), TextAnchor.MiddleCenter, Muted, 0.1f);
            _ammo = MakeText(_face, "∞", new Vector3(0.45f, -0.12f, 0f), TextAnchor.MiddleCenter, ScreenText, 0.32f);

            Refresh();
        }

        TextMesh MakeText(Transform parent, string text, Vector3 pos, TextAnchor anchor, Color color, float size)
        {
            var go = new GameObject("T_" + text);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = Vector3.one * size;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = anchor;
            tm.alignment = TextAlignment.Center;
            tm.characterSize = 0.1f;
            tm.fontSize = 72;
            tm.color = color;
            return tm;
        }

        static Transform MakeQuad(Transform parent, string name, Color color)
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
            q.GetComponent<MeshRenderer>().sharedMaterial = m;
            return q.transform;
        }
    }
}
