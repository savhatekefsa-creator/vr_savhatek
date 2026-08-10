# H3VR2 OYUN MANTIĞINA ÖZENİLEREK YAPILMASI GEREKEN DEĞİŞİKLİKLER.

Plan
Faz 0 — Görünürlük (yarım gün, hiçbir şeyi bozmaz)
Bu bitmeden hiçbir sayı ayarlama. Şu an kör ayar yapıyorsun ve 6 aydır dönen döngünün sebebi bu.

0.1 Debug eksen rig'i: h.anchor'a ve silahın grip noktasına 3'er renkli çubuk (X/Y/Z, ~10 cm). Gizmo değil, gerçek GameObject — Quest build'inde görünmeli.
0.2 HUD sayacı: Vector3.Angle(anchor.forward, weapon.forward) + sapmanın hangi eksende olduğu (yaw/pitch/roll ayrı ayrı).
0.3 Bozulma teşhisi — üç testi ayrı ayrı yap:
Silahı bırak, elleri boşta salla → bozulma var mı? (varsa mesh/skinning, weld değil)
WeaponHandWeld'i devre dışı bırak, silahı tut → bozulma geçiyor mu? (geçiyorsa weld-IK çatışması)
Parmak poser'ı kapat → geçiyor mu? (geçiyorsa curl)
Çıktı: "hangi silah, kaç derece, hangi eksen" tablosu + bozulmanın adı. Bu tablo olmadan Faz 1'in kabul kriteri ölçülemez.

Faz 1 — Açıyı kaynağında çöz
1.1 Her silah prefab'ına Grip adlı boş child: forward = namlu ekseni, up = silahın üstü, konum = avuç merkezi. H3VR'nin PoseOverride'ının birebir karşılığı. Sahne görünümünde gözle hizalanır — üç Euler sayısını başlıkta deneme-yanılmayla bulmaktan kat kat kolay.
1.2 Assets/_VRMultiplayer/Scripts/Interaction/HandGrabber.cs:663'i emekliye ayır. Bounds-tabanlı eksen tahmini (±X/±Y/±Z'ye yuvarlıyor) ve FromToRotation (roll'u tanımsız bırakıyor) gidiyor. Grip transform'u varsa onu oku, yoksa uyar ve kimlik döndür — sessizce tahmin etme.
1.3 Kontrolcü grip-pose kalibrasyonu: tek seferlik jest (doğal tutuşla ileri doğrult + tuş), anchor.rotation kaydedilir. Silah başına değil, kontrolcü başına tek quaternion. Notlarındaki kalıcı 22.5° yaw büyük ihtimalle tam olarak buraya ait — 40 silahta ayrı ayrı kovalanacak bir şey değil.
1.4 Profilli yol (HandGrabber.cs:414) da aynı Grip transform'unu okusun. Şu an iki ayrı doğruluk kaynağı var (yakalanmış gripLocalRot vs otomatik eksen) ve birbirini tutmuyor.
Kabul kriteri: kontrolcü referans pozundayken, test edilen her silahta |sapma| < 1°, ve iki kez kapıp bırakınca aynı sonuç (tekrarlanabilirlik).

Faz 2 — El
Faz 1'in kabul kriteri geçmeden başlama. Bugün iki bilinmeyenli denklem çözmeye çalışıyorsun.

2.1 Faz 0.3'ün sonucuna göre karar ver.
2.2 Weld-IK çatışmasıysa: bileği mutlak yazmak yerine IK hedefini silahın grip noktasına koy. Kol zinciri kendi çözer, deri gerilmez, üstelik WeaponHandWeld'in blend rampası da gereksiz kalır.
2.3 Meta elleri kararı ancak burada. Karşılığında ne kaybettiğini (Humanoid parmak sistemi, tek el pipeline'ı) yukarıdaki tabloya bakarak tart.
Faz 3 — Destek eli / iki el
duzeltme/silah-destek-eli-ik dalındaki iş. H3VR yaklaşımı (silahın yönünü iki el arasındaki vektöre kilitlemek) burada değerlendirilir — ama tek elde 1°'nin altına inmeden iki elli çözüme geçmenin anlamı yok.



## 1. Şu ana kadar yapılanlar: 

    1. Silahlar sağ elle tutulduğunda SAĞ QUEST KOLU düz tutulduğunda silah her zaman düz ve QUESTİN baktığı yöne doğru bakıyor.

    2. QUEST KOLU ile oyundaki EL MESH'i aynı noktada duruyorlar. (Daha öncesinde oyundaki EL MESH'i QUEST KOLUNDAN solda ve geride kalıyordu. "Düzeltildi")

## 2. Geriye kalanlar:

    1. EL MESH'i bozulmaları genellikle MESHin kendisinden kaynaklı kullanılan assets packi iyi değil. Bunun yerine iyi riglere sahip iyi bir mesh el kullanılabilir. Texture atarak aynı sonuca varabiliriz.

    2. Destek eli sol el için sağ elde yapıldığı gibi bir ayarlama yapılmalı ve Silah QUEST ile aynı yerde bulunmalı.

    3. Destek eli de tamamlandığında artık silahı tutarken yamulma vs. olmuyorsa, EL MESH'i için düzenlemelere geçilebilir. Elleri tek tek silaha oturtma işlemi yine UNİTY üzerinden tek tek ayarlanarak yapılmalı.

