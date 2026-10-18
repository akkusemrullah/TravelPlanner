# Üçüncü taraf lisansları / Third-Party Notices – Travel Planner

Travel Planner'ın kendi kodu özel lisanslıdır ([LICENSE](LICENSE)). Aşağıdaki bileşenler ve veri kaynakları kendi lisanslarıyla kullanılır. Uygulamadaki karşılığı: *Profil → Yasal ve gizlilik → Lisanslar* (`Licenses_Body`); web sitesindeki karşılığı: `acik-kaynak-lisanslari.html`. Sürümler `TravelPlanner.csproj`'dan alınmıştır.

## Uygulamaya dahil olan yazılımlar / Software shipped in the app

| Bileşen / Component | Sürüm | Kullanım | Lisans |
| --- | --- | --- | --- |
| .NET MAUI (`Microsoft.Maui.Controls`) ve .NET çalışma zamanı | 10.0.110 / .NET 10 | Uygulama çatısı | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM (ObservableProperty, RelayCommand) | MIT |
| Microsoft.Extensions.Http | 10.0.13 | HttpClient fabrikası | MIT |
| Microsoft.Extensions.Http.Resilience | 10.11.0 | Yeniden deneme / zaman aşımı | MIT |
| Microsoft.Extensions.Logging.Debug | 10.0.13 | Hata ayıklama günlüğü | MIT |
| System.Text.Json | 10.0.13 | JSON | MIT |
| sqlite-net-pcl | 1.11.285 | Cihaz içi veritabanı erişimi | MIT |
| SQLitePCLRaw (sqlite-net bağımlılığı) | – | SQLite bağlayıcısı | Apache 2.0 |
| SQLite | – | Cihaz içi veritabanı | Public domain |
| Plugin.LocalNotification | 14.1.2 | Hatırlatma bildirimleri | MIT |
| PDFsharp | 6.1.1 | Seyahat özeti PDF'i (cihazda) | MIT |
| MapLibre GL JS (`Resources/Raw/maplibre`) | 4.7.1 | Harita görüntüleme (WebView) | BSD 3-Clause – metni `Resources/Raw/maplibre/LICENSE.txt` |
| Inter yazı tipi (Rasmus Andersson) | – | Yazı tipi | SIL Open Font License 1.1 – `docs/licenses/Inter-OFL.txt` |
| Lucide ikonları | – | İkon yazı tipi | ISC – `docs/licenses/Lucide-LICENSE.txt` |

Yalnızca geliştirme/test için (uygulamaya dahil değil): xunit 2.9.2 (Apache 2.0), xunit.runner.visualstudio 2.8.2 (Apache 2.0), Microsoft.NET.Test.Sdk 17.12.0 (MIT), Moq 4.20.72 (BSD 3-Clause), coverlet.collector 6.0.2 (MIT).

## Veri kaynakları ve hizmetler / Data sources and services

| Kaynak / Source | Kullanım | Lisans / atıf |
| --- | --- | --- |
| OpenStreetMap | Harita ve yer verisi | © OpenStreetMap katkıcıları / contributors, ODbL 1.0 |
| OpenFreeMap | Harita karoları ve stili | OpenFreeMap; şema © OpenMapTiles (CC BY 4.0) |
| Open-Meteo | Hava tahmini, geçmiş hava, şehir arama | Open-Meteo.com, CC BY 4.0; kaynak veriler ulusal meteoroloji servisleri ve ECMWF/Copernicus ERA5 |
| Overpass API (overpass-api.de ve aynaları; sunucu tarafında, `plan-trip`) | Yapay zekâ planındaki yerlerin ve yemek mekânlarının doğrulanması | Veri © OpenStreetMap katkıcıları (ODbL) |
| Photon (komoot) | Yer arama ve ters coğrafi kodlama | Yazılım Apache 2.0; veri © OpenStreetMap katkıcıları (ODbL) |
| Frankfurter | Döviz kurları | Avrupa Merkez Bankası referans kurları; yazılım MIT |
| Supabase | Hesap, veritabanı, dosya depolama, Edge Functions | Hizmet koşulları: supabase.com/terms |
| OpenAI | Yapay zekâ ile planlama (sunucu tarafında) | Hizmet koşulları: openai.com/policies |
| Cloudflare Turnstile | Bot koruması (CAPTCHA) | Hizmet koşulları: cloudflare.com/terms |

Harita ekranında atıf metni ("© OpenStreetMap contributors", "OpenFreeMap") MapLibre'nin atıf kontrolüyle her zaman gösterilir.

## Markalar / Trademarks
Android, Google Play ve Google, Google LLC'nin; Apple, iPhone, iPad ve App Store, Apple Inc.'in; OpenAI, OpenAI OpCo LLC'nin ticari markalarıdır. Yalnızca uyumluluğu veya kullanılan hizmeti belirtmek için anılır.

---
Bir bağımlılık eklendiğinde veya sürümü değiştiğinde bu dosyayı, `Licenses_Body` metnini (resx) ve web sitesindeki sayfayı birlikte güncelle.
