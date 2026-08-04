# Skorbord (B tuşu) — Tasarım Planı

> Üst taskbar'ın (`MatchBarUI`) yerine geçen, B'ye basınca ekranda açılan CS-tarzı skor
> tablosu. Referans: kullanıcının gönderdiği görsel (iki takım sütunu, ortada süre, altta
> kişisel şerit). Ölçüler ve API adları 2026-08-04'te `main` (`6dc44b1`) üzerinde doğrulandı.
> **Onay bekliyor — kod yazılmadı.**

---

## 1. Hedef

Oyun içinde **sağ B basılı tutulunca** görüş merkezinde bir tablo:

```
                        ┌─────────────┐
                        │    SÜRE     │
                        │    12:34    │
   ┌────────────────────┴──┬──────────┴───────────────────┐
   │ ● MAVİ TAKIM      12  │  7        KIZIL TAKIM ●      │  ← renkli başlık şeritleri
   ├───────────────────────┼──────────────────────────────┤
   │ İsim         K    Ö   │  İsim              K    Ö    │
   │ CRSY        15    2   │  ccKane           10    5    │
   │▸SEN (vurgu)  6    3   │  Ssswing           5    5    │
   │ Fires ÖLÜ    5    2   │  crankeybw ÖLÜ     2    5    │  ← ölü: soluk + ÖLÜ etiketi
   └───────────────────────┴──────────────────────────────┘
          ┌────────────────────────────────────┐
          │  ● SENİN_ADIN        K 6  ·  Ö 3   │  ← alt şerit, büyük
          └────────────────────────────────────┘
```

- **Açılış animasyonu:** panel ortada yatay bir ÇİZGİ olarak belirir, aşağı-yukarı
  büyüyerek açılır (kullanıcı isteği). Kapanış tersi, daha hızlı.
- Kendi satırın tabloda **vurgulu**, ayrıca altta büyük kişisel şerit (istek: ikisi birden).
- Ölü oyuncular belirgin: satır soluklaşır + isim yanında kırmızı **ÖLÜ** etiketi.

**Bu turda YOK:** ping (ağ RTT — istenmedi), MVP/harita şeridi, maç sonu ekranı,
kazanan ilanı, skor sıfırlama (hepsi `MatchManager` işi).

---

## 2. Kaldırılanlar (kullanıcı isteği) ve etkileri

### 2.1 Üst taskbar — `MatchBarUI.cs` SİLİNİR
- `PlayerHUD`'daki `_matchBar` alanı + üretim + yok etme satırları gider.
- `UIMesh.RoundedRectOutline` **KALIR** — skorbordun çerçevesi kullanacak.
- `TASKBAR_PLANI.md` belgesi eskidi — aynı commit'te silinmesi önerilir.
- `SetTime(string)` kancası (MatchManager bağlantı noktası) skorborda TAŞINIR, kaybolmaz.

### 2.2 B tuşu = yalnızca skorbord
Sağ B'nin bugünkü sahipleri ve yeni durumu:

| Nerede | Bugün | Olacak |
|---|---|---|
| `LanBootstrap.cs:98` | B = katıl / yeniden dene | **KALDIRILIR** |
| `TeamSelector.cs:74` | Yedek panelde B = kırmızı takım | **KALDIRILIR** (bkz. 2.3) |
| `ConstructorPlacer.cs:1356` | İnşa modunda B | DOKUNULMAZ — yaratıcı modda, skorbord oyuncu modunda; çakışmaz |
| Skorbord (YENİ) | — | Oyun içinde tek sahip |

⚠ **B kalkınca kopan bağlantı nasıl yeniden kurulur?** Bugün düşen oyuncu B ile tekrar
katılıyor ("B = YENIDEN KATIL"). Öneri: **otomatik yeniden bağlanma** — oturum düştüğünde
(`AppMode.IsPlayer` + profil onaylı iken) `LanBootstrap` 2 sn bekleyip `JoinAsClient`'ı
kendisi çağırır, bulamazsa döngüyle dener. Panel metinleri güncellenir
("Yeniden bağlanılıyor..."). B'siz kalan tek akış buydu; otomatiği daha iyi UX.

### 2.3 A tuşu — ERTELENDİ (kullanıcı kararı, 2026-08-04)

Kullanıcı bu maddeyi anlamadığını belirtti; **`TeamSelector`'a dokunulmadı**, A tuşu
olduğu gibi duruyor. Uygulanan tek şey skorbord tarafındaki koruma:

```csharp
// ScoreboardUI.ReadButton()
if (local == null || local.Team.Value == 0) { _want = false; ... return; }
```

Yani takımsızken skorbord B'yi **okumaz** → `TeamSelector`'ın A/B'siyle çakışmaz.

**Sonraya kalan asıl soru:** `TeamSelector` nedir ve neden A/B kullanıyor?
Normal akışta **hiç açılmaz** — takım giriş ekranında (`PlayerEntryUI`) seçiliyor. O panel
yalnızca bir şey ters gittiğinde (profil okunamadı, oyuncu bir şekilde takımsız spawn oldu)
açılan bir **hata yolu**. Takımsız oyuncu kabul edilemez çünkü doğum bölgesi bulamaz
(`PlayerHealth.TickSpawn` team==0'da bekler). Bugün bunu A/B ile elle seçtiriyor.

Öneri hâlâ geçerli: paneli kaldırıp takımsız oyuncuyu az kişili takıma otomatik atamak
(`JoinTeamServerRpc` zaten var). O zaman sağ A tamamen boşa çıkar. Ayrı bir iş olarak durur.

Sol X (oda tarama) ve sol Y (kalibrasyon) dokunulmuyor.

---

## 3. Tasarım — mevcut temadan devam

Tüm renkler `UITheme` / `PlayerIdentity`'den; **ham renk yazılmaz** (palet kopyalama
dersi). Tüm yazılar `UITheme.MakeText` (Quest font tuzağı). Görsel dil eşlemesi:

| Görseldeki öğe | Bizdeki karşılığı |
|---|---|
| Mavi/kırmızı takım başlık barları | `UITheme.MakeRounded`, zemin `TeamBlueEdge` / `TeamRedEdge` (alfa ~0.9), yazı `TextPrimary` |
| Takım toplam skoru (başlıkta) | `TextPrimary` 0.040 m — MatchBar'daki gibi renk chip'i (`TeamAColor`/`TeamBColor`) yanında |
| Ortadaki süre altıgeni | Yuvarlak köşeli küçük kart: `SurfaceFill` + `SurfaceEdge` çerçeve, "SÜRE" `TextMuted`, saat `TextPrimary` 0.048 |
| Satır zeminleri | `SurfaceFill` alfa ~0.5, kendi satırım `HoverCol` (giriş ekranının vurgu rengi — aynı dil) |
| Panel zemini | `PanelBg` alfa **~0.72** + `RoundedRectOutline` çerçeve `PanelEdge` — bkz. §3.1 |
| Ölü oyuncu | satır içeriği alfa ~0.45 + "ÖLÜ" etiketi `TeamRedText` (ikon/emoji YOK — font riski; Türkçe glifler cihazda kanıtlı) |
| K / D kolonları | "K" / "Ö" — kol saatiyle (`WatchScreenUI` "K x Ö y") aynı terim |

### 3.1 Yarı saydamlık — sorun çıktı, KÖKÜNDEN çözüldü (2026-08-04)

> **Sonuç: panel yarı saydam KALDI (alfa 0.72).** Aşağıdaki kısıt bir süre geçerliydi,
> sonra kök sebep bulunup kaldırıldı. Kayıt, passthrough bir gün oyun içinde tekrar
> açılırsa diye duruyor.

İlk cihaz testinde **panelin içinden gerçek oda görünüyordu** — çizim doğru, ama panel bir
"pencere" gibi davranıyordu.

Sebep: bu oyunda passthrough uygulamanın **altına** kompozit ediliyor
(`Meta Quest: Camera (Passthrough)` OpenXR özelliği `m_enabled: 1`, sahnedeki
`ARCameraManager` `m_Enabled: 1`, kamera arka plan alfası `0`). Kare tamponunun **alfası**
"burada sanal içerik var mı" demek. HUD malzemesi `GUI/Text Shader` ve harmanı
`Blend SrcAlpha OneMinusSrcAlpha` — **alfa kanalını da harmanlıyor**:

```
dstA = srcA² + (1 − srcA)·dstA      →   0.72 ile:  1.00 → 0.80
```

Yani opak bir zeminin **üstüne** çizilen yarı saydam bir yüzey bile alfayı düşürür. Kural
tek bir yüzeye değil **hepsine** uygulanır: zemin, çerçeve, satır zeminleri, takım şeritleri,
vurgular, animasyon fade'i.

**İlk çözüm (geri alındı):** saydamlığı alfayla değil renkle taklit etmek. İşe yarıyordu ama
sis, patlama ve tüm WarFX efektleri de aynı sızıntıyı taşıdığı için bitmeyen bir iş olurdu —
üstelik hepsi üçüncü parti shader.

**KÖK ÇÖZÜM (uygulanan):** passthrough'un oyuncu modunda **hiç açılmaması**.
Sızıntı aslında yeni bir regresyondu — `32ae691` sahneye `ARCameraManager`'ı açık koydu,
`919e160` Android OpenXR passthrough özelliğini açtı, ve oyuncu modunda kimse kapatmıyordu.
`ConstructorPassthrough.DisableAtStartup()` artık başlangıçta kapatıyor; yalnızca inşa modu
açıyor. Passthrough kapalıyken blend modu **opak** olur ve alfa kanalının hiçbir önemi kalmaz.

Bunun üzerine panel, kill paneli ve ölüm ekranı **yarı saydam hâllerine geri alındı**.
`UITheme.PreserveDestinationAlpha` ise duruyor: görsel bedeli sıfır, ikinci savunma hattı.

Aynı ders `PlayerEntryPanel.cs:76`'da zaten yazılıydı — o panel bugün de opak, ama sebebi
passthrough değil okunurluk (arkadaki parlak yüzeyler yazıyı soldurmasın).

### Ölçüler (mesafe 1.3 m, sönümlü kafa takibi — killfeed deseni, `followSpeed 9`)

| Öğe | Değer | Açı |
|---|---|---|
| Panel | 0.84 × 0.56 m, merkez göz hizasında | ±17.9° × ±12.2° — lens güvenli (<30°) |
| Süre kartı | 0.20 × 0.11, panel üst-orta | saat 0.048 → 2.1° |
| Takım başlık şeridi | 0.40 × 0.055, toplam skor 0.040 | 1.8° |
| Oyuncu satırı | yükseklik 0.036, yazı 0.024 | 1.06° (≥1° eşik) |
| Sütun kapasitesi | takım başına 8 satır + taşarsa "+N oyuncu" özeti | sessiz kırpma yok |
| Alt şerit | 0.46 × 0.075, panel altı 0.03 boşluk; isim 0.034, K/Ö 0.030 | 1.5 / 1.3° |
| Kuyruk | zemin **3052**, yazı **3053** | killfeed'in (3050/3051) üstünde — üst üste binerse skorbord kazanır; ölüm perdesi 3000'de kalır, **ölüyken de okunur** (en çok o an bakılır) |

### Sıralama ve tazeleme
- Satırlar `Kills` çoktan aza, eşitse `Deaths` azdan çoğa, eşitse isim.
- Açıkken 2 Hz içerik tazeleme; oyuncu girip çıkınca satırlar yeniden bağlanır
  (satır havuzu — killfeed deseni). **Kapalıyken hiçbir şey çalışmaz** (yalnızca B okunur).
- Veri: `PlayerIdentity.All` (YENİ tek satırlık accessor — `_all` şu an private),
  `Kills/Deaths/Team/NetName` zaten replike; ölü durumu `GetComponent<PlayerHealth>().Dead.Value`
  (replike). **Yeni ağ katmanı yine YOK.**
- Süre: `ServerTime.Time` → `mm:ss` (MatchBar'dan taşınır) + `SetTime(string)` kancası korunur.

### Animasyon (kullanıcı isteği: çizgiden aşağı-yukarı, pürüzsüz)
- **Açılış:** zemin + çerçeve `scaleY 0.03 → 1` (0.22 sn, smoothstep); scaleX hep 1 —
  "çizgi → panel" hissi. Yazılar/satırlar animasyonun **son %40'ında alfa ile** belirir —
  yazıları dikeyde ezip açmak çirkin durur, fade pürüzsüz.
- **Kapanış:** tersi, 0.14 sn.
- `unscaledTime`; her kare mesh üretimi yok (yalnızca transform ölçeği + malzeme alfası).
- Alt şerit panelle birlikte aynı ölçek/alfa eğrisinde.

### Tuş davranışı
- **Öneri: BASILI TUTUNCA açık** (bırakınca kapanır) — referans görseldeki FPS davranışı;
  VR'da yanlışlıkla açık unutulan panel nişanı kapatır. `holdToShow` alanı serialize edilir,
  cihaz testinde toggle denemek tek tık olur.
- Okuma: `XRButtons.Button(RightHand, secondaryButton)` (anlık durum — hold için doğru).
- Kapılar: `XRButtons.GameplayInputSuppressed` iken okunmaz (inşa modu güvencesi).

---

## 4. Dosyalar

| Dosya | Değişiklik |
|---|---|
| `Scripts/UI/ScoreboardUI.cs` | **YENİ** — görünüm + akış tek dosyada (killfeed deseni) |
| `Scripts/Player/PlayerHUD.cs` | `_matchBar` → `_scoreboard` (3 satır) |
| `Scripts/Player/PlayerIdentity.cs` | `+public static IReadOnlyList<PlayerIdentity> All` (1 satır) |
| `Scripts/Networking/LanBootstrap.cs` | B kalkar; otomatik yeniden bağlanma; panel metinleri |
| ~~`Scripts/Player/TeamSelector.cs`~~ | **ERTELENDİ** — bkz. §2.3, dokunulmadı |
| `Scripts/UI/MatchBarUI.cs` | **SİLİNİR** (+`.meta`) |
| `TASKBAR_PLANI.md` | **SİLİNİR** (belge eskidi) |

Sahne / prefab / `UIMesh` / `KillFeedUI`: **dokunulmuyor.**

---

## 5. Doğrulama

**Editor:** 0 hata; B ile aç/kapa; animasyon süreleri; skorlar (`Kills.Value = 7` → tabloda);
ölüm senaryosu (`PlayerHUD`'ın H test tuşu ile öl → kendi satırında ÖLÜ + soluk); kendi satır
vurgusu + alt şerit; sıralama; 9+ oyuncu simülasyonunda "+N" satırı; kuyruklar ≥3052;
fontsuz TextMesh 0; `SetTime` devralması; bağlantı düşürünce otomatik yeniden katılma.

**Cihaz (Quest 3):** panel görünürlüğü; B basılı tutma hissi (toggle mı istenir?);
animasyon akıcılığı; 0.72 zemin opaklığı; ±17.9° kenar netliği; ölüyken okunurluk.

---

## 6. Sıra

1. `PlayerIdentity.All` accessor
2. `ScoreboardUI` iskelet: zemin + çerçeve + süre kartı + animasyon (B ile aç/kapa)
3. Takım başlıkları + satır havuzu + veri bağlama + ölü/kendi vurguları
4. Alt kişisel şerit
5. Kaldırmalar: `MatchBarUI` sil, `LanBootstrap` B→otomatik yeniden bağlanma, `TeamSelector` otomatik atama, `PlayerHUD` geçişi
6. Editor doğrulaması (§5)
7. Commit (dal: `ozellik/skorbord`) → cihaz testi → merge kararı
