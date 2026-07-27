using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Diagnostic read-out for the avatar height fit. Shows, in front of your face, the values
    /// that used to drift silently: the calibrated standing height, the locked scale, the crouch
    /// blend and the root height. The point of the fix is that all four stand still once the fit
    /// locks — if any of them keeps moving while you stand still, the fit is still wrong.
    ///
    /// Add it next to <see cref="AvatarIKController"/> on the avatar and tick
    /// <see cref="showOverlay"/>. It is deliberately OFF by default and returns immediately when
    /// off, so it can sit on the prefab without costing anything in a normal build.
    /// </summary>
    [RequireComponent(typeof(AvatarIKController))]
    public class AvatarFitDebug : MonoBehaviour
    {
        [Tooltip("Show the read-out. Can be ticked live in Play mode; tick it before building to read it on the headset.")]
        public bool showOverlay;
        [Tooltip("Refresh interval (s). The panel is for reading, not for per-frame precision.")]
        public float refreshInterval = 0.15f;

        AvatarIKController _ik;
        TextMesh _panel;
        float _nextRefresh;

        void Awake() => _ik = GetComponent<AvatarIKController>();

        void LateUpdate()
        {
            if (!showOverlay)
            {
                if (_panel != null) { Destroy(_panel.gameObject); _panel = null; }
                return;
            }

            // Every client simulates every avatar, so an un-owned one would stack its panel on
            // top of yours in the same spot. Only the avatar you are wearing reports.
            var netObj = GetComponentInParent<Unity.Netcode.NetworkObject>();
            if (netObj != null && !netObj.IsOwner)
                return;

            if (_panel == null)
                _panel = UI.HeadFollowPanel.Create("Avatar Fit Debug", "", Color.cyan);

            if (Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + refreshInterval;

            float headY = _ik.headSource != null ? _ik.headSource.position.y : 0f;
            _panel.color = _ik.FitLocked ? Color.cyan : Color.yellow;
            _panel.text =
                (_ik.FitLocked ? "FIT: KILITLI" : "FIT: olculuyor...") + "\n" +
                $"boy     {_ik.StandingHeight:F3} m\n" +
                $"olcek   {_ik.FitScale:F4}\n" +
                $"comelme {_ik.CrouchWeight:F2}\n" +
                $"kok Y   {transform.position.y:F3}\n" +
                $"kafa Y  {headY:F3}";
        }

        void OnDisable()
        {
            if (_panel != null) { Destroy(_panel.gameObject); _panel = null; }
        }
    }
}
