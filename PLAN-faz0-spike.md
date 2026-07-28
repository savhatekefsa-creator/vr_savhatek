# FAZ 0 — Spike Görev Planı (AprilTag / Passthrough Camera fizibilitesi)

> Üst plan: [PLAN-kalibrasyon.md](PLAN-kalibrasyon.md)
> Bu bir **spike**: kod atılacak, ürün kalitesi aranmayacak. Amaç kod değil **bilgi**.
> **Zaman kutusu: 2 gün.** Sonuç ne olursa olsun dur ve değerlendir.

---

## ⛔ EN ÖNEMLİ KURAL — nerede çalışılacak

**Bu projede, atılacak bir dalda başla. Sadece zorunda kalırsan ayrı projeye taşı.**

```bash
git checkout -b spike/apriltag
```

```
spike/apriltag dali (bu proje)
        |
        +-- A yolu: WebCamTexture + OpenCV
        |   Meta XR SDK GEREKMIYOR -> BURADA YAP
        |
        +-- "Meta XR SDK / MRUK olmadan olmuyor" noktasi
            -> DUR. AYRI PROJEYE GEC.
```

**Tek somut risk:** `com.meta.xr.sdk.*` veya MRUK paketini, projede zaten kurulu olan
`com.unity.xr.meta-openxr` (2.5.0) yanına koymak. İkisi de Meta OpenXR sağlayıcısıdır;
çakışırlarsa çalışan room-scan boru hattı (`ARPlaneManager`, `MetaOpenXRSessionSubsystem`)
bozulur. O paketi kurmadığın sürece risk yok.

| Yapılan | Nerede |
|---|---|
| Kamera izni, OpenCV asset'i, WebCamTexture denemesi | ✅ `spike/apriltag`, bu proje |
| `manifest.json`'a Meta XR SDK / MRUK eklemek | ⛔ Ayrı proje |

**Sınır: manifest.json'a Meta paketi eklemek üzereysen, orası sınırdır.**

Dal atılacaktır — spike bitince sil, hiçbir şey commit'leme (OpenCV asset'i yüzlerce MB).
Spike yeşil çıkarsa "bunu ana projeye nasıl sokarız" ayrı bir tasarım sorusudur (Faz 3).

---

## Cevaplanacak asıl soru (R1)

> **Passthrough Camera API'yi, Meta XR Core SDK'ya göç etmeden kullanabiliyor muyuz?**

Yan sorular:
- Cihaz/OS destekliyor mu?
- Kamera karesi + **kamera pozu** + **intrinsics** alabiliyor muyuz? (üçü de şart)
- Marker tespiti bizim odamızda, bizim ışığımızda ne kadar iyi?

---

## Adım 0 — Ön kontroller (15 dk, kod yok)

- [x] **Gözlük modeli:** Quest 3 ✅ (Quest 2 **yapamaz**)
- [x] **Horizon OS sürümü:** **v2.6** ✅ — fazlasıyla yeni.
      ⚠️ Not: Meta sürüm şemasını değiştirdi. Eski `v74/v76/v81/v83` numaraları artık
      `v1.x` / `v2.x` ile değiştirildi — yani v2.6, PCA'nın geldiği v7x döneminden **çok sonra**.
      Eski dokümanlarda "v76+" görürseniz kafanız karışmasın. (v2.6 PTC kanalından geliyor olabilir.)
- [ ] **USB hata ayıklama** açık ve PC'den `adb devices` cihazı görüyor
      (spike boyunca log okuyacaksın, bu şart)

```bash
adb devices
```

> **Kırmızı bayrak:** Quest 2 ya da güncellenemeyen eski OS → **DUR.** AprilTag yolu bu cihazda kapalı,
> planın anchor yarısıyla (Faz 1-2) devam et.

---

## Adım 1 — Ayrı proje + örnek (1-2 saat)

- [ ] Unity Hub'da **yeni 3D (URP) projesi** aç — Unity **6000.0.38f1 veya üstü**
      (sizde 6000.3.18f1 var, uygun)
- [ ] Android platformuna geç, XR Plug-in Management → OpenXR + Meta özellik grubu
- [ ] Örneği klonla:

```bash
git clone https://github.com/TakashiYoshinaga/QuestArUcoMarkerTracking
```

- [ ] README'yi oku ve **hangi sürümü kullanacağına karar ver**:

| Sürüm | Gereksinim | Sizin için anlamı |
|---|---|---|
| **Yeni** (PassthroughCameraAccess) | Meta XR SDK **v83+** | Kolay çalışır ama SDK bağımlılığı — ana projeye taşıması zor |
| **Eski** (WebCamTexture) | Meta XR SDK'ya daha az bağımlı | **Sizin için daha değerli** — backend'den bağımsız olabilir |

> **İkisini de dene.** Öncelik **eski/WebCamTexture** yolunda: o çalışırsa ana projeye taşıma
> sorunu büyük ölçüde çözülmüş olur. Yeni sürüm sadece "çalışıyor mu" sorusunu cevaplar.

- [ ] **OpenCV for Unity** kararı: Asset Store'da ~$95. Ücretsiz alternatif arayacaksan
      **şimdi** ara, spike'ın ortasında değil. (Karar spike'ın çıktılarından biri.)

---

## Adım 2 — Kamera erişimi çalışıyor mu (1-3 saat) ⬅ ASIL SORU BURADA

- [ ] `AndroidManifest.xml`'e izin eklendi mi:
      `horizonos.permission.HEADSET_CAMERA`
- [ ] Build al, gözlüğe kur, çalıştır, **izni onayla**
- [ ] **Kamera görüntüsü geliyor mu?** (örnek sahnesi ham kareyi bir quad'a basıyor)

```bash
adb logcat -d -s Unity:V > spike.txt
```

- [ ] Kamera **pozu** ve **intrinsics** okunabiliyor mu? (sadece görüntü yetmez —
      poz olmadan marker'ı dünyaya yerleştiremezsin)

> **Kırmızı bayraklar:**
> - İzin diyaloğu hiç çıkmıyor → manifest/OS sorunu
> - Görüntü geliyor ama poz/intrinsics yok → **yarım çözüm, işe yaramaz**
> - Yalnızca Meta XR Core SDK + OVRCameraRig ile çalışıyor → **R1 SARI**:
>   yol açık ama ana projeye taşıma maliyeti yüksek, Faz 3 baştan planlanmalı

> **Not:** Editor ve XR Simulator'da **çalışmaz.** Her deneme cihaza build. Buna göre sabırlı ol.

---

## Adım 3 — Marker tespiti (1 saat)

- [ ] Marker bas: **A4'e sığacak en büyük boy**, tercihen **20 cm**
      - **MAT kâğıt** (parlak kâğıt tespiti öldürür)
      - Kenar boşluğu (quiet zone) bırak, kesme
      - Sert bir yüzeye yapıştır — **kıvrılmasın**
      - **Gerçek kenar uzunluğunu cetvelle ölç ve koda gir** (poz doğruluğu buna bağlı)
- [ ] Örneği çalıştır, marker'a bak, sanal küp marker'ın üstüne oturuyor mu?
- [ ] Çalışıyorsa: sözlüğü **`DICT_APRILTAG_36h11`** yapıp aynı testi tekrarla
      (AprilTag marker'ı ayrıca basman gerekir)

---

## Adım 4 — Ölçüm (2-3 saat) ⬅ SAYILAR BURADAN ÇIKACAK

Bu sayılar tag boyutunu ve adedini belirleyecek. **Göz kararı yazma, ölç.**

| Ölçüm | Nasıl | Neden önemli |
|---|---|---|
| **Menzil** | Marker'dan 0.5 m adımlarla uzaklaş. Küp **kararsızlaşınca** dur, mesafeyi yaz. | Tag aralığı = tag adedi |
| **Açı limiti** | Sabit mesafede yana kay, marker'a giderek daha eğik bak. Kopma açısını yaz. | Duvar yerleşimi |
| **Jitter** | 2 m'de **kıpırdamadan** dur, küpün titremesini gözle. Marker boyuna kıyasla tahmin et (mm). | Yumuşatma ihtiyacı + büyük alandaki hata payı |
| **Gecikme** | Kafanı hızlı çevir. Küp marker'dan **geride kalıyor mu**? | Timestamp hizalaması şart mı |
| **Kare hızı / ısınma** | 10 dk çalıştır. FPS düşüyor mu, gözlük ısınıyor mu? | CPU bütçesi |
| **Işık** | Işıkları kıs, tekrar dene. | Gerçek maç koşulu |

**Kaydet:** her ölçümü bir satır olarak yaz. Örnek:

```
20 cm AprilTag 36h11, ev odası, tavan lambası:
  menzil     : 4.5 m'ye kadar stabil, 5 m'de kopuyor
  açı        : ~55 dereceye kadar
  jitter     : 2 m'de ~3 mm
  gecikme    : hızlı dönüşte belirgin, timestamp hizalaması ŞART
  fps        : 72 sabit, 10 dk sonra gözlük ılık
  loş ışık   : menzil 3 m'ye düşüyor
```

> **Büyük alan uyarısı:** 1°'lik hata 3 m'de 5 cm, 20 m'de **35 cm**. Küçük odada
> "gözüme iyi geldi" büyük alanda yetmez — bu yüzden **mm cinsinden** yaz.

---

## Adım 5 — Karar

| Sonuç | Anlamı | Ne yapılır |
|---|---|---|
| 🟢 **YEŞİL** | Kamera+poz+intrinsics alınıyor, tespit stabil, menzil ≥3 m | Faz 3 planlandığı gibi; ölçülen menzil tag adedini verir |
| 🟡 **SARI** | Çalışıyor ama Meta XR SDK / OVRCameraRig şart | Faz 3 baştan planlanmalı: iki backend yan yana mı, göç mü? **Bana söyle, birlikte karar verelim** |
| 🔴 **KIRMIZI** | Kamera erişimi yok, poz alınamıyor, tespit kararsız | AprilTag yolu kapalı. Plan **anchor ağırlıklı** yeniden kurulur (Faz 1-2 + drift uyarıları) |

**Hangi sonuç çıkarsa çıksın Faz 1 ve Faz 2 etkilenmez** — onlar kameraya bağlı değil.
Spike kırmızı çıksa bile anchor omurgası ayakta kalır.

---

## Spike bittiğinde bana getir

1. Yukarıdaki **ölçüm bloğu** (doldurulmuş)
2. 🟢/🟡/🔴 kararı
3. Hangi sürüm çalıştı: WebCamTexture mı, PassthroughCameraAccess mi?
4. OpenCV kararı: satın alındı mı, alternatif mi?
5. Takıldığın yer varsa **tam hata metni** + `spike.txt`

Bunlarla Faz 3'ü gerçek sayılarla planlarız — tahminle değil.

---

## Hatırlatmalar

- **Ana projeye dokunma.** Spike ayrı projede.
- **Editor'da test edilemez** — her deneme cihaza build, döngü yavaş. Buna göre plan yap.
- **Kod atılacak** — temiz yazma, hızlı öğren.
- **2 gün dolduğunda dur.** Bitmediyse bile o noktada bildiklerini yaz; yarım bilgi de bilgidir.
