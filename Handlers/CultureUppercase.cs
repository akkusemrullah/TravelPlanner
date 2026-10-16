using System.Globalization;
using Microsoft.Maui.Handlers;

namespace TravelPlanner.Handlers
{
    /// <summary>
    /// Makes <c>TextTransform="Uppercase"</c> follow the app language. MAUI upper-cases with the INVARIANT
    /// culture, which is wrong for Turkish: "Bitiş" became "BITIŞ" and "Gidiş" "GIDIŞ" (dotless I) instead of
    /// "BİTİŞ" / "GİDİŞ". After MAUI has set a label's text, this re-sets it upper-cased with the current
    /// culture (tr: i → İ, ı → I). Only plain-text labels are touched (no FormattedText / HTML).
    /// Registered once in MauiProgram, after UseMauiApp (so MAUI's own label mappings are already in place).
    /// </summary>
    public static class CultureUppercase
    {
        public static void Configure()
        {
            foreach (var key in new[] { nameof(Label.Text), nameof(Label.TextTransform), nameof(Label.FormattedText) })
                LabelHandler.Mapper.AppendToMapping(key, Apply);
        }

        private static void Apply(ILabelHandler handler, ILabel view)
        {
            if (view is not Label label ||
                label.TextTransform != TextTransform.Uppercase ||
                label.FormattedText is not null ||
                label.TextType != TextType.Text ||
                string.IsNullOrEmpty(label.Text))
                return;

            try
            {
                var upper = label.Text.ToUpper(CultureInfo.CurrentCulture);
#if ANDROID
                if (handler.PlatformView is { } textView && textView.Text != upper)
                    textView.Text = upper;
#elif IOS || MACCATALYST
                var uiLabel = handler.PlatformView;
                if (uiLabel is null)
                    return;
                if (uiLabel.AttributedText is { Length: > 0 } attributed)
                {
                    // Keep MAUI's attributes (character spacing etc.): replace only the characters.
                    var copy = new Foundation.NSMutableAttributedString(attributed);
                    copy.Replace(new Foundation.NSRange(0, copy.Length), upper);
                    uiLabel.AttributedText = copy;
                }
                else
                {
                    uiLabel.Text = upper;
                }
#elif WINDOWS
                if (handler.PlatformView is { } textBlock)
                    textBlock.Text = upper;
#endif
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CultureUppercase] {ex.Message}");
            }
        }
    }
}
