# TravelPlanner

Seyahatlerinizi tek yerden planlamak için bir .NET MAUI uygulaması. Seyahat, görev, harcama ve gezilecek yer kaydı tutabilir, bütçenizi takip edebilir, gideceğiniz yerin hava durumunu ve haritasını görebilirsiniz. Uygulama hesap olmadan (misafir) çalışır ve veriler cihazda (SQLite) saklanır; isteğe bağlı bir hesapla (Supabase, AB – Frankfurt) veriler cihazlar arasında eşitlenir, seyahatler paylaşılır ve yapay zekâ ile plan oluşturulur. Cihazdan çağrılan web servisleri ücretsizdir ve API anahtarı gerektirmez.

## Özellikler

- Seyahatler: ad, ülke, şehir (otomatik tamamlamalı), tarih aralığı, bütçe ve bütçe para birimi, not
- Seyahat listesi: isim/şehir/ülke araması (Türkçe karakter ve büyük/küçük harf duyarsız), durum filtresi (Yaklaşan / Devam Ediyor / Tamamlandı), tarih, bütçe veya isme göre sıralama
- Gün gün plan: her gün için saatli ya da saatsiz etkinlikler, etkinliğe kayıtlı bir yer bağlama, tutamaçtan sürükleyerek sıralama; seyahat sürerken detay sayfasında "Bugün" kartı
- Görevler: seyahat başına yapılacaklar listesi; hava tahmini ve seyahat süresine göre akıllı bavul önerileri (şemsiye, mont, adaptör…), kaydedilip başka seyahatlere uygulanabilen bavul şablonları
- Harcamalar: kategori, tutar, tarih; farklı dövizle giriş (ECB kuruyla seyahatin para birimine otomatik çevrilir, orijinal tutar saklanır); bütçe kullanımı ve kalan bütçe
- Harcama analizi: kategori dağılımı ve günlük harcama grafiği (günlük bütçe çizgisiyle); bütçenin %80'i ve %100'ünde bildirim
- Yerler: yer adı otomatik tamamlama (müze, anıt, tarihi yer…), ziyaret edildi işareti
- Harita: kayıtlı yerler MapLibre haritasında (ziyaret edilenler yeşil, planlananlar mavi), açık/koyu temaya uygun harita stili
- Hava durumu: seyahat şehrinin anlık hava durumu ve seyahatin her günü için tahmin (16 gün içi gerçek tahmin, daha uzak günler için geçmiş 3 yılın ortalaması)
- Hatırlatmalar (saati Ayarlar'dan seçilir; Android / iOS / Mac Catalyst): yapılacaklar bitmediyse 3 gün önce hazırlık hatırlatması, seyahat boyunca etkinliği olan her günün sabahı o günün planı (erken bir etkinlik varsa ondan 1 saat önce), dönüşün ertesi sabahı harcama özeti / hesaplaşma hatırlatması
- Listelerde silme: seyahat, görev, yer, harcama, belge ve plan listelerinde çöp kutusu düğmesi seçme modunu açar; satırlar tek tek ya da "Tümünü seç" ile işaretlenip tek onayla silinir. Silinenler 30 gün çöp kutusunda kalır
- Belgeler: bilet, rezervasyon, vize PDF'i ve fotoğraflar (kamera / galeri / dosya); uygulamaya kopyalanır, çevrimdışı açılır
- Harcama bölüşme: katılımcılar, kimin ödediği, eşit bölüşüm ve "kim kime ne kadar borçlu" özeti
- Harcamaları dışa aktarma: seyahatin tüm harcamaları CSV olarak (Excel / Google E-Tablolar; Türkçe Excel için `;` ayraçlı), ödeyen ve kur bilgisiyle
- Seyahati kopyala: aynı rotayı tekrar gezmek için yeni seyahat - görevler (işaretsiz), yerler, günlük plan ve katılımcılar kopyalanır; tarihler değişince plan da kayar
- Paylaşma: seyahat planı ve özeti metin veya PDF olarak (sistem paylaşım menüsü)
- Yedekleme: tüm veriler ve belgeler tek .zip dosyasına; başka cihaza geri yükleme
- Harita: günün rotası (plandaki yerler sırasıyla), haritaya basılı tutarak yer ekleme
- Ayarlar: ad, varsayılan para birimi (₺ / € / $ / £), hatırlatma saati, tema (sistem / açık / koyu), dil (Türkçe / English)
- Hesap (isteğe bağlı): e-posta veya Google ile giriş, cihazlar arası eşitleme (çevrimdışı değişiklikler kuyruğa alınır), hesabı uygulama içinden silme
- Paylaşım: davet koduyla seyahate üye ekleme (kod tek kullanımlık ve 48 saat geçerli, isteğe bağlı en fazla 10 kişilik grup kodu; katılan görüntüleyici olur, düzenleme yetkisini sahip verir), üye ve rol yönetimi, yeni üye bildirimi; üyelerin e-postasını yalnızca seyahat sahibi görür
- Yapay zekâ ile planlama: 4 soruluk sihirbaz, gün gün plan + yerler + görevler; Supabase Edge Function (`supabase/functions/plan-trip`) üzerinden OpenAI, hesap ve cihaz başına günde 5 plan; uygunsuz öneriyi bildirme bağlantısı
- Çöp kutusu: silinenler 30 gün geri alınabilir; seçim moduyla (tek tek ya da "Tümünü seç") kalıcı olarak silinebilir - belge dosyalarının buluttaki kopyaları da silinir
- Destek ve yasal (Profil sekmesi): yardım merkezi (aranabilir SSS), "Bize ulaşın" e-postası, gizlilik politikası, KVKK aydınlatma metni, kullanım koşulları, açık kaynak lisansları. Metinler `AppResources` içinde (`Privacy_Body`, `Kvkk_Body`, `Terms_Body`, `Licenses_Body`, `Help_Faq`; `## Başlık` / `? Soru` biçimi, `Helpers/DocumentText`), iletişim bilgileri `Helpers/AppConstants.cs` içinde

## Gereksinimler

| Araç | Sürüm |
| --- | --- |
| .NET SDK | 10.0 (`global.json`) |
| MAUI workload | `dotnet workload install maui` |
| Visual Studio 2026 | 18.0 veya üstü (VS 2022 .NET 10 projelerini açamaz), ".NET Multi-platform App UI development" iş yüküyle |
| Android | Android SDK API 36 (hedef; Google Play zorunluluğu), en az API 24 |
| iOS / Mac Catalyst | Xcode yüklü bir Mac (eşleştirilmiş Mac veya doğrudan macOS) |

## Kurulum ve çalıştırma

```bash
git clone <repo-adresi>
cd TravelPlanner
dotnet restore
dotnet build -t:Run -f net10.0-android      # Android emülatörde çalıştır
```

Visual Studio'da `TravelPlanner.sln` dosyasını açıp hedef olarak Android emülatörünü seçmeniz de yeterli.

## Testler

Birim testleri `Tests/TravelPlanner.Tests` projesinde (xUnit + Moq). Servisler gerçek bir geçici SQLite dosyası üzerinde, web servisleri sahte bir `HttpMessageHandler` ile test edilir.

```bash
dotnet test Tests/TravelPlanner.Tests
dotnet test Tests/TravelPlanner.Tests --collect:"XPlat Code Coverage"   # kapsam raporu (coverlet)
```

Test projesi uygulama projesine referans vermez. MAUI uygulaması çoklu platform hedefli bir exe olduğu için platformdan bağımsız kaynak dosyaları (Models, Helpers, Services, ViewModels) test projesine **link** olarak eklenir.

## Mimari

```
Views (XAML sayfaları)
  └─ ViewModels (CommunityToolkit.Mvvm, ViewModelBase: hata yönetimi + tekil yükleme)
       └─ Servis arayüzleri (ITripService, IExpenseService, ITodoService, IPlaceService,
          IWeatherService, IGeocodingService, IExchangeRateService, IActivityService,
          IPackingService, IBudgetAlertService, INotificationService, IDialogService)
            ├─ IDatabaseService → SQLite (sqlite-net-pcl), PRAGMA user_version ile şema migration'ları
            ├─ HttpClient (zaman aşımı, User-Agent, Microsoft.Extensions.Http.Resilience ile retry)
            │    + IResponseCache (bellek içi, süreli önbellek)
            └─ Plugin.LocalNotification
```

- **Dependency injection:** Tüm kayıtlar `MauiProgram.cs` içinde, arayüz → sınıf şeklinde yapılır.
- **Para tutarları:** Kuruş cinsinden `long` olarak saklanır (`AmountMinor`, `PlannedBudgetMinor`). `decimal` özellikler bunların hesaplanmış görünümüdür.
- **Çoklu para birimi:** Her seyahatin bir bütçe para birimi vardır (`Trip.CurrencyCode`; boşsa Ayarlar'daki varsayılan). `Expense.AmountMinor` her zaman seyahatin para birimindedir, böylece SQL toplamları doğru kalır; kullanıcının girdiği tutar/döviz/kur `OriginalAmountMinor`, `OriginalCurrencyCode`, `ExchangeRate` alanlarında saklanır. Son alınan kur cihazda tutulur, çevrimdışıyken o kullanılır.
- **Veritabanı migration'ı:** `DatabaseService.Migrations` listesine bir sonraki sürüm numarasıyla yeni bir adım eklenir. Yayınlanmış bir adım asla değiştirilmez. Sadece yeni bir kolon eklemek için migration gerekmez.

## Sınırlar ve uç durumlar

Kullanıcının (veya geri yüklenen bir yedeğin) girebileceği her değerin üst sınırı `Helpers/InputLimits.cs` ve `AppConstants.MaxTripDays` içinde tek yerde tanımlıdır; formlar, servisler ve yedek içe aktarma aynı sınırları uygular.

| Konu | Sınır | Nerede uygulanır |
| --- | --- | --- |
| Seyahat süresi | En fazla 90 gün | Seyahat formu (doğrulama + bitiş tarihi seçicisi). Gün planı, gün gün hava durumu ve günlük harcama grafiği bu kadar gün için çizilir; eski uzun kayıtlarda "ilk 90 gün" uyarısı çıkar. |
| Tarih değişikliği | Seyahat bütünüyle kaydırılırsa etkinlikler de kayar; dışarıda kalanlar için "en yakın güne taşı / sil / vazgeç" sorulur | `TripDateRealignment`, `ActivityService.ApplyRealignmentAsync` |
| Tutar (harcama, bütçe, çevrilmiş tutar) | 0 – 9.999.999.999,99 | Formlar + `ExpenseService`/`TripService` + kur dönüşümü (aşarsa tamamı geri alınır) |
| Döviz kuru | 0,000001 – 1.000.000 | API cevabı, cihazda saklanan kur, harcama kaydı |
| Tarihler | 1900 – 2100 | Servisler ve yedek içe aktarma |
| Metinler | Model `[MaxLength]` değerleri (ör. seyahat adı 100, açıklama 500) | Entry `MaxLength` + kayıttan önce `InputLimits.EnforceTextLimits` |
| Koordinatlar | Sonlu, enlem ±90, boylam ±180 | `PlaceService`, haritadan gelen konum, harita işaretleri |
| Yedek dosyası | En fazla 2 GB, veri JSON'u 100 MB, ek başına 50 MB; ek yolları `attachments/` dışına çıkamaz | `BackupService.ImportAsync` / `Sanitize` |

## Yerelleştirme ve tema

- **Metinler:** `Resources/Strings/AppResources.resx` (Türkçe, varsayılan) ve `AppResources.en.resx` (İngilizce). XAML'de `{x:Static res:AppResources.Anahtar}`, C#'ta `AppResources.Anahtar` kullanılır. Yeni bir metin eklerken anahtarı **iki dosyaya birden** ekleyin; Visual Studio `AppResources.Designer.cs` dosyasını yeniden üretir.
- **Dil seçimi:** Ayarlar'da yapılır, uygulama yeniden başlatıldığında tüm ekranlara uygulanır (`LocalizationHelper`).
- **Renkler:** Sayfalar renkleri `{DynamicResource fds-...}` ile alır. Açık ve koyu değerler `Resources/Themes/LightTheme.xaml` ve `DarkTheme.xaml` içindedir; `ThemeService` sistem temasına veya kullanıcı seçimine göre bunları değiştirir. Yeni bir renk anahtarını iki tema dosyasına da ekleyin.

## Kullanılan dış servisler

| Servis | Kullanım | Not |
| --- | --- | --- |
| [Open-Meteo Forecast](https://open-meteo.com/en/docs) | Hava durumu | Anahtarsız, ücretsiz |
| [Open-Meteo Geocoding](https://open-meteo.com/en/docs/geocoding-api) | Şehir arama, koordinat | Anahtarsız, ücretsiz |
| [Photon (komoot)](https://photon.komoot.io) | Yer adı otomatik tamamlama | OpenStreetMap verisi |
| [Open-Meteo Historical Weather](https://open-meteo.com/en/docs/historical-weather-api) | 16 günden uzak seyahat günleri için geçmiş yıllar ortalaması | Anahtarsız, ücretsiz |
| [Frankfurter](https://frankfurter.dev) | Döviz kurları (Avrupa Merkez Bankası referans kurları) | Anahtarsız, ücretsiz, kota yok, ticari kullanım serbest |
| [OpenFreeMap](https://openfreemap.org) | Harita (vektör karolar, OpenStreetMap verisi) | Anahtarsız, ücretsiz, istek sınırı yok, ticari kullanım serbest |
| [MapLibre GL JS 4.7.1](https://maplibre.org) | Harita kütüphanesi | `Resources/Raw/maplibre` içinde paketli (BSD-3) |
| [Photon reverse](https://photon.komoot.io) | Haritada seçilen noktanın adı | Anahtarsız, ücretsiz |
| [PDFsharp 6.1](https://docs.pdfsharp.net) | Seyahat özeti PDF'i (cihazda üretilir) | MIT lisansı, ağ bağlantısı yok |
| [Supabase](https://supabase.com) | Hesap, eşitleme, paylaşım, belge depolama (yalnızca giriş yapınca) | Proje bölgesi Frankfurt (AB); tablolar RLS ile korunur |
| [OpenAI](https://openai.com) | Yapay zekâ ile planlama (Edge Function üzerinden, anahtar yalnızca sunucuda) | İsteğe bağlı; günlük kota |

**Ücretsiz API denetimi (Sprint 4):** Önceki sürümde kullanılan CARTO raster harita karoları artık API anahtarı istiyor ve ücretsiz ticari kullanımı sınırlıyor; harita, tamamen ücretsiz ve anahtarsız OpenFreeMap + MapLibre'ye taşındı (`Resources/Raw/leaflet` artık paketlenmiyor, klasör silinebilir). Open-Meteo'nun ücretsiz planı **ticari olmayan** kullanım içindir (günde 10.000 istek altında); uygulama ücretli/reklamlı olarak yayınlanırsa Open-Meteo'nun ticari planı veya ticari kullanıma açık bir alternatif (ör. MET Norway, yalnızca ~9 günlük tahmin) değerlendirilmelidir. Photon'un herkese açık sunucusu adil kullanım koşuluyla ücretsizdir.

## Klasör yapısı

```
Helpers/      Sabitler, converter'lar, MoneyConverter, ülke listesi, Charts/ (GraphicsView grafik çizimleri)
Models/       SQLite varlıkları (Trip, TodoItem, Expense, Place, TripActivity, PackingTemplate) ve DTO'lar
Services/     Veri, web ve platform servisleri + arayüzleri
ViewModels/   Sayfa ViewModel'leri ve ViewModelBase
Views/        XAML sayfaları
Platforms/    Platforma özel kod ve manifest'ler
Resources/    İkon, font, görsel, stil ve ham dosyalar (MapLibre)
Tests/        Birim test projesi
```

## Platform notları

- **Android:** Hatırlatmalar tam zamanlı alarm izni (`SCHEDULE_EXACT_ALARM`) istemez (TP-58); sistem birkaç dakika geciktirebilir, Doze modunda da çalışır.
- **Windows:** Plugin.LocalNotification Windows'u desteklemez. Uygulama çalışır ama hatırlatma gönderilmez.

## Yayın ve CI/CD

- `.github/workflows/ci.yml`: her PR/push'ta birim testleri + Android derlemesi, `main`'de imzalı `.aab`.
- `docs/RELEASE.md`: sürümleme, keystore oluşturma ve güvenli saklama, GitHub secrets, sürüm kontrol listesi.
- `docs/PRIVACY.md`, `docs/KVKK.md`, `docs/TERMS.md`: gizlilik politikası, KVKK aydınlatma metni ve kullanım koşulları (uygulamada *Profil → Yasal ve gizlilik*; GitHub Pages ile yayınlanır, bkz. `docs/RELEASE.md`).
- `docs/ACCOUNT_DELETION.md`: Google Play'in hesap/veri silme bağlantısı için sayfa. `docs/HELP.md`: yardım merkezinin web kopyası.
- `docs/store/`: mağaza metinleri (TR/EN), 512 px ikon, öne çıkan görsel.
- Tüm belgelerin dizini: `docs/README.md` (mimari `ARCHITECTURE.md`, veri sözlüğü `docs/DATA_MODEL.md`, güvenlik `SECURITY.md`, lisans `LICENSE` ve `THIRD_PARTY_NOTICES.md`, uyum `docs/COMPLIANCE.md`, açık rıza / yapay zekâ feragatnamesi / iade politikası `docs/legal/`, veri güvenliği beyanı ve sürüm notları `docs/store/`).
