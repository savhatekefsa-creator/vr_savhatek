# MatchManager — Tasarım Planı

> Şemanın ŞERİT 4'ü: maç süresi, geri sayım, bitiş koşulu, kazanan/beraberlik, ana menüye dönüş.
> İki kanca bunu bekliyor ve ikisi de test edilmiş durumda.
> Ölçüler ve API adları 2026-08-04'te `main` (`581835e`) üzerinde doğrulandı.
> **Onay bekliyor — kod yazılmadı.**

---

## 1. Hazır olan (yeniden yazma)

| İhtiyaç | Zaten var | Nerede |
|---|---|---|
| Senkron sunucu saati | `NetworkManager.ServerTime.Time` | `NetworkWeapon.cs:686` |
| Süre kancası | `ScoreboardUI.SetTime(string)` — çağrıldığı an panelin kendi sayacı susar | `ScoreboardUI.cs:129` |
| Ana menüye dönüş | `AppMode.ReturnToModeSelect()` — `ModeSelectUI` paneli geri açıyor | `AppMode.cs:61` |
| Kişisel skor | `PlayerIdentity.Kills / Deaths` (`NetworkVariable<ushort>`, sunucu yazar) | `PlayerIdentity.cs:46` |
| Oyuncu listesi | `PlayerIdentity.All` | `PlayerIdentity.cs:285` |
| Ölüm/doğum akışı | `PlayerHealth.Dead`, `SpawnProgress`, `TickSpawn` | `PlayerHealth.cs:54` |
| Ağ prefabını elle listeye eklemeden kaydetme | `WeaponPrefabRegistrar` deseni (`Resources` + `AddNetworkPrefab`) | `WeaponPrefabRegistrar.cs:33` |
| Config deseni | `CombatConfig.Instance` → `Resources.Load` + yoksa varsayılan üret | `CombatConfig.cs:63` |
| Panel paleti / mesh / font | `UITheme`, `UIMesh` | — |

**Yeni UI altyapısı gerekmiyor, yeni saat senkronu gerekmiyor.**

---

## 2. Asıl mimari karar: maç durumu nerede yaşayacak?

Proje bugüne kadar bunu **bilerek** ertelemiş. `PlayerIdentity.cs:44`:

> *"Ayrı bir MatchManager/NetworkObject KURULMADI: skorun oyuncunun kimliğinde yaşaması sahne ve
> prefab değişikliğini tamamen gereksiz kılıyor."*

Bu karar skor için doğruydu. Ama maç durumu **oyuncuya ait değil**: faz, bitiş zamanı, kazanan
ve kalıcı takım skoru oyuncu ayrılınca kaybolmamalı. Yani karar bozulmak zorunda — ama bedeli
mümkün olduğunca küçük tutularak.

### Seçenekler

| | Nasıl | Bedel |
|---|---|---|
| **A. Resources'tan spawn edilen NetworkObject** ✅ | `Resources/MatchPrefabs/Match.prefab` (NetworkObject + MatchManager). `WeaponPrefabRegistrar` gibi `AddNetworkPrefab` ile kaydedilir, sunucu açılınca spawn edilir | Küçük bir prefab. **Sahne değişikliği YOK** |
| B. Sahne NetworkObject'i | Sahneye obje koy | `SampleScene.unity` değişir → `UnityYAMLMerge` sessiz kayıp riski, ekip çakışması |
| C. NetworkObject yok, elle RPC | Durumu ClientRpc ile yay | **Geç katılan senkronu elle yazılır.** `NetworkVariable`'ın bedavaya verdiği şeyi hataya açık şekilde tekrar yazmak demek |

**Öneri: A.** Projedeki mevcut desenin (`WeaponPrefabRegistrar`) aynısı; sahneye ve
`DefaultNetworkPrefabs` listesine hiç dokunulmuyor, geç katılan senkronu bedava geliyor.

### Spawn nerede tetiklenir
`LanBootstrap`'e **dokunmadan**: statik bir `MatchBootstrap`, `NetworkManager.OnServerStarted`
olayına abone olur ve Match objesini spawn eder. Hem host hem adanmış sunucu yolunu tek yerden
kapatır.

---

## 3. Durum makinesi

```
        sunucu acildi
              │
              ▼
    ┌────────────────────┐  PC "MACI BASLAT"  ┌──────────────────┐
    │   WARMUP           │ ─────────────────► │   STARTING (5sn) │
    │ isinma: ates VAR   │                    │ 3 . 2 . 1 . BASLA│
    │ hasar YOK          │                    │ hasar YOK        │
    │ DOGUM ACIK         │                    │ DOGUM ACIK       │
    └────────────────────┘                    └────────┬─────────┘
              ▲                                        │
              │ endScreenSeconds sonra                 ▼
              │ (skorlar sifirlanir)         ┌────────────────────┐
              │                              │   PLAYING          │
              │                              │ 3:00 geri sayim    │
              │                              │ hasar VAR          │
              │                              └────────┬───────────┘
              │                                       │ sure doldu
              │                                       ▼
              │                              ┌────────────────────┐
              └──────────────────────────────│   ENDED (12 sn)    │
                                             │ kazanan ilan       │
                                             │ ates VAR, hasar YOK│
                                             │ DOGUM KAPALI       │
                                             └────────────────────┘

        HASAR  yalnizca PLAYING'de gecer.
        DOGUM  yalnizca ENDED'de kapali.   <-- IKISI AYRI KAPI, bkz. 8.8
```

### Replike edilen durum (hepsi `NetworkVariable`, sunucu yazar)

```csharp
NetworkVariable<byte>   Phase;            // 0 Warmup, 1 Playing, 2 Ended
NetworkVariable<double> PhaseEndsAt;      // ServerTime cinsinden; geri sayimin TEK kaynagi
NetworkVariable<ushort> ScoreBlue, ScoreRed;
NetworkVariable<byte>   Winner;           // 0 beraberlik, 1 mavi, 2 kizil
```

**Geri sayım tek bir `double` ile taşınıyor**, her kare tick paketi değil: istemci
`PhaseEndsAt − ServerTime.Time` hesaplar. Bu zaten `NetworkWeapon`'ın reload'da kullandığı desen
(`_reloadDoneAt`) — aynı dil.

---

## 4. Skor sahipliği — bilinen bir hatayı da kapatıyor

Bugün `PlayerIdentity.TeamScore(team)` istemcide `_all` listesini toplayarak hesaplanıyor ve
dosyanın kendi yorumu sınırı yazıyor (`PlayerIdentity.cs:253`):

> *"BILINEN SINIR: oyuncu çıkınca kimliği despawn olur ve skoru toplamdan düşer."*

Maçın kazananı buna bağlanamaz — biri bağlantıyı koparınca takımı puan kaybeder.

**Karar:** kalıcı takım skorunun sahibi `MatchManager` olur. Sunucu her ölümde
(`PlayerHealth`'in zaten çalışan kill yolundan) ilgili sayacı artırır. `PlayerIdentity.Kills`
**kalır** — o kişisel istatistik ve skorbordun satırları onu gösteriyor.

`ScoreboardUI` takım toplamlarını `MatchManager` varsa ondan, yoksa bugünkü hesaplamadan alır —
böylece MatchManager'sız da (tek başına test, eski kayıt) çalışmaya devam eder.

---

## 5. Config — `CombatConfig` deseniyle

`Resources/MatchConfig.asset`, `MatchConfig.Instance` (asset yoksa varsayılanla üretir):

| Alan | Varsayılan | Not |
|---|---|---|
| `matchSeconds` | 180 | şemadaki `CONFIG · Maç süresi: 3:00` |
| `warmupSeconds` | 15 | 0 = beklemeden başla |
| `endScreenSeconds` | 12 | sonuç ekranı süresi |
| `scoreLimit` | 0 | 0 = kapalı; >0 ise erken bitirir |
| `minPlayersToStart` | 2 | §9'daki karar noktası |
| `autoRestart` | true | §9'daki karar noktası |

---

## 6. Maç sonu ekranı — YENİ PANEL YAZMA

Skorbord zaten kazananı ilan etmek için gereken **her şeyi** gösteriyor: iki takım, skorlar,
oyuncu listesi, kişisel şerit. Üstelik açılma animasyonu ve kafa takibi test edilmiş.

**Karar: maç bitince skorbord kendiliğinden açılır**, üstünde bir sonuç başlığı belirir:

```
        ┌───────────────────────────────┐
        │      MAVİ TAKIM KAZANDI       │   ← YENİ: tek satır, takım renginde
        ├───────────────────────────────┤
        │   (mevcut skorbord aynen)     │
        └───────────────────────────────┘
```

`ScoreboardUI`'ya eklenecek yüzey (~2 metot):
```csharp
public void ShowResult(string headline, Color tint);  // baslik + paneli acik KILITLE
public void ClearResult();                            // kilit kalkar, B'ye geri doner
```
Kilitliyken B ile kapatılamaz — sonucu kaçırmasın.

Süre alanı bu fazda `SetTime` ile "MAÇ BİTTİ" ya da geri dönüş sayacı yazar; zaten dışarıdan
beslenebiliyor.

---

## 7. Dokunulan yerler

| Dosya | Değişiklik | Risk |
|---|---|---|
| `Scripts/Match/MatchManager.cs` | **YENİ** — NetworkBehaviour, durum makinesi | yok |
| `Scripts/Match/MatchBootstrap.cs` | **YENİ** — `OnServerStarted` → spawn | yok |
| `Scripts/Match/MatchConfig.cs` | **YENİ** — ScriptableObject | yok |
| `Resources/MatchPrefabs/Match.prefab` | **YENİ** — NetworkObject + MatchManager | yok |
| `Scripts/UI/ScoreboardUI.cs` | `ShowResult/ClearResult` + skoru MatchManager'dan al (~25 satır) | düşük |
| `Scripts/Player/PlayerHealth.cs` | ① kill'de takım skorunu artır ② `Ended`/`Warmup` fazında hasar ve doğum durur (~6 satır) | **orta** — savaş yolunun göbeği |

**Sahne / prefab / `NetworkPlayer` değişikliği: YOK.**

---

## 8. Bu projede yanan tuzaklar (tekrar yanma)

1. **`AddNetworkPrefab` yalnızca `IsListening` false iken güvenli** — `WeaponPrefabRegistrar`
   bunu `AfterSceneLoad`'da yapıyor, Match prefabı da aynı anda kaydedilmeli. Sonra kaydetmek
   `ForceSamePrefabs` hash'ini bozar ve istemciler bağlanamaz.
2. **Statikler oyunlar arası taşınır** (domain reload kapalı): `MatchBootstrap`'in aboneliği
   `ResetStatics` ile temizlenmeli — `AppMode`'da bu bir kez yandı.
3. **Sunucunun avatarı yok.** MatchManager yerel oyuncuya bakmamalı; `PlayerIdentity.Local`
   sunucuda null.
4. **Geç katılan.** `NetworkVariable` ilk senkronu otomatik yapar — ama `PhaseEndsAt` mutlak
   `ServerTime` olduğu için geç katılan doğru geri sayımı görür. Göreli süre saklanırsa görmez.
5. **`ScoreboardUI.SetTime` çağrıldığı an panelin kendi sayacı susar** — geri dönüşü yok.
   `Ended` fazında panel yeniden `--:--`'a düşmemeli, MatchManager beslemeye devam etmeli.
6. **Fontsuz `TextMesh` Quest'te çizilmez** — sonuç başlığı `UITheme.MakeText` ile.
7. **Passthrough kapalı** (`ConstructorPassthrough.DisableAtStartup`) — sonuç başlığı yarı
   saydam olabilir, alfa kısıtı kalktı.

8. **⚠ HASAR KAPISI İLE DOĞUM KAPISI AYRIDIR — bu tuzağa düşüldü ve cihazda yakalandı.**
   Bu oyunda oyuncu **ölü katılır**: `PlayerHealth.OnNetworkSpawn` `Dead = true` yazar,
   *"İLK DOĞUŞ da çember mekanizmasından geçer"*. İlk sürümde `TickSpawn` `DamageAllowed`
   kapısına bağlanmıştı — sonuç: ısınmada kimse oyuna giremiyor, çembere gelen oyuncuya
   "bölgene git" yazıp duruyor, dolayısıyla **silah da alamıyor**. Doğru kural:
   - `DamageAllowed`  → yalnızca `Playing`
   - `RespawnAllowed` → `Ended` **dışında her yerde açık**

9. **Fazın kendisi görünür olmalı.** Maç katmanı eklendiğinde durumu görmenin tek yolu B
   ile skorbordu açmaktı; oyuncu ısınmada ateş edip "kimse ölmüyor, oyun bozuk" sanıyordu.
   `MatchStatusUI` bu yüzden var — ve VR'da **ses** yazıdan güçlü bir kanal.

---

## 9. Kararlar (onaylandı 2026-08-04)

**1. Maç ne zaman başlar? → PC'DEN ELLE**
Otomatik başlatma yok. Sunucuyu çalıştıran PC'de bir **"MAÇI BAŞLAT"** butonu olacak
(`LanBootstrap`'in mevcut `SUNUCU başlat` butonunun yanında, aynı `OnGUI` bloğunda).
Maç yalnızca o basılınca başlar.

`minPlayersToStart` config'te **bilgi amaçlı** kalır: buton kilitlenmez, ama hazır olup
olmadığını yazar ("2 oyuncu · MAVİ 1 / KIZIL 1"). Test için tek kişiyle de başlatılabilsin.

**2. Maç bitince → `endScreenSeconds` sonra `Warmup`'a döner, skorlar sıfırlanır.**
Yeni maçı yine PC başlatır (1. kararla tutarlı). Kimse ana menüye atılmaz.
`AppMode.ReturnToModeSelect()` ölü kalmaz — sunucu kapanınca / bağlantı düşünce çağrılır.

**3. Bitiş koşulu → yalnızca süre.** `scoreLimit` config'te durur ama v1'de 0 (kapalı).

**4. `Ended` fazında → hareket var, hasar yok, ölü olanlar doğmaz.**

**5. EK KURAL — `Warmup` da `Ended` gibi davranır.**
Maç başlamadan da herkes dolaşabilir, silah tutabilir, **ateş edebilir** — ama kimse hasar
almaz. Yani kural faz bazlı değil tek cümle:

> **Hasar YALNIZCA `Playing` fazında geçer.**

Bu ısınma/deneme için doğal bir alan açıyor: oyuncular maç başlamadan silahlarını deniyor,
nişan alıyor, doğum bölgelerini öğreniyor. Ateş etmenin kendisi (ses, geri tepme, mermi,
namlu alevi) tamamen çalışmaya devam eder — yalnızca `ServerApplyDamage` etkisiz kalır.

---

## 10. Doğrulama

**Editor (host):** 0 hata; faz geçişleri (`Warmup → Playing → Ended → Warmup`); geri sayım
`ScoreboardUI`'da `mm:ss` iniyor; süre dolunca kazanan doğru (skor eşitse "BERABERE"); skor
oyuncu **çıkınca düşmüyor** (bugünkü hatanın testi); `Ended` fazında `ServerApplyDamage`
etkisiz; geç katılan doğru kalan süreyi görüyor (bağlanmayı geciktirerek); MatchManager
yokken skorbord eski hesaplamayla çalışmaya devam ediyor.

**Cihaz (Quest 3):** sonuç başlığı okunuyor; skorbord kilitliyken B ile kapanmıyor; yeni maç
başlarken skorlar sıfırlanıyor.

---

## 11. Sıra

1. `MatchConfig` + `MatchManager` iskeleti (faz + `PhaseEndsAt`), spawn ve kayıt
2. Geri sayımı `ScoreboardUI.SetTime`'a bağla
3. Takım skorunun sahipliğini `MatchManager`'a al, `PlayerHealth` kill yolunu bağla
4. Bitiş koşulu + kazanan hesabı + `ShowResult`
5. `Ended` davranışı (§9.4) ve yeni maç / dönüş (§9.2)
6. Editor doğrulaması (§10)
7. **Cihaz testi** → commit (`ozellik/mac-yonetimi`) → merge

---

## 12. Kapsam dışı (bilerek)

Kalıcı skor tablosu (oturumlar arası), maç geçmişi, MVP hesabı, tur/round sistemi, harita
oylaması, takım dengeleme, izleyici modu.
