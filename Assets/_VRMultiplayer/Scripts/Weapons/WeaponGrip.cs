using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Runs on EVERY client on a profiled weapon (attached at runtime by
    /// <see cref="WeaponGripBinder"/>). Watches the grabbable's replicated holder / hand /
    /// support state through a dirty flag (NGO deltas can land in any order inside a frame, so
    /// we evaluate once in LateUpdate) and applies the grip profile to the holder's avatar:
    /// finger overrides via <see cref="ProceduralFingerPoser"/> and wrist welding via
    /// <see cref="WeaponHandWeld"/>. Evaluate() is idempotent — grab, release, hand swap,
    /// holder change, late join and disconnect all funnel through the same path.
    ///
    /// Runs BEFORE the finger poser (100) and the weld (110), so state set here lands in the
    /// same frame's pose.
    /// </summary>
    [DefaultExecutionOrder(90)]
    [RequireComponent(typeof(GrabbableObject))]
    public class WeaponGrip : MonoBehaviour
    {
        WeaponGripProfile _profile;
        GrabbableObject _grab;
        System.Action _markDirty;
        bool _dirty;

        // The avatar + exact hands THIS grip currently drives. A grip only ever clears the
        // (avatar, hand) slots it applied itself, so dual-wielding two profiled weapons — each
        // driving one hand on the shared per-avatar poser/weld — never wipes the other's hand.
        ProceduralFingerPoser _appPoser;
        WeaponHandWeld _appWeld;
        bool _appLeft, _appRight;

        public WeaponGripProfile Profile => _profile;

        /// <summary>Called by the binder right after AddComponent (and again on re-binds).</summary>
        public void Bind(WeaponGripProfile profile)
        {
            _profile = profile;
            if (_grab == null)
            {
                _grab = GetComponent<GrabbableObject>();
                _markDirty = () => _dirty = true;
                _grab.StateDirty += _markDirty;
            }
            _dirty = true;
        }

        void OnDestroy()
        {
            if (_grab != null && _markDirty != null) _grab.StateDirty -= _markDirty;
            ClearApplied();
        }

        void LateUpdate()
        {
            if (_grab == null || _profile == null) return;

            // Late join: the weapon's initial "held" state can replicate before the holder's
            // player object spawns — keep looking for the avatar until it appears.
            if (!_dirty && !(_grab.IsHeld && _appPoser == null)) return;
            _dirty = false;
            Evaluate();
        }

        void Evaluate()
        {
            ProceduralFingerPoser poser = null;
            WeaponHandWeld weld = null;
            bool wantLeft = false, wantRight = false, mainLeft = false, mirrored = false;

            if (_grab.IsHeld)
            {
                var avatar = FindPlayerObject(_grab.HolderClientId);
                if (avatar != null)
                {
                    poser = avatar.GetComponentInChildren<ProceduralFingerPoser>(true);
                    if (poser != null)
                    {
                        weld = poser.GetComponent<WeaponHandWeld>();
                        if (weld == null) weld = poser.gameObject.AddComponent<WeaponHandWeld>();

                        mainLeft = _grab.HolderHand == 0;
                        mirrored = mainLeft; // authored main=RIGHT/support=LEFT; swapped -> mirror
                        bool otherLeft = !mainLeft;
                        byte sup = _grab.SupportHand;
                        bool hasSupport = sup != GrabbableObject.NoHand && (sup == 0) == otherLeft;
                        wantLeft = mainLeft || (hasSupport && otherLeft);
                        wantRight = !mainLeft || (hasSupport && !otherLeft);
                    }
                }
            }

            // Release ONLY the (avatar, hand) slots THIS grip previously drove that are no
            // longer wanted — or that now live on a different avatar (holder change / release /
            // disconnect). A hand another weapon owns is never touched.
            bool avatarChanged = _appPoser != poser || _appWeld != weld;
            ReleaseSlot(ref _appLeft, true, avatarChanged || !wantLeft);
            ReleaseSlot(ref _appRight, false, avatarChanged || !wantRight);

            _appPoser = poser;
            _appWeld = weld;
            if (poser == null || weld == null) return;

            if (wantLeft) ApplySlot(ref _appLeft, poser, weld, true, true != mainLeft, mirrored);
            if (wantRight) ApplySlot(ref _appRight, poser, weld, false, false != mainLeft, mirrored);
        }

        void ReleaseSlot(ref bool applied, bool left, bool shouldRelease)
        {
            if (!applied || !shouldRelease) return;
            if (_appPoser != null) _appPoser.ClearHandOverride(left);
            if (_appWeld != null) _appWeld.ClearHand(left);
            applied = false;
        }

        void ApplySlot(ref bool applied, ProceduralFingerPoser poser, WeaponHandWeld weld,
            bool left, bool isSupport, bool mirrored)
        {
            poser.SetHandOverride(left, _profile, isSupport);
            weld.SetHand(left, transform, _profile, isSupport, mirrored);
            applied = true;
        }

        void ClearApplied()
        {
            ReleaseSlot(ref _appLeft, true, true);
            ReleaseSlot(ref _appRight, false, true);
            _appPoser = null;
            _appWeld = null;
        }

        // Client-safe holder lookup: PlayerObjects lists every player object visible to this
        // client (ConnectedClients is server-only).
        static GameObject FindPlayerObject(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;
            var players = nm.SpawnManager.PlayerObjects;
            for (int i = 0; i < players.Count; i++)
            {
                var po = players[i];
                if (po != null && po.OwnerClientId == clientId) return po.gameObject;
            }
            return null;
        }
    }
}
