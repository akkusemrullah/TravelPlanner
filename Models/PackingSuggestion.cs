using CommunityToolkit.Mvvm.ComponentModel;

namespace TravelPlanner.Models
{
    /// <summary>
    /// One "smart packing list" suggestion (TP-405). Usually selected; a "maybe" (e.g. swimwear just because it's hot)
    /// starts unticked. The user can change either.
    /// </summary>
    public partial class PackingSuggestion : ObservableObject
    {
        public PackingSuggestion(string title, string reason, bool isSelected = true)
        {
            Title = title;
            Reason = reason;
            IsSelected = isSelected;
        }

        public string Title { get; }

        /// <summary>Why it is suggested, e.g. "Yağış bekleniyor" or "8 günlük seyahat".</summary>
        public string Reason { get; }

        [ObservableProperty]
        private bool isSelected = true;
    }
}
