# AprilTag kütüphanesi — kaynak ve künye

> Bu klasör **dışarıdan alınmış** koddur. Elle düzenlemeyin; güncelleme gerekirse
> aşağıdaki kaynaklardan yeniden alın.

## Nereden geldi

| | |
|---|---|
| **Alındığı proje** | [juchong/AprilTagUnity](https://github.com/juchong/AprilTagUnity) (MIT) |
| **Asıl kütüphane** | [keijiro/jp.keijiro.apriltag](https://github.com/keijiro/jp.keijiro.apriltag) v1.0.2 |
| **Çekirdek algoritma** | [AprilRobotics/apriltag](https://github.com/AprilRobotics/apriltag) — Michigan Üniversitesi |
| **Lisans** | **BSD-2-Clause** — bkz. `LICENSE`. Ticari kullanım serbest, telif notunun korunması şartıyla. |
| **Alınma tarihi** | 2026-07-29 |

`jp.keijiro.apriltag`'in özgün hâli yalnızca `tagStandard41h12` ailesini destekliyor;
juchong'un sürümüne **Tag36h11** desteği eklenmiş — bizim kullanacağımız aile bu.

## İçindekiler

- `Detector/Runtime/` — C# API + native interop (~900 satır)
- `Detector/Plugin/Android/libAprilTag.so` — **önceden derlenmiş ARM64 binary** (Quest 3)
- Diğer platform binary'leri (Windows/macOS/Linux/iOS) — Editor'de sınama için işe yarayabilir

**NDK, OpenCV veya derleme adımı GEREKMİYOR.**

## Kullanım

```csharp
var detector = new AprilTag.TagDetector(genislik, yukseklik,
                   AprilTag.Interop.TagFamily.Tag36h11, decimation: 2);

detector.ProcessImage(pikseller, yatayFovRadyan, tagBoyutuMetre);

foreach (var tag in detector.DetectedTags)
{
    tag.ID;        // hangi tag
    tag.Position;  // KAMERAYA gore konum
    tag.Rotation;  // KAMERAYA gore yon
}
```

Dünyaya çevirme:

```csharp
worldPos = kameraPozu.position + kameraPozu.rotation * tag.Position;
worldRot = kameraPozu.rotation * tag.Rotation;
```

## HENÜZ ALINMAYAN — bilerek

Kaynak repodaki `Assets/AprilTag/PassthroughCamera/` klasörü (Meta'nın kamera yardımcıları:
kamera pozu + intrinsics) **kopyalanmadı**, çünkü `Meta.XR.Samples` isim alanına bağımlı ve
projede **Meta XR SDK henüz kurulu değil** — kopyalamak derlemeyi kırardı.

Meta XR SDK kurulduktan sonra o klasör de alınacak. Plan:
[PLAN-apriltag-uyarlama.md](../../PLAN-apriltag-uyarlama.md)

## Alınmayanlar (gerekmiyor)

Kaynak repo bir FIRST Robotics uygulaması. Saha lokalizasyonu, görselleştirme, kendi spatial
anchor yöneticisi (~10.000 satır) bizim işimize yaramıyor — **bizim `CalibrationAnchor`
sistemimiz zaten var.**

## Not: klasör adı neden `Detector`

Kaynak repoda bu klasörün adı `Library` idi. Projemizin `.gitignore`'unda Unity'nin kendi
`Library/` klasörü için bir kural var (`[Ll]ibrary/`) ve bu kural `Assets/AprilTag/Library/`
içeriğini de yok sayıyordu — dosyalar diskte olmasına rağmen commit'e girmiyordu.
Karışıklığı kökten kesmek için klasör `Detector` olarak yeniden adlandırıldı.
