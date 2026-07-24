using System.Collections.Generic;
using UnityEngine;
using VRMultiplayer.UI;

namespace VRMultiplayer
{
    /// <summary>
    /// Bir takimin FIZIKSEL dogum bolgesi: zeminde bir cember. Elenen oyuncu kendi takiminin
    /// cemberine YURUYEREK gelir ve icinde kesintisiz bekleyince yeniden dogar
    /// (bkz. <see cref="PlayerHealth"/>).
    ///
    /// Neden isinlanma degil de yuruyus: bu oyun kolokasyonlu — <see cref="CalibrationManager"/>
    /// rig'i gercek odaya kilitler, kalibrasyondan sonra sanal konum ile fiziksel konum AYNI
    /// seydir. Oyuncuyu koddan dogum noktasina tasimak ikisini ayirir; oyuncu sanal dunyada
    /// dogum noktasinda gorunurken gercek odada hala oldugu yerdedir ve yurudugunde baskasinin
    /// ya da duvarin icine girer. Bu yuzden dogum noktasi gercek odada GERCEK bir yerdir.
    ///
    /// Bolge bir sahne objesidir, aga girmez: konumu herkeste zaten aynidir (sahnenin parcasi)
    /// ve icerik kontrolunu SUNUCU yapar. Halka gorseli her istemcide yerel olarak cizilir;
    /// ilerleme yayi da yereldir, yani her oyuncu yalnizca KENDI geri sayimini gorur.
    ///
    /// Sahneye kurulum: Tools > VR Multiplayer > 22.
    /// </summary>
    public class TeamSpawnZone : MonoBehaviour
    {
        [Tooltip("Bu bolge hangi takima ait? 1 = A (mavi), 2 = B (kirmizi).")]
        public byte team = 1;

        [Tooltip("Cemberin yaricapi (metre). Oyuncunun KAFASI bu dairenin icindeyse sayilir; " +
                 "yukseklik dikkate alinmaz. Fiziksel odada rahat durulabilecek kadar genis olmali.")]
        public float radius = 1.2f;

        [Tooltip("Halkanin zeminden yuksekligi (metre) — z-fighting olmasin diye kucuk bir pay.")]
        public float groundOffset = 0.02f;

        const int RingSegments = 64;

        static readonly List<TeamSpawnZone> _all = new List<TeamSpawnZone>();

        LineRenderer _ring;       // tam cember, surekli gorunur (sonuk)
        LineRenderer _progress;   // 0..ilerleme yayi, yalnizca beklerken parlar
        Material _ringMat, _progressMat;
        float _highlight;         // 0 = normal, 1 = bu oyuncu bu bolgeyi bekliyor
        float _builtRadius = -1f; // halkanin cizildigi yaricap (Play modunda degisimi yakalar)

        public Color TeamColor =>
            team == 2 ? PlayerIdentity.TeamBColor :
            team == 1 ? PlayerIdentity.TeamAColor : Color.white;

        void OnEnable()
        {
            if (!_all.Contains(this)) _all.Add(this);
            BuildVisuals();
        }

        void OnDisable()
        {
            _all.Remove(this);
        }

        /// <summary>Verilen takimin bolgesi; yoksa null. Ayni takima birden fazla bolge
        /// konmussa ILKI kullanilir — sunucu ile istemcilerin ayni bolgeyi secmesi icin
        /// secim kurali sabit tutulur.</summary>
        public static TeamSpawnZone For(byte team)
        {
            for (int i = 0; i < _all.Count; i++)
                if (_all[i] != null && _all[i].team == team) return _all[i];
            return null;
        }

        /// <summary>Nokta cemberin icinde mi? YATAY mesafeye bakilir: kafa zeminden ~1.7 m
        /// yukarida durur, dikey farki hesaba katmak herkesi disarida birakirdi.</summary>
        public bool Contains(Vector3 worldPos)
        {
            Vector3 d = worldPos - transform.position;
            d.y = 0f;
            return d.sqrMagnitude <= radius * radius;
        }

        /// <summary>Yatay mesafe (metre) — HUD'daki "x.x m" gostergesi icin.</summary>
        public float HorizontalDistance(Vector3 worldPos)
        {
            Vector3 d = worldPos - transform.position;
            d.y = 0f;
            return d.magnitude;
        }

        /// <summary>YEREL gorunum: bu bolge yerel oyuncunun beklediği bolge mi ve geri sayim
        /// nerede? Yalnizca cizimi etkiler, oyun mantigina girmez.</summary>
        public void SetLocalState(bool waitingHere, float progress01)
        {
            _highlight = waitingHere ? 1f : 0f;
            if (_progress != null)
                ApplyProgress(waitingHere ? Mathf.Clamp01(progress01) : 0f);
        }

        void Update()
        {
            if (_ring == null) return;

            // Yaricap Play modunda ayarlanabilsin (bolge boyutu ancak kulaklikta oturuyor).
            if (!Mathf.Approximately(_builtRadius, radius))
            {
                _builtRadius = radius;
                WriteCircle(_ring, RingSegments, 1f);
            }

            // Beklerken halka nabiz gibi atar — olu oyuncunun gozu onu uzaktan yakalasin.
            float pulse = _highlight > 0f ? 0.75f + 0.25f * Mathf.Sin(Time.time * 4f) : 1f;
            float alpha = Mathf.Lerp(0.28f, 0.95f, _highlight) * pulse;
            Color c = TeamColor;
            UITheme.SetMaterialColor(_ringMat, new Color(c.r, c.g, c.b, alpha));
            _ring.widthMultiplier = Mathf.Lerp(0.03f, 0.055f, _highlight);
        }

        // ------------------------------------------------------------------ gorsel

        void BuildVisuals()
        {
            // Yalnizca Play modunda: edit modunda hiyerarsiye runtime cocuklari eklemek hem
            // kalabalik yapar hem sahneyi kirletir. Editorde cember Gizmo ile gorunur.
            if (!Application.isPlaying || _ring != null) return;

            _builtRadius = radius;
            _ringMat = UITheme.CreateTransparentMaterial(TeamColor);
            _progressMat = UITheme.CreateTransparentMaterial(Color.white);

            _ring = NewLine("Spawn Ring", _ringMat, RingSegments + 1);
            _ring.loop = true;
            WriteCircle(_ring, RingSegments, 1f);

            _progress = NewLine("Spawn Progress", _progressMat, RingSegments + 1);
            _progress.loop = false;
            _progress.widthMultiplier = 0.075f;
            ApplyProgress(0f);
        }

        LineRenderer NewLine(string name, Material mat, int points)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.hideFlags = HideFlags.DontSave;   // sahneye kaydedilmesin, runtime'da kurulur
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.sharedMaterial = mat;
            lr.positionCount = points;
            lr.widthMultiplier = 0.03f;
            lr.numCapVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.alignment = LineAlignment.TransformZ;   // yere yatik cizilsin
            lr.textureMode = LineTextureMode.Stretch;
            return lr;
        }

        void WriteCircle(LineRenderer lr, int segments, float fraction)
        {
            int used = Mathf.Max(2, Mathf.CeilToInt(segments * Mathf.Clamp01(fraction)) + 1);
            lr.positionCount = used;
            for (int i = 0; i < used; i++)
            {
                float t = (i / (float)segments) * Mathf.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(t) * radius, groundOffset, Mathf.Sin(t) * radius));
            }
        }

        void ApplyProgress(float p)
        {
            if (p <= 0.001f)
            {
                _progress.enabled = false;
                return;
            }
            _progress.enabled = true;
            WriteCircle(_progress, RingSegments, p);
            Color c = Color.Lerp(TeamColor, Color.white, 0.6f);
            UITheme.SetMaterialColor(_progressMat, new Color(c.r, c.g, c.b, 1f));
        }

        void OnDrawGizmos()
        {
            Gizmos.color = TeamColor;
            Vector3 p = transform.position;
            const int seg = 48;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i / (float)seg * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                Gizmos.DrawLine(
                    p + new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius,
                    p + new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * radius);
            }
        }
    }
}
