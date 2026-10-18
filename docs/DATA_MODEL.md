# Veri Sözlüğü / Data Model – Travel Planner

Kaynak: `Models/*.cs` (cihazdaki SQLite tabloları) ve `supabase/migrations/*` (sunucu tabloları). Kod değişirse bu belge güncellenmelidir. Sunucuda kolon adı, özellik adının snake_case hâlidir (`AmountMinor` → `amount_minor`, `SyncFieldMapper`); tamsayı ilişkiler sunucuda ilgili satırın SyncId'si (uuid) olur.

Genel terimlerin karşılıkları: **Itinerary** (gezi planı) = `Trip` + günlere dağılmış `TripActivity` kayıtları; **Waypoint** (durak) = `Place` (koordinatlı yer) ve ona bağlanan `TripActivity`; **Expense** = `Expense` (+ bölüşüm için `Participant`, `ExpenseShare`).

## Ortak alanlar (her senkron tablo – `SyncEntityBase`)

| Alan | Tür | Açıklama |
| --- | --- | --- |
| `Id` | int, PK, otomatik | Cihaz içi kimlik (yalnızca bu cihazda anlamlı) |
| `SyncId` | string(36) uuid | Cihazlar arası kimlik; sunucuda `id` |
| `CreatedBy` / `UpdatedBy` | string(36)? | Hesap (auth uid); misafirde boş |
| `UpdatedAtUtc` | DateTime | Son değişiklik zamanı |
| `Version` | long | Sunucu sürümü (çakışma tespiti için `base_version`) |
| `ChangeSeq` | long | Sunucunun değişiklik sırası; 0 = henüz buluta gitmedi |

## Seyahat ve plan

### `Trip` → `trips`
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `Name` | string(100), zorunlu | Seyahat adı |
| `Country`, `City` | string(100), zorunlu | Varış yeri |
| `StartDate`, `EndDate` | tarih | En fazla 90 gün (`AppConstants.MaxTripDays`); bitiş ≥ başlangıç |
| `PlannedBudgetMinor` | long | Planlanan bütçe, seyahat para biriminin kuruş/cent cinsinden |
| `CurrencyCode` | string(3)? | ISO 4217; boş = eski seyahat, kullanıcının varsayılan para birimi. Masraflıyken yalnızca `convert_trip_currency` ile değişir (TP-11) |
| `Description` | string(500)? | Not |
| Sunucu ek: `owner_id` | uuid | Sahip; yalnızca `transfer_trip_ownership` ile değişir (TP-12) |

### `TripActivity` → `activities` (gün planı)
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `Date` | tarih | Seyahatin günü |
| `StartTime`, `EndTime` | TimeSpan? | İsteğe bağlı saat |
| `Title` | string(150), zorunlu | |
| `Note` | string(500)? | |
| `PlaceId` | int? → Place | Bağlı yer (durak) |
| `SortOrder` | int | Gün içindeki sıra (sürükle-bırak) |

### `Place` → `places`
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `Name` | string(150), zorunlu | |
| `Description` | string(500)? | |
| `Latitude`, `Longitude` | double | Dünya üzerinde geçerli koordinat (`PlaceService.IsValidCoordinate`) |
| `IsVisited` | bool | Haritada farklı renk |

### `TodoItem` → `todos` (yapılacaklar ve bavul listesi)
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `Title` | string(150), zorunlu | |
| `IsCompleted` | bool | |
| `AssignedToUserId` | string(36)? | Paylaşılan seyahatte atanan üye |
| `CreatedDate` | DateTime | |

### `PackingTemplate` → `packing_templates` (hesaba ait, seyahatten bağımsız)
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `Name` | string(80), zorunlu | |
| `Items` | string | Satır başına bir eşya |
| `CreatedDate` | DateTime | |

## Bütçe ve bölüşüm

### `Expense` → `expenses`
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `Category` | enum `ExpenseCategory` | 0 Ulaşım, 1 Konaklama, 2 Yemek, 3 Eğlence, 4 Alışveriş, 5 Diğer |
| `AmountMinor` | long, 0 … 999 999 999 999 | Seyahat para biriminde tutar (kuruş/cent); SQL toplamları bu kolondan |
| `AmountCurrencyCode` | string(3)? | `AmountMinor`'ın para birimi (TP-11); seyahatinkinden farklıysa sunucu kayıtlı kurla çevirir |
| `OriginalAmountMinor` | long | Kullanıcının yazdığı tutar |
| `OriginalCurrencyCode` | string(3)? | Yazıldığı para birimi |
| `ExchangeRate` | double, 0.000001 … 1 000 000 | Orijinal → seyahat para birimi kuru |
| `Date` | tarih, 1900 … 2100 | |
| `PaidByParticipantId` | int? → Participant | Ödeyen (bölüşüm) |
| `Description` | string(250)? | |

### `Participant` → `participants`
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `Name` | string(60), zorunlu | Bölüşümdeki kişi (hesabı olmayabilir) |
| `UserId` | string(36)? | Seyahat üyesine bağlıysa hesabı |
| `CreatedDate` | DateTime | |

### `ExpenseShare` → `expense_shares`
| Alan | Tür | Açıklama |
| --- | --- | --- |
| `ExpenseId` | int → Expense | |
| `ParticipantId` | int → Participant | Masrafı paylaşan kişi (eşit bölüşüm) |

## Belgeler

### `TripAttachment` → `attachments`
| Alan | Tür / sınır | Açıklama |
| --- | --- | --- |
| `TripId` | int → Trip | |
| `DisplayName` | string(150), zorunlu | |
| `RelativePath` | string(260) | Cihazdaki yol: `attachments/<tripId>/<dosya>` |
| `ContentType` | string(100)? | JPEG, PNG, HEIC/HEIF, WebP, PDF |
| `Kind` | enum | 0 Fotoğraf, 1 Belge |
| `SizeBytes` | long | |
| `RemotePath` | string(300)? | Sunucuda `storage_path`: `trips/<trip SyncId>/<SyncId>.<uzantı>`; yüklenmeden önce boş |
| `CreatedDate` | DateTime | |

## Yalnızca cihazda (senkronlanmaz)

| Tablo | Amaç | Önemli alanlar |
| --- | --- | --- |
| `PendingChange` | Outbox: buluta gönderilecek değişiklikler | `EntityType`, `EntitySyncId`, `TripSyncId`, `Operation` (Upsert/Delete), `ChangedFields` (null = tüm satır), `BaseVersion`, `Attempts`, `LastError` |
| `TrashItem` | Çöp kutusu (30 gün) | `BatchId` (birlikte silinenler), `IsRoot`, `EntityType`, `Json` (satırın kopyası), `DeletedAtUtc`, `DeletedBy` |
| `SyncState` | Senkron durumu | `Key`/`Value` (pull cursor, son tam çekme) |
| `TripMembership` | Kullanıcının seyahatlerdeki rolü | `TripSyncId`, `Role` (owner/editor/viewer) |
| `KnownUser` | Üye adları (çevrimdışı "son düzenleyen") | `UserId`, `DisplayName`, `Email` (yalnızca sahibe) |

## Yalnızca sunucuda

| Tablo | Amaç |
| --- | --- |
| `profiles` | Hesabın görünen adı ve fotoğraf adresi (`email` kolonu API'den okunamaz) |
| `trip_members` | Üyelik ve rol; seyahat başına tek sahip |
| `trip_invites` | Davet kodları (tek kullanımlık, 48 saat) |
| `private.*` | Kotalar (`sync_usage`, AI sayaçları), dosya temizlik kuyruğu, para birimi değişim günlüğü, engelli e-posta alan adları |

## Keşfet kataloğu (yalnızca sunucuda, senkron dışı, salt okunur – TP-33, migration 23)

| Tablo | Amaç | Önemli alanlar |
| --- | --- | --- |
| `explore_trips` | Admin'in örnek seyahati | `slug` (benzersiz), `title` ≤ 100, `summary` ≤ 500, `description` ≤ 2000, `country`/`city` ≤ 100, `duration_days` 1–90, `budget_level` 1–3, `est_budget_minor` + `currency_code`, `tags` (culture, nature, sea, food, family, romantic, adventure), `months` 1–12, `tips`, `cover_path`/`thumb_path`, `image_credit`, `lang`, `sort_rank`, `is_featured`, `status` (draft/published/archived), `publish_from`/`publish_until`, `published_at`, `content_version`, `search_text` (Türkçe katlanmış arama metni, trigram index) |
| `explore_trip_places` | Planın yerleri | `name` ≤ 150, `description` ≤ 500, `latitude`/`longitude`, `sort_order` |
| `explore_trip_activities` | Gün planı | `day_index` 1–90 (1 = ilk gün), `start_time`/`end_time`, `title` ≤ 150, `note` ≤ 500, `place_id` |
| `explore_trip_todos` | Yapılacaklar ve bavul | `title` ≤ 150, `kind` (todo/packing) |

Uygulamada saklanmaz; eklenen plan normal `Trip`/`Place`/`TripActivity`/`TodoItem` satırlarına kopyalanır (bağımsız kopya).
Cihazda yalnızca filtresiz ilk sayfa (önbellek klasörü), filtre seçimleri ve "daha önce eklendi" listesi (Preferences) tutulur.

## API'ye özel veri yapıları (saklanmaz)
- `ExploreTripCard`, `ExploreTripDetail` – Keşfet listesi ve plan detayı (`explore_list`, `explore_detail`).
- `AiTripPlan` – `plan-trip` cevabı: günler → etkinlikler, yerler (doğrulanmış koordinatla), yapılacaklar, ipuçları.
- `WeatherInfo`, `DailyForecast` – Open-Meteo cevapları (önbellekli).
- `CitySuggestion`, `PlaceSuggestion` – şehir/yer arama sonuçları.
- `PackingSuggestion` – hava ve süreye göre bavul önerisi (cihazda kurallarla).

Değer sınırları hem uygulamada (`InputLimits`, `EntityValidator`) hem sunucuda (migration 11 ve 13) aynıdır.
