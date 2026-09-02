# Silah tutuş işi — kaldığın yer

**Tarih:** 2026-08-05
**Dal:** `duzeltme/tutus-ayar-araci` (push edilmedi, yalnızca yerel)
**Son commit:** `71a7b09`

Bu klasör git dışında, dal değiştirince silinmez.

---

## Ne çözüldü

Aylardır "silah elde 15° yamuk duruyor" diye kovalanan sorunun kök nedeni bulundu ve **ölçüldü**.

Suçlu silahlar değildi, **kumandaydı**. Quest'in OpenXR grip pose ileri ekseni, nişan aldığın hattan **~56° aşağıda**. Üç silah (Pistol 2, HK416, Smg 2) cihazda birbirinden bağımsız ayarlandı, üçünde de namlu göstergesi yeşile döndü — ve ortak çerçeveye çevrilince üçü de aynı noktaya yakınsadı:

| Çift | ayar öncesi | ayar sonrası |
|---|--:|--:|
| Pistol 2 ↔ HK416 | 13,15° | 4,16° |
| Pistol 2 ↔ Smg 2 | 11,55° | 1,97° |
| HK416 ↔ Smg 2 | 3,91° | 5,45° |

**Ortak değer: yaw −32,78°, pitch −56,38°** (namlunun kumanda uzayında bakması gereken yön).

Bu yüzden "silahın mavi eksenini kumandanın mavi eksenine eşle" sezgisi hiç tutmadı — arada gözle kapatılacak bir fark yoktu, 56° vardı.

---

## Yapılanlar

| Commit | Ne |
|---|---|
| `507e23a` | `GripDebugRig` + `WeaponGripTuner` yazıldı, NetworkPlayer prefab köküne eklendi |
| `e3862d1` | Tuner ayar sırasında weld önbelleğini tazeliyor (el silahtan kayıyordu) |
| `5887ec6` | Hedef "iki ekseni eşle" değil: sarı namlu çubuğu + renk geri bildirimi + yön ipuçları |
| `f8efa96` | Pistol 2 işlendi + `.md` dosyasında ondalık nokta düzeltmesi (Türkçe virgül YAML'ı bozuyordu) |
| `3d4fe1a` | HK416 + Smg 2 işlendi |
| `41747b1` | `GripCalibration` aracı — menü 45 (uygula) / 46 (geri al) |
| `89c7e5d` | Ortak kalibrasyon 13 profile uygulandı |
| `6b1dc53` | Yatıklık düzeltme aracı — menü 47 (diz) / 48 (yaz) |
| `71a7b09` | Oturum sonu: 48 çalıştı, namlu-kayması uyarısı eklendi, rig sahneyle commit edildi |

---

## ⚠ Devam ederken İLK bakılacak şey

**Menü 48 çalıştı ama dizilim tamamlanmamış olabilir.** 12:24'te çalıştırıldı; o sırada hangi objenin çevrileceği henüz netleşmemişti (silah modeli mi, taşıyıcı mı). Profillerdeki **yatıklık** değerleri bu yüzden şüpheli.

Yapılacak: cihazda birkaç silahı eline al. Yan yatık duruyorlarsa:

```
Tools ▸ VR Multiplayer ▸ 46. Tutus Kalibrasyonu Geri Al
```

En yeni yedek `grip-yedek-20260805-122442.txt` = **48 öncesi, 45 sonrası** hali. Yani yatıklık geri alınır, namlu kalibrasyonu korunur. Sonra 47/48'i düzgün tekrarla.

---

## Menü 47/48 doğru kullanımı

1. **47** bütün silahları namluları +Z'ye bakacak şekilde yan yana dizer
2. Hiyerarşide **taşıyıcı boş objeyi** seç — profil adını taşıyan üst obje (`Smg 3_GripProfile`), **silah modelini değil** (`Weapon_Smg 3`)
3. `E` (döndürme kolu), araç çubuğunda **Global** modda olsun (Local değil)
4. Yalnızca **mavi halkayı** çevir — o dünya Z'si, yani namlu ekseni. Kırmızı/yeşil namluyu eksenden çıkarır (48 artık 10°'den fazla sapmayı uyarıyor)
5. Kabza aşağı, üst ray yukarı bakınca bırak
6. **Referansları da çevir** (Pistol 2, HK416, Smg 2) — profillerine dokunulmuyor ama ortak "yukarı" onlardan öğreniliyor
7. **48** ile yaz

---

## Cihaz ayar aracı (tuner) hatırlatma

Prefab kökünde `WeaponGripTuner`, `tuning` açık. Sağ el, doğal maç duruşu (dümdüz bilek DEĞİL).

- Sol çubuk ←/→ yaw, ↑/↓ pitch
- **B basılı** + çubuk ←/→ roll, ↑/↓ derinlik
- **A+X 1 sn basılı** kaydet → `.md` dosyası
- **B+Y** oturum başına dön

Tek iş: sarı namlu çubuğunu **yeşile** döndürmek.

Dosyaları çekmek:

```bash
"/c/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" pull /sdcard/Android/data/com.UnityTechnologies.com.unity.template.urpblank/files/GripOlcum ~/savhateks/GripOlcum
```

---

## Bilinen açıklar

- **Paintball** kalibrasyonda 62° döndü, diğerlerinin üç katı. Profili baştan bozuk olabilir; ayrıca bakılmalı.
- **HK416'nın kabza-namlu dikey mesafesi 0,024 m**, diğerlerinde 0,09-0,21. Modelin orijini namlu ekseninde değil. Yatıklığı kabza konumundan otomatik çıkarma denemesi bu yüzden başarısız oldu (referanslar 25-34° uyuşmazlık).
- `gripLocalPosition` (kabzanın avuçta nerede durduğu) hiç ele alınmadı — doğası gereği silaha özel.
- **Kalıcı çözüm hâlâ Faz 1.1:** silah başına Editor'de yerleştirilen `Grip` child transform, H3VR'nin yaptığı gibi. `HandGrabber`'a dokunmayı gerektiriyor, ertelendi.
- `HandGrabber.SnapRotOffset` (profilsiz silahların yolu) hâlâ bozuk: `LongestLocalAxis` bounds'tan geldiği için ±X/±Y/±Z'ye yuvarlıyor, `FromToRotation` da roll'u tanımsız bırakıyor.

---

## Vazgeçmek istersen

```bash
git checkout main && git branch -D duzeltme/tutus-ayar-araci
```

Scriptler, prefab bileşenleri ve profil değişiklikleri gider. Ölçüm dosyaları bu klasörde kalır.
