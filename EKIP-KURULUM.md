# Savhatek Simülasyon — Ekip Kurulum Rehberi

## Yeni ekip üyesi kurulumu (bir kez)
1. **Git + Git LFS** kur: https://git-scm.com (LFS, Git for Windows ile birlikte gelir).
2. **Unity Hub** kur ve içinden **Unity 6000.3.18f1** sürümünü yükle (Android Build Support + OpenJDK + SDK/NDK modülleriyle). ⚠️ Farklı Unity sürümü KULLANMA — projeyi bozar.
3. Depoyu klonla:
   ```
   git lfs install
   git clone <DEPO-ADRESI>
   ```
4. Unity sahne birleştirme aracını git'e tanıt (tek satır, kendi makinende bir kez):
   ```
   git config merge.unityyamlmerge.name "Unity SmartMerge"
   git config merge.unityyamlmerge.driver "\"C:/Program Files/Unity/Hub/Editor/6000.3.18f1/Editor/Data/Tools/UnityYAMLMerge.exe\" merge -p %O %A %B %A"
   ```
5. Projeyi Unity Hub'dan aç (ilk açılış Library'yi üretir, 10-20 dk sürebilir — normal).

## ⚠️ Asker avatarı — ARTIK DEPODA, Soldiers-Pack import ETME
Avatarın ihtiyaç duyduğu her şey (US-Soldier.fbx + materyaller + dokular, ~375 MB)
`Assets/_VRMultiplayer/Models/Soldier/` altında **depoya alındı**. Klonlayan herkes
karakterleri eksiksiz görür — ek paket gerekmez.

**SAKIN `Soldiers-Pack.unitypackage`'ı import etme:** paketin dosyaları depodakilerle
AYNI GUID'leri taşıyor; import edersen Unity GUID çakışmasında birini yeniden
numaralandırır ve karakter referansları kırılır. (Bu bölümün eski sürümü paketi
import etmeni söylüyordu — o talimat geçersiz.)

Materyaller zaten URP'ye çevrilmiş, dokular 2048'e küçültülmüş halde depoda.
Quest build'i öncesi doku import ayarları için: `Tools > VR Multiplayer >
38. Silah Dokularini Android'e Optimize Et` (rapor için 39).

## ⚠️ Silahlar: FPS Gun Pack 4K — git'e girmiyor (lisans)
Pistol, ücretli **FPS Gun Pack 4K** paketinin "Pistol 2" mesh/materyallerine bağlı.
Paket hem boyut hem lisans nedeniyle depoda YOK. Kendi Asset Store hesabından
`Assets/FPS Gun Pack 4K/` altına import et — GUID'ler aynı olduğu için pistol
kendiliğinden düzelir. Import etmezsen pistol mesh'siz/pembe görünür (oyun çalışır).

## ⚠️ "Bir sürü hata / modeller yok" — %90 Git LFS eksik
Büyük dosyalar (FBX, PNG, ~1 GB) **Git LFS**'te durur. LFS kurulu olmadan çekersen
her biri 130 baytlık "pointer" metin dosyası olarak gelir: Unity yüzlerce import
hatası basar, RooftopArena gibi modeller sahnede görünmez. Çözüm (bir kez):

1. Unity'yi KAPAT.
2. `git lfs version` çalışmıyorsa Git LFS kur (https://git-lfs.com).
3. Proje klasöründe: `git lfs install` sonra `git lfs pull` (~1 GB indirir).
4. Unity'yi aç — değişen dosyaları kendiliğinden yeniden import eder.
   Hatalar sürerse: Unity kapalıyken `Library/` klasörünü sil, tekrar aç (10-20 dk).

`git lfs pull` "**This repository is over its data quota**" derse GitHub'ın aylık
LFS bant genişliği kotası bitmiştir — depo sahibine haber ver (kota sıfırlanana
kadar beklenir ya da GitHub'dan ek veri paketi alınır).

## ⚠️ Pull/merge öncesi Unity'yi KAPAT
Unity açıkken `git pull` yaparsan, gelen `.fbx`/`.png` dosyalarını Unity onların
`.meta` dosyaları yerleşmeden görür ve kendisi **yeni GUID üretir** — pull'la gelen
doğru meta'ların üzerine yazar. GUID değişince o asset'e bağlı her prefab variant,
materyal ve sahne referansı kopar.

Başımıza geldi: tek bir merge'de 140 `.meta` dosyasının GUID'i değişti; 22 prefab
"Missing Prefab Variant parent" hatası verdi, SampleScene açılamadı, zemin materyali
dokusunu kaybetti.

Doğru sıra: **Unity'yi kapat → `git pull` → Unity'yi aç.**

Yine de olduysa panik yok, hiçbir şey kaybolmaz. Doğru GUID'ler commit'lerde duruyor;
Unity KAPALIYKEN geri koy:
```
git restore -- "*.meta"
```
Sonra Unity'yi aç, reimport'un bitmesini bekle ve konsolun temiz olduğunu doğrula.

## Günlük çalışma akışı
- Çalışmaya başlamadan önce: **Unity'yi kapat**, `git pull`, sonra Unity'yi aç.
- İş bitince: `git add -A && git commit -m "ne yaptigini yaz" && git push`
- Küçük ve sık commit at; gün sonuna dev tek commit biriktirme.

## Altın kurallar
- **SampleScene'i aynı anda tek kişi düzenler** — sahneyi açmadan ekibe haber ver.
- Dosya taşıma/yeniden adlandırma işlemlerini **Unity içinden** yap (dışarıdan yaparsan .meta bozulur).
- `Library/`, `Temp/` gibi klasörler depoya girmez — bunlar makinede otomatik oluşur.
- Build çıktılarını (apk) depoya ekleme.
- **Unity açıkken `git pull` yapma** — yukarıdaki bölüme bak, .meta GUID'lerini bozar.

## MCP (yapay zekâ asistanı ↔ Unity köprüsü) — isteğe bağlı
Asistanın çalışan Unity Editor'e bağlanmasını sağlar: sahne hiyerarşisini okur,
konsol hatalarını görür, prefab/materyal düzenler, testleri çalıştırır. Projeyi
çalıştırmak için **gerekli değil** — kullanmayacaksan bu bölümü atla.

Unity paketi (`com.coplaydev.unity-mcp`) `Packages/manifest.json`'da zaten kayıtlı,
klonlayınca kendiliğinden gelir. Kalan üç adım kendi makinende:

1. **uv kur** (MCP sunucusunu `uvx` çalıştırır):
   ```
   powershell -ExecutionPolicy ByPass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```
   `%USERPROFILE%\.local\bin\uvx.exe` altına kurulur.

2. **Proje kökünde `.mcp.json` oluştur.** Bu dosya depoda YOK (`.gitignore`'da),
   çünkü aşağıdaki yol makineye özel — `KULLANICI` yerine kendi kullanıcı adını yaz:
   ```json
   {
     "mcpServers": {
       "UnityMCP": {
         "type": "stdio",
         "command": "C:\\Users\\KULLANICI\\.local\\bin\\uvx.exe",
         "args": ["--from", "mcpforunityserver==10.1.0", "mcp-for-unity"]
       }
     }
   }
   ```
   Sürüm numarası Unity paketinin sürümüyle aynı olmalı; `Window > MCP for Unity`
   penceresinde yazar.

3. **Unity'de transport'u seç:** `Window > MCP for Unity` → **Transport: Stdio**.
   HTTP'de bırakırsan istemci Unity'yi bulamaz ("No Unity Editor instances found")
   ve sabit 8080 portu başka bir uygulamayla çakışabilir.

**Doğrulama:** bağlantı kurulduysa `%USERPROFILE%\.unity-mcp\` altında
`unity-mcp-status-<hash>.json` oluşur, içinde `"reason":"ready"` yazar.
