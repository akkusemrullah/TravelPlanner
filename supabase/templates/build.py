"""Builds the Supabase Auth e-mail templates (*.html) in this folder. Run: python build.py
The colours are the app's (Resources/Styles/Colors.xaml); see README.md for pasting them into Supabase."""
from pathlib import Path

LOGO = "https://raw.githubusercontent.com/akkusemrullah/TravelPlanner/main/supabase/templates/logo.png"
SUPPORT = "emrullahakkus@gmail.com"
SITE = "https://emrullahakkus.com.tr/travelplanner/"

PRIMARY = "#1E3A8A"
TINT = "#E6EDFC"
TEXT = "#1C2026"
MUTED = "#6B7280"
PAGE = "#F1F3F6"
LINE = "#E2E5EA"
FONT = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif"


def code_box(token="{{ .Token }}"):
    return f"""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:8px 0 24px">
              <tr><td align="center" style="background:{TINT};border:1px solid #C7D6F7;border-radius:14px;padding:20px 12px">
                <div style="font-family:'SFMono-Regular',Consolas,'Courier New',monospace;font-size:34px;line-height:40px;font-weight:700;letter-spacing:8px;color:{PRIMARY}">{token}</div>
              </td></tr>
            </table>"""


def button(label, url="{{ .ConfirmationURL }}"):
    return f"""<table role="presentation" cellpadding="0" cellspacing="0" style="margin:8px 0 24px">
              <tr><td style="background:{PRIMARY};border-radius:12px">
                <a href="{url}" style="display:inline-block;padding:14px 28px;font-family:{FONT};font-size:16px;font-weight:600;color:#FFFFFF;text-decoration:none;border-radius:12px">{label}</a>
              </td></tr>
            </table>"""


def p(text, size=16, color=TEXT, bottom=16):
    return f'<p style="margin:0 0 {bottom}px;font-family:{FONT};font-size:{size}px;line-height:1.55;color:{color}">{text}</p>'


def page(title, preheader, body):
    return f"""<!DOCTYPE html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1">
  <meta name="color-scheme" content="light">
  <meta name="supported-color-schemes" content="light">
  <title>{title}</title>
</head>
<body style="margin:0;padding:0;background:{PAGE}">
  <div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent">{preheader}</div>
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:{PAGE}">
    <tr><td align="center" style="padding:32px 16px">
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:560px">
        <tr><td style="padding:0 4px 20px">
          <table role="presentation" cellpadding="0" cellspacing="0"><tr>
            <td style="padding-right:12px"><img src="{LOGO}" width="44" height="44" alt="TravelPlanner" style="display:block;border:0;border-radius:12px"></td>
            <td>
              <div style="font-family:{FONT};font-size:20px;font-weight:700;color:{PRIMARY};line-height:24px">TravelPlanner</div>
              <div style="font-family:{FONT};font-size:12px;color:{MUTED};line-height:16px">by EAKSoft</div>
            </td>
          </tr></table>
        </td></tr>
        <tr><td style="background:#FFFFFF;border:1px solid {LINE};border-radius:20px;overflow:hidden">
          <div style="height:6px;background:{PRIMARY};line-height:6px;font-size:0">&nbsp;</div>
          <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td style="padding:32px 32px 16px">
            <h1 style="margin:0 0 16px;font-family:{FONT};font-size:24px;line-height:1.3;color:{TEXT}">{title}</h1>
            {body}
          </td></tr></table>
        </td></tr>
        <tr><td align="center" style="padding:24px 16px 0">
          {p(f'Yardım için <a href="mailto:{SUPPORT}" style="color:{PRIMARY};text-decoration:none">{SUPPORT}</a>', 13, MUTED, 6)}
          {p(f'<a href="{SITE}" style="color:{MUTED};text-decoration:underline">TravelPlanner</a> bir EAKSoft ürünüdür.', 13, MUTED, 6)}
          {p('Bu e-posta {{ .Email }} adresine gönderildi.', 12, '#9AA1AD', 0)}
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>
"""


IGNORE = p("Bu isteği sen yapmadıysan bu e-postayı yok sayabilirsin.", 14, MUTED)
NEVER_SHARE = p("Kodu kimseyle paylaşma. TravelPlanner ekibi bu kodu asla sormaz.", 14, MUTED)

TEMPLATES = {
    "confirm_signup": ("Hoş geldin!", "Hesabını doğrulamak için kodun hazır.",
        p("TravelPlanner'a katıldığın için teşekkürler. Hesabını doğrulamak için bu kodu uygulamaya yaz:")
        + code_box() + NEVER_SHARE + IGNORE),
    "reset_password": ("Şifreni sıfırla", "Şifre sıfırlama kodun hazır.",
        p("Yeni şifre belirlemek için bu kodu uygulamaya yaz:")
        + code_box() + NEVER_SHARE
        + p("Bu isteği sen yapmadıysan bu e-postayı yok sayabilirsin. Şifren değişmez.", 14, MUTED)),
    "magic_link": ("Giriş kodun", "TravelPlanner giriş kodun hazır.",
        p("Giriş yapmak için bu kodu uygulamaya yaz:")
        + code_box() + p("Ya da bu cihazdan tek dokunuşla giriş yap:", 14, MUTED, 8)
        + button("Giriş yap") + IGNORE),
    "invite": ("Davet edildin", "TravelPlanner'a davet edildin.",
        p("TravelPlanner'da bir hesap açman için davet edildin. Seyahatlerini planla, bütçeni takip et, arkadaşlarınla paylaş.")
        + button("Daveti kabul et") + IGNORE),
    "change_email": ("E-posta değişikliği", "Yeni e-posta adresini doğrula.",
        p('Hesabının e-posta adresini <b>{{ .NewEmail }}</b> olarak değiştirmek için bu kodu uygulamaya yaz:')
        + code_box() + p("Ya da bağlantıyla onayla:", 14, MUTED, 8) + button("Değişikliği onayla")
        + p("Bu değişikliği sen istemediysen hemen şifreni değiştir.", 14, MUTED)),
    "reauthentication": ("Bu sensin, değil mi?", "Onay kodun hazır.",
        p("Hassas bir işlemi onaylamak için bu kodu uygulamaya yaz:")
        + code_box() + NEVER_SHARE + IGNORE),
    "password_changed": ("Şifren değişti", "TravelPlanner şifren değiştirildi.",
        p("TravelPlanner hesabının şifresi az önce değiştirildi.")
        + p(f'Bunu sen yapmadıysan hemen "Şifremi unuttum" ile yeni şifre belirle ve <a href="mailto:{SUPPORT}" style="color:{PRIMARY}">{SUPPORT}</a> adresine yaz.', 14, MUTED)),
}

if __name__ == "__main__":
    here = Path(__file__).parent
    for name, (title, preheader, body) in TEMPLATES.items():
        (here / f"{name}.html").write_text(page(title, preheader, body), encoding="utf-8")
        print("wrote", name + ".html")
