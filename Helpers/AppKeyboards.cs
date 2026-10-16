namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Keyboards used from XAML ({x:Static helpers:AppKeyboards.Sentence}). Both start with a capital letter but,
    /// unlike Keyboard.Text, don't force spell-check/auto-correct onto names and places.
    /// </summary>
    public static class AppKeyboards
    {
        /// <summary>First letter of each sentence capitalised - default for every Entry/Editor (Styles.xaml).</summary>
        public static Keyboard Sentence { get; } = Keyboard.Create(KeyboardFlags.CapitalizeSentence | KeyboardFlags.Suggestions);

        /// <summary>Every word capitalised - person names, cities, countries, place names.</summary>
        public static Keyboard Words { get; } = Keyboard.Create(KeyboardFlags.CapitalizeWord | KeyboardFlags.Suggestions);
    }
}
