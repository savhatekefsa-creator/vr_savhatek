# PC'de Inşa Modu — Tuş Rehberi

Bilgisayardan harita düzenlerken kullanılan **bütün** tuşlar. Gözlük (Quest) tarafı ayrıdır;
buradaki hiçbir tuş VR kumandasını etkilemez.

Akış şöyle kurgulandı: **gözlükteki kişi ızgarayla kaba yerleşimi yapar**, PC'deki kişi onun
yönlendirmesiyle propu serbest katmana çevirip (`J`) son santimetreleri çeker. Her değişiklik
anında iki tarafta da görünür ve haritaya kaydedilir.

---

## 1. Temel inşa (ConstructorPlacer)

| Tuş | İş |
|---|---|
| `B` | İnşa modunu aç / kapat (yalnızca **Yaratıcı** modda çalışır) |
| **Sol tık** | Prop koy — `P` ile kilitlenebilir (bkz. bölüm 2) |
| `F` | İşaret edilen propu sil |
| `U` | Geri al |
| `R` | Döndür — serbest dönen propta **+5°**, diğerlerinde **+90°** |
| `Z` / `X` | Önceki / sonraki prop |
| `K` | Haritayı kaydet |
| `+` / `-` | Boy (yükseklik) yüzdesini artır / azalt — numpad `+` `-` de çalışır |
| `[` / `]` | En (genişlik) — **birer hücre** adımla |
| `PageUp` / `PageDown` | Kat (yükseklik adımı) ↑ / ↓ |
| `T` | Passthrough aç / kapat — gerçek oda ↔ sanal dünya (VR'daki **sol grip**'in PC karşılığı) |
| `H` | Yükseklik modu (VR'daki sağ stick tıkının PC karşılığı) |
| `C` **basılı tut** | Prop paletini aç |
| `C` + `←` `→` | Palet açıkken **palet seç** (UZAY / SİPER / DUVAR / DOĞUŞ …) |

### Paletler

Çarkın her dilimi bir **palet**. Paletler sabit değil — `Tools > VR Multiplayer >
31. İnşa Modu Kütüphanesi` penceresinden oluşturulur, adlandırılır, sıralanır ve silinir.
Çarktaki dilim sırası, o penceredeki liste sırasıdır (`▲` `▼` ile değiştir).

Palete eşya eklemek: prop satırındaki palet kutusundan seç. Çok sayıda propu tek hamlede
taşımak için arama/filtreyi daralt, sonra paletin yanındaki **Filtredekileri ata**'ya bas.

Paleti olmayan proplar kaybolmaz — çarkta **DİĞER** diliminde toplanırlar. O dilim boşsa hiç
çizilmez, yani kütüphaneye yeni giren bir propu palete atamayı unutsan bile onu bulabilirsin.

Bir paleti silmek propları silmez, onları DİĞER'e alır.

Seçili palet haritayla kaydedilir: uzay haritanı tekrar açtığında çark UZAY'da başlar. Palet
hiçbir şeyi **kısıtlamaz**, yalnızca çarkı böler — proplar kimlikle çözüldüğü için paletleri
karıştırarak kurduğun harita olduğu gibi korunur.

> **Not:** *Kategori* (Cover/Wall/Spawn…) alanı duruyor ama artık çarkı belirlemiyor. O alan
> hâlâ gerçek iş yapıyor: neyin zemin parçası sayılacağı, neyin mermi durduracağı ve takım
> doğuş noktalarının nasıl bulunacağı ona bağlı.

---

## 2. İnce ayar editörü (FreeEditController)

Bu panel **yalnızca PC'de** açılır, sol üstte durur. Quest'te hiç doğmaz.

### Seçim ve mod

| Tuş | İş |
|---|---|
| **Sağ tık** | Propu seç. Boşluğa sağ tık = seçimi bırak |
| `Esc` | Seçimi bırak |
| `P` | **Koyma kilidi.** Kilitliyken sol tık prop bırakmaz, ayrıca **hayalet ve nişan ışını gizlenir** |
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
| `Shift` **basılı** | Yukarıdaki adımların **onda biri** — 1 mm ve 0.1° |

Adımlar **oda eksenlerinde**, kamera göreli değil: "X'i 2 cm artır" diyen gözlükteki kişiyle
aynı koordinatı konuşmak için.

### Sayısal panel (en hassas yol)

Panelde Konum / Açı / Ölçek kutuları var — Unity Inspector'daki gibi. Değeri yaz, **Enter**
(veya "Uygula" düğmesi). Türkçe klavyedeki virgül de kabul edilir: `91,53` = `91.53`.
Bozuk bir sayı girilirse alan sessizce eski değere döner.

---

## 3. Sürükleme kolları — gizmo (FreeEditGizmo)

Seçili **serbest** propta çıkar; propun kameraya bakan yüzünün önünde durur, gövdenin içinde
kaybolmaz.

| Giriş | İş |
|---|---|
| **Sol tık + sürükle** (ok) | O eksende taşı |
| **Sol tık + sürükle** (düzlem karesi) | İki eksende birden taşı — yeşil kare = zeminde sürükleme |
| **Sol tık + sürükle** (halka) | O eksen etrafında döndür |
| `Ctrl` **basılı** | Kademeli: **1 cm** / **5°** |
| `N` | Oklar ↔ halkalar arası geçiş |

Renkler Unity ile aynı: **kırmızı = X**, **yeşil = Y**, **mavi = Z**. Düzlem karesinin rengi
**sabit kalan** ekseni gösterir. Üzerine gelinen kol sarıya döner.

Sürüklerken kollar hep üstte çizilir (duvarın arkasında kalsa da görünür) ve prop gözlükte
gerçek zamanlı hareket eder.

---

## 4. Serbest-uçuş kamerası (ServerView) — PC sunucu

PC sunucu olarak çalışıyorsa harita üzerinde gezinmek için:

| Tuş | İş |
|---|---|
| `W` `A` `S` `D` | Hareket |
| `Q` / `E` | Aşağı / yukarı |
| `Shift` | Hızlı hareket |
| **Sağ tık + fare** | Bakış yönü |
| `M` | Kamera modu |

---

## 5. Bilinen tuş çakışmaları

Serbest-uçuş kamerası ile ince ayar editörü aynı anda açık olduğunda üç tuş paylaşılıyor:

| Tuş | Çakışma | Etkisi |
|---|---|---|
| `Q` / `E` | Kamera yukarı/aşağı **+** prop yaw ±1° | Bir prop seçiliyken Q/E hem kamerayı hem propu oynatır |
| **Sağ tık** | Kamera bakışı **+** prop seçimi | Kısa tıkta seçim olur, basılı tutup sürüklersen kamera döner |
| `Shift` | Hızlı uçuş **+** ince adım (mm) | Zararsız; ikisi birlikte çalışır |

Pratikte rahatsız ediyorsa söylemen yeterli — editörün tuşları ya da kameranınkiler
değiştirilebilir.

---

## 6. Hatırlatmalar

- **Izgara propu ile serbest prop farklı şeyler.** Izgara propu hücrelere oturur, doluluk
  tutar, çarpışmayı ve yürünebilirliği etkiler. Serbest prop tam transform taşır (her eksende
  her derece, milimetrik konum) ama **hücre tutmaz** — yapı için ızgara, ince ayar ve dekor
  için serbest katman.
- Gizmo ve klavye ince ayarı **yalnızca serbest propta** çalışır. Izgara propunda önce `J`.
- Geri al (`U` / VR'da `B`) her iki katmanı da kapsar: koyma, silme ve **taşıma** jestleri
  sırayla geri alınır. Bir sürükleme jesti tek kayıt bırakır, kare kare değil.
- Harita otomatik kaydedilir (sunucuda). `K` ile elle de kaydedebilirsin.
- Ağ mesajları bu sürümde değişti: **tüm cihazlar aynı build'den kurulmalı**.
