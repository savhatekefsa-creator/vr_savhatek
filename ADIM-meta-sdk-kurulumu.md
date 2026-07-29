# Adım Adım: Meta XR SDK + MRUK kurulumu (spike/apriltag dalı)

> **Bu kurulum RİSKLİ ADIM içeriyor.** Aşağıdaki ⛔ uyarılarını atlamayın —
> yanlış bir tıklama çalışan XR kurulumunuzu bozabilir.
> İlgili plan: [PLAN-apriltag-uyarlama.md](PLAN-apriltag-uyarlama.md)

---

## 0. Önce güvenlik ağı

- [ ] **Doğru daldasınız:** `spike/apriltag` (kontrol: `git branch --show-current`)
- [ ] **Her şey commit'li:** `git status` temiz olsun. Bozulursa `git checkout .` ile dönersiniz.
- [ ] Unity kapalı olsun (paket kurulumu sırasında açık olması sorun çıkarabilir — ya da
      kurulum sonrası yeniden başlatın)

---

## 1-2. Paketleri kurma — TARAYICIYA GEREK YOK

Projede **Unity 6000.3 (Unity 6.3)** var ve bu sürümde Meta paketleri **Unity'nin içinden**
kuruluyor. Unity'nin kendi dokümanı:

> *"In Unity 6.3 and later, you can install Meta packages when you add the Meta Quest build
> profile using the Platform Browser. **You don't need to install them from the Asset Store.**"*

Yani Asset Store hesabı, tarayıcı, indirme/import adımı **yok**.

- [ ] Unity'de **File > Build Profiles** (Build Settings) aç
- [ ] **Meta Quest** platformunu ekle/seç → **Platform Browser** paket listesini gösterir
- [ ] Listeden **yalnızca şu ikisini** işaretle:
      - **Meta XR Core SDK** → `com.meta.xr.sdk.core`
      - **Meta XR Mixed Reality Utility Kit** → `com.meta.xr.mrutilitykit`
- [ ] Kur, Unity'nin derlemesini bitirmesini bekle (birkaç dakika sürebilir)

> **All-in-One'ı seçmeyin.** Interaction, Platform, Audio, Voice, Haptics gibi ihtiyacımız
> olmayan paketleri de kurar — hem şişirir hem çakışma yüzeyini büyütür.

> Platform Browser'ı bulamazsanız alternatif: **Window > Package Manager** içinden aynı
> paketler görünebilir. İki yol da aynı paketi kurar.

### R1 riski resmen çürüdü

Unity'nin kendi dokümanı: *"You can use Unity's OpenXR plug-in and OpenXR Meta package
together to develop a cross-platform application that has additional features tailored for
Meta Quest devices."* — yani `com.unity.xr.meta-openxr` ile Meta XR SDK **birlikte
desteklenen** bir kurulum. Yine de 4. maddedeki regresyon kontrolünü atlamayın.

---

## 3. ⛔ EN KRİTİK ADIM — Project Setup Tool

Kurulumdan sonra Meta'nın **Project Setup Tool** penceresi açılacak ve bir dizi "düzeltme"
önerecek. **Hepsini körlemesine "Fix All" yapmayın.**

### Kabul ETMEYİN

| Öneri | Neden reddedilmeli |
|---|---|
| **Oculus XR Plugin'e geçiş** / XR provider değişikliği | Biz **OpenXR** kullanıyoruz. Değişirse `com.unity.xr.meta-openxr` devre dışı kalır, **room-scan ve kalibrasyon çöker.** |
| OpenXR özelliklerini kapatma | Menü 45'te açtığımız **Meta Quest: Anchors** özelliği kapanırsa kalibrasyon çalışmaz |
| Android manifest'i tamamen değiştirme | Mevcut izinlerimiz (`USE_SCENE`) kaybolabilir |

### Kabul edilebilir

- Android minimum API seviyesi
- Texture sıkıştırma (ASTC)
- Grafik API sırası (Vulkan)

**Emin değilseniz: hiçbirini uygulamayın, önce bana sorun.** Sonradan tek tek açmak,
bozulanı geri getirmekten kolaydır.

---

## 4. ⛔ HEMEN REGRESYON KONTROLÜ

Kurulumdan sonra **başka hiçbir şey yapmadan** şunları doğrulayın:

- [ ] **Konsol temiz mi?** `error CS` var mı?
- [ ] **XR ayarları bozulmadı mı?**
      `Project Settings > XR Plug-in Management > Android` → **OpenXR işaretli olmalı**,
      Oculus DEĞİL
- [ ] **Anchors özelliği hâlâ açık mı?**
      `XR Plug-in Management > OpenXR > Android` → **Meta Quest: Anchors** işaretli
- [ ] **Room-scan çalışıyor mu?**
      `Tools > VR Multiplayer > 12. Import Room Plan` → oda planı sahneye çiziliyor mu?
- [ ] **Kalibrasyon kodu derleniyor mu?** `CalibrationAnchor` / `CalibrationShareSync` hatasız

### Herhangi biri bozulduysa

```bash
git checkout .
git clean -fd Packages/
```

ve **durun** — bana durumu bildirin. Ayrı projeye geçme planı devrede.

---

## 5. PassthroughCamera yardımcılarını alma

Kurulum temiz geçtiyse, kaynak repodaki kamera yardımcılarını kopyalayacağız
(`Meta.XR.Samples` isim alanı artık çözülecek):

```
juchong/AprilTagUnity/Assets/AprilTag/PassthroughCamera/
    → Assets/AprilTag/PassthroughCamera/
```

Bunu ben yapabilirim — kurulum bitince haber verin.

İçindekiler:
- `PassthroughCameraUtils.cs` — kamera pozu + intrinsics (bize gereken iki fonksiyon)
- `WebCamTextureManager.cs` — kamera karesi akışı
- Editor betiği — Android manifest'e `HEADSET_CAMERA` iznini ekler

---

## 6. Sonraki adımlar (bende)

- `AprilTagCalibration.cs` yazımı (~150 satır)
- Tag yerleşim dosyası (hangi ID nerede)
- Minimum test sahnesi

## 7. Sizde paralel yapılabilir

- [ ] **Tag36h11 bas** — 20 cm, **mat** kâğıt, düz yüzeye yapıştır
      Görseller: https://github.com/AprilRobotics/apriltag-imgs (`tag36h11` klasörü)
- [ ] **Kenar uzunluğunu cetvelle ölç** — poz doğruluğu doğrudan buna bağlı, tahmin etmeyin
- [ ] Basılan tag'in **ID'sini not et**

---

## Özet: nerede durmalısınız

| Aşama | Durum |
|---|---|
| Paketleri kur | Sizde |
| **Project Setup Tool'da "Fix All" DEME** | ⛔ En kritik nokta |
| Regresyon kontrolü (madde 4) | Sizde — **bozulursa durun** |
| PassthroughCamera kopyalama + kod | Bende |
