using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// A networked object that can be picked up. It stays FROZEN (kinematic) while resting on
    /// the ground or held, so it can never fall through the world; physics only turns on for the
    /// brief arc of a throw, then it re-freezes where it lands. The server arbitrates who holds
    /// it (first grab wins) and transfers NGO ownership to the grabber, whose owner-authoritative
    /// ClientNetworkTransform replicates the motion to everyone.
    ///
    /// Needs on the same object: NetworkObject, ClientNetworkTransform, Rigidbody and a collider.
    /// Use Tools > VR Multiplayer > 10 to set these up.
    /// </summary>
    public class GrabbableObject : NetworkBehaviour
    {
        public const ulong NoHolder = ulong.MaxValue;
        public const byte NoHand = byte.MaxValue;

        [Tooltip("Tutunca obje avucun icine cekilir (silah gibi). Kapaliysa yakalandigi mesafe/acida kalir (tas gibi).")]
        public bool snapToHand = true;

        [Tooltip("Elde tutus acisi duzeltmesi (derece). Sifir birakilirsa namlu ekseni otomatik bulunur ve bakilan yone hizalanir.")]
        public Vector3 gripRotationEuler;

        readonly NetworkVariable<ulong> _holder = new NetworkVariable<ulong>(NoHolder);
        readonly NetworkVariable<byte> _holderHand = new NetworkVariable<byte>(0); // 0=L 1=R

        // Two-handed carry: which of the holder's hands is clamped on the handguard as SUPPORT
        // (0=L, 1=R, NoHand=none). Owner-written like the weapon's transform itself; consumers
        // must gate on IsHeld — after a disconnect the server cannot clear an owner-permission
        // variable, so a stale value may linger on an unheld weapon.
        readonly NetworkVariable<byte> _supportHand = new NetworkVariable<byte>(NoHand,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        Rigidbody _rb;
        Vector3 _homePos;
        Quaternion _homeRot;
        bool _flying; // true only during a thrown arc (owner side)

        public ulong HolderClientId => _holder.Value;
        public byte HolderHand => _holderHand.Value;
        public bool IsHeld => _holder.Value != NoHolder;
        public byte SupportHand => _supportHand.Value;

        /// <summary>Raised whenever holder / holder-hand / support-hand state replicates —
        /// possibly several times in one frame and in any delta order. Consumers should only
        /// set a dirty flag here and evaluate once in LateUpdate.</summary>
        public event System.Action StateDirty;

        /// <summary>Fired on every client when a grabbable finishes its network spawn. The
        /// weapon-grip binder attaches per-weapon visual components through this hook, so
        /// prefabs and scene objects need no manual component edits.</summary>
        public static event System.Action<GrabbableObject> AnySpawned;

        /// <summary>Owner-side: publish which hand steadies the weapon (0=L, 1=R, NoHand).</summary>
        public void SetSupportHandOwner(byte hand)
        {
            if (IsOwner && _supportHand.Value != hand)
                _supportHand.Value = hand;
        }

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _homePos = transform.position;
            _homeRot = transform.rotation;
            if (_rb != null)
            {
                // Frozen from frame zero — it never falls while resting, even before the network
                // spawns or on a machine that doesn't own it. Only a throw turns physics on.
                _rb.isKinematic = true;
                _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic; // anti-tunnel
                _rb.interpolation = RigidbodyInterpolation.Interpolate;
            }
        }

        public override void OnNetworkSpawn()
        {
            _holder.OnValueChanged += OnHolderChanged;
            _holder.OnValueChanged += OnStateChanged;
            _holderHand.OnValueChanged += OnStateChanged;
            _supportHand.OnValueChanged += OnStateChanged;
            if (IsServer)
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
            AnySpawned?.Invoke(this);
        }

        public override void OnNetworkDespawn()
        {
            _holder.OnValueChanged -= OnHolderChanged;
            _holder.OnValueChanged -= OnStateChanged;
            _holderHand.OnValueChanged -= OnStateChanged;
            _supportHand.OnValueChanged -= OnStateChanged;
            if (IsServer && NetworkManager != null)
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        void OnStateChanged(ulong _, ulong __) => StateDirty?.Invoke();
        void OnStateChanged(byte _, byte __) => StateDirty?.Invoke();

        void OnClientDisconnected(ulong clientId)
        {
            if (_holder.Value == clientId)
                _holder.Value = NoHolder; // dropped where it was
        }

        void OnHolderChanged(ulong _, ulong now)
        {
            if (_rb == null || !IsOwner) return;
            // Grabbed OR released-without-throw -> frozen. While held, gravity must never
            // run, no matter what any other component thinks.
            if (now != NoHolder || !_flying)
                _rb.isKinematic = true;
        }

        void FixedUpdate()
        {
            if (!IsOwner || _rb == null) return;

            // Escaped the world -> bring it back to where it started.
            if (transform.position.y < _homePos.y - 20f)
            {
                _flying = false;
                _rb.isKinematic = true;
                _rb.position = _homePos + Vector3.up * 0.2f;
                transform.SetPositionAndRotation(_homePos + Vector3.up * 0.2f, _homeRot);
                return;
            }

            if (IsHeld)
            {
                // While in a hand: physics stays OFF, period. Self-heals every physics step
                // in case another component (e.g. NetworkRigidbody on an ownership change)
                // flips the body back to dynamic and gravity yanks it out of the hand.
                _flying = false;
                if (!_rb.isKinematic) _rb.isKinematic = true;
                return;
            }

            if (_flying)
            {
                // Thrown arc came to rest -> freeze so it stays put on the ground.
                if (!_rb.isKinematic &&
                    _rb.linearVelocity.sqrMagnitude < 0.03f &&
                    _rb.angularVelocity.sqrMagnitude < 0.03f)
                {
                    _rb.isKinematic = true;
                    _flying = false;
                }
            }
            else if (!_rb.isKinematic)
            {
                // Nothing should be simulating it -> enforce frozen rest.
                _rb.isKinematic = true;
            }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestGrabServerRpc(byte hand, RpcParams p = default)
        {
            if (_holder.Value != NoHolder) return; // someone already holds it
            ulong sender = p.Receive.SenderClientId;
            _holderHand.Value = hand;
            _holder.Value = sender;
            NetworkObject.ChangeOwnership(sender);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReleaseServerRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != _holder.Value) return;
            _holder.Value = NoHolder;
        }

        /// <summary>Owner-side: on release, either drop-in-place or throw with the hand's velocity.</summary>
        public void ApplyThrow(Vector3 velocity, Vector3 angularVelocity)
        {
            if (_rb == null || !IsOwner) return;

            if (velocity.magnitude < 0.6f)
            {
                // Gentle release: let it settle straight down onto the ground, then re-freeze.
                _flying = true;
                _rb.isKinematic = false;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
            else
            {
                _flying = true;
                _rb.isKinematic = false;
                _rb.linearVelocity = velocity;
                _rb.angularVelocity = angularVelocity;
            }
        }
    }
}
