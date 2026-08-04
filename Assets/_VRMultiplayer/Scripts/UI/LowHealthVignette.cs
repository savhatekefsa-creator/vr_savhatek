using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Can dustukce gorus alaninin kenarlarinda beliren kirmizi bir vignette (kenar kararmasi)
    /// efekti. Can esik altina indikce yogunlasir ve nabiz gibi hafifce atar (can azaldikca
    /// hizlanir); oyuncuya bara bakmadan "kritik durumdayim" hissini verir. PlayerHUD her can
    /// degisiminde <see cref="SetHealthRatio"/> ile bunu besler.
    /// </summary>
    public class LowHealthVignette : MonoBehaviour
    {
        [Tooltip("Can bu oranin (0-1) altina dustugunde vignette gorunmeye baslar.")]
        public float threshold = 0.4f;
        [Tooltip("Can sifira yaklastigindaki en yuksek opaklik.")]
        public float maxAlpha = 0.6f;
        [Tooltip("Nabiz atisinin siddeti (0 = sabit, nabizsiz).")]
        public float pulseAmount = 0.25f;

        static readonly Color VignetteColor = new Color(0.8f, 0.05f, 0.03f);

        Transform _quad;
        Material _mat;
        float _ratio = 1f;

        void Awake()
        {
            // Doku UITheme'de tek kaynak: olum ekrani da ayni kalibre dokuyu kullaniyor
            // (bkz. UITheme.VignetteTexture — aci esleme notu orada).
            _quad = UITheme.MakeVignetteQuad(transform, "Vignette Quad", VignetteColor, out _mat);
            _quad.localScale = new Vector3(2f, 2f, 1f);
            _quad.gameObject.SetActive(false);
        }

        /// <summary>Guncel can oranini (0-1) bildirir.</summary>
        public void SetHealthRatio(float ratio)
        {
            _ratio = Mathf.Clamp01(ratio);
        }

        void LateUpdate()
        {
            // Esigin ustundeyse ya da oyuncu olmusse efekt kapali.
            bool active = _ratio > 0f && _ratio < threshold;
            if (_quad.gameObject.activeSelf != active) _quad.gameObject.SetActive(active);
            if (!active) return;

            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            // Katman 2: en geride, hasar flasinin arkasi — ust uste binince titreme olmasin.
            // Mesafe HeadOverlay'den; sabit 0.52 m durbun merceginin arkasinda kaliyordu ve
            // durbune bakan oyuncu vinyeti hic gormuyordu. Quad olcegi mesafeyle AYNI oranla
            // kuculur: doku esigi (uv 0.30) 0.52 m'ye gore kalibre, atan(uv*olcek/mesafe)
            // sabit kalmazsa kizarma yine lens disina tasar (Quest FOV dersi geri gelir).
            float dist = HeadOverlay.Distance(HeadOverlay.Vignette);
            transform.SetPositionAndRotation(head.position + head.forward * dist, head.rotation);
            float s = 2f * (dist / 0.52f);
            _quad.localScale = new Vector3(s, s, 1f);

            // Can esikten sifira indikce siddet 0 -> 1 arasi artar.
            float severity = 1f - _ratio / threshold;
            float pulseSpeed = Mathf.Lerp(2.5f, 6f, severity); // can azaldikca nabiz hizlanir
            float pulse = 1f + pulseAmount * Mathf.Sin(Time.time * pulseSpeed);
            float a = Mathf.Clamp01(severity * maxAlpha * pulse);
            UITheme.SetMaterialColor(_mat, new Color(VignetteColor.r, VignetteColor.g, VignetteColor.b, a));
        }

    }
}
