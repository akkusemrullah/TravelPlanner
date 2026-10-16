# Supabase (G2)

Sunucu tarafı: tablolar, güvenlik kuralları (RLS), senkron ve paylaşım fonksiyonları, dosya alanı.
Proje: `bobliztrprgqhnudnhrs` (Frankfurt).

## Kurulum (bir kez)

1. Supabase Dashboard → **SQL Editor** → **New query**.
2. `all_in_one.sql` dosyasının tamamını yapıştır → **Run**. Sonunda "Success. No rows returned" görmelisin.
   - Dosya tekrar çalıştırılabilir (zaten var olanı bozmaz).
   - Sonda `pg_cron could not be enabled` uyarısı çıkarsa: **Database → Extensions → pg_cron → Enable**,
     sonra yalnızca `migrations/20261010000006_cron.sql` dosyasını SQL Editor'da çalıştır.
3. Kontrol: **Table Editor**'de `trips`, `expenses`, `trip_members` … tabloları; **Storage**'da `trip-files`
   (private) kovası görünmeli.

`all_in_one.sql`, `migrations/` klasöründeki 8 dosyanın sırayla birleşimidir. Supabase CLI kullanmak
istersen aynı dosyalar `supabase db push` ile de uygulanabilir.

## Dosyalar

| Dosya | İçerik |
| --- | --- |
| `migrations/…01_core_schema.sql` | Tablolar, `sync_change_seq`, sürüm/zaman tetikleyicileri, hesap açılınca profil |
| `migrations/…02_security.sql` | `has_trip_role`, RLS (üyeler okur), API rollerinin yetkileri (doğrudan yazma yok) |
| `migrations/…03_sync_rpc.sql` | `apply_changes` (outbox'ı uygular), `pull_changes` (değişiklikleri çeker) |
| `migrations/…04_sharing_rpc.sql` | Davet oluştur/kabul/iptal, üyeler, rol, çıkar/ayrıl, hesabı sil |
| `migrations/…05_storage.sql` | `trip-files` kovası ve `trips/<trip_id>/…` yolu için erişim kuralları |
| `migrations/…06_cron.sql` | 30 günden eski silinmiş satırları her gece temizleme |
| `tests/` | **Sadece yerel test** – Supabase'de çalıştırma |

## Kurallar özeti

- Okuma: seyahatin her üyesi (owner/editor/viewer) seyahatin her şeyini okur. Başkası hiçbir şey göremez.
- Yazma: tablolara doğrudan yazılamaz; yalnızca `apply_changes` üzerinden. Viewer hiçbir şey yazamaz,
  editor içerik ekler/düzenler/siler, seyahati silmek ve silinmiş seyahati geri getirmek owner'ın işi.
- Alan bazlı birleştirme: yalnızca değişen alanlar yazılır; aynı alanda son yazan kazanır, istemciye
  `conflict: true` döner. Offline düzenlenen satır başkası tarafından silinmişse silme kazanır
  (`row_deleted`); çöp kutusundan geri alma (`full: true`) satırı geri getirir.
- Satırlar başka seyahate taşınamaz; masraf/bölüşüm/aktivite ilişkileri aynı seyahatin satırlarına bağlı
  olmak zorunda (bileşik yabancı anahtarlar).
- Sürüm, `change_seq`, oluşturan/düzenleyen ve tarihler sunucuda atanır; istemcinin gönderdiği değerler
  yok sayılır.

## Yerel test (geliştirici için)

PostgreSQL 15+ üzerinde, boş bir veritabanında:

```bash
psql -d tp -f tests/00_local_stub.sql      # Supabase'in auth/storage parçalarının taklidi
for f in migrations/*.sql; do psql -v ON_ERROR_STOP=1 -d tp -f "$f"; done
psql -d tp -f tests/sync_tests.sql          # 75 kontrol, sonda "ALL SYNC TESTS PASSED"
```

## Yapay zekâ ile planlama (Edge Function `plan-trip`)

Uygulama planı doğrudan OpenAI'ye değil, Supabase'deki `plan-trip` fonksiyonuna sorar; OpenAI anahtarı yalnızca
Supabase'de durur. Kota: hesap başına günde 5 plan (`migrations/20261012000009_ai_planner.sql`,
`private.ai_daily_limit()` ile değiştirilir).

1. SQL: `all_in_one.sql`'i (veya yalnızca migration 9'u) SQL Editor'da çalıştır.
2. Anahtar: Dashboard → **Edge Functions → Secrets** → `OPENAI_API_KEY` = OpenAI anahtarın
   (isteğe bağlı `AI_MODEL`, varsayılan `gpt-5-nano`).
3. Fonksiyonu yayınla — iki yoldan biri:
   - **Dashboard:** Edge Functions → *Deploy a new function* → *Via Editor* → adı `plan-trip`;
     `functions/plan-trip/index.ts`, `functions/plan-trip/planner.ts` ve `functions/plan-trip/places.ts` dosyalarını aynı adlarla ekle (places.ts: yerleri OpenStreetMap üzerinde doğrular, gerçek restoranları bulur);
     **Verify JWT** ayarını kapat (kimlik kontrolünü fonksiyon kendisi, kullanıcının oturumuyla yapar).
   - **CLI:** `npx supabase login` → proje klasöründe
     `npx supabase functions deploy plan-trip --project-ref bobliztrprgqhnudnhrs --no-verify-jwt`
4. Fonksiyonun testleri (Node 22.18+): `node --test supabase/functions/plan-trip/planner.test.ts`
