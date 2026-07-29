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

## 1. Paketleri edinme (Unity Asset Store, ÜCRETSİZ)

Tarayıcıdan, Unity hesabınızla giriş yaparak iki paketi hesabınıza ekleyin:

- [ ] **Meta XR Core SDK** — `com.meta.xr.sdk.core`
- [ ] **Meta MR Utility Kit** — `com.meta.xr.mrutilitykit`

> Alternatif: **Meta XR All-in-One SDK** tek pakette hepsini getirir, ama fazlasını da kurar
> (Interaction, Platform, Audio, Voice…). **Sadece ihtiyacımız olan ikisini kurmak daha temiz.**

Kaynak repo 78.0.0 sürümünü kullanıyor; daha yeni sürüm gelirse onu alın, sürüm uyumsuzluğu
çıkarsa 78'e dönersiniz.

---

## 2. Unity'de içe aktarma

- [ ] Unity'yi aç → **Window > Package Manager**
- [ ] Sol üstten **My Assets** seç
- [ ] **Meta XR Core SDK** bul → **Download** → **Import**
- [ ] **Meta MR Utility Kit** için aynısı
- [ ] Unity'nin derlemesini bitirmesini bekle (birkaç dakika sürebilir)

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
