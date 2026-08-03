using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// Gorusun UST-ORTASINDA duran MAC BARI: iki takimin skoru ve mac suresi.
    ///
    ///   [chip] MAVI TAKIM   7  |  12:34  |  5   KIZIL TAKIM [chip]
    ///
    /// Sahibe ozel, <see cref="PlayerHUD"/> tarafindan uretilir.
    ///
    /// YENI AG KATMANI YOK. Skor zaten replike (<see cref="PlayerIdentity.TeamScore"/>
    /// oyunculardaki Kills'i toplar), saat zaten senkron (<c>NetworkManager.ServerTime</c>
    /// her istemcide AYNI degeri verir). Yani bar ne NetworkObject ne NetworkVariable ister.
    ///
    /// SURE = GECEN SURE (sunucu acildigindan beri). Hedef aslinda GERI SAYIM ama o
    /// MatchManager demek: mac uzunlugu, baslangic olayi, bitis kosulu, skor sifirlama.
    /// Bar o ise BEKLEMESIN diye sure tek bir string uzerinden besleniyor
    /// (<see cref="SetTime"/>) — MatchManager gelince yalnizca BESLEYEN taraf degisir,
    /// bu dosyaya dokunulmaz.
    ///
    /// YERLESIM KARARI (+28 derece): kill paneli +19 derecede ve ust kenari 0.44 m'de.
    /// Bar 0.64 m'ye (alt kenar 0.59) konarak onu 15 cm asiyor. +28 "bak-gor" bolgesidir,
    /// nisan hattinda degil — skor tablosu surekli izlenen bir sey degil, dogru davranis.
    /// SINIR: Quest 3'te merkezden ~30 dereceyi gecen icerik lens kenarina duser; barin UST
    /// KOSELERI 34.5 derecede. Cihazda bulaniklasirsa <see cref="offsetUp"/> dusurulur ya da
    /// <see cref="barWidth"/> daraltilir (ikisi de calisma aninda ayarlanabilir).
    ///
    /// RENKLER MUTLAK, bakan oyuncuya gore DEGIL. Kill paneli goreceli calisir (mavi = senin
    /// takimin) ama orada takim ADI yazmaz. Bar "MAVI TAKIM" yazdigi icin goreceli renk
    /// dogrudan yalan olurdu.
    /// </summary>
    public class MatchBarUI : MonoBehaviour
    {
        [Header("Yerlesim (kafaya gore, metre)")]
        public float distance = 1.2f;
        [Tooltip("+0.64 @ 1.2 m ≈ 28 derece yukari. Kill panelinin ust kenarini 15 cm asar.")]
        public float offsetUp = 0.64f;
        [Tooltip("Sonumlu takip hizi. Buyuk deger = kafaya daha sert kilitli.")]
        public float followSpeed = 9f;

        [Header("Bar")]
        public float barWidth = 0.90f;
        public float barHeight = 0.10f;

        [Header("Opaklik")]
        [Tooltip("Zeminin opakligi. Kullanici istegi: seffaf / dusuk opaklik.")]
        [Range(0f, 1f)] public float bgAlpha = 0.35f;
        [Range(0f, 1f)] public float edgeAlpha = 0.5f;

        // Kill paneliyle AYNI kusak (bkz. KillFeedUI): oda geometrisi, olum perdesi, vinyet ve
        // hasar flasi saydam kuyruk 3000'de ve kafaya cok yakin. Ayni kuyrukta saydamlar
        // mesafeye gore siralandigi icin 3000'de kalan bir HUD ezilir.
        const int QBg = 3050, QText = 3051;

        // Yerel yerlesim (bar merkezi orijin). Tasarim: chip | takim adi | skor | ayrac |
        // SURE | ayrac | skor | takim adi | chip.
        const float ChipX = 0.421f, ChipSize = 0.018f;
        const float NameX = 0.400f, ScoreX = 0.215f, SepX = 0.155f;
        const float NameSize = 0.026f, ScoreSize = 0.048f, TimeSize = 0.044f;
        const float SepW = 0.0015f, SepH = 0.055f;
        const float Radius = 0.014f, EdgeThickness = 0.0025f;

        // Skor/sure tazeleme. 5 Hz saniye hanesini canli tutar; metin DEGISMEDIKCE yazilmaz
        // (TextMesh.text atamasi mesh'i yeniden uretir).
        const float RefreshInterval = 0.2f;

        Transform _bar;
        TextMesh _scoreBlue, _scoreRed, _time;
        float _nextRefresh;
        bool _placed;
        bool _externalTime;   // SetTime cagrildiysa otomatik besleme susar

        /// <summary>Sure alanini disaridan yaz. MatchManager geldiginde TEK dokunulacak yer:
        /// ilk cagridan sonra barin kendi gecen-sure sayaci susar, yoksa iki kaynak ayni
        /// alani yazip yanip sonerdi.</summary>
        public void SetTime(string s)
        {
            _externalTime = true;
            if (_time != null && _time.text != s) _time.text = s;
        }

        // ------------------------------------------------------------------- kurulum

        void Awake()
        {
            _bar = new GameObject("Match Bar Panel").transform;
            _bar.SetParent(transform, false);

            var size = new Vector2(barWidth, barHeight);

            // Zemin ve cerceve AYRI mesh'ler: cerceve gercek bir HALKA (UIMesh.RoundedRectOutline).
            // Dolu bir dikdortgeni zeminin altina koymak saydamda ortada iki katman uretirdi ve
            // "seffaf bar" istegini bozardi (bkz. o metodun yorumu).
            var bg = UITheme.MakeShape(_bar, "Bg",
                UIMesh.RoundedRect(size.x, size.y, Radius),
                Alpha(UITheme.PanelBg, bgAlpha), QBg);
            bg.localPosition = new Vector3(0f, 0f, 0.002f);   // yazinin ARKASINDA

            var edge = UITheme.MakeShape(_bar, "Edge",
                UIMesh.RoundedRectOutline(size.x, size.y, Radius, EdgeThickness),
                Alpha(UITheme.PanelEdge, edgeAlpha), QBg + 1);
            edge.localPosition = new Vector3(0f, 0f, 0.0015f);

            BuildSide(-1f, "MAVİ TAKIM", UITheme.TeamBlueText, PlayerIdentity.TeamAColor,
                out _scoreBlue);
            BuildSide(+1f, "KIZIL TAKIM", UITheme.TeamRedText, PlayerIdentity.TeamBColor,
                out _scoreRed);

            Separator(-SepX);
            Separator(+SepX);

            _time = UITheme.MakeText(_bar, "--:--", UITheme.TextPrimary, TimeSize,
                TextAnchor.MiddleCenter, QText);
            _time.transform.localPosition = Vector3.zero;

            Refresh();
        }

        /// <param name="dir">-1 = sol (mavi), +1 = sag (kizil).</param>
        void BuildSide(float dir, string label, Color textColor, Color chipColor, out TextMesh score)
        {
            // Renk chip'i: takim rengini YAZIDAN BAGIMSIZ gosterir. Yazi rengi okunurluk icin
            // acilmis tonlar (TeamBlueText/TeamRedText); chip avatar renginin ta kendisi.
            var chip = UITheme.MakeShape(_bar, label + " Chip",
                UIMesh.RoundedRect(ChipSize, ChipSize, ChipSize * 0.28f), chipColor, QText);
            chip.localPosition = new Vector3(dir * ChipX, 0f, 0f);

            var name = UITheme.MakeText(_bar, label, textColor, NameSize,
                dir < 0f ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight, QText);
            name.transform.localPosition = new Vector3(dir * NameX, 0f, 0f);

            // Skor TAKIM RENGINDE DEGIL: barin en baskin ogesi o, en okunakli renk kazanir.
            // Hangi skorun kime ait oldugu zaten yaninda duran ad ve chip'ten belli.
            score = UITheme.MakeText(_bar, "0", UITheme.TextPrimary, ScoreSize,
                TextAnchor.MiddleCenter, QText);
            score.transform.localPosition = new Vector3(dir * ScoreX, 0f, 0f);
        }

        void Separator(float x)
        {
            var s = UITheme.MakeShape(_bar, "Sep",
                UIMesh.RoundedRect(SepW, SepH, 0f), UITheme.SurfaceEdge, QText);
            s.localPosition = new Vector3(x, 0f, 0f);
        }

        static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

        // ------------------------------------------------------------------- guncelleme

        // LateUpdate: kafa transformu bu asamada kesinlesmis olur, bar bir kare geriden gelmez.
        void LateUpdate()
        {
            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                Refresh();
            }
            Follow();
        }

        void Refresh()
        {
            Write(_scoreBlue, PlayerIdentity.TeamScore(PlayerProfile.TeamBlue).ToString());
            Write(_scoreRed, PlayerIdentity.TeamScore(PlayerProfile.TeamRed).ToString());
            if (!_externalTime) Write(_time, ElapsedText());
        }

        static void Write(TextMesh tm, string s)
        {
            if (tm != null && tm.text != s) tm.text = s;
        }

        /// <summary>Sunucu acildigindan beri gecen sure. <c>ServerTime</c> her istemcide AYNI
        /// degeri verdigi icin herkes ayni sayiyi gorur — senkron icin ek bir sey gerekmez.</summary>
        static string ElapsedText()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) return "--:--";

            double t = nm.ServerTime.Time;
            if (t < 0d) t = 0d;
            int dk = (int)(t / 60d), sn = (int)(t % 60d);
            return dk.ToString("00") + ":" + sn.ToString("00");
        }

        // Gorus uzayinda sabit ama SONUMLU: kafa donunce bar gecikmeli yakalar, goze
        // civilenmis hissi olusmaz. KillFeedUI.Follow ile ayni desen (offsetRight = 0).
        void Follow()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 target = head.position + head.forward * distance + head.up * offsetUp;
            Quaternion rot = Quaternion.LookRotation(target - head.position, head.up);

            if (!_placed)
            {
                _bar.SetPositionAndRotation(target, rot);
                _placed = true;
                return;
            }

            float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            _bar.SetPositionAndRotation(
                Vector3.Lerp(_bar.position, target, k),
                Quaternion.Slerp(_bar.rotation, rot, k));
        }
    }
}
