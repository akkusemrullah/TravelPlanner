# Güvenlik Politikası / Security Policy

## Güvenlik açığı bildirme (Vulnerability Disclosure)

Travel Planner'da (Android/iOS uygulaması, Supabase arka ucu, Edge Functions veya web sitesi) bir güvenlik açığı bulduysan lütfen **herkese açık bir issue açma**; doğrudan e-postayla bildir:

- **E-posta:** emrullahakkus@gmail.com (konu: `[SECURITY] Travel Planner`)
- Mümkünse ekle: etkilenen sürüm (Profil sayfasının altındaki sürüm satırı), adımlar, beklenen/gerçekleşen sonuç, kavram kanıtı, etki tahmini.

**Süreç ve süreler**
| Adım | Hedef süre |
| --- | --- |
| Alındı bildirimi | 3 iş günü |
| İlk değerlendirme ve önem derecesi | 7 gün |
| Düzeltme (kritik / yüksek) | 30 gün |
| Düzeltme (orta / düşük) | 90 gün |
| Koordineli açıklama | Düzeltme yayınlandıktan sonra, birlikte kararlaştırılan tarihte |

**Kapsam:** uygulama kodu, `supabase/migrations` (RLS, RPC'ler), `supabase/functions` (`plan-trip`, `purge-files`), `emrullahakkus.com.tr` alan adları.
**Kapsam dışı:** üçüncü taraf hizmetlerin (Supabase, OpenAI, Google, Cloudflare) kendi açıkları; hizmet engelleme (DoS) ve yük testleri; sosyal mühendislik; fiziksel erişim gerektiren senaryolar; root'lu/jailbreak cihazda cihaz içi veriye erişim.

**İyi niyetli araştırma:** Yalnızca kendi hesabın ve kendi verinle test et; başkalarının verisine erişme, değiştirme veya silme; kota ve hız sınırlarını kasıtlı olarak tüketme. Bu kurallara uyan iyi niyetli araştırmalar için yasal işlem başlatmayız. Ödül programı (bug bounty) yoktur; istersen teşekkür listesinde adın anılır.

## Gizli anahtarların korunması (Secrets handling)

Kurallar ve nerede durdukları için bkz. [.env.example](.env.example).

1. **Uygulamada gizli anahtar yok.** Uygulamada yalnızca herkese açık değerler bulunur: Supabase URL'si ve *publishable* anahtar (`Helpers/SupabaseConfig.cs`; verileri satır düzeyinde erişim kuralları korur) ve Turnstile *site* anahtarı. `service_role` anahtarı, OpenAI anahtarı, Turnstile *secret* anahtarı ve OAuth istemci sırları **asla** uygulamaya, depoya veya sohbetlere yazılmaz.
2. **Sunucu sırları** yalnızca Supabase → Edge Functions → Secrets'ta (`OPENAI_API_KEY`, `FILE_CLEANUP_SECRET`) ve Supabase Vault'ta (`file_cleanup_secret`) durur.
3. **İmzalama anahtarı** (`*.keystore`) git'e eklenmez (`.gitignore`); şifreleri `build-apk.ps1` her seferinde sorar, CI'da GitHub Secrets'ta durur (bkz. `docs/RELEASE.md`). Anahtarın kaybı uygulamanın güncellenememesi demektir: şifreli bir yedeğini depo dışında sakla.
4. `.env` ve `.env.*` dosyaları git'e eklenmez (yalnızca `.env.example`).
5. Bir sır sızarsa: hemen döndür (OpenAI'de yeni anahtar, Supabase'de yeni secret / JWT), eski değeri iptal et, günlükleri kontrol et ve kullanıcı verisi etkilendiyse KVKK md. 12 uyarınca 72 saat içinde Kurul'a ve ilgili kişilere bildir.

## Uygulanan güvenlik önlemleri (özet)

- Tüm trafik HTTPS/TLS; oturum anahtarları cihazın güvenli deposunda (Android Keystore / iOS Keychain).
- Sunucu: her tabloda RLS; istemci tablolara doğrudan yazamaz, yalnızca `apply_changes` RPC'si (rol kontrolü, değer sınırları, kota ve hız sınırları; reddedilen değişiklikte yalnızca kısa hata kodu – TP-14).
- Banlanan veya silinen hesaplar her RPC'de anında reddedilir; `account_status` yalnızca kendi hesabını sorar.
- Kayıt: Cloudflare Turnstile, e-posta doğrulama, yeni şifrelerde en az 8 karakter + harf ve rakam, tek kullanımlık e-posta alan adı engeli (TP-15).
- Dosyalar: yalnızca izinli türler (JPEG, PNG, HEIC/HEIF, WebP, PDF) ve boyutlar, yol doğrulaması; silinen dosyalar Storage'dan da temizlenir.
- Yapay zekâ: OpenAI anahtarı yalnızca Edge Function'da; hesap, cihaz ve global günlük kotalar.
- Cihaz: yerel veri Android yedeğine ve cihazdan cihaza aktarıma dahil edilmez (TP-13); çıkışta yerel veriyi silme seçeneği (TP-16).

## English summary

Please report vulnerabilities privately to **emrullahakkus@gmail.com** (subject `[SECURITY] Travel Planner`), not in public issues. We acknowledge within 3 business days, assess within 7 days, aim to fix critical/high issues within 30 days and others within 90 days, and agree on a disclosure date with you. Test only with your own account and data; no DoS, no social engineering, no access to other people's data. Good-faith research following these rules won't face legal action. There is no bug bounty. Secrets never go into the app or the repository; see `.env.example` for where each one lives.
