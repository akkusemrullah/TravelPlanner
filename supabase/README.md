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

`all_in_one.sql`, `migrations/` klasöründeki dosyaların sırayla birleşimidir. Supabase CLI kullanmak
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
| `migrations/…07_account_deletion.sql` | G7: `delete_account` (sahibi olunan seyahatler, üyelikler, profil, giriş bilgileri); hesabın başka seyahatlere yüklediği dosyalar sahipsiz kalır |
| `migrations/…08_account_status.sql` | G7: `account_status`, banlanan/silinen hesabı RPC ve RLS anında reddeder (`require_uid`, `has_trip_role`), `private.ban_user` / `unban_user` (TP-14 ile `account_status` yalnızca kendi hesabını sorar) |
| `migrations/…09_ai_planner.sql` | Yapay zekâ planı: hesap ve cihaz başına günlük kota (`ai_plan_quota`) |
| `migrations/…10_ai_refund.sql` | OpenAI cevap veremezse planı geri veren tek kullanımlık fiş (`ai_plan_refund`) |
| `migrations/…11_value_limits.sql` | SEC-01: tutar, kur, tarih, kategori, sıra ve saat sınırları (uygulamadaki `InputLimits` ile aynı); sınır dışı yazma `invalid` ile reddedilir |
| `migrations/…12_ai_cost_limits.sql` | SEC-02: herkes için günlük global tavan, yeni hesaplara ilk 24 saatte düşük kota, hesap başına günde en fazla 2 iade |
| `migrations/…13_quota_limits.sql` | TP-3: hesap/seyahat başına satır sınırları, günlük yeni satır ve dakikalık senkron sınırı, `trip-files` için dosya türü ve boyut sınırları, `items`/`avatar_url` uzunlukları |
| `migrations/…14_invite_hardening.sql` | TP-4: davet kodları tek kullanımlık ve 48 saat geçerli, katılan görüntüleyici olur; yanlış kod denemesi sınırı; e-postalar yalnızca sahibe; yeni üye bildirimi için `get_recent_joins` |
| `migrations/…15_attachment_paths.sql` | TP-5: ekin `storage_path`'i yalnızca kendi dosyası (`trips/<trip_id>/<id>.<uzantı>`), `content_type`'ı yalnızca JPEG/PNG/HEIC/HEIF/WebP/PDF olabilir; aykırı yazma `invalid` ile reddedilir, eski bozuk değerler temizlenir |
| `migrations/…16_file_cleanup.sql` | G7: kalıcı silinen eklerin dosyaları Storage'dan da silinir (kuyruk `private.file_cleanup_queue`, gece `purge-files` fonksiyonu) |
| `migrations/…17_account_file_cleanup.sql` | TP-7: `delete_account` sahibi olunan seyahatlerin klasörlerindeki tüm dosyaları aynı işlemde temizlik kuyruğuna yazar; gece taraması var olmayan seyahatlerin klasörlerini de temizler |
| `migrations/…18_pull_cursor_safety.sql` | TP-8: `pull_changes` cursor'ı hâlâ açık bir yazma transaction'ının satırını atlamaz (güvenli nokta: 10 sn önce ya da en eski açık yazma transaction'ının başlangıcı); `apply_changes` satır kilidi için en fazla 2 sn bekler, çağrı başına en fazla 6 sn sürer |
| `migrations/…19_trip_currency.sql` | TP-11: para birimi değişimi tek atomik RPC'de (`convert_trip_currency`, yalnızca sahip/editör; başkası önce çevirdiyse reddedilir); masrafta tutarın para birimi (`amount_currency_code`); eski para biriminde geç gelen masraf kayıtlı kurla çevrilir; masraflı seyahatin `currency_code`'u `apply_changes` ile değiştirilemez |
| `migrations/…20_ownership_transfer.sql` | TP-12: `get_owned_shared_trips` (sahibi olunan, başka üyesi olan seyahatler ve üye sayısı), `transfer_trip_ownership` (sahip → editör, seçilen üye → sahip); devredilen seyahat hesap silinince silinmez |
| `migrations/…21_error_details.sql` | TP-14: `apply_changes` reddedilen değişiklikte veritabanı mesajı yerine kısa kod döner (`duplicate`, `missing_parent`, `out_of_range` …), ayrıntı sunucu loguna yazılır; `account_status` anon'a kapalı, yalnızca kendi hesabını sorar |
| `migrations/…22_signup_protection.sql` | TP-15: tek kullanımlık e-posta alan adlarıyla hesap açılamaz (`hook_before_user_created`, liste `private.blocked_email_domains`). **Çalıştırdıktan sonra** Dashboard'da: Auth → Hooks → Before User Created'ı bu fonksiyona bağla; Email → Confirm email açık, şifre en az 8 + harf ve rakam; Attack Protection → CAPTCHA (Turnstile) açık |
| `migrations/…23_explore_catalog.sql` | TP-33: Keşfet kataloğu (`explore_trips` + yerler, gün planı, yapılacaklar), senkron dışında ve salt okunur; herkes yalnızca yayında ve yayın aralığındaki planları okur (RLS). `explore_list` (keyset sayfalama, Türkçe duyarsız arama, filtre, sıralama, sayfa ≤ 30), `explore_detail`; içerik `private.explore_upsert(jsonb)` / `private.explore_set_status` ile (bkz. [docs/EXPLORE_CONTENT.md](../docs/EXPLORE_CONTENT.md)); herkese açık `explore-images` kovası. `unaccent` ve `pg_trgm` eklentilerini açar |
| `migrations/…25_admin_panel.sql` | Yönetim paneli (private depo `TravelPlanner.Admin`): yalnızca `service_role`'ün çağırabildiği `admin_*` RPC'leri (genel sayılar, tüm Keşfet planları + `explore_upsert`/`explore_set_status` sarmalayıcıları, kullanıcı arama, AI kullanımı, seyahatler ve detayları salt okunur); hesaba özel günlük AI hakkı (`private.ai_user_limits`, `ai_limit_for` önce buna bakar) ve "bugünü sıfırla" |
| `migrations/…26_admin_ai_devices.sql` | Panelden verilen AI hakkı gerçekten işler: hesaba özel hakkı olan hesap cihaz sayacına takılmaz (aynı telefondaki diğer hesaplar cihazı doldurmuş olsa da); hesabın kullandığı cihazlar hatırlanır (`private.ai_user_devices`), "bugünü sıfırla" hepsinin bugünkü sayacını sıfırlar; panel cihaz sayacını da görür |
| `migrations/…27_admin_accounts.sql` | Panelden hesap işlemleri: banla (gün sayısıyla ya da süresiz, tüm cihazlardan çıkış), ban kaldır, tüm cihazlardan çıkış yaptır, hesabı sil (uygulamadaki "hesabımı sil" ile aynı yol: `delete_account`); Keşfet planını kalıcı silme. Yalnızca `service_role` |
| `migrations/…28_deleted_trip_invites.sql` | Silinen seyahatin davet kodları: çöpe atılan seyahatin açık kodları iptal edilir (geri alınsa da eski kod çalışmaz); hesapla birlikte silinen seyahatin kodu süresi dolana dek hatırlanır. `accept_invite` artık çalışmayan gerçek bir kod için `reason` döner (`trip_deleted`, `revoked`, `expired`, `used_up`) ve bunu yanlış deneme saymaz |
| `migrations/…29_app_settings.sql` | Panelden yönetilen uygulama ayarları (`private.app_settings`, tek satır): AI bakım modu ve mesajı, AI limitleri (kişi başı günlük 5, yeni hesap 2, toplam günlük 200, günlük iade 2; `private.ai_*_limit()` artık bu satırı okur), ana sayfa duyurusu, en düşük uygulama sürümü, yeni kullanıcı varsayılanları (para birimi, hatırlatma saati). Uygulama `app_config()` ile okur (anon da); panel `admin_settings` / `admin_set_settings` (yalnızca `service_role`). AI durdurulunca `ai_plan_quota` HTTP 503 döner, `plan-trip` bunu `ai_paused` yapar |
| `migrations/…30_forced_sign_out.sql` | Panelden "tüm cihazlardan çıkış" (ve ban) hemen işler: çıkış zamanı `private.forced_sign_outs`'ta tutulur, `account_status` bundan önce verilmiş token için `signed_out` döner, uygulama açılışta / öne gelince çıkış yapar (önceden telefondaki token 1 saate kadar çalışıyordu) |
| `migrations/…31_ai_model_setting.sql` | AI modeli ve düşünme seviyesi panelden seçilir (`app_settings.ai_model` / `ai_reasoning`); `plan-trip` bunu `ai_model_settings()` ile (yalnızca `service_role`) okur. Boş bırakılırsa `AI_MODEL` / `AI_REASONING` secret'ları, o da yoksa `gpt-5-mini` |
| `explore_samples.sql` | TP-33: üç taslak Keşfet planı (isteğe bağlı içerik, `all_in_one.sql`'de yok) |
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
- Çekme (TP-8): cursor yalnızca, daha düşük `change_seq` ile commit edebilecek hiçbir transaction'ın açık
  kalmadığı noktaya kadar ilerler; yeni satırlar yine gönderilir, sadece bir sonraki çekmede tekrar gelebilir.
  Uygulama çağrı başına en fazla 50 değişiklik gönderir ve günde bir kez her şeyi baştan çeker (`p_since = 0`).
- Para birimi (TP-11): masraflı bir seyahatin `currency_code`'u `apply_changes` ile değişmez; uygulama bulut seyahatinde
  `convert_trip_currency` çağırır (tek işlem, sahip/editör, başkası önce çevirdiyse `TPC01`). Masraf, tutarının para
  birimini (`amount_currency_code`) taşır; eski para biriminde gelen masraf kayıtlı kurla çevrilir.
- Sahiplik (TP-12): `owner_id` yalnızca `transfer_trip_ownership` ile değişir (sahip → editör, seçilen üye → sahip).
- Hatalar (TP-14): reddedilen değişikliğin `error` alanında yalnızca kısa kod vardır (`duplicate`, `missing_parent`,
  `out_of_range`, `missing_value`, `bad_value`, `busy`, `error`; kota mesajları hariç); ayrıntı sunucu loguna yazılır.

## Kullanım sınırları (TP-3)

Ücretsiz plan 500 MB veritabanı / 1 GB dosya alanı verir; dolarsa proje salt okunur olur ve uygulama herkes
için durur. Bu yüzden tek bir hesabın (ör. bir script ile) alanı dolduramaması için sınırlar var. Hepsi
`private.quota_limit()` (migration 13) içinde; değiştirmek için oradaki sayıyı düzenleyip dosyayı yeniden çalıştır.

| Sınır | Varsayılan |
| --- | --- |
| Hesabın sahip olduğu seyahat | 200 |
| Hesap başına hazır eşya listesi | 100 |
| Seyahat başına masraf / yer / aktivite / ek | 2 000 / 500 / 500 / 200 |
| Seyahat başına katılımcı / yapılacak / masraf paylaşımı | 200 / 2 000 / 50 000 |
| Hesap başına günde yeni satır (UTC) | 10 000 |
| `apply_changes`: hesap başına dakikada çağrı / değişiklik | 60 / 3 000 (aşılınca HTTP 429) |
| Dosya: seyahat başına toplam / dosya sayısı | 250 MB / 250 |
| Dosya: hesabın sahip olduğu tüm seyahatler toplamı | 500 MB |
| Dosya türleri | JPEG, PNG, HEIC, HEIF, WebP, PDF |

- Satır sınırları canlı satırları sayar; çöp kutusundakiler de sınırın iki katına kadar sayılır (sil–oluştur
  döngüsüyle tablo büyütülemesin; çöp 30 günde temizlenir). Çöpten geri alma da canlı sınıra takılır.
- Sınırı aşan değişiklik `apply_changes`'tan `rejected` / `quota` olarak döner (`error` hangi sınır olduğunu söyler),
  paketin geri kalanı uygulanır. Uygulama değişikliği silmez: kullanıcıya bir kez mesaj gösterir ve 30 dakikada
  bir yeniden dener (kullanıcı bir şey silince ya da ertesi gün gider).
- Dosyalar, editör yüklese bile seyahatin **sahibinin** hesabına sayılır.
- Dashboard'da elle: **Authentication → Rate Limits** değerlerini gözden geçir (ör. e-posta gönderimi, kayıt ve
  giriş istekleri); CAPTCHA ve "Confirm email" açıkken toplu hesap açmak zaten zordur.

## Davetler ve üyeler (TP-4)

- `create_invite` varsayılan olarak **tek kullanımlık**, **48 saat** geçerli ve **görüntüleyici** daveti üretir.
  Uygulamadaki "Birden çok kişi için kod" en fazla 10 kullanımlık kod ister (yine 48 saat). Katılan herkes
  görüntüleyici olur; düzenleme yetkisini sahip sonradan verir (`set_member_role`). Eski uygulama sürümlerinin
  "düzenleyici, 7 gün, sınırsız" isteği de aynı kurallara indirgenir; migration çalıştığında açıkta olan eski
  davetler de en fazla bir kullanım daha, görüntüleyici olarak ve 48 saat içinde geçerli kalacak şekilde kısılır.
- `accept_invite`: hesap başına saatte 10 yanlış kod; 10. yanlıştan sonra hesap 1 saat bekler (HTTP 429, `PT429`).
  Yanlış kod hata değil **boş sonuç** döner (hata sayacı geri almasın diye); uygulama bunu "geçersiz kod" sayar.
  Zaten üye olan biri kodu tekrar girerse kod harcanmaz.
- E-posta: `get_trip_members` e-postaları yalnızca seyahatin sahibine (ve herkese kendi e-postasını) döndürür.
  `profiles` tablosunun `email` kolonu API'den hiç okunamaz (kolon yetkisi); ad ve fotoğraf okunabilir.
- `get_recent_joins(since)`: sahibin seyahatlerine `since`'ten sonra katılanlar (en fazla 7 gün geri, 20 kişi);
  uygulama bununla "Ayşe, Roma seyahatine katıldı" bildirimini gösterir.
- Sayılar `private.invite_limit()` içinde (migration 14).

## Yerel test (geliştirici için)

PostgreSQL 15+ üzerinde, boş bir veritabanında:

```bash
psql -d tp -f tests/00_local_stub.sql      # Supabase'in auth/storage parçalarının taklidi
for f in migrations/*.sql; do psql -v ON_ERROR_STOP=1 -d tp -f "$f"; done
psql -d tp -f tests/sync_tests.sql          # ~250 kontrol, sonda "ALL SYNC TESTS PASSED"
```

## Silinen dosyaların temizliği (G7, Edge Function `purge-files`)

Bir ek silinince satırı 30 gün çöpte (`deleted_at`) bekler, dosyası da bulutta kalır ki geri alınabilsin. Gece
temizliği (migration 6) satırı kalıcı silince dosyası artık kimsenin işine yaramaz; migration 16 bu dosyaları siler:

- Satırı kalıcı silinen ekin (30 günlük temizlik, seyahatin temizliği, hesap silme) ve dosya yolu değişen ekin eski
  dosyası `private.file_cleanup_queue` kuyruğuna girer. Yalnızca satırın **kendi** dosyası (`trips/<trip_id>/<id>.<uzantı>`).
- Gece temizliği ayrıca hiçbir ek satırına ait olmayan, en az 1 günlük dosyaları da kuyruğa koyar (bu migration'dan
  önce kalanlar, sunucuda tutunamamış satırların dosyaları). Çöpteki eklerin dosyaları silinmez.
- 03:47'de pg_cron `purge-files` fonksiyonunu çağırır; fonksiyon kuyruktan 500'lük partiler alır
  (`file_cleanup_claim`), Storage API ile siler, silineni kuyruktan düşer (`file_cleanup_done`). Silinemeyen yol
  15 dk sonra yeniden denenir, 10 denemeden sonra kuyrukta bekler. Kuyruktayken bir satır o yolu yeniden
  kullanırsa (çöpten geri alma) dosya silinmez.
- Uygulama da kendi çöpünü boşaltırken dosyayı siler (`TrashService`); bu sunucu tarafı, onun kaçırdıklarını yakalar.
- Hesap silme (migration 17): `delete_account` sahibi olunan seyahatlerin klasörlerindeki **tüm** dosyaları (adı ne olursa
  olsun, yeni yüklenmiş olsa da) aynı işlemde kuyruğa yazar; dosyalar aynı gece silinir. Uygulamanın önce kendisinin
  silmesi yedek olarak durur, Storage'a ulaşamazsa hesap silme yine de tamamlanır. Gece taraması da artık var olmayan
  bir seyahatin klasöründe 1 günden eski ne kalmışsa kuyruğa koyar.

Kurulum (bir kez):

1. **Database → Extensions**: `pg_cron` ve `pg_net` açık olsun.
2. Bir gizli değer üret (ör. `openssl rand -hex 32`); depoya ya da sohbete yazma.
3. **Edge Functions → Secrets** → `FILE_CLEANUP_SECRET` = o değer.
4. **SQL Editor**: `select vault.create_secret('<o değer>', 'file_cleanup_secret');`
5. Fonksiyonu yayınla, **Verify JWT kapalı** (çağıran kullanıcı değil, kontrol gizli değerle yapılır):
   `npx supabase functions deploy purge-files --project-ref bobliztrprgqhnudnhrs --no-verify-jwt`
   (Dashboard'dan: adı `purge-files`, dosyalar `functions/purge-files/index.ts` ve `functions/purge-files/cleanup.ts`.)
6. Migration 16 ve 17'yi (ya da `all_in_one.sql`'i) çalıştır; cron işi `travelplanner-purge-files` kurulur.
7. Kontrol: `select count(*) from private.file_cleanup_queue;` ertesi sabah 0'a yakın olmalı;
   Edge Functions → purge-files → Logs'ta `purge-files {"deleted":…}` satırı görünür. Elle denemek için:
   `select net.http_post(url := 'https://bobliztrprgqhnudnhrs.supabase.co/functions/v1/purge-files', headers := jsonb_build_object('Authorization', 'Bearer ' || (select decrypted_secret from vault.decrypted_secrets where name = 'file_cleanup_secret')));`
8. Fonksiyonun testleri: `node --test supabase/functions/purge-files/*.test.ts`

## Yapay zekâ ile planlama (Edge Function `plan-trip`)

Uygulama planı doğrudan OpenAI'ye değil, Supabase'deki `plan-trip` fonksiyonuna sorar; OpenAI anahtarı yalnızca
Supabase'de durur.

**Kota ve maliyet sınırları** (ilk dördü yönetim panelinden, Ayarlar sayfasında değişir; migration 29. AI'ı bakım için durdurmak da oradan):

| Sınır | Varsayılan | Nerede |
| --- | --- | --- |
| Hesap başına günlük plan (aynı sayı cihaz başına da) | 5 | Panel → Ayarlar (`private.ai_daily_limit()`) |
| Yeni hesap, ilk 24 saat | 2 | Panel → Ayarlar (`private.ai_new_account_limit()`) |
| Tüm kullanıcılar için günlük toplam plan (aşılınca herkes 429) | 200 | Panel → Ayarlar (`private.ai_global_daily_limit()`) |
| Hesap başına günlük iade | 2 | Panel → Ayarlar (`private.ai_daily_refund_limit()`) |
| Plan cevabı için en çok çıktı token'ı | 16 000 (gezi uzunluğuna göre daha az) | `AI_MAX_TOKENS` secret'ı, `planTokenBudget` (planner.ts) |

İade yalnızca OpenAI hiç cevap vermediğinde yapılır (ağ hatası, HTTP hata kodu). Cevap geldiyse — kesilmiş,
boş, JSON olmayan ya da boş plan — veya 100 sn zaman aşımı dolduysa OpenAI faturaladığı için kota düşer.
Cihaz kimliği yalnızca yardımcı sinyaldir (uygulamanın gönderdiği bir değer); asıl koruma hesap kotası,
yeni hesap kotası ve global tavandır. Fonksiyon her OpenAI çağrısının gerçek token kullanımını
`openai usage …` satırıyla loglar (Edge Functions → plan-trip → Logs): `planTokenBudget` sayılarını bununla ayarla.

**Dashboard'da elle yapılacaklar (kod ile yapılamaz):**

- **OpenAI** → Settings → Limits (Organization → Limits): aylık bütçe / hard limit tanımla.
- **Supabase** → Authentication → Sign In / Providers → Email: **Confirm email** açık olsun.
- CAPTCHA: aşağıdaki "CAPTCHA (Cloudflare Turnstile)" bölümü.

1. SQL: `all_in_one.sql`'i (veya yalnızca migration 9, 10 ve 12'yi) SQL Editor'da çalıştır.
2. Anahtar: Dashboard → **Edge Functions → Secrets** → `OPENAI_API_KEY` = OpenAI anahtarın
   (isteğe bağlı `AI_MODEL`, varsayılan `gpt-5-mini`; `AI_REASONING`; `AI_MAX_TOKENS`, varsayılan 16000).
   **Model artık panelden seçilir** (Ayarlar → AI planlama → Model, migration 31): panelde seçilen model ve düşünme
   seviyesi secret'lardan önce gelir, boş bırakılırsa aşağıdaki secret kuralı geçerlidir. Fonksiyon her planda
   kullandığı modeli `model … reasoning …` satırıyla loglar. gpt-4.1 / gpt-4o gibi düşünmeyen modellere
   `reasoning_effort` gönderilmez.
   `AI_MODEL` tanımlı değilse kod `gpt-5-mini` kullanır (`functions/plan-trip/index.ts`): `gpt-5-nano` daha ucuzdur ama
   aynı yerleri tekrarlayan ve birbirinden uzak bölgeleri karıştıran planlar üretti. Maliyeti nano'ya göre hesapladıysan
   `AI_MODEL` secret'ını bilinçli olarak ayarla (ör. `gpt-5-nano`; nano'da `AI_REASONING` varsayılanı `minimal`, diğerlerinde `low`).
3. Fonksiyonu yayınla — iki yoldan biri:
   - **Dashboard:** Edge Functions → *Deploy a new function* → *Via Editor* → adı `plan-trip`;
     `functions/plan-trip/index.ts`, `functions/plan-trip/planner.ts`, `functions/plan-trip/openai.ts` ve `functions/plan-trip/places.ts` dosyalarını aynı adlarla ekle (openai.ts: OpenAI çağrısı ve istek gövdesi sınırı; places.ts: yerleri OpenStreetMap üzerinde doğrular, gerçek restoranları bulur);
     **Verify JWT** ayarını kapat (kimlik kontrolünü fonksiyon kendisi, kullanıcının oturumuyla yapar; JWT biçiminde
     `Authorization` olmayan istekler gövde okunmadan 401, 16 KB'tan büyük gövdeler 413 alır).
   - **CLI:** `npx supabase login` → proje klasöründe
     `npx supabase functions deploy plan-trip --project-ref bobliztrprgqhnudnhrs --no-verify-jwt`
4. Fonksiyonun testleri (Node 22.18+): `node --test supabase/functions/plan-trip/*.test.ts`

## CAPTCHA (Cloudflare Turnstile)

E-posta ile kayıt, şifreyle giriş, "kodu tekrar gönder" ve şifre sıfırlama, Cloudflare Turnstile ile korunur
(ücretsiz, istek sınırı yok). Google ile giriş, kod doğrulama ve oturum yenileme etkilenmez. Çoğu kullanıcı
hiçbir şeye dokunmadan geçer; Cloudflare şüphelenirse tek bir onay kutusu çıkar.

Uygulamada: `Views/CaptchaPage.xaml` (WebView), `Helpers/TurnstilePage.cs` (sayfanın HTML'i — bir yerde
barındırılmaz, uygulamanın içindedir), `Services/SupabaseAuthService.cs` (token'ı `gotrue_meta_security.captcha_token`
olarak gönderir). `SupabaseConfig.CaptchaSiteKey` boşken CAPTCHA kapalıdır ve uygulama eskisi gibi çalışır.

**Sıra önemli** — Supabase'de CAPTCHA açıldığı anda token göndermeyen (eski) uygulama sürümleri e-posta ile
kayıt olamaz ve giriş yapamaz:

1. **Cloudflare** (ücretsiz hesap) → **Turnstile** → **Add widget**:
   - Widget name: `TravelPlanner`
   - Hostnames: `emrullahakkus.com.tr` (alt alan adlarını da kapsar; `SupabaseConfig.CaptchaPageUrl` bu alan
     adında olmalı — sayfa orada barındırılmaz, sadece Turnstile'a o alan adı gösterilir)
   - Widget Mode: **Managed**
   - Kaydet → **Site Key** ve **Secret Key**'i kopyala.
2. **Uygulama:** `Helpers/SupabaseConfig.cs` → `CaptchaSiteKey = "<Site Key>"`. Derle, telefonda e-posta ile
   giriş yap: kısa bir "Güvenlik kontrolü" ekranı görünüp kendiliğinden kapanmalı. (Bu aşamada Supabase token'ı
   henüz kontrol etmez; ekran hata gösterirse çoğunlukla Hostnames listesi ya da site key yanlıştır.)
3. Yeni APK'yı dağıt; kullanıcıların güncellemesini bekle.
4. **Supabase** → Authentication → **Attack Protection** → **Enable Captcha protection** → Provider:
   **Turnstile by Cloudflare** → Secret Key'i yapıştır → **Save**.
5. Kontrol: e-posta ile giriş yap (çalışmalı). Secret key yanlışsa uygulama "Güvenlik kontrolü tamamlanamadı"
   der — o durumda 4. adımı kapatıp anahtarı düzelt.

Secret Key yalnızca Supabase'e girilir, uygulamaya ve depoya asla yazılmaz. Site Key herkese açıktır.
Gizlilik politikası, KVKK metni (uygulama içi ve `docs/`) ve web sitesindeki kopyalar Cloudflare (ABD)'yi listeler.
