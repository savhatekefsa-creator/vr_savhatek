using UnityEngine;

namespace VRMultiplayer.Weapons
{
    /// <summary>
    /// Pure math for the data-driven grip: weapon pose from a hand anchor, closest point on the
    /// support rail, the soft-knee two-handed aim filter and right↔left mirroring. No scene,
    /// component or network state — every function is testable with plain vectors.
    /// </summary>
    public static class WeaponGripMath
    {
        /// <summary>
        /// World pose of the weapon such that its grip anchor lands exactly on the hand anchor.
        /// Scale-safe: gripLocalPos is scaled by the weapon's lossyScale (scene rifles run at
        /// (2,2,2)), so the authored local point stays glued under any uniform/non-uniform scale.
        /// </summary>
        public static void SolveWeaponPose(
            Vector3 anchorPos, Quaternion anchorRot,
            Vector3 gripLocalPos, Quaternion gripLocalRot,
            Vector3 weaponLossyScale,
            out Vector3 weaponPos, out Quaternion weaponRot)
        {
            weaponRot = anchorRot * Quaternion.Inverse(gripLocalRot);
            weaponPos = anchorPos - weaponRot * Vector3.Scale(weaponLossyScale, gripLocalPos);
        }

        /// <summary>Clamped parametric t (0..1) of the point on the start→end segment closest
        /// to worldPoint. Degenerate (zero-length) rails return 0.</summary>
        public static float RailClosestT(Vector3 railStartWorld, Vector3 railEndWorld, Vector3 worldPoint)
        {
            Vector3 seg = railEndWorld - railStartWorld;
            float len2 = seg.sqrMagnitude;
            if (len2 < 1e-8f) return 0f;
            return Mathf.Clamp01(Vector3.Dot(worldPoint - railStartWorld, seg) / len2);
        }

        /// <summary>
        /// One step of the two-handed aim filter. Angle error inside the deadzone is ignored
        /// completely (rock-solid barrel while both hands are still); past the soft knee the aim
        /// follows with the given half-life. The SmoothStep knee makes the blend continuous, so
        /// crossing the deadzone edge can never pop.
        /// </summary>
        public static Vector3 FilterAim(
            Vector3 currentDir, Vector3 rawDir,
            float deadzoneDeg, float softKneeDeg, float halfLifeMs, float dt)
        {
            if (rawDir.sqrMagnitude < 1e-8f) return currentDir;
            rawDir.Normalize();
            if (currentDir.sqrMagnitude < 1e-8f) return rawDir;

            float angle = Vector3.Angle(currentDir, rawDir);
            float knee = Mathf.Max(0.01f, softKneeDeg);
            float w = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(deadzoneDeg, deadzoneDeg + knee, angle));
            if (w <= 0f) return currentDir;

            float halfLife = Mathf.Max(1f, halfLifeMs) / 1000f;
            float k = 1f - Mathf.Exp(-0.6931472f * dt / halfLife); // ln(2): yarilanma tanimi
            return Vector3.Slerp(currentDir, rawDir, w * k).normalized;
        }

        /// <summary>Mirror a weapon-local position across the weapon's local YZ plane — turns a
        /// right-hand authored pose into its left-hand twin.</summary>
        public static Vector3 MirrorX(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        /// <summary>Mirror a weapon-local rotation across the local YZ plane (M·q·M for
        /// M = diag(-1,1,1), which for quaternions is negating the y and z components).</summary>
        public static Quaternion MirrorX(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);
    }
}
