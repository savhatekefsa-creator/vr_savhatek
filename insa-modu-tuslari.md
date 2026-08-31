# İnşa Modu — Tuş ve Kumanda Rehberi (Gözlük + PC)

Oyun içi harita editörünün (**Constructor**) iki taraftaki **bütün** girdileri.
Doğrudan koddan çıkarıldı (`ConstructorPlacer`, `ConstructorPaletteUI`,
`FreeEditController`, `FreeEditGizmo`, `ServerView`) — bir tuşun burada yazmayan
bir işi yok.

Akış şöyle kurgulandı: **gözlükteki kişi ızgarayla kaba yerleşimi yapar**, PC'deki
kişi onun yönlendirmesiyle propu serbest katmana çevirip son santimetreleri çeker.
Her değişiklik anında iki tarafta da görünür ve haritaya kaydedilir.

---

## 0. Giriş şartları ve kayıt

- İnşa modu **yalnızca YARATICI modda** açılır. Oyuncu modunda tuşlar ölüdür —
  maç ortasında kazara stick tıklamak hiçbir şey yapmaz.
- **Gözlükte ilk açılışta kalibrasyon kapısı** çıkar: sağ kumandayı **A noktasına**
  koy, **tetiğe** bas; **B noktasına** koy, tekrar bas. Kalibrasyon biter bitmez
  inşa modu **kendiliğinden** açılır, tekrar tuşa basman gerekmez.
  (Kulaklık takılı değilse ya da PC sunucusundaysan bu kapı atlanır.)
- **Kayıt otomatiktir:** her değişiklikten ~3 saniye sonra sunucuda diske yazılır;
  moddan çıkarken de bir kez yazılır. PC'de `K` ile elle de kaydedebilirsin.
  **Yaratıcı akışta** çıkışta "Kaydet?" sorulur (isimlendir / değişiklikleri at) —
  o akışta otomatik kayıt askıya alınır ki "at" seçeneği anlamlı kalsın.
- Gözlükte inşa modundayken **hareket fiziksel yürümedir**: stick'lerin ikisi de
  editöre ayrılmıştır, kaygan yürüme ve snap-turn kapalıdır.

---

## 1. GÖZLÜK (Quest kumandaları)

### Sağ el

| Girdi | İş |
|---|---|
| **Tetik** | Prop koy — hayaletin durduğu hücreye. Palet açıkken koymaz |
| **A** | İşaret edilen propu sil (ızgara propu hücresinden, serbest prop ışının çarptığı yerden bulunur) |
| **B** | Geri al — koyma, silme ve taşıma sırayla geri alınır. Palet açıkken çalışmaz |
| **Grip (basılı tut)** | Palet çarkını aç (bkz. bölüm 1.1) |
| **Stick sağ/sol** | Döndür — serbest dönen propta **±5°** (basılı tutunca tekrarlar), diğerlerinde **±90°** |
| **Stick yukarı/aşağı** | Normalde **prop seç** (önceki/sonraki); **yükseklik modunda kat** ↑ / ↓ |
| **Stick TIK** | Yükseklik modunu aç/kapat — panelde `[YUKSEKLIK MODU]` yazar |

> Yatay eksen **her zaman** döndürür; yükseklik modu yalnızca dikey eksenin
> anlamını değiştirir (prop seçimi ↔ kat).

### Sol el

| Girdi | İş |
|---|---|
| **Grip** | Passthrough aç/kapat — gerçek oda ↔ sanal dünya |
| **Stick TIK** | İnşa modunu aç / kapat (yalnızca Yaratıcı modda) |
| **Stick yukarı/aşağı** | **BOY** (yükseklik) yüzdesi ±10 — sınır %25–%250 |
| **Stick sağ/sol** | **EN** (genişlik) — **birer hücre** adımla |
| *Palet açıkken* stick sağ/sol | **Nişan modu** değiştir: SOL = gözden, SAĞ = kumandadan (bkz. 1.2) |
| *Palet açıkken* stick yukarı/aşağı | Aktif nişan modunun **açı düzeltmesi ±2°** (yukarı = ışın kalksın = daha uzağa) |

> Boy ve en ayrı eksenlerde bilerek: bir bariyeri yalnızca yükseltmek ya da
> yalnızca genişletmek istersin; ikisini birden büyütmek çoğu zaman istenen şey
> değil. **Prop değiştirince ikisi de %100'e döner; açı korunur** (çeyrek-tur
> propa geçilirse en yakın 90°'ye oturtulur).

### 1.1 Palet çarkı (sağ grip)

Silah çarkıyla aynı alışkanlık: **tut → işaret et → bırak.**

1. Sağ **grip'i basılı tut** — çark kafanın tam karşısında açılır.
2. **Sağ stick** ile dilime işaret et (dilim = palet: UZAY / SİPER / DUVAR / DOĞUŞ…).
3. Onay iki yolla: stick **merkeze dönünce** ya da **grip bırakılınca** işlenir.
   Vurgu anında işlenmez — stick gezerken üstünden geçilen paletler seçilmez.

- Çark açıkken stick'in sahibi çarktır: altta duran hayalet dönmez, prop değişmez.
- Paleti olmayan proplar **DİĞER** diliminde toplanır; o dilim boşsa hiç çizilmez.
- Seçili palet haritayla kaydedilir: uzay haritanı tekrar açtığında çark UZAY'da
  başlar. Palet hiçbir şeyi kısıtlamaz, yalnızca çarkı böler.

### 1.2 Nişan kalibrasyonu (palet açıkken sol stick)

Işının elden nasıl çıkacağı kişiye göre değişir — kumandayı herkes farklı tutar.
İki mod var, ikisi de anında denenebilir:

- **GÖZDEN** (stick SOL): ışın baskın gözünden elinin içinden geçip zemine iner —
  bir şeyi işaret parmağınla göstermek gibi. Kumandanın rotasyonunu hiç
  kullanmaz, o yüzden bilek açısı kalibrasyonu gerektirmez; nişan bilekle değil
  kolla alınır.
- **KUMANDADAN** (stick SAĞ): kumandanın yönü + açı düzeltmesi. Ham yön tutamak
  ekseni boyunca bakar, "işaret ettiğini sandığın" yön değildir — fark, yukarı/
  aşağı stick'le ayarlanan ±2°'lik adımlarla kapatılır.

Seçim ve açı **hatırlanır** (cihaza kaydedilir), her oturumda yeniden ayarlamazsın.

### 1.3 Panel mesajları

Sessiz red yok: koyma/silme başarısızsa paneli **sebep yazar** —
"İmleç bir yüzeye bakmıyor", "Hücreler dolu ya da alan dışı", "KAT 2/4 (1.50 m)"
gibi. Tetiğe basıp hiçbir şey olmuyorsa panele bak.

---

## 2. PC — temel inşa (ConstructorPlacer)

| Tuş | İş |
|---|---|
| `B` | İnşa modunu aç / kapat (yalnızca **Yaratıcı** modda) |
| **Sol tık** | Prop koy — `P` ile kilitlenebilir (bkz. bölüm 3) |
| `F` | İşaret edilen propu sil |
| `U` | Geri al |
| `R` | Döndür — serbest dönen propta **+5°**, diğerlerinde **+90°** |
| `Z` / `X` | Önceki / sonraki prop |
| `K` | Haritayı elle kaydet |
| `+` / `-` | **BOY** yüzdesi ±10 (numpad `+` `-` de çalışır) — sınır %25–%250 |
| `[` / `]` | **EN** — birer hücre daralt / genişlet |
| `PageUp` / `PageDown` | **KAT** ↑ / ↓ (PC'de yükseklik moduna gerek yok, tuşlar boş) |
| `H` | Yükseklik modu aç/kapat (VR'daki sağ stick tıkının karşılığı — PC'de şart değil) |
| `T` | Passthrough aç / kapat (VR'daki sol grip'in karşılığı) |
| `C` **basılı tut** | Prop paletini (çarkı) aç |
| `C` + `←` `→` | Palet açıkken dilim seç; `C` bırakılınca seçim işlenir |

Fare ışını: gözlük yokken imleç, ekranı çizen kameranın fare ışınıdır — sunucu
serbest-uçuş kamerasındayken de çalışır.

### Paletlerin yönetimi (Editor)

Çarkın dilimleri `Tools > VR Multiplayer > 31. İnşa Modu Kütüphanesi`
penceresinden oluşturulur, adlandırılır, sıralanır (`▲` `▼`) ve silinir. Palete
eşya eklemek: prop satırındaki palet kutusu; toplu atama için filtre daralt +
**Filtredekileri ata**. Paleti silmek propları silmez, DİĞER'e alır.

> **Not:** *Kategori* (Cover/Wall/Spawn…) alanı çarkı belirlemez ama hâlâ gerçek
> iş yapar: neyin zemin parçası sayılacağı, neyin mermi durduracağı ve takım
> doğuş noktalarının nasıl bulunacağı ona bağlı.

---

## 3. PC — ince ayar editörü (FreeEditController)

Bu panel **yalnızca PC'de** açılır, sol üstte durur. Quest'te hiç doğmaz.

### Seçim ve mod

| Tuş | İş |
|---|---|
| **Sağ tık** | Propu seç. Boşluğa sağ tık = seçimi bırak |
| `Esc` | Seçimi bırak |
| `P` | **Koyma kilidi.** Kilitliyken sol tık prop bırakmaz; hayalet ve nişan ışını da gizlenir |
| `J` | Seçili **ızgara** propunu **serbest** katmana çevir (görünüm birebir korunur) |
| `L` | Seçili **serbest** propu **ızgaraya** geri oturt (açı en yakın 5°'ye, ölçek %100'e döner) |
| `N` | Gizmo takımı: **TAŞI** (oklar + düzlem kareleri) ↔ **DÖNDÜR** (halkalar) |
| `Delete` | Seçili serbest propu sil |

### Klavyeyle milimetrik ayar (yalnızca serbest propta)

| Tuş | İş |
|---|---|
| `←` `→` | X ekseninde ±1 cm |
| `↑` `↓` | Z ekseninde ±1 cm |
| `G` / `V` | Y ekseninde (yukarı / aşağı) ±1 cm |
| `Q` / `E` | Yaw (Y ekseni dönüşü) ∓1° |
| `Alt` + `↑` `↓` | X ekseni dönüşü (öne/arkaya yatırma) ±1° |
| `Alt` + `←` `→` | Z ekseni dönüşü (yana devirme) ±1° |
| `Shift` **basılı** | Adımların **onda biri** — 1 mm ve 0.1° |

Adımlar **oda eksenlerinde**, kamera göreli değil: "X'i 2 cm artır" diyen
gözlükteki kişiyle aynı koordinatı konuşmak için.

### Sayısal panel (en hassas yol)

Panelde Konum / Açı / Ölçek kutuları var — Unity Inspector'daki gibi. Değeri yaz,
**Enter** (veya "Uygula"). Türkçe klavyedeki virgül de kabul edilir: `91,53` =
`91.53`. Bozuk sayı girilirse alan sessizce eski değere döner.

---

## 4. PC — sürükleme kolları (FreeEditGizmo)

Seçili **serbest** propta çıkar; propun kameraya bakan yüzünün önünde durur.

| Girdi | İş |
|---|---|
| **Sol tık + sürükle** (ok) | O eksende taşı |
| **Sol tık + sürükle** (düzlem karesi) | İki eksende birden taşı — yeşil kare = zeminde sürükleme |
| **Sol tık + sürükle** (halka) | O eksen etrafında döndür |
| `Ctrl` **basılı** | Kademeli: **1 cm** / **5°** |
| `N` | Oklar ↔ halkalar arası geçiş |

Renkler Unity ile aynı: **kırmızı = X**, **yeşil = Y**, **mavi = Z**. Düzlem
karesinin rengi **sabit kalan** ekseni gösterir; üzerine gelinen kol sarıya döner.
Kollar hep üstte çizilir, prop gözlükte gerçek zamanlı hareket eder.

---

## 5. PC — serbest-uçuş kamerası (ServerView, sunucu)

PC sunucu olarak çalışıyorsa harita üzerinde gezinmek için:

| Tuş | İş |
|---|---|
| `W` `A` `S` `D` | Hareket |
| `Q` / `E` | Aşağı / yukarı |
| `Shift` | Hızlı hareket |
| **Sağ tık + fare** | Bakış yönü |
| `M` | Kamera modu |

---

## 6. VR ↔ PC karşılık tablosu

| İş | Gözlük | PC |
|---|---|---|
| İnşa modu aç/kapat | **Sol stick TIK** | `B` |
| Prop koy | **Sağ tetik** | Sol tık |
| Prop sil | **Sağ A** | `F` |
| Geri al | **Sağ B** | `U` |
| Döndür | Sağ stick sağ/sol | `R` |
| Prop seç | Sağ stick yukarı/aşağı | `Z` / `X` |
| Palet çarkı | **Sağ grip** (tut) | `C` (tut) + `←` `→` |
| BOY (yükseklik %) | Sol stick yukarı/aşağı | `+` / `-` |
| EN (hücre) | Sol stick sağ/sol | `[` / `]` |
| KAT değiştir | Yükseklik modu + sağ stick dikey | `PageUp` / `PageDown` |
| Yükseklik modu | **Sağ stick TIK** | `H` |
| Passthrough | **Sol grip** | `T` |
| Kaydet | — (otomatik) | `K` (+ otomatik) |
| Nişan kalibrasyonu | Palet açık + sol stick | — (farede gerek yok) |
| Serbest katman / ince ayar | — (yalnız PC) | Bölüm 3–4 |

---

## 7. Bilinen tuş çakışmaları (PC)

Serbest-uçuş kamerası ile ince ayar editörü aynı anda açıkken üç tuş paylaşılıyor:

| Tuş | Çakışma | Etkisi |
|---|---|---|
| `Q` / `E` | Kamera yukarı/aşağı **+** prop yaw ±1° | Prop seçiliyken Q/E ikisini birden oynatır |
| **Sağ tık** | Kamera bakışı **+** prop seçimi | Kısa tıkta seçim, basılı tutup sürükleyince kamera döner |
| `Shift` | Hızlı uçuş **+** ince adım (mm) | Zararsız; ikisi birlikte çalışır |

---

## 8. Kavramlar ve hatırlatmalar

- **Izgara propu ile serbest prop farklı şeyler.** Izgara propu hücrelere oturur,
  doluluk tutar, çarpışmayı ve yürünebilirliği etkiler. Serbest prop tam transform
  taşır (her eksende her derece, milimetrik konum) ama hücre tutmaz — yapı için
  ızgara, ince ayar ve dekor için serbest katman. Gizmo ve klavye ince ayarı
  yalnızca serbest propta çalışır; ızgara propunda önce `J`.
- **Duvar ↔ zemin kendiliğinden seçilir:** ışın taranmış bir duvara yakınsa prop
  duvara, zemine yakınsa zemine oturur (panelde `[DUVAR]` yazar). Geçiş
  histerezisli — duvar dibinde kare kare mod zıplamaz.
- **Kat sınırı tavandır:** taranan tavanın üstüne çıkılamaz; panel `KAT 2/4
  (1.50 m)` diye bildirir.
- **Geri al iki katmanı da kapsar:** koyma, silme ve taşıma jestleri sırayla geri
  alınır. Bir sürükleme jesti tek kayıt bırakır, kare kare değil.
- **Prop değişince ölçek sıfırlanır** (%100/%100), **açı korunur** — bir duvarı
  örerken açıyı proptan propa taşırsın.
- **Ağ:** tüm cihazlar aynı build'den kurulmalı; mesaj formatı sürümler arasında
  değişebiliyor.
