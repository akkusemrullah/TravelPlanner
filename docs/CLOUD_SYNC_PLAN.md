# Bulut, hesap, senkron ve paylaşım planı

Bu belge TravelPlanner'a hesap sistemi, bulut yedekleme, offline senkron ve seyahat paylaşımı eklemek için iş planıdır. İşler gruplara ayrıldı; her grup tek seferde yaptırılabilir ve kendi testleriyle biter.

## Alınan kararlar

| Konu | Karar |
| --- | --- |
| Backend | Supabase (PostgreSQL + Auth + Storage + Realtime), bölge Frankfurt |
| Yerel veri | SQLite asıl kaynak olarak kalır (local-first). Kullanıcı başına ayrı dosya: `travelplanner_{userId}.db3` |
| Kimlikler | Yerelde `int Id` ve int ilişkiler (TripId …) kalır; her satıra cihazda üretilen `SyncId` (Guid) eklendi. Sunucu satırları yalnızca SyncId ile tanır; int ↔ SyncId eşlemesi `Services/Sync/SyncSchema` üzerinden yapılır (karar G1'de değişti: tam Guid geçişi 60+ dosyayı riske atıyordu) |
| Giriş | E-posta/şifre ve Google; iOS yayını olursa Apple. Facebook girişi yapılmayacak (karar 2026-10-10). Misafir modu var, ilk girişte veriler hesaba taşınır |
| Roller | `owner`, `editor`, `viewer` |
| Viewer | Sadece görür. Masraf dahil hiçbir şey ekleyemez |
| Editor | İçerik ekler, düzenler, siler. Üye ve yetki yönetemez, seyahati silemez |
| Silme | Yerelde satır silinir, kopyası JSON olarak `Trash` tablosuna gider (aynı işlemde silinenler tek `BatchId`); 30 gün geri alınabilir, sonra temizlenir. Ekin dosyası `attachments/trash/{SyncId}` klasöründe bekler. Sunucuda soft delete (`deleted_at`) |
| Listeler | Yapılacaklar ve bavul listesi ortak; her madde bir kişiye atanabilir (`AssignedTo`) |
| Kişisel veri | Bavul şablonları, bildirim ayarları, varsayılan para birimi |
| Davet | Link/kod ile (`https://<alan-adı>/invite/<kod>`), süreli |
| Offline değişiklik | Her yazma yerel tablo + `PendingChange` outbox kaydı, aynı transaction içinde |
| Çakışma | Alan bazında son yazan kazanır; silme düzenlemeye karşı kazanır; kaybedene bildirim |
| Sıralama | Sunucu `change_seq` (Postgres sequence). Cihaz saati sıralamada kullanılmaz |
| Sadece online | Davet, davet kabulü, rol değiştirme, üye çıkarma, seyahati silme, hesap işlemleri |

## Supabase projesi

| Bilgi | Değer |
| --- | --- |
| Project ref | `bobliztrprgqhnudnhrs` |
| Project URL | `https://bobliztrprgqhnudnhrs.supabase.co` |
| Bölge | Central EU (Frankfurt) |
| Publishable key | `sb_publishable_yiAjU7bdS3ObKRASN2bLcA_7LRNLbYh` (gizli değil; secret key depoya girmez) |
| Proje ayarları | Data API açık, "Automatically expose new tables" kapalı (GRANT'ler SQL'de), otomatik RLS açık |

## Gruplar

Bağımlılık: G1 hemen başlayabilir. G2, Supabase projesi açılınca. G3, G2 ve sağlayıcı ayarlarından sonra. G4, G1–G3'ten sonra. G5 ve G6, G4'ten sonra. G7 en son.

### G1 — Yerel veri altyapısı (backend gerekmez) — ✅ tamam (Windows'ta derlendi ve test edildi)
- `Models/SyncEntityBase.cs`: `ISyncEntity` + ortak alanlar `SyncId`, `CreatedBy`, `UpdatedBy`, `UpdatedAtUtc`, `Version`, `ChangeSeq`. Senkronlanan 9 model bundan türer (Trip, Todo, Expense, Place, Activity, Attachment, Participant, ExpenseShare, PackingTemplate).
- `Models/PendingChange.cs` (outbox) ve `Models/TrashItem.cs` (tablo `Trash`).
- `Services/Sync/SyncSchema.cs`: tablo adları, ebeveyn-önce sırası (Rank), int ilişkiler (FK) ve her satırın TripSyncId'si.
- `Services/Sync/ChangeTracker.cs`: tek yazma yolu. `InsertTracked/UpdateTracked/SaveTracked/DeleteTracked/DeleteAllTracked/RestoreTracked` (+ Async). Güncellemede yalnızca değişen alanlar kuyruğa yazılır, aynı satırın ardışık düzenlemeleri birleşir; silmede satır çöpe kopyalanır ve Delete kuyruğa girer (bekleyen upsert'in yerine, kuyruğun sonuna).
- Tüm servis yazmaları (Trip, Todo, Expense, Place, Activity, Split, Packing, Attachment) ChangeTracker'dan geçiyor; ham `UPDATE/DELETE` SQL'leri satır bazlı izlenen yazmalara çevrildi. Masraf bölüşümü artık sadece farkı yazıyor.
- Migration 3: mevcut her satıra SyncId verir ve tüm veriyi (ebeveyn önce) outbox'a koyar → ilk girişte her şey yüklenir.
- `TrashService` (+`ITrashService`): çöp listesi (kök öğeler), batch geri alma (orijinal Id/SyncId ile, eksik ebeveyn varsa atlanır, opsiyonel bağ kopuksa null'lanır), 30 gün sonra temizleme (uygulama açılışında).
- `TodoItem.AssignedToUserId`, `Participant.UserId`.
- Yedek formatı v2 (v1 hâlâ okunur); geri yüklemede outbox/çöp sıfırlanır, tüm satırlar yeniden kuyruğa girer.
- Testler: `Tests/.../Services/SyncTrackingTests.cs`.
- **G3'e taşındı:** kullanıcı başına veritabanı dosyası ve misafir verisinin hesaba taşınması (oturumla birlikte anlamlı).

**G4 için notlar (outbox sözleşmesi):** girdiler `Sequence` sırasıyla gönderilir; `ChangedFields = null` → tüm alanlar (yeni/geri alınmış satır), aksi halde virgüllü özellik adları; `BaseVersion` çakışma tespiti için; Delete her zaman gönderilir (sunucu bilinmeyen satırı silmeyi sessizce yok saymalı); int FK'ler gönderimde `SyncSchema.ForeignKeys` ile SyncId'ye çevrilir; sunucudan gelen satırlar ChangeTracker'ı değil ayrı bir "uzaktan uygula" yolunu kullanmalı (outbox'a yazmamalı) ve `Version/ChangeSeq` alanlarını doldurmalı; yerelde bekleyen alanın üzerine yazılmamalı. Yedekten geri yükleme buluta "birleştirme" olarak gider (yedekte olmayan sunucu satırları silinmez).

### G2 — Supabase şeması ve güvenlik (SQL, depoda `supabase/migrations/`) — ✅ tamam (Supabase'e uygulandı)
- Tablolar: `profiles`, `trips`, `trip_members`, `trip_invites` ve tüm içerik tabloları.
- `change_seq` ve `version` trigger'ları, `updated_at` sunucu saati.
- `has_trip_role()` fonksiyonu ve her tabloda RLS kuralları (viewer okur, editor yazar, owner yönetir).
- RPC'ler: `apply_changes(json)` (yetki + alan bazlı birleştirme + sürüm), `pull_changes(since_seq)`, `create_invite`, `revoke_invite`, `accept_invite`, `get_trip_members`, `set_member_role`, `remove_member`, `leave_trip`, `delete_account`. Seyahat silme `apply_changes` içindeki Trip delete (yalnızca owner).
- 30 günü geçen silinmiş kayıtları temizleyen zamanlanmış görev (pg_cron).
- Storage bucket `trip-files`, yol `trips/{tripId}/{attachmentId}.{ext}`, üyeliğe göre politika.
- SQL testleri: yetkisiz kullanıcı okuyamıyor/yazamıyor, viewer yazamıyor.
- Sözleşme ayrıntıları `supabase/migrations/…03_sync_rpc.sql` başındaki yorumda. **G4 için:** alan adları sunucuda snake_case (AmountMinor → `amount_minor`, TripId → üst seviye `trip_id`); int FK'ler SyncId'ye çevrilip gönderilir (`paid_by_participant_id`, `expense_id`, `participant_id`, `place_id`); `TripAttachment.RelativePath` gönderilmez (cihaza özel), sunucuda `storage_path` var (G5); tarih alanları `yyyy-MM-dd`, saatler `HH:mm:ss`, CreatedDate `timestamp` (saat dilimsiz). Pull'da `deleted_at` dolu satır = yerelde sil; `memberships`'te olmayan, daha önce senkronlanmış seyahat = yerelden kaldır.

### G3 — Hesap ve giriş — ✅ tamam (derlendi ve test edildi)
Yapılanlar:
- `Services/SupabaseAuthService.cs` (+`IAuthService`): ek SDK yok, Supabase Auth REST'i düz HttpClient ile. E-posta/şifre kaydı ve girişi; kayıt onayı ve şifre sıfırlama **e-postadaki kodla** (uygulamaya link dönmesi gerekmiyor); Google: `WebAuthenticator` + PKCE (`authorize` → `token?grant_type=pkce`); oturum `SecureStorage`'da (`tp.auth.session`), token süresi dolmadan 60 sn önce yenilenir, offline'da eski oturumla devam, sunucu refresh token'ı reddederse çıkış. `GetAccessTokenAsync()` G4 içindir.
- `Services/AccountService.cs` (+`IAccountService`): misafir = eski `travelplanner.db3` + `AppData/attachments`; hesap = `AppData/users/<uid>/travelplanner.db3` + `.../attachments`. İlk girişte misafirin seyahati varsa sorulur ("Hesabıma taşı" / "Bu cihazda kalsın"); taşınırsa dosyalar taşınır, outbox zaten dolu olduğu için G4 hepsini yükler. Hesap değişince hatırlatmalar iptal/yeniden planlanır, `ChangeTracker.CurrentUserId` ayarlanır, uygulama kabuğu (AppShell) yeniden kurulur. Çıkışta hesabın verisi cihazda kalır.
- `DatabaseService.SwitchDatabaseAsync/CloseAsync`, `AttachmentService.UseRootDirectory`.
- Ekran: `Views/AuthPage.xaml` + `AuthViewModel` (giriş / kayıt / kod doğrulama / şifremi unuttum / yeni şifre, Google, misafir devam). İlk açılışta bir kez gösterilir; Profil'de hesap kartı (giriş yap / çıkış yap).
- Platform: Android `WebAuthenticationCallbackActivity` + CustomTabs sorgusu; iOS/MacCatalyst `CFBundleURLTypes`. Windows'ta Google girişi desteklenmez (mesaj gösterilir), e-posta çalışır.
- Testler: `Tests/.../Services/AccountTests.cs` (auth servisi, hesap/misafir geçişi, giriş ekranı).
- Supabase panelinde gereken: "Confirm signup" ve "Reset password" e-posta şablonlarına `{{ .Token }}` eklenmesi.

Plan:
- (G1'den taşındı) Kullanıcı başına veritabanı dosyası + ekler klasörü; misafir verisinin ilk girişte hesaba taşınması. `ChangeTracker.CurrentUserId` giriş/çıkışta ayarlanır.
- `AuthService`: kayıt, giriş, e-posta doğrulama, şifre sıfırlama, çıkış, token yenileme; token'lar `SecureStorage`'da.
- Google (ve iOS'ta Apple) girişi: `WebAuthenticator` + PKCE, `com.eaksoft.travelplanner://auth-callback`.
- Android intent filter, iOS URL şeması.
- Ekranlar: hoş geldin / giriş / kayıt / şifremi unuttum / profil.
- Misafir modu; ilk girişte misafir verisinin hesaba taşınması.
- Offline açılışta kayıtlı oturumla devam.

### G4 — Senkron motoru — ✅ tamam (339 test geçti, eşitleme çalışıyor)
- `Services/Sync/SupabaseClient.cs`: RPC (`rest/v1/rpc/...`) ve Storage (`storage/v1/object/trip-files/...`) çağrıları, düz HttpClient + kullanıcının access token'ı.
- `Services/Sync/SyncFieldMapper.cs`: yerel satır ↔ sunucu alanları (snake_case; int ilişkiler ↔ SyncId; gün `yyyy-MM-dd`, saat `HH:mm:ss`; TripId üst seviye `trip_id`; `RelativePath` gönderilmez, `RemotePath` ↔ `storage_path`).
- `Services/Sync/SyncService.cs` (+`ISyncService`): bir tur = outbox'ı gönder (200'lük partiler, `apply_changes`) → dosyaları yükle (G5) → tekrar gönder → `pull_changes`. Başarılı değişiklik outbox'tan silinir (gönderilirken tekrar düzenlendiyse bir sonraki tura kalır), sunucunun version/change_seq'i yerel satıra yazılır (kendi değişikliğimiz geri gelince atlanır). `forbidden/row_deleted/trip_deleted/trip_mismatch` → değişiklik düşer ve baştan tam çekme yapılır (sunucu kazanır). `invalid/trip_missing` → deneme sayısı artar, 5 denemeden sonra 30 dk bekler.
- `Services/Sync/RemoteApplier.cs`: çekilen satırlar tek transaction'da, ChangeTracker'sız (kuyruğa/çöpe gitmez) yazılır; `deleted_at` → yerelde silinir; aynı/daha yeni change_seq → atlanır; yerelde bekleyen alanlar korunur; yerelde silinmiş (silmesi kuyrukta) satır geri getirilmez; üyelikten çıkarılan seyahat (daha önce senkronlanmışsa) yerelden kaldırılır; roller `TripMembership` tablosunda; imleç `SyncState` tablosunda (hesap veritabanı başına).
- Tetikleyiciler: değişiklikten 3 sn sonra (ChangeTracker.LocalChangeQueued), bağlantı gelince, uygulama öne gelince, 2 dakikada bir (ön plandayken), giriş/hesap değişince. Misafirde hiç çalışmaz.
- Değişen seyahatlerin hatırlatmaları yeniden planlanır; açık olan Ana sayfa / Seyahatler / Seyahat detayı sayfaları uzaktan değişiklik gelince yenilenir (`Helpers/RemoteChanges.cs`).
- Profil → hesap kartında durum ("Eşitlendi · 14:32", "3 değişiklik bekliyor", "Çevrimdışı") ve "Şimdi eşitle".
- Testler: `Tests/.../Services/SyncEngineTests.cs`.
- Ertelenenler: çakışma bildirimi arayüzü (şimdilik sadece sayılıyor) → G6; Realtime → G7.

### G5 — Belgelerin buluta taşınması — ✅ tamam
- `TripAttachment.RemotePath` (sunucuda `storage_path`). Satırı sunucuya gitmiş, dosyası gitmemiş ekler her turda `trips/<trip SyncId>/<ek SyncId>.<uzantı>` yoluna yüklenir; yol kaydedilip diğer cihazlara gönderilir.
- Diğer cihazda: satır gelir (dosya `attachments/<yerel trip id>/<SyncId>.<uzantı>` yolunda beklenir); belge açılınca indirilir, fotoğraflar ekler sayfası açılınca arka planda indirilir (`IAttachmentService.EnsureLocalFileAsync`).
- Ertelenenler: fotoğraf sıkıştırma (.NET 9 MAUI'de EXIF yönünü koruyan güvenli bir API yok; .NET 10'un MediaPicker seçenekleriyle eklenecek); silinen ek/seyahat dosyalarının Storage'dan temizlenmesi → G7'de yapıldı (aşağıda).

### G6 — Paylaşım ve yetkiler — ✅ yazıldı 2026-10-10, derleme/test ve iki hesapla deneme bekliyor
- **Yetki kontrolü tek yerde:** `ChangeTracker` her yazmada rolü `TripMembership` tablosundan okur; viewer hiçbir şey değiştiremez (`TripAccessException` ReadOnly), seyahati yalnızca owner silebilir (OwnerOnly). Misafir ve hiç paylaşılmamış seyahat = owner. `ViewModelBase.RunSafeAsync` bu hatayı anlaşılır bir mesajla gösterir. Sunucu da aynı kuralları ayrıca uygular (G2).
- `Services/TripAccessService.cs`: rol/yetki bilgisi (`TripAccessInfo`), son değişiklik (seyahat ve tüm satırları arasında en yeni `UpdatedBy/UpdatedAtUtc`), isimler için `KnownUser` tablosu (üye listesinden doldurulur, offline da çalışır). `TripAccess.Current` ile tüm ViewModel'ler `CanEdit/IsReadOnly` alır.
- `Services/SharingService.cs`: `create_invite` (seyahat buluta gitmemişse önce eşitler), `accept_invite` (kod normalize edilir, sonra eşitleyip seyahati bulur), `get_trip_members`, `set_member_role`, `remove_member`, `leave_trip`. Hepsi sadece online; hata türleri `SharingErrorKind` (Offline, NotSignedIn, NotUploaded, InvalidInvite, Forbidden). `SupabaseClient` artık sunucu hata kodunu (`P0002`, `42501`) taşıyor.
- Ekranlar: **Üyeler ve davet** (`MembersPage`, seyahat detayından): üye listesi ve rolleri, owner için "Davet kodu oluştur" (kod + paylaşma menüsü; TP-4'ten beri tek kullanımlık, 48 saat geçerli, katılan görüntüleyici olur) ve "Birden çok kişi için kod" (en fazla 10 kişi), üyeye dokunup rol değiştirme/çıkarma, owner olmayanlar için "Seyahatten ayrıl". **Çöp kutusu** (`TrashPage`, Profil → Veriler): 30 gün içinde silinenler, kalan gün, tek dokunuşla geri al. **Profil → Davet koduyla katıl**.
- Seyahat detayı: "Paylaşım" bölümü (rolün + "Son değişiklik: Ayşe · 5 dk önce"), viewer için "yalnızca görüntüleyebilirsin" bandı ve düzenle butonu gizli, sil butonu sadece owner'da; paylaşılmış seyahati silmek internet ister.
- Viewer için ekleme butonları gizli: yapılacaklar, masraflar, yerler, plan, belgeler, katılımcılar.
- Masraf bölüşümü: "Seyahat üyelerini ekle" — üyeler hesaplarına bağlı katılımcı olur; aynı isimde hesapsız katılımcı varsa bağlanır, isimler benzersiz kalır ("Can (2)").
- Senkron: sunucu bir değişikliği kesin reddederse (silinmiş / rol değişmiş) kullanıcıya bir kez bildirilir (`ISyncService.ChangesRejected`). TP-6: değişiklik cihazda geri alınır; sunucuda olan satır `ChangeSeq = -1` ile işaretlenir ve tam pull'da sunucu kopyası mutlaka uygulanır, sunucuya hiç ulaşmamış yeni satır (`ChangeSeq = 0`) çocuklarıyla ve dosyasıyla silinir. Bildirim hangi kaydın neden geri alındığını söyler (`SyncRejectionMessage`).
- Testler: `Tests/.../Services/SharingTests.cs`. Statik `ChangeTracker.CurrentUserId`'yi değiştiren testler `SignedInUser` koleksiyonunda tek başına çalışır.
- **Ertelendi (alan adı gerekiyor):** tıklanabilir `https://<alan-adı>/invite/<kod>` linki ve App Links / Universal Links. Şimdilik kod paylaşılıyor.
- Sunucu tarafında (SQL) değişiklik gerekmedi.

### G7 — Canlı güncelleme ve yayın hazırlığı
- Supabase Realtime: açık seyahatte değişiklik sinyali gelince çekme.
- Uygulama içi hesap silme (veriler + dosyalar). ✅ Yazıldı: Profil → "Hesabımı sil" (iki onay, "SİL" yazdırılır, yalnızca çevrimiçi). Sahibi olunan seyahatlerin dosyaları Storage API ile silinir, `delete_account` (migration 7) seyahatleri ve kullanıcıyı siler, başkalarının seyahatine yüklenen dosyaların sahipliğini boşaltır; cihazdaki hesap klasörü silinir, uygulama misafir olarak devam eder. **Supabase'de migration 7 çalıştırılmalı.**
- Admin tarafından silinen / banlanan hesap: ✅ Yazıldı (migration 8). Uygulama açılışta ve öne gelince `account_status` sorar; silinmiş/banlı/oturumu iptal edilmiş hesapta çıkış yapıp nedenini söyler. Banlı hesabın RPC, tablo ve Storage erişimi hemen kesilir; Supabase giriş yaptırmaz (`user_banned`). Ban: Dashboard → Authentication → Users → Ban user, ya da SQL'de `select private.ban_user('e-posta');` / `private.unban_user`.
- Silinen dosyaların Storage'dan temizlenmesi: ✅ Yazıldı (G7, migration 16 + Edge Function `purge-files`). Satırı kalıcı silinen (30 gün çöpten sonra, seyahatin temizliği, hesap silme) ya da dosya yolu değişen ekin dosyası kuyruğa girer; hiçbir satıra ait olmayan 1 günden eski dosyalar da. Her gece 03:47'de pg_cron fonksiyonu çağırır, fonksiyon Storage API ile siler. Çöpteki ekin dosyası 30 gün kalır. **Supabase'de: pg_net açılmalı, `FILE_CLEANUP_SECRET` (Edge secret) ve aynı değerle Vault'ta `file_cleanup_secret` oluşturulmalı, fonksiyon `--no-verify-jwt` ile yayınlanmalı, migration 16 çalıştırılmalı** (`supabase/README.md`).
- `PRIVACY.md`, KVKK aydınlatma metni, Play *Veri güvenliği* formu cevapları.
- Uçtan uca test listesi (iki hesap, iki cihaz, offline senaryolar).

## Emrullah'ın yapacakları

Gizli anahtarlar (Supabase `service_role`/secret key, veritabanı şifresi, Google/Facebook client secret, Apple .p8) sohbete veya depoya girmez; bunları doğrudan ilgili panele girersin.

### 1. Supabase (G2 öncesi)
- [ ] supabase.com'da hesap aç, yeni proje: ad `travelplanner`, bölge **Central EU (Frankfurt)**, güçlü veritabanı şifresi (parola yöneticisine kaydet).
- [ ] Authentication → URL Configuration: Redirect URLs'e `com.eaksoft.travelplanner://auth-callback` ekle.
- [ ] Authentication → Email: "Confirm email" açık.
- [ ] Bana ver: **Project URL** ve **anon / publishable key**.
- [ ] SQL dosyalarını sen çalıştıracaksın: SQL Editor'a yapıştırarak ya da Supabase CLI ile (`supabase link`, `supabase db push`). Komutları hazır vereceğim.

### 2. E-posta gönderimi (yayından önce)
- [ ] Supabase'in dahili e-postası saatte çok az mail gönderir; Resend veya Brevo'da ücretsiz hesap aç, alan adını doğrula, SMTP bilgilerini Supabase → Authentication → SMTP'ye gir.

### 3. Google girişi (G3 öncesi)
- [ ] Google Cloud Console'da proje aç.
- [ ] OAuth consent screen: uygulama adı, destek e-postası, gizlilik politikası linki, alan adı.
- [ ] Credentials → OAuth client ID, tür **Web application**, Authorized redirect URI: `https://<proje-ref>.supabase.co/auth/v1/callback`.
- [x] Client ID ve Client secret'ı Supabase → Authentication → Providers → Google'a gir.
- [x] Google provider açık (2026-10-10). Google Auth Platform: Testing modunda, test kullanıcıları ekli; logo/gizlilik linki yayından önce eklenecek.

### 5. Apple girişi (sadece iOS'ta yayınlanacaksa)
- [ ] Apple Developer Program üyeliği.
- [ ] Services ID, Sign in with Apple anahtarı (.p8); Supabase → Providers → Apple'a gir. Apple secret'ı 6 ayda bir yenilenmeli.
- [ ] Bana ver: Team ID ve iOS'un planda olup olmadığı.

### 6. Alan adı ve web sayfaları (G6 öncesi, gizlilik için daha erken)
- [ ] Bir alan adı al (davet linklerinin WhatsApp gibi uygulamalarda tıklanabilir olması için `https` link gerekir).
- [ ] Cloudflare Pages veya GitHub Pages ile ücretsiz barındırma.
- [ ] Bu sitede yer alacaklar (içeriklerini ben hazırlarım): gizlilik politikası, hesap silme talebi sayfası (Google Play zorunlu tutuyor), davet açılış sayfası, `/.well-known/assetlinks.json` ve `apple-app-site-association`.
- [ ] Bana ver: alan adı, Play Console → App signing'den **SHA-256 parmak izleri** (yükleme ve uygulama imzalama anahtarı).

### 7. Mağaza ve yasal (G7)
- [ ] Play Console *Veri güvenliği* formunu güncelle (cevapları hazırlarım).
- [ ] Gizlilik politikası ve KVKK metnini yayınla; yurt dışında veri saklama için bir uzmana danış.
- [ ] Canlıya çıkarken Supabase Pro plana geç (ücretsiz projeler kullanılmayınca duraklatılıyor; günlük yedek de Pro'da).

### 8. Test
- [ ] Paylaşım testleri için ikinci bir test hesabı ve mümkünse ikinci cihaz/emülatör.

## Bana verilecekler özeti

| Bilgi | Ne zaman | Gizli mi |
| --- | --- | --- |
| Supabase Project URL | G2 | Hayır |
| Supabase anon / publishable key | G3 | Hayır (uygulamada zaten görünür) |
| Google provider açıldı bilgisi | G3 | ✔ verildi |
| iOS yayını olacak mı, Apple Team ID | G3 | Hayır |
| Alan adı | G6 | Hayır |
| Android SHA-256 parmak izleri | G6 | Hayır |
