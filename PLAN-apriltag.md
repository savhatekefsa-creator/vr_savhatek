# Görev: AprilTag tabanlı ortak çerçeve — anchor'ın yerine geçmesi

> Bu dosya, işi ilerleyen günlerde yaptırmak için hazırlanmış bir görev tanımıdır.
> Yeni bir oturumda bunu ver, sıfırdan keşif yapmaya gerek kalmasın.
> En alttaki **"Bu işi başlatırken verilecek prompt"** bölümünü kopyalayıp yapıştır.
>
> **Bu plan `PLAN-apriltag-uyarlama.md`'nin devamı değil, üstüdür.** O dosya kütüphaneyi
> projeye almanın planıydı ve **bitti** (tespit çalışıyor, cihazda ölçüldü: 1 m'de 3 mm).
> Bu dosya, tespitin üstüne kurulacak SİSTEMİ tarif eder.

---

## Neden — çözülen gerçek problem

Bugün ortak çerçeve **Meta shared spatial anchor**'a dayanıyor. Ölçülen sonuç: çalışıyor ama
üç ayrı yerden sızdırıyor.

**1. Anchor drift ediyor, tag'i eziyor.** İki sistem aynı rig'i yazıyor, aralarında hakem yok:

```
AprilTagCalibration.Update()      -> rig.RotateAround(...) + rig.position += ...   (goreli)
CalibrationAnchor.LateUpdate():473 -> rig.SetPositionAndRotation(pos, rot)         (MUTLAK)
```

LateUpdate sonra çalışır. Anchor sürüyorken tag'in düzeltmesi **aynı karede tamamen siliniyor.**
Belirti: "tag'e bakıyorum, sadece ölçüyor, hiçbir şey yapmıyor." Doğru — rig üzerinde kalıcı
etkisi yok.

`d1176d6` bu kavgayı fark edip tag'in **kendi** `Bind()` çağrısını kaldırdı (cihazda doğrulandı).
Ama anchor'ı uyandıran **ikinci kapı** açık kaldı: ağdan/diskten gelen paylaşılan çerçeve
(`CalibrationAnchor.cs:358` → `_driving = true`).

**2. Kalıcılık, taze ölçümün önüne geçiyor.** `5bfed11` sunucuya grup GUID'ini diske yazdırıyor
(`SharedCalibration.json`). Tek gözlükle girsen bile PC dünkü çerçeveyi yükleyip push ediyor —
sen tag'e bakmadan kalibre oluyorsun ve "KALİBRASYON AĞDAN GELDİ" görüyorsun.

**3. Çok oyunculu ortak çerçeve pahalı kuruluyor.** `CalibrationShareSync` 415 satır: grup GUID
dağıtımı, "ilk yayınlayan sahiplenir" kilidi, pull-first yedeği, disk kalıcılığı, 25 günlük
Meta paylaşım süresi uyarısı. Hepsi **anchor'ı paylaşmak** için.

### Tag bunların üçünü birden siliyor

Duvardaki basılı tag drift etmez, uykuda kaymaz, paylaşılması gerekmez. Aynı fiziksel tag'i
gören her gözlük **bağımsız olarak** aynı çerçeveye varır — fiducial marker'ın klasik faydası.

| Bugünkü dert | Tag tabanlı çerçevede |
|---|---|
| Anchor drift ediyor | Anchor yok |
| Anchor tag'i eziyor | Tek sürücü, kavga yok |
| `SharedCalibration.json` dünkü çerçeveyi dayatıyor | Kalıcılığa gerek yok — tag duvarda zaten kalıcı |
| 415 satırlık GUID dağıtımı + sahiplenme kilidi | Gereksiz |
| A/B dokunma adımı | Gereksiz |

---

## Hedef akış

```
KURULUM (mekana ilk gidiste, bir kez)
1. Tag 0'i DUVARA as   -> orijin. Yerden yuksekligini (h) BIR KEZ metreyle olc.
2. DOGRULA (C maddesi) -> bant referanslariyla tekrarlanabilirlik testi
3. TEK TAG ile tasarimi yap  <-- tutarsizlik matematiksel olarak imkansiz
4. Sonra diger tag'leri as; ogrenme moduyla olculurler
5. Uyustuklarini dogrula (Test C), sonra guven

OYUN (her seferinde)
6. Gozlugu tak, oyuna gir
7. Herhangi bir tag'i gor    -> cerceve kurulu, kalibre oldun
8. Oyun sirasinda tag kadraja girdikce drift duzeltilir
```

**3. adımın sırası kritik:** tasarım tek tag'le yapılırsa tag'ler arası tutarsızlık riski hiç
doğmaz (karar #8). Çoklu tag sonra, doğrulanarak eklenir.

---

## Yeni mekân prosedürü — tekrar tekrar kullanılacak olan

Sıfırdan bir mekân (boş oda, harita yok). **Oda taraması gerekmez** (creator planı karar #11).

```
1. Tag 0'i duvara as   — goz hizasina yakin, DUZ, saglam ve tasinmayacak bir duvara
2. Yerden yuksekligini olc  ->  h        (serit metre, TEK olcum, +-5 mm yeter)
3. Yerlesim:  id 0, position (0, h, 0), yawDegrees 180
4. Tag'e bak  ->  kalibre oldun
5. Creator modda duvarlari ciz, proplari koy, kaydet
```

**Neden bu kadar basit:** korunacak eski bir dünya yok. Bugünkü göç (Mod 2) zordu çünkü
mevcut sahne eski orijine bağlıydı; yeni mekânda o bağ hiç kurulmuyor.

**`yawDegrees: 180` her mekânda aynı** — bu sayı odaya değil, tespit kütüphanesinin yön
sözleşmesine ait. Tag duvara düz asıldığı sürece değişmez. Sonucu: `+Z`, tag'den odanın
içine doğru bakar.

**İkinci ve sonraki tag'ler:** tag 0'dan kalibre ol, `learnMode`'u aç, her tag'e 1.5 m'den
yakın bak, panelde çıkan sayıları yerleşime ekle, `learnMode`'u kapat. Tag 0 ölçüm
gerektirmediği için zinciri o başlatır (karar #1).

---

## Sıfır noktası nereden geliyor — arka plan

Bu proje baştan beri şu sözleşmeyle çalışıyor (`CalibrationManager.cs:15-18`):

> "Sağ kumandayı fiziksel A noktasına koy (**the shared origin**)... Rig, **A → `sharedOrigin`**
> ve **A→B → `sharedForward`** olacak şekilde yeniden merkezlenir."

Sahnede `sharedOrigin = (0,0,0)`, `sharedForward = (0,0,1)`. Yani **odanın belirli bir fiziksel
noktası haritanın `(0,0,0)`'ı seçilmiş**, sahne de ona göre çizilmiş. Oda taraması bunu
doğruluyor: duvarlar `X ∈ [-2.94, 0.35]`, `Z ∈ [-5.51, -0.92]` — orijin odanın kenarında.

**Sözleşmenin zayıflığı: orijin GÖRÜNMEZ.** Duvarda bir çentik ya da birinin hafızasında bir
nokta. 2026-07-31'de tam bu yüzden zorlandık — eski A noktasını bulmak ve oraya kumanda
değdirmek gerekti.

**Tag'in asıl kazancı bu:** görünmez bir konvansiyonu duvara asılı fiziksel bir nesneye
çeviriyor. Kaybolmuyor, unutulmuyor, ve gözlük onu insandan hassas okuyor (1 m'de 3 mm).
Drift düzeltmesi ikincil fayda.

A/B yok, anchor yok, ağ üzerinden çerçeve dağıtımı yok.

---

## Verilmiş kararlar — bunları tekrar tartışma

1. **Tag 0 = oyunun orijini.** Konumu ölçülmez, **tanım gereği** `(0, h, 0)`'dır.
   Bu, tavuk-yumurta sorununu çözer: `learnMode` bir tag'i ölçmek için önce kalibre olmayı
   şart koşar (`AprilTagCalibration.cs:330`). Tag 0 ölçüm gerektirmediği için zinciri o başlatır,
   sonra diğerleri ona göre ölçülür.

2. **Tag 0 DUVARA asılır**, zemine değil. Yerleşimi `(0, h, 0)`; `h` = yerden yüksekliği,
   bir kez şerit metreyle ölçülür. Orijin, tag'in **zemine izdüşümüdür** (fiziksel olarak
   işaretlenmesi gerekmeyen sanal bir nokta).

   Zemin önce düşünüldü ("`h = 0`, sıfır manuel ölçüm") ama duvar üç yönden öne geçiyor:
   - **Hassasiyet:** tag 0 orijindir, diğer her şey ona göre ölçülür — poz kestirimi en iyi
     olmalı. Duvara **dik** bakılır; zemindeki tag'e ayakta 2 m'den ~50° eğik bakılır ve
     AprilTag eğik açıda hassasiyet kaybeder. Ölçülen 3 mm neredeyse dik bakıştaki değerdir.
   - **Çift iş:** duvardaki tag 0 aynı zamanda oyun içi kapsama tag'idir. Zemindeki yalnızca
     ilk kalibrasyonda işe yarar — nişancı oyununda kimse aşağı bakmaz.
   - **Dayanıklılık:** üstünde durulmaz, aşınmaz, insan kapatmaz.

   Karşılığı tek bir kerelik ölçüm ve hatası yalnızca **dikey** eksende.

3. **Tag'in iki işi vardır, ikisi de duvardan görülür.**
   - *Çerçeveyi kurmak* — bilinçli eylem
   - *Oyun sırasında drift düzeltmek* — fırsatçı, tag kadraja girdiğinde

   Tag 0 ikisini birden yapar. Sonradan eklenen duvar tag'leri yalnızca ikincisi içindir.

4. **Tag tek otoritedir. Anchor rig'i SÜRMEZ.** `CalibrationAnchor`'ın rig yazma yolu kapanır.
   Bileşen silinmez (paylaşım/kalıcılık kodu tarihte kalsın) ama sürücü olarak devre dışıdır.

5. **A/B akışı GÖRÜNMEZ yapılır, SİLİNMEZ.** `PLAN-00-SIRA.md` durma kuralı: *"A/B fallback'i asla
   silme."* Geçerli. Tag düşerse, kâğıt yırtılırsa, ışık kötüyse tek kurtarıcı odur.
   `CalibrationManager`'ın durum paneli ve Y tuşu yeniden kalibrasyonu da kalır.

6. **`tagLayout` VERİDİR, sahnede serileşmez.** Şu an sahne bileşeninde duruyor; öyle kalırsa
   yeni mekân = Unity Editor + yeni APK. Bu, creator mode'un kaçtığı şeyin aynısı.
   Mekân verisi olarak kaydedilip yüklenir.

7. **Çerçeve sözleşmesi DEĞİŞİYOR** — ve bu, diğer planları etkiler. Aşağıdaki
   "Diğer planlara etkisi" bölümü zorunlu okumadır.

8. **Tasarım TEK TAG ile yapılır; çoklu tag sonra gelir.**
   Kalibrasyonda korkulması gereken şey "yanlışlık" değil **tutarsızlıktır** — aynı fiziksel
   noktanın farklı zamanlarda farklı sanal koordinat vermesi. Tag 0 orijini *tanım gereği*
   olduğu için "yanlış ölçülmüş" olamaz; tutarsızlığın tek kaynağı **tag'ler arası uyuşmazlıktır**
   (tag 1'in ölçülen yeri 3 cm hatalıysa, hangi tag'den kalibre olduğuna göre çerçeve değişir).

   Tek tag varken bu kaynak **yoktur** — çerçeve inşaattan tutarlıdır. Bu yüzden creator mode'da
   tasarım tek tag'le yapılır; tag'ler sonradan, uyuştukları doğrulanarak eklenir.

9. **Doğrulama tasarımdan ÖNCE gelir (C maddesi). Test A geçmeden creator mode'da tasarıma
   başlanmaz.** Boş odada referans yokluğu bahane değil — bant ile kendi referansını koyarsın.

10. **Çerçeve değişimi her zaman KATI dönüşümdür, bu yüzden harita kurtarılabilir.**
    Kalibrasyon asla eğim uygulamıyor, yalnızca yaw döndürme + öteleme:
    ```csharp
    rig.RotateAround(measuredPos, Vector3.up, yawDelta);   // yalniz yaw
    rig.position += delta;                                  // oteleme
    ```
    Harita da bir konum/rotasyon listesi (creator planı karar #1). Çerçeve sonradan değişirse
    listeye aynı katı dönüşüm uygulanır — tasarım korunur, toptan kayar.
    **Şartı:** eski yerleşimin kayıtlı olması, yoksa delta hesaplanamaz. `venueId` bunun içindir.

---

## Mevcut kod envanteri — yeniden keşfetme

Bugün (2026-07-31) okunarak doğrulandı.

| Ne | Nerede | Durum |
|---|---|---|
| Tag tespiti, dünyaya çevirme, uyarlanır hız | `Scripts/XR/AprilTagCalibration.cs` | **çalışıyor**, cihazda doğrulandı |
| Sürekli düzeltme + kendini onarma (uyku/konum değişimi) | aynı dosya → `ContinuousCorrect()` | **çalışıyor** (`d1176d6`, cihazda doğrulandı) |
| Rig hizalama (yaw döndür + ötele, eğim asla) | aynı dosya → `ApplyCorrection()` | çalışıyor |
| Öğrenme modu — tag'in çerçevedeki yerini ölçüp loglar | aynı dosya → `Learn()`, `learnMode` | **çalışıyor**, ölçüm aracı hazır |
| `tagLayout` **listesi** (id, position, yawDegrees) | aynı dosya, `TagEntry` | yapı çoklu tag'e hazır, **kullanımı tek tag** |
| Yatay tag'de yaw yedeği | aynı dosya → `YawOf()` | duvar tag'inde gerekmiyor; zemin tag'i denenirse hazır |
| Tag'den kalibrasyonu tamamlama | `CalibrationManager.CompleteFromTag()` | **bugün eklendi** (`f271c93`) |
| Katılırken zaten kalibreyse A/B istememe | `CalibrationManager.Begin()` | **bugün eklendi** (`d1e7707`) |
| Nişanın rig hareketinden etkilenmemesi | `HandGrabber.CompensateRigRotation()` | **bugün eklendi** (`4ecb81c`), cihazda ✅ |
| Anchor sürücüsü (kapatılacak) | `Scripts/XR/CalibrationAnchor.cs:402-473` | **karar #4 ile devre dışı** |
| Grup GUID dağıtımı + disk kalıcılığı | `Scripts/XR/CalibrationShareSync.cs` | **gereksizleşir** |
| A/B iki nokta yakalama | `CalibrationManager.Apply()` | **karar #5: kalır ama görünmez** |

### Ölçülmüş sayılar — tahmin etme, bunları kullan

| Ne | Değer | Kaynak |
|---|---|---|
| Jitter | 1 m'de **3 mm**, 2 m'de **15 mm** | FAZ 0 spike |
| Tag boyutu | 0.14 m | sahne |
| Kalibrasyon menzili | 2 m (`calibrateMaxDistance`) | sahne |
| Tespit hızı | 3 Hz meşgul / 1 Hz boşta | `5116a4a` |
| Düzeltme ölü bölgesi | 2 cm / 1.5° | `d1176d6` |
| **Tag 0 yüksekliği (`h`)** | **1.52 m** | şerit metre, 2026-07-31 — tag yeni yerine asıldıktan sonra |

**Tag 0 yerleşimi (BU mekân, göç sonrası):** `id: 0, position: (1.30, 1.52, -0.07), yawDegrees: 180`.
`x`/`z` sıfır DEĞİL çünkü bu bir göç — bkz. B maddesi. Sıfırdan bir mekânda `(0, h, 0)` olur.
`y` her durumda tag'in zeminden yüksekliği (zemin `y=0` kalsın diye — `y: 0` yazılsaydı zemin
−1.52'ye düşer, oyuncu yerin altında görünürdü).

**`yawDegrees` orijin seçiminden BAĞIMSIZDIR — 180, sıfır değil.** Önce "tag orijinse yaw'ı
da sıfırdır" diye 0 yazıldı; cihazda oyuncu tag'in ÖNÜNE değil ARKASINA düştü. Sebep: tespit
kütüphanesinin verdiği tag "ön" yönü duvarın içine bakıyor, yani sözleşme 180° kaymış.
Eski `learnMode` ölçümü de bunu söylüyordu (`175.2` ≈ 180) ama fark edilmedi.
Panel bu durumda `yaw sapma 0.2` diyordu — sistem kendi içinde tutarlıydı, yanlış olan tanımdı.
**Ders: yaw ölçülen/gözlenen bir şeydir, ilan edilen değil. Konumun aksine.**

---

## İş kalemleri

### A. Anchor'ı sürücülükten çıkar — **tek başına anlamlı**

`CalibrationAnchor`'ın `LateUpdate` içindeki rig yazması kapanır. Tag tek otorite olur.

Bugünkü "tag ölçüyor ama hiçbir şey yapamıyor" durumu **bu maddeyle biter.**

Dikkat: anchor'ı uyandıran iki kapı var — tag'inki `d1176d6`'da kapandı, ağdan/diskten gelen
(`CalibrationAnchor.cs:358`) hâlâ açık. Kapatılacak olan bu.

> **Kabul kriteri:** Tag'e bak, panelde "düzeltildi (X cm)" bir kez çıkıp ardından **"HİZALI"**'da
> kalıyor. Sürekli "düzeltildi" tekrar ediyorsa kavga sürüyor demektir — sapma hiç kapanmıyor.

---

### B. Tag kalibrasyon kaynağı olur — İKİ MOD

**Hangi modda olduğunu bilmek şart; ikisi farklı sayı yazdırıyor.**

#### Mod 1 — SIFIRDAN MEKÂN (asıl kullanım, kolay olan)

Korunacak eski dünya yok; harita creator mode'da sıfırdan kurulacak. Tag doğrudan orijindir:

```
position: (0, h, 0)      h = tag merkezinin yerden yuksekligi (serit metre, TEK olcum)
yawDegrees: 180
```

Ölçüm yok, hesap yok. Bkz. **"Yeni mekân prosedürü"**.

#### Mod 2 — GÖÇ (bu mekânda yapıldı, 2026-07-31)

Sahnede eski A/B orijinine göre çizilmiş bir dünya var (odalar, kapı, silah rafları,
`RoomPlan.json`) ve kaybedilmek istenmiyor. O zaman tag orijin OLMAZ — **mevcut çerçeveyi
üretir**:

```
1. Gecici olarak (0, h, 0) yaz, tag'den kalibre ol
2. Sag kumandayi ESKI orijin isaretine degdir, TETIGE BAS (deger donar)
3. Okunan = eski orijinin TAG cercevesindeki yeri, orn. (-1.30, ?, +0.07)
4. Tag'in eski cercevedeki yeri = ters isaretlisi -> (1.30, h, -0.07)
5. Yerlesime onu yaz
```

**Sonuç (bu mekân):** `(1.30, 1.52, -0.07)`, yaw 180. A/B hiç kullanılmadı — ölçüm tag
çerçevesinden alındı.

#### Ölçüm tuzağı — yaşandı, tekrarlanmasın

İlk ölçüm **2.55 m** okundu; gerçeği **1.30 m**. Sebep: panel canlıydı, kumandayı noktaya
değdirip panele bakmak için el oynayınca sayı değişiyordu.

**Nasıl yakalandı:** tag'den o noktaya ADIMLANDI (~2 adım ≈ 1.4 m), 2.55 ile uyuşmadı.
Ölçümü başka bir ölçümle değil, kaba fiziksel gerçekle sınamak yanlış sayıyı yakaladı.

**Düzeltme:** tetik yakalama eklendi — değdir, tetiğe bas, sayı DONAR. Yeni ölçüm 1.30
çıktı ve adımlamayla uyuştu.

> **Her ölçümden sonra kaba bir fiziksel sınama yap.** Adımla, karışla, göz kararıyla —
> bir büyüklük mertebesi hatası ancak böyle yakalanır.

---

#### Aşağısı ilk taslaktan kalan tarihsel not (Mod 1 denenip vazgeçildi)

Yerleşim: `id: 0, position: (0, 1.52, 0), yawDegrees: 0` — **denendi 2026-07-31, sonra Mod 2'ye geçildi.**

`x`/`z` sıfır çünkü orijin tanım gereği orası, ölçülmez. `y` = tag merkezinin zeminden
yüksekliği (şerit metre). `y: 0` yazılsaydı zemin −1.52'ye düşer, oyuncu yerin altında görünürdü.

**Bedeli — miktarı ölçülmedi ve ölçülmesine gerek yok.** Mevcut dünya içeriği (`RoomPlan.json`,
sahne geometrisi, doğum bölgeleri) eski orijine göre yazılmıştı; orijin taşındı, onlar taşınmadı.
Kayma miktarı = eski A/B noktası ile yeni tag 0'ın zemine izdüşümü arası mesafe. Tag bambaşka bir
duvara taşındığı için bu **metre mertebesinde**.

> Bu planın ilk taslağında "~39 cm" yazıyordu; o sayı tag'in ESKİ yerine (`0.39, 1.36, 0`)
> aitti ve tag taşınınca geçersiz kaldı. Yeni bir sayı hesaplanmadı — creator mode zaten
> hepsini sıfırdan kuracağı için değersiz.

**Sonucu bilerek kabul ediyoruz:** sahnedeki duvarlar/koridor bugün fiziksel odayla
örtüşmeyecek. Bu **kalibrasyon hatası değildir** — test ederken görsel örtüşmeye bakma,
panele ve yazıya bak. Gerçek örtüşme testi C maddesinde bantla yapılır.

> **Kabul kriteri:** Sadece tag 0'a bakarak (başka hiçbir şey yapmadan) kalibre oluyorsun;
> "TAG İLE KALİBRE EDİLDİ" çıkıyor, A/B istenmiyor.

---

### C. DOĞRULAMA — tasarımdan önceki kapı

**Bu madde kod yazmaz, ölçüm yapar.** Amacı tek soruya cevap vermek: *creator mode'da tasarıma
başlamak güvenli mi?*

Aranan şey **doğruluk değil, tekrarlanabilirliktir** (karar #8). Orijinin "gerçekte" 5 cm yanda
olması hiçbir şeyi bozmaz — tasarım o çerçevede yapılıp o çerçevede oynanır. Bozan tek şey,
aynı fiziksel noktanın her seferinde farklı sanal koordinat vermesidir.

**Gereken malzeme: bir rulo koli bandı + şerit metre.** Boş odada referans yoksa referansı
sen yaratırsın. Zemine 3-4 yere bant ile artı işareti yapıştır.

#### Test A — Tekrarlanabilirlik (ZORUNLU GEÇİLMELİ)

```
1. Bir prop'u bant artisinin TAM ustune koy
2. Kaydet, oyunu kapat
3. Tekrar ac, tag'e bak, haritayi yukle
4. Prop hala artinin ustunde mi?
```

Bu, tasarım yapmak için gereken **tek** garantidir. Geçmezse C'de kal, D'ye geçme.

#### Test B — Oturum içi kayma

```
1. Prop'u artiya koy
2. 10 dakika sahada yuru: uzaklas, yaklas, egil, tag'i kaybet-bul
3. Geri gel — hala ustunde mi?
```

Panel sayıyı da veriyor: sürekli "HİZALI (1.2 cm)" görüyorsan kayma yok. Sürekli "düzeltildi"
tekrar ediyorsa A maddesi eksik kalmış demektir.

#### Test C — Tag'ler uyuşuyor mu *(yalnızca 2. tag asılınca, D maddesinden önce)*

```
1. Tag 0'dan kalibre ol, prop'u artiya koy
2. Tag 1'in yanina git, oradan yeniden kalibre ol
3. Prop hala artinin ustunde mi?
```

Kaymışsa tag 1'in ölçümü hatalı — `learnMode` ile yeniden ölç. **Uyuşmayan tag'i yerleşimde
bırakma**, tutarsızlığın tek kaynağı budur.

#### Ölçülecek ve yazılacak

| Ne | Kabul |
|---|---|
| Test A sapması | **< 2 cm** (düzeltme ölü bölgesi zaten 2 cm) |
| Test B sapması, 10 dk sonunda | < 5 cm |
| Test C, tag'ler arası fark | < 3 cm |

Sayıları bu dosyaya yaz. Sonraki oturum tahmin etmesin.

> **Kabul kriteri:** Test A geçti ve sapması ölçülüp yazıldı. **Bu satır geçilmeden creator
> mode'da tasarıma başlanmaz** (karar #9).

---

### D. Çoklu tag

Bugün ilk tag bulununca duruluyor:

```csharp
break; // tek tag yeter; coklu tag FAZ 5
```

Açılacak. **Füzyon şart değil** — en yakın (ya da en dik açıdan görünen) tag'i seçmek yeterli.
Jitter mesafeyle büyüdüğü için "en yakın" iyi bir seçim ölçütü.

> **Kabul kriteri:** Odanın iki ayrı ucunda, iki farklı tag'e bakarak kalibre oluyorsun ve
> ikisinden gelen sonuç aynı yeri gösteriyor (bir nesne iki durumda da aynı fiziksel noktada).

---

### E. `tagLayout` sahneden veriye

`RoomPlanData.cs` tarzında `[Serializable]` + `JsonUtility`. PC'de kaydedilir/yüklenir.

Karar #6'nın gerekçesi: yeni mekân, yeni APK gerektirmemeli.

> **Kabul kriteri:** Yerleşimi bir JSON dosyasından yükleyip kalibre oluyorsun; sahnedeki
> `tagLayout` alanı boş. Dosyayı değiştirip yeniden başlatınca yeni yerleşim geçerli.

---

### F. A/B'yi görünmez yap + paylaşımı devre dışı bırak

- A/B akışı normal oyunda hiç görünmez (kod durur — karar #5)
- `CalibrationShareSync` devre dışı; `SharedCalibration.json` artık okunmaz
- Bekleme ekranı ("ORTAK KALİBRASYON BEKLENİYOR") kalkar

> **Kabul kriteri:** Sıfırdan kurulmuş bir gözlükle, ağda kimse yokken, sadece tag'e bakarak
> oyuna giriliyor. Hiçbir noktada A/B veya bekleme ekranı görünmüyor.

---

## Diğer planlara etkisi — ZORUNLU OKUMA

Bu plan iki yerde mevcut kararları **geçersiz kılıyor**.

**1. `PLAN-00-SIRA.md` § "Planlar arası temas noktaları" #1 — `venueId`**

> *"`MapLayout.venueId`, kalibrasyon FAZ 2'nin ürettiği group GUID olmalı"*

**Geçersiz.** Anchor kalkıyor, group GUID üretilmiyor. `venueId` artık **tag yerleşiminin
kimliği** olmalı — hangi mekânın hangi tag düzeni. Aynı işi görür (harita yanlış mekânda
yüklenince operatör uyarılır), üstelik daha doğrudan: harita, üzerine kurulduğu fiziksel
tag'lere bağlanır.

**2. `PLAN-00-SIRA.md` § "Planlar arası temas noktaları" #2 — frame sözleşmesi**

> *"A = origin, A→B = +Z sözleşmesi aynı kalacak ... bugün kaydedilen harita, anchor ve AprilTag
> geldikten sonra da geçerlidir."*

**Artık geçerli değil.** Karar #1 ile orijin **tag 0**'a taşınıyor. Sonuç: bu plandan ÖNCE
kaydedilmiş haritalar tag 0'ın ofseti kadar kayar.

Pratikte sorun değil — creator mode henüz kaydedilmiş üretim haritası üretmedi. Ama
**creator mode'u yapan kişiye bildirilmeli** ve bildirim zamanı şu: **E maddesi bitip yerleşim
formatı netleştiğinde.** Daha erken söylemek, sonra formatı değiştirmek demek olur.

**Creator mode'a söylenecek tek cümle (E bitince):**
> "Mekân verisine tag yerleşimi listesi de eklenecek; `venueId` o listenin kimliği olacak.
> Orijin artık A/B noktası değil, zemindeki tag 0."

**3. Sıra dosyasındaki 5. adım ("FAZ 3 → 5 — AprilTag, 2.5–4 gün")**
Bu plan onun yerine geçer. Anchor fazları (FAZ 1–2) yapılmış durumda ve bu planla **geri
alınıyor** — emek boşa gitmedi, drift probleminin gerçek şeklini o iş öğretti.

---

## Tuzaklar

- **İki sürücü sorunu tekrar doğabilir.** Anchor'ı kapatırken `_driving`'i uyandıran her yolu
  kapat, sadece birini değil. Bugün tam olarak bu yüzden yarım kaldı.
- **`learnMode` kalibrasyon şart koşuyor** (`AprilTagCalibration.cs:330`). Tag 0'ın ölçüm
  gerektirmemesi bu kilidi açan şey — karar #1'i bozma, yoksa zincir başlamaz.
- **Zemin tag'i fiziksel olarak korunmalı.** Kâğıt hâliyle bir maç dayanmaz. Pleksi altına,
  lamine, ya da sert plakaya bastır.
- **Menzil × tag boyutu.** 14 cm tag'e 2 m'den yakın olmalısın. Büyük salonda bu, çok tag ya da
  büyük tag demek. Mekânı görmeden tag sayısına karar verme.
- **Eğik açı hassasiyeti düşürür.** Ölçülen 3 mm neredeyse dik bakışta. Duvar tag'lerini göz
  hizasına yakın ve oyuncunun doğal bakış yönüne dik as.
- **Nişan telafisi zaten var** (`4ecb81c`): rig döndüğünde `HandGrabber.aimDir` da dönüyor.
  Rig'i oynatan her mekanizma bu telafinin kapsamındadır — yeni bir sürücü eklersen bunu bozma.
- **`RoomScanSync` `Calibrated` şart koşuyor** (`RoomScanSync.cs:63`). `CompleteFromTag()` bunu
  sağlıyor, kopmaz — ama A/B'yi görünmez yaparken bu yolu kesme.
- **`AvatarIKController.RecalibrateAll()` tetiğini kaybediyoruz.** Şu an sadece A/B yolundan
  çağrılıyor (`CalibrationManager:236`). Avatar boyu kendi kendine kalibre olduğu ve
  `recalibrateRise` ile yukarı doğru onardığı için **blokerkler değil** — ama "oyuncu şu an kesin
  dik duruyor" ipucu gider. Tag'e bakarken poz bilinmediği için o ipucu zaten verilemez.

---

## Kurallar

- **TUTUŞLARA DOKUNMA.** `WeaponGripProfile`, `HandGrabber`'ın kabza hesabı, el IK hedefleri.
  Bir çözüm tutuşlara dokunmayı gerektiriyorsa o çözüm yanlıştır, söyle.
  (`HandGrabber.CompensateRigRotation` bu kuralın istisnası değil — nişan durumunu düzeltir,
  kabza hesabına dokunmaz.)
- **A/B'yi silme** (karar #5, `PLAN-00-SIRA.md` durma kuralı).
- **Dalda çalış** (`ozellik/tag-cerceve` gibi), main'de değil.
- **Onay almadan commit etme.** Commit mesajları kısa: ne + neden + varsa tuzak.
  **Test durumunu doğrulamadan "cihazda doğrulandı" yazma.**
- Kod yorumları Türkçe ama **ASCII**.
- **Tahmin etme — koddan oku, sonra konuş.**
- **Her madde ayrı commit.** A bitmeden B'ye geçme; kabul kriterini elle doğrula.

---

## Test planı

Cihazda, sırayla:

- **A:** Tag'e bak → panel bir kez "düzeltildi", sonra "HİZALI"'da kalıyor (kavga bitti)
- **A regresyon:** İki elle tutuşta destek eliyle sağa çevirme hâlâ çalışıyor (`4ecb81c`)
- **B:** Sadece tag 0'a bakarak kalibre olunuyor, A/B istenmiyor
- **C:** Test A — kapat/aç/yükle sonrası prop bant artısının üstünde (**tasarımın kapısı**)
- **C:** Test B — 10 dk dolaşma sonrası prop yerinde, panel "HİZALI"da
- **C:** Sapma sayıları ölçüldü ve plana yazıldı
- **D:** Test C — iki farklı tag'den kalibre → aynı fiziksel nokta, aynı yerde
- **D:** Tag'den uzaklaş, yaklaş, tekrar bak → çerçeve tutuyor
- **E:** Yerleşim JSON'dan yükleniyor, sahnedeki alan boş
- **F:** Sıfırdan gözlük, ağda kimse yok, sadece tag → oyuna giriliyor
- **Negatif:** Hiç tag görünmeyen bir noktada başlat → sistem ne yapıyor? (bekleme mi, uyarı mı —
  davranış bilinçli seçilmeli)
- **Negatif:** Uyku → tag'in görünmediği başka bir noktada uyanma → tag'i görünce toparlıyor mu
  (`d1176d6` bu senaryoyu cihazda geçmişti, regresyon olmamalı)
- **Çok oyunculu:** İki gözlük aynı tag'i görüyor → bir nesne ikisinde de aynı fiziksel yerde.
  **Bu, `CalibrationShareSync`'in silinebileceğinin kanıtıdır.**

---

## Bu işi başlatırken verilecek prompt

Aşağıdakini yeni bir oturumda olduğu gibi yapıştır:

```
AprilTag cerceve isine basliyoruz. Once PLAN-apriltag.md dosyasini bastan sona oku —
kesif orada yapilmis, yeniden kesfetme. PLAN-00-SIRA.md'deki iki temas noktasini bu
plan gecersiz kiliyor, "Diger planlara etkisi" bolumunu atlamadan oku.

Kurallar:
- "Verilmis kararlar" bolumunu tartisma, uygula.
- Isi A -> B -> C -> D -> E -> F sirasiyla yap. Bir madde bitmeden digerine gecme.
- C maddesi KAPIDIR: Test A gecmeden creator mode'da tasarima baslanmaz (karar #9).
- Her maddenin "Kabul kriteri" satirini CIHAZDA dogrulamadan bitti deme.
- TUTUSLARA DOKUNMA (WeaponGripProfile / HandGrabber kabza hesabi / el IK).
- A/B kodunu SILME, yalnizca gorunmez yap.
- Dalda calis (ozellik/tag-cerceve acildi). Onay almadan commit etme. Her madde ayri commit.
- Kod yorumlari Turkce ama ASCII.
- Tahmin etme, koddan oku sonra konus.

A maddesiyle basla: anchor'i rig surucusu olmaktan cikar. Tek basina anlamli ve
bugunku "tag olcuyor ama hicbir sey yapamiyor" durumunu bitiriyor.
```
