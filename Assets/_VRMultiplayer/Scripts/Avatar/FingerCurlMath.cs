using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Parmak kivrimi icin ORTAK kural. Iki yerden kullaniliyor:
    /// <see cref="ProceduralFingerPoser"/> (avatarin humanoid elleri, uzak oyuncularin
    /// gordugu) ve <see cref="FirstPersonFingerCurl"/> (birinci sahis elinin KENDI rig'i).
    /// Kural tek yerde durmali - iki kopya zamanla ayrisir ve iki el farkli kivrilir.
    ///
    /// Neden bu kural: mentese ekseni GEOMETRIDEN turetiliyor, modelden veya sol/sag
    /// tarafindan bagimsiz. Pozitif aci HER ZAMAN ucu hedefe buker, dolayisiyla yeni bir
    /// rig'e gecerken "bu modelde isaret ters mi?" diye tahmin yurutmek gerekmiyor.
    /// Meta'nin eli tam da bu yuzden ek ayar istemeden calisiyor.
    /// </summary>
    public static class FingerCurlMath
    {
        /// <summary>
        /// Bir bogum icin acik/kapali yerel donusleri cozer.
        ///
        /// DORT PARMAK (<paramref name="planeNormal"/> dolu): mentese =
        /// Cross(uzanim, avuc normali) — her parmak KENDI duzleminde paralel katlanir.
        /// Hepsini tek bir noktaya kivirmak parmaklari avucun ortasinda birbirine
        /// gecirmisti, o yuzden duzlem kurali.
        /// BASPARMAK (<paramref name="planeNormal"/> null): mentese =
        /// Cross(uzanim, hedefe) — avucun uzerinden capraz gecer.
        /// </summary>
        /// <param name="restLocal">Acik el = DINLENME poz anlik goruntusu. Canli pozdan
        /// alinmamali: animator/idle klibi parmaklari zaten kismen kivirmis olabilir.</param>
        public static bool Solve(Transform bone, Vector3 extWorld, Vector3 target,
                                 Vector3? planeNormal, float curlDegrees, Quaternion restLocal,
                                 out Quaternion open, out Quaternion closed)
        {
            open = restLocal;
            closed = restLocal;
            if (bone == null || bone.parent == null) return false;
            if (extWorld.sqrMagnitude < 1e-12f) return false;

            Vector3 hinge = planeNormal.HasValue
                ? Vector3.Cross(extWorld.normalized, planeNormal.Value)
                : Vector3.Cross(extWorld.normalized, (target - bone.position).normalized);
            if (hinge.sqrMagnitude < 1e-6f) return false;

            Vector3 axisParent = bone.parent.InverseTransformDirection(hinge.normalized).normalized;
            closed = Quaternion.AngleAxis(curlDegrees, axisParent) * restLocal;
            return true;
        }

        /// <summary>
        /// Elin avuc duzlemi. Dort parmagin mentese duzlemi ve basparmagin hedefi bundan
        /// cikar; iki rig'de de ayni anatomik tanim kullanildigi icin sonuc tutarli.
        /// </summary>
        public static void PalmFrame(Transform wrist, Transform indexProximal,
                                     Transform littleProximal, Transform middleProximal,
                                     bool left, out Vector3 palmNormal, out Vector3 curlPlaneNormal,
                                     out Vector3 thumbTarget)
        {
            Vector3 fingersDir = (middleProximal.position - wrist.position).normalized;
            Vector3 sideDir = (indexProximal.position - littleProximal.position).normalized;
            palmNormal = Vector3.Cross(fingersDir, sideDir).normalized;

            // sideDir (isaret->serce) ham carpimi SAG avucun disina, SOL elin ise SIRTINA
            // bakan bir normal veriyor - solda cevrilir.
            curlPlaneNormal = left ? -palmNormal : palmNormal;

            // Basparmak avucun uzerinden gecer: hedefi isaret/orta tabanlarinin ortasi.
            thumbTarget = (indexProximal.position + middleProximal.position) * 0.5f;
        }
    }
}
