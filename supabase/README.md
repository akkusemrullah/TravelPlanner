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
| `migrations/…09_ai_planner.sql` | Yapay zekâ planı: hesap ve cihaz başına günlük kota (`ai_plan_quota`) |
| `migrations/…10_ai_refund.sql` | OpenAI cevap veremezse planı geri veren tek kullanımlık fiş (`ai_plan_refund`) |
| `migrations/…11_value_limits.sql` | SEC-01: tutar, kur, tarih, kategori, sıra ve saat sınırları (uygulamadaki `InputLimits` ile aynı); sınır dışı yazma `invalid` ile reddedilir |
| `migrations/…12_ai_cost_limits.sql` | SEC-02: herkes için günlük global tavan, yeni hesaplara ilk 24 saatte düşük kota, hesap başına günde en fazla 2 iade |
| `migrations/…13_quota_limits.sql` | TP-3: hesap/seyahat başına satır sınırları, günlük yeni satır ve dakikalık senkron sınırı, `trip-files` için dosya türü ve boyut sınırları, `items`/`avatar_url` uzunlukları |
| `migrations/…14_invite_hardening.sql` | TP-4: davet kodları tek kullanımlık ve 48 saat geçerli, katılan görüntüleyici olur; yanlış kod denemesi sınırı; e-postalar yalnızca sahibe; yeni üye bildirimi için `get_recent_joins` |
| `migrations/…15_attachment_paths.sql` | TP-5: ekin `storage_path`'i yalnızca kendi dosyası (`trips/<trip_id>/<id>.<uzantı>`), `content_type`'ı yalnızca JPEG/PNG/HEIC/HEIF/WebP/PDF olabilir; aykırı yazma `invalid` ile reddedilir, eski bozuk değerler temizlenir |
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
psql -d tp -f tests/sync_tests.sql          # 177 kontrol, sonda "ALL SYNC TESTS PASSED"
```

## Yapay zekâ ile planlama (Edge Function `plan-trip`)

Uygulama planı doğrudan OpenAI'ye değil, Supabase'deki `plan-trip` fonksiyonuna sorar; OpenAI anahtarı yalnızca
Supabase'de durur.

**Kota ve maliyet sınırları** (değiştirmek için ilgili fonksiyonu düzenleyip dosyayı yeniden çalıştır):

| Sınır | Varsayılan | Nerede |
| --- | --- | --- |
| Hesap başına günlük plan (aynı sayı cihaz başına da) | 5 | `private.ai_daily_limit()` (migration 9) |
| Yeni hesap, ilk 24 saat | 2 | `private.ai_new_account_limit()` (migration 12) |
| Tüm kullanıcılar için günlük toplam plan (aşılınca herkes 429) | 200 | `private.ai_global_daily_limit()` (migration 12) |
| Hesap başına günlük iade | 2 | `private.ai_daily_refund_limit()` (migration 12) |
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
Gizlilik politikası ve KVKK metnine Cloudflare (ABD) eklendi; web sitesindeki kopyaları da güncelle.
