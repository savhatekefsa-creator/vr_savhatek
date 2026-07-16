using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Owner-side recoil: every shot pushes the weapon back along its barrel and lifts the muzzle,
    /// then it springs back to the aim line. Attached at runtime by <see cref="NetworkWeapon"/> only
    /// when the profile actually asks for kick, so an unprofiled or zero-kick weapon never even
    /// ticks. Plain MonoBehaviour — NGO cannot AddComponent a NetworkBehaviour at runtime.
    ///
    /// Slots into the pose chain at order 60: <see cref="HandGrabber"/> (10) has already written
    /// the weapon transform from the hand anchor, and <see cref="WeaponHandWeld"/> (110) has not yet
    /// read it. So the weld welds the wrist to the RECOILED weapon and the hand can never separate
    /// from the grip. Remote clients receive the recoiled pose through the owner-authoritative
    /// ClientNetworkTransform and their own weld glues their copy of the hand — no extra RPC or
    /// NetworkVariable is needed for any of this.
    ///
    /// The rotation pivots around the profile's GRIP ANCHOR (mirrored for a left-handed hold, same
    /// as HandGrabber.FollowProfiled), which is exactly the point the weld pins the wrist to: that
    /// point is provably invariant under the kick, so the hand stays put while the muzzle climbs.
    /// Muzzle climb also aims the next shot for free — NetworkWeapon reads muzzle.forward the next
    /// frame, off the already-recoiled transform.
    /// </summary>
    [DefaultExecutionOrder(60)]
    public class WeaponRecoil : MonoBehaviour
    {
        GrabbableObject _grab;
        WeaponGripProfile _profile;

        float _pitch;   // birikmis namlu kalkisi (derece)
        float _yaw;     // birikmis yatay sekme (derece)
        float _back;    // birikmis geri itilme (DUNYA metresi)
        bool _sustained;

        // Yaw jitter tavani: sekme karakter katar, nisani kaybettirmez.
        const float MaxAccumYaw = 2f;

        public void Init(GrabbableObject grab, WeaponGripProfile profile)
        {
            _grab = grab;
            _profile = profile;
        }

        /// <summary>Owner-side: bir atisin tepmesini birige ekle. Iki elli tutus hem tirmanisi
        /// hem sekmeyi profildeki carpanla kisar.</summary>
        public void AddKick()
        {
            if (_profile == null || _grab == null) return;

            float mult = _grab.SupportHand != GrabbableObject.NoHand
                ? _profile.supportRecoilMultiplier
                : 1f;

            _pitch = Mathf.Min(_pitch + _profile.kickPitchPerShot * mult, _profile.maxAccumPitch);
            _back = Mathf.Min(_back + _profile.kickBackMeters * mult, _profile.maxAccumBack);
            _yaw = Mathf.Clamp(
                _yaw + Random.Range(-_profile.kickYawJitter, _profile.kickYawJitter) * mult,
                -MaxAccumYaw, MaxAccumYaw);
        }

        /// <summary>NetworkWeapon bildirir: tetik hala cekili ve az once atis yapildi mi? Tarama
        /// sirasinda tepme yavas soner (tirmanis birikir), tetik kesilince hizla toparlanir.</summary>
        public void SetSustainedFire(bool sustained) => _sustained = sustained;

        void LateUpdate()
        {
            if (_profile == null || _grab == null) return;

            // Sadece silahi TUTAN oyuncunun makinesi tepmeyi uygular; uzaktakiler tepmis pozu
            // zaten ClientNetworkTransform'dan aliyor, ikinci kez uygularlarsa cift sayilir.
            var nm = NetworkManager.Singleton;
            bool mine = nm != null && _grab.IsHeld && _grab.HolderClientId == nm.LocalClientId;
            if (!mine)
            {
                _pitch = _yaw = _back = 0f; // birakildi: durum sifir, transforma dokunma
                _sustained = false;
                return;
            }

            float halfLife = _sustained ? _profile.recoilDecayHalfLife : _profile.recoilRestDecayHalfLife;
            float k = Mathf.Pow(2f, -Time.deltaTime / Mathf.Max(0.001f, halfLife));
            _pitch *= k;
            _yaw *= k;
            _back *= k;

            if (Mathf.Abs(_pitch) < 1e-4f && Mathf.Abs(_yaw) < 1e-4f && Mathf.Abs(_back) < 1e-6f)
                return;

            Apply();
        }

        void Apply()
        {
            // Kabza cipasi: poz yazim kurali ana el=SAG, sol elde tutuluyorsa aynalanir —
            // HandGrabber.FollowProfiled ve WeaponHandWeld ile ayni mantik, ayni nokta.
            Vector3 gripLocal = _profile.gripLocalPosition;
            if (_grab.HolderHand == 0) gripLocal = WeaponGripMath.MirrorX(gripLocal);
            Vector3 pivot = transform.TransformPoint(gripLocal);

            // Namlu ekseni HER ZAMAN profilden gelir (+Z degil: HK416 -X, Pistol -Z).
            Vector3 barrelLocal = _profile.barrelLocalDirection.sqrMagnitude > 1e-6f
                ? _profile.barrelLocalDirection.normalized
                : Vector3.forward;
            Vector3 barrel = (transform.rotation * barrelLocal).normalized;

            // Tirmanis ekseni dunya yataylarindan kurulur, silahin lokal eksenlerinden DEGIL:
            // silahin lokal "ust"u profilden profile degisiyor (HK416 namlusu -X), dolayisiyla
            // guvenilir bir referans degil. Cross(up, ileri) = sag, ve Unity'de sag eksende
            // POZITIF aci namluyu asagi egdigi icin isaret negatif.
            Vector3 right = Vector3.Cross(Vector3.up, barrel);
            if (right.sqrMagnitude < 1e-4f) right = Vector3.Cross(transform.forward, barrel); // namlu dike yakin
            if (right.sqrMagnitude < 1e-4f) right = transform.right;
            right.Normalize();

            Quaternion q = Quaternion.AngleAxis(-_pitch, right) * Quaternion.AngleAxis(_yaw, Vector3.up);

            // Kabza etrafinda pivotlu rijit donus: yeni kabza konumu = pivot + q*(pivot-pivot) =
            // pivot, yani cipa kilitli kalir ve el kabzadan kopmaz. Geri itilme ise silahi
            // tumuyle oteler — el de silahla birlikte geri gelir (beklenen VR illuzyonu).
            transform.SetPositionAndRotation(
                pivot + q * (transform.position - pivot) - barrel * _back,
                q * transform.rotation);
        }
    }
}
