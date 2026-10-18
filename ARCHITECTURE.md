# Mimari / Architecture – Travel Planner

Bu belge kodun güncel hâlini anlatır; kod ile çelişirse doğru olan koddur ve bu belge güncellenmelidir. Ayrıntılar: veri modeli [docs/DATA_MODEL.md](docs/DATA_MODEL.md), sunucu [supabase/README.md](supabase/README.md), senkron tasarımı [docs/CLOUD_SYNC_PLAN.md](docs/CLOUD_SYNC_PLAN.md), görsel dil [docs/DESIGN.md](docs/DESIGN.md), yayın [docs/RELEASE.md](docs/RELEASE.md).

## 1. Genel bakış

```
┌──────────────────────────── Mobil uygulama (.NET MAUI, net10.0-android / ios) ───────────────────────────┐
│ Views (XAML)  ──binding──▶  ViewModels (CommunityToolkit.Mvvm)  ──▶  Services (arayüz + uygulama)       │
│                                                                      │                                   │
│                    SQLite (sqlite-net) ◀── ChangeTracker ──▶ PendingChange (outbox)                      │
│                    users/<uid>/ veya misafir veritabanı      │                                           │
│                                                            SyncService ──▶ SupabaseClient (HTTP)          │
│ Doğrudan ücretsiz servisler: Open-Meteo · Photon · OpenFreeMap (MapLibre WebView) · Frankfurter          │
└───────────────────────────────────────────────────────┬───────────────────────────────────────────────────┘
                                                        │ HTTPS (publishable key + kullanıcı JWT)
┌─────────────────────────────── Supabase (Frankfurt) ──┴───────────────────────────────────────────────────┐
│ Auth (e-posta + Turnstile, Google OAuth, Before User Created hook)                                        │
│ Postgres: tablolar + RLS · RPC: apply_changes / pull_changes / paylaşım / convert_trip_currency / …        │
│ Storage: trip-files (trips/<trip_id>/<id>.<ext>) · pg_cron: gece temizliği                                 │
│ Edge Functions: plan-trip ──▶ OpenAI · OpenStreetMap (Photon, Overpass)    purge-files ──▶ Storage API    │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

- **Offline-first:** Uygulama hesapsız (misafir) tamamen cihazda çalışır. Hesapla her yazma önce yerel veritabanına ve outbox'a gider, bağlantı olunca gönderilir.
- **Platformlar:** Android birincil hedef (`Platforms/Android`); iOS/MacCatalyst/Windows klasörleri MAUI şablonundan gelir.

## 2. Mobil katmanlar (MVVM)

| Katman | Klasör | Sorumluluk |
| --- | --- | --- |
| Görünüm | `Views/`, `Controls/`, `Handlers/`, `Resources/Styles` | XAML sayfaları, özel kontroller, platform handler'ları. İş mantığı yok. |
| ViewModel | `ViewModels/` | Ekran durumu, komutlar (`[ObservableProperty]`, `[RelayCommand]`), doğrulama. `ViewModelBase.RunSafeAsync` hataları kullanıcıya açıklar. |
| Servis | `Services/` | İş kuralları ve veri erişimi; her servis bir arayüzle (`IExpenseService` …) DI'a kayıtlı (`MauiProgram.cs`). |
| Senkron | `Services/Sync/` | `ChangeTracker`, `SyncService`, `RemoteApplier`, `SyncFieldMapper`, `SyncSchema`, `SupabaseClient`. |
| Model | `Models/` | SQLite tabloları (`SyncEntityBase` + `ISyncEntity`) ve DTO'lar. |
| Yardımcı | `Helpers/` | Sabitler (`AppConstants`, `SupabaseConfig`, `InputLimits`), para/tarih biçimleme, yasal metin doldurma. |
| Kaynak | `Resources/Strings` | Tüm metinler TR (`AppResources.resx`) + EN (`.en.resx`); `AppResources.Designer.cs` elle güncellenir. |

Yönlendirme Shell ile (`AppShell.xaml.cs`, `Routes`). Bağımlılıklar `MauiProgram.cs`'de tekil (servisler) veya geçici (ViewModel/sayfa) kaydedilir. Testler (`Tests/TravelPlanner.Tests`, xUnit + Moq) uygulama kaynaklarını link'leyerek gerçek SQLite üzerinde çalışır.

## 3. Yerel veri ve hesaplar

- Misafir verisi ve her hesabın verisi ayrı SQLite dosyalarındadır (`AppData/users/<uid>/` + ekler). `AccountService` hesap değişiminde senkron turunu bitirir (TP-9), veritabanını değiştirir, hatırlatmaları yeniden kurar.
- Çıkışta yerel kopya kalır veya kullanıcı isterse silinir (TP-16); hesap silinince silinir. Yerel veri Android yedeğine dahil edilmez (TP-13).
- Para tutarları tam sayı kuruş/cent olarak saklanır (`*Minor`), dövizli masrafta girilen tutar + kur + tutarın para birimi tutulur (TP-11).
- Yedekleme: `BackupService` tüm tabloları + ekleri `.zip`'e yazar; geri yüklemede paylaşılan seyahatler atlanır veya kopya olarak yüklenir (TP-10).

## 4. Senkronizasyon (G1–G7)

1. **Yazma:** Tüm senkron satır yazmaları `ChangeTracker` üzerinden yapılır (aynı SQLite transaction'ında): SyncId/kim-ne-zaman, rol kontrolü (görüntüleyici yazamaz), outbox'a yalnızca değişen alanlar, silmede çöp kutusu (`TrashItem`).
2. **Gönderme:** `SyncService` outbox'ı 50'lik paketlerle `apply_changes` RPC'sine yollar. Sunucu her değişikliği ayrı uygular (alan bazlı birleştirme, son yazan kazanır, silme çevrimdışı düzenlemeyi yener). Kalıcı redler cihazda geri alınır (TP-6); kota redleri 30 dk bekletilir.
3. **Çekme:** `pull_changes(cursor)` değişen satırları ve üyelikleri döner; `RemoteApplier` uygular. Cursor açık transaction'ları atlamaz (TP-8); günde bir kez tam çekme yapılır.
4. **Tetikleyiciler:** yerel değişiklikten ~1 sn sonra, bağlantı gelince, uygulama öne gelince, açıkken 30 sn'de bir.
5. **Dosyalar:** ekler `trip-files` kovasına `trips/<trip SyncId>/<SyncId>.<ext>` yoluyla yüklenir, gerektiğinde indirilir; silinenleri sunucu temizler (`purge-files`).
6. **Paylaşım:** davet kodu (tek kullanımlık, 48 saat, görüntüleyici), roller (sahip/düzenleyici/görüntüleyici), sahiplik devri (TP-12). Para birimi değişimi sunucuda atomik RPC ile (TP-11).

## 5. Yapay zekâ orkestrasyonu

```
AiPlannerViewModel (ilk kullanımda açık rıza: ConsentService) ─▶ AiPlannerService ─(JWT, deviceId = tuzlu SHA-256)─▶ Edge Function plan-trip
   plan-trip: 1) JWT ve gövde (≤16 KB) kontrolü  2) ai_plan_quota RPC (hesap/cihaz/yeni hesap/global kota)
              3) destinasyon merkezi + gerçek yemek mekânları (OpenStreetMap)  4) OpenAI (JSON şeması, AI_MODEL, token bütçesi)
              5) yerleri haritada doğrula, bulunamayanları modelden yeniden iste veya at  6) temizlenmiş plan
              OpenAI hiç cevap vermezse ai_plan_refund ile hak iade edilir (günde en fazla 2)
   ◀─ plan (günler, yerler, yapılacaklar, ipuçları) ─ kullanıcı seçer ─▶ normal servislerle seyahate eklenir
```
Kişisel veriler (ad, e-posta, harcama, belge) gönderilmez. Varsayılan model `gpt-5-mini` (bkz. supabase/README).

## 5a. Keşfet kataloğu (TP-33)

Admin'in hazırladığı örnek seyahatler senkronun dışındadır: ayrı, salt okunur tablolar (`explore_*`, migration 23) ve iki RPC.
`ExploreService` bunları yalnızca publishable key ile çağırır (misafir de görür): `explore_list` 20'şer kart döner (keyset
cursor, OFFSET yok), `explore_detail` planın tamamını tek JSON'da. İlk sayfa `ResponseCache`'te 6 saat, filtresiz ilk sayfa
ayrıca önbellek klasöründe tutulur (sekme anında açılır, çevrimdışı da kart gösterir). `ExploreViewModel` sonsuz kaydırmayı
yönetir (aynı anda tek "daha fazla" isteği, id ile tekilleştirme, `RangeObservableCollection.AddRange` ile tek olay, yeni arama
eski isteği iptal eder). Kullanıcı planı eklediğinde `TripService.CreateFromTemplateAsync` tek transaction'da bağımsız bir kopya
oluşturur (gün N = başlangıç + N − 1, her satır `InsertTracked` ile outbox'a). İçerik yönetimi: [docs/EXPLORE_CONTENT.md](docs/EXPLORE_CONTENT.md).

## 5b. Yönetim paneli

Ayrı, private bir depodadır: [akkusemrullah/TravelPlanner.Admin](https://github.com/akkusemrullah/TravelPlanner.Admin) (ASP.NET Core Razor Pages).
Keşfet planlarını ekler/düzenler/yayınlar, kullanıcıları ve seyahatlerini (salt okunur) gösterir, AI kullanımını gösterir ve hesaba
özel günlük AI hakkı verir. Supabase'e yalnızca sunucudan, service
key ile, migration 25'teki `admin_*` RPC'leri üzerinden gider; tarayıcı anahtarı görmez. Giriş: izinli e-posta listesi +
Supabase şifresi. Kurulum o deponun README'sinde.

## 6. Güvenlik özeti
RLS + yalnızca RPC ile yazma, değer ve kota sınırları, Turnstile ve kayıt koruması, banlı/silinmiş hesabın anında reddi, kısa hata kodları. Ayrıntılar: [SECURITY.md](SECURITY.md).

## 7. Derleme ve test
- `dotnet test Tests/TravelPlanner.Tests` – birim ve entegrasyon testleri (gerçek SQLite).
- `dotnet build TravelPlanner.csproj -f net10.0-android` – Android; imzalı APK: `build-apk.ps1`; CI: `docs/ci/ci.yml`.
- Sunucu: `supabase/migrations/*` (ve birleşik `all_in_one.sql`), yerel SQL testleri `supabase/tests/`, fonksiyon testleri `node --test supabase/functions/*/*.test.ts`.

## English summary
.NET MAUI app with MVVM (Views → ViewModels → Services, DI in `MauiProgram.cs`), offline-first SQLite storage per account, and a Supabase backend (Postgres with RLS, RPC-only writes, Storage, Edge Functions). Every synced write goes through `ChangeTracker` into an outbox; `SyncService` pushes changed fields to `apply_changes` in batches of 50 and pulls with `pull_changes`. AI planning runs in the `plan-trip` Edge Function (quota → OpenStreetMap venues → OpenAI with a JSON schema → map verification); the OpenAI key never reaches the app. The Explore tab (TP-33) reads a separate, read-only catalog of admin-made sample trips (`explore_list` keyset pages, `explore_detail`) with the publishable key; adding one creates an independent copy through `TripService.CreateFromTemplateAsync`.
