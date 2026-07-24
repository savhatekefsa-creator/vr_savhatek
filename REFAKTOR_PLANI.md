# Refaktör ve Hata Düzeltme Planı

Tarih: 2026-07-23 · Branch: `duzeltme/inceleme-bulgulari`
Kaynak: 4 paralel kod incelemesi (ağ/oyuncu, silah sistemi, UI+Editor, performans+mimari) — 60 dosya / ~13.400 satır tam okuma.

Her düzeltme **ayrı commit** olarak atılır. Her maddenin sonunda **TEST** satırı vardır — o düzeltme birleştirilmeden önce mutlaka o senaryo denenmelidir.

---

## Faz 0 — Editör güvenlik ağı (veri kaybı riskleri)

### 0.1 — Menü 1 prefab silme koruması 🔴 KRİTİK
**Dosya:** `Assets/_VRMultiplayer/Editor/VRMultiplayerSetup.cs` (`CreateNetworkPlayerPrefab`)
**Sorun:** Menü "1. Create NetworkPlayer Prefab" mevcut prefab'ı kontrolsüz eziyor; combat/takım/avatar bileşenleri tek tıkla kalıcı siliniyor.
**Çözüm:** Prefab varsa `EditorUtility.DisplayDialog` ile "SIFIRDAN kurulur, mevcut bileşenler silinir" onayı iste; onay yoksa çık.
**TEST:** Menü 1'e tıkla → prefab varken diyalog çıkmalı; "Hayır" deyince prefab dokunulmamış kalmalı (PlayerHealth/TeamSelector bileşenleri yerinde mi bak).

### 0.2 — Dmr1GripSetup iç düğüm koruması 🟠
**Dosya:** `Assets/_VRMultiplayer/Editor/Dmr1GripSetup.cs` (`FindSceneWeapon`)
**Sorun:** "Dmr1" adı prefab'ın İÇ düğümünde de eşleşiyor; otomatik kurulum iç düğüme Rigidbody + NetworkObject takabilir (iç içe NetworkObject NGO'da yasak, çift-RB silahı düşürür).
**Çözüm:** `GunPhysicsSetup`'taki gibi `PrefabUtility.GetOutermostPrefabInstanceRoot` kontrolü — yalnızca instance KÖKÜ kabul edilir.
**TEST:** Sahnede Dmr1 varken script derlet (otomatik çalışır) → Console'da hedefin sahne kök objesi olduğunu doğrula; iç düğümlerde yeni Rigidbody/NetworkObject OLMAMALI.

### 0.3 — Avatar gri malzeme ezmesi 🟠
**Dosya:** `Assets/_VRMultiplayer/Editor/VRMultiplayerSetup.cs` (`AddHumanoidAvatar`)
**Sorun:** İlk SkinnedMeshRenderer'ın tüm malzeme slotları düz gri `Mat_Avatar` ile değiştiriliyor → renkli asker dokusu griye dönüyor.
**Çözüm:** Modelin kendi malzemesi geçerliyse (URP shader'lı) dokunma; gri malzeme yalnızca malzemesi kırık/eksik slotlara yedek olarak atanır.
**TEST:** Menü 21 (Swap Avatar To Colored Soldier) çalıştır → prefab'ı aç, asker gövdesi RENKLİ kalmalı; PlayerIdentity takım tonu hâlâ çalışmalı (iki istemcili testte kırmızı/mavi ayrımı görünmeli).

### 0.4 — LoadPrefabContents varlık kontrolleri + yanlış undo vaadi 🟡
**Dosya:** `Assets/_VRMultiplayer/Editor/VRMultiplayerSetup.cs` (6 menü), `RoomScanSetup.cs` (1 menü)
**Sorun:** Prefab yokken menü 7/10/11/15/18/19/20 ham exception fırlatıyor; StripSoldierGear diyaloğu "Ctrl+Z ile geri alınır" diyor ama prefab içi silme undo'suz.
**Çözüm:** Her `LoadPrefabContents` öncesi varlık kontrolü + "önce Adım 1-2'yi çalıştır" diyaloğu; StripSoldierGear metninden Ctrl+Z ifadesini çıkar, yerine "geri dönüş: git" uyarısı.
**TEST:** NetworkPlayer.prefab'ı geçici olarak başka klasöre taşı → menü 7/10/15/18 tıkla → exception değil, açıklayıcı diyalog gelmeli. Prefab'ı geri koy.

---

## Faz 1 — Runtime hataları (oyuncuyu vuranlar)

### 1.1 — LanBootstrap `_busy` kilidi 🟠
**Dosya:** `Assets/_VRMultiplayer/Scripts/LanBootstrap.cs`
**Sorun:** Başarılı katılımdan sonra `_busy` hiç sıfırlanmıyor. Sunucu kapanınca/Wi-Fi kopunca "B'ye bas" paneli görünüyor ama B tuşu ölü — uygulama restart gerekiyor.
**Çözüm:** `OnClientDisconnectCallback` / oturum bitişinde `_busy = false`; sunucu durduğunda da aynı şekilde.
**TEST:** İki cihazla bağlan → sunucuyu kapat → istemcide panel gelince B'ye bas → YENİDEN bağlanabilmeli (restart olmadan). PC tarafında sunucuyu durdur-başlat da denenecek.

### 1.2 — Ateş token bucket derinliği 🟠
**Dosya:** `Assets/_VRMultiplayer/Scripts/NetworkWeapon.cs` (`FireServerRpc` doğrulama bloğu)
**Sorun:** Kova derinliği 1 → Wi-Fi titremesinde art arda gelen meşru RPC'ler "kadans" reddi yiyor; istemci sesi/tepmeyi oynatmış ama hasar/mermi işlenmemiş oluyor (sessiz desync).
**Çözüm:** Kova kapasitesi 3'e çıkar (dolum hızı AYNI kalır → uzun vadeli atış hızı sınırı değişmez, sadece jitter emilir).
**TEST:** Otomatik modda şarjör bitene dek sıkı ateş (tercihen Wi-Fi'da) → Console'da "kadans" reddi OLMAMALI; mermi sayacı ile çıkan tracer sayısı birebir örtüşmeli. Hile sınırı korunmalı: tek karede 5+ RPC gönderilirse hâlâ reddedilmeli.

### 1.3 — Çift silahta çapraz tetik 🟡
**Dosya:** `Assets/_VRMultiplayer/Scripts/NetworkWeapon.cs` (tetik okuma, ~satır 322)
**Sorun:** Tutan istemcide HER İKİ elin tetiği de silahı ateşliyor → iki elde iki silah varken tek tetik ikisini birden ateşliyor.
**Çözüm:** Tetik yalnızca `_grab.HolderHand`'e (istenirse destek eline de değil, SADECE ana ele) ait düğümden okunur.
**TEST:** İki ele iki silah al → yalnız sağ tetiği çek → yalnız sağdaki ateş etmeli. Tek silah sol eldeyken sol tetik çalışmalı (regresyon).

### 1.4 — Fırlatma donması (host olmayan istemci) 🟠
**Dosya:** `Assets/_VRMultiplayer/Scripts/GrabbableObject.cs` (`FixedUpdate`)
**Sorun:** `Release` sonrası `IsHeld` (sunucu yazmalı NetworkVariable) ~1 RTT boyunca eski değerde; `FixedUpdate` `_flying`'i söndürüp objeyi kinematik yapıyor → obje havada asılı kalıyor.
**Çözüm:** `FixedUpdate`'te `IsHeld` dalına `!_flying` koşulu ekle (OnHolderChanged'daki koruma ile tutarlı hale gelir).
**TEST:** İKİ cihaz şart (host'ta bug görünmez): istemci cihazda profilsiz bir objeyi (taş vb.) fırlat → yay çizerek düşmeli, havada donmamalı. Fırlatıp hemen geri kapma da denensin (holder geri gelirken çakışma).

### 1.5 — Silah takası el seçimi + öksüz silah 🟠
**Dosya:** `Assets/_VRMultiplayer/Scripts/HandGrabber.cs` (`RequestWeaponSwap`, `EquipSpawnedRpc`)
**Sorun:** (a) Takas hep `_right ?? _left` → sol eldeki silah değiştirilse bile yenisi SAĞ ele gidiyor; sağ el doluysa equip atlanıyor. (b) Atlama yollarında sunucunun spawn'ladığı silah sahipsiz sahnede kalıyor (mermi kopyasıyla birlikte).
**Çözüm:** (a) `held == mevcut silah` olan eli bul; yoksa boş ele düş. (b) Equip gerçekleşmeyen her yolda sunucuya despawn RPC'si gönder.
**TEST:** Silahı SOL ele al → çarktan başka silah seç → yenisi SOL ele gelmeli. Sonra: iki el doluyken çarktan seçim yap → ortada sahipsiz silah kalMAmalı (Hierarchy'de kontrol), mermi sayısı korunmalı.

### 1.6 — RoomScan RPC gönderen doğrulaması 🟡
**Dosya:** `Assets/_VRMultiplayer/Scripts/RoomScanSync.cs` (`SendRoomChunkServerRpc`)
**Sorun:** Herhangi bir istemci başkasının objesi üzerinden chunk gönderebilir → devam eden transferi bozar veya sahte RoomPlan.json yazdırır.
**Çözüm:** RPC başında `SenderClientId == OwnerClientId` kontrolü, değilse at.
**TEST:** Normal akış regresyonu yeterli: Quest'te oda tara → gönder → PC'de RoomPlan.json doğru yazılmalı ve "PC onayi bekleniyor" akışı tamamlanmalı.

### 1.7 — Küçük UI düzeltmeleri 🟡
**Dosyalar:** `WeaponSelectorUI.cs`, `SingleAudioListener.cs`
**Sorunlar:** (a) Çark açıkken bileşen disable olursa 3D önizlemeler sahnede asılı kalıyor (`OnDisable` `SetOpen(false)`'u atlıyor). (b) `SingleAudioListener` "keep" olarak DEVRE DIŞI listener'ı seçebiliyor → tüm sesler kapanıyor.
**Çözüm:** (a) `OnDisable` → `SetOpen(false)`. (b) Keep adayı etkin bir listener'dan seçilir ve sonda `keep.enabled = true` garanti edilir.
**TEST:** (a) Çark açıkken silahı bırak/objeyi kapat → önizleme klonları görünmez olmalı. (b) Main camera'nın AudioListener'ını elle kapat, 1-2 sn bekle → oyunda ses KESİLMEMELİ.

---

## Faz 2 — Quest performansı (bu branch'te İSTEĞE BAĞLI, ayrı oturum önerilir)

| # | İş | Dosya | Kazanç |
|---|---|---|---|
| 2.1 | Destek-el collider listesini tutuşta cache'le (+~10 Hz'e düşür) | HandGrabber.cs:248 | İki elle nişanda kare başı GC alloc biter |
| 2.2 | `GrabbableObject` statik kayıt listesi; 4 periyodik `FindObjectsByType` bunu kullansın | WeaponInventory / HandGrabber.Reconcile / WeaponRackRespawner / SingleAudioListener | Periyodik sahne taraması + dizi alloc biter |
| 2.3 | Atış loglarını `DEVELOPMENT_BUILD`'e al; `RaycastNonAlloc` + gerçek layer mask | NetworkWeapon.cs:574-616, 591 | Ateş yolu alloc'suz |
| 2.4 | Global paylaşımlı decal havuzu; tracer/impact malzemesi profil başına cache + despawn'da imha | NetworkWeapon.cs:851-898 | Silah takası hitch'i + malzeme sızıntısı biter |
| 2.5 | Parmak kemik Transform cache (`ApplyAuthored`); `OnGUI`'lere platform kapısı | ProceduralFingerPoser.cs:345, LanBootstrap.cs:112, TeamSelector.cs:113 | Kare başı 15×N engine çağrısı + boş IMGUI biter |

**TEST (Faz 2 genel):** Her adım sonrası Quest'te Profiler ile GC Alloc/frame kontrolü; silah takası anında frame spike'ının küçüldüğü doğrulanmalı; iki elle nişan + otomatik ateş kombinasyonu 72 Hz'i korumalı.

## Faz 3 — Mimari (TAMAMLANDI — branch: `refaktor/modul-yapisi`)

**Durum (2026-07-23):** Aşağıdaki maddelerin tamamı uygulandı; tek bilinçli sapma:
`WeaponEquipService` ERTELENDİ — takas RPC'leri bir NetworkBehaviour üzerinde yaşamak
zorunda, yeni bileşene taşımak NetworkPlayer prefab'ına bileşen eklemeyi gerektirir;
bu prefab-dokunuşlu iş ayrı ve dikkatli bir oturuma bırakıldı. Ayrıca WeaponPackSetup
ve Dmr1GripSetup'taki namlu kodları kopya değil bilinçli farklı algoritma çıktı — birleştirilmedi.

1. **Kopya kod birleştirme (önce bu):** `WeaponGeometry` (6 namlu-tespit kopyası tek statik sınıfa), `HeadFollowPanel` (6 panel kopyası), `XRButtons` (7 tuş okuma — eşikler standardize), `XRRigReference.HeadOrCamera` (5 kopya), shader fallback'leri `UITheme`'e (4 kopya).
2. **`NetworkWeapon` bölme (ayrı commit'ler):** `WeaponFx` (~330 satır istemci FX) → `WeaponHitscanServer` (statik, sahnesiz test edilebilir) → `WeaponReloadGesture`. Geriye ~350 satır NetworkBehaviour kalır.
3. **Katman düzeltme:** `WeaponInventory` + `WeaponRackRespawner` → `Weapons/`; hasar yoluna atıcı pozisyonu ekle, `DamageDirectionFlash`'ın tracer kazımasını sil; takas spawn mantığı → `WeaponEquipService`.
4. **Klasör yapısı (meta'larla birlikte, tek commit):** `Networking/ · XR/ · Player/ · Avatar/ · Interaction/ · Weapons/ · RoomScan/ · UI/`. `Combat/` (2 dosya) `Player/`e erir. Taşımalar Unity içinden veya .meta ile birlikte yapılır → GUID korunur, sahne referansları kırılmaz.
5. **`VRMultiplayerSetup` bölme:** SceneSetupMenu / AvatarSetupMenu / GrabbableSetupMenu / CombatSetupMenu.

**TEST (Faz 3 genel):** Her bölme sonrası tam derleme + Console sıfır hata; sahnede mevcut prefab referanslarının (NetworkPlayer, silah prefab'ları) kopmadığı kontrol edilir; tek cihaz smoke test (bağlan-tut-ateş-takas) her commit sonrası.

## Faz 4 — Hijyen (TAMAMLANDI — branch: `duzeltme/faz4-hijyen`)

**Durum (2026-07-23):** Ana maddeler + görsel cilalar uygulandı (9 commit): profil eşit-skor
uyarısı, isim-bazlı silah anahtarı, ThirdParty/ düzeni (yol sabitleri dahil), _Recovery
temizliği, SpreadSpawn birikimi, PC ok-tuşu anında-seçim, parmak poz pop'u, weld fade
sıçraması, MCP paket manifesti. **Yapılmayanlar:** stash düşürme (kalıcı silme — kullanıcı
kararı), ServerView ping kolonu (NGO/UTP id eşleme API'si araştırma istiyor), geç katılan
reload sesi (kozmetik), istemci origin doğrulaması (bilinçli ölç-önce kararı, backlog).

- Profil eşit-skor çakışmasına uyarı logu; silah kimliğini `Resources.LoadAll` indeksi yerine isim anahtarıyla gönder (Editor↔Quest sessiz desync sigortası).
- Asset paketleri (`P A I N T B A L L...`, `FPS Gun Pack 4K`, `Soldiers-Pack`, `Gece Studio`, `Standout7`) → `Assets/ThirdParty/`.
- `Assets/_Recovery` eski sahneleri temizle; aşılmış stash'leri düşür.
- Bilinçli açık: istemci `origin`/nişan doğrulaması log-only (ölç-önce kararı) — ileride sunucu taraflı poz doğrulaması.
- Orta/düşük kalanlar: PC'de ok tuşunun anında seçim yapması (WeaponSelectorUI histerezisi), parmak poz "pop"u (ProceduralFingerPoser reseed), WeaponHandWeld fade kesintisinde ağırlık sıçraması, DamageDirectionFlash yanlış yön heuristiği, geç katılanın reload ses başlangıcını kaçırması, `SpreadSpawn` birikimli rig offseti, ServerView ping kolonunun yanlış id uzayı, RoomScan chunk'larının tek karede kuyruklanması, kinematik gövdede `ContinuousDynamic`.

---

## Bu branch'in kapsamı

Bu branch'te **Faz 0 + Faz 1 + Faz 2** uygulandı (11 düzeltme + 8 performans işi, her biri ayrı commit). Faz 2'de ana 5 kalemin yanında ek üç iş de yapıldı: kinematik gövdede `ContinuousSpeculative`, paylaşımlı çift-yüzlü malzeme varyantları (`MaterialDoubleSided`), oda chunk'larının kare başına ~8'erli gönderimi. Faz 3 ayrı branch'te yapılır ki inceleme diff'leri okunabilir kalsın.

## Birleştirme öncesi zorunlu test turu

1. **Tek cihaz (Editor):** derleme temiz → bağlan → silah al → ateş → şarjör değiştir → çarktan takas → bırak.
2. **İki cihaz (PC sunucu + Quest):** katıl → takım seç → kalibre → oda tarama gönder → çatışma (otomatik ateş uzun seri) → sunucuyu kapat-aç → Quest'ten yeniden katıl (1.1'in asıl testi).
3. **Editör araçları:** Menü 1 diyaloğu, Menü 21 renkli avatar, prefab yokken menüler.
