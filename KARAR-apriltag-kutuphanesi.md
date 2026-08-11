# Karar Notu: İşaret okuma kütüphanesi — ücretli lisans gerekiyor mu?

> **SONUÇ: Hayır. Para harcamamıza gerek yok.**
> Aradığımız işi yapan, MIT lisanslı, Quest için yazılmış hazır çözümler var.
> Hazırlayan: kalibrasyon ekibi · 2026-07-29

---

## 1. Neyi çözmeye çalışıyoruz

Oyuncular sanal dünyayı gerçek odaya hizalamak için elle kalibrasyon yapıyor (iki noktaya
dokunup tetiğe basma). Çalışıyor, ama üç zayıflığı var:

- Hizalama zamanla kayıyor — **ölçtük: saatte ~27 cm**
- Ortak hizayı sürdürmek **internet** ve **Meta'nın sunucuları** gerektiriyor
- Meta'daki referans **~30 günde** siliniyor

**Çözüm:** duvarlara basılı işaretler (AprilTag) asmak. Gözlük kamerayla işareti görüp konumunu
kendi hesaplıyor. İnternet gerekmiyor, süre sınırı yok, **veri tamamen bizde kalıyor**.

Bunun için görüntüde işareti bulacak bir yazılım kütüphanesi lazım. Soru buydu: satın almalı mıyız?

---

## 2. Cevap: hayır — hazır ve ücretsiz çözüm var

Araştırma sonucunda **tam bizim senaryomuz için yazılmış** bir proje bulundu:

### `juchong/AprilTagUnity`

| | |
|---|---|
| **Lisans** | **MIT** — takımca sınırsız kullanım, ürüne konabilir, kimseye ödeme yok |
| Platform | **Meta Quest 2 / 3 / Pro** |
| Kamera erişimi | Meta Passthrough Camera API (Meta'nın kendi ücretsiz paketi) |
| Ek kütüphane | **Gerekmiyor** — işaret okuma kodu paketin içinde |
| İşaret ailesi | Tag36h11 ve TagStandard41h12 |
| Çıktı | **Tam 6 eksenli konum + yön** |
| Unity sürümü | 6000.2.6f2+ → bizde 6000.3.18 ✅ |

Ve tasarım felsefesi zaten bizim kurduğumuz sistemle aynı: işaretler yalnızca referans noktası
kurmak için kullanılıyor, sonrasında gözlüğün kendi takibi devralıyor.

### Yedek seçenek: `jp.keijiro.apriltag`

BSD-2-Clause lisanslı, Android destekli, aynı işi yapan bir başka ücretsiz paket. Birincisi
beklenmedik bir sorun çıkarırsa alternatifimiz var.

---

## 3. Peki ücretli seçenek neydi, neden gerekmiyor

İlk araştırmada **OpenCV for Unity** (~$95, Unity Asset Store) düşünülmüştü.

**Önemli ayrım:**

| | Nedir | Fiyat |
|---|---|---|
| **OpenCV** | Genel amaçlı görüntü işleme kütüphanesi | **Ücretsiz** (Apache 2.0) |
| **OpenCV for Unity** | Aynı kütüphanenin Unity + Android'e bağlanmış hâli | ~$95, **kişi başı** |

Yani ücretli olan şey algoritmalar değil, onları Unity'ye **bağlama emeği**. O emeği kendimiz de
harcayabilirdik ama 2-3 gün uzmanlık işi olurdu.

**Artık ikisi de gereksiz:** bulduğumuz AprilTag paketleri bu bağlama işini zaten yapmış ve
ücretsiz paylaşmış. Üstelik OpenCV'nin tamamına ihtiyacımız yok — bize sadece işaret okuma lazım,
o da bu paketlerde hazır.

> ⚠️ **Tuzak:** GitHub'da `EnoxSoftware/OpenCVForUnity` diye bir depo var ve ücretsiz görünüyor.
> Değil — içinde yalnızca örnek sahneler var, asıl kütüphane yine ücretli pakette. README'si
> bunu açıkça söylüyor.

---

## 4. Bu kararın kazandırdıkları

| | Ücretli yol | Seçtiğimiz yol |
|---|---|---|
| Maliyet | ~$95 × kişi sayısı | **0** |
| Takım kullanımı | Her kişiye ayrı lisans | **Sınırsız** |
| Ürüne koyma | Lisans şartlarına tabi | **Serbest (MIT)** |
| Hazırlık süresi | Birkaç saat | Birkaç saat |
| Bakım | Yayıncıda | Bizde (ama kaynak kod elimizde) |

---

## 5. Sırada ne var

Kütüphane sorunu çözüldü, ama **asıl soru hâlâ cevaplanmadı:**

> Quest'in kamerası bizim mekânımızda, bizim ışığımızda işareti yeterince iyi okuyabiliyor mu?

Bunu **1-2 günlük bir fizibilite testiyle** öğreneceğiz:

1. Bir işaret basılıp duvara asılacak
2. Tek gözlükle test edilecek
3. Ölçülecek: kaç metreden okuyor, hangi açıya kadar, ne kadar titriyor
4. Sonuca göre karar: devam mı, plan değişikliği mi

Test planı hazır: `PLAN-faz0-spike.md`

**Bu aşamada hiçbir maliyet yok** — ne lisans, ne ekipman. Sadece bir kâğıt çıktısı ve zaman.

---

## 6. Olası sorular

**S: Ücretsiz olması kalitesiz olduğu anlamına gelmez mi?**
Hayır. AprilTag, Michigan Üniversitesi'nin robotik araştırmaları için geliştirdiği ve
endüstride yaygın kullanılan bir sistem. Bulduğumuz paketler o kütüphaneyi Unity'ye taşıyor.
MIT/BSD lisansı akademik ve endüstriyel yazılımda standarttır.

**S: İleride para ödememiz gerekir mi?**
Hayır. MIT ve BSD lisansları kalıcı ve ücretsizdir; sonradan ücretlendirilemez. Elimizdeki
sürümü istediğimiz kadar kullanabiliriz.

**S: Destek alamayacak mıyız?**
Ticari destek yok, ama kaynak kod elimizde — gerekirse kendimiz düzeltebiliriz. Ücretli pakette
de destek sınırlıdır.

**S: Bu iş yapılmazsa ne olur?**
Mevcut sistem çalışmaya devam eder, ama üç zayıflık kalır: internet bağımlılığı, Meta'nın
sunucularına bağımlılık, 30 günlük süre sınırı.

**S: Veri güvenliği açısından ne değişir?**
Olumlu yönde. Şu an mekânımızın uzamsal verisi Meta'nın sunucularından geçiyor. İşaret
sistemine geçince **hiçbir veri dışarı çıkmaz**, her şey kendi sunucumuzda kalır.

**S: Riski ne?**
Tek risk **fizibilite**: Quest kamerası bizim koşullarımızda yeterince iyi okuyabilecek mi?
1-2 günlük testle öğreniriz ve test hiçbir maliyet getirmiyor.

---

## 7. Özet

- **Para harcamamıza gerek yok** — MIT lisanslı, Quest için yazılmış hazır çözüm var
- Takımca sınırsız kullanılabilir, ürüne konabilir
- GitHub'daki "OpenCVForUnity" deposu ücretsiz sürüm değil, sadece örnekler
- Sıradaki adım **maliyetsiz fizibilite testi** (1-2 gün)
- Kazanımı: internet bağımsızlığı, süre sınırının kalkması, **verinin bizde kalması**
