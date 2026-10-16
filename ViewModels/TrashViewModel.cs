using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One deleted item in the trash list.</summary>
    public sealed class TrashItemView
    {
        public TrashItemView(TrashEntry entry, string? tripName, DateTime nowUtc)
        {
            Entry = entry;
            Title = string.IsNullOrWhiteSpace(entry.Title) ? KindName(entry.EntityType) : entry.Title!;

            var parts = new List<string> { KindName(entry.EntityType) };
            if (entry.EntityType != "Trip" && !string.IsNullOrWhiteSpace(tripName))
                parts.Add(tripName!);
            if (entry.ItemCount > 1)
                parts.Add(string.Format(CultureInfo.CurrentCulture, AppResources.Trash_ItemCountFormat, entry.ItemCount));
            Subtitle = string.Join(" · ", parts);

            var daysLeft = Math.Max(0, (int)Math.Ceiling((entry.ExpiresAtUtc - nowUtc).TotalDays));
            ExpiryText = string.Format(CultureInfo.CurrentCulture, AppResources.Trash_DaysLeftFormat, daysLeft);
        }

        public TrashEntry Entry { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public string ExpiryText { get; }

        public static string KindName(string entityType) => entityType switch
        {
            "Trip" => AppResources.Trash_KindTrip,
            "Expense" => AppResources.Trash_KindExpense,
            "Todo" => AppResources.Trash_KindTodo,
            "Place" => AppResources.Trash_KindPlace,
            "Activity" => AppResources.Trash_KindActivity,
            "Attachment" => AppResources.Trash_KindAttachment,
            "Participant" => AppResources.Trash_KindParticipant,
            "PackingTemplate" => AppResources.Trash_KindTemplate,
            _ => AppResources.Trash_KindOther,
        };
    }

    /// <summary>Çöp kutusu (G1/G6): everything deleted in the last 30 days, restorable with one tap.</summary>
    public partial class TrashViewModel : ViewModelBase
    {
        private readonly ITrashService _trash;
        private readonly ITripService _trips;

        public TrashViewModel(ITrashService trash, ITripService trips, IDialogService dialogs)
            : base(dialogs)
        {
            _trash = trash;
            _trips = trips;
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public ObservableCollection<TrashItemView> Items { get; } = new();

        [ObservableProperty]
        private bool hasItems;

        [ObservableProperty]
        private bool isEmpty;

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            var entries = await _trash.GetEntriesAsync();
            var tripNames = (await _trips.GetAllAsync())
                .Where(t => !string.IsNullOrEmpty(t.SyncId))
                .GroupBy(t => t.SyncId)
                .ToDictionary(g => g.Key, g => g.First().Name);

            var now = UtcNow();
            Items.Clear();
            foreach (var entry in entries)
            {
                string? tripName = null;
                if (entry.TripSyncId is { } syncId)
                    tripNames.TryGetValue(syncId, out tripName);
                Items.Add(new TrashItemView(entry, tripName, now));
            }

            HasItems = Items.Count > 0;
            IsEmpty = !HasItems;
        }, AppResources.Trash_LoadError);

        [RelayCommand]
        private Task RestoreAsync(TrashItemView? item) => RunSafeAsync(async () =>
        {
            if (item is null) return;

            var restored = await _trash.RestoreAsync(item.Entry.BatchId);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, restored > 0
                ? string.Format(CultureInfo.CurrentCulture, AppResources.Trash_RestoredFormat, item.Title)
                : AppResources.Trash_RestoreParentMissing);

            await LoadAsync();
        }, AppResources.Trash_RestoreError);

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
