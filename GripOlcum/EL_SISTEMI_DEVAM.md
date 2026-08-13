# Birinci şahıs eli — devir belgesi

**Dal:** `silah-d` (push edilmedi). **Son commit:** `112f03e` + bu belgeyle gelen ince-ayar refaktörü.
**Cihaz:** Quest 3, kablo bağlı. Unity 6.3 (6000.3.18f1), URP, Linear renk uzayı, Netcode for GameObjects.

---

## 1. SIRADAKİ İŞ — ince ayar (kullanıcı bunu isteyecek)

Kullanıcı elin duruşunu "X'te şu kadar, Y'de şu kadar" diye ayarlamak istiyor. **Tek dokunuş noktası** `FirstPersonHandView.cs` başındaki **altı sayı**:

```csharp
const float OffsetForward = 0f;   // metre, + ileri (parmakların gösterdiği yön)
const float OffsetUp      = 0f;   // metre, + yukarı (başparmağın olduğu taraf)
const float OffsetInward  = 0f;   // metre, + gövde ortasına doğru

const float TweakYaw   = 0f;      // derece, + eli yukarı eksende dışa çevirir
const float TweakPitch = -10f;    // derece, + parmak uçlarını AŞAĞI indirir
const float TweakRoll  = 40f;     // derece, + avuç içini aşağı döndürür
```

**Bunlar kumandanın ham eksenleri DEĞİL, kullanıcının gördüğü yönler.** Ham grip eksenleri sezgisel değil (cihazda ölçüldü: grip **+Z ≈ dünya yukarısı**, **+Y ≈ dünya gerisi**, **+X ≈ dünya sağı**); çevrim kodun içinde yapılıyor.

Hepsi **SAĞ ELE** göre yazılır; sol el `WeaponGripMath.MirrorX` ile aynalanır. **Sol el için ayrı sayı YOK** — simetri ayarın değil yapının garantisi.

`BaseWristEuler = (0, 220.3, 209.9)` **hesaplanmış** temel dönüş, ona dokunma; ince ayar `Tweak*` ile yapılır.

**Her değişiklikten sonra ayna testi koşulmalı** (aşağıdaki §5).

**İşaretler ölçüldü (2026-08-12), tahmin değil:** yaw `+` = dışa ✓, roll `+` = avuç içi aşağı ✓, **pitch `+` = parmak uçları AŞAĞI** — belgenin ilk hâlinde "yukarı kaldırır" yazıyordu, **yanlıştı** (Unity'de +X ekseni etrafında artı dönüş burnu aşağı eğer). Yeni bir tweak eklenirse işareti yazmadan önce ölç.

**Parmak ucu açıları (rest duruş, + = aşağı):** temel hâlde orta parmak ucu 2.49° aşağıydı; `-2.5` onu düzledi ama **kullanıcı hâlâ aşağı gördü** — çünkü ölçüm *kumandaya göre*, kumandanın sapı elde zaten öne-aşağı eğik duruyor. `-10`'da orta parmak ucu 7.51° yukarıda (işaret 16.95 yukarı, serçe 9.92 aşağı, başparmak 50.61 yukarı). Ölçek birebir: her 1° eksi = 1° yukarı. **Cihaz kararı bekliyor.**

**Ölçülmüş referans (2026-08-12):** temel duruşta (tüm Tweak = 0) sağ avucun normali **40.3° YUKARI** yatık. `TweakRoll` her derece için bunu birebir aşağı çeker: `+20` → 20.3°, `+40` → 0.3° (avuç düpedüz içe bakar). Yani 5°'lik denemeler bu yatıklığın yanında **gözle görülmez** — kullanıcı ilk denemede tweak'in yönünü değil bu 40°'yi fark etti. Cihazda 20 denendi, az geldi; **yerleşen değer `TweakRoll = 40`** (avuç yukarı açısı 0.3°, avuç normali tam `x = ∓1.000`).

---

## 2. Mimari — neden böyle

Birinci şahıs eli avatarın iskeletinden **tamamen ayrık**. `FirstPersonHandView` (order 120) eli doğrudan kumanda taşıyıcısının altına parent'lar:

```
kumanda taşıyıcısı (NetworkVRPlayer.leftHand/rightHand)
│   DÜZGÜN OLMAYAN ölçek (0.08, 0.045, 0.13) — DOKUNMA
└── FP_HandView          ölçeği tersleyen düğüm; dönüşü HEP identity
    ├── Pose             silaha kaynaklanınca sürülen düğüm
    │   └── Hand         Meta XR el modeli (Generic rig: b_l_* / b_r_*)
    │                    + FirstPersonFingerCurl
    └── KumandaNoktasi   kumandanın gerçek yerini gösteren beyaz nokta
```

**Neden üç katlı:** ters ölçek ile serbest dönüş *aynı düğümde* olursa `S·R·S⁻¹` ortonormal olmaz ve **mesh makaslanır**. Ters ölçek düğümünün dönüşü identity kaldığı sürece `S·S⁻¹` sadeleşir ve alttaki `Pose` serbestçe döndürülebilir. Ölçüldü: rastgele dönüşlerde dik açıdan sapma ~1e-7.

**Taşıyıcının ölçeğini prefabta düzeltme:** `HandGrabber` silah tutuş offsetlerini `anchor.InverseTransformPoint` ile çözüyor — ölçek kalibrasyonun içinde.

**Neden avatardan ayrık:** kullanıcının mutlak kuralı — "kumanda neredeyse EL ORADA olmak zorunda". Kol IK'si bunu yapısal olarak veremez (kol ancak boyu kadar uzanır). Ayrıklık sayesinde garanti koddan değil parent-child ilişkisinden gelir. Ölçüldü: boş elde sapma 0.3–2.0 m'de **tam 0.0000 mm / 0.0000°**.

---

## 3. Davranış kuralları (cihazda doğrulanmış kararlar — yeniden sorma)

- **Boş el:** kumandaya birebir yapışık, gecikme/yumuşatma YOK.
- **Silah tutarken:** yalnız **ANA EL** silahın kabza ankrajına tam güçle oturur (`WeaponHandWeld.TryGetHandAnchor`). Ölçüldü: 12 silahta el↔kumanda 0–7 mm.
- **Destek eli:** V-Speedway modeli — silaha kaynaklanır, kumandanın gerçek yeri **beyaz noktayla** gösterilir (sapma <3 cm görünmez, sonra opaklık açılır), mesafe aşılınca **kopar**.
- **Kol (uzak oyuncu):** UZAYAMAZ. `ArmReach` bileği omuzdan `armLen*0.98`'e kelepçeler; kol düz kalıp hedefe *doğru* bakar. Dönüş kelepçelenmez.
- **REDDEDİLMİŞ, tekrarlama:** sapmayla weld ağırlığını soldurma (`6eb5795`), kayan destek rayı (`196a637`), avatar rig'inin parmaklarını kısaltma (üçüncü şahıs eldivenini bozuyor).

---

## 4. TUZAKLAR — hepsi bu oturumda canlı yaşandı

1. **El-lilik (ÜÇ KEZ patladı).** `Cross(parmakYönü, index−pinky)` **sağ avuçtan dışarı, sol elin SIRTINDAN** dışarı bakar. İki eli aynı eksene zorlamak birini 180° çevirir. Dahası: `Quaternion.LookRotation` **ayna-eşdeğer değildir** — aynalanmış iki girdiden AYNI dönüşü üretir, yani el-liliği girdilerden ummak işe yaramaz, **açıkça verilmeli**. Doğrusunu spec sabitliyor: *avuç normali sol avuçtan dışarı, SAĞ avuçtan içeri* → sağ avuç −x.
2. **Renk uzayı.** Proje **Linear**. `SetColor`'a gamma değeri vermek rengi iki kat açar, `.linear` vermek fazla koyultur. Çözüm tahmin etmemek: küçük **sRGB doku + beyaz `_BaseColor`** (askerin malzemesiyle aynı yol).
3. **`Object.Destroy` edit modunda ertelenir ve HİÇ çalışmaz.** `CreatePrimitive`'in collider'ı hayatta kalır. Moda göre `Destroy`/`DestroyImmediate`.
4. **Canlı ölçüm geri besleme kurar.** Kol boyunu her karede ölçmek, weld'in kendi yazdığı bileği geri okur. Boy **bir kez**, yerel uzayda ölçülür, kullanım anında `lossyScale` ile çarpılır.
5. **Yumuşatmanın içinden ölçme.** `LateUpdate` değerleri ağdaki gerçek değere (editörde 0) geri çeker; elle yazdığın değer yok olur. `FirstPersonFingerCurl.Apply(grip, trigger)` bu yüzden ayrı durur — **ölçüm onu çağırmalı**.
6. **Render açısı yanıltır.** Bu oturumda üst üste yanlış teşhis kondu (parmaklar kameraya kıvrılınca "düz" görünüyor; el kadraj dışı kalınca "kayıp"). **Sayı > piksel.** Şüphedeyken ölç.
7. **`Resources/` içindeki her şey build'e girer**, kullanılmasa bile.
8. **GPU sızıntısı Unity'yi çökertir.** Render döngüsünde her karede Mesh/RenderTexture yaratmak D3D12 buffer hatası verdi. Tek RT + her karede mesh imhası.

---

## 5. Doğrulama tarifleri

**Ayna testi (duruşa dokunulduysa ZORUNLU):** sol elin çerçevesini x'te aynala, sağla karşılaştır → parmak/başparmak/avuç üçünde de **≤1°** (bugün 0.00°). Ayrıca avuç `x` işaretleri **zıt** olmalı.

**Kıvrım:** `Apply(grip, trigger)` çağır, `b_?_middle3` ↔ `b_?_wrist` mesafesini ölç. grip 0→1'de **182 → 123 mm**. Tetik 0→1'de **yalnız** işaret parmağı (160→115 mm).

**Başparmak:** iki ölçüt birden. (1) Ucun avuç düzlemine işaretli uzaklığı her grip değerinde **≥12 mm** (düzeltme öncesi 2 mm'ye iniyordu; şu an tam kavramada 58.3 mm). (2) **Kavrama teması:** `r_thumb_finger_tip_marker` ↔ `r_index_fingernail_marker` mesafesi grip 0→1'de **133 → 60 → 1.1 mm**, yani tekdüze azalmalı. Eski kuralda grip 0.5'te 42 mm'ye inip 1.0'da 48 mm'ye geri açılıyordu — el kapandıkça uçlar uzaklaşıyordu, hatanın imzası buydu. İki elde de aynı sayı çıkmalı (aynalı yastık sapması 0.000 mm).

**Cihazdan sayı çekme:**
```
adb logcat -d -s Unity:I     →  [FPEl] ve [FPTutus] satırlarını ayıkla
```
adb PATH'te değil: `C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`. Kabukta takılabiliyor → `Start-Job` + `Wait-Job -Timeout`. Log tamponu 16 MB'a çıkarıldı.

**Ekran yakalama:** `GripOlcum/quest_yakala.ps1` — kareyi çeker, **tek göze kırpar ve küçültür** (ham kare 4128×2208, çift gözde el seçilmiyor). Çok kare varsa pafta yap.

**Editör ölçüm deseni:** prefabı `Instantiate` → `FirstPersonHandView.Attach(lc, rc, avatar)` (reflection ile) → ölç → `DestroyImmediate`. Oynatma modu gerekmez.

---

## 6. Açık işler

| iş | durum |
|---|---|
| İnce ayar (§1) | **sıradaki** |
| Cihazda canlı ayar aracı | planlandı, yazılmadı; gerekirse `WeaponGripTuner` deseni |
| Tutunma kapısı / kopma eşiği | ~45 cm ve silahın *herhangi* parçasına bakıyor; kundak ankrajına taşınacak. `[FPTutus]` verisi bekliyor |
| Silah başına parmak kıvrımı | **8 tüfekte orta/yüzük/serçe = 0.00** (el kapanmıyor). Silah başına 5 sayı, rig'den bağımsız |
| Silah filtreleme (ağırlık hissi) | ertelendi — H3VR "Hand Filtering"; **ele değil SİLAHA** uygulanacak; `aimHalfLifeMs` ailesi zaten var |
| Manşet/kol | bilek açık tüp; `MaterialDoubleSided` uygulandı ama kol yok |
| Uzak oyuncu eldiveni | hâlâ askerin kendi mesh'i (kısa parmak + başparmak dibi çentiği kabul edildi) |

---

## 7. Ellerle ilgili sayısal künye

- Meta XR Core SDK 205.0.0, `OculusHand_L/R` — lisans: Oculus SDK License, **Meta onaylı cihazlar için serbest** (hedef Quest 3). İki el **4.628 üçgen**.
- El ölçeği `HandScale = 1.20` (anatomik boy zaten doğruydu; VR'da küçük algılandığı için — 1.10 da küçük geldi, 2026-08-12'de büyütüldü). Açık elde bilek→orta parmak ucu 228.3 mm.
- Askerî palet, askerin `T_Soldier_Glove_BaseColor` dokusundan örneklendi: gövde `#564E3C` (%54), uç boğumlar `#352F22`.
- **Boyanmış mesh (`Glove_Meta_R/L`) ARTIK KULLANILMIYOR (2026-08-12).** İki alt-mesh'e ayrılmış hâli (1910 gövde / 404 uç üçgen) başparmakta **koyu, zikzak kenarlı bir leke** bırakıyordu; kullanıcı bunu "başparmak bozuluyor" diye gördü ve suçu duruşta sandık. Ölçüldü: vertex, normal, tangent, kemik ağırlığı ve üçgen sarımı orijinalle **birebir aynı**, fark yalnızca alt-mesh ayrımı — ama leke iki alt-mesh'e **aynı** malzeme verildiğinde bile duruyor, Meta'nın orijinal `r_handMeshNode` mesh'inde ise hiç yok. Şimdilik orijinal mesh + tek malzeme kullanılıyor; uç boğum koyuluğu doku işi yapılınca dokudan gelecek. **Teşhis dersi:** duruşu suçlamadan önce başparmağı DİNLENMEDE bırakıp render al — leke orada da duruyordu, yani poz masumdu.
- Kıvrım açıları: parmak 55/80/55. İşaret parmağı `max(grip, tetik)`.
- **Başparmak (2026-08-12'de baştan yazıldı):** eski 30/30/24 gitti. Duruş, rig'in **kendi anatomik eksen işaretçilerinde** dört sabit açı: `cmc_fe = −73°`, `cmc_aa = +1°`, `mcp_fe = +82°`, `ip_fe = +45°`. `thumb3` hiç döndürülmez. Eksen = işaretçinin `right` vektörü (konvansiyon işaret parmağında doğrulandı: `index_mcp_fe_axis.right = [0.98, −0.02, −0.18]`).
  - **Menteşeyi TÜRETME.** `Cross(uzanım, avuç normali)` başparmak için YANLIŞ: gerçek eksenler `cmc [0.04, 0.86, −0.51]`, `mcp [0.02, 0.50, −0.86]`, `ip [−0.10, 0.39, −0.92]` — hepsinin büyük −z bileşeni var, yani başparmak kendi düzleminde katlanır, avuç düzleminde değil. Türetilmiş eksenle parmak işarete **yanlış taraftan** yaklaşıyordu.
  - **REDDEDİLMİŞ, tekrarlama:** (1) serbest CCD — eksen kısıtsız olduğu için burulma binip mesh'i yamultuyor; (2) menteşe kısıtlı CCD ama türetilmiş eksenlerle — burulma bitti, yön yanlış kaldı; (3) efektör `thumb3` + hedef `index_null` — sayı güzel (5.1 mm) ama tırnak son parçanın *başında* kalıyor, uç boşluğa uzanıyor.
  - `ip` küçükken (13°) başparmak **düz** görünüyor. Büyütünce uç tırnaktan kaçar, cmc/mcp yeniden çözülmeli. Tarama: ip 13 → 7.4 mm, 25 → 2.9, **45 → 0.4**, 65 → 8.0 (uç tırnağı geçer).
- Kıvrım kuralı **tek yerde**: `FingerCurlMath` — avatarın `ProceduralFingerPoser`'ı da oradan kullanıyor. Değiştirirsen iki el birden değişir.
