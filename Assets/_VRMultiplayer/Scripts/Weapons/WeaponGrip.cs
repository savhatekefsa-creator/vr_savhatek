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

        // The avatar the profile is currently applied to (cleared when the holder changes).
        ProceduralFingerPoser _poser;
        WeaponHandWeld _weld;

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
            if (!_dirty && !(_grab.IsHeld && _poser == null)) return;
            _dirty = false;
            Evaluate();
        }

        void Evaluate()
        {
            ProceduralFingerPoser poser = null;
            WeaponHandWeld weld = null;

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
                    }
                }
            }

            // Holder changed / released / disconnected -> fully release the previous avatar.
            if (_poser != null && _poser != poser)
            {
                _poser.ClearHandOverride(true);
                _poser.ClearHandOverride(false);
            }
            if (_weld != null && _weld != weld)
            {
                _weld.ClearHand(true);
                _weld.ClearHand(false);
            }
            _poser = poser;
            _weld = weld;
            if (poser == null || weld == null) return;

            bool mainLeft = _grab.HolderHand == 0;
            byte sup = _grab.SupportHand;
            bool otherLeft = !mainLeft;
            // Poses are authored main=RIGHT / support=LEFT; swapped roles mirror the data.
            bool mirrored = mainLeft;

            poser.SetHandOverride(mainLeft, _profile, false);
            weld.SetHand(mainLeft, transform, _profile, false, mirrored);

            bool hasSupport = sup != GrabbableObject.NoHand && (sup == 0) == otherLeft;
            if (hasSupport)
            {
                poser.SetHandOverride(otherLeft, _profile, true);
                weld.SetHand(otherLeft, transform, _profile, true, mirrored);
            }
            else
            {
                poser.ClearHandOverride(otherLeft);
                weld.ClearHand(otherLeft);
            }
        }

        void ClearApplied()
        {
            if (_poser != null)
            {
                _poser.ClearHandOverride(true);
                _poser.ClearHandOverride(false);
                _poser = null;
            }
            if (_weld != null)
            {
                _weld.ClearHand(true);
                _weld.ClearHand(false);
                _weld = null;
            }
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
