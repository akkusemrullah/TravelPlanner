# Değişiklik Günlüğü / Changelog

Biçim: [Keep a Changelog](https://keepachangelog.com/tr-TR/1.1.0/), sürümleme: SemVer (`ApplicationDisplayVersion`). Kullanıcıya gösterilen kısa metinler: [docs/store/RELEASE_NOTES.md](docs/store/RELEASE_NOTES.md). Sunucu değişiklikleri `supabase/migrations/` numaralarıyla belirtilir; her biri Supabase SQL Editor'da elle çalıştırılır.

## [Yayınlanmadı / Unreleased] – 1.0.0

### Eklendi
- Yeni ana sayfa ve alt menü (TP-106): mavi üst alanda geri sayım ("Roma'ya 12 gün kaldı", seyahatteyken "Roma'da 3. gün") ve beyaz biniş kartı, Plan / Görevler / Harita / Hava / Harcamalar kısayolları, "Seyahat takvimin" ve "Seyahat karnen" (seyahat, ülke, şehir). Seyahat yokken "Bir sonraki durağın neresi?" ve şehir kutusu (yazılan şehirle yeni seyahat formu açılır), "3 adımda yola hazır", Keşfet'ten hazır planlar ve "Neler yapabilirsin". Alt menü yüzen yuvarlak bir çubuk, ortasında yeni seyahat için mavi + butonu.
- Tüm ekranlar prototipe uyduruldu (TP-100 … TP-105): giriş ve hesap oluşturma, ana sayfa, seyahat listesi ve detayı, formlar, gün gün plan, görevler, yerler, harita (lejant, yakınlaştırma, seçili yer kartı), hava durumu (hero kart, rozetli gün satırları), harcamalar (iki özet kartı, kategori renkleri, ikonlu çipler), harcama formu (kategori kutucukları, kişi başı pay), analiz (halka grafik), bölüşme (kim kime), üyeler (avatar, rol rozeti, davet kodu kopyalama), belgeler, AI planlayıcı (durum kartları, segmentli sorular, kart görünümlü önizleme), Keşfet ve detayı (gün kartları, işaretlenebilir satırlar), Profil (avatar, misafirde Giriş yap / Hesap oluştur), Tercihler (tema kutucukları, dil segmenti), Çöp kutusu, Yardım merkezi ve yasal metinler. Listeler tek kart içinde satırlar olarak gösterilir.
- Yeni tasarım (TP-95 … TP-99): biniş kartı tarzı seyahat kartı (mavi geçiş, uçak rotada ilerler, Hava / Harita / Detaylar kartın içinde), yenilenmiş Ana Sayfa (2x2 istatistik, son seyahatler tek kartta), Seyahatler'de sayılı filtre çipleri ve sıralama hapı, seyahat detayında bütçe kartı (%, renkli çubuk) ve satırlarda özet ("12 etkinlik", "2 bekleyen", "1/3 ziyaret edildi"), Profil'de renkli eşitleme noktası ve "Şimdi eşitle" butonu, Keşfet kartlarında tarz hapları, Android sekme çubuğunda seçili sekmeye hap. Ortak bileşenler (kart, buton, çip, etiket, pencere) yeni köşe ve renklerle; 18 yeni ikon.
- İlk açılışta 4 kartlık tanıtım (TP-93): Seyahatini oluştur, AI ile plan yap, Keşfet, Birlikte planla. Kaydırılır, İleri / Başla, sağ üstte çarpı; bir kez gösterilir, Profil > Destek > "Tanıtımı tekrar göster" ile yeniden açılır. Çizimler tema renkleriyle kodda çizilir, açık/koyu temaya uyar.
- AI planı arka planda hazırlanır (TP-57, migration 39): plan hazırlanırken sayfadan çıkılabilir, uygulama kapansa bile plan kaybolmaz; bitince "Planın hazır" bildirimi gelir, açılışta "Görmek ister misin?" diye sorulur. Aynı anda tek plan. TP-49'un çıkış uyarısı kalktı, yerine "Çıkabilirsin, arka planda sürer" yazar. `plan-trip` yeniden deploy edilmeli.
- Seyahat kapak fotoğrafı (migration 32): formdan ekle / değiştir / kaldır; 16:9 olmayan fotoğrafta kırpma penceresi açılır (sürükle, iki parmakla yakınlaştır; 1280x720 JPEG), Seyahatler listesinde ve ana sayfada 256x256 küçük resmi, detayda kapağı görünür. Dosyalar cihazda ve `trip-files` kovasında; diğer cihazlar indirir, eski kapaklar gece temizlenir.
- AI modeli ve düşünme seviyesi yönetim panelinden seçilir (migration 31); `plan-trip` boşsa `AI_MODEL` / `AI_REASONING` secret'larını kullanır.
- Yönetim panelinden uygulama ayarları (migration 29): AI planlamayı bakıma alma (uygulama mesajı gösterir, `plan-trip` 503 `ai_paused` döner), AI limitleri artık SQL sabiti değil panelden değişir, ana sayfa duyurusu, eski sürüme "Yeni sürüm var" uyarısı, yeni kullanıcıların varsayılan para birimi ve hatırlatma saati.
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
- Büyük harfli başlıklarda Türkçe harfler düzeldi (TP-106): "GİDİŞ", "SEYAHATLERİM" artık "GIDIŞ", "SEYAHATLERIM" görünmez; büyük harf uygulamanın diline göre yapılır.
- Yeni şifreler en az 8 karakter + harf ve rakam (TP-15).
- Reddedilen değişiklikte istemciye yalnızca kısa hata kodu; `account_status` yalnızca kendi hesabını sorar (TP-14, migration 21).
- Belgeler koda göre güncellendi (TP-17).

### Güvenlik
- Yerel veriler Android Google yedeğine ve cihazdan cihaza aktarıma dahil edilmiyor (TP-13).
- Sunucu ve istemcide değer doğrulaması (SEC-01, migration 11); yapay zekâ maliyet sınırları (SEC-02, migration 12).
