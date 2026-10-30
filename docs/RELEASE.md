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

**İmzalı APK (Windows, en kolayı):** keystore proje kökündeyse (`travelplanner.keystore`)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-signed-apk.ps1
```

Betik şifreleri gizli sorar, önce şifreyi ve alias'ı doğrular, eski Release çıktısını siler (yoksa MSBuild eski, debug imzalı APK'yı kopyalayabilir) ve `publish\com.eaksoft.travelplanner-Signed.apk` üretir. Keystore başka yerdeyse: `-KeyStore C:\yol\travelplanner.keystore`.

**Play Store için imzalı `.aab` (Windows, en kolayı):** aynı betik `-Aab` ile App Bundle üretir; `-VersionCode` her yüklemede bir öncekinden büyük olmalıdır (ilk yükleme 1, sonra 2, 3…):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-signed-apk.ps1 -Aab -VersionCode 1
```

Çıktı: `publish\com.eaksoft.travelplanner-Signed.aab`. Play Console'a bu dosya yüklenir.

**Elle (Play Store için `.aab`, csproj varsayılanı; APK için `-p:AndroidPackageFormat=apk` ekleyin):**

```bash
dotnet publish TravelPlanner.csproj -c Release -f net10.0-android \
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
- [ ] Play Console: gizlilik politikası ve hesap silme bağlantıları doğru (aşağıdaki tablo), *Veri güvenliği* formu [store/DATA_SAFETY.md](store/DATA_SAFETY.md) ile uyumlu
- [ ] `versionCode` (`ApplicationVersion`) Play'e yüklenen son sürümden büyük
- [ ] İletişim bilgileri doğru (`Helpers/AppConstants.cs` → `SupportEmail`, `DataController*`)
- [ ] Git etiketi atıldı

## Yasal sayfalar (tanıtım sitesi)

Yasal metinlerin yayındaki hâli tanıtım sitesindedir (depo `akkusemrullah/akkusemrullah.github.io`, GitHub Pages; aynı dosyalar `https://emrullahakkus.com.tr/travelplanner/` altında da yayınlanır). Play Console'a her zaman güncel olan GitHub Pages adreslerini girin:

| Sayfa | Adres | Nerede kullanılır |
| --- | --- | --- |
| Web sitesi | `https://akkusemrullah.github.io/` | Play Console → *Mağaza ayarları → Web sitesi* |
| Gizlilik politikası | `https://akkusemrullah.github.io/gizlilik-politikasi.html` (EN: `privacy-policy.html`) | Play Console → *Uygulama içeriği → Gizlilik politikası*, App Store |
| Hesap ve veri silme | `https://akkusemrullah.github.io/hesap-ve-veri-silme.html` | Play Console → *Veri güvenliği → Hesap silme bağlantısı* |
| KVKK aydınlatma metni | `https://akkusemrullah.github.io/kvkk-aydinlatma-metni.html` | Mağaza açıklaması / web sitesi |
| Kullanım koşulları | `https://akkusemrullah.github.io/kullanim-kosullari.html` | Mağaza açıklaması / web sitesi |
| Yapay zekâ uyarısı | `https://akkusemrullah.github.io/yapay-zeka-feragatnamesi.html` | AI içerik politikası |
| Tüm yasal belgeler | `https://akkusemrullah.github.io/yasal.html` | Web sitesi |

`docs/` altındaki `.md` dosyaları kaynak/arşiv kopyasıdır; metin değişince site deposundaki `src/pages/*.html` de güncellenir.

## Play Console *Veri güvenliği* formu

Form cevapları ve App Store gizlilik etiketleri: [store/DATA_SAFETY.md](store/DATA_SAFETY.md).

## KVKK notları

- Veri sorumlusu bilgileri `Helpers/AppConstants.cs` içindedir; uygulama içi ve `docs/` metinleri bunları kullanır.
- Yurt dışına aktarım (OpenAI – ABD, Supabase): KVKK md. 9 kapsamında hizmet sağlayıcıların standart sözleşmelerinin imzalanması ve gerekiyorsa Kurul'a bildirilmesi veri sorumlusunun sorumluluğundadır. Metinleri yayınlamadan önce bir hukukçuya kontrol ettirmen önerilir.
