# Veri Güvenliği ve Uygulama Gizliliği Beyanları / Data Safety & App Privacy Declarations

Son güncelleme: 18 Ekim 2026 · Kaynak: kod (bkz. [ARCHITECTURE.md](../../ARCHITECTURE.md), [DATA_MODEL.md](../DATA_MODEL.md)) ve [Gizlilik Politikası](../PRIVACY.md). Bir özellik veri toplamayı değiştirirse bu dosya, Play Console formu ve App Store Connect beyanı birlikte güncellenir.

## Temel gerçekler (koddan)

- **Hesapsız (misafir) kullanım:** hiçbir veri cihaz dışına çıkmaz (hava/harita/kur isteklerinde yalnızca şehir/koordinat/para birimi gider).
- **Konum:** cihaz konumu **alınmaz** (konum izni yok); yerler kullanıcı tarafından seçilir veya aranır.
- **Reklam, analitik, çökme raporlama SDK'sı:** yok. Kullanıcı izleme (tracking) yok. Sentry (TP-70) kodda var ama `Helpers/CrashReportingConfig.SentryDsn` boş olduğu sürece hiç başlamaz. DSN girilirse formda *App info and performance → Crash logs* ve *Diagnostics* "toplanıyor, paylaşılmıyor, isteğe bağlı değil, amaç: analitik/uygulama işlevi" olarak işaretlenmeli; gizlilik politikasına da eklenmeli.
- **İzinler (AndroidManifest):** internet, ağ durumu, bildirim, açılışta bildirimleri geri yükleme, titreşim, kamera (belge ve seyahat kapak fotoğrafı), Android ≤12'de depolama okuma.
- **Şifreleme:** tüm trafik HTTPS/TLS. Yerel veri Android yedeğine ve cihazdan cihaza aktarıma dahil edilmez.
- **Silme:** *Profil → Hesabımı sil*, çöp kutusunda *Kalıcı olarak sil*, çıkışta *bu cihazdaki verilerimi sil*, web: hesap silme sayfası.
- **Satın alma / abonelik:** yok ([İade politikası](../legal/REFUND_POLICY.md)).

## Google Play – *Veri güvenliği* formu

Hizmet sağlayıcılar (Supabase, OpenAI, Cloudflare) bizim adımıza işler; seyahat üyeleriyle paylaşımı kullanıcı başlatır. Bu nedenle Play tanımına göre **"paylaşılan" veri yoktur, yalnızca "toplanan"** veri vardır.

| Veri türü (Play) | Toplanıyor | Zorunlu / isteğe bağlı | Amaç | Not |
| --- | --- | --- | --- | --- |
| Kişisel bilgiler → Ad | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi | Görünen ad; seyahat üyelerine görünür |
| Kişisel bilgiler → E-posta adresi | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi | |
| Kişisel bilgiler → Kullanıcı kimlikleri | Evet | İsteğe bağlı (hesap) | Uygulama işlevselliği, Hesap yönetimi | Hesap uid'i |
| Finansal bilgiler → Diğer finansal bilgiler | Evet | İsteğe bağlı | Uygulama işlevselliği | Kullanıcının girdiği harcama kayıtları (ödeme bilgisi değil) |
| Fotoğraflar ve videolar → Fotoğraflar | Evet | İsteğe bağlı | Uygulama işlevselliği | Belge olarak eklenen fotoğraflar ve seyahat kapak fotoğrafı |
| Dosyalar ve belgeler | Evet | İsteğe bağlı | Uygulama işlevselliği | PDF bilet/rezervasyon |
| Uygulama etkinliği → Kullanıcının oluşturduğu diğer içerik | Evet | İsteğe bağlı | Uygulama işlevselliği | Seyahat, plan, notlar, yapay zekâ isteği ve cevabı |
| Cihaz veya diğer kimlikler | Evet | İsteğe bağlı (hesap) | Dolandırıcılığı önleme, güvenlik ve uyumluluk | Tuzlu SHA-256 cihaz özeti: AI kotası ve aynı cihazdaki hesapları görme (kötüye kullanım) |
| Konum (yaklaşık / kesin) | **Hayır** | – | – | |
| Kişiler, mesajlar, ses, sağlık, web geçmişi | **Hayır** | – | – | |

- Veriler aktarım sırasında şifreleniyor mu? **Evet.**
- Kullanıcı verilerinin silinmesini isteyebilir mi? **Evet** (uygulama içi ve web bağlantısı – `ACCOUNT_DELETION`).
- Hesap oluşturma: e-posta + şifre veya Google ile giriş; **hesap silme bağlantısı** zorunlu alan için hesap silme sayfası.
- Bağımsız güvenlik incelemesi: yok. Aile politikası / çocuklara yönelik: **Hayır** (13 yaş ve üzeri).
- **Yapay zekâ ile üretilen içerik** (AI-Generated Content politikası): kullanıcılar uygunsuz öneriyi plan ekranındaki *Bildir* bağlantısıyla bildirir; uyarı metni: [AI_DISCLAIMER](../legal/AI_DISCLAIMER.md).
- **Not – IP adresi:** Supabase, Cloudflare Turnstile ve diğer servisler her istekte IP adresini görür; uygulama bunu saklamaz veya kendi amacıyla kullanmaz. Güvenlik amacıyla geçici işleme Play'in beyan zorunluluğu dışındadır; yine de Gizlilik Politikası'nda açıklanır.

## Apple App Store – *App Privacy* (gizlilik etiketleri)

**Data Not Used to Track You** – takip yok (App Tracking Transparency istemi gerekmez). Tümü **"Data Linked to You"** (hesaba bağlı), yalnızca hesap açılırsa:

| Apple kategorisi | Veri | Amaç |
| --- | --- | --- |
| Contact Info → Name | Görünen ad | App Functionality |
| Contact Info → Email Address | E-posta | App Functionality |
| Identifiers → User ID | Hesap uid'i | App Functionality |
| Identifiers → Device ID | AI kotası ve kötüye kullanım için cihaz özeti | App Functionality (fraud prevention) |
| Financial Info → Other Financial Info | Girilen harcamalar | App Functionality |
| User Content → Photos or Videos | Belge fotoğrafları, seyahat kapak fotoğrafı | App Functionality |
| User Content → Other User Content | Seyahat, plan, notlar, belgeler, AI isteği | App Functionality |

Toplanmayan: Location, Health & Fitness, Contacts, Browsing/Search History, Usage Data, Diagnostics, Purchases, Sensitive Info.
iOS sürümü yayınlanırsa `PrivacyInfo.xcprivacy` (Required Reason API'ler: UserDefaults, dosya zaman damgası) eklenmelidir.

## English summary
No location, no ads, no analytics or crash SDKs, no tracking. Without an account nothing leaves the device. With an optional account the app collects (never "shares" in Play's sense) name, e-mail, user ID, user-entered expenses, photos/documents, trips and AI requests, plus a salted device hash used for the AI quota and abuse prevention (accounts on the same device). Data is encrypted in transit and users can delete it in the app or via the web deletion page. App Store: "Data Not Used to Track You"; all collected data is linked to the user and used only for app functionality.
