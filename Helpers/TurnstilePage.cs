using System.Text.Json;

namespace TravelPlanner.Helpers
{
    /// <summary>What the Turnstile page reported: a token, or an error code.</summary>
    public readonly record struct CaptchaResult(string? Token, string? Error)
    {
        public bool IsToken => !string.IsNullOrEmpty(Token);
    }

    /// <summary>
    /// The little HTML page the CAPTCHA WebView shows (Cloudflare Turnstile, free). It is loaded from a string with
    /// <see cref="SupabaseConfig.CaptchaPageUrl"/> as its base URL, so Turnstile sees that host - nothing has to be
    /// hosted there. The page hands its result back by navigating to <see cref="ResultUrl"/> (the WebView cancels
    /// that navigation and reads the query) and by keeping it in <c>window.tpResult</c> (read as a fallback).
    /// </summary>
    public static class TurnstilePage
    {
        /// <summary>Base URL of the page (a trailing slash keeps relative URLs under it).</summary>
        public static string BaseUrl => SupabaseConfig.CaptchaPageUrl.TrimEnd('/') + "/";

        /// <summary>The page navigates here with "?token=…" or "?error=…".</summary>
        public static string ResultUrl => BaseUrl + "done";

        /// <summary>JavaScript that returns the result the page keeps ("" while there is none).</summary>
        public const string ReadResultScript = "window.tpResult || ''";

        /// <param name="siteKey">Turnstile site key.</param>
        /// <param name="language">"tr" or "en" - the widget's own texts.</param>
        /// <param name="dark">Dark widget (follows the app's theme).</param>
        public static string BuildHtml(string siteKey, string language, bool dark)
        {
            var key = JsonSerializer.Serialize(siteKey ?? string.Empty);
            var lang = JsonSerializer.Serialize(language == "en" ? "en" : "tr");
            var theme = JsonSerializer.Serialize(dark ? "dark" : "light");
            var result = JsonSerializer.Serialize(ResultUrl);

            // appearance "interaction-only": most people pass without seeing anything; the checkbox shows up only when
            // Cloudflare wants an interaction. retry "never": an error is reported and the app offers "Try again".
            return $$"""
                <!DOCTYPE html>
                <html>
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <style>
                  html, body { margin: 0; padding: 0; background: transparent; }
                  body { display: flex; justify-content: center; padding-top: 4px; }
                </style>
                <script>
                  var tpDone = false;
                  function tpReport(query) {
                    if (tpDone) return;
                    tpDone = true;
                    window.tpResult = query;
                    // the app polls window.tpResult (every 0.7 s); the navigation is a second, slower channel
                    setTimeout(function () { try { window.location.href = {{result}} + '?' + query; } catch (e) { } }, 1500);
                  }
                  function tpLoad() {
                    try {
                      turnstile.render('#tp-widget', {
                        sitekey: {{key}},
                        language: {{lang}},
                        theme: {{theme}},
                        appearance: 'interaction-only',
                        retry: 'never',
                        callback: function (token) { tpReport('token=' + encodeURIComponent(token)); },
                        'error-callback': function (code) { tpReport('error=' + encodeURIComponent(code || 'error')); return true; },
                        'expired-callback': function () { tpReport('error=expired'); },
                        'timeout-callback': function () { tpReport('error=timeout'); },
                        'unsupported-callback': function () { tpReport('error=unsupported'); }
                      });
                    } catch (e) {
                      tpReport('error=' + encodeURIComponent('render: ' + e));
                    }
                  }
                  // the script couldn't be loaded (offline, blocked)
                  setTimeout(function () { if (!window.turnstile) tpReport('error=script_not_loaded'); }, 20000);
                </script>
                <script src="https://challenges.cloudflare.com/turnstile/v0/api.js?onload=tpLoad&render=explicit" async defer
                        onerror="tpReport('error=script_not_loaded')"></script>
                </head>
                <body><div id="tp-widget"></div></body>
                </html>
                """;
        }

        /// <summary>The result in a navigation URL to <see cref="ResultUrl"/>; null for any other URL.</summary>
        public static CaptchaResult? FromNavigation(string? url)
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith(ResultUrl, StringComparison.OrdinalIgnoreCase))
                return null;
            var q = url.IndexOf('?');
            return q < 0 ? new CaptchaResult(null, "empty") : Parse(url[(q + 1)..]);
        }

        /// <summary>
        /// The result kept in <c>window.tpResult</c>, as returned by EvaluateJavaScriptAsync (which may wrap it in
        /// JSON quotes on some platforms); null while there is none.
        /// </summary>
        public static CaptchaResult? FromScript(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value is "null" or "undefined" or "\"\"")
                return null;
            var text = value.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            {
                try
                {
                    text = JsonSerializer.Deserialize<string>(text) ?? string.Empty;
                }
                catch (JsonException)
                {
                    text = text[1..^1];
                }
            }
            return text.Length == 0 ? null : Parse(text);
        }

        /// <summary>Parses "token=…" or "error=…".</summary>
        public static CaptchaResult Parse(string query)
        {
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                var name = eq < 0 ? part : part[..eq];
                var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
                if (name == "token" && value.Length > 0)
                    return new CaptchaResult(value, null);
                if (name == "error")
                    return new CaptchaResult(null, value.Length > 0 ? value : "error");
            }
            return new CaptchaResult(null, "empty");
        }
    }
}
