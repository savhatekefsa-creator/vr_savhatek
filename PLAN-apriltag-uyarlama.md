# AprilTag Uyarlama Planı — `juchong/AprilTagUnity` → vr_savhatek

> Kaynak repo incelendi (2026-07-29). Bu doküman **ne alacağımızı, ne yazacağımızı ve mevcut
> kalibrasyon sistemine nasıl bağlanacağını** anlatır.
> Üst plan: [PLAN-kalibrasyon.md](PLAN-kalibrasyon.md) · Fizibilite: [PLAN-faz0-spike.md](PLAN-faz0-spike.md)

---

## 1. En önemli bulgu: entegrasyon sandığımızdan çok küçük

Mevcut sistemimiz zaten şu yapıda:

```
A/B dokunma  →  Apply()  →  CalibrationAnchor.Bind(rig, hedefPoz)  →  anchor rig'i surer
                                                                        (Faz 1)
                                                                     →  paylasim + kalicilik
                                                                        (Faz 2)
```

**AprilTag yalnızca ilk kutuyu değiştiriyor:**

```
TAG GORULDU  →  poz hesabi  →  CalibrationAnchor.Bind(rig, hedefPoz)  →  AYNEN AYNI
```

Yani Faz 1 ve Faz 2'de yazdığımız hiçbir şeye dokunmuyoruz. Anchor sürüşü, paylaşım, kalıcılık,
kilit, panel — hepsi olduğu gibi kalıyor. **Tag, A/B dokunmasının yerine geçiyor, o kadar.**

---

## 2. Repodan ALACAKLARIMIZ

| Klasör | Ne | Lisans | Boyut |
|---|---|---|---|
| `Assets/AprilTag/Library/` | AprilTag dedektörü — keijiro'nun `jp.keijiro.apriltag` v1.0.2'si, Tag36h11 desteği eklenmiş | **BSD-2-Clause** (Michigan Üniv.) | ~200 KB |
| `Assets/AprilTag/Library/Plugin/Android/libAprilTag.so` | **Önceden derlenmiş ARM64 binary** | BSD-2-Clause | 192 KB |
| `Assets/AprilTag/PassthroughCamera/` | Meta'nın kamera yardımcıları — poz + intrinsics + WebCamTexture yönetimi | Meta örnek kodu | ~670 satır |

**Kritik:** Android `.so` hazır geliyor. **NDK yok, OpenCV yok, derleme yok.**

### Dedektör API'si — tek çağrı

```csharp
var detector = new TagDetector(genislik, yukseklik, TagFamily.Tag36h11, decimation: 2);
detector.ProcessImage(pikseller, yatayFov, tagBoyutuMetre);
foreach (var tag in detector.DetectedTags)
{
    tag.ID;         // hangi tag
    tag.Position;   // KAMERAYA gore konum
    tag.Rotation;   // KAMERAYA gore yon
}
```

### Dünyaya çevirme — iki satır

```csharp
worldPos = kameraPozu.position + kameraPozu.rotation * tag.Position;
worldRot = kameraPozu.rotation * tag.Rotation;
```

Kamera pozu `PassthroughCameraUtils.GetCameraPoseInWorld(eye)`, FOV ise
`GetCameraIntrinsics(eye)`'ın odak uzaklığından hesaplanıyor.

---

## 3. ALMAYACAKLARIMIZ

Repo bir FIRST Robotics uygulaması; büyük kısmı bize yabancı:

| Dosya | Satır | Neden almıyoruz |
|---|---|---|
| `AprilTagController.cs` | 2560 | Uygulama katmanı — kendi ince köprümüzü yazacağız |
| `AprilTagTransforms.cs` | 2285 | Çoğu FRC'ye özel; bize gereken 2 satır (yukarıda) |
| `AprilTagSpatialAnchorManager.cs` | 2005 | **Bizim `CalibrationAnchor`'ımız zaten var ve daha iyi** |
| `FRCFieldLocalizer.cs` + saha JSON'ları | ~2500 | Tamamen FIRST Robotics'e özel |
| `TagVisualizations`, `DebugImageSaver`, `AnchorInteraction` | ~1200 | Görselleştirme/hata ayıklama, gerekmiyor |
| `AprilTagGPUPreprocessor.cs` | 642 | Optimizasyon — gerekirse sonra |
| `AprilTagPoseFilter.cs` | 449 | **İleride işimize yarayabilir** (Faz 4 yumuşatma) — şimdilik değil |

**Toplam ~11.000 satırdan bize gereken ~900 satır kütüphane + kendi yazacağımız ~150 satır.**

---

## 4. YAZACAĞIMIZ: `AprilTagCalibration.cs` (~150 satır)

Tek bir ince köprü bileşeni:

```
1. WebCamTextureManager'dan kamera karesini al
2. PassthroughCameraUtils'ten kamera pozunu + FOV'u al
3. detector.ProcessImage(...)
4. Bulunan tag'i dunyaya cevir
5. "Bu tag NEREDE OLMALIYDI" ile karsilastir  ->  hedef poz
6. CalibrationAnchor.Bind(rig, hedefPoz)      ->  gerisi mevcut sistem
```

### 5. adım — tag yerleşimi (survey)

Her tag'in ortak çerçevede nerede olduğu bir veri dosyasında:

```json
{
  "tagSizeMeters": 0.20,
  "tags": [
    { "id": 0, "pos": [0, 1.0, 0], "yaw": 0 }
  ]
}
```

**Tek tag'le başlarken bu dosya bir satır** — "tag #0, origin'de, 1 m yükseklikte".
Bu dosya **bizim sunucumuzda** durur ve `CalibrationShareSync` deseniyle dağıtılır — böylece
müdürün istediği "veri bizde" şartı sağlanır.

---

## 5. Mevcut sisteme dokunmayacağımız yerler

| Bileşen | Durum |
|---|---|
| `CalibrationAnchor` (drift düzeltmesi) | ✅ Dokunulmuyor |
| `CalibrationShareSync` (paylaşım + kalıcılık) | ✅ Dokunulmuyor |
| `CalibrationManager` (A/B) | ✅ **Fallback olarak kalıyor** — tag görünmezse devreye girer |
| Ortak çerçeve sözleşmesi (A = origin, A→B = +Z) | ✅ Değişmiyor |
| Room-scan boru hattı | ✅ Etkilenmiyor |

---

## 6. Emek tahmini

| İş | Süre |
|---|---|
| Kütüphane + kamera yardımcılarını kopyala, derlensin | ~1 saat |
| Meta XR SDK / MRUK kurulumu + **room-scan regresyon kontrolü** | ~1 saat |
| `AprilTagCalibration.cs` yaz | ~yarım gün |
| Tag bas, cihazda test, ölçüm, ayar | ~yarım–1 gün |
| **Toplam** | **~1-2 gün** |

Önceki tahmin 2-3 gündü ve o **yalnızca OpenCV boru tesisatı** içindi. Hazır kütüphane sayesinde
o iş tamamen ortadan kalktı.

---

## 7. Açık riskler

**R1 — Meta XR SDK kurulumu.** Repo `com.meta.xr.sdk.core` + `mrutilitykit` 78.0.0 kullanıyor.
Bizim projede yok. **İyi haber:** aynı repo `com.unity.xr.meta-openxr` 2.2.0'ı da kullanıyor —
yani ikisi **yan yana çalışıyor**, çakışma korkusu kanıtla çürüdü (bizde 2.5.0 var, sürüm farkı
kontrol edilmeli).
→ `spike/apriltag` dalında kur, **hemen ardından menü 11/12 ile room-scan'i doğrula.**

**R2 — Editor'de test edilemez.** Passthrough kamera yalnızca cihazda. Her deneme build.

**R3 — Tag okuma kalitesi bilinmiyor.** Menzil/açı/ışık performansı bizim odamızda ölçülmeli.
**Faz 0 spike'ının asıl sorusu bu ve hâlâ cevapsız.**

**R4 — Sürüm uyumu.** Repo Unity 6000.2.6f2 + Meta SDK 78 ile yazılmış; bizde Unity 6000.3.18.
Muhtemelen sorunsuz ama doğrulanmalı.

---

## 8. Sıradaki adım

Kütüphane sorunu çözüldü, entegrasyon yolu net. Kalan tek bilinmeyen **R3**: Quest'in kamerası
bizim mekânımızda tag'i yeterince iyi okuyor mu?

Bunun için:
1. **Tag36h11 bas** — 20 cm, mat kâğıt, gerçek kenar uzunluğunu cetvelle ölç
2. Kütüphaneyi + kamera yardımcılarını `spike/apriltag` dalına kopyala
3. En küçük test: tag'in üstünde bir küp çiz, cihazda bak
4. Ölç: menzil, açı limiti, jitter, kare hızı

Ölçüm tablosu [PLAN-faz0-spike.md](PLAN-faz0-spike.md) Adım 4'te hazır.
