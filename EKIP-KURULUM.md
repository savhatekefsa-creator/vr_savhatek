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

## Günlük çalışma akışı
- Çalışmaya başlamadan önce: `git pull`
- İş bitince: `git add -A && git commit -m "ne yaptigini yaz" && git push`
- Küçük ve sık commit at; gün sonuna dev tek commit biriktirme.

## Altın kurallar
- **SampleScene'i aynı anda tek kişi düzenler** — sahneyi açmadan ekibe haber ver.
- Dosya taşıma/yeniden adlandırma işlemlerini **Unity içinden** yap (dışarıdan yaparsan .meta bozulur).
- `Library/`, `Temp/` gibi klasörler depoya girmez — bunlar makinede otomatik oluşur.
- Build çıktılarını (apk) depoya ekleme.
