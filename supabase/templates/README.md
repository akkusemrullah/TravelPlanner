# Auth e-posta şablonları

TravelPlanner renkleri (Primary `#1E3A8A`, Secondary `#E6EDFC`) ve logosuyla, Türkçe Supabase Auth şablonları.
Şablonları `build.py` üretir; değiştirmek için `build.py`'yi düzenleyip `python build.py` çalıştır.

Logo: `logo.png`, GitHub'daki public repodan okunur
(`https://raw.githubusercontent.com/akkusemrullah/TravelPlanner/main/supabase/templates/logo.png`).
Repo private olursa ya da adı değişirse, `logo.png`'yi siteye yükleyip `build.py`'deki `LOGO` adresini değiştir.

## Supabase'e yapıştırma

Dashboard > Authentication > Emails > Templates. Her şablon için **Subject** alanına aşağıdaki konuyu,
**Message body** alanına (Source sekmesi) ilgili `.html` dosyasının tamamını yapıştır ve **Save**.

| Supabase şablonu | Dosya | Konu (Subject) |
|---|---|---|
| Confirm sign up | `confirm_signup.html` | TravelPlanner doğrulama kodun |
| Invite user | `invite.html` | TravelPlanner'a davet edildin |
| Magic link | `magic_link.html` | TravelPlanner giriş kodun |
| Change email address | `change_email.html` | E-posta değişikliğini onayla |
| Reset password | `reset_password.html` | TravelPlanner şifre sıfırlama kodun |
| Reauthentication | `reauthentication.html` | TravelPlanner onay kodun |
| Security > Password changed (bildirim açıksa) | `password_changed.html` | TravelPlanner şifren değişti |

Uygulama kayıt ve şifre sıfırlamada **kod** kullanır (`{{ .Token }}`); bu yüzden kod kutusu şablonların merkezinde.
Gönderen adı "TravelPlanner" kalabilir (SMTP ayarlarında Sender name).
