using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;
using VRMultiplayer.Weapons;

namespace VRMultiplayer
{
    /// <summary>
    /// Lets the LOCAL player grab <see cref="GrabbableObject"/>s with the GRIP button of either
    /// controller: squeeze near an object to pick it up, release to drop/throw it (hand velocity
    /// is applied, so you can toss objects). A second hand squeezing near a held weapon becomes
    /// the SUPPORT hand: the weapon aims along the line between the two hands (ready stance).
    /// Lives on the NetworkPlayer root; uses the networked hand children so what you hold
    /// matches what others see in your avatar's hand.
    /// </summary>
    [DefaultExecutionOrder(10)] // NetworkVRPlayer (0) writes the hand carriers; read them same-frame
    public class HandGrabber : NetworkBehaviour
    {
        [SerializeField] Transform leftHand;
        [SerializeField] Transform rightHand;

        [Tooltip("Grab reach around the hand, in meters.")]
        public float grabRadius = 0.3f;

        class HandState
        {
            public Transform anchor;
            public XRNode node;
            public byte index;              // 0 = left, 1 = right
            public bool prevGrip;
            public GrabbableObject held;
            public GrabbableObject supporting; // two-hand aim: this hand steadies the OTHER hand's weapon
            public WeaponGrip supportGrip;     // cached profile component of `supporting` (null = legacy)
            public Collider[] supportCols;     // supporting'in collider'lari (engage aninda cache — tutus boyunca degismezler)
            public float nextSupportCheck;     // birakma-mesafesi kontrolunun bir sonraki calisma zamani (~10 Hz)
            public WeaponGrip grip;            // cached profile component of `held` (null = legacy path)
            public Vector3 aimDir;             // filtered two-hand aim direction (zero = not engaged)
            public float supportSince;         // time the support grip engaged (grace before auto-release)
            public float requestedAt;       // when the grab RPC was sent
            public bool confirmed;          // server confirmed WE hold it
            public Vector3 posOffset;       // grab-moment offset, hand-local
            public Quaternion rotOffset;
            public float blendUntil;        // pose-blend window end (0 = not blending)
            public Vector3 blendFromPos;    // weapon pose captured at the discontinuity
            public Quaternion blendFromRot;
            public bool legacyAiming;       // legacy path: was two-hand aim engaged last frame?
            public GrabbableObject pinFrom; // bu el OTEKI eldeki bombanin pimini tutuyor
            public readonly Queue<(Vector3 pos, float t)> trail = new Queue<(Vector3, float)>();
        }

        HandState _left, _right;

        HandState Other(HandState h) => h == _left ? _right : _left;

        // Compound-collider weapons (GunPhysicsSetup builds one box per region) need the NEAREST
        // part, not GetComponentInChildren's first hit — that could be the magazine or stock while
        // the support hand reaches for the handguard, making two-handed hold impossible on some
        // weapons and fine on others purely by child order.
        // Returns -1 when the weapon has no usable collider.
        static float NearestColliderDistance(GrabbableObject weapon, Vector3 point, bool useBounds)
            => NearestColliderDistance(weapon.GetComponentsInChildren<Collider>(), point, useBounds);

        // Asil govde cache'lenmis diziyle calisir: destek eli tutarken kosan birakma kontrolu
        // her cagride GetComponentsInChildren ile heap alloc yapmasin (iki elle nisan Quest'te
        // varsayilan catisma durusu — kare basi alloc surekli GC baskisiydi). Silah despawn
        // olursa dizideki collider'lar olebilir; null elemanlar atlanir.
        static float NearestColliderDistance(Collider[] cols, Vector3 point, bool useBounds)
        {
            float best = -1f;
            foreach (var c in cols)
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                // ClosestPoint throws on a non-convex MeshCollider — fall back to its bounds.
                var mesh = c as MeshCollider;
                bool canClosestPoint = mesh == null || mesh.convex;
                Vector3 p = (useBounds || !canClosestPoint)
                    ? c.ClosestPointOnBounds(point)
                    : c.ClosestPoint(point);
                float d = Vector3.Distance(point, p);
                if (best < 0f || d < best) best = d;
            }
            return best;
        }

        /// <summary>True while that hand is holding a grabbable (used by the finger poser to
        /// firm up the grip). Non-owner instances never run grab logic, so these stay false —
        /// the finger poser falls back to the networked grip value, which is what we want.</summary>
        public bool HoldingLeft => _left != null && _left.held != null;
        public bool HoldingRight => _right != null && _right.held != null;

        /// <summary>Bu elin tuttugu obje, yoksa null. Kol saati ekrani gibi tuketiciler
        /// tutulan silaha (ve mermisine) buradan ulasir.</summary>
        public GrabbableObject HeldLeft => _left != null ? _left.held : null;
        public GrabbableObject HeldRight => _right != null ? _right.held : null;

        /// <summary>Networked hand anchor transforms (read-only; the weapon-grip system solves
        /// the held weapon's pose from these).</summary>
        public Transform LeftAnchor => leftHand;
        public Transform RightAnchor => rightHand;

        // ─── Silah secici kancasi (VRMultiplayer.UI.WeaponSelectorUI) ────────────────────
        // Galeriden secilen silah ele BURADAN girer. Yakinlik ile kapma yolu (TryGrab) hic
        // degismedi — iki yol da ayni Adopt() govdesini kullanir.

        /// <summary>Owner-side: trade <paramref name="current"/> (may be null) for a fresh instance
        /// of <paramref name="prefab"/>. The old one is DESPAWNED — that is the "goes into the bag"
        /// disappearance — and the server spawns the new one straight into this hand. Weapons are
        /// unlimited at the rack, so a TYPE is all the bag needs to remember; no per-object
        /// ownership bookkeeping is required.
        ///
        /// The caller passes <paramref name="current"/> rather than us reading a hand, because the
        /// PC test harness carries weapons with this component switched off — the server's holder
        /// value is the only truth both paths agree on. Silent no-op off-owner / unregistered
        /// prefab.</summary>
        public void RequestWeaponSwap(GrabbableObject current, GameObject prefab,
                                      int ammo = -1, int spares = -1)
        {
            if (!IsOwner) return;

            // Pimi cekilmis bomba cantaya GERI KONAMAZ. Bu blok olmasa silah carkindan baska bir
            // sey secmek canli bombayi despawn edip "sondururdu" — pim sisteminin butun anlamini
            // bosa cikaran kacamak. Once firlatmak zorundasin.
            if (current != null)
            {
                var live = current.GetComponent<GrenadeController>();
                if (live != null && live.Armed) return;
            }

            // prefab == null -> SADECE yok et (birakma: "cantaya girdi", yerine bir sey gelmez).
            // Kimlik agda ISIM olarak gider (indeks degil) — bkz. WeaponPrefabRegistrar.FindByName.
            string key = "";
            if (prefab != null)
            {
                if (WeaponPrefabRegistrar.FindByName(prefab.name) != prefab)
                    return; // Resources/WeaponPrefabs disinda -> spawn edilemez
                key = prefab.name;
            }

            // Yenisi ESKISINI TUTAN ele gitmeli. Onceden kosulsuz "_right ?? _left" secildigi
            // icin sol eldeki silah takasta sag ele isiniyordu; sag el ayrica doluysa equip
            // atlanip oyuncu iki silahsiz kaliyordu. current yoksa (bos elle galeri secimi)
            // BOS bir el tercih edilir. Secim, asagidaki lokal birakma blogundan ONCE yapilmali
            // (blok held referanslarini temizler).
            HandState h = null;
            if (current != null)
            {
                if (_right != null && _right.held == current) h = _right;
                else if (_left != null && _left.held == current) h = _left;
            }
            if (h == null && _right != null && _right.held == null) h = _right;
            if (h == null && _left != null && _left.held == null) h = _left;
            if (h == null) h = _right ?? _left;
            if (h == null) return;

            // Let go LOCALLY first: Reconcile releases anything we hold that is in neither hand,
            // and the swap round trip takes ~1 RTT. Dropping the reference also stops UpdateHand
            // from posing a weapon that is about to be destroyed.
            if (_left != null && _left.held == current) { _left.held = null; _left.grip = null; _left.confirmed = false; }
            if (_right != null && _right.held == current) { _right.held = null; _right.grip = null; _right.confirmed = false; }
            if (_left != null && _left.supporting == current) { _left.supporting = null; _left.supportGrip = null; _left.supportCols = null; }
            if (_right != null && _right.supporting == current) { _right.supporting = null; _right.supportGrip = null; _right.supportCols = null; }
            // Pimi bu objeden cekilmis el varsa serbest kalsin: obje birazdan yok edilecek,
            // aksi halde o el "pim tutuyor" sanip bir daha hicbir sey kavrayamazdi.
            if (_left != null && _left.pinFrom == current) _left.pinFrom = null;
            if (_right != null && _right.pinFrom == current) _right.pinFrom = null;

            var oldRef = current != null && current.NetworkObject != null
                ? new NetworkObjectReference(current.NetworkObject)
                : default;
            SwapWeaponServerRpc(oldRef, key, h.index, ammo, spares);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void SwapWeaponServerRpc(NetworkObjectReference oldRef, string prefabName, byte hand,
                                 int ammo, int spares, RpcParams p = default)
        {
            ulong sender = p.Receive.SenderClientId;

            // Despawn the old one — but only if the sender really held it (never destroy someone
            // else's weapon on a stale/forged reference).
            if (oldRef.TryGet(out var oldNo) && oldNo != null && oldNo.IsSpawned)
            {
                var og = oldNo.GetComponent<GrabbableObject>();
                if (og != null && og.HolderClientId == sender) oldNo.Despawn(true);
            }

            // Isim anahtari sunucunun KENDI listesinde cozulur; bos isim = yalniz yok etme.
            var prefab = WeaponPrefabRegistrar.FindByName(prefabName);
            if (prefab == null) return;

            // Spawn at the requester's hand: these anchors are the networked hand carriers, so
            // the server already knows where that player's palm is.
            Transform anchor = hand == 1 ? rightHand : leftHand;
            Vector3 pos = anchor != null ? anchor.position : transform.position + Vector3.up;

            var go = Instantiate(prefab, pos, Quaternion.identity);
            var no = go.GetComponent<NetworkObject>();
            if (no == null) { Destroy(go); return; }
            no.SpawnWithOwnership(sender);

            // Silah cantaya kac mermiyle girdiyse o kadarla ciksin. Spawn'dan SONRA yaziyoruz:
            // NetworkWeapon.OnNetworkSpawn sarjoru doldurur, biz onun uzerine gercek degeri
            // koyariz. Yoksa galeriyi acip kapamak bedava sarjor olurdu.
            var nw = go.GetComponent<NetworkWeapon>();
            if (nw != null) nw.SetAmmoStateServer(ammo, spares);

            EquipSpawnedRpc(new NetworkObjectReference(no), hand,
                RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void EquipSpawnedRpc(NetworkObjectReference r, byte hand, RpcParams p)
        {
            if (!IsOwner) return;
            if (!r.TryGet(out var no) || no == null) return;
            var g = no.GetComponent<GrabbableObject>();

            var h = hand == 1 ? _right : _left;
            if (g == null || h == null || h.held != null)
            {
                // El bu arada dolmus (ya da hedef kullanilamaz) -> sunucunun spawn'ladigi silah
                // SAHIPSIZ kalmasin: iade et ki elde-degil silahlar sahnede birikmesin (her
                // biri ayrica cantadan dusulmus mermilerin bedava kopyasidir).
                CancelEquipServerRpc(r);
                return;
            }
            Adopt(h, g);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void CancelEquipServerRpc(NetworkObjectReference r, RpcParams p = default)
        {
            // Yalnizca GONDERENIN sahibi oldugu ve halen KIMSENIN tutmadigi spawn iptal edilir —
            // sahte/bayat referansla baskasinin silahi yok edilemez.
            if (!r.TryGet(out var no) || no == null || !no.IsSpawned) return;
            if (no.OwnerClientId != p.Receive.SenderClientId) return;
            var g = no.GetComponent<GrabbableObject>();
            if (g != null && g.IsHeld) return;
            no.Despawn(true);
        }

        public override void OnNetworkSpawn()
        {
            if (!IsOwner) { enabled = false; return; }
            _left = new HandState { anchor = leftHand, node = XRNode.LeftHand, index = 0 };
            _right = new HandState { anchor = rightHand, node = XRNode.RightHand, index = 1 };
        }

        void LateUpdate()
        {
            if (_left != null) UpdateHand(_left);
            if (_right != null) UpdateHand(_right);
            Reconcile();
        }

        void UpdateHand(HandState h)
        {
            if (h.anchor == null) return;

            // Velocity trail (last ~0.15 s) for throwing.
            float now = Time.time;
            h.trail.Enqueue((h.anchor.position, now));
            while (h.trail.Count > 2 && now - h.trail.Peek().t > 0.15f)
                h.trail.Dequeue();

            // Grip: prefer the boolean, but fall back to the analog axis — some OpenXR
            // interaction profiles only deliver the float. Slight hysteresis so a half
            // squeeze doesn't flicker between grab and release.
            var device = InputDevices.GetDeviceAtXRNode(h.node);
            bool grip = XRButtons.HeldWithAxisFallback(device, CommonUsages.gripButton,
                CommonUsages.grip, h.prevGrip ? 0.35f : 0.55f);

            if (grip && !h.prevGrip) TryGrab(h);
            else if (!grip && h.prevGrip) Release(h);
            h.prevGrip = grip;

            // Two-hand support is only valid while the other hand truly holds that object.
            if (h.supporting != null && h.supporting.HolderClientId != NetworkManager.LocalClientId)
            {
                h.supporting = null;
                h.supportGrip = null;
                h.supportCols = null;
            }

            // Profiled weapons: auto-release the support hand only when it truly LEAVES the
            // weapon. Measure against the weapon COLLIDERS (looser BOUNDS metric here, vs the
            // tighter surface metric used to grab — releasing should lag grabbing, not race it),
            // NOT the thin rail segment — the hand can sit validly beside the rail while still on
            // the weapon, and the two-hand aim rotates the weapon (moving a rail-relative point)
            // right after engage. A short grace period lets that aim settle before we judge.
            if (h.supporting != null && h.supportGrip != null && h.supportGrip.Profile != null)
            {
                var p = h.supportGrip.Profile;
                // ~10 Hz yeterli: birakma tespiti kare hassasiyeti istemez; boylece alloc'suz
                // kontrol de her kare degil saniyede 10 kez kosar.
                if (Time.time - h.supportSince > 0.4f && Time.time >= h.nextSupportCheck)
                {
                    h.nextSupportCheck = Time.time + 0.1f;
                    if (h.supportCols == null)
                        h.supportCols = h.supporting.GetComponentsInChildren<Collider>();
                    float nearest = NearestColliderDistance(h.supportCols, h.anchor.position, useBounds: true);
                    float d = nearest >= 0f ? nearest : 0f;
                    // Break threshold is at least the grab reach so grabbing can't instantly undo.
                    if (d > Mathf.Max(p.supportBreakDistance, grabRadius * 1.5f))
                    {
                        h.supporting = null;
                        h.supportGrip = null;
                        h.supportCols = null;
                    }
                }
            }

            // Follow. IMPORTANT: between our grab request and the server's reply (~1 RTT) the
            // object still LOOKS unheld — we must keep waiting, not give up instantly (the old
            // code dropped the local grab here every time, leaving objects ghost-held forever).
            if (h.held != null)
            {
                bool mine = h.held.HolderClientId == NetworkManager.LocalClientId
                            && h.held.HolderHand == h.index;
                if (mine && h.held.IsOwner)
                {
                    if (!h.confirmed)
                    {
                        h.confirmed = true;
                        // Confirm lands ~1 RTT after the squeeze with the weapon still at its
                        // rest pose — blend it into the hand instead of teleporting it.
                        StartPoseBlend(h);
                    }

                    if (h.grip != null && h.grip.Profile != null)
                    {
                        // Data-driven pose: authored grip anchor + filtered two-hand aim.
                        FollowProfiled(h, h.grip.Profile);
                    }
                    else
                    {
                        // Two-handed aim: grip hand anchors the weapon, and if the OTHER hand is
                        // clamped on it, the barrel points along the line between the two hands.
                        var sup = Other(h);
                        Vector3 aim = sup != null && sup.supporting == h.held
                            ? sup.anchor.position - h.anchor.position
                            : Vector3.zero;

                        // The legacy path has no aim filter, so engaging/releasing the support
                        // hand reorients the weapon in one step — re-blend across the switch.
                        bool aiming = aim.sqrMagnitude > 0.0025f;
                        if (aiming != h.legacyAiming)
                        {
                            h.legacyAiming = aiming;
                            StartPoseBlend(h);
                        }

                        ApplyWeaponPose(h,
                            h.anchor.TransformPoint(h.posOffset),
                            aiming
                                ? Quaternion.LookRotation(aim.normalized, h.anchor.up) * h.rotOffset
                                : h.anchor.rotation * h.rotOffset);
                    }
                }
                else if (h.confirmed && !h.held.IsHeld)
                {
                    h.held = null;             // server dropped it after we truly had it
                    h.confirmed = false;
                }
                else if (!h.confirmed && h.held.IsHeld && !mine)
                {
                    h.held = null;             // arbitration lost: someone else got it first
                }
                else if (!h.confirmed && Time.time - h.requestedAt > 1.5f)
                {
                    h.held = null;             // request lost in transit; reconcile will clean up
                }
            }
        }

        // Data-driven follow for profiled weapons: the weapon is posed so its authored grip
        // anchor sits exactly on the hand anchor (scale-safe), instead of pivot-snapping.
        // With a support hand the barrel follows the FILTERED two-hand line: rock solid inside
        // the deadzone, half-life smoothed outside, seeded from the one-hand barrel direction
        // so engaging support never pops. Roll stays 1:1 with the grip hand (up = hand up).
        void FollowProfiled(HandState h, WeaponGripProfile profile)
        {
            Vector3 gripLocal = profile.gripLocalPosition;
            Quaternion gripLocalRot = profile.GripLocalRotation;
            if (h.index == 0) // grip in the LEFT hand -> mirror the right-hand authored anchor
            {
                gripLocal = WeaponGripMath.MirrorX(gripLocal);
                gripLocalRot = WeaponGripMath.MirrorX(gripLocalRot);
            }

            var sup = Other(h);
            bool hasSupport = sup != null && sup.supporting == h.held;

            // Publish the support hand to everyone (owner-authoritative, like the transform).
            h.held.SetSupportHandOwner(hasSupport ? sup.index : GrabbableObject.NoHand);

            // Grip anchors are captured from the controller's REAL grip rake, so the barrel
            // is NOT the anchor's +Z — aim along the profile's weapon-local barrel axis.
            Vector3 barrelLocal = profile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? profile.barrelLocalDirection.normalized
                : Vector3.forward;

            Quaternion oneHandRot = h.anchor.rotation * Quaternion.Inverse(gripLocalRot);
            // The barrel direction the grip hand ALONE would produce — the two-hand aim only
            // REFINES this, it never swings the muzzle wildly (e.g. back toward the player).
            Vector3 oneHandBarrel = oneHandRot * barrelLocal;
            Quaternion weaponRot;
            if (hasSupport)
            {
                if (h.aimDir.sqrMagnitude < 1e-6f)
                    h.aimDir = oneHandBarrel; // engage seed = current one-hand barrel -> no pop

                Vector3 raw = sup.anchor.position - h.anchor.position;
                // Clamp the raw two-hand line to a cone around the one-hand barrel so a bad hand
                // placement can't rotate the weapon to face the shooter.
                const float maxDeviation = 45f;
                if (raw.sqrMagnitude > 1e-6f && Vector3.Angle(oneHandBarrel, raw) > maxDeviation)
                    raw = Vector3.RotateTowards(oneHandBarrel, raw.normalized, maxDeviation * Mathf.Deg2Rad, 0f);

                h.aimDir = WeaponGripMath.FilterAim(
                    h.aimDir, raw,
                    profile.aimDeadzoneDegrees, profile.aimSoftKneeDegrees,
                    profile.aimHalfLifeMs, Time.deltaTime);

                // Minimal rotation from the one-hand pose that puts the barrel on the aim line —
                // roll stays 1:1 with the grip hand, no up-vector guessing.
                weaponRot = Quaternion.FromToRotation(oneHandBarrel, h.aimDir) * oneHandRot;
            }
            else if (h.aimDir.sqrMagnitude > 1e-6f)
            {
                // Support just let go: keep filtering the aim back onto the one-hand barrel
                // (zero deadzone so it always converges) instead of snapping there in one frame.
                h.aimDir = WeaponGripMath.FilterAim(
                    h.aimDir, oneHandBarrel, 0f, 1f, profile.aimHalfLifeMs, Time.deltaTime);
                if (Vector3.Angle(h.aimDir, oneHandBarrel) < 0.5f)
                    h.aimDir = Vector3.zero; // converged; next engage re-seeds
                weaponRot = h.aimDir.sqrMagnitude > 1e-6f
                    ? Quaternion.FromToRotation(oneHandBarrel, h.aimDir) * oneHandRot
                    : oneHandRot;
            }
            else
                weaponRot = oneHandRot;

            Vector3 weaponPos = h.anchor.position
                - weaponRot * Vector3.Scale(h.held.transform.lossyScale, gripLocal);
            ApplyWeaponPose(h, weaponPos, weaponRot);
        }

        // Any would-be single-frame weapon-pose jump (the grab confirm arriving ~1 RTT after
        // the squeeze, the legacy path's support engage/disengage) is blended out over this
        // window instead of teleporting the weapon — and, through the wrist weld, the hand.
        const float PoseBlendSeconds = 0.12f;

        void StartPoseBlend(HandState h)
        {
            if (h.held == null) return;
            h.blendFromPos = h.held.transform.position;
            h.blendFromRot = h.held.transform.rotation;
            h.blendUntil = Time.time + PoseBlendSeconds;
        }

        void ApplyWeaponPose(HandState h, Vector3 pos, Quaternion rot)
        {
            if (Time.time < h.blendUntil)
            {
                float t = Mathf.SmoothStep(0f, 1f,
                    1f - (h.blendUntil - Time.time) / PoseBlendSeconds);
                pos = Vector3.Lerp(h.blendFromPos, pos, t);
                rot = Quaternion.Slerp(h.blendFromRot, rot, t);
            }
            h.held.transform.SetPositionAndRotation(pos, rot);
        }

        // Self-heal: if the server thinks WE hold something that neither hand is actually
        // holding (lost reply, gave-up request that later won arbitration...), release it so
        // it never stays ghost-held and ungrabbable.
        float _nextReconcile;
        void Reconcile()
        {
            if (Time.time < _nextReconcile) return;
            _nextReconcile = Time.time + 1f;
            var actives = GrabbableObject.Active; // spawn kayit listesi — sahne taramasi + dizi alloc'u yok
            for (int i = 0; i < actives.Count; i++)
            {
                var g = actives[i];
                if (g.HolderClientId != NetworkManager.LocalClientId) continue;
                if ((_left != null && g == _left.held) || (_right != null && g == _right.held)) continue;
                g.ReleaseServerRpc();
            }
        }

        void TryGrab(HandState h)
        {
            if (h.held != null || h.supporting != null || h.pinFrom != null) return;

            // BOMBA: oteki eldeki bombaya bos elle yaklasip grip'e basmak destek eli DEGIL, pim
            // cekmektir. Destek dalindan once bakilir, yoksa bomba "cift elle nisan" moduna
            // girip pim hic cekilemezdi. Pim, bu el grip'i birakana kadar elde kalir.
            var o = Other(h);
            GrenadeController grenade = null;
            if (o != null && o.held != null && o.confirmed)
                grenade = o.held.GetComponent<GrenadeController>();

            if (grenade != null && !grenade.Armed)
            {
                // Bombanin collider'i konveks olmayan bir MeshCollider olabilir; bounds ile olcmek
                // hem guvenli hem de kucuk bir objede yeterince hassas.
                float pinDist = NearestColliderDistance(o.held, h.anchor.position, useBounds: true);
                if (pinDist >= 0f && pinDist < grenade.PinPullReach)
                {
                    grenade.PullPin(h.index);
                    h.pinFrom = o.held;
                    return;
                }
            }

            // If my OTHER hand already holds a snap-style weapon and this hand squeezes near
            // it, this hand becomes the SUPPORT hand (two-handed ready stance) instead of
            // trying to grab something else. Slightly longer reach: the handguard is long.
            // Bomba destek eli KABUL ETMEZ: tek elle tutulur, oteki el ya pim ceker ya da
            // serbesttir (yakindaki baska bir objeyi alabilir).
            if (o != null && o.held != null && o.confirmed && o.held.snapToHand && grenade == null)
            {
                float sd = NearestColliderDistance(o.held, h.anchor.position, useBounds: false);
                if (sd >= 0f && sd < grabRadius * 1.5f)
                {
                    h.supporting = o.held;
                    h.supportGrip = o.grip; // null for legacy weapons — rail logic then stays off
                    // Collider listesi tutus boyunca degismez — birakma kontrolu icin bir kez
                    // cache'lenir (her kare GetComponentsInChildren alloc'u yerine).
                    h.supportCols = o.held.GetComponentsInChildren<Collider>();
                    h.supportSince = Time.time;
                    h.nextSupportCheck = 0f;
                    return;
                }
            }

            GrabbableObject best = null;
            float bestDist = float.MaxValue;
            // AllLayers: the default mask skips the "Ignore Raycast" layer, which would make
            // objects on that layer silently ungrabbable.
            foreach (var col in Physics.OverlapSphere(h.anchor.position, grabRadius,
                         Physics.AllLayers, QueryTriggerInteraction.Collide))
            {
                var g = col.GetComponentInParent<GrabbableObject>();
                if (g == null || g.IsHeld) continue;
                float d = Vector3.Distance(h.anchor.position, col.ClosestPoint(h.anchor.position));
                if (d < bestDist) { bestDist = d; best = g; }
            }
            if (best == null) return;
            Adopt(h, best);
        }

        /// <summary>Take <paramref name="g"/> into this hand and ask the server for it. Split out
        /// of <see cref="TryGrab"/> so the weapon selector can hand a weapon straight into the
        /// palm (see <see cref="EquipFromInventory"/>) — proximity grabbing is unchanged.</summary>
        void Adopt(HandState h, GrabbableObject g)
        {
            h.held = g;
            h.requestedAt = Time.time;
            h.confirmed = false;
            // Grab-moment lookup only (the binder attached WeaponGrip at spawn); a weapon
            // without a profile keeps the legacy pivot-snap path bit-for-bit.
            h.grip = g.GetComponent<WeaponGrip>();
            if (h.grip != null && h.grip.Profile == null) h.grip = null;
            h.aimDir = Vector3.zero;
            h.blendUntil = 0f;      // main'in pop duzeltmesi: kapma aninda blend durumunu sifirla
            h.legacyAiming = false;
            if (g.snapToHand)
            {
                // Pull it into the palm, barrel aligned with where the controller points.
                h.posOffset = Vector3.zero;
                h.rotOffset = SnapRotOffset(g);
            }
            else
            {
                // Keep the grab-moment pose (natural for rocks and props).
                h.posOffset = h.anchor.InverseTransformPoint(g.transform.position);
                h.rotOffset = Quaternion.Inverse(h.anchor.rotation) * g.transform.rotation;
            }
            g.RequestGrabServerRpc(h.index);
        }

        void Release(HandState h)
        {
            // Pimi tutan el birakti -> pim yok olur. Bomba KURULU kalir: pim geri takilmaz,
            // birakmak bombayi guvenli hale getirmez (bilincli karar, gercekci taraf).
            if (h.pinFrom != null)
            {
                var gc = h.pinFrom.GetComponent<GrenadeController>();
                if (gc != null) gc.DropPin();
                h.pinFrom = null;
                return;
            }

            // Support hand lets go -> back to one-handed carry, weapon stays in the grip hand.
            if (h.supporting != null)
            {
                h.supporting = null;
                h.supportGrip = null;
                h.supportCols = null;
                return;
            }

            if (h.held == null) return;

            var g = h.held;
            bool profiled = h.grip != null;
            h.held = null;
            h.grip = null;
            h.aimDir = Vector3.zero;
            h.blendUntil = 0f;
            h.legacyAiming = false;
            var o = Other(h);
            if (o != null && o.supporting == g)
            {
                o.supporting = null; // dropped: support ends too
                o.supportGrip = null;
                o.supportCols = null;
            }
            if (g.HolderClientId != NetworkManager.LocalClientId) return;

            if (profiled)
            {
                g.SetSupportHandOwner(GrabbableObject.NoHand); // clear while we still own it

                // PIMI CEKILMIS BOMBA cantaya GIRMEZ — canli bombayi cebe koymak yok. Grip'i
                // birakmak onu el hiziyla firlatir (GrenadeController hizi olcekler + takla
                // ekler). Pimi cekilmemis bomba asagidaki normal silah yolundan cantaya gider.
                var grenade = g.GetComponent<GrenadeController>();
                if (grenade != null && grenade.Armed)
                {
                    g.ApplyThrow(HandVelocity(h), Vector3.zero);
                    g.ReleaseServerRpc();
                    return;
                }

                // A released WEAPON vanishes — the "went into the bag" look; the selector gallery
                // brings its type back on demand. Only profiled weapons: rocks and props (no grip
                // profile) keep the throw-and-land behaviour bit-for-bit.
                RequestWeaponSwap(g, null);
                return;
            }

            g.ApplyThrow(HandVelocity(h), Vector3.zero);
            g.ReleaseServerRpc();
        }

        // How the object should sit in the hand: use the manual override if given, otherwise
        // find the mesh's LONGEST local axis (a rifle's barrel line), pick the sign that points
        // toward the bulk of the mesh (the muzzle side), and map that axis onto hand-forward.
        static Quaternion SnapRotOffset(GrabbableObject g)
        {
            if (g.gripRotationEuler != Vector3.zero)
                return Quaternion.Euler(g.gripRotationEuler);

            // Namlu sozlesmesi WeaponGeometry'de — NetworkWeapon.ComputeBarrel ile AYNI
            // yardimcilar, boylece elde hizalanan eksen ile atis ekseni asla ayrisamaz.
            var biggest = WeaponGeometry.FindBiggestMesh(g.transform);
            if (biggest == null) return Quaternion.identity;

            Bounds mb = biggest.sharedMesh.bounds;
            Vector3 axis = WeaponGeometry.LongestLocalAxis(mb, biggest.transform.lossyScale, out _);
            float sign = WeaponGeometry.BulkSign(mb, axis);

            // Into root space, then compute the rotation that maps it onto +Z (hand forward).
            Quaternion childToRoot = Quaternion.Inverse(g.transform.rotation) * biggest.transform.rotation;
            Vector3 axisRoot = childToRoot * (axis * sign);
            return Quaternion.FromToRotation(axisRoot, Vector3.forward);
        }

        Vector3 HandVelocity(HandState h)
        {
            if (h.trail.Count < 2) return Vector3.zero;
            var oldest = h.trail.Peek();
            var newest = h.anchor.position;
            float dt = Time.time - oldest.t;
            if (dt < 0.02f) return Vector3.zero;
            Vector3 v = (newest - oldest.pos) / dt;
            return Vector3.ClampMagnitude(v, 12f);
        }
    }
}
