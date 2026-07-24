using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Kafanin onunde duran dunya-uzayi bilgi paneli. Ayni "kafa yonunu duzle + 1.4 m one
    /// koy + kafaya dondur" kodu 4 dosyada (LanBootstrap, TeamSelector, CalibrationManager,
    /// RoomScanSync) kopyaliydi; panel kurulum satirlari da oyle. Takip LateUpdate'te —
    /// obje inaktifken calismaz, yani eski "activeSelf" kontrolleriyle ayni davranis.
    /// </summary>
    public class HeadFollowPanel : MonoBehaviour
    {
        [Tooltip("Panelin kafadan uzakligi (metre).")]
        public float distance = 1.4f;

        void LateUpdate()
        {
            var head = XRRigReference.HeadOrCamera;
            if (head == null) return;
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            fwd.Normalize();
            transform.position = head.position + fwd * distance;
            transform.rotation = Quaternion.LookRotation(fwd);
        }

        /// <summary>Standart panel fabrikasi: TextMesh ayarlari (0.16 olcek, 0.1 karakter,
        /// 60 punto, ortali) tek yerde. Donen TextMesh'in text/renk alanlari sonradan
        /// degistirilebilir; takip bileseni otomatik takilidir.</summary>
        public static TextMesh Create(string name, string text, Color color)
        {
            var go = new GameObject(name);
            go.transform.localScale = Vector3.one * 0.16f;
            var tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.characterSize = 0.1f;
            tm.fontSize = 60;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.color = color;
            go.AddComponent<HeadFollowPanel>();
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
