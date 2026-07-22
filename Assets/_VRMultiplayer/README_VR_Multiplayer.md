# savhateks — Meta Quest 3 Multiplayer VR (LAN)

Bu proje, **3 Meta Quest 3 gözlüğün aynı WiFi üzerinden aynı orman dünyasına girip
birbirini (kafa + 2 el) görerek hareket etmesi** için hazırlandı.

- **VR runtime:** OpenXR (`com.unity.xr.openxr` 1.16.1)
- **Ağ:** Unity Netcode for GameObjects (`com.unity.netcode.gameobjects` 2.13.0), LAN / direct-IP
- **Avatar:** Basit kafa + 2 el, owner-authoritative `NetworkTransform` ile senkron
- **Unity:** 6000.3.18f1 (URP)

---

## Mimari (nasıl çalışıyor?)

Her cihazda **iki ayrı hiyerarşi** vardır:

1. **Yerel XR Rig** (ağa bağlı DEĞİL): `XR Rig > Main Camera + LeftHand/RightHand Anchor`.
   Kamera doğrudan gözlüğün hareketini takip eder (sıfır gecikme — VR'da mide bulantısını önler).
   Yürüme/dönme bu rig'i hareket ettirir.
2. **Ağ Avatarı** (NetworkManager Player Prefab): `NetworkPlayer > Head + LeftHand + RightHand`.
   Her bağlanan oyuncu için bir tane oluşur ve herkese kopyalanır.

**Sahip kopyalar, ağ dağıtır:** Avatarın sahibi olan cihaz her karede kendi rig'inin kamera/el
konumlarını avatarın kafa/ellerine yazar; owner-authoritative `NetworkTransform` bunu diğer
herkese pürüzsüz (interpolasyonlu) olarak iletir. Sahip kendi kafasını gizler (kafanın içini
görmemek için). Yürüyünce kamera dünya konumu değişir → avatar kafası da değişir → diğerleri
seni hareket ederken görür. Ekstra RPC gerekmez.

Detaylı script'ler:
- `Scripts/XRDevicePoseDriver.cs` — kafa/el pozunu gözlükten okur (built-in `UnityEngine.XR`)
- `Scripts/XRTrackingOriginSetup.cs` — zemin (floor) takip modu
- `Scripts/XRRigReference.cs` — yerel rig'i avatara bağlayan tekil (singleton)
- `Scripts/ClientNetworkTransform.cs` — owner-authoritative NetworkTransform
- `Scripts/NetworkVRPlayer.cs` — sahip → avatar kopyalama + kendi kafasını gizleme
- `Scripts/NetworkDiscovery.cs` — LAN'da host'u otomatik bulma (UDP broadcast)
- `Scripts/LanBootstrap.cs` — A=Host / B=Katıl mantığı + durum etiketi
- `Editor/VRMultiplayerSetup.cs` — tek tıkla sahne/prefab kurulum sihirbazı

---

## Kurulum adımları

### Adım 0 — Unity Hub'da Android modülü
Unity Hub > Installs > 6000.3.18f1 > (dişli) **Add Modules** → şunları kur:
**Android Build Support** + alt öğeleri **OpenJDK** ve **Android SDK & NDK Tools**.
(Bunlar olmadan Quest'e build alınamaz.)

### Adım 1 — Projeyi aç, paketleri yüklet
`manifest.json` zaten güncellendi (Netcode 2.13.0 + OpenXR 1.16.1 eklendi).
Projeyi Unity'de aç; Package Manager paketleri otomatik indirir (transport 2.6.0 ve
xr.management 4.5.4 bağımlılık olarak gelir). Derleme hatası olmadığından emin ol.

> İlk açılışta OpenXR "yeni giriş sistemi backend'i" için editörü yeniden başlatmanı isteyebilir → **Yes**.

### Adım 2 — Platformu Meta Quest'e çevir (Player Settings'i otomatik ayarlar)
**File > Build Profiles** → listeden **Meta Quest** seç.
- "Meta Quest is currently disabled" yazıyorsa önce **Enable Platform**'a bas (Meta Quest
  desteğini/paketlerini indirir — birkaç dakika sürebilir). ÖNEMLİ: bundan önce Console'da
  derleme hatası olmamalı, yoksa kurulum takılır.
- Sonra **Switch Platform** de.

Bu, Quest için doğru ayarları otomatik uygular: IL2CPP, ARM64, Vulkan, Linear renk uzayı,
Single Pass Instanced, Min API 29 / Target API 32. (Bu yolu kullanırsan Adım 3'teki OpenXR
etkinleştirmesinin çoğu otomatik yapılır; yine de Interaction Profiles'ı kontrol et.)

> Meta Store'a **yükleyeceksen** Target API'yi Android 14 (API 34) yap. Sadece kendi
> gözlüğüne sideload/test için API 32 yeterli.

### Adım 3 — OpenXR'ı Quest için aç
**Edit > Project Settings > XR Plug-in Management**:
1. **Android** sekmesinde (Android robotu ikonu) **OpenXR** kutusunu işaretle.
   (Varsa eski "Oculus" sağlayıcısını KAPAT — aynı anda tek sağlayıcı.)
2. Sol menüden **XR Plug-in Management > OpenXR** (Android sekmesi):
   - **Interaction Profiles** listesine `+` ile ekle:
     **Oculus Touch Controller Profile** ve **Meta Quest Touch Plus Controller Profile**
     (Quest 3'ün kumandaları). İkisini de açık bırak.
   - **OpenXR Feature Groups** altında **Meta Quest Support** özelliğini aç.
3. **XR Plug-in Management > Project Validation**: sarı/kırmızı uyarıları **Fix All** ile gider.

### Adım 3.5 — Android izinleri (otomatik LAN keşfi için ŞART)
Quest/Android, `MulticastLock` + izin olmadan gelen broadcast paketlerini düşürür; bu olmadan
"B ile otomatik katıl" cihazda çalışmaz (Editörde çalışır çünkü Windows filtrelemez).
`MulticastLock` kodu `NetworkDiscovery.cs` içinde hazır; sadece izinleri eklemen gerekir:

1. **Project Settings > Player > Android > Publishing Settings > Build** altında
   **Custom Main Manifest** kutusunu işaretle. Bu, `Assets/Plugins/Android/AndroidManifest.xml`
   dosyasını Unity'nin doğru varsayılanlarıyla (VR aktivitesi dahil) oluşturur.
2. O dosyayı aç ve `<manifest ...>` etiketinin hemen altına şu satırları ekle:
   ```xml
   <uses-permission android:name="android.permission.INTERNET" />
   <uses-permission android:name="android.permission.ACCESS_WIFI_STATE" />
   <uses-permission android:name="android.permission.CHANGE_WIFI_MULTICAST_STATE" />
   ```

> Bu adımı atlarsan yine de oynanabilir: host ekranındaki IP'yi, katılan gözlüklerde
> NetworkManager objesindeki **LanBootstrap > Manual Host Ip** alanına yazıp build alman yeterli
> (bu manuel yol izin gerektirmez).

### Adım 4 — Sahneyi tek tıkla kur (sihirbaz)
1. Kullanmak istediğin sahneyi aç. Öneri: orman sahnesi
   `Assets/Standout7/LOKIT_Forest/Scene/Demo.unity`.
2. Üst menü: **Tools > VR Multiplayer > 2. Setup Current Scene**.
   Bu otomatik olarak şunları ekler:
   - Yerel **XR Rig** (kamera + el anchor'ları, yürüme, floor takip)
   - **NetworkManager** + UnityTransport + LAN keşfi + LanBootstrap
   - Avatar prefab'ı (`Assets/_VRMultiplayer/Prefabs/NetworkPlayer.prefab`) — Player Prefab olarak atanır
   - Dünyada asılı bir **durum etiketi** ("A = HOST / B = KATIL")
3. Sahneyi kaydet (**Ctrl+S**).
4. Bu sahneyi **File > Build Profiles > Scene List**'e ekle (üstte ve işaretli olsun).

### Adım 5 — Editörde hızlı test (gözlük olmadan)
Play'e bas. Sol üstteki **HOST başlat** / **KATIL** düğmeleriyle mantığı test edebilirsin.
(Gözlük olmadan tam VR hareketi görünmez ama ağ bağlantısı test edilir. İki örnek için
ParrelSync/ikinci bir build kullanılabilir.)

### Adım 6 — Gözlüğü geliştirici moduna al
1. Meta hesabın bir **doğrulanmış geliştirici organizasyonuna** bağlı olmalı (bir kerelik,
   18+ kimlik doğrulaması) — https://developers.meta.com
2. Telefondaki **Meta Horizon** uygulaması > Cihazlar > Quest 3'ünü seç >
   **Headset Settings > Developer Mode > AÇIK**.
3. Gözlüğü USB-C ile PC'ye bağla; gözlük içinde **"USB hata ayıklamaya izin ver /
   Bu bilgisayara her zaman izin ver"** de. (Windows'ta gerekiyorsa Oculus ADB sürücüsünü kur.)

### Adım 7 — Build al ve gözlüğe yükle
- Gözlük bağlıyken: **File > Build Profiles > Meta Quest > Build And Run**.
  APK üretilir ve gözlükte otomatik açılır.
- Alternatif: **Build** ile `.apk` üret, sonra terminalde `adb install -r yol\app.apk`.
  Gözlükte **Library > Unknown Sources** altından çalıştır.

Aynı APK'yı **3 gözlüğe de** yükle.

### Adım 8 — Birlikte oyna (3 gözlük, aynı WiFi)
1. 3 gözlük de **aynı WiFi ağında** olsun. Uygulamayı hepsinde başlat.
2. **1 kişi** sağ kumandada **A** → HOST olur (etikette IP görünür).
3. **Diğer 2 kişi** sağ kumandada **B** → LAN'da host'u otomatik bulup katılır.
4. Artık aynı ormandasınız — birbirinizi kafa+el olarak görür, sol analogla yürür,
   sağ analogla dönersiniz.

**Kontroller:** Sol analog = yürü · Sağ analog (sağa/sola it) = 45° dön · A = Host · B = Katıl.

---

## Sorun giderme

- **"Sunucu bulunamadı":** Bazı WiFi'ler cihazlar arası trafiği engeller (AP/client isolation).
  Çözüm: Ev/telefon hotspot'u kullan, veya host IP'sini elle gir → NetworkManager objesindeki
  **LanBootstrap > Manual Host Ip** alanına host'un ekranında yazan IP'yi yaz ve yeniden build al.
- **Tüm oyuncular aynı noktada:** normalde `NetworkVRPlayer` küçük bir halka ofseti uygular;
  başlangıç yerini değiştirmek için sahnedeki **XR Rig** objesini istediğin konuma taşı.
- **Ekranda iki kamera / ses uyarısı:** sihirbaz fazladan AudioListener'ları temizler; yine de
  sahnede eski bir "Main Camera" varsa sil (sadece XR Rig altındaki kamera kalsın).
- **Prefab "not registered" hatası:** Player Prefab için gerekmez; ama sonradan RPC ile başka
  ağ objesi doğuracaksan onu NetworkManager > NetworkConfig > Prefabs listesine ekle.
- **Derleme hatası (menü görünmüyor):** Önce Netcode paketinin yüklendiğinden emin ol; sihirbaz
  ancak paketler derlendikten sonra derlenir ve menü görünür.

---

## İnsansı Avatar (Humanoid model + kol IK + otomatik renk/isim)

Oyuncular tek bir insansı modeli kullanır; her oyuncu **otomatik renk + isim etiketiyle** ayrılır.
Kollar **Animation Rigging (Two-Bone IK)** ile kumandaları, gövde kafayı takip eder. Sen kendi
küp ellerini görürsün; **diğerleri seni insansı olarak görür**.

> Not: Ready Player Me servisleri Ocak 2026'da kapandı — bunun yerine **Mixamo** (ücretsiz) kullan.
> Paket `com.unity.animation.rigging 1.4.1` manifest'e eklendi; Unity açılınca otomatik iner
> (Burst + Mathematics'i de çeker).

### Adım A — Ücretsiz Humanoid model indir (Mixamo)
1. [mixamo.com](https://www.mixamo.com) → ücretsiz Adobe hesabıyla gir.
2. Bir karakter seç → **Download** → Format: **FBX for Unity (.fbx)**, Pose: **T-pose**, Skin: **With Skin**.
3. `.fbx` dosyasını `Assets/_VRMultiplayer/Avatar/` klasörüne sürükle.

### Adım B — Modeli Humanoid yap
1. Project'te FBX'i seç → Inspector → **Rig** sekmesi → **Animation Type = Humanoid** →
   **Avatar Definition = Create From This Model** → **Apply**.
2. **Configure**'a tıkla; kol/kafa kemiklerinin **yeşil (eşleşmiş)** olduğunu doğrula → **Done**.
   (Bu şart: sihirbaz kemikleri buradan bulur.)
3. Mobil için optimize: tek SkinnedMeshRenderer, tek materyal, materyalde **GPU Instancing** açık,
   dokular ASTC/512-1024, ~15-20k üçgen altı.

### Adım C — Sihirbazla avatarı ekle
1. Project'te **Humanoid FBX'i seç** (seçili kalsın).
2. Menü: **Tools > VR Multiplayer > 3. Add Humanoid Avatar (select model first)**.
3. Sihirbaz modeli prefab'a gömer, kol IK'sini + IK sürücüsünü + isim etiketini + renk/isim
   bileşenini otomatik bağlar. "Humanoid avatar eklendi" mesajını görürsün.
4. Gözlüklere **yeniden build al**.

### Adım D — Play/cihazda ince ayar (`Avatar` altındaki `AvatarIKController`)
- **Eller ters/kayık**: `Left/Right Grip Position/Euler Offset` değerlerini ayarla (kumanda ≠ bilek).
- **Dirsek ters bükülüyor**: `UpperBodyRig` altındaki `Left/RightElbowHint` konumlarını dirseğin
  arkasına al.
- **Kafa yanlış dönüyor**: `Head Euler Offset`'i ayarla (veya `Drive Head Rotation`'ı kapat).
- **İsim etiketi ters/aynalı**: `NameTag > Billboard > Flip` kutusunu işaretle.

## Sonraki adımlar (isteğe bağlı geliştirmeler)
- **Sesli konuşma (voice chat):** Dissonance/Vivox veya basit bir mikrofon RPC'si.
- **El takibi (hand tracking):** `com.unity.xr.hands` ekleyip parmak eklemlerini avatara işle.
- **Nesne tutma/etkileşim:** `com.unity.xr.interaction.toolkit` (XRI) ekleyip grab interactor'lar.
- **İsim etiketi:** kafanın üstünde billboard `NetworkVariable<FixedString>`.
- **İnternet üzerinden oyun:** Unity Relay + Lobby (bulut hesabı) — LAN yerine uzaktan bağlanma.
- **Gerçekçi avatarlar:** Meta Avatars SDK.
