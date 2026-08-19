using Unity.Netcode;
using UnityEngine;

namespace VRMultiplayer
{
    /// <summary>
    /// <see cref="PlayerHealth"/>'in DUSME bolumu: catidan cikan oyuncu gercek yercekimiyle
    /// asagi iner, carptigi yerde olur, sonra ciktigi noktaya DIKEY olarak geri gelir ve
    /// kendi dogum bolgesine yuruyerek doner.
    ///
    /// NEDEN AYRI DOSYA AMA AYRI BILESEN DEGIL. Bu is bir <c>PlayerFall</c> bileseni olmak
    /// isterdi, ama Netcode bir NetworkObject spawn olduktan SONRA eklenen NetworkBehaviour'u
    /// tanimaz (bilesen sirasi spawn aninda sabitlenir) — yani <see cref="Audio.PlayerAudio"/>
    /// gibi calisma aninda eklenemezdi ve NetworkPlayer prefabina elle konmasi gerekirdi.
    /// Prefabi sihirbaz (menu 1) yeniden kuruyor; oraya konan her bilesen bir sonraki kurulumda
    /// sessizce kaybolur. PlayerHealth zaten prefabda ve zaten NetworkBehaviour, o yuzden
    /// dusme onun bir parcasi — ama kendi dosyasinda.
    ///
    /// OYUNCU GERCEKTEN DUSMEZ, GORUNTU DUSER. Bu oyunda hareket tamamen fiziksel; rig'i
    /// koddan oynatmak kalibrasyonu bozdugu icin locomotion silinmisti
    /// (bkz. VRMultiplayerSetup "5. Setup Colocation"). Dusus bu kurali DELMEZ, cunku
    /// tamamen DIKEY: kalibrasyonun kurdugu sey yatay eslemedir (gercek odadaki 1 m = sanal
    /// 1 m, ve herkes ayni cercevede). Rig duz asagi inerken o esleme aynen durur, yalnizca
    /// yukseklik degisir; ofset olurken uygulanir, dirilmeden once tam olarak geri alinir.
    ///
    /// BASKALARI GERCEK BIR DUSUS GORUR, bedavaya: sahip her karede kamerasini ag avatarinin
    /// kafasina kopyaliyor (<see cref="NetworkVRPlayer"/>), yani rig asagi inince avatar da
    /// iner ve owner-authoritative NetworkTransform bunu herkese tasir. Dusus icin tek bir
    /// ekstra RPC yok.
    /// </summary>
    public partial class PlayerHealth
    {
        // ------------------------------------------------------------- sunucu tarafi durum

        /// <summary>Bosluk uzerinde KESINTISIZ gecen sure. Zemine donunce sifirlanir —
        /// kenara egilip cekilmek dususu baslatmamali.</summary>
        float _voidTimer;

        bool _falling;      // dusus surecte mi
        bool _impacted;     // yere carpti mi (olum uygulandi mi)
        float _impactAt;    // carpma ani (Time.time)
        float _returnAt;    // dipte bekleme bitisi (Time.time)
        float _holdSeconds; // dipte bekleme (BeginFall'da ilandan kopyalanir)
        float _cooldownSeconds;

        /// <summary>Bir sonraki dususun mumkun oldugu an — dongu kirici (bkz. TickFall).</summary>
        float _refallReadyAt;

        // ------------------------------------------------------------- sahip tarafi (yerel rig)

        /// <summary>Emniyet kemerinin, beklenen dusus suresinin uzerine ekledigi pay (sn).
        /// Dipte bekleme (0,5) + ag aksakligi icin comert tutuldu: erken tetiklenen bir
        /// kemer, oyuncuyu dususun ortasinda yukari isinlar.</summary>
        const float WatchdogGraceSeconds = 5f;

        bool _rigFalling;
        float _rigWatchdogAt; // donus emri gelmezse rig'in kendini geri alacagi an
        float _rigVelocity;   // anlik dusus hizi (m/sn)
        float _rigApplied;    // rig'e uygulanmis toplam dikey ofset (m, pozitif = asagi)
        float _rigDistance;   // bu dususte inilecek toplam mesafe (m)
        float _rigGravity = 9.81f;

        // ------------------------------------------------------------- sunucu

        /// <summary>Server-only. <see cref="Update"/> her karede cagirir.</summary>
        void TickFall()
        {
            // AG KAPANIYORSA HICBIR SEY BASLATMA. IsSpawned bir kare daha true kalabiliyor
            // (Play modundan cikis, host dususu, editorde oynarken derleme) ve o karede
            // gonderilen RPC "Rpc methods can only be invoked after starting the NetworkManager!"
            // diye patliyor. Olculdu: Play surerken yapilan bir derleme tam bunu uretti.
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening) { _voidTimer = 0f; return; }

            // SUREN BIR DUSUS HER SEYDEN ONCE BITIRILIR — ilan kontrolunun bile ONUNDE.
            // Ilan dusus ortasinda silinir ya da kapatilirsa (harita degisimi, sahne
            // duzenleme, bileseni kapatmak) asagidaki null kontrolu dususu yarida keserdi:
            // EndFallRpc hic gonderilmez ve oyuncu 45 m yerin altinda SONSUZA KADAR asili
            // kalirdi. Bitis bu yuzden ilana hic dokunmuyor; ihtiyaci olan iki sureyi
            // BeginFall onbellege aliyor.
            if (_falling) { TickFalling(); return; }

            var hazard = FallHazard.Instance;
            if (hazard == null) { _voidTimer = 0f; return; }   // bu haritada ucurum yok

            // OLU OYUNCU DUSMEZ. Tek satir ama tasidigi sey bu ozelligin kilidi: dusus bitince
            // oyuncu tam ciktigi noktaya, yani BOSLUGUN USTUNE geri donuyor. Olulerde kontrol
            // acik kalsaydi orada aninda yeniden duser, yine olur, yine donerdi — sonsuz dongu.
            // Olu oyuncu havada durur ve kendi bolgesine yuruyerek doner; dogum modelinin
            // zaten istedigi sey bu.
            if (Dead.Value) { _voidTimer = 0f; return; }

            // DUSUS ISINMADA DA ISLER, ve bu bilincli bir duzeltme. Ilk halinde kontrol
            // MatchManager.DamageAllowed kapisinin arkasindaydi — yani yalnizca Playing
            // fazinda. Sonucu sahada su oldu: mac hic baslatilmadigi icin oyuncu catidan
            // cikti ve HICBIR SEY olmadi. Oysa isinma, insanlarin kenari ilk kez denedigi
            // an; ucurumun orada sessizce yok olmasi ozelligin kendisini goze gorunmez yapiyor.
            //
            // Kapinin asil sebebi sonsuz dongu korkusuydu: isinmada ServerApplyDamage hasari
            // gecirmez, oyuncu olmez, boslugun ustune geri doner ve ayni karede yine duserdi.
            // Onu artik BEKLEME SURESI cozuyor (refallCooldownSeconds) — dongu kirilir ama
            // ucurum her fazda gercek kalir. Isinmada dusen oyuncu olmez, sadece duser ve doner.
            if (Time.time < _refallReadyAt) { _voidTimer = 0f; return; }

            Vector3 head = HeadPosition;
            if (hazard.HasGroundUnder(head)) { _voidTimer = 0f; return; }

            _voidTimer += Time.deltaTime;
            if (_voidTimer < hazard.graceSeconds) return;

            BeginFall(hazard, head);
        }

        void BeginFall(FallHazard hazard, Vector3 head)
        {
            float landingY;
            float distance = hazard.TryFindLanding(head, out landingY)
                ? Mathf.Max(0f, hazard.walkableLevel - landingY)
                : hazard.MaxFallDistance;

            // SURE MESAFEDEN CIKAR, SABIT DEGILDIR. "Gercekte nasil dusuyorsa" demek, altinda
            // ne varsa ona inmek demek: RooftopArena'da catidan asfalta 44,8 m -> 3,02 sn,
            // alcak bina kusaginin damina 18,6 m -> 1,95 sn. Emniyet tavani asilirsa mesafe de
            // tavana kirpilir, yoksa oyuncu havada durup bir sure sonra olurdu.
            float duration = Mathf.Sqrt(2f * distance / Mathf.Max(0.01f, hazard.gravity));
            if (duration > hazard.maxFallSeconds)
            {
                duration = hazard.maxFallSeconds;
                distance = 0.5f * hazard.gravity * duration * duration;
            }

            _voidTimer = 0f;
            _falling = true;
            _impacted = false;
            _impactAt = Time.time + duration;

            // Bitis icin gereken iki sure SIMDI kopyalanir: TickFalling ilana bakmaz
            // (yukaridaki siralama notu).
            _holdSeconds = hazard.groundHoldSeconds;
            _cooldownSeconds = hazard.refallCooldownSeconds;

            // Mesafe ve yercekimi sahibe gonderilir, KONUM gonderilmez: iki taraf ayni
            // yercekimini ayni mesafeye uygular, yani sonuc kendiliginden ayni. Dusus boyunca
            // konum paketi akmasina gerek yok — avatarin inisi zaten NetworkTransform'dan gidiyor.
            BeginFallRpc(distance, hazard.gravity);

            Debug.Log($"[PlayerHealth] Oyuncu {OwnerClientId} bosluga cikti: " +
                      $"{distance:0.0} m, {duration:0.00} sn.");
        }

        void TickFalling()
        {
            if (!_impacted && Time.time >= _impactAt)
            {
                _impacted = true;
                _returnAt = Time.time + _holdSeconds;

                // KAYNAGI OLMAYAN OLUM. NoAttacker tam da bunun icin ayrilmisti (bkz. kendi
                // yorumu: "dusme, harita disi"). Kill paneli katilsiz bir satir yazar, kimseye
                // puan gitmez, kurbanin olum sayisi artar.
                ServerApplyDamage(MaxHealth, NoAttacker);
            }

            if (_impacted && Time.time >= _returnAt)
            {
                _falling = false;
                _refallReadyAt = Time.time + _cooldownSeconds;
                EndFallRpc();
            }
        }

        // ------------------------------------------------------------- sahip

        [Rpc(SendTo.Owner)]
        void BeginFallRpc(float distance, float gravity)
        {
            _rigDistance = distance;
            _rigGravity = gravity;
            _rigVelocity = 0f;
            _rigFalling = true;

            // Emniyet kemerinin kurulmasi: beklenen dusus suresi + comert bir pay.
            float expected = Mathf.Sqrt(2f * distance / Mathf.Max(0.01f, gravity));
            _rigWatchdogAt = Time.time + expected + WatchdogGraceSeconds;
        }

        [Rpc(SendTo.Owner)]
        void EndFallRpc() => RestoreRig();

        /// <summary>
        /// Rig'i yercekimiyle indirir. LateUpdate'te, cunku tag kalibrasyonu rig'e Update'te
        /// yaziyor — once o duzeltsin, ofset onun USTUNE binsin.
        /// </summary>
        void LateUpdate()
        {
            if (!IsOwner) return;

            if (!_rigFalling)
            {
                // EMNIYET KEMERI. Dibe indik ama donus emri hic gelmediyse rig kendi kendini
                // geri alir. Sunucu tarafi EndFallRpc'yi gonderemeyebilir (baglanti kopmasi,
                // sunucunun oyuncu nesnesinin erken yok olmasi) ve o durumda oyuncu haritanin
                // 45 m altinda kalir — gozlukte bunun oyunu kapatmaktan baska cikisi yok.
                if (_rigApplied > 0.001f && Time.time > _rigWatchdogAt)
                {
                    Debug.LogWarning("[PlayerHealth] Dusus donus emri gelmedi — rig kendi " +
                                     "kendine geri alindi.");
                    RestoreRig();
                }
                return;
            }

            var rig = XRRigReference.Instance;
            if (rig == null) { _rigFalling = false; return; }

            _rigVelocity += _rigGravity * Time.deltaTime;
            float next = Mathf.Min(_rigDistance, _rigApplied + _rigVelocity * Time.deltaTime);
            ApplyRigDrop(rig.transform, next);

            // Dibe indi: donus emrini (EndFallRpc) beklerken burada durur.
            if (next >= _rigDistance) _rigFalling = false;
        }

        void ApplyRigDrop(Transform rig, float offset)
        {
            // FARK YAZILIR, MUTLAK KONUM DEGIL. Tag kalibrasyonu ayni karede rig'in X/Z'sini
            // duzeltmis olabilir; mutlak bir konum yazmak o duzeltmeyi sessizce ezer ve oyuncu
            // dususten sonra gercek odaya gore kaymis olarak cikardi — kolokasyonun bozulmasi
            // tam olarak budur.
            float delta = offset - _rigApplied;
            if (Mathf.Abs(delta) < 1e-5f) return;

            rig.position += Vector3.down * delta;
            _rigApplied = offset;

            // Tag'in DIKEY duzeltmesi sussun: rig asagidayken tag de asagida olculur ve
            // duzeltme oyuncuyu dususun ortasinda yukari cekerdi (bkz. FallHazard).
            FallHazard.SuppressVerticalCalibration = _rigApplied > 0.001f;
        }

        /// <summary>
        /// Rig'i ciktigi yukseklige DIKEY olarak geri koyar. Uygulanan ofsetin tam tersi
        /// yazilir, "y = 0" gibi mutlak bir deger degil: aradaki farki kalibrasyon belirliyor
        /// olabilir ve mutlak yazmak oyuncuyu gozlugun zemin tahminine geri iterdi.
        /// </summary>
        void RestoreRig()
        {
            _rigFalling = false;
            _rigVelocity = 0f;

            var rig = XRRigReference.Instance;
            if (rig != null && Mathf.Abs(_rigApplied) > 1e-5f)
                rig.transform.position += Vector3.up * _rigApplied;

            _rigApplied = 0f;
            FallHazard.SuppressVerticalCalibration = false;
        }

        /// <summary>Baglanti koparsa ya da sahne kapanirsa rig 45 m yerin altinda kalmasin.</summary>
        public override void OnNetworkDespawn()
        {
            RestoreRig();
        }

        void OnDisable()
        {
            if (_rigApplied != 0f) RestoreRig();
        }
    }
}
