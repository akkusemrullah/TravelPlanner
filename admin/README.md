# TravelPlanner yönetim paneli

Küçük bir web paneli (ASP.NET Core Razor Pages, .NET 10). Telefonda da çalışır.

- **Genel:** kullanıcı, seyahat, bugünkü AI planı ve Keşfet sayıları.
- **Keşfet:** tüm planlar (taslak ve arşiv dahil), form ile ekleme/düzenleme, tam JSON yapıştırma, yayınla/taslağa al/arşivle,
  kapak ve liste resmi yükleme (`explore-images` kovası).
- **Kullanıcılar:** e-posta/ad ile arama, bugünkü AI kullanımı ve hakkı, son 14 gün; hesaba özel günlük AI hakkı (0–100,
  boş = varsayılan) ve "bugünü sıfırla".

## Güvenlik

- Supabase'e yalnızca sunucu konuşur, **service key** ile. Anahtar tarayıcıya hiç gitmez, depoya yazılmaz
  (user-secrets ya da ortam değişkeni).
- Panel yalnızca migration 25'teki `admin_*` RPC'lerini çağırır; bunları yalnızca `service_role` çalıştırabilir.
- Giriş: `Admin:Emails` listesindeki bir adres + o hesabın Supabase şifresi (uygulamadaki e-posta/şifre). Listeden çıkarılan
  adres bir sonraki istekte dışarıda kalır. Oturum 8 saat; giriş sayfası IP başına dakikada 10 istekle sınırlı.
- Çerez yalnızca HTTPS, `HttpOnly`, `SameSite=Strict`; formlar antiforgery korumalı; sıkı CSP.

## Kurulum (bir kez)

1. Supabase SQL Editor'da `supabase/migrations/20261018000025_admin_panel.sql`'i çalıştır (ya da `all_in_one.sql`'i).
2. Anahtar ve izinli e-posta (Dashboard → Project Settings → API Keys → **secret** / `service_role` anahtarı):

   ```powershell
   cd admin/src/TravelPlanner.Admin
   dotnet user-secrets set "Supabase:ServiceKey" "<service key>"
   dotnet user-secrets set "Admin:Emails:0" "emrullahakkus@gmail.com"
   ```

   İkinci bir yönetici için `Admin:Emails:1`. Supabase adresi `appsettings.json`'da (herkese açık bilgi).
3. Hesabın şifresi yoksa (yalnızca Google ile girdiysen) uygulamada "Şifremi unuttum" ile bir şifre belirle.

## Çalıştırma

```powershell
cd admin/src/TravelPlanner.Admin
dotnet dev-certs https --trust   # ilk seferde, yerel HTTPS sertifikası
dotnet run
```

Tarayıcıda <https://localhost:7180>. Testler: `dotnet test admin/TravelPlanner.Admin.slnx`.

## Notlar

- Eksik ayar varsa panel açılmaz ve hangi ayarın eksik olduğunu yazar.
- Yeni bir RPC Supabase'de "bulunamadı" derse SQL Editor'da `notify pgrst, 'reload schema';` çalıştır.
- Uygulama Keşfet görsellerini 7 gün önbellekte tutar: aynı adla yeni görsel yüklersen telefonlarda geç yenilenebilir.
- İnternete açmak istersen (ör. bir sunucuya koymak) yalnızca HTTPS arkasında yayınla; anahtarı ortam değişkeni
  `Supabase__ServiceKey`, e-postaları `Admin__Emails__0` ile ver.
