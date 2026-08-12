using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer
{
    /// <summary>
    /// Lives on the networked player avatar (root with NetworkObject). On the OWNING client it
    /// copies the local XR rig's head + hand poses onto this prefab's networked child transforms
    /// every frame. Owner-authoritative <see cref="ClientNetworkTransform"/> components on those
    /// children replicate the motion to everyone; remote clients see it interpolated.
    ///
    /// Gorunurluk modeli — SAHIP ve UZAK OYUNCU ayri mekanizmadan beslenir, cunku
    /// gereksinimleri birbirine zit:
    ///  - SAHIP: avatarin govdesi tamamen kapali. Kendi elini
    ///    <see cref="FirstPersonHandView"/>'den gorur; el kumanda tasiyicisina rijit
    ///    bagli oldugu icin kumanda nerede olursa olsun (2 m otede bile) el TAM
    ///    orasidir. Kol IK'si bu garantiyi veremez, kol ancak boyu kadar uzanir.
    ///  - UZAK OYUNCU: tam humanoid avatar (<see cref="AvatarIKController"/>). Orada
    ///    kural terstir: kol uzayamaz, sinirina gelince dumduz kalip hedefe dogru
    ///    bakar (<see cref="ArmReach"/>).
    /// </summary>
    // Must write the pose carriers BEFORE AvatarIKController (order 0) reads them in LateUpdate;
    // with both at the default order Unity gives no guarantee, and losing the race adds a
    // one-frame head/hand mismatch that reads as hand jitter.
    [DefaultExecutionOrder(-100)]
    public class NetworkVRPlayer : NetworkBehaviour
    {
        [Header("Networked pose carriers (this prefab's own children)")]
        [SerializeField] Transform head;
        [SerializeField] Transform leftHand;
        [SerializeField] Transform rightHand;

        [Header("Visibility")]
        [Tooltip("Renderers OFF for the local owner, ON for others (e.g. head when there is no humanoid).")]
        [SerializeField] Renderer[] ownerHiddenRenderers;
        [Tooltip("Renderers ON only for the local owner (e.g. the simple hand cubes).")]
        [SerializeField] Renderer[] ownerOnlyRenderers;
        [Tooltip("Renderers OFF for everyone (pose-carrier meshes hidden once a humanoid exists).")]
        [SerializeField] Renderer[] alwaysHiddenRenderers;
        [Tooltip("The humanoid avatar — shown to everyone, including you (first-person body).")]
        [SerializeField] GameObject remoteAvatar;

        // Hand analog inputs, owner-written and replicated to everyone, so ProceduralFingerPoser
        // can curl each player's fingers on ALL clients. byte = 0..255 quantized grip/trigger;
        // one byte each, cheap. Owner writes directly (no RPC — the player owns this object).
        readonly NetworkVariable<byte> _leftGrip = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _rightGrip = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _leftTrig = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
        readonly NetworkVariable<byte> _rightTrig = new NetworkVariable<byte>(
            0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        public float LeftGrip01 => _leftGrip.Value / 255f;
        public float RightGrip01 => _rightGrip.Value / 255f;
        public float LeftTrigger01 => _leftTrig.Value / 255f;
        public float RightTrigger01 => _rightTrig.Value / 255f;

        Transform _srcHead, _srcLeft, _srcRight;
        bool _bound;

        public override void OnNetworkSpawn()
        {
            ApplyVisibility();

            if (!IsOwner)
                return;

            var rig = XRRigReference.Instance;
            if (rig == null)
            {
                Debug.LogWarning("[NetworkVRPlayer] No XRRigReference in scene — avatar will not follow the headset.");
                return;
            }

            _srcHead = rig.head;
            _srcLeft = rig.leftHand;
            _srcRight = rig.rightHand;
            _bound = _srcHead != null && head != null;
        }

        void ApplyVisibility()
        {
            // The primitive Head/Hand carriers are invisible pose sources — hide their meshes.
            HideRenderers(head);
            HideRenderers(leftHand);
            HideRenderers(rightHand);

            // Everyone — including you — sees the full humanoid (first-person embodiment).
            if (remoteAvatar != null)
            {
                remoteAvatar.SetActive(true);

                // IK can pull the hands far outside each piece's authored bounds (arms raised
                // overhead). With stale bounds Unity frustum-culls the piece while it is plainly
                // on screen — hands vanish when lifted. Live bounds keep them visible.
                foreach (var r in remoteAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    r.updateWhenOffscreen = true;

                if (IsOwner)
                {
                    // BIRINCI SAHIS ELI ARTIK AVATAR ISKELETINDEN GELMIYOR.
                    // Kumanda neredeyse el ORADA olmak zorunda; kol IK'si bunu
                    // yapisal olarak veremez (kol ancak boyu kadar uzanir). O yuzden
                    // sahibin gordugu el dogrudan kumanda tasiyicisina parent'lanir
                    // (FirstPersonHandView) ve avatarin kendi govdesi tamamen kapatilir.
                    // Uzak oyuncular tam askeri gormeye devam eder; orada kural terstir,
                    // kol uzayamaz ve sinirinda dumduz kalir (ArmReach).
                    foreach (var r in remoteAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        r.enabled = false;

                    // Kendi isim etiketini de gorme.
                    foreach (var tm in remoteAvatar.GetComponentsInChildren<TextMesh>(true))
                    {
                        var tr = tm.GetComponent<MeshRenderer>();
                        if (tr != null) tr.enabled = false;
                    }

                    // Tasiyicilarin kendi mesh'leri yukarida HideRenderers ile zaten
                    // kapatildi; el gorseli ondan SONRA kuruluyor ki kapanmasin.
                    FirstPersonHandView.Attach(leftHand, rightHand, remoteAvatar);
                }
            }
        }

        static void HideRenderers(Transform t)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>())
                r.enabled = false;
        }

        // SpreadSpawn KALDIRILDI: oyuncular ust uste dogmasin diye rig'i client id'ye gore bir
        // cember uzerinde kaydiriyordu. Kolokasyonda bu YANLIS — rig'i koddan oynatmak sanal
        // konumu fiziksel konumdan ayirir ve oyuncu gercek odada baskasinin icine yurur.
        // Oyuncular artik <see cref="TeamSpawnZone"/> cemberine FIZIKSEL olarak yuruyerek
        // girer; dagilimi gercek oda saglar.
        // NOT: origin/main'deki birikimli-ofset duzeltmesi (009bd56) bilerek ALINMADI —
        // duzelttigi kod yolu bu dalda tamamen kaldirildi.

        void LateUpdate()
        {
            if (!_bound)
                return;

            head.SetPositionAndRotation(_srcHead.position, _srcHead.rotation);
            if (_srcLeft != null && leftHand != null)
                leftHand.SetPositionAndRotation(_srcLeft.position, _srcLeft.rotation);
            if (_srcRight != null && rightHand != null)
                rightHand.SetPositionAndRotation(_srcRight.position, _srcRight.rotation);

            // Publish the analog grip/trigger for both hands so everyone's finger poser matches.
            WriteInput(XRNode.LeftHand, _leftGrip, _leftTrig);
            WriteInput(XRNode.RightHand, _rightGrip, _rightTrig);
        }

        static void WriteInput(XRNode node, NetworkVariable<byte> grip, NetworkVariable<byte> trig)
        {
            var dev = InputDevices.GetDeviceAtXRNode(node);
            if (!dev.isValid) return;

            float g = XRButtons.Axis01WithButtonFallback(dev, CommonUsages.grip, CommonUsages.gripButton);
            float t = XRButtons.Axis01WithButtonFallback(dev, CommonUsages.trigger, CommonUsages.triggerButton);

            byte gb2 = (byte)Mathf.RoundToInt(Mathf.Clamp01(g) * 255f);
            byte tb2 = (byte)Mathf.RoundToInt(Mathf.Clamp01(t) * 255f);
            // Deadband: only replicate when it actually moves, to avoid per-frame dirtying.
            if (Mathf.Abs(gb2 - grip.Value) > 3) grip.Value = gb2;
            if (Mathf.Abs(tb2 - trig.Value) > 3) trig.Value = tb2;
        }
    }
}
