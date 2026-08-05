# Sahne haritasının havuza alınması — kalan iş

**Durum:** örtüşme düzeltildi (`SceneMapVisibility` + `MapBuilder`), yerleşik katalog girişi **yapılmadı**.

## Sorun

Sahnedeki dekore harita ile havuz haritaları iki ayrı dünya:

| Kök | Nereden gelir | Kim temizler |
|---|---|---|
| `RoomMap` | Oda taraması + Editor dekorasyonu (`RoomMapDecorator`, menü 18) | Hiç kimse |
| `ConstructorMap` | `MapBuilder.DefaultRootName`, havuz haritaları | `MapBuilder.Clear` |

İki sonuç doğdu:

1. **Havuz haritası sahne haritasının üstüne kuruluyordu.** `MapBuilder.Clear` yalnızca kendi kökünü temizliyor, çalışma anında `RoomMap`'e dokunan başka kod yoktu. → **DÜZELTİLDİ**: `SceneMapVisibility.SetVisible` ile kök doluysa sahne haritası kapanır, boşaldıysa açılır. Yalnızca `ConstructorMap` kökü için — Editor'ün bebek evi önizlemesi kendi kökünü kullanıyor.
2. **Sahne haritası katalogda yok.** `MapCatalog.RefreshFromDisk` yalnızca `MapLayout.List()` dosyalarını okur. Sahne haritası bir dosya değil → listede çıkmaz, havuza eklenemez. → **AÇIK**

## Yapılacak: yerleşik katalog girişi

Karar (2026-08-05): sahne haritası, katalogda **her zaman duran sentetik bir kayıt** olacak. Seçilince `ConstructorMap` boşaltılır ve `RoomMap` açılır; prop kurulmaz. Dönüştürme yok, dekorasyon aynen korunur.

### 1. `MapCatalog`

- Ayrılmış kimlik sabiti, ör. `SceneMapKey = "sahne-haritasi"`, görünen ad "Sahne Haritası".
- `RefreshFromDisk`: listenin başına sentetik `Entry` ekle (`propCount = 0`, `poolEligible = true`).
  `poolEligible` neden koşulsuz: bu harita zaten aylardır oynanıyor, doğum bölgeleri sahnede duruyor. İstenirse sahnedeki `TeamSpawnZone` örnekleri sayılarak gerçek kontrol yapılabilir.
- **Havuz üyeliği nerede saklanacak:** normal haritalarda üyelik dosyanın içinde (`MapLayout.inPool`). Sentetik kaydın dosyası yok → otoritede `PlayerPrefs` (ör. `MapCatalog.SceneMapInPool`), **varsayılan `true`**. Varsayılanın true olması şart: aksi halde güncelleme sonrası havuz boş kalır ve oyuncu modu hiç açılmaz (`PoolIsEmpty` kapısı).
- `AddToPool` / `RemoveFromPool`: `SceneMapKey` için `MapLayout.Load` yoluna girmeden PlayerPrefs'i yaz, sonra `RefreshAndBroadcast`.
- `Delete` / `Rename`: `SceneMapKey` için reddet, sebebini `hata`ya yaz.
- `NameAvailable`: `SceneMapKey`'i rezerve et — kullanıcı aynı adda harita oluşturamasın.
- Ağ tarafı çalışıyor: `SnapshotJson`/`ApplySnapshot` `Entry` listesini olduğu gibi taşıyor, sentetik kayıt kendiliğinden istemciye gider.

### 2. Seçim yolu

- `ConstructorSync.PickMatchMapServerRpc` → `Session.OpenExisting(mapName)` çağırıyor. `SceneMapKey` için `OpenExisting` başarısız olur (dosya yok).
  Yapılacak: bu anahtarı özel geçir — `MapBuilder.Clear(ConstructorMap)` çağır (bu zaten `SceneMapVisibility` üzerinden `RoomMap`'i açar) ve başarı dön.
- `ConstructorSync.OpenMapServerRpc` (yaratıcı modda düzenlemek için açma): `SceneMapKey` için **reddet** — "Sahne haritası düzenlenemez, kopyala." Sahne haritası Editor'de yazarlanmış, prop tabanlı değil.

### 3. Arayüz

- `MapListPanel` / `MapActionsPanel`: sentetik kayıtta "Sil" ve "Yeniden adlandır" düğmeleri gizlensin ya da pasif olsun.

## Doğrulama

Cihazda: yaratıcı modda yeni harita oluştur → sahne haritası kaybolmalı, yalnız yeni harita görünmeli. Haritayı kapat → sahne haritası geri gelmeli. Oyuncu modunda havuzda "Sahne Haritası" görünmeli ve seçilince dekore harita gelmeli.
