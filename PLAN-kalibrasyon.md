# Kalibrasyon Planı — Anchor + AprilTag ile Drift Çözümü

> Durum: **Araştırma tamamlandı, uygulama başlamadı.**
> Hedef: "Oyun ortasında kalibrasyon bozulması" ve "multiplayer'da oyuncuların birbirinden ayrışması" sorunlarını kökten çözmek.

---

## 0. Problem Özeti

**Şu anki sistem:** [`CalibrationManager.cs`](Assets/_VRMultiplayer/Scripts/XR/CalibrationManager.cs) — iki noktalı (A/B) manuel kalibrasyon. Sağ kumanda A noktasında tetik → origin, B noktasında tetik → yön. Rig bir kez döndürülüp kaydırılıyor.

**Matematiği doğru. Sorun mimaride:** bu **tek kare fotoğraf**. Bir kez ölçüp sonsuza kadar tracking'e güveniyor.

**Kök neden:** Quest'in SLAM tracking'inin koordinat sistemi zamanla kayar (drift). A/B ofseti sabit kalırken zemin altından kayıyor. Multiplayer'da her gözlük **bağımsız** kaydığı için oyuncular ayrışıyor.

**Çözüm fikri:** Tek seferlik snapshot'ı, **sürekli kendini onaran kapalı döngüyle** değiştirmek.

---

## 0.0 DURUM VE YOL HARİTASI (yönetime anlatmak için)

### Veri şu an nerede duruyor?

| Ne | Nerede | Kimin |
|---|---|---|
| **Uzamsal veri** (anchor / nokta bulutu) | **Meta'nın sunucuları** | Meta ⚠️ |
| Grup kimliği (sadece bir isim) | Bizim PC'miz, diskte | Biz |
| Konum hesabı | Her gözlükte, yerel — ağa çıkmaz | Biz |

**Tek cümle:** *Konum verisi Meta'nın sunucularından geçiyor; bizim sunucumuz yalnızca bir
kimlik numarası taşıyor.* Bu bir tasarım tercihi değil — Meta'nın Shared Spatial Anchor
API'si uzamsal veriyi kendi bulutundan geçirir, dışarı vermez.

**Bedeli:** internet zorunlu · anchor ~30 günde düşüyor · mekânın uzamsal verisi Meta'ya gidiyor.

### Sunucu tarafına taşımak istenirse → AprilTag

| | Meta'ya giden | İnternet | Ömür | Veri sahibi |
|---|---|---|---|---|
| **Shared Anchor** (bugünkü) | Uzamsal veri | Zorunlu | ~30 gün | Meta |
| **AprilTag** (Faz 3) | **Hiçbir şey** | **Gerekmez** | **Sınırsız** | **Biz** |

AprilTag'de tag yerleşimi (hangi ID nerede) bizim sunucumuzda bir JSON dosyasıdır; her gözlük
duvardaki işareti kendi kamerasıyla okur ve konumunu yerel hesaplar. Sunucu-otoriter dağıtım,
kalıcılık ve kilit mantığı aynen kalır — sadece dağıtılan şey "grup GUID" yerine "tag yerleşimi"
olur. **Faz 1 (drift düzeltmesi) zaten tamamen yereldir, internet gerektirmez.**

### Yol haritası

**Bitti (2026-07-29):**
1. ✅ **Drift düzeltmesi** — oyun ortasında bozulma çözüldü (~27 cm/saat birikim telafi ediliyor). *Tamamen yerel.*
2. ✅ **Ortak kalibrasyon** — bir kişi kalibre eder, diğerleri ağdan alır. *Meta bulutunu kullanır.*
3. ✅ **Kalıcılık** — bir kez kalibre edilir, sunucu hatırlar; ertesi gün kimse kalibre etmez.

**Sırada:**
4. ⬜ **AprilTag fizibilite testi** (1-2 gün) — [PLAN-faz0-spike.md](PLAN-faz0-spike.md). Karar noktası.
5. ⬜ **AprilTag entegrasyonu** (2-3 gün) — Meta bağımlılığı biter, veri bize geçer.
6. 🔜 **Büyük alana taşıma** (2-4 gün) — işin ~%90'ı olduğu gibi taşınır.

**Bağımsız ve ucuz ara adım:** A/B noktalarını **bantla işaretlemek**. Şu an elle yaklaşık
alınıyor; co-location'ın tamamı o noktaların sabitliğine dayanıyor. Aynı yükseklikte iki işaret,
2-3 m arayla. AprilTag'e giden yolun da ilk adımı.

---

## 0.1 ÖLÇÜLEN: Drift hızı (2026-07-28, Quest 3, küçük oda)

Faz 1 cihaz testinde ölçüldü — **tahmin değil, gerçek sayı**:

| | Değer |
|---|---|
| Süre | ~8-10 dk oyun |
| Biriken kayma | **~4 cm** + küçük bir yaw |
| **Hız** | **~0.45 cm/dakika** |

Düzeltilmezse projeksiyon: **30 dk → ~13 cm**, **60 dk → ~27 cm**.
Multiplayer'da bağıl hata bunun ~2 katı olabilir (her gözlük bağımsız, farklı yönde kayar).

**Sonuçları:**
- "Oyun ortasında kalibrasyon bozuluyor" şikayetinin sayısal açıklaması budur.
- Bu hızda AprilTag (Faz 3) **opsiyonel değil**: anchor drift'i telafi ediyor ama anchor'ın
  kendisi mutlak gerçek değil, uzun oturumda onun da sapması birikir.
- Faz 3 planlanırken düzeltme sıklığı bu hıza göre seçilecek.

⚠️ Tek ölçüm, tek oda, tek oturum. Drift hızı oda dokusuna/ışığa/hareket miktarına göre
büyük değişir. Yön gösterici, kanun değil. Büyük alanda **yeniden ölçülmeli**.

## 0.2 Sahada öğrenilenler (2026-07-29 test günü)

Kod hatası olmayan ama işi saatlerce tıkayan şeyler — bir daha aynı tuzağa düşmeyin:

| Bulgu | Sonuç |
|---|---|
| **A/B noktaları fiziksel olarak İŞARETLİ DEĞİLDİ** (elle yaklaşık alınıyordu) | Co-location'ın tamamı bu noktaların sabitliğine dayanır. Yaklaşık nokta = her oyuncuda farklı çerçeve. **Bantla işaretlenmeli**, ikisi aynı yükseklikte ve 2-3 m arayla (uzun mesafe yaw hatasını küçültür). |
| **Gözlüğün zemin tahmini ~80 cm bozuktu** (`Kafa: 2.47 m` ölçüldü) | "Tavanda doğma" sorunu. Kodda değil cihazda — **Alan Kurulumu yenilenince düzeldi**. `Floor TAMAM` yazısı "Floor modu verildi" der, "zemin doğru yerde" DEMEZ. Sık takıp çıkarma bu hatayı tetikliyor. |
| **Unity build'i yalnızca o an bağlı gözlüğe kuruyor** | İki gözlükte farklı sürüm kalıp testleri günlerce yanıltabilir. Her build'den sonra `adb -s <serial> install -r <apk>` ile diğerine de kurun, sürümleri doğrulayın. |
| **Sunucu sanal adaptör adresi duyuruyordu** (VirtualBox/VMware) | Gözlükler hiç bağlanamıyordu. Düzeltildi (gateway'e göre seçim), ama başka PC'de tekrarlayabilir — log'da seçilen/elenen adresler yazıyor. |
| **Gözlükte Console yok** | Her teşhis bilgisi VR panelinde görünmeli, yoksa kör kalırsınız. Bu yüzden panele durum/teşhis satırları eklendi. Alternatif: `adb logcat -d -s Unity:V`. |

---

## 1. Terimler Sözlüğü

Bu dokümanda ve işin geri kalanında geçecek her terim. Sırayla okunursa sistem kendiliğinden anlaşılır.

### Tracking temelleri

| Terim | Anlamı |
|---|---|
| **SLAM** | *Simultaneous Localization and Mapping* — "Eşzamanlı Konumlanma ve Haritalama". Gözlüğün nerede olduğunu bulma yöntemi. Kameralar odadaki köşe/doku/kenarları izler, sen hareket ettikçe bunların görüntüde nasıl kaydığına bakıp hareketi geri hesaplar. Aynı anda odanın 3B haritasını kurar ve kendini o haritanın içine yerleştirir. **Quest'te zaten çalışıyor, bedava geliyor. Bizim yazacağımız bir şey değil.** |
| **Feature point** | SLAM'in takip ettiği görsel işaret: köşe, kenar, doku, kontrast noktası. Boş beyaz duvar = feature yok = kötü tracking. |
| **IMU** | İvmeölçer + jiroskop. Hızlı hareketlerde kameradan daha çabuk tepki verir; SLAM ikisini birleştirir. |
| **Dead reckoning** | "Adım sayarak yol bulma". Her adımda küçük bir hata yaparsın, hatalar birikir. SLAM'in temel zayıflığı bu. |
| **Drift** | Biriken hatanın sonucu: koordinat sisteminin gerçek dünyaya göre yavaşça kayması. **Bizim problemimizin adı.** |
| **Loop closure** | SLAM'in daha önce gördüğü bir yeri tanıyıp "burayı biliyorum" diyerek haritayı düzeltmesi. Faydalı ama düzeltme anında **tüm koordinat sistemi birden kayabilir** — oyun ortasında ani bozulmaların sebeplerinden. |
| **Relocalization** | Tracking kaybedildikten sonra (uyku, karanlık, kapatma) haritadan yeniden yer bulma. Sonrasında frame kaymış olabilir. |
| **Tracking origin / tracking space** | Gözlüğün kendi koordinat sisteminin sıfır noktası. Drift eden şey tam olarak bu. |
| **Recenter** | Kullanıcının/sistemin origin'i sıfırlaması. Bizim kalibrasyonu bozan olaylardan biri. |
| **Frame (koordinat çerçevesi)** | Bir referans sistemi. "Shared frame" = tüm oyuncuların anlaştığı ortak dünya. Bizde şu an: A noktası = origin, A→B = +Z. |
| **6DOF** | 6 serbestlik derecesi: 3 konum (x,y,z) + 3 dönüş (pitch, yaw, roll). Tam poz. |
| **Poz (pose)** | Konum + yön birlikte. |

### Anchor (çapa)

| Terim | Anlamı |
|---|---|
| **Spatial anchor** | Runtime'a "şu fiziksel noktayı özellikle takip et" demek. Harita yeniden düzenlenince runtime **çapanın koordinatlarını günceller** ki çapa gerçek dünyadaki yerinde kalsın. *Benzetme: sürekli yeniden çizilen bir krokideki bir noktayı gerçek duvara raptiyeyle tutturmak.* |
| **Persistence** | Çapayı kaydedip sonraki oturumda geri yükleyebilmek. Bir kez kalibre et, ertesi gün hazır gelsin. |
| **Shared anchor** | Aynı çapanın birden fazla gözlükte çözülebilmesi. Multiplayer co-location'ın temeli. |
| **Colocation** | Aynı fiziksel odadaki oyuncuların aynı sanal dünyada, gerçek mesafeleri koruyarak buluşması. **Projenin can damarı.** |
| **Group ID** | Shared anchor'ları paylaşmak için kullanılan ortak GUID. Ağ üzerinden biz dağıtacağız. |
| **Trackable / TrackableId** | AR Foundation'ın izlenen nesnelere verdiği kimlik. |
| **Tracking state** | Çapanın o an güvenilir izlenip izlenmediği. Düzeltme uygulamadan önce **mutlaka** kontrol edilmeli. |

### Marker / bilgisayarla görü

| Terim | Anlamı |
|---|---|
| **Fiducial marker** | Bilinen boyut ve desende, referans olarak kullanılan basılı işaret. |
| **AprilTag** | Michigan Üniversitesi APRIL lab'in robotik için geliştirdiği fiducial sistem. Uzak/eğik/loş koşullarda sağlam, **yanlış-pozitif oranı çok düşük**. Bizim seçimimiz. |
| **ArUco** | Yaygın alternatif, OpenCV'de yerleşik, çok hızlı. Hazır Quest örnekleri bunu kullanıyor. |
| **ChArUco** | ArUco + satranç tahtası. Sub-piksel hassasiyet ama tam görünürlük ister. Bize gerekmiyor. |
| **Tag family / 36h11** | AprilTag kod ailesi. `36h11` = 6×6 veri hücresi, minimum Hamming mesafesi 11. **Standart seçim, bizim kullanacağımız.** |
| **Hamming distance** | İki kod arasındaki bit farkı. Yüksek olması = bir tag'in yanlışlıkla başka bir tag olarak okunma ihtimalinin çok düşük olması. AprilTag'in güvenliği buradan geliyor. |
| **False positive** | Olmayan tag'i "gördüm" sanmak. Bizde sonucu ağır: **dünyanın yanlış yere anlık zıplaması.** AprilTag'i seçmemizin ana sebebi bunu minimize etmesi. |
| **PnP / solvePnP** | *Perspective-n-Point*. "Bilinen 3B noktalar görüntüde şu piksellerde görünüyor → nesne nerede duruyor?" problemini çözen algoritma. OpenCV'de hazır. |
| **Pinhole model** | Kameranın 3B'yi 2B piksele bastırma matematiksel modeli: `x_piksel = f · (X / Z)` |
| **Intrinsics** | Kameranın iç parametreleri: odak uzaklığı (f), optik merkez, lens distorsiyonu. `solvePnP` için şart. |
| **Extrinsics** | Kameranın gözlüğün neresinde durduğu (kafa merkezine göre ofseti). Kamera pozunu dünya pozuna çevirmek için gerekli. |
| **rvec / tvec** | `solvePnP`'nin çıktısı: dönüş vektörü ve konum vektörü. Birlikte = (kamera → tag) dönüşümü. |
| **Flip / pose ambiguity** | Düzlemsel bir kare küçük/uzak/cepheden görününce iki farklı eğim yorumu neredeyse aynı görüntüyü verir → poz ara sıra ters dönebilir. Çözüm: büyük tag, yakın mesafe, zaman tutarlılığı, çoklu tag. |
| **Occlusion** | Tag'in önüne bir şey girmesi (oyuncu, siper). Yedekli tag koymanın sebebi. |
| **Reprojection error** | Çözülen pozla köşelerin nereye düşmesi gerektiğini hesaplayıp gerçek tespitle karşılaştırma. **Kalite ölçütü** — yüksekse o ölçümü çöpe at. |
| **Survey** | Tag'lerin birbirine göre konumlarının bir kez ölçülüp kaydedilmesi. Bu olmadan çoklu tag tek ortak frame vermez. |

### Uygulama / mühendislik

| Terim | Anlamı |
|---|---|
| **Passthrough Camera API (PCA)** | Meta'nın Quest 3/3S ön kameralarına erişim API'si. Horizon OS v74+/v76+. Marker tespiti için **şart**. |
| **HEADSET_CAMERA** | Kamera erişimi için gereken Android izni (`horizonos.permission.HEADSET_CAMERA`). |
| **USE_SCENE** | Uzamsal veri izni (`com.oculus.permission.USE_SCENE`). **Zaten kullanıyoruz** — bkz. [`RoomScanSync.cs`](Assets/_VRMultiplayer/Scripts/RoomScan/RoomScanSync.cs). |
| **One Euro filter** | Gecikme/titreme dengesi iyi olan yumuşatma filtresi. CV çıktısını doğrudan uygulamak titretir; bu onu düzeltir. |
| **Timestamp alignment** | Kamera karesinin **çekildiği andaki** poz ile eşleştirilmesi. Yapılmazsa kafa hareketinde marker kayar (bilinen tuzak). |
| **Spike** | "Yapılabilir mi?" sorusunu ucuza cevaplamak için yapılan, **atılacak** kısa deneme. Ürün kodu değil, bilgi üretme işi. Zaman kutulu. |
| **Fallback** | Ana sistem çalışmadığında devreye giren yedek. Bizde: mevcut A/B kalibrasyonu. |

---

## 2. Sistemin Mantığı

Üç parça, rakip değil — ekip. Her biri diğerinin açığını kapatıyor:

| Parça | Ne verir | Açığı | Durum |
|---|---|---|---|
| **SLAM** | Sürekli, pürüzsüz, her yerde tracking | Mutlak referansı yok → kayar | **Zaten var** |
| **Anchor** | Kalibre noktanın ucuz, kalıcı, paylaşılabilir hafızası | Doğru yeri bilmez, yavaşça gezebilir | Yazılacak |
| **AprilTag** | Mutlak gerçek, drift'i sıfırlar, herkesi anlaştırır | Sadece görünürken, CPU + izin ister | Yazılacak |

**İş bölümü:** An-be-an işi SLAM+anchor yapar (ucuz, pürüzsüz). AprilTag ara sıra devreye girip onları gerçeğe geri çeken "gerçeklik kontrolü"dür (kesin, mutlak).

Her karede CV çalıştırmıyoruz (pil/CPU ölür) — tag göründükçe düzeltip aradaki zamanı SLAM+anchor ile idare ediyoruz.

> **Benzetme:** SLAM = adım sayar (pürüzsüz ama şaşar). Anchor = aklında tuttuğun yer işareti. AprilTag = ara sıra sokak tabelasına bakıp kafandaki konumu düzeltmek.

### AprilTag neden haritadan bağımsız (kritik nokta)

AprilTag, SLAM gibi "odayı tanıyıp eşleştirerek" çalışmaz. Tag'in **gerçek boyutunu önceden** biliriz (ör. 20 cm). Kamera onu gördüğü an poz **saf geometriden** çıkar:

```
Z = f · W / w        (f=odak uzaklığı, W=gerçek yarı-genişlik, w=piksel yarı-genişlik)
```

Hafıza yok, harita eşlemesi yok. **Gücü tam da bundan geliyor:** haritaya bağlı olsaydı, kayan haritanın drift'ini miras alır ve hiçbir şey kazandırmazdı.

Üç görsel ipucu → poz:
- **Boyut → uzaklık** (bilinen gerçek boyut sayesinde mutlak)
- **Şekil bozulması → eğim** (kare yamuklaşıyor; bozuk paranın elipse dönmesi gibi)
- **Görüntüdeki yer → yön**

4 köşe × 2 = 8 ölçüm, 6 bilinmeyen → çözülür, fazlası gürültüyü bastırır.

---

## 3. Yol Haritası

**Tahmini toplam: 5-9 iş günü** (büyük alan, tam kapsam). Cihaz üstü test döngüsü yavaş olduğu için takvim süresi kod süresinden uzun.

> **Şu anki küçük alan için ~4-7 gün** — ve Faz 5 ertelenebilir. Kapsam ayrımı için bkz. [Bölüm 4](#4-kapsam-şimdi-küçük-alan--sonra-büyük-alan).

Sıralama mantığı: **Riski öne al, değeri erken teslim et.** Spike en büyük belirsizliği 1-2 günde öldürür; sonra düşük riskli anchor işiyle somut kazanç gelir; tag en sona kalır.

---

### FAZ 0 — Spike: Fizibilite (1-2 gün) ⚠️ EN KRİTİK

> 📋 **Adım adım görev planı: [PLAN-faz0-spike.md](PLAN-faz0-spike.md)** — cihaz başında
> kullanılacak uygulama dokümanı (ön kontroller, ölçüm tablosu, karar kriterleri).

**Amaç:** Kod yazmadan "bu yapılabilir mi" sorusunu cevaplamak. Çıktısı bilgi, kod değil.

**Cevaplanacak sorular:**
1. Passthrough Camera API bizim yığınımızda (**AR Foundation + Unity OpenXR**, Meta XR Core SDK **yok**) çalışıyor mu? → **1 numaralı bilinmeyen, bkz. Risk R1**
2. OpenCV for Unity alınacak mı? (~$95 Asset Store) Ücretsiz alternatif yeterli mi?
3. Hazır örnek ([QuestArUcoMarkerTracking](https://github.com/TakashiYoshinaga/QuestArUcoMarkerTracking)) cihazda çalışıyor mu?
4. **Kendi odamızda, kendi ışığımızda** poz ne kadar stabil?

**Ölçülecek (bu sayılar sonraki fazları belirler):**
- Tag hangi **mesafeye** kadar okunuyor? (tag boyutuna göre)
- Hangi **açıya** kadar okunuyor? (oblik limit)
- Poz **jitter**'ı ne kadar? (mm cinsinden)
- Kare hızı ve CPU maliyeti

**Çıktı:** Kısa bir not — ölçülen menzil/açı/jitter + "yeşil/sarı/kırmızı" kararı. Kod atılır.

**Zaman kutusu: 2 gün. Sonuç ne olursa olsun dur ve değerlendir.**

---

### FAZ 1 — Anchor Omurgası ✔️ TAMAMLANDI (commit `4d40d80`)

> Cihazda doğrulandı: ~8-10 dk oyunda ~4 cm kayma yakalanıp düzeltildi.
> Kalıcılık (kaydet/yükle) hâlâ YOK — bilinçli ertelendi.

<details><summary>Özgün plan (referans)</summary>

**Neden önce bu:** `com.unity.xr.meta-openxr 2.5.0` **zaten kurulu**. Yeni paket, yeni izin, CV yükü yok. Tek başına bile kazanç sağlıyor.

**Yapılacak:**
1. A/B kalibrasyonu bittiğinde, hesaplanan origin'e bir `ARAnchor` oluştur (`ARAnchorManager.TryAddAnchorAsync(pose)`).
2. Rig ofsetini **bir kez** hesaplayıp bırakmak yerine, anchor'ın **canlı pozundan her güncellemede yeniden türet**. (Mevcut `Apply()` matematiği aynen kullanılabilir — sadece girdisi değişiyor.)
3. Anchor'ın `tracking state`'ini kontrol et; güvenilir değilse düzeltme uygulama.
4. Persistence: `TrySaveAnchorAsync` ile kaydet, açılışta `TryLoadAnchorsAsync` ile yükle → "her oturumda yeniden kalibre etme" derdi biter.

**Kazanç:** SLAM harita düzeltmelerine karşı dayanıklılık + oturumlar arası kalıcılık.

---

</details>

---

### FAZ 2 — Shared Anchor / Multiplayer ✔️ TAMAMLANDI (commit `ab9016d`)

> Cihazda doğrulandı: **2. gözlük A/B'ye hiç basmadan kalibre oldu.**
> Uzun oturum ve iki-gözlük eşzamanlı hiza ölçümü yapılmadı.

**Uygulanan kararlar:** ilk yayınlayan çerçeveyi kilitler (sahibi tazeleyebilir, sahip
çıkarsa sahiplik serbest kalır ama çerçeve korunur); çerçeveyi alan A/B'ye basmaz;
takım seçiminden sonra 12 sn ortak çerçeve beklenir.

**⚠️ Mekân planlamasını ilgilendiren kısıt:** Meta'nın dokümanına göre shared spatial
anchor **Enhanced Spatial Services** ayarını ve nokta bulutu verisi Meta sunucularından
geçtiği için **internet** gerektiriyor. İnternetsiz bir salonda Faz 2 çalışmaz —
o durumda ya herkes A/B yapar (Faz 1 yine drift'i düzeltir) ya da **AprilTag** devreye
girer. Bu, Faz 3'ün önceliğini artırıyor.

<details><summary>Özgün plan (referans)</summary>

**Yapılacak:**
1. `MetaOpenXRAnchorSubsystem.isSharedAnchorsSupported` kontrolü.
2. Host bir group GUID üretir, `sharedAnchorsGroupId`'ye atar.
3. Anchor'ı paylaş: `TryShareAnchorAsync()`.
4. GUID'i Netcode üzerinden istemcilere dağıt (**altyapı zaten var** — `RoomScanSync` chunk gönderiyor).
5. İstemciler `TryLoadAllSharedAnchorsAsync()` ile yükleyip aynı çapaya kilitlenir.

**Kazanç:** Oyuncuların birbirinden ayrışması durur. Herkes tek fiziksel referansta.

**Not:** Shared anchor'lar son paylaşımdan itibaren ~30 gün yaşıyor. Paylaşım geri alınamıyor. Batch işlemler ya hep ya hiç.

---

</details>

---

### FAZ 3 — AprilTag Tespiti (1-2 gün)

Faz 0 yeşilse başlar. Spike kodunu **üretim kalitesine** taşıma işi.

**Yapılacak:**
1. Passthrough kamera erişimi + `HEADSET_CAMERA` izin akışı (kullanıcıya açıklayıcı ekran).
2. OpenCV `aruco` modülü, **`DICT_APRILTAG_36h11`** sözlüğü ile tespit.
3. `solvePnP` → (kamera → tag) pozu.
4. **Timestamp alignment:** kareyi, çekildiği andaki kamera pozuyla eşleştir (aksi halde kafa hareketinde marker kayar).
5. Kalite kapısı: `reprojection error` eşiği + minimum piksel boyutu → kötü ölçümü **at**.
6. Performans: her karede değil, **kısıtlı frekansta** çalıştır (ör. 5-10 Hz yeter).

---

### FAZ 4 — Düzeltme Döngüsü + Durum Makinesi (1 gün)

Sistemin beyni. Üç sorumluluk:

1. **Düzeltme hesabı:** "tag'in olması gereken yer" ile "şu an ölçülen yer" farkı = drift. Tersini uygula.
2. **Yumuşatma:** One Euro filter veya eşik-kapılı düzeltme. Her karede sert `snap` **yapma** — titrer ve oyuncuyu rahatsız eder. Küçük hataları yavaşça, büyükleri (kopma sonrası) daha hızlı düzelt.
3. **Durum makinesi — kime ne zaman güvenilecek:**
   - Tag görünür + kalite iyi → düzelt
   - Tag yok → anchor + SLAM ile "coast" et
   - Anchor tracking kaybı → A/B fallback'e düş, kullanıcıyı uyar
   - Ani büyük fark → **şüphelen**, tek ölçümle zıplama; birkaç tutarlı ölçüm bekle

**Ayrıca:** drift/tracking olaylarını dinle (`InputTracking.trackingLost/Acquired`, `XRInputSubsystem.trackingOriginUpdated`) → sessizce bozuk oynamak yerine kullanıcıyı uyar.

---

### FAZ 5 — Çoklu Tag + Survey (0.5-1 gün) ⏸️ ŞİMDİLİK ERTELENDİ

> **Küçük alanda gerekmiyor** — tek tag zaten kendi başına origin, survey diye bir iş yok.
> Bu faz büyük alan geldiğinde yapılacak. Aşağıdaki yerleşim tablosu o zaman için referans.
> Yine de Faz 3-4 yazılırken "tek tag" varsayımı koda gömülmemeli (bkz. Tasarım kuralı 7).

**Sorun:** Her tag'in dünyada nerede olduğu bilinmezse, farklı tag'ler farklı sonuç verir.

**Çözüm:** Bir kez survey — tag'lerin birbirine göre konumlarını ölç ve kaydet. Sonrasında hangi tag görülürse görülsün aynı ortak frame'e çözülür.

**Yerleşim (25 m² ≈ 5×5 m için):**

| Karar | Değer | Gerekçe |
|---|---|---|
| **Adet** | ~8-12 (10 iyi) | Teorik taban 2-4; shooter'da occlusion + oyuncunun tag'e bakmaması için yastık |
| **Boyut** | 20-30 cm | Quest passthrough düşük çözünürlüklü; cömert ol. 20 cm ≈ 4-6 m menzil |
| **Baskı** | **Mat**, düz, kırışıksız | Parlama tespiti öldürür |
| **Yükseklik** | Göz/göğüs hizası, **siper üstü** | Yerde/tavanda oblik görünür, nadiren bakılır |
| **Dağılım** | Dört duvara + iç duvarlara yay | Kümeleme yapma; her konumdan biri cepheye yakın olsun |
| **Aile** | `36h11`, her tag benzersiz ID | Standart, bol ID, yüksek Hamming |

**Not:** Bu odada menzil darboğaz değil — tek tag odanın bir ucundan diğerini görebiliyor. Tag'i **yön kapsaması** ve **occlusion** için çoğaltıyoruz. Kesin adet Faz 0'da ölçülen menzille netleşir.

---

### FAZ 6 — Saha Testi + Polish (1-2 gün, yayılmış)

- Gerçek ışıkta, gerçek maçta test
- Işık değişimi, parlama, hızlı hareket, tag kapanması senaryoları
- Kullanıcı geri bildirimi: "kalibre", "düzeltiliyor", "tag görünmüyor" durumları görünür olmalı
- A/B fallback yolunun hâlâ çalıştığını doğrula
- Performans profili: CPU/pil etkisi

---

## 4. Kapsam: Şimdi Küçük Alan / Sonra Büyük Alan

**Durum:** Şu an elimizde küçük bir alan var. Büyük alan ileride gelecek; çalışmalar şimdiden onun için başlatıldı.

### Süre alanla pek değişmiyor

İşin maliyeti alanla değil, **yazılımla** orantılı. 7 fazın 5'i oda büyüklüğünden tamamen bağımsız:

| Faz | Alandan etkilenir mi? | Büyük alan | Küçük alan (şimdi) |
|---|---|---|---|
| 0 — Spike | Hayır | 1-2 gün | 1-2 gün |
| 1 — Anchor omurgası | Hayır | 1-1.5 gün | 1-1.5 gün |
| 2 — Shared anchor | Hayır | 0.5-1 gün | 0.5-1 gün |
| 3 — Tag tespiti | Hayır | 1-2 gün | 1-2 gün |
| 4 — Düzeltme + durum makinesi | Hayır | 1 gün | 1 gün |
| 5 — Çoklu tag + survey | **Evet** | 0.5-1 gün | **~0.25 gün / ertele** |
| 6 — Saha testi | Kısmen | 1-2 gün | 0.5-1 gün |
| **TOPLAM** | | **5-9 gün** | **~4-7 gün** |

"Quest'ten kamera görüntüsü alıp tag'in pozunu çözmek ve rig'e uygulamak" 3 m²'de de 300 m²'de de aynı kod. Kazanç yalnızca Faz 5-6'dan: **10 tag yerine 1-2 tag** → survey diye bir iş yok, test döngüsü hızlı.

### Şimdiki iş büyük alana taşınıyor

Bu, kötü haber değil — **iyi** haber: şimdi yapılan işin ~%90'ı olduğu gibi taşınır. Büyük alana geçince yeniden yapılacaklar sadece:

- **Faz 5** — tag yerleşimi + gerçek survey (1-2 gün)
- **Faz 6** — geniş alanda yeniden test (1-2 gün)

**Büyük alanın marjinal maliyeti: 2-4 gün.** Sıfırdan proje değil.

### Küçük alan = ucuz laboratuvar

- Test iterasyonu hızlı (Faz 0'ın "cihaza build et, dene" döngüsü zaten yavaş; küçük alanda en azından koşuşturma yok)
- Tek tag'le tüm boru hattı doğrulanabilir
- Büyük alana **çalıştığı kanıtlanmış** sistemle gidilir

> Kaçınılan senaryo: büyük alan hazır olduğunda oraya test edilmemiş bir sistemle gitmek.

### Büyük alanda sertleşen şey: açısal hata mesafeyle büyür

1°'lik yaw hatasının fiziksel karşılığı:

| Mesafe | Sapma |
|---|---|
| 3 m | ~5 cm (fark edilmez) |
| 10 m | ~17 cm |
| 20 m | **~35 cm** (kabul edilemez) |

**Sonuç:** Küçük odada "yeterince iyi" görünen hassasiyet büyük alanda yetmez.

**İki pratik kural:**
1. Faz 0'da jitter/hassasiyeti **sayıyla ölç** (mm cinsinden), "gözüme iyi geldi" deme — büyük alandaki karşılığını hesaplayabilmek için.
2. Küçük odada drift az hissedilir; "düzeldi mi?" sorusunu gözle yanıtlayamazsın. Bilinen bir fiziksel noktaya sanal işaret koyup **zamanla kaymayı ölç.**

### Tarama artık opsiyonel — bu kalibrasyonun ÖNEMİNİ ARTIRIYOR

[PLAN-creator-mode.md](PLAN-creator-mode.md) karar #11 ile alan artık taranmadan da kurulabiliyor:
duvarlar gözlükte elle çiziliyor (gerçek duvarın iki ucuna dokun → duvar üretilir). Gerekçe
Quest 3 Space Setup'ın büyük/boş alanlarda zorlanması.

**Bu kararın kalibrasyona etkisi sezgiye ters:** tarama kalkınca kalibrasyon *daha az* değil,
**daha çok** kritik oluyor.

| | Taramalı düzen | Elle çizilmiş düzen |
|---|---|---|
| Sanal duvarın kaynağı | Gerçek duvardan **türetilmiş** | Ortak çerçevede **kaydedilmiş ölçüm** |
| Drift olunca ne olur | Sanal duvar gerçeğinden ayrılır | **Her şey birlikte kayar** |
| Fark edilir mi | Passthrough'da **gözle görülür** | **Görünmez** |

Taramalı düzende drift kendini ele veriyordu. Elle çizilmiş haritada böyle doğal bir geri
bildirim yok — harita da, proplar da, siperler de aynı çerçevede olduğu için hep birlikte
kayıyorlar ve içeriden bakınca hiçbir şey yanlış görünmüyor. Sonuç: oyuncu, gerçek engelin
**artık olmadığı** yere siper alıyor.

**Üç sonuç:**
1. **Drift'i sistem tespit etmeli, göz değil.** Faz 4'ün durum makinesi ve kullanıcı uyarısı
   opsiyonel bir konfor değil, güvenlik gereği.
2. **Hassasiyet şartı yükseliyor.** Elle çizilen duvar zaten ±5 cm hedefliyor
   (creator planı C kabul kriteri); üstüne kalibrasyon hatası binerse bütçe hızla tükenir.
3. **Faz 2 önceliği artıyor.** Creator planı `venueId` alanını Faz 2'nin ürettiği shared anchor
   group GUID'ine bağlıyor. Bu bağ, "harita hangi fiziksel referansın üstüne kuruldu"
   sorusunun tek cevabı — elle çizilmiş haritada bunu doğrulayacak başka hiçbir şey yok.

### Şimdiki plan (yalın sürüm)

**Faz 0 → 4'ü tek tag'le tam kur (~4-6 gün). Faz 5'i (çoklu tag + survey) büyük alan gelene kadar ertele.**

Bu haliyle bugünkü "oyun ortasında bozulma" ve "multiplayer ayrışması" dertleri çözülür; büyük alan geldiğinde elde çalışan sistem + ölçülmüş sayılar olur. Faz 0'da ölçülen menzil, büyük alandaki tag adedini de doğrudan verecek.

---

## 5. Nelere Dikkat Edilmeli

### Riskler

**R1 — Backend uyumsuzluğu (EN BÜYÜK RİSK)**
Biz **AR Foundation + Unity OpenXR** kullanıyoruz (`MetaOpenXRSessionSubsystem`, `ARPlaneManager`). Passthrough Camera API örnekleri **MRUK / Meta XR SDK** ister. MRUK'un QR takibi ayrıca **OVRCameraRig** ister — bu bizim rig'imizden farklı.
→ **Bu yüzden MRUK QR yolunu seçmedik.**
→ Faz 0'ın 1 numaralı görevi: kamera erişiminin bizim yığınımızda çalıştığını **kanıtlamak**. Çalışmazsa alternatif: WebCamTexture tabanlı erişim + extrinsics'i elle tanımlama (daha çok iş).
→ **Asla:** çalışan room-scan pipeline'ını (`ARPlaneManager`) bozacak bir SDK göçüne körlemesine girme.

**R2 — Editor'da test edilemez**
Passthrough Camera API **Editor ve XR Simulator'da çalışmaz**. Her test cihaza build. Geliştirme döngüsü yavaş → **takvim süresi kod süresinden uzun.** Planlarken buna göre pay bırak.

**R3 — Yanlış tespit = dünyanın zıplaması**
False positive'in bedeli ağır. Savunma: AprilTag'in yüksek Hamming'i + reprojection error eşiği + tek ölçümle asla zıplama.

**R4 — Flip ambiguity**
Uzak/küçük/cepheden tag'de poz ters dönebilir. Savunma: büyük tag, yakın mesafe, zaman tutarlılığı, çoklu tag.

**R5 — Timestamp kayması**
Kamera karesi ile poz eşleşmezse kafa hareketinde marker kayar. Bilinen tuzak — PCA timestamp veriyor, **kullan**.

**R6 — Titreme**
Ham CV çıktısını doğrudan uygulamak titretir. Yumuşatma **opsiyonel değil**.

**R7 — Görünmez drift (elle çizilmiş haritada)**
Tarama olmadan kurulan haritada drift kendini ele vermez — harita, proplar ve siperler aynı
çerçevede olduğu için hep birlikte kayar, içeriden bakınca hiçbir şey yanlış görünmez. Oyuncu
gerçek engelin artık olmadığı yere siper alır. → Drift'i **sistem** tespit edip **söylemeli**;
gözle doğrulamaya güvenme. Bkz. [Bölüm 4](#tarama-artık-opsiyonel--bu-kalibrasyonun-önemini-artırıyor).

### Kurulum / bağımlılık kontrol listesi

- [x] Unity **6000.3.18f1** — PCA için gereken 6000.0.38f1+ şartını karşılıyor ✅
- [x] `com.unity.xr.meta-openxr` **2.5.0** kurulu — anchor/shared anchor için yeterli ✅
- [ ] Quest 3 Horizon OS **v74+** (PCA v76'da yayınlandı) — cihaz sürümünü doğrula
- [ ] `horizonos.permission.HEADSET_CAMERA` — manifest + runtime izin akışı
- [x] `com.oculus.permission.USE_SCENE` — zaten var
- [ ] OpenCV for Unity (~$95) veya ücretsiz alternatif — **karar Faz 0'da**
- [ ] AprilTag `36h11` baskıları — mat, tam ölçülü

### Tasarım kuralları (uygulamada uyulacak)

1. **A/B fallback'i asla silme.** Tag görünmediğinde, kamera izni reddedildiğinde, cihaz desteklemediğinde tek kurtarıcı o.
2. **Kademeli güven:** tag > anchor > SLAM > A/B. Her kademe bir üstü yoksa devreye girer.
3. **Sessizce bozulma.** Sistem kalibrasyonun bozulduğunu anladığında kullanıcıya **söyle**. Şu anki en büyük şikayet "fark etmeden bozuluyor".
4. **Ortak frame tanımı değişmiyor.** A = origin, A→B = +Z sözleşmesi aynı kalacak; anchor/tag sadece o çerçevenin *sürekli* tanımı olacak. → Hem room-scan pipeline'ı ([`RoomPlanData.cs`](Assets/_VRMultiplayer/Scripts/RoomScan/RoomPlanData.cs), `RoomScanSync`) hem de creator'ın elle çizilmiş haritaları (`MapLayout`) **değişmeden çalışmalı**. Bugün kaydedilen harita, anchor/AprilTag geldikten sonra da geçerli olmalı.
5. **Ölç, tahmin etme.** Tag adedi/boyutu/aralığı Faz 0'da ölçülen menzilden çıkacak.
6. **Performans:** CV'yi her karede çalıştırma. 5-10 Hz yeter, pil/CPU bütçesini koru.
7. **Tag yerleşimi KOD DEĞİL, VERİ olsun.** Tag ID'leri ve konumları bir config/ScriptableObject'ten okunsun; sahneye/koda gömülmesin. Büyük alana geçiş o zaman "yeni survey dosyası + yeni baskılar" olur, kod değişikliği değil. **Şimdi yapmak bedava, sonra yapmak refactor.** Aynı sebeple "tek tag var" varsayımını koda gömme — bir tag'lik liste, N tag'lik listenin özel hali olsun.

### Bilinen sınırlar

- Shared anchor'lar ~**30 gün** yaşıyor (son paylaşımdan itibaren). Paylaşım **geri alınamaz**. Batch işlemler **ya hep ya hiç**.
- Marker tespiti sadece **Quest 3 / 3S** (Quest 2 yapamaz).
- Tag pozu düşük frekansta güncellenir — sabit duvar marker'ı için sorun değil, hareketli nesne için uygun değil.

---

## 6. Uygulama İçin Prompt

Bu işi yaptırmak istediğinde aşağıdakini kopyala. Faz seçerek küçük parçalar halinde ilerlemek en sağlıklısı.

### Genel prompt

```
PLAN-kalibrasyon.md dosyasını oku. Kalibrasyon drift problemini anchor + AprilTag
ile çözüyoruz.

Şu an FAZ <N> üzerinde çalışmak istiyorum: <faz adı>

Bağlam:
- Yığın: Unity 6000.3.18f1, AR Foundation + Unity OpenXR (com.unity.xr.meta-openxr 2.5.0).
  Meta XR Core SDK / OVRCameraRig KULLANMIYORUZ.
- Mevcut kalibrasyon: Assets/_VRMultiplayer/Scripts/XR/CalibrationManager.cs (A/B, tek
  seferlik). Bu FALLBACK olarak kalacak, silinmeyecek.
- Ortak frame sözleşmesi değişmiyor: A = origin, A→B = +Z.
  Room-scan pipeline'ı (RoomScanSync.cs) bozulmamalı.
- Multiplayer: Netcode for GameObjects, LAN.
- KAPSAM: Şu an KÜÇÜK alan var, büyük alan ileride gelecek. Faz 5 (çoklu tag + survey)
  ertelendi — tek tag'le ilerliyoruz. Ama tag yerleşimi KOD DEĞİL VERİ olmalı ve "tek tag"
  varsayımı koda gömülmemeli; büyük alana geçiş config değişikliği olsun.

İstediğim:
1. Önce bu fazın plan içindeki yerini ve neye dokunacağını özetle.
2. Kod yazmadan önce tasarımı anlat (hangi dosya, hangi sorumluluk, hangi API).
3. Onay verdikten SONRA kodu yaz.
4. Editor'da test edilemeyen kısımları açıkça belirt; cihazda nasıl doğrulayacağımı yaz.

Emin olmadığın API detaylarını uydurma — araştır veya bilmediğini söyle.
```

### Faz 0 (spike) için özel prompt

```
PLAN-kalibrasyon.md'deki FAZ 0 spike'ını yapmak istiyorum.

En kritik bilinmeyen (R1): Passthrough Camera API bizim AR Foundation + Unity OpenXR
yığınımızda, Meta XR Core SDK'ya geçmeden çalışıyor mu?

Bana şunu ver:
1. Adım adım spike planı — hangi paket, hangi izin, hangi örnek repo, hangi sırayla.
2. R1'i en hızlı test edecek minimum kurulum (mevcut projeyi kirletmeden; ayrı test
   sahnesi veya ayrı proje).
3. Cihazda ölçeceğim şeylerin listesi ve nasıl ölçeceğim: menzil, oblik açı limiti,
   jitter (mm), kare hızı, CPU.
4. Kırmızı bayraklar: hangi durumda "bu yol tıkalı" deyip alternatife geçmeliyim.

Zaman kutusu 2 gün. Bu bir spike — kod atılacak, ürün kalitesi arama.
```

### Faz 1 (anchor) için özel prompt

```
PLAN-kalibrasyon.md'deki FAZ 1 — Anchor Omurgası'nı uygulamak istiyorum.

Hedef: CalibrationManager'ın tek seferlik rig ofsetini, ARAnchor'ın canlı pozundan
sürekli türetilen bir sisteme çevirmek. Persistence de olsun (kaydet/yükle).

Kısıtlar:
- com.unity.xr.meta-openxr 2.5.0 zaten kurulu, yeni paket ekleme.
- Mevcut Apply() matematiği korunsun, sadece girdisi değişsin.
- Anchor tracking state güvenilir değilse düzeltme UYGULAMA.
- A/B fallback yolu çalışır kalsın.
- Room-scan pipeline'ı bozulmasın.

Önce tasarımı anlat, onaydan sonra kod yaz.
```

---

## 7. Kaynaklar

**Anchor (bizim yığınımız):**
- [Meta Quest Anchors feature — Unity OpenXR Meta](https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.4/manual/features/anchors/anchors-feature.html)
- [Shared anchors — Unity OpenXR Meta](https://docs.unity3d.com/Packages/com.unity.xr.meta-openxr@2.5/manual/features/anchors/shared-anchors.html)

**Passthrough Camera / marker:**
- [Getting Started with Passthrough Camera API in Unity](https://developers.meta.com/horizon/documentation/unity/unity-pca-documentation/)
- [Unity-PassthroughCameraApiSamples (resmi örnek)](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples)
- [QuestArUcoMarkerTracking (hazır marker örneği)](https://github.com/TakashiYoshinaga/QuestArUcoMarkerTracking)
- [QuestCameraKit (vision şablonları)](https://github.com/xrdevrob/QuestCameraKit)

**AprilTag:**
- [AprilTag: A robust and flexible visual fiducial system (Olson 2011, orijinal makale)](https://april.eecs.umich.edu/pdfs/olson2011tags.pdf)
- [AprilTags in Unity: shared spatial anchor'a yerel alternatif (Sensors 2025)](https://www.mdpi.com/1424-8220/25/14/4408)

**Seçmediğimiz yol (neden olmadığını hatırlamak için):**
- [MRUK QR code tracking](https://developers.meta.com/horizon/documentation/unity/unity-mr-utility-kit-qrcode-detection/) — Meta XR Core SDK v83+ ve OVRCameraRig istiyor, bizim backend'imizle çakışıyor.
