# Mod Seçimi (Yaratıcı / Oyuncu) — Uygulama Planı

> Kaynak: ekip akış şeması (VR Arena · Yaratıcı & Oyuncu modları, harita havuzu, kalibrasyon, maç).
> Bu belge şemanın **yalnızca ŞERİT 1'in ilk kararını** hedefler. Amaç: yaratıcı modu ayrı bir
> dalda geliştiren ekip arkadaşının işi `main`'e çekildiğinde **çakışma çıkmaması**.
> Ölçüler ve API adları 2026-08-03'te `main` (`438af56`) üzerinde doğrulandı.

---

## 1. Hedef (dar kapsam)

Uygulama açılınca **oyuncu girişi ekranından ÖNCE** bir mod seçim paneli:

```
              VR ARENA
            Bir mod seç
   ┌──────────────┐   ┌──────────────┐
   │   YARATICI   │   │    OYUNCU    │
   │ Harita tasarla│   │  Maça katıl  │
   └──────────────┘   └──────────────┘
```

- **YARATICI** → ekip arkadaşının dalındaki yaratıcı akışı devralır.
- **OYUNCU** → mevcut isim + takım ekranı (`PlayerEntryUI`) açılır, oradan sonrası bugünkü akış.

Panel, oyuncu giriş ekranıyla **aynı tema ve aynı kalitede** (`UITheme` paleti, yuvarlak köşeli
paneller, gradyan başlık, lazer imleç).

**Bu turda YAPILMAYACAK** (şemada var, sonraya): harita editörü, kayıtlı haritalar, harita havuzu,
harita yöneticisi, havuz boş kontrolü, maç sonu → ana menü dönüşü. Bunlar arkadaşın dalının ve
`MatchManager` işinin konusu.

---

## 2. Asıl mesele: çakışmasız birleşme

Arkadaşın dalının ne içerdiğini **bilmiyoruz**. Plan bu belirsizliği yönetecek şekilde kuruldu.

### 2.1 Tek bir bağlantı noktası (seam)

Yaratıcı modun devreye girmesi için ortak bir dosyayı ikimizin de düzenlemesi **gerekmeyecek**.
Bunun yerine tek bir olay:

```csharp
// Scripts/Flow/AppMode.cs  (YENİ — yalnızca biz yazıyoruz)
public static class AppMode
{
    public enum Mode { None, Creative, Player }

    public static Mode Current { get; private set; }
    public static bool IsPlayer   => Current == Mode.Player;
    public static bool IsCreative => Current == Mode.Creative;

    /// Mod seçildiğinde tetiklenir. Yaratıcı taraf BUNA abone olur.
    public static event System.Action<Mode> Chosen;

    public static void Choose(Mode m);          // panel çağırır
    public static void ReturnToModeSelect();    // maç/işlem bitince (şemadaki DÖNÜŞ)
}
```

Arkadaşın dalının yapması gereken **tek şey**:

```csharp
void OnEnable()  => AppMode.Chosen += OnModeChosen;
void OnDisable() => AppMode.Chosen -= OnModeChosen;
void OnModeChosen(AppMode.Mode m) { if (m == AppMode.Mode.Creative) OpenCreativeMenu(); }
```

Ortak dosyada satır çakışması yok.

### 2.2 Dokunduğumuz dosyalar minimumda

| Dosya | Değişiklik | Çakışma riski |
|---|---|---|
| `Scripts/Flow/AppMode.cs` | **YENİ** | yok |
| `Scripts/UI/ModeSelectPanel.cs` | **YENİ** | yok |
| `Scripts/UI/ModeSelectUI.cs` | **YENİ** | yok |
| `Scripts/UI/PlayerEntryUI.cs` | Bootstrap koşullu olur (~4 satır) | düşük |
| `Scripts/Networking/LanBootstrap.cs` | Kapıya `AppMode.IsPlayer` eklenir (2 satır) | düşük |

**Sahne/prefab değişikliği YOK.** Panel kendini `RuntimeInitializeOnLoadMethod` ile kurar —
projedeki yerleşik desen (`PlayerEntryUI`, `WeaponSelectorUI`, `SingleAudioListener`).
Arkadaşın dalı sahneyi değiştirdiyse bile bizimkiyle çakışmaz.

### 2.3 Birleştirmeden ÖNCE yapılacak inceleme

Dalı doğrudan `main`'e merge **etme**. Önce ne getirdiğine bak:

```bash
git fetch origin
git log --oneline main..origin/<arkadasin-dali>
git diff --stat main...origin/<arkadasin-dali>
```

Bakılacaklar:

1. **Kendi mod seçim ekranını yazmış mı?** Yazmışsa ikisinden biri elenmeli — karar önce verilsin.
   (Bizimki `PlayerEntryUI` ile aynı temada; onunki değilse bizimki kalsın.)
2. **`SampleScene.unity`'ye dokunmuş mu?** Dokunduysa sahne birleştirmesi elle yapılmalı.
   ⚠ `UnityYAMLMerge` bu projede **sessizce kayıp veriyor** — sürücü uzantısız temp dosyada hiç
   çalışmıyor, dosyayı OURS bırakıp karşı tarafı yutuyor ve marker koymuyor. Çözüm: `git merge-file`
   ve sonrasında md5 karşılaştırması.
3. **`NetworkPlayer.prefab`'a dokunmuş mu?** Dokunduysa dikkat: sihirbaz Adım 1 prefabı sıfırdan
   kurup combat/takım bileşenlerini siliyor.
4. **Kendi lazer imleci / panel altyapısı yazmış mı?** Yazmışsa `VRPointer` + `UITheme` + `UIMesh`
   zaten var; ikinci bir kopya yerine bunlara geçmesi önerilir.

Merge'ü geçici bir dalda dene:
```bash
git checkout -b deneme/creative-merge main
git merge origin/<arkadasin-dali>
```

---

## 3. Akış (şemanın ŞERİT 1 → ŞERİT 3 bağlantısı)

```
UYGULAMA BAŞLAR
      │
      ▼
[MOD SEÇİMİ]  ◄──────────────── AppMode.ReturnToModeSelect()  (şemadaki DÖNÜŞ; şimdilik çağıran yok)
   │       │
YARATICI  OYUNCU
   │       │
   │       ▼
   │   [İSİM + TAKIM]  (PlayerEntryUI — mevcut)
   │       │
   │       ▼
   │   B / OYUNA BAŞLA → bağlan → kalibrasyon → takım bölgesi → maç
   │
   ▼
AppMode.Chosen(Creative)  →  arkadaşın yaratıcı akışı
```

### Şemadan bilinçli olarak ERTELENENLER

| Şema adımı | Neden şimdi değil |
|---|---|
| ŞERİT 3 "Havuzda harita var mı?" kontrolü | Harita havuzu arkadaşın dalında; havuz API'si gelince `AppMode` oyuncu yoluna **tek satırlık** bir ön kontrol olarak eklenir (bkz. §6) |
| ŞERİT 1 "Maç bitince ana menüye dön" | Maç bitişi yok (`MatchManager` yok). `ReturnToModeSelect()` hazır bekliyor |
| ŞERİT 2 tümü (kayıt/havuz/yönetici) | Tamamen arkadaşın dalı |

---

## 4. Panel tasarımı

Oyuncu giriş ekranıyla **aynı dil**. Yeni ham renk yazma — `UITheme`'den al. (Bir kez renkler
kopyalandığı için iki ekran birbirinden kaymıştı; palet o yüzden tek kaynağa alındı.)

### Ölçüler (mesafe 1.4 m, `PlayerEntryUI` ile aynı yerleşim mantığı)

| Öğe | Değer |
|---|---|
| Panel | 0.78 × 0.44 m (±15.5° × ±8.9°) |
| Kart (her biri) | 0.32 × 0.20 m |
| Kartlar arası boşluk | 0.05 m |
| Köşe yarıçapı | panel 0.020 m, kart 0.014 m |

### Yazı boyutları (1.4 m'de)

| Öğe | Satır yüksekliği | Görme açısı |
|---|---|---|
| Başlık "VR ARENA" | 0.050 m | ~2.0° |
| Alt başlık "Bir mod seç" | 0.020 m | ~0.8° |
| Kart başlığı (YARATICI / OYUNCU) | 0.042 m | ~1.7° |
| Kart açıklaması | 0.020 m | ~0.8° |

### Renkler

| Öğe | Kaynak |
|---|---|
| Panel zemini | `UITheme.PanelBg` — **tam opak** (yarı saydam panel aydınlık odada solup ucuz duruyor) |
| Panel kenarı | `UITheme.PanelEdge` |
| Başlık | `UITheme.AccentCyan` → `AccentPurple` gradyan (harf harf, `PlayerEntryPanel.BuildTitle` deseni) |
| YARATICI kartı | kenar/yazı `UITheme.AccentPurple` |
| OYUNCU kartı | kenar/yazı `UITheme.AccentCyan` |
| Kart dolgusu | `UITheme.SurfaceFill`; üzerine gelince kenar parlar + arkasında hale belirir |
| Alt başlık / açıklama | `UITheme.TextMuted` |

### Etkileşim

- `VRPointer` (mevcut lazer imleç — nişan pozunu okuyor, `aimTrim` ile ayarlanabilir).
- Üzerine gelince: `UIMesh.RoundedRect` ile taşınan tek bir vurgu (`PlayerEntryPanel` deseni).
- Tıklayınca **doğrudan geçiş** — ayrı onay butonu yok, iki büyük seçenek yeterince net.
- Basışta haptik: `VRPointer.Haptic()`.
- Masaüstü yedeği: `OnGUI` ile iki buton (`Application.isMobilePlatform` ile mobilde kapalı) —
  gözlüksüz iterasyon için, `PlayerEntryUI` aynısını yapıyor.

---

## 5. Mevcut koda dokunuşlar

### 5.1 `PlayerEntryUI` — artık kendiliğinden açılmaz

Şu an (`PlayerEntryUI.cs:49`):
```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
static void Bootstrap() { ... hemen kurulur ... }
```

Olacak: bootstrap kendini kurmak yerine **mod seçimini bekler**.
```csharp
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
static void Bootstrap() => AppMode.Chosen += m => { if (m == AppMode.Mode.Player) Create(); };
```
`Create()` bugünkü gövde. Aboneliğin `AppMode.ResetStatics` ile temizlenmesi şart —
domain reload kapalıyken statikler oyunlar arası taşınır (projede bu kalıp her statikte var).

### 5.2 `LanBootstrap` — yaratıcı modda ağ başlamasın

İki yerde `PlayerProfile.Confirmed` kapısı var (satır 94 ve 105). Yanına `AppMode.IsPlayer`
eklenir: yaratıcı modda katılım paneli açılmaz, B tuşu çalışmaz.

⚠ **`StartAsServer()` bu kapıya TAKILMAMALI.** PC adanmış sunucu olarak çalışıyor, avatar spawn
etmiyor ve mod seçimi kulaklık işi. Bugün de profil kapısına takılmıyor; aynı muafiyet korunur.

### 5.3 Mod seçimi kimlere gösterilir

`LanBootstrap`'ın katılım panelini `right.isValid` ile kapıladığı gibi, mod paneli de
**XR sağ el cihazı geçerliyse** açılır. PC'de (gözlüksüz) panel çıkmaz, `SUNUCU başlat` butonu
her zamanki gibi çalışır.

---

## 6. Gelecek bağlantı noktaları (şimdi boş bırakılıyor)

| Kanca | Ne zaman dolar |
|---|---|
| `AppMode.ReturnToModeSelect()` | Maç bitiş ekranı (`MatchManager`) geldiğinde çağrılır — şemadaki DÖNÜŞ |
| Oyuncu yolunda "havuz boş mu?" ön kontrolü | Harita havuzu API'si geldiğinde; boşsa şemadaki UYARI EKRANI gösterilip yaratıcıya yönlendirilir |
| `AppMode.Chosen(Creative)` | Arkadaşın dalı abone olur |

---

## 7. Sıra

1. `AppMode` (statik, olay, `ResetStatics`)
2. `ModeSelectPanel` + `ModeSelectUI` — panel çizimi ve yerleşim
3. `PlayerEntryUI` bootstrap'ini koşullu yap
4. `LanBootstrap` kapısına `AppMode.IsPlayer` ekle
5. Editor doğrulaması (§8)
6. **Cihaz testi**
7. Commit → `main`
8. **Sonra** arkadaşın dalını §2.3'teki incelemeyle çek

---

## 8. Doğrulama

**Editor:**

| Kontrol | Beklenen |
|---|---|
| Açılış | Mod paneli görünüyor, isim ekranı **görünmüyor** |
| OYUNCU tıklanınca | Mod paneli kapanır, isim+takım ekranı açılır |
| YARATICI tıklanınca | Mod paneli kapanır, `AppMode.Chosen(Creative)` tetiklenir (test abonesiyle logla) |
| Yaratıcı modda | `LanBootstrap` katılım paneli açılmıyor, B tuşu çalışmıyor |
| Tıklama isabeti | Her ögenin merkezine ışın → kendisi bulunuyor; panel dışı `-1` |
| PC (gözlüksüz) | Mod paneli çıkmıyor, `SUNUCU başlat` çalışıyor |
| Konsol | 0 hata |

**Cihaz (Quest 3):**
- [ ] Panel **görünüyor** (Editor'de görünmesi kanıt değil — Unity 6'da fontsuz `TextMesh` cihazda çizilmez)
- [ ] Lazer kartlara doğru isabet ediyor
- [ ] Panel duvarın arkasında kaybolmuyor (overlay malzeme + kuyruk ≥ 3050)
- [ ] Yazılar gözlüksüz okunuyor

---

## 9. Bu projede yanan tuzaklar

1. **`_ZTest` URP'de çalışmaz.** `URP/Unlit`'te böyle bir property yok; `SetInt("_ZTest", 8)`
   sessizce hiçbir şey yapmaz. HUD/menü için `UITheme.CreateOverlayMaterial` (GUI/Text Shader).
2. **Render kuyruğu.** Oda geometrisi ve kafa efektleri saydam kuyruk 3000'de; aynı kuyrukta
   saydamlar mesafeye göre sıralanır → panel delinir. **3050+ kullan.**
3. **Font atanmazsa Quest'te yazı çizilmez.** `UITheme.MakeText` bunu hallediyor; ham `TextMesh`
   üretme.
4. **Editor kamerası zemin hizasında olabilir**, gerçek kafa ~1.62 m'de. Yerleşim testinde
   `XRRigReference.HeadOrCamera.position`'ı 1.62'ye kur — **ama Play modundan çıkarken sahneye
   kaydolmasın**, bir kez sızdı.
5. **Yazı boyutu punto değil metre/derece** (`UITheme.SizeText`); rahat okuma eşiği ~1°.
6. **Panel dünyaya sabit olmalı, kafaya kilitli değil** — lazerle nişan alınan yüzey her kare
   kafayla kayarsa tuşa basılamaz. `PlayerEntryUI`'daki tembel takip (38°'de yumuşak
   yeniden merkezleme) kopyalanmalı.
