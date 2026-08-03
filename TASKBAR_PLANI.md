# Üst Taskbar (takım skorları + süre) — Uygulama Planı

> Bu belge yeni bir oturumda sıfırdan devam edilebilsin diye yazıldı. Ölçüler ve API adları
> 2026-08-03'te `main` (`438af56`) üzerinde **doğrulandı**.

---

## 1. İstenen

Görüşün üst kısmında, şeffaf / düşük opaklıkta bir bar:

```
┌──────────────────────────────────────────────────────────────┐
│  ● MAVİ TAKIM   7          12:34          5   KIZIL TAKIM ●  │
└──────────────────────────────────────────────────────────────┘
   sol: mavi takım + skor    orta: süre    sağ: kırmızı takım + skor
```

---

## 2. Neyin hazır olduğu (yeniden yazma)

| İhtiyaç | Zaten var | Nerede |
|---|---|---|
| Takım skoru | `PlayerIdentity.TeamScore(byte team)` → o takımdaki tüm oyuncuların `Kills` toplamı | `Scripts/Player/PlayerIdentity.cs:256` |
| Kişisel skor | `PlayerIdentity.Kills` / `Deaths` (`NetworkVariable<ushort>`, sunucu yazar) | aynı dosya |
| Takım renkleri | `PlayerIdentity.TeamAColor` (mavi, takım **1**), `TeamBColor` (kırmızı, takım **2**) | aynı dosya |
| Senkron sunucu saati | `NetworkManager.ServerTime.Time` — projede zaten kullanılıyor | `Weapons/NetworkWeapon.cs:686` |
| Panel paleti | `UITheme.PanelBg / PanelEdge / SurfaceFill / SurfaceEdge / AccentCyan / TextPrimary / TextMuted / TeamRedText / TeamBlueText` | `Scripts/UI/UITheme.cs` |
| Yuvarlak köşeli yüzey | `UITheme.MakeRounded(parent, ad, merkez, boyut, yarıçap, renk, z, kuyruk)` | `UITheme.cs` |
| Çerçeveli yüzey | `UITheme.MakeOutlined(...)` | `UITheme.cs` |
| Dünya-uzayı yazı | `UITheme.MakeText(parent, metin, renk, satırYüksekliğiMetre, hizalama, kuyruk)` | `UITheme.cs` |
| Mesh (yuvarlak dikdörtgen, ok, üçgen) | `UIMesh.RoundedRect / Arrow / Play / Bolt` | `Scripts/UI/UIMesh.cs` |
| HUD malzemesi | `UITheme.CreateOverlayMaterial(renk)` | `UITheme.cs` |

**Sonuç: yeni bir ağ katmanı gerekmiyor.** Skor zaten replike, saat zaten senkron.

---

## 3. "SÜRE" ne demek? — ekip akış şeması cevapladı

> **Güncelleme:** Ekip akış şeması (bkz. [MOD_SECIMI_PLANI.md](MOD_SECIMI_PLANI.md) — aynı şema)
> ŞERİT 4'te bunu netleştiriyor:
>
> - `CONFIG · Maç süresi: 3:00` — "Varsayılan 3 dakika, config'ten değiştirilebilir"
> - `ADIM · HUD · Maç sürüyor` — "**Geri sayım** ve takım skoru ekranın **üst-ortasında**,
>   mevcut 0–0 göstergesinin üzerinde birlikte"
> - `KARAR · Süre doldu mu?` → Hayır = döngü, Evet = skor karşılaştırması → kazanan/beraberlik
>   → ana menüye dön
>
> Yani hedef **geri sayım** (Seçenek B) ve taskbar tam olarak şemadaki o HUD öğesi. Ayrıca şema
> "mevcut 0–0 göstergesinin ÜZERİNDE birlikte" diyor — bu, §5'teki kill paneli skor başlığı
> yinelenmesini de doğruluyor: skor taskbar'a taşınmalı.
>
> **Sıralama önerisi değişmiyor:** yine de A ile başla (10 satır, sıfır risk), taskbar'ı
> `SetTime(string)` üzerinden besle. `MatchManager` gelince yalnızca besleyen taraf değişir.
> Böylece taskbar `MatchManager`'ı BEKLEMEZ.

Projede **maç/tur mantığı YOK** — `MatchManager` diye bir şey yok, skor limiti yok, maç bitişi yok.
Bu, 2026-07-27 incelemesinde "eksik oyun katmanı" olarak zaten işaretlenmişti.

### Seçenek A — Geçen süre (ÖNERİLEN, v1)
`NetworkManager.ServerTime.Time` her istemcide **aynı** değeri verir. Sunucu açıldığından beri
geçen süreyi `mm:ss` yazmak:

- Yeni netcode **yok**, yeni sahne objesi **yok**, `NetworkObject` **yok**.
- Tüm oyuncular aynı sayıyı görür (senkron).
- ~10 satır kod.

```csharp
double t = NetworkManager.Singleton.ServerTime.Time;
int dk = (int)(t / 60), sn = (int)(t % 60);
label.text = $"{dk:00}:{sn:00}";
```

### Seçenek B — Geri sayım (MatchManager gerekir)
Maç uzunluğu + başlangıç olayı + bitiş koşulu demek. Bu **ayrı bir iş**: sahneye
`NetworkObject` taşıyan bir `Match` objesi, `NetworkVariable<double> MatchEndServerTime`,
maç sonu ekranı, skor sıfırlama. Taskbar'ı buna bağlamak taskbar işini 3-4 katına çıkarır.

**Öneri: A ile başla.** Taskbar'ın süre alanını tek bir `string` üzerinden besle
(`SetTime(string)`), böylece MatchManager geldiğinde yalnızca besleyen taraf değişir,
taskbar'a dokunulmaz.

---

## 4. ⚠ Yerleşim: mevcut kill paneliyle ÇAKIŞMA var

Kill paneli (`KillFeedUI`) şu an:

| | değer | 1.2 m'de açı |
|---|---|---|
| mesafe | 1.2 m | — |
| yatay merkez | −0.58 m | −25.8° |
| dikey merkez | +0.42 m | +19.3° |
| genişlik | 0.52 m | x aralığı **−0.84 … −0.32 m** |
| üst kenar (başlık dahil) | ~+0.47 m | +21.3° |

**Ortalanmış 0.9 m'lik bir bar (±0.45 m) kill paneliyle 13 cm çakışır.**

### Çözüm: barı DAHA YUKARI koy

| Parametre | Değer | Gerekçe |
|---|---|---|
| mesafe | 1.2 m | kill paneliyle aynı düzlem |
| dikey merkez | **+0.64 m** (≈ **+28°**) | kill panelinin üst kenarını (0.47 m) 12 cm aşar |
| genişlik | 0.90 m (±21°) | Quest 3'te rahat okunur |
| yükseklik | 0.10 m | |

+28° "bak-gör" bölgesi: nişan hattında değil, kafanı hafif kaldırınca orada. Skor tablosu
zaten sürekli izlenen bir şey değil — bu doğru davranış.

**Sınır:** Quest 3'te merkezden ~30°'yi geçen içerik lens kenarına düşüp bulanıklaşır.
+28° sınıra yakın; cihazda ilk bakılacak şey bu. Rahatsız ederse iki yol var:
1. Barı +24°'ye indir ve kill panelini +14°'ye indir (ikisi birlikte kayar),
2. Barı dar tut (0.6 m) ve +20°'de bırak.

---

## 5. ⚠ Yinelenen skor: kill panelinin başlığı

`KillFeedUI` şu an kendi başlığında zaten takım skorunu yazıyor (`MAVİ 7 — KIRMIZI 5`,
`RefreshScore()` / `SetScore()`, `_scoreMine / _scoreSep / _scoreTheirs`).

Taskbar gelince bu **iki yerde aynı bilgi** olur. Karar:

- **Öneri:** kill panelinin skor başlığını **kaldır** (`BuildHeader()` ve `RefreshScore()`
  silinir, `_score*` alanları gider). Taskbar tek kaynak olur, kill paneli yalnızca
  "kim kimi öldürdü" işini yapar — daha temiz bir sorumluluk ayrımı.
- Alternatif: başlık kalsın ama taskbar'da skor olmasın — o zaman kullanıcının istediği
  yerleşim olmaz.

---

## 6. Görünüm

Giriş ekranı / ölüm kartıyla **aynı palet** (`UITheme`). Yeni ham renk yazma — bir kez
kayıp düzeltildi, sebebi renklerin kopyalanmasıydı.

| Öğe | Renk | Not |
|---|---|---|
| Bar zemini | `UITheme.PanelBg` **alfa ~0.35** | kullanıcı isteği: şeffaf/düşük opaklık |
| Bar kenarı | `UITheme.PanelEdge` alfa ~0.5 | ince çerçeve |
| Mavi takım yazısı | `UITheme.TeamBlueText` | |
| Kırmızı takım yazısı | `UITheme.TeamRedText` | |
| Skor sayıları | `UITheme.TextPrimary`, takım renginde değil | okunurluk; renk chip'ten geliyor |
| Süre | `UITheme.TextPrimary` | |
| Ayraç çizgileri | `UITheme.SurfaceEdge` | süreyi skorlardan ayırır |

**Renk chip'i:** takım adının yanına `UIMesh.RoundedRect` ile küçük dolu bir kare
(`PlayerIdentity.TeamAColor` / `TeamBColor`) — takım rengini yazıdan bağımsız gösterir.

### Yazı boyutları (1.2 m'de)

| Öğe | Satır yüksekliği | Görme açısı |
|---|---|---|
| Takım adı | 0.026 m | ~1.24° |
| Skor | 0.048 m | ~2.3° — barın en baskın öğesi |
| Süre | 0.044 m | ~2.1° |

VR'da rahat okuma eşiği ~1°; hepsi üstünde.

---

## 7. Dosyalar

**Yeni:** `Assets/_VRMultiplayer/Scripts/UI/MatchBarUI.cs`

```csharp
public class MatchBarUI : MonoBehaviour
{
    public float distance = 1.2f, offsetUp = 0.64f;
    public float barWidth = 0.90f, barHeight = 0.10f;
    public float followSpeed = 9f;
    [Range(0f,1f)] public float bgAlpha = 0.35f;

    public void SetTime(string s);   // MatchManager gelince tek dokunulacak yer
}
```

- `Awake`: barı kur (zemin + çerçeve + 2 chip + 2 takım adı + 2 skor + süre + ayraçlar).
- `LateUpdate`: **sönümlü** kafa takibi (`KillFeedUI.Follow()` ile birebir aynı desen —
  oradan kopyala, göze çivilenmiş HUD yorucudur), skorları ~2 Hz tazele.
- Skor: `PlayerIdentity.TeamScore(1)` / `TeamScore(2)`.
- Süre: `NetworkManager.Singleton.ServerTime.Time` (Seçenek A).

**Değişecek:**

| Dosya | Değişiklik |
|---|---|
| `Scripts/Player/PlayerHUD.cs` | `BuildHud()` içinde `MatchBarUI` üret, `OnNetworkDespawn`'da yok et — `_killFeed` ile birebir aynı kalıp |
| `Scripts/UI/KillFeedUI.cs` | Skor başlığını kaldır (bkz. §5) |

**Sahne/prefab değişikliği: YOK.** Diğer HUD öğeleri gibi çalışma anında kurulur.

---

## 8. Bu projede yanan tuzaklar (tekrar yanma)

1. **`_ZTest` URP'de çalışmaz.** `URP/Unlit`'in pass'inde `ZTest` tanımlı değil, `_ZTest`
   property'si YOK — `SetInt("_ZTest", 8)` sessizce hiçbir şey yapmaz. HUD için
   `UITheme.CreateOverlayMaterial` (GUI/Text Shader) kullan; oyun zaten yazı çizdiği için
   build'de garanti, strip riski yok.
2. **Render kuyruğu.** Sahnedeki oda geometrisi (`RoomPlanTemplate` duvarları, masa) saydam
   kuyruk **3000**'de; ölüm perdesi/vinyet/hasar flaşı da 3000'de ve kafaya çok yakın. Aynı
   kuyrukta saydamlar mesafeye göre sıralanır → HUD ezilir. **Kuyruk 3050+ kullan**
   (kill paneli ve ölüm kartı öyle yapıyor).
3. **Font atanmazsa Quest'te yazı ÇIZILMEZ.** Unity 6'nın varsayılan TextMesh fontu yok.
   `UITheme.MakeText` bunu zaten hallediyor — ham `TextMesh` üretme.
4. **Editor kamerası yanıltır.** Editor'de `Camera.main` zemin hizasında (y=0) olabilir;
   gerçek kafa ~1.62 m'de. Yön oku bu yüzden bir kez görüş alanının dışında kalmıştı.
   Test ederken `XRRigReference.HeadOrCamera.position` değerini 1.62'ye kur — **ama Play
   modundan çıkarken sahneye kaydolmasın**, bir kez sızdı.
5. **Yazı boyutunu punto ile değil metre/derece ile düşün** (`UITheme.SizeText`).

---

## 9. Doğrulama

**Editor (host olarak):**
```csharp
VRMultiplayer.PlayerProfile.Confirm("Test", VRMultiplayer.PlayerProfile.TeamBlue);
var nm = Unity.Netcode.NetworkManager.Singleton;
nm.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>().SetConnectionData("127.0.0.1", 7800, "0.0.0.0");
nm.StartHost();
// sonraki çağrıda:
var id = UnityEngine.Object.FindFirstObjectByType<VRMultiplayer.PlayerIdentity>();
id.Team.Value = 1; id.Kills.Value = 7;
```

| Kontrol | Beklenen |
|---|---|
| Skor | `TeamScore(1)` = 7 barda görünüyor |
| Süre | `mm:ss` ilerliyor, iki istemcide **aynı** |
| Konum | `Camera.main.WorldToViewportPoint(bar.position)` → x≈0.50, y kadraj içinde |
| Açı | bar merkezi ile kafa arasındaki dikey açı ≈ +28° |
| Çakışma | bar alt kenarı (~0.59 m) > kill paneli üst kenarı (~0.47 m) |
| Kuyruk | bar malzemelerinin `renderQueue` ≥ 3050 |
| Konsol | 0 hata |

**Cihaz (Quest 3):**
- [ ] Bar **görünüyor** (font düzeltmesi Editor'de görünmesiyle kanıtlanmaz)
- [ ] +28° rahat mı, yoksa boyun yoruyor mu → `offsetUp` ile ayarla
- [ ] Bar kenarları lens kenarında bulanıklaşıyor mu → `barWidth` daralt
- [ ] Şeffaflık (0.35) oyun sırasında rahatsız etmiyor
- [ ] Duvarın önünde kayboluyor mu (kuyruk doğru mu)

---

## 10. Sıra

1. `MatchBarUI` iskeleti + `PlayerHUD` bağlantısı → Editor'de görünsün
2. Skorları bağla (`TeamScore`)
3. Süreyi bağla (`ServerTime.Time`)
4. Kill panelinin skor başlığını kaldır
5. Editor doğrulaması (§9)
6. **Cihaz testi** → `offsetUp` / `barWidth` / `bgAlpha` ince ayarı
7. Commit + main'e merge

**Kapsam dışı (bilerek):** maç bitiş koşulu, kazanan/beraberlik ekranı, ana menüye dönüş,
kalıcı skor tablosu. Hepsi `MatchManager` işi ve ayrı bir dal hak ediyor — şemada ŞERİT 4'ün
geri kalanı.

---

## 11. Sıra bağımlılığı

Bu iş [MOD_SECIMI_PLANI.md](MOD_SECIMI_PLANI.md)'ye **bağlı değil**; ikisi bağımsız ilerleyebilir.
Ama şemanın bütünü şu sırayı öneriyor:

1. **Mod seçimi** (yaratıcı/oyuncu ayrımı) — arkadaşın dalı çekilmeden önce, çakışmayı önlemek için
2. **Taskbar** (skor + süre) — bu belge
3. **MatchManager** (3:00 geri sayım, maç sonu, ana menüye dönüş) — taskbar'ın `SetTime`'ını devralır
