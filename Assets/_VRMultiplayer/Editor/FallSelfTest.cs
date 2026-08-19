using System.Text;
using Unity.Netcode;
using UnityEditor;
using UnityEngine;

namespace VRMultiplayer.EditorTools
{
    /// <summary>
    /// 47b. Dusme Oz-Denetimi — dusmenin TAM zincirini gozluksuz calistirir ve olcer.
    ///
    /// NEDEN VAR. Bu ozelligin dogrulanmasi normalde bir gozluk, bir sunucu ve bir de catidan
    /// asagi yuruyecek birini gerektiriyor; yani "calisiyor mu" sorusu her seferinde saha
    /// denemesine cikiyor. Oysa zincirin gozluk gerektiren tek parcasi KAFANIN NEREDE OLDUGU.
    /// Rig'i koddan bir metre guneye koymak o parcanin yerini birebir tutar: sunucu ayni
    /// sorguyu yapar, ayni RPC gider, ayni rig duser, ayni olum uygulanir.
    ///
    /// HOST BASLATIR, SUNUCU DEGIL. Oyunun kendi akisinda PC adanmis SUNUCU olur
    /// (<see cref="LanBootstrap"/>) ve adanmis sunucunun yerel oyuncusu yoktur — dusecek kimse
    /// olmaz. Denetim bu yuzden host baslatir: tek makinede hem sunucu hem oyuncu.
    ///
    /// Sonuc <see cref="Report"/> icinde birikir; menuden calistirildiginda konsola dusar.
    /// </summary>
    public static class FallSelfTest
    {
        /// <summary>Rig'in guneye tasinacagi mesafe (m). Cati kenari orijinin ~10 cm
        /// guneyinde; 1 m boslugun icinde, kenarin belirsiz bandindan uzak.</summary>
        const float StepSouth = 1f;

        /// <summary>Dusus baslamazsa denetimin pes edecegi sure (sn).</summary>
        const float TriggerTimeout = 6f;

        /// <summary>
        /// Rig'in kaldirilacagi yukseklik (m) — gozluksuz "ayakta duran kafa".
        ///
        /// SIFIR KAFA ILE AVATAR HIC OLCULEMEZ: AvatarIKController, kafa dosemeden en az
        /// trackingReadyMinHeight (1 m) yukselene kadar govdeyi ve IK'yi TAMAMEN dondurur
        /// (spawn anindaki "ucma/yere batma" hatasina karsi konmus bir kapi). Rig yerde
        /// dururken avatar zaten hic kimildamaz, yani "avatar dusuyor mu" sorusu olculemez.
        /// Rig'i bir insan boyuna kaldirmak o kapiyi gercek oyundaki gibi acar.
        /// </summary>
        const float HeadStandHeight = 1.7f;

        /// <summary>Rig kaldirildiktan sonra boy kalibrasyonunun oturmasi icin beklenen sure
        /// (sn). Kilitlenmeden dusulurse govde takibi 1,7 m sasar — hata degil ama olcum
        /// kirlenir.</summary>
        const float SettleSeconds = 1.5f;

        /// <summary>
        /// Denetimin deneyecegi ilk port. Oyunun kendi portu (7777) bilerek kullanilmaz:
        /// olculdu — arka planda duran ya da yeni kapanmis bir sunucu o portu tutuyorken
        /// StartHost "address already in use" ile dusuyor ve denetim, dusme bozuk sanip
        /// yanlis alarm veriyor.
        /// </summary>
        const ushort TestPortFirst = 7911;

        /// <summary>
        /// Kac port denenecek. TEK PORT YETMIYOR: Netcode'un Play modu cikisindaki kapanisi
        /// her zaman temiz degil (kapanista kendi NullReference'larini basiyor) ve soket
        /// editor surecinde asili kalabiliyor. Ikinci kosuda 7911 de doluydu. Sirayla
        /// denemek, denetimi onceki kosunun artiklarindan bagimsiz kilar.
        /// </summary>
        const int TestPortTries = 8;

        /// <summary>Dusus basladiktan sonra izlenecek azami sure (sn).</summary>
        const float WatchTimeout = 12f;

        enum Step { Idle, WaitNetwork, WaitPlayer, Arm, Settle, Watch, Done }

        static Step _step = Step.Idle;
        static double _stepStart;
        static Vector3 _rigStart;      // dususun olculecegi referans (kaldirilmis rig)
        static Vector3 _rigOriginal;   // denetim oncesi rig konumu — sonunda buraya donulur
        static Transform _rig;
        static Transform _avatar;      // ag avatarinin govde koku (AvatarIKController'in surdugu)
        static float _avatarLowest;
        static float _avatarStartY;
        static PlayerHealth _health;
        static readonly StringBuilder _log = new StringBuilder();

        static float _lowest;          // ulasilan en dusuk rig yuksekligi
        static float _prevY;           // onceki karedeki yukseklik (dip tespiti icin)
        static double _dropStartedAt;  // rig'in ilk kez asagi gittigi an
        static double _lastDescentAt;  // rig'in EN SON alcaldigi an = dip
        static double _returnedAt;     // ciktigi yukseklige dondugu an
        static bool _sawDeath;
        static bool _rigCaptured;      // _rigStart gercekten olculdu mu (0,0,0 gecerli bir deger)
        static int _portAttempt;       // kacinci port deneniyor
        static double _nextSampleAt;   // zincir ornegi yazma ani

        public static string Report => _log.ToString();
        public static bool Done => _step == Step.Done;

        [MenuItem("Tools/VR Multiplayer/47b. Dusme Oz-Denetimi (Play modunda calistir)")]
        public static void Menu()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("Dusme Oz-Denetimi",
                    "Once Play moduna gir, sonra bu menuyu calistir.", "Tamam");
                return;
            }
            Begin();
        }

        /// <summary>Denetimi baslatir. Betikten (ve MCP'den) cagrilabilsin diye menuden ayri.</summary>
        public static void Begin()
        {
            _log.Length = 0;
            _step = Step.WaitNetwork;
            _stepStart = EditorApplication.timeSinceStartup;
            _lowest = 0f;
            _dropStartedAt = _lastDescentAt = _returnedAt = 0d;
            _sawDeath = false;
            _rigCaptured = false;
            _portAttempt = 0;
            _nextSampleAt = 0d;
            _health = null;
            _rig = null;

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            Line("=== DUSME OZ-DENETIMI ===");
            var hz = FallHazard.Instance;
            Line(hz == null
                ? "HATA: FallHazard.Instance yok — ilan ya kurulu degil (menu 47) ya da harita " +
                  "anahtariyla KAPATILDI (eski zemindeysen normal; catiya donmek icin menu 55)."
                : $"Ilan var: seviye {hz.walkableLevel:0.00} m, esik {hz.maxStepDown:0.00} m, " +
                  $"pay {hz.graceSeconds:0.00} sn, bekleme {hz.refallCooldownSeconds:0.00} sn.");
            if (hz == null) Finish();
        }

        static void Tick()
        {
            if (!Application.isPlaying) { Line("Play modundan cikildi — denetim yarida kesildi."); Finish(); return; }

            double now = EditorApplication.timeSinceStartup;
            double inStep = now - _stepStart;

            switch (_step)
            {
                case Step.WaitNetwork: TickWaitNetwork(inStep); break;
                case Step.WaitPlayer: TickWaitPlayer(inStep); break;
                case Step.Arm: TickArm(); break;
                case Step.Settle: TickSettle(inStep); break;
                case Step.Watch: TickWatch(now, inStep); break;
            }
        }

        static void TickWaitNetwork(double inStep)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null)
            {
                if (inStep > 5d) { Line("HATA: NetworkManager yok."); Finish(); }
                return;
            }

            if (inStep > 20d) { Line("HATA: ag 20 sn icinde kurulamadi."); Finish(); return; }

            // Basarisiz bir StartHost'tan sonra Netcode kendini kapatir; kapanis bitmeden
            // yeniden denemek ayni hataya duser.
            if (nm.ShutdownInProgress) return;

            if (!nm.IsListening)
            {
                if (_portAttempt >= TestPortTries)
                {
                    Line($"HATA: {TestPortFirst}-{TestPortFirst + TestPortTries - 1} " +
                         "araligindaki portlarin hicbirine baglanilamadi.");
                    Finish();
                    return;
                }

                // Oyunun kendi dugmesi adanmis SUNUCU baslatiyor; denetim host istiyor.
                ushort port = (ushort)(TestPortFirst + _portAttempt);
                _portAttempt++;

                var utp = nm.NetworkConfig.NetworkTransport as Unity.Netcode.Transports.UTP.UnityTransport;
                if (utp != null) utp.SetConnectionData("127.0.0.1", port);

                if (!nm.StartHost())
                {
                    Line($"port {port} dolu — sonraki deneniyor.");
                    return;   // bir sonraki tick'te sonraki port
                }
                Line($"Host baslatildi (port {port}) — sunucu + yerel oyuncu.");
            }
            else if (!nm.IsHost)
            {
                Line("HATA: ag zaten SUNUCU olarak calisiyor, yerel oyuncu yok. " +
                     "Play'den cikip tekrar dene (sunucu dugmesine BASMA).");
                Finish();
                return;
            }

            Go(Step.WaitPlayer);
        }

        static void TickWaitPlayer(double inStep)
        {
            var nm = NetworkManager.Singleton;
            var po = nm != null && nm.IsListening ? nm.LocalClient.PlayerObject : null;
            if (po == null)
            {
                if (inStep > 8d) { Line("HATA: yerel oyuncu 8 sn icinde spawn olmadi."); Finish(); }
                return;
            }

            _health = po.GetComponent<PlayerHealth>();
            if (_health == null) { Line("HATA: oyuncuda PlayerHealth yok."); Finish(); return; }

            Line($"Yerel oyuncu spawn oldu: {po.name} (client {po.OwnerClientId}).");
            Go(Step.Arm);
        }

        static void TickArm()
        {
            // Oyuncu OLU dogar ve dogum cemberinde bekleyerek dirilir; sahnede cember yok, ve
            // takim secilmeden guvenlik agi bile islemiyor (TickSpawn team==0'da eriyor).
            // Denetim iki adimi da koddan atlar: takim ver, ayaga kaldir.
            var id = _health.GetComponent<PlayerIdentity>();
            if (id != null && id.Team.Value == 0) id.Team.Value = 1;
            _health.ServerResetForMatch();
            Line($"Oyuncu ayaga kaldirildi (takim {(id != null ? id.Team.Value : 0)}, olu={_health.IsDead}).");

            // MAC FAZI ZORLANIR. ServerApplyDamage yalnizca Playing fazinda hasar gecirir; mac
            // baslatilmadan yapilan bir denetim dususu gorur ama OLUMU goremez ve "calisiyor"
            // diye yanlis rapor verir. Ilk kosuda tam bu oldu — zincirin yarisi olculmustu.
            var mm = Match.MatchManager.Instance;
            if (mm != null && !Match.MatchManager.DamageAllowed)
            {
                // FAZIN BITISI DE YAZILMALI. Yalnizca PhaseRaw'i Playing yapmak yetmiyor:
                // MatchManager.Update "now >= PhaseEndsAt" gorup maci ayni karede BITIRIYOR
                // (PhaseEndsAt warmup'tan 0 kaliyor), hasar kapisi carpmadan once kapaniyor ve
                // denetim "olum uygulanmadi" diyor. Olculdu — ilk iki kosu tam bu yuzden yanildi.
                mm.PhaseRaw.Value = (byte)Match.MatchManager.Phase.Playing;
                mm.PhaseEndsAt.Value = NetworkManager.Singleton.ServerTime.Time + 600d;
                Line($"Mac fazi denetim icin Playing'e alindi (hasara izin: {Match.MatchManager.DamageAllowed}).");
            }

            var rigRef = XRRigReference.Instance;
            if (rigRef == null) { Line("HATA: sahnede XR rig yok."); Finish(); return; }

            _rig = rigRef.transform;
            _rigOriginal = _rig.position;

            // Gozluksuz kafa y=0'da kalir; avatarin IK kapisi acilmaz ve govde HIC kimildamaz.
            // Rig'i bir insan boyuna kaldirmak o kapiyi gercek oyundaki gibi acar.
            _rig.position = _rigOriginal + new Vector3(0f, HeadStandHeight, 0f);
            _rigStart = _rig.position;
            _rigCaptured = true;
            _lowest = _rigStart.y;
            _prevY = _rigStart.y;

            _avatar = null;
            var ik = _health.GetComponentInChildren<AvatarIKController>();
            if (ik != null) { _avatar = ik.transform; _avatarStartY = _avatar.position.y; }
            _avatarLowest = _avatar != null ? _avatarStartY : 0f;

            Line($"Rig ayakta kafa yuksekligine alindi ({HeadStandHeight:0.00} m). " +
                 $"Avatar govdesi: {(_avatar == null ? "BULUNAMADI" : _avatar.name)}");
            Go(Step.Settle);
        }

        /// <summary>Boy kalibrasyonu otursun diye kisa bir bekleme, sonra bosluga adim.</summary>
        static void TickSettle(double inStep)
        {
            if (inStep < SettleSeconds) return;

            var ik = _health != null ? _health.GetComponentInChildren<AvatarIKController>() : null;
            Line($"Avatar boy kalibrasyonu: {(ik == null ? "avatar yok" : (ik.FitLocked ? $"kilitli ({ik.StandingHeight:0.00} m)" : "KILITLENMEDI"))}");
            if (_avatar != null) _avatarStartY = _avatarLowest = _avatar.position.y;

            Vector3 target = _rigStart + new Vector3(0f, 0f, -StepSouth);
            _rig.position = target;
            Line($"Rig {StepSouth:0.0} m guneye tasindi: {_rigStart} -> {target}");
            Line($"Beklenen: ~{FallHazard.Instance.graceSeconds:0.00} sn sonra dusus baslar.");

            Go(Step.Watch);
        }

        static void TickWatch(double now, double inStep)
        {
            float y = _rig.position.y;
            if (y < _lowest) _lowest = y;
            if (_avatar != null && _avatar.position.y < _avatarLowest) _avatarLowest = _avatar.position.y;

            // ZINCIRIN HER HALKASI AYRI AYRI YAZILIR: rig -> ag kafasi -> avatar govdesi.
            // "Avatar inmedi" tek basina hangi halkanin koptugunu soylemiyor; kafa da inmediyse
            // sorun kopyalamada, kafa indi ama govde inmediyse sorun IK'nin govde yuksekliginde.
            if (_dropStartedAt != 0d && now >= _nextSampleAt)
            {
                _nextSampleAt = now + 0.4d;
                var ikNow = _health != null ? _health.GetComponentInChildren<AvatarIKController>() : null;
                string headY = ikNow != null && ikNow.headSource != null
                    ? ikNow.headSource.position.y.ToString("0.00") : "?";

                // Kapinin durumu ve govdenin X/Z'si birlikte okunur: X/Z kafayi takip ediyorsa
                // LateUpdate CALISIYOR ve sorun yalnizca Y'de; X/Z de donmussa LateUpdate erken
                // donuyor demektir ve aranacak yer konum satiri degil, ustundeki kapilardir.
                string gate = "?", av = "?";
                if (ikNow != null)
                {
                    var fi = typeof(AvatarIKController).GetField("_trackingValid",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    gate = fi == null ? "?" : fi.GetValue(ikNow).ToString();
                }
                if (_avatar != null)
                    av = $"({_avatar.position.x:0.00},{_avatar.position.y:0.00},{_avatar.position.z:0.00})";

                Line($"   ornek [{inStep:0.00}] rig={y:0.00}  agKafasi={headY}  avatar={av}  izlemeGecerli={gate}");
            }

            // DIP, "en dusuk noktaya deginca" DEGIL "artik alcalmiyorken"dir. Ilk halinde dip
            // tespiti dususun HEMEN basinda ateslendi (o an y zaten _lowest'a esitti) ve
            // 3 saniyelik dususu 0,78 sn diye raporladi. Alcalmanin SON anini tutmak, dususun
            // bittigi ani hicbir esik secmeden verir.
            if (y < _prevY - 0.001f) _lastDescentAt = now;
            _prevY = y;

            if (_dropStartedAt == 0d && y < _rigStart.y - 0.25f)
            {
                _dropStartedAt = now;
                Line($"[{inStep:0.00} sn] DUSUS BASLADI (rig y = {y:0.00}).");
            }

            if (!_sawDeath && _health != null && _health.IsDead)
            {
                _sawDeath = true;
                Line($"[{inStep:0.00} sn] OLUM uygulandi (can {_health.Health.Value}).");
            }

            if (_dropStartedAt != 0d && _returnedAt == 0d && Mathf.Abs(y - _rigStart.y) < 0.01f
                && now - _dropStartedAt > 0.5d)
            {
                _returnedAt = now;
                Line($"[{inStep:0.00} sn] GERI DONDU: rig y = {y:0.00} " +
                     $"(cikis {_rigStart.y:0.00}, fark {Mathf.Abs(y - _rigStart.y):0.0000} m).");
                Summarise();
                Finish();
                return;
            }

            if (_dropStartedAt == 0d && inStep > TriggerTimeout)
            {
                Line($"HATA: {TriggerTimeout:0} sn gecti, dusus BASLAMADI.");
                Diagnose();
                Finish();
                return;
            }

            if (inStep > WatchTimeout)
            {
                Line($"HATA: {WatchTimeout:0} sn gecti, dusus tamamlanmadi " +
                     $"(en dusuk y = {_lowest:0.00}, su an {y:0.00}).");
                Finish();
            }
        }

        /// <summary>Dusus baslamadiysa SEBEBI soyler — "olmadi" demek tek basina ise yaramaz.</summary>
        static void Diagnose()
        {
            var hz = FallHazard.Instance;
            Line("--- teshis ---");
            Line("FallHazard.Instance: " + (hz == null ? "YOK" : "var"));
            if (_health != null)
            {
                Line($"oyuncu olu mu: {_health.IsDead}   kafa: {_health.HeadPosition}");
                if (hz != null)
                    Line($"kafanin altinda zemin var mi: {hz.HasGroundUnder(_health.HeadPosition)}");
            }
            Line("rig: " + (_rig == null ? "yok" : _rig.position.ToString()));
            Line("hasara izin (MatchManager): " + Match.MatchManager.DamageAllowed);
        }

        static void Summarise()
        {
            var hz = FallHazard.Instance;
            float drop = _rigStart.y - _lowest;
            double fall = _lastDescentAt > 0d ? _lastDescentAt - _dropStartedAt : 0d;
            double expected = hz != null ? Mathf.Sqrt(2f * drop / hz.gravity) : 0d;

            float avatarDrop = _avatar != null ? _avatarStartY - _avatarLowest : 0f;

            Line("--- ozet ---");
            Line($"dusus mesafesi : {drop:0.00} m");
            Line($"avatar govdesi : {(_avatar == null ? "olculemedi" : $"{avatarDrop:0.00} m indi")}" +
                 $"   {(_avatar != null && avatarDrop > drop * 0.9f ? "(kafayi takip etti)" : "(TAKIP ETMEDI)")}");
            Line($"dusus suresi   : {fall:0.00} sn   (serbest dusus beklentisi {expected:0.00} sn," +
                 $" olcum dususun ilk 0,25 m'sini kaciriyor)");
            Line($"olum           : {(_sawDeath ? "uygulandi" : "UYGULANMADI")}");
            Line($"geri donus     : {(_returnedAt > 0d ? "tam" : "OLMADI")}");
        }

        static void Go(Step s)
        {
            _step = s;
            _stepStart = EditorApplication.timeSinceStartup;
        }

        static void Finish()
        {
            EditorApplication.update -= Tick;

            // Rig'i denetimden once neredeyse oraya birak — yarida kesilen bir denetim
            // oyuncuyu 45 m yerin altinda (ya da boslugun uzerinde) birakmasin.
            //
            // _rigCaptured bayragi sart: "_rigStart != Vector3.zero" diye yazilmisti ve rig
            // TAM ORIJINDE oldugu icin geri koyma sessizce atlandi — denetim bitti, rig
            // boslugun uzerinde kaldi. Sifir gecerli bir konumdur, "olculmedi" isareti degil.
            if (_rig != null && _rigCaptured) _rig.position = _rigOriginal;

            _step = Step.Done;
            Debug.Log(_log.ToString());
        }

        static void Line(string s) => _log.AppendLine(s);
    }
}
