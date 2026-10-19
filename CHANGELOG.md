# Değişiklik Günlüğü / Changelog

Biçim: [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/), sürümleme: SemVer (`ApplicationDisplayVersion`). Kullanıcıya gösterilen kısa metinler: [docs/store/RELEASE_NOTES.md](docs/store/RELEASE_NOTES.md). Sunucu değişiklikleri `supabase/migrations/` numaralarıyla belirtilir; her biri Supabase SQL Editor'da elle çalıştırılır.

## [Yayınlanmadı / Unreleased] – 1.0.0

### Eklendi
- Ana sayfada gezilen şehir sayısı. Silinen seyahatin davet kodları iptal edilir, ölü kodda net mesaj gösterilir ve yanlış deneme sayılmaz; paylaşılan seyahat telefondan kalkınca üyeye bildirilir (migration 28).
- Yönetim paneli için sunucu fonksiyonları (migration 25, panel ayrı private depo `TravelPlanner.Admin`): Keşfet yönetimi, kullanıcı ve seyahat görüntüleme, hesaba özel günlük AI hakkı.
- Keşfet sekmesi: admin'in hazırladığı örnek seyahatler; arama, filtre, sonsuz kaydırma, detay, "Ekle" ve "Düzenle ve ekle" ile kendi seyahatlerine bağımsız kopya (TP-33, migration 23, içerik: `docs/EXPLORE_CONTENT.md`).
- Paylaşılan seyahat içeren yedek geri yüklenirken atla / kopya olarak yükle / vazgeç seçimi (TP-10).
- Para birimi değişimi sunucuda tek işlemde (`convert_trip_currency`), masrafta tutarın para birimi (TP-11, migration 19).
- Hesap silerken paylaşılan seyahatin sahipliğini devretme (`transfer_trip_ownership`, TP-12, migration 20).
- Çıkışta "bu cihazdaki verilerimi sil" seçeneği ve gönderilmemiş değişiklik uyarısı (TP-16).
- Tek kullanımlık e-posta alan adlarıyla kaydı engelleyen Auth hook'u (TP-15, migration 22).
- Senkron cursor güvenliği, apply_changes zaman sınırları (TP-8, migration 18); hesap değişiminde senkron turunun durdurulması (TP-9).
- Silinen hesap ve eklerin dosyalarının Storage'dan temizlenmesi (TP-7, migrations 16–17; Edge Function `purge-files`).
- Reddedilen yerel değişikliğin cihazda geri alınması (TP-6); ek dosya yolu doğrulaması (TP-5, migration 15); davet sertleştirmesi (TP-4, migration 14); kota ve hız sınırları (TP-3, migration 13).
- Yasal ve teknik belgeler: açık rıza metni, yapay zekâ feragatnamesi, iade politikası, LICENSE, SECURITY, ARCHITECTURE, veri sözlüğü, veri güvenliği beyanı, üçüncü taraf lisansları (TP-25).
- Yapay zekâ ile planlama ve Google ile giriş öncesi açık rıza onayı, uygulama içi Açık Rıza Metni ve rızayı geri alma (TP-25).

### Değişti
- Yeni şifreler en az 8 karakter + harf ve rakam (TP-15).
- Reddedilen değişiklikte istemciye yalnızca kısa hata kodu; `account_status` yalnızca kendi hesabını sorar (TP-14, migration 21).
- Belgeler koda göre güncellendi (TP-17).

### Güvenlik
- Yerel veriler Android Google yedeğine ve cihazdan cihaza aktarıma dahil edilmiyor (TP-13).
- Sunucu ve istemcide değer doğrulaması (SEC-01, migration 11); yapay zekâ maliyet sınırları (SEC-02, migration 12).
