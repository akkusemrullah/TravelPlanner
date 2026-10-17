# Yayın rehberi (TP-506 / TP-507)

## Sürümleme

| Alan | Nerede | Kural |
| --- | --- | --- |
| `ApplicationDisplayVersion` | `TravelPlanner.csproj` | Kullanıcının gördüğü sürüm, **SemVer** `MAJOR.MINOR.PATCH` (ör. `1.2.0`). Her mağaza sürümünde elle artırılır. |
| `ApplicationVersion` | CI | Mağaza yapı numarası (Android `versionCode`, iOS `CFBundleVersion`). Her yüklemede **artmak zorunda**; CI bunu GitHub Actions çalışma numarasıyla verir (`-p:ApplicationVersion=${{ github.run_number }}`). |

- **MAJOR:** geriye dönük uyumsuz değişiklik (ör. yedek formatı değişir, eski yedek açılamaz).
- **MINOR:** yeni özellik (yeni sprint).
- **PATCH:** yalnızca hata düzeltmesi.
- Her sürüm için git etiketi: `git tag v1.2.0 && git push origin v1.2.0`.
- Veritabanı şeması değiştiyse `DatabaseService.Migrations` listesine yeni adım eklenir (yayınlanmış adım değiştirilmez).
- Yedek formatı değiştiyse `BackupService.FormatVersion` artırılır ve eski sürümü okuyabildiği doğrulanır.

## Android imzalama anahtarı (keystore)

Keystore **asla** depoya girmez (`.gitignore`: `*.keystore`, `*.jks`). Kaybedilirse aynı uygulamaya güncelleme yayınlanamaz; Play App Signing kullanılsa bile yükleme anahtarı korunmalıdır.

### 1. Anahtarı oluşturma (bir kez)

```bash
keytool -genkeypair -v -keystore travelplanner.keystore -alias travelplanner \
        -keyalg RSA -keysize 4096 -validity 10000
```

### 2. Güvenli saklama

- Dosyayı ve şifreleri bir **parola yöneticisinde** (Bitwarden, 1Password, KeePass…) saklayın; en az bir çevrimdışı yedek (şifreli USB) tutun.
- Play Console'da **Play App Signing**'i açın: Google uygulama imzalama anahtarını saklar, sizin keystore'unuz yalnızca *yükleme anahtarı* olur ve kaybolursa sıfırlatılabilir.

### 3. GitHub secrets

Depo → *Settings → Secrets and variables → Actions* altında:

| Secret | Değer |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | `base64 -w0 travelplanner.keystore` çıktısı (Windows: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("travelplanner.keystore"))`) |
| `ANDROID_KEY_ALIAS` | `travelplanner` |
| `ANDROID_KEYSTORE_PASSWORD` | keystore şifresi |
| `ANDROID_KEY_PASSWORD` | anahtar şifresi |

CI keystore'u yalnızca `main` derlemesinde geçici klasöre açar ve iş bitince siler. Pull request'ler imzasız derlenir (fork'lardan gelen PR'lar secret'lara erişemez).

### 4. Yerelde imzalı paket (gerekirse)

```bash
dotnet publish TravelPlanner.csproj -c Release -f net9.0-android -p:TargetFrameworks=net9.0-android \
  -p:AndroidKeyStore=true -p:AndroidSigningKeyStore=/güvenli/yol/travelplanner.keystore \
  -p:AndroidSigningKeyAlias=travelplanner \
  -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASSWORD -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASSWORD
```

Şifreleri komut satırına yazmak yerine `env:` ile ortam değişkeninden verin (shell geçmişine düşmez).

## CI/CD (`.github/workflows/ci.yml`)

1. **Unit tests** – her PR ve push'ta `dotnet test` (Ubuntu), sonuçlar ve kapsam artifact olarak yüklenir.
2. **Android build** – testler geçerse `maui-android` workload'u ile derleme. PR'da imzasız derleme; `main`'de imzalı `.aab` üretilir ve `travelplanner-android-<run>` artifact'ı olarak indirilebilir.

Play Console'a otomatik yükleme bilinçli olarak eklenmedi; ilk sürümler elle yüklenmeli (iç test kanalı). İstenirse ücretsiz `r0adkll/upload-google-play` action'ı ve bir hizmet hesabı ile eklenebilir.

## Sürüm kontrol listesi

- [ ] `ApplicationDisplayVersion` artırıldı, `README.md` güncellendi
- [ ] Testler yeşil, emülatörde ve gerçek cihazda duman testi (seyahat, plan, harcama, belge ekleme, yedek al/geri yükle, PDF paylaş, harita)
- [ ] Açık ve koyu temada ekranlar kontrol edildi
- [ ] Mağaza metinleri ve ekran görüntüleri güncel (`docs/store/`)
- [ ] Yasal metinler güncel ve yayında: `docs/PRIVACY.md`, `docs/KVKK.md`, `docs/TERMS.md`, `docs/ACCOUNT_DELETION.md`, `docs/HELP.md` (uygulamadaki metinlerle aynı; metin değiştiyse `DocumentText.LastUpdated` da güncellendi)
- [ ] Play Console: gizlilik politikası ve hesap silme bağlantıları doğru, *Veri güvenliği* formu aşağıdaki tabloya uygun
- [ ] İletişim bilgileri doğru (`Helpers/AppConstants.cs` → `SupportEmail`, `DataController*`)
- [ ] Git etiketi atıldı

## Yasal sayfaların yayınlanması (GitHub Pages)

Depo → *Settings → Pages* → *Deploy from a branch* → `main` / `/docs`. Birkaç dakika sonra sayfalar şu adreslerde açılır:

| Sayfa | Adres | Nerede kullanılır |
| --- | --- | --- |
| Gizlilik politikası | `https://<kullanıcı>.github.io/TravelPlanner/PRIVACY` | Play Console → *Uygulama içeriği → Gizlilik politikası*, App Store |
| Hesap ve veri silme | `https://<kullanıcı>.github.io/TravelPlanner/ACCOUNT_DELETION` | Play Console → *Veri güvenliği → Hesap silme bağlantısı* |
| KVKK aydınlatma metni | `https://<kullanıcı>.github.io/TravelPlanner/KVKK` | Mağaza açıklaması / web sitesi |
| Kullanım koşulları | `https://<kullanıcı>.github.io/TravelPlanner/TERMS` | Mağaza açıklaması / web sitesi |
| Yardım merkezi | `https://<kullanıcı>.github.io/TravelPlanner/HELP` | Play Console → *Mağaza ayarları → Web sitesi* |

## Play Console *Veri güvenliği* formu

Uygulama hesap olmadan da çalışır; aşağıdakiler yalnızca (isteğe bağlı) hesapla toplanır. Yapay zekâ (OpenAI) ve barındırma (Supabase) hizmet sağlayıcısı olarak bizim adımıza işlediği, seyahat üyeleriyle paylaşımı da kullanıcı başlattığı için Play'in tanımına göre **"paylaşılan" değil, yalnızca "toplanan"** veridir.

| Veri türü (Play) | Toplanıyor | Zorunlu / isteğe bağlı | Amaç |
| --- | --- | --- | --- |
| Kişisel bilgiler → Ad | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi |
| Kişisel bilgiler → E-posta adresi | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi |
| Kişisel bilgiler → Kullanıcı kimlikleri | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi |
| Finansal bilgiler → Diğer finansal bilgiler (girilen harcama kayıtları) | Evet | İsteğe bağlı | Uygulama işlevselliği |
| Fotoğraflar ve videolar → Fotoğraflar | Evet | İsteğe bağlı | Uygulama işlevselliği |
| Dosyalar ve belgeler | Evet | İsteğe bağlı | Uygulama işlevselliği |
| Uygulama etkinliği → Kullanıcının oluşturduğu diğer içerik (seyahat, plan, notlar, yapay zekâ cevapları) | Evet | İsteğe bağlı | Uygulama işlevselliği |
| Cihaz veya diğer kimlikler (yapay zekâ kotası için SHA-256 özet) | Evet | İsteğe bağlı (yapay zekâ) | Dolandırıcılığı önleme, güvenlik ve uyumluluk |
| Konum | **Hayır** (cihaz konumu alınmaz; yerler kullanıcı tarafından seçilir) | – | – |

- Veriler aktarım sırasında şifreleniyor: **Evet** (HTTPS/TLS).
- Kullanıcılar verilerinin silinmesini isteyebilir: **Evet** – uygulama içinde *Profil → Hesabımı sil*, çöp kutusunda *Kalıcı olarak sil*, ayrıca `ACCOUNT_DELETION` sayfası.
- Reklam: **Yok**. Hedef kitle: **13 yaş ve üzeri** (çocuklara yönelik değil).
- Yapay zekâ ile üretilen içerik: kullanıcılar uygunsuz öneriyi plan ekranındaki **Bildir** bağlantısıyla bildirebilir (Google Play AI-Generated Content politikası).

## KVKK notları

- Veri sorumlusu bilgileri `Helpers/AppConstants.cs` içindedir; uygulama içi ve `docs/` metinleri bunları kullanır.
- Yurt dışına aktarım (OpenAI – ABD, Supabase): KVKK md. 9 kapsamında hizmet sağlayıcıların standart sözleşmelerinin imzalanması ve gerekiyorsa Kurul'a bildirilmesi veri sorumlusunun sorumluluğundadır. Metinleri yayınlamadan önce bir hukukçuya kontrol ettirmen önerilir.
