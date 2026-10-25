# Uyum ve Regülasyon / Compliance – Travel Planner

Son güncelleme: 25 Ekim 2026. Bu belge hukuki görüş değildir; mağazaya çıkmadan önce bir hukukçuya kontrol ettirilmesi önerilir. Kaynak: kod ve [PRIVACY](PRIVACY.md), [KVKK](KVKK.md), [TERMS](TERMS.md), [ACCOUNT_DELETION](ACCOUNT_DELETION.md), [legal/](legal/) metinleri.

## 1. Kapsam ve roller
- **Veri sorumlusu:** Emrullah AKKUŞ (EAKSOFT), Erciş / Van – `Helpers/AppConstants.cs`.
- **Veri işleyenler (hizmet sağlayıcılar):** Supabase Inc. (Frankfurt, AB – barındırma, kimlik, depolama), OpenAI (ABD – yapay zekâ, isteğe bağlı), Google (giriş, isteğe bağlı), Cloudflare (ABD – bot koruması), e-posta gönderim sağlayıcısı (Supabase Auth).
- **Mevzuat:** 6698 sayılı KVKK ve ikincil düzenlemeler; AB kullanıcıları için GDPR; 6502 sayılı Tüketicinin Korunması Hakkında Kanun; Google Play Geliştirici Programı Politikaları (Kullanıcı Verileri, Hesap Silme, AI-Generated Content); Apple App Store Review Guidelines 5.1.

## 2. Kişisel veri envanteri (özet)

| Veri kategorisi | Örnek | Amaç | Hukuki sebep (KVKK md. 5) | Saklama |
| --- | --- | --- | --- | --- |
| Kimlik / iletişim | Ad, e-posta, hesap uid'i, profil foto bağlantısı | Hesap, giriş, paylaşım | (c) sözleşmenin ifası | Hesap açık olduğu sürece |
| Kimlik doğrulama | Şifre özeti, oturum anahtarları, doğrulama kodları | Güvenli giriş | (c) | Oturum / kod süresi |
| Seyahat ve içerik | Seyahat, plan, yerler, notlar, yapılacaklar | Temel işlev, eşitleme, paylaşım | (c) | Hesap süresi; silinen kayıt 30 gün çöpte |
| Finansal (kullanıcı girdisi) | Harcama, bütçe, bölüşüm | Bütçe takibi | (c) | Aynı |
| Belgeler ve fotoğraflar | Bilet, pasaport fotoğrafı | Belge saklama | (c) | Aynı; dosyalar en geç 31 gün içinde temizlenir |
| Yapay zekâ isteği | Destinasyon, tarih, tercihler, not, yer adları | Plan önerisi | Yurt dışı aktarım için açık rıza ([ACIK_RIZA](legal/ACIK_RIZA.md)) | Sunucuda saklanmaz (OpenAI kendi politikasına göre) |
| Cihaz özeti | Tuzlu SHA-256 | AI kotası, kötüye kullanımı önleme | (f) meşru menfaat | Günlük sayaç |
| İşlem güvenliği | IP, istek kayıtları | Güvenlik, bot koruması | (ç) hukuki yükümlülük, (f) | Kısa süreli sunucu kayıtları |
| İşlem logu (`private.audit_log`, migration 34) | Yönetici işlemleri (ban, oturum kapatma, hesap silme, AI hakkı, ayarların eski/yeni değeri, Keşfet değişiklikleri), yapanın ve hedef hesabın e-postası/ID'si, tarayıcı bilgisi, IP | Hesap verebilirlik, güvenlik, kötüye kullanımı ve hataları izleme | (f) meşru menfaat, (ç) hukuki yükümlülük | 1 yıl; her gece otomatik silinir. Hesap silinince loglar kalır (e-posta metin olarak), 1 yıl dolunca silinir. Yalnızca yönetici (service key) okur |

Hassas (özel nitelikli) kişisel veri bilinçli olarak toplanmaz; kullanıcı belge olarak pasaport/vize yükleyebilir → güvenli depolama, yalnızca seyahat üyeleri erişir.

## 3. Yükümlülükler ve durum

| Yükümlülük | Nasıl karşılanıyor | Durum |
| --- | --- | --- |
| Aydınlatma (KVKK md. 10) | Uygulama içi *Yasal ve gizlilik*, `docs/KVKK.md`, web sitesi | ✅ |
| <a id="açık-rıza"></a>Açık rıza (yurt dışı aktarım, isteğe bağlı özellikler) | Metin: [legal/ACIK_RIZA.md](legal/ACIK_RIZA.md) | ✅ Uygulama, yapay zekâ ile planlamayı ve Google ile girişi ilk kullanımda açık rıza sorusuyla başlatır (`ConsentService`, cihazda sürüm ile saklanır); metin *Profil → Yasal ve gizlilik → Açık Rıza Metni*, geri alma aynı yerde *Açık rızamı geri al* |
| Yurt dışına aktarım (md. 9) | Sağlayıcı sözleşmeleri (DPA/SCC) + gerektiğinde açık rıza | ⚠️ Supabase ve OpenAI DPA'larının imzalanması, gerekiyorsa Kurul'a bildirim veri sorumlusunun görevi |
| İlgili kişi başvuruları (md. 11, 13) | E-posta, web `kvkk-basvuru`, uygulama içi silme/dışa aktarma; 30 gün | ✅ |
| Veri güvenliği (md. 12) | [SECURITY.md](../SECURITY.md): RLS, TLS, kota, doğrulama | ✅ |
| İhlal bildirimi | 72 saat içinde Kurul'a ve ilgililere (SECURITY.md §5) | Süreç tanımlı |
| VERBİS kaydı | Gerçek kişi veri sorumlusu, yıllık çalışan < 50 ve mali bilanço < 100 milyon TL ise ve ana faaliyet özel nitelikli veri işleme değilse muafiyet olabilir | ⚠️ Kontrol edilmeli |
| Silme / anonimleştirme | Çöp 30 gün, gece temizliği, hesap silme, çıkışta yerel silme; işlem logu 1 yıl sonra otomatik silinir | ✅ |
| Çocuklar | 13 yaş altı hesap açamaz (TERMS §2); çocuklara yönelik değil | ✅ (18 yaş altı için veli onayı hukukçuyla değerlendirilmeli) |
| Google Play hesap silme | Uygulama içi + web sayfası (`ACCOUNT_DELETION`) | ✅ |
| Google Play veri güvenliği | [store/DATA_SAFETY.md](store/DATA_SAFETY.md) | ✅ |
| Google Play AI içerik politikası | *Bildir* bağlantısı, [AI_DISCLAIMER](legal/AI_DISCLAIMER.md) | ✅ |
| Tüketici mevzuatı | Ücretli içerik yok ([REFUND_POLICY](legal/REFUND_POLICY.md)) | ✅ |
| Açık kaynak lisans yükümlülükleri | [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md), uygulama içi lisans sayfası, atıflar haritada | ✅ |
| Çerezler (web sitesi) | `cerez-politikasi.html`; uygulamada çerez yok | ✅ |

## 4. Açık noktalar (veri sorumlusunun yapması gerekenler)
1. Supabase ve OpenAI veri işleme sözleşmeleri (DPA), OpenAI'de veri saklama ayarları.
2. VERBİS muafiyet kontrolü.
3. Yasal metinlerin hukukçu tarafından gözden geçirilmesi.
4. İşlem logu (migration 34) için Gizlilik Politikası ve KVKK Aydınlatma Metni'ne bir saklama satırı eklenmeli ("hesabınla ilgili yönetici işlemleri ve hesap silme kaydı 1 yıl tutulur"); hesap silme logu (TP-53) ile birlikte yapılması önerilir.
