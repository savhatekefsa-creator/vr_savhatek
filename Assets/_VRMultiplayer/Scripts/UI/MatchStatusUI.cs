using UnityEngine;

namespace VRMultiplayer.UI
{
    /// <summary>
    /// MACIN DURUMUNU SURGUN GORUNUR KILAN oge: isinmada "hasar yok" uyarisi, baslangicta
    /// 3-2-1 geri sayimi, macin son 10 saniyesinde sayac, bitiste "MAC BITTI" — ve hepsinde SES.
    ///
    /// NEDEN VAR: mac katmani eklendiginde durumu gorebilmenin TEK yolu skorbordu acmakti
    /// (B basili tutmak). Yani oyuncu gozlugu takiyor, ates ediyor, kimse olmuyor ve sebebini
    /// bilmiyor — "oyun bozuk" saniyor. Faz bilgisi istege bagli olamaz.
    ///
    /// NISANI KAPATMAZ: durum yazisi gorus merkezinin USTUNDE (~18 derece; kullanici istegi
    /// 2026-08-05 — oyun bilgisi tepede dursun) ve MAC SIRASINDA tamamen kayboluyor. Yalnizca
    /// son 10 saniyede sayac geri geliyor — o an zaten herkes sureye bakiyor.
    ///
    /// ILK DOGUMDAN ONCE YAZI YOK: oyuncu OLU KATILIR (takim secimi/kalibrasyon lobisi) ve o
    /// asamada "ISINMA" yazisi kafa karistiriyordu. Durum satiri, yerel oyuncu EN AZ BIR KEZ
    /// dogana kadar gizli; sonra olse de acik kalir (olu oyuncu mac durumunu gormeli).
    /// Geri sayim rakamlarina dokunulmaz — onlar faz bilgisi degil oynanis bilgisidir.
    ///
    /// SES EN GUCLU KANAL: VR'da oyuncu bakmadigi bir yaziyi kacirir, sesi kacirmaz. Klipler
    /// kodda uretiliyor (bkz. <see cref="Match.MatchSounds"/>), 2B calindiklari icin herkes
    /// ayni sekilde duyar.
    ///
    /// Sahibe ozel; <see cref="PlayerHUD"/> uretir. Kill paneli gibi <c>_root</c>'un DISINDA:
    /// olu oyuncu da mac durumunu gormeli.
    /// </summary>
    public class MatchStatusUI : MonoBehaviour
    {
        [Header("Yerlesim (kafaya gore, metre)")]
        public float distance = 1.2f;
        [Tooltip("Durum yazisi: +0.38 @ 1.2 m ≈ 18 derece YUKARI — oyun bilgisi tepede, nisan " +
                 "hattinin disinda (lens kenari ~45 derecede; 42+ derece cihazda gorunmez olur).")]
        public float statusOffsetUp = 0.38f;
        [Tooltip("Geri sayim: daha merkeze yakin, cunku o an nisan alinmiyor.")]
        public float countdownOffsetUp = -0.16f;
        public float followSpeed = 9f;

        [Header("Boyut (metre, satir yuksekligi)")]
        public float statusSize = 0.024f;
        public float countdownSize = 0.11f;
        [Tooltip("Macin son saniyelerindeki sayac — geri sayimdan kucuk.")]
        public float endingSize = 0.075f;

        [Header("Sure")]
        [Tooltip("Macin son kac saniyesinde sayac ve tik-tak devreye girer.")]
        public int endingWarningSeconds = 10;

        // Kill paneliyle ayni kusak (oda geometrisi ve olum perdesi 3000'de).
        const int QText = 3051;

        Transform _root;
        TextMesh _status, _countdown;
        AudioSource _audio;
        bool _placed;

        // Ses YALNIZCA degisim aninda calinsin diye son gorulen durum saklanir.
        Match.MatchManager.Phase _lastPhase = (Match.MatchManager.Phase)255;
        int _lastWholeSecond = -1;
        float _goFlashUntil;   // "BASLA!" bu ana kadar ekranda kalir

        // Yerel oyuncu EN AZ BIR KEZ dogdu mu? Mandal: bir kez true olunca oyle kalir —
        // sonradan olen oyuncu mac durumunu gormeye devam eder (bilincli tasarim).
        bool _everSpawned;
        PlayerHealth _localHealth;

        bool LocalPlayerEverSpawned()
        {
            if (_everSpawned) return true;
            if (_localHealth == null)
            {
                var nm = Unity.Netcode.NetworkManager.Singleton;
                var po = nm != null && nm.LocalClient != null ? nm.LocalClient.PlayerObject : null;
                if (po != null) _localHealth = po.GetComponent<PlayerHealth>();
            }
            if (_localHealth != null && !_localHealth.IsDead) _everSpawned = true;
            return _everSpawned;
        }

        void Awake()
        {
            _root = new GameObject("Match Status").transform;
            _root.SetParent(transform, false);

            _status = UITheme.MakeText(_root, "", UITheme.TextMuted, statusSize,
                TextAnchor.MiddleCenter, QText);
            _status.transform.localPosition = new Vector3(0f, 0f, 0f);

            _countdown = UITheme.MakeText(_root, "", UITheme.TextPrimary, countdownSize,
                TextAnchor.MiddleCenter, QText);

            // 2B ses: konumdan bagimsiz, herkes ayni duyar. Bildirim sesi mekansal olmamali.
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.spatialBlend = 0f;
            _audio.playOnAwake = false;

            Hide();
        }

        void LateUpdate()
        {
            var m = Match.MatchManager.Instance;
            if (m == null) { Hide(); return; }

            Sync(m);
            Follow();
        }

        void Sync(Match.MatchManager m)
        {
            var phase = m.Current;
            bool phaseChanged = phase != _lastPhase;
            if (phaseChanged)
            {
                _lastPhase = phase;
                _lastWholeSecond = -1;
                if (phase == Match.MatchManager.Phase.Playing)
                {
                    Play(Match.MatchSounds.StartHorn);
                    // "BASLA!" MACIN ILK ANINDA gosterilir, geri sayimin sonunda DEGIL: sunucu
                    // sayac sifirlanir sifirlanmaz Playing'e gectigi icin oradaki kare hic
                    // gorunmezdi.
                    _goFlashUntil = Time.unscaledTime + 1.2f;
                }
                else if (phase == Match.MatchManager.Phase.Ended) Play(Match.MatchSounds.EndHorn);
            }

            // Durum SATIRI ilk dogumdan once cizilmez (lobide "ISINMA" kafa karistiriyordu);
            // geri sayim rakamlari ve sesler etkilenmez.
            bool showStatus = LocalPlayerEverSpawned();

            switch (phase)
            {
                case Match.MatchManager.Phase.Warmup:
                    SetStatus(showStatus ? "ISINMA — hasar yok, maç bekleniyor" : null, UITheme.TextMuted);
                    SetCountdown(null, 0f, Color.white);
                    break;

                case Match.MatchManager.Phase.Starting:
                {
                    SetStatus(showStatus ? "MAÇ BAŞLIYOR" : null, UITheme.AccentCyan);
                    int s = Mathf.CeilToInt(m.SecondsLeft);
                    SetCountdown(Mathf.Max(1, s).ToString(), countdownSize, UITheme.TextPrimary);
                    TickOnSecondChange(s, Match.MatchSounds.Beep);
                    break;
                }

                case Match.MatchManager.Phase.Playing:
                {
                    float left = m.SecondsLeft;
                    if (Time.unscaledTime < _goFlashUntil)
                    {
                        SetStatus(null, Color.white);
                        SetCountdown("BAŞLA!", countdownSize, UITheme.AccentCyan);
                    }
                    else if (left <= endingWarningSeconds)
                    {
                        // Son duzluk: sayac geri gelir ve her saniye tik atar.
                        SetStatus(null, Color.white);
                        int s = Mathf.CeilToInt(left);
                        SetCountdown(s.ToString(), endingSize, UITheme.TeamRedText);
                        TickOnSecondChange(s, Match.MatchSounds.Tick);
                    }
                    else
                    {
                        // Mac suruyor: EKRAN TEMIZ. Sureyi merak eden B'ye basar.
                        SetStatus(null, Color.white);
                        SetCountdown(null, 0f, Color.white);
                    }
                    break;
                }

                case Match.MatchManager.Phase.Ended:
                    SetStatus(showStatus ? "MAÇ BİTTİ" : null, UITheme.TextPrimary);
                    SetCountdown(null, 0f, Color.white);
                    break;
            }
        }

        /// <summary>Saniye hanesi her degistiginde bir kez calar. Update basina degil —
        /// yoksa saniyede 72 tik olurdu.</summary>
        void TickOnSecondChange(int second, AudioClip clip)
        {
            if (second == _lastWholeSecond) return;
            _lastWholeSecond = second;
            if (second > 0) Play(clip);
        }

        void Play(AudioClip clip)
        {
            if (_audio != null && clip != null) _audio.PlayOneShot(clip);
        }

        void SetStatus(string text, Color c)
        {
            bool on = !string.IsNullOrEmpty(text);
            if (_status.gameObject.activeSelf != on) _status.gameObject.SetActive(on);
            if (!on) return;
            if (_status.text != text) _status.text = text;
            _status.color = c;
            _status.transform.localPosition = new Vector3(0f, statusOffsetUp - CenterUp, 0f);
        }

        void SetCountdown(string text, float size, Color c)
        {
            bool on = !string.IsNullOrEmpty(text);
            if (_countdown.gameObject.activeSelf != on) _countdown.gameObject.SetActive(on);
            if (!on) return;
            if (_countdown.text != text) _countdown.text = text;
            _countdown.color = c;
            UITheme.SizeText(_countdown, size);
            _countdown.transform.localPosition = new Vector3(0f, countdownOffsetUp - CenterUp, 0f);
        }

        // Kok, durum yazisinin yuksekligine yerlesiyor; ogeler ondan sapma olarak konumlaniyor.
        float CenterUp => statusOffsetUp;

        void Hide()
        {
            if (_status != null && _status.gameObject.activeSelf) _status.gameObject.SetActive(false);
            if (_countdown != null && _countdown.gameObject.activeSelf) _countdown.gameObject.SetActive(false);
        }

        // Gorus uzayinda sabit ama SONUMLU — KillFeedUI.Follow ile ayni desen.
        void Follow()
        {
            Transform head = XRRigReference.HeadOrCamera;
            if (head == null) return;

            Vector3 target = head.position + head.forward * distance + head.up * statusOffsetUp;
            Quaternion rot = Quaternion.LookRotation(target - head.position, head.up);

            if (!_placed)
            {
                _root.SetPositionAndRotation(target, rot);
                _placed = true;
                return;
            }

            float k = 1f - Mathf.Exp(-followSpeed * Time.unscaledDeltaTime);
            _root.SetPositionAndRotation(
                Vector3.Lerp(_root.position, target, k),
                Quaternion.Slerp(_root.rotation, rot, k));
        }
    }
}
