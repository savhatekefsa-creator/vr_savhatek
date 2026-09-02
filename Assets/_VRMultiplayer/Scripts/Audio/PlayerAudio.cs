using UnityEngine;
using UnityEngine.XR;

namespace VRMultiplayer.Audio
{
    /// <summary>
    /// Oyuncuya bagli sesler. PlayerHealth spawn'da calisma aninda ekler — prefab
    /// degisikligi YOK (sihirbaz prefab'i sifirdan kursa da calismaya devam eder).
    ///
    /// 1) AYAK SESI: KAFANIN yatay yer degistirmesi adim sayacinda birikir; her adim
    ///    boyunda (~0.72 m) bir klip, oyuncunun AYAK hizasinda 3D calinir.
    ///
    ///    NEDEN KAFA, NEDEN KOK DEGIL: bu bilesen oyuncu KOKUNDE durur ama kok hicbir
    ///    zaman kimildamaz. Bu oyunda lokomosyon yok (oyuncular gercek odada fiziksel
    ///    olarak yuruyor) ve poz yalnizca Head/LeftHand/RightHand cocuklarinda,
    ///    ClientNetworkTransform ile replike oluyor; KOKTE NetworkTransform YOK. Onceki
    ///    surum transform.position'i, yani kokun konumunu izliyordu: fark her karede 0
    ///    cikiyor, MinWalkSpeed esiginin altinda kaliyor ve sayac bosaltiliyordu. Sonuc,
    ///    hicbir adim sesinin CALMAMASIYDI. Ayni tuzaga PlayerHealth de dusmus ve dogum
    ///    cemberi icin HeadPosition'a gecmisti; burasi o duzeltmeden pay almamisti.
    ///
    ///    KENDI ADIMIMIZ CALINMAZ. Fiziksel olarak yuruduugumuz icin gercek ayak sesimizi
    ///    zaten duyuyoruz (Quest'in hoparlorleri acik, odayi kesmiyor) — sentetik olan
    ///    onun yerine gecmez, ustune biner. Ustelik senkronu tutamaz: bu sayac ayagin
    ///    TEMAS ANINI degil yol alinan MESAFEYI olcer, yani faz her adimda rastgele kayar.
    ///    Asil sebep ise su: adim sesinin bu oyundaki tek isi "yakinimda biri var, SU
    ///    YONDE" demek. Kendi adimimiz yonsuz calacagi icin tam da o sinyalin taklidi olur
    ///    ve her hareket ettigimizde kendimize yanlis alarm uretir.
    ///
    ///    Isinlanma/spawn sicramasi (karede >1.5 m) ve yavas kafa sallantisi (0.6 m/s
    ///    alti) adim SAYILMAZ — durup dururken hayalet adim olmaz.
    ///
    ///    ZEMIN FARKINDALIGI: adim aninda ayagin altinda bir <see cref="FootSurface"/> varsa
    ///    (cam kirigi yamasi gibi) klip, siddet ve MENZIL oradan gelir. Bunun ag maliyeti
    ///    SIFIR: kaplama her istemcide harita duzeninden yerel kuruluyor ve kokun konumu
    ///    zaten replike, yani her istemci uzaktaki oyuncunun cama bastigini kendi basina
    ///    hesapliyor. Yavas yurumek de bedava calisiyor — 0.6 m/s alti zaten adim saymiyor,
    ///    yani camin uzerinden sessizce gecmek kendiliginden mumkun.
    ///
    /// 2) VUCUDA MERMI SESI ("tik/puf"): can dususu her istemcide replike olsa da ses
    ///    YALNIZ hasari alanin kendi kulakliginda (2D) calar — kisiye ozel geri bildirim.
    ///    Siddet hasar miktariyla olceklenir: siyrik hafif "tik", agir isabet dolgun "puf".
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerAudio : MonoBehaviour
    {
        const float StrideMeters = 0.72f;      // iki adim sesi arasi yatay yol
        const float TeleportThreshold = 1.5f;  // karede bundan uzun sicrama = isinlanma, adim degil

        // ─── HAREKET AYIKLAMA ─────────────────────────────────────────────────────────
        // Onceki tasarim iki yerde birden yaniliyordu ve adim sesi CIHAZDA HIC CALMIYORDU.
        // Olculdu (2026-08-31, [AdimOlcum] loglari, oyuncu normal yuruyusteyken):
        //   tepeHiz 0.62-0.90 m/s   esikUstuKare %3-7   birikim 0.00/0.72 m
        //
        // 1) ESIK COK YUKSEKTI. 0.6 m/s, 3 metrelik bir odayi 5 saniyede KESINTISIZ
        //    yurumek demek; oda olceginde VR'da kimse oyle hareket etmiyor. Tepe hiz
        //    esigi geciyordu ama karelerin ancak %3-7'si. Yeni esik 0.28 m/s: oda ici
        //    hareketi yakalar, dururken kafa sallantisini hala eler (olculen sallanti
        //    tipik olarak bunun cok altinda).
        //
        // 2) BOSALTMA YURUME HIZIYLA AYNI MERTEBEDEYDI. Esigin altindaki her kare
        //    _accum'u StrideMeters*dt = 0.72 m/s hiziyla siliyordu. Aritmetigi:
        //      kazanc %5 x 0.7 = +0.035 m/s,  kayip %95 x 0.72 = -0.684 m/s
        //    Yani sayac 0'da cakili kaliyordu. Iki adim atip bir saniye durmak, kazanilan
        //    yolun TAMAMINI siliyordu.
        //
        // Yeni davranis: sayac YURUNEN YOLU biriktirir (adim sayacinin isi zaten bu) ve
        // hic sizdirmaz. Hayalet adima karsi koruma bosaltma degil, HAREKETSIZLIK
        // ZAMANLAYICISI: esigin altinda araliksiz IdleResetSeconds gecerse sayac sifirlanir.
        // Boylece duraklama ilerlemeyi silmez, ama dakikalarca kipirdamadan durup damla
        // damla dolmus bir sayac da adim uretmez.
        const float MinWalkSpeed = 0.28f;      // m/s; alti kafa sallantisi/egilme sayilir
        const float IdleResetSeconds = 1.2f;   // bu kadar araliksiz hareketsizlik sayaci sifirlar

        // ADIM SESI MESAFE MODELI — silahinkinden AYRI ve METRE cinsinden.
        // Eskiden yalnizca maxDistance=22 veriliyordu; egrinin a katsayisi sabit oldugu icin
        // bu, sesin yariya-dusme mesafesini sessizce 0.44 m'ye indiriyordu (bkz.
        // WeaponAudioPlayer.Rolloff). 5 m'de ses %7'ye iniyordu, yani yaklasani duymak
        // imkansizdi.
        //
        // MENZIL KASITLI OLARAK KISA: adim sesi bu oyunda "biri YANIMDA" sinyali, "biri
        // haritada" degil. Uzun menzil surekli bir ayak ugultusu yapar ve sinyal degerini
        // yitirir. Olculdu (klip RMS 0.11, volume 0.90 ile):
        //   3.0 m -> 0.09  yeni duyulur
        //   2.0 m -> 0.29
        //   1.0 m -> 0.55
        //   0.5 m -> 0.73
        // 3.5 m'den sonra sessiz; PlayerAudio'daki mesafe kulu de ayni sayiyi kullanir,
        // yani menzil disindaki adimlar havuzdan slot da harcamaz.
        const float StepRefDistance = 1.5f;    // sesin YARIYA dustugu mesafe
        const float StepMaxDistance = 3.5f;    // burada kesin susar
        const float StepVolume = 0.9f;

        // MAIN'DEN GELEN ZEMIN FARKINDALIGI ICIN VARSAYILANLAR. Ayagin altinda bir
        // FootSurface yoksa bu degerler kullanilir; varsa klip/siddet/menzil oradan gelir.
        const int DefaultVariants = 4;         // WeaponSounds/footstep_1..4
        const float OtherStepVolume = 0.9f;    // yuzey yoksa (= eski StepVolume)


        PlayerHealth _health;
        Transform _avatar;
        Vector3 _lastPos;
        float _accum;
        float _idle;      // araliksiz hareketsiz gecen sure (bkz. IdleResetSeconds)
        int _stepIdx;

        // ─── GECICI OLCUM (2026-08-28) ────────────────────────────────────────────────
        // Adim sesi cihazda duyulmuyor ve statik inceleme suclu bulamadi. Elenenler:
        // klipler saglam, bilesen her istemcide var, Avatar koku kafayi izliyor,
        // Camera.main kulu atlaniyor, Head'de Interpolate=True, oyuncular olu degil.
        // Kalan supheli: MinWalkSpeed esigi + StrideMeters*dt bosaltmasi.
        //
        // Bu blok DAVRANISI DEGISTIRMEZ, yalnizca gorunurluk verir. Okumak icin:
        //   adb logcat -d -s Unity:I | findstr [AdimOlcum]
        //
        // SAHIP satiri kendi kafandan, UZAK satiri karsi oyuncunun replike kafasindan
        // gelir. Tek gozlukte yalniz SAHIP akar ve esigin gecilip gecilmedigini gosterir;
        // interpolasyon supheci icin IKI gozluk gerekir, kablo DINLEYEN tarafta olmali.
        //
        // OLCUM BITINCE SILINECEK.
        //
        // SIMDILIK KAPALI (2026-09-01): arastirma parkta, ama blok DURUYOR — cihazda iki
        // gozlukle tekrar bakilacak. const oldugu icin kapaliyken cagri tamamen derlemeden
        // cikar, yani calisma zamaninda hicbir bedeli yok. Devam ederken true yap.
        const bool StepDebug = false;
        float _dbgNext;
        float _dbgPeak;          // aradaki en yuksek hiz (m/s)
        int _dbgFrames, _dbgOver;  // toplam kare / esigi gecen kare
        Vector3 _dbgLast;
        float _dbgAccum, _dbgIdle;
        int _dbgSteps;
        // ──────────────────────────────────────────────────────────────────────────────

        void Awake()
        {
            _health = GetComponent<PlayerHealth>();
            // Avatar cocugu AvatarIKController tarafindan zaten AYAK hizasina oturtuluyor
            // (zemin sondasi orada cozulmus). Sesi oradan calmak, burada ikinci bir zemin
            // raycast'i yazmaktan hem ucuz hem tutarli.
            _avatar = transform.Find("Avatar");
            _lastPos = _health != null ? _health.HeadPosition : transform.position;
            _dbgLast = _lastPos;   // GECICI olcum; ilklenmezse ilk kare sahte sicrama sayar
        }

        void OnEnable()
        {
            if (_health != null) _health.Health.OnValueChanged += OnHealthChanged;
        }

        void OnDisable()
        {
            if (_health != null) _health.Health.OnValueChanged -= OnHealthChanged;
        }

        void OnHealthChanged(int prev, int now)
        {
            if (now >= prev) return;                       // yenilenme/ilk senkron, hasar degil
            if (_health == null || !_health.IsOwner) return; // KISIYE OZEL: yalniz hasari alan
            float vol = Mathf.Lerp(0.45f, 0.95f, Mathf.InverseLerp(5f, 40f, prev - now));
            WeaponAudioPlayer.Play2D("WeaponSounds/hit_body_" + Random.Range(1, 4), vol, 0.92f, 1.08f);
        }

        /// <summary>GECICI: adim sayacinin girdilerini yarim saniyede bir yazar. Kendi
        /// birikimini tutar, gercek _accum'a DOKUNMAZ — olcum davranisi degistirmesin.</summary>
        void DebugSample(bool owner)
        {
            Vector3 p = _health.HeadPosition;
            float dt = Mathf.Max(1e-5f, Time.deltaTime);
            Vector3 d = p - _dbgLast;
            _dbgLast = p;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist > TeleportThreshold) { _dbgAccum = 0f; return; }

            float hiz = dist / dt;
            _dbgFrames++;
            if (hiz > _dbgPeak) _dbgPeak = hiz;

            // Gercek mantigin AYNISI olmali, yoksa log yanlis sey gosterir.
            if (dist < MinWalkSpeed * dt)
            {
                _dbgIdle += dt;
                if (_dbgIdle >= IdleResetSeconds) _dbgAccum = 0f;
            }
            else { _dbgIdle = 0f; _dbgOver++; _dbgAccum += dist; }
            if (_dbgAccum >= StrideMeters) { _dbgAccum -= StrideMeters; _dbgSteps++; }

            if (Time.time < _dbgNext) return;
            _dbgNext = Time.time + 0.5f;
            Debug.Log(string.Format(
                "[AdimOlcum] {0} tepeHiz {1:F2} m/s  esikUstuKare %{2:F0}  birikim {3:F2}/{4:F2} m  adim {5}  bos {6:F1} sn  esik {7:F2} m/s",
                owner ? "SAHIP" : "UZAK", _dbgPeak,
                _dbgFrames > 0 ? 100f * _dbgOver / _dbgFrames : 0f,
                _dbgAccum, StrideMeters, _dbgSteps, _dbgIdle, MinWalkSpeed));
            _dbgPeak = 0f; _dbgFrames = 0; _dbgOver = 0;
        }

        void Update()
        {
            if (_health == null) return;
            // OLCUM: sahip yolunda SES CALINMAZ, yalnizca sayilar toplanir. Boylece tek
            // gozlukle de esigin gecilip gecilmedigi gorulebiliyor.
            if (StepDebug) DebugSample(_health.IsOwner);

            // SAHIP DE AKISA GIRER ama sesi calmaz - asagida, calma noktasinda ayrilir.
            // Sebep: main'in getirdigi zemin TITRESIMI (cama basinca) yalniz sahibi
            // ilgilendiriyor ve adim sayacinin dolmasini bekliyor. Burada donsek titresim
            // hic tetiklenmezdi.

            Vector3 p = _health.HeadPosition;   // kok kimildamaz, hareketi kafa tasir
            Vector3 d = p - _lastPos;
            _lastPos = p;
            d.y = 0f; // comelme/egilme dikey oynamasi adim degildir
            float dist = d.magnitude;

            if (dist > TeleportThreshold) { _accum = 0f; _idle = 0f; return; }
            if (_health.IsDead) { _accum = 0f; _idle = 0f; return; }   // olu/bekleyen sessiz
            if (dist < MinWalkSpeed * Time.deltaTime)
            {
                // Duruyor ya da sallaniyor. Birikim SIZDIRILMAZ - duraklamak ilerlemeyi
                // silmemeli. Yalnizca hareketsizlik araliksiz surerse sayac sifirlanir.
                _idle += Time.deltaTime;
                if (_idle >= IdleResetSeconds) _accum = 0f;
                return;
            }

            _idle = 0f;
            _accum += dist;
            if (_accum < StrideMeters) return;
            _accum -= StrideMeters;

            // Ses AYAK hizasinda dogar, kafada degil. Avatar henuz kurulmamissa kafanin
            // altinda makul bir noktaya dusulur.
            Vector3 pos = _avatar != null ? _avatar.position : new Vector3(p.x, p.y - 1.6f, p.z);

            // ZEMIN FARKINDALIGI (main'den). Ayagin altinda ozel bir kaplama var mi? Adim
            // BASINA bir kez soruluyor, her kare degil - sorgu ucuz degil ve adim zaten
            // saniyede bir-iki kez oluyor.
            var surf = FootSurface.Under(pos);
            int variants = surf != null ? Mathf.Max(1, surf.clipVariants) : DefaultVariants;

            // Varyantlar sirayla degil karisik ama ardisik tekrarsiz: ayni klibin arka
            // arkaya calmasi "makine" hissi verir.
            _stepIdx = (_stepIdx + Random.Range(1, variants)) % variants;
            string clip = surf != null
                ? surf.ClipPath(_stepIdx)
                : "WeaponSounds/footstep_" + (_stepIdx + 1);

            // KENDI ADIMIMIZ: SES YOK, TITRESIM VAR.
            //
            // Sentetik kendi-adim sesi kaldirildi (bkz. sinif aciklamasi): fiziksel olarak
            // yuruduugumuz icin gercek ayak sesimizi zaten duyuyoruz, sentetik olan onun
            // yerine gecmez, ustune biner. Ama main'in getirdigi TITRESIM kalmali - o ses
            // degil, "az once bir seye bastin" bilgisi ve yalniz ozel kaplamada calisiyor.
            // Her adimda titreyen kumanda VR'da bilek yorgunlugu uretir; nadir oldugunda
            // bilgi tasir.
            if (_health.IsOwner)
            {
                if (surf != null) Buzz(surf.hapticAmplitude, surf.hapticDuration);
                return;
            }

            // MESAFE KULU: havuz 16 kaynakli ve silah sesleriyle ORTAK. Menzil disindaki
            // her oyuncunun her adimi, duyulmayacak olmasina ragmen bir slot tuketir ve
            // calan atis seslerini devirirdi. Camera.main = AudioListener'in durdugu yer
            // (bkz. SingleAudioListener); bulunamazsa kulmeden calariz.
            float menzil = surf != null ? surf.maxDistance : StepMaxDistance;
            var cam = Camera.main;
            if (cam != null && (cam.transform.position - pos).sqrMagnitude > menzil * menzil)
                return;

            WeaponAudioPlayer.PlayAt(clip, pos,
                surf != null ? surf.otherVolume : OtherStepVolume,
                0.93f, 1.07f, menzil, false, StepRefDistance);
        }

        /// <summary>Iki kumandaya da kisa bir darbe. Cihaz yoksa sessizce gecer.</summary>
        static void Buzz(float amplitude, float duration)
        {
            if (amplitude <= 0f || duration <= 0f) return;

            var left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (left.isValid) left.SendHapticImpulse(0, amplitude, duration);

            var right = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (right.isValid) right.SendHapticImpulse(0, amplitude, duration);
        }
    }
}
