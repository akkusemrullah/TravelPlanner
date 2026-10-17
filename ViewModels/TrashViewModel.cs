using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One deleted item in the trash list. Tickable in "Seçerek sil" mode (<see cref="ISelectableRow"/>).</summary>
    public sealed class TrashItemView : ISelectableRow, INotifyPropertyChanged
    {
        private bool _isSelected;

        public TrashItemView(TrashEntry entry, string? tripName, DateTime nowUtc)
        {
            Entry = entry;
            // The root trash row's id is unique; entries built elsewhere (tests) fall back to the batch id.
            Id = entry.RootItemId != 0 ? entry.RootItemId : StringComparer.Ordinal.GetHashCode(entry.BatchId);
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

        public int Id { get; }

        /// <summary>"Trip", "Expense" … - picks the row's icon.</summary>
        public string Kind => Entry.EntityType;

        public string Title { get; }
        public string Subtitle { get; }
        public string ExpiryText { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

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

    /// <summary>
    /// Çöp kutusu (G1/G6): everything deleted in the last 30 days, restorable with one tap. The trash button opens
    /// the same "Seçerek sil" mode as every other list - rows are ticked one by one or with "Tümünü seç", and one
    /// confirmation deletes them for good ("Kalıcı olarak sil").
    /// </summary>
    public partial class TrashViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly ITrashService _trash;
        private readonly ITripService _trips;

        public TrashViewModel(ITrashService trash, ITripService trips, IDialogService dialogs)
            : base(dialogs)
        {
            _trash = trash;
            _trips = trips;
            Selection = new ListSelection<TrashItemView>(() => Items);
            Selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selection.IsActive))
                    OnPropertyChanged(nameof(ShowDeleteMenu));
            };
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public ObservableCollection<TrashItemView> Items { get; } = new();

        public ListSelection<TrashItemView> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button (enters selection mode) - only with something to delete.</summary>
        public bool ShowDeleteMenu => HasItems && Selection.IsInactive;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowDeleteMenu))]
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

            UpdateEmptyState();
            Selection.Sync();
        }, AppResources.Trash_LoadError);

        /// <summary>A tap on a row: ticks it in selection mode (outside it the row's own button restores).</summary>
        [RelayCommand]
        private void ItemTapped(TrashItemView? item)
        {
            if (item is not null && Selection.IsActive)
                Selection.Toggle(item);
        }

        [RelayCommand]
        private Task RestoreAsync(TrashItemView? item) => RunSafeAsync(async () =>
        {
            if (item is null || Selection.IsActive) return;

            var restored = await _trash.RestoreAsync(item.Entry.BatchId);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, restored > 0
                ? string.Format(CultureInfo.CurrentCulture, AppResources.Trash_RestoredFormat, item.Title)
                : AppResources.Trash_RestoreParentMissing);

            await LoadAsync();
        }, AppResources.Trash_RestoreError);

        // ================= Deleting for good =================

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteForeverAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.Trash_DeleteError);

        private async Task DeleteForeverAsync(IReadOnlyList<TrashItemView> items, string title)
        {
            if (items.Count == 0)
                return;

            var confirmed = items.Count == 1
                ? await Dialogs.ConfirmAsync(AppResources.Trash_DeleteForeverTitle,
                    string.Format(CultureInfo.CurrentCulture, AppResources.Trash_DeleteForeverMessageFormat, items[0].Title),
                    AppResources.Trash_DeleteForever)
                : await Dialogs.ConfirmAsync(title,
                    string.Format(CultureInfo.CurrentCulture, AppResources.Trash_DeleteForeverManyFormat, items.Count),
                    AppResources.Trash_DeleteForever);
            if (!confirmed)
                return;

            try
            {
                await _trash.DeleteForeverAsync(items.Select(i => i.Entry.BatchId).ToList());
                foreach (var item in items)
                    Items.Remove(item);
            }
            finally
            {
                UpdateEmptyState();
                Selection.Cancel();
            }
        }

        [RelayCommand]
        private async Task GoBackAsync()
        {
            if (Selection.IsActive)
            {
                Selection.Cancel();
                return;
            }

            await Shell.Current.GoToAsync("..");
        }

        private void UpdateEmptyState()
        {
            HasItems = Items.Count > 0;
            IsEmpty = !HasItems;
        }
    }
}
