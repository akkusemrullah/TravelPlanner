# Keşfet içeriği / Explore content – Travel Planner

Keşfet sekmesindeki hazır seyahatler (TP-33) uygulamadan değil, Supabase'den yönetilir. Uygulamada admin paneli yok;
içerik **SQL Editor**'da iki fonksiyonla, görseller **Dashboard → Storage**'dan yönetilir. Kullanıcı bir planı eklediğinde
kendi seyahatlerine bağımsız bir kopya düşer: planı sonradan değiştirmek veya arşivlemek eklenmiş kopyaları etkilemez.

Şema ve fonksiyonlar: `supabase/migrations/20261018000023_explore_catalog.sql` (aynı içerik `all_in_one.sql`'de).

**Kolay yol:** yönetim paneli ([admin/README.md](../admin/README.md)) aşağıdaki işlerin hepsini form ve düğmelerle yapar
(plan ekleme/düzenleme, JSON yapıştırma, yayınlama/arşivleme, görsel yükleme). SQL Editor yolu yine çalışır.

## 1. Plan ekleme veya güncelleme

```sql
select private.explore_upsert($json${ ... aşağıdaki biçimde JSON ... }$json$::jsonb);
```

- Eşleştirme `slug` ile yapılır: aynı slug varsa plan güncellenir (`content_version` +1), yoksa eklenir.
- Gün planı, yerler ve yapılacaklar her güncellemede **bütünüyle** değiştirilir.
- Yeni plan `draft` olarak eklenir (JSON'da `"status": "published"` verilmedikçe); güncellemede `status` verilmezse
  mevcut durum korunur.
- Geçersiz değer (ör. süre dışı gün, bilinmeyen yer anahtarı, uzun başlık) bütün işlemi geri alır.

### JSON biçimi

| Alan | Zorunlu | Açıklama |
| --- | --- | --- |
| `slug` | evet | `istanbul-3-gun` gibi; küçük harf, rakam ve tire, ≤ 80 |
| `title` | evet | Kart başlığı, ≤ 100 |
| `summary` | | Kısa özet, ≤ 500 (kopyada seyahat açıklaması olur) |
| `description` | | Detaydaki uzun açıklama, ≤ 2000 |
| `country`, `city` | evet | ≤ 100 |
| `duration_days` | evet | 1–90 |
| `budget_level` | | 1 = ₺, 2 = ₺₺ (varsayılan), 3 = ₺₺₺ |
| `est_budget_minor`, `currency_code` | | Tahmini bütçe kuruş/cent cinsinden (15 000 ₺ = `1500000`), para birimi (varsayılan `TRY`) |
| `tags` | | `culture`, `nature`, `sea`, `food`, `family`, `romantic`, `adventure` (en fazla 10) |
| `months` | | Uygun aylar 1–12; boş = her mevsim |
| `tips` | | İpuçları (en fazla 10) |
| `cover_path`, `thumb_path` | | `covers/<slug>.webp`, `thumbs/<slug>.webp` veya tam `https://` görsel adresi (ör. Wikimedia Commons, migration 24) |
| `image_credit` | | Görsel kaynağı/lisansı, ≤ 200 (detayda küçük yazıyla görünür) |
| `lang` | | `tr` (varsayılan) veya `en` |
| `sort_rank` | | "Önerilen" sırası, küçük olan önce (varsayılan 1000) |
| `is_featured` | | Öne çıkan |
| `status` | | `draft` / `published` / `archived` |
| `publish_from`, `publish_until` | | Mevsimlik yayın aralığı (ISO tarih); dışında kimse göremez, cron gerekmez |
| `places[]` | | `key` (gün planı bununla bağlar), `name` ≤ 150, `description` ≤ 500, `lat`, `lon` |
| `days[]` | | `day` (1 = ilk gün), `activities[]`: `start`/`end` ("09:00"), `title` ≤ 150, `note` ≤ 500, `place` (yer `key`'i) |
| `todos[]` | | `title` ≤ 150, `kind`: `todo` (varsayılan) veya `packing` (bavul) |

Sınırlar: plan başına en fazla 500 yer, 500 etkinlik, 500 yapılacak.

### Örnek

```json
{
  "slug": "istanbul-3-gun",
  "title": "İstanbul 3 gün",
  "summary": "Tarihi yarımada, Boğaz ve Beyoğlu.",
  "country": "Türkiye",
  "city": "İstanbul",
  "duration_days": 3,
  "budget_level": 2,
  "est_budget_minor": 1500000,
  "currency_code": "TRY",
  "tags": ["culture", "food"],
  "months": [4, 5, 9, 10],
  "tips": ["Müze Kart ile kuyrukları kısalt."],
  "cover_path": "covers/istanbul-3-gun.webp",
  "thumb_path": "thumbs/istanbul-3-gun.webp",
  "image_credit": "Kendi çekimimiz",
  "sort_rank": 10,
  "status": "published",
  "places": [
    { "key": "ayasofya", "name": "Ayasofya", "lat": 41.0086, "lon": 28.9802 }
  ],
  "days": [
    { "day": 1, "activities": [
      { "start": "09:00", "end": "11:00", "title": "Ayasofya", "place": "ayasofya", "note": "Erken git." }
    ] }
  ],
  "todos": [
    { "title": "Müze Kart al" },
    { "title": "Rahat ayakkabı", "kind": "packing" }
  ]
}
```

Başlangıç için üç taslak plan: `supabase/explore_samples.sql` (taslak olarak eklenir; gözden geçirip yayınla).

## 2. Yayınlama, taslağa alma, arşivleme

```sql
select private.explore_set_status('istanbul-3-gun', 'published');  -- yayınla
select private.explore_set_status('istanbul-3-gun', 'draft');      -- taslağa al
select private.explore_set_status('istanbul-3-gun', 'archived');   -- kaldır (silme yerine)
```

Silme yerine arşivle: kullanıcıların kopyaları zaten bağımsızdır, arşiv geri alınabilir.

## 3. Görseller

- Kova: `explore-images` (herkese açık okuma, yazma yalnızca Dashboard / service role).
- `covers/<slug>.webp`: detay kapağı, ~1200 px genişlik.
- `thumbs/<slug>.webp`: liste küçük resmi, ~600 px genişlik, WebP, **≤ 80 KB** (liste akıcı kalsın).
- Kova dosya sınırı 1 MB, yalnızca WebP/JPEG.
- Kovaya yüklemek yerine `cover_path` / `thumb_path` alanına doğrudan `https://` adresi de yazılabilir (ör. Wikimedia Commons
  küçük resim adresi, kapak ~1200 px, liste ~600 px). Dış adres kalkarsa görsel boş görünür; ara sıra kontrol et.
- Yalnızca kendi çekimimiz veya ticari kullanıma uygun lisanslı görseller; kaynağı `image_credit`'e yaz.

## 4. Düzenli güncelleme

- **Ayda bir içerik turu:** kapanan/taşınan mekânlar, fiyat seviyeleri, tahmini bütçeler, görsel lisansları.
- **Mevsim başlarında:** öne çıkanları (`is_featured`, `sort_rank`) yenile; mevsimlik planlara `publish_from` /
  `publish_until` ver (ör. kayak rotaları kasımda açılır, martta kapanır).
- **Yeni içerik:** istersen `plan-trip` yapay zekâsıyla taslak üret, gözden geçirip JSON'a çevir. Yayınlanan her şey
  insan kontrolünden geçer.
- Yayına çıkmadan önce en az 15–20 plan hazır olsun (ör. İstanbul 3 gün, Kapadokya 2 gün, Ege kıyıları 5 gün, Roma 4 gün,
  Paris 4 gün, Balkan turu 7 gün).

## 5. Uygulama tarafı

- Liste `explore_list` ile 20'şer kart (keyset sayfalama), detay `explore_detail` ile tek JSON gelir; ikisi de giriş
  gerektirmez (publishable key).
- İlk sayfa 6 saat bellekte, filtresiz ilk sayfa ayrıca cihazda tutulur (çevrimdışı gösterim).
- Bir plan cihazda daha önce eklendiyse uygulama "Bu seyahati daha önce ekledin. Yine de ekle?" diye sorar.

---

## English summary

Explore's sample trips are managed in Supabase, not in the app: `private.explore_upsert(jsonb)` adds or replaces a
sample by `slug` (format above; the plan, places and to-dos are replaced as a whole, `content_version` goes up),
`private.explore_set_status(slug, status)` publishes / unpublishes / archives it, and images go into the public
`explore-images` bucket (`covers/<slug>.webp`, `thumbs/<slug>.webp` ≤ 80 KB). Users get an independent copy, so later
edits never touch their trips. Review content monthly and refresh featured samples each season.
