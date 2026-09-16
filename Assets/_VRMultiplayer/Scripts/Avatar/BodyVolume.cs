using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// Avatarin govdesinin KABA HACMI — "govde ne kadar genis" sorusunun TEK cevabi.
    ///
    /// NEDEN TEK YERDE: bu soru iki yerde ayri ayri cevaplaniyordu ve cevaplar ayrismisti.
    /// WeaponHandWeld govdeyi 0.22 m yari genislikte sayip silahi disari itiyor,
    /// AvatarIKController ise dirsegin govde ekseninden en az 0.20 m disarida kalmasini
    /// garanti ediyordu. Yani dirsek, govdenin KENDI modelinin 2 cm ICINDE durmaya
    /// yetkiliydi — kol gorunur sekilde kaburgalara giriyordu. Olcu tek yerden gelince
    /// boyle bir bosluk yapisal olarak kalamaz.
    ///
    /// KESIT DAIRE DEGIL ELIPS: gogus onu yanlardan belirgin daha dardir. Daire kesit,
    /// gogus onunde iki elle tutulan tabancayi "iceride" sayip 18 cm one itiyordu; silah
    /// havada duruyordu.
    ///
    /// Hacim kalca ile boyun arasindaki BANT'tir. Bandin disindaki ornekler yok sayilir:
    /// bacak hizasindaki el itilmez, yuze yaklastirilan silah da itilmez (nisan hatti).
    /// </summary>
    public static class BodyVolume
    {
        /// <summary>Govdenin YAN yari genisligi (m, omuz/kaburga hizasi).</summary>
        public const float Radius = 0.22f;

        /// <summary>Govdenin ON/ARKA yari derinligi (m, gogus + yelek).</summary>
        public const float Depth = 0.14f;

        /// <summary>Govde bandinin bir karelik cercevesi. Bir kez kurulur, cok kez sorgulanir:
        /// silah temizligi uzun eksen boyunca 11 ornek atiyor, hepsi ayni cerceveyi kullanir.</summary>
        public struct Frame
        {
            public bool ok;
            public Vector3 basePoint;   // kalca
            public Vector3 axisVec;     // kalca -> boyun (BIRIMLESTIRILMEMIS)
            public float axisLen2;
            public Vector3 right, fwd;  // eksene dik elips eksenleri
            public bool ellipse;        // yon guvenilmezse daireye dusuldu
        }

        /// <summary>Govde cercevesini kurar. <paramref name="bodyForward"/> avatar kokunun ileri
        /// yonudur (govde kafanin yaw'ini izler); eksene dik bileseni alinir.</summary>
        public static Frame Make(Transform hips, Transform neck, Vector3 bodyForward)
        {
            Frame f = default(Frame);
            if (hips == null || neck == null) return f;

            f.basePoint = hips.position;
            f.axisVec = neck.position - hips.position;
            f.axisLen2 = f.axisVec.sqrMagnitude;
            if (f.axisLen2 < 1e-6f) return f;

            Vector3 axis = f.axisVec / Mathf.Sqrt(f.axisLen2);
            Vector3 fwd = bodyForward - axis * Vector3.Dot(bodyForward, axis);
            f.ellipse = fwd.sqrMagnitude > 1e-4f;
            if (f.ellipse)
            {
                f.fwd = fwd.normalized;
                f.right = Vector3.Cross(axis, f.fwd).normalized;
            }
            f.ok = true;
            return f;
        }

        /// <summary>
        /// Noktayi govde hacminin DISINA iten vektor; nokta zaten disarida ya da bandin
        /// disindaysa sifir. Itme govde eksenine DIKTIR — itilen sey yukari/asagi kaymaz.
        /// </summary>
        /// <param name="ra">Yan yaricap (m). Cagiran, itilen nesnenin kalinligini EKLER.</param>
        /// <param name="rb">On/arka yaricap (m).</param>
        /// <param name="endFade">Bandin UCLARINDA itmeyi yumusatan pay (bandin oranindan,
        /// 0 = sert kesme). Silah yolu 0 kullanir (davranis degismesin, ayrica onun kaydirmasi
        /// zaten SmoothDamp'ten geciyor). EL yolu 0'dan buyuk kullanir ve bunun IKI sebebi var:
        /// (1) sert kesme sinirda sicrama uretir - eli kalca hizasinda asagi indirince itme bir
        /// anda sifirlanir; (2) govde asagida GERCEKTEN daha dardir, silindir modeli kalcada
        /// fazla genis kalir. Yanlarda sarkan rahat kol boylece itilmez, gogus onundeki el
        /// tam itilir.</param>
        /// <param name="penetration">Ne kadar iceride kalindigi (m) — cagiran en derin ornegi
        /// secebilsin diye. Nokta tam eksenin ustundeyken yon tanimsizdir: itme sifir doner ama
        /// derinlik dolu gelir, boylece cagiran kendi yedek yonunu secebilir.</param>
        public static Vector3 PushOut(in Frame f, Vector3 p, float ra, float rb,
                                     out float penetration, float endFade = 0f)
        {
            penetration = 0f;
            if (!f.ok) return Vector3.zero;

            Vector3 rel = p - f.basePoint;
            float u = Vector3.Dot(rel, f.axisVec) / f.axisLen2;
            if (u < 0f || u > 1f) return Vector3.zero;      // govde bandinin disinda

            float fade = 1f;
            if (endFade > 0f)
            {
                float k = Mathf.Min(u, 1f - u) / endFade;
                if (k <= 0f) return Vector3.zero;
                if (k < 1f) fade = Mathf.SmoothStep(0f, 1f, k);
            }

            Vector3 d = rel - f.axisVec * u;                // eksene DIK bilesen

            if (f.ellipse)
            {
                float x = Vector3.Dot(d, f.right), z = Vector3.Dot(d, f.fwd);
                float e = Mathf.Sqrt((x * x) / (ra * ra) + (z * z) / (rb * rb));   // 1 = yuzey
                if (e >= 1f) return Vector3.zero;
                if (e < 1e-3f) { penetration = rb * fade; return Vector3.zero; }   // eksenin ustunde
                Vector3 push = d * (1f / e - 1f);                                  // yuzeye, elips-radyal
                penetration = push.magnitude * fade;
                return push * fade;
            }

            float dist = d.magnitude;
            float pen = ra - dist;
            if (pen <= 0f) return Vector3.zero;
            penetration = pen * fade;
            return (dist > 1e-4f ? d * (pen / dist) : Vector3.zero) * fade;
        }
    }
}
