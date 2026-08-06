using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Kafanin onunde duran dunya-uzayi bilgi paneli. Ayni "kafa yonunu duzle + 1.4 m one
    /// koy + kafaya dondur" kodu 4 dosyada (LanBootstrap, TeamSelector, CalibrationManager,
    /// RoomScanSync) kopyaliydi; panel kurulum satirlari da oyle. Takip LateUpdate'te —
    /// obje inaktifken calismaz, yani eski "activeSelf" kontrolleriyle ayni davranis.
    ///
    /// IKI TAKIP KIPI VAR, bkz. <see cref="lazy"/>. Kisa omurlu adim panelleri sert takipte
    /// kalabilir; EKRANDA UZUN KALAN her panel tembel olmali.
    /// </summary>
    public class HeadFollowPanel : MonoBehaviour
    {
        [Tooltip("Panelin kafadan uzakligi (metre).")]
        public float distance = 1.4f;

        [Tooltip("Goz hizasina gore dikey kaydirma (metre). Surekli acik kalan paneller " +
                 "(or. insa modu durumu) asagi alinmali — goz hizasinda dururlarsa oyuncunun " +
                 "tam bakmak istedigi yeri kapatirlar.")]
        public float heightOffset = 0f;

        [Tooltip("TEMBEL TAKIP: panel dunyaya sabit durur ve ancak kafa cok donunce onune " +
                 "yumusakca geri kayar.")]
        public bool lazy;

        /// <summary>Panel bu acidan fazla yana kalirsa onune geri getirilir. Menulerdeki
        /// (ModeSelectUI / PlayerEntryUI) esikle AYNI — iki farkli his olmasin.</summary>
        const float RecenterAngle = 38f;
        const float RecenterSpeed = 3.5f;

        bool _placed, _recentering;

        void LateUpdate()
        {
            var head = XRRigReference.HeadOrCamera;
            if (head == null) return;
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();

            Vector3 targetPos = head.position + fwd * distance + Vector3.up * heightOffset;
            Quaternion targetRot = Quaternion.LookRotation(fwd);

            // Sert takip (varsayilan) ve ILK yerlestirme: dogrudan otur. Ilk kare tembel
            // olamaz — panel origin'den suzulerek gelirdi.
            if (!lazy || !_placed)
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
                _placed = true;
                _recentering = false;
                return;
            }

            if (!_recentering)
            {
                Vector3 toPanel = transform.position - head.position; toPanel.y = 0f;
                if (toPanel.sqrMagnitude < 0.0001f) return;
                if (Vector3.Angle(fwd, toPanel.normalized) < RecenterAngle) return;   // yerinde kalsin
                _recentering = true;
            }

            float k = 1f - Mathf.Exp(-RecenterSpeed * Time.unscaledDeltaTime);
            transform.SetPositionAndRotation(
                Vector3.Lerp(transform.position, targetPos, k),
                Quaternion.Slerp(transform.rotation, targetRot, k));

            if ((transform.position - targetPos).sqrMagnitude < 0.0004f) _recentering = false;
        }

        /// <summary>Standart panel fabrikasi: TextMesh ayarlari (0.16 olcek, 0.1 karakter,
        /// 60 punto, ortali) tek yerde. Donen TextMesh'in text/renk alanlari sonradan
        /// degistirilebilir; takip bileseni otomatik takilidir.
        ///
        /// <paramref name="lazy"/> icin bkz. <see cref="lazy"/>: BEKLETEN her panelde true
        /// verilmeli. Varsayilan false, cunku adim adim ilerleyen kisa paneller (kalibrasyon)
        /// bugunku sert takiple ayarlandi.</summary>
        public static TextMesh Create(string name, string text, Color color, bool lazy = false)
        {
            var go = new GameObject(name);
            go.transform.localScale = Vector3.one * 0.16f;
            var tm = go.AddComponent<TextMesh>();
            // FONT SART: Unity 6'da varsayilan TextMesh fontu yok. Bu satir olmadan bu
            // fabrikanin urettigi HER panel (katilim, takim secme, kalibrasyon, oda tarama,
            // avatar fit) editorde gorunur ama Quest build'inde bombos cikar.
            UITheme.ApplyFont(tm);
            tm.text = text;
            tm.characterSize = 0.1f;
            tm.fontSize = 60;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            go.AddComponent<HeadFollowPanel>().lazy = lazy;
            return tm;
        }

        /// <summary>Var olan bir panele (or. sahnede serilestirilmis kalibrasyon paneli)
        /// takip bilesenini bir kez ekler.</summary>
        public static void Attach(Component panel)
        {
            if (panel != null && panel.GetComponent<HeadFollowPanel>() == null)
                panel.gameObject.AddComponent<HeadFollowPanel>();
        }
    }
}
