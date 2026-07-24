using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Bomba elindeyken thumbstick'i itince cikan KIRMIZI nisan yayi — Quest'in isinlanma
    /// yayinin bomba versiyonu. Yay, bombayi tutan KUMANDANIN dogrultusundan sabit nisan
    /// hiziyla (config: aimArcSpeed x throwVelocityScale) balistik yorunge cizer; ilk
    /// carptigi yerde kirmizi inis halkasi gosterir. Yalniz sahibinde (yerel oyuncu)
    /// gorunur; firlatma yine gercek el savurmasiyla yapilir, yay sadece nisan yardimi.
    /// GrenadeController.Bind runtime'da takar — sahne/prefab duzenlemesi SIFIR.
    /// </summary>
    public class GrenadeAimArc : MonoBehaviour
    {
        const int MaxSteps = 60;
        const float StepDt = 0.04f;
        const float StickDeadzone = 0.35f;
        static readonly Color ArcColor = new Color(1f, 0.15f, 0.1f, 0.9f);

        GrenadeConfig _cfg;
        GrabbableObject _grab;
        LineRenderer _arc;
        LineRenderer _ring;
        Material _mat;
        readonly Vector3[] _pts = new Vector3[MaxSteps + 1];

        public void Init(GrenadeConfig cfg, GrabbableObject grab)
        {
            _cfg = cfg;
            _grab = grab;
        }

        void EnsureRenderers()
        {
            if (_arc != null) return;
            // Sprites/Default her pipeline'da calisir; vertex rengiyle boyanir.
            _mat = new Material(Shader.Find("Sprites/Default"));

            _arc = NewLine("~NisanYayi", 0.018f, 0.008f, false);
            _ring = NewLine("~InisHalkasi", 0.014f, 0.014f, true);
            _ring.positionCount = 17;
        }

        LineRenderer NewLine(string name, float w0, float w1, bool loop)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var lr = go.AddComponent<LineRenderer>();
            lr.material = _mat;
            lr.startColor = lr.endColor = ArcColor;
            lr.startWidth = w0; lr.endWidth = w1;
            lr.loop = loop;
            lr.useWorldSpace = true;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.enabled = false;
            return lr;
        }

        void LateUpdate()
        {
            bool show = false;
            if (_cfg != null && _grab != null && _grab.IsHeld && _grab.IsOwner &&
                NetIsLocalHolder())
            {
                XRNode node = _grab.HolderHand == 0 ? XRNode.LeftHand : XRNode.RightHand;
                var dev = InputDevices.GetDeviceAtXRNode(node);
                if (dev.isValid &&
                    dev.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick) &&
                    stick.magnitude > StickDeadzone)
                {
                    show = TryDrawArc();
                }
#if UNITY_EDITOR
                // Editor testinde (kumandasiz) bomba eldeyken yay hep gorunur.
                if (!dev.isValid) show = TryDrawArc();
#endif
            }

            if (_arc != null && _arc.enabled != show)
            {
                _arc.enabled = show;
                _ring.enabled = show && _ring.positionCount > 0;
            }
            if (_ring != null && _ring.enabled != (show && _hasLanding))
                _ring.enabled = show && _hasLanding;
        }

        bool _hasLanding;

        bool NetIsLocalHolder()
        {
            var nm = Unity.Netcode.NetworkManager.Singleton;
            return nm == null || _grab.HolderClientId == nm.LocalClientId;
        }

        bool TryDrawArc()
        {
            EnsureRenderers();

            // Yon: bombayi tutan kumandanin dogrultusu (rig referansindan); rig yoksa
            // (editor testi) kamera dogrultusu + hafif yukari.
            Transform hand = null;
            var rig = XRRigReference.Instance;
            if (rig != null)
                hand = _grab.HolderHand == 0 ? rig.leftHand : rig.rightHand;
            Vector3 origin = transform.position;
            Vector3 dir;
            if (hand != null) dir = hand.forward;
            else if (Camera.main != null) dir = (Camera.main.transform.forward + Vector3.up * 0.35f).normalized;
            else return false;

            float speed = Mathf.Max(1f, _cfg.aimArcSpeed * _cfg.throwVelocityScale);
            Vector3 v = dir * speed;
            Vector3 p = origin;
            int n = 0;
            _hasLanding = false;
            _pts[n++] = p;

            for (int i = 0; i < MaxSteps; i++)
            {
                Vector3 next = p + v * StepDt;
                v += Physics.gravity * StepDt;
                if (Physics.Linecast(p, next, out RaycastHit hit, ~0, QueryTriggerInteraction.Ignore) &&
                    !hit.collider.transform.IsChildOf(transform.root))
                {
                    _pts[n++] = hit.point;
                    DrawRing(hit.point + hit.normal * 0.02f, hit.normal);
                    _hasLanding = true;
                    break;
                }
                _pts[n++] = next;
                p = next;
            }

            _arc.positionCount = n;
            _arc.SetPositions(_pts);
            return true;
        }

        void DrawRing(Vector3 center, Vector3 normal)
        {
            const float R = 0.25f;
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, normal);
            for (int i = 0; i <= 16; i++)
            {
                float a = i / 16f * Mathf.PI * 2f;
                _ring.SetPosition(i, center + rot * new Vector3(Mathf.Cos(a) * R, 0f, Mathf.Sin(a) * R));
            }
        }

        void OnDestroy()
        {
            if (_mat != null) Destroy(_mat);
        }
    }
}
