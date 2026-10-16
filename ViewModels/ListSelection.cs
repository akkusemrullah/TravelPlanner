using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.ViewModels
{
    /// <summary>What the shared selection app bar (Views/SelectionAppBar) binds to - the non-generic face of <see cref="ListSelection{T}"/>.</summary>
    public interface IListSelection : INotifyPropertyChanged
    {
        /// <summary>The list is in "Seçerek sil" mode: tapping a row ticks it instead of opening it.</summary>
        bool IsActive { get; }

        bool IsInactive { get; }

        int Count { get; }

        bool HasSelection { get; }

        /// <summary>"3 seçildi".</summary>
        string Title { get; }

        /// <summary>"Tümünü seç", or "Seçimi kaldır" once every row is ticked.</summary>
        string ToggleAllText { get; }

        /// <summary>The list's trash button: enters selection mode (rows get a tick, the app bar offers "Tümünü seç" and "Sil").</summary>
        ICommand StartCommand { get; }

        ICommand ToggleAllCommand { get; }

        ICommand CancelCommand { get; }
    }

    /// <summary>A list page whose rows can be ticked and deleted together (the selection app bar binds to this).</summary>
    public interface ISelectableListViewModel
    {
        IListSelection Selection { get; }

        IAsyncRelayCommand DeleteSelectedCommand { get; }
    }

    /// <summary>
    /// Multi-select state of one list page ("Seçerek sil"). Rows remember their tick in
    /// <see cref="SyncEntityBase.IsSelected"/>; the selection itself is kept by row id, so it survives the list being
    /// reloaded (a sync bringing a trip mate's change) or filtered - <see cref="Sync"/> re-applies it to the new rows.
    /// </summary>
    public sealed class ListSelection<T> : ObservableObject, IListSelection where T : SyncEntityBase, ISyncEntity
    {
        private readonly Func<IEnumerable<T>> _items;
        private readonly HashSet<int> _selectedIds = new();
        private bool _isActive;

        /// <param name="items">The rows currently shown (what "Tümünü seç" ticks).</param>
        public ListSelection(Func<IEnumerable<T>> items)
        {
            _items = items;
            ToggleCommand = new RelayCommand<T?>(Toggle);
            StartCommand = new RelayCommand(() => Start());
            ToggleAllCommand = new RelayCommand(ToggleAll);
            CancelCommand = new RelayCommand(Cancel);
        }

        public bool IsActive
        {
            get => _isActive;
            private set
            {
                if (SetProperty(ref _isActive, value))
                    OnPropertyChanged(nameof(IsInactive));
            }
        }

        public bool IsInactive => !IsActive;

        public int Count => _selectedIds.Count;

        public bool HasSelection => Count > 0;

        /// <summary>Every shown row is ticked (and there is at least one).</summary>
        public bool AllSelected
        {
            get
            {
                var any = false;
                foreach (var item in _items())
                {
                    any = true;
                    if (!_selectedIds.Contains(item.Id))
                        return false;
                }

                return any;
            }
        }

        public string Title => Count == 0
            ? AppResources.Selection_NoneSelected
            : string.Format(AppResources.Selection_CountFormat, Count);

        public string ToggleAllText => AllSelected ? AppResources.Selection_ClearAll : AppResources.Selection_SelectAll;

        /// <summary>Title of the delete confirmation: "Tümünü sil" when every row is ticked, else "Seçilenleri sil".</summary>
        public string DeleteTitle => AllSelected ? AppResources.Selection_DeleteAllTitle : AppResources.Selection_DeleteSelectedTitle;

        /// <summary>The ticked rows, in list order.</summary>
        public IReadOnlyList<T> SelectedItems => _items().Where(i => _selectedIds.Contains(i.Id)).ToList();

        public IRelayCommand<T?> ToggleCommand { get; }

        public ICommand StartCommand { get; }

        public ICommand ToggleAllCommand { get; }

        public ICommand CancelCommand { get; }

        /// <summary>Enters selection mode with nothing (or <paramref name="first"/>) ticked. Not on an empty list.</summary>
        public void Start(T? first = null)
        {
            if (!_items().Any())
                return;

            ClearTicks();
            IsActive = true;
            if (first is not null)
                Toggle(first);
            NotifyCountChanged();
        }

        /// <summary>Leaves selection mode and unticks every row.</summary>
        public void Cancel()
        {
            ClearTicks();
            IsActive = false;
            NotifyCountChanged();
        }

        public void Toggle(T? item)
        {
            if (item is null || !IsActive)
                return;

            if (_selectedIds.Remove(item.Id))
            {
                item.IsSelected = false;
            }
            else
            {
                _selectedIds.Add(item.Id);
                item.IsSelected = true;
            }

            NotifyCountChanged();
        }

        public void ToggleAll()
        {
            if (!IsActive)
                return;

            var select = !AllSelected;
            foreach (var item in _items())
            {
                item.IsSelected = select;
                if (select)
                    _selectedIds.Add(item.Id);
                else
                    _selectedIds.Remove(item.Id);
            }

            NotifyCountChanged();
        }

        /// <summary>
        /// Call after the shown rows changed (reload, filter, delete): the new row objects get their ticks, rows no
        /// longer shown drop out of the selection, and an emptied list leaves selection mode.
        /// </summary>
        public void Sync()
        {
            var shown = _items().ToList();
            _selectedIds.IntersectWith(shown.Select(i => i.Id));
            foreach (var item in shown)
                item.IsSelected = IsActive && _selectedIds.Contains(item.Id);

            if (IsActive && shown.Count == 0)
                IsActive = false;

            NotifyCountChanged();
        }

        private void ClearTicks()
        {
            _selectedIds.Clear();
            foreach (var item in _items())
                item.IsSelected = false;
        }

        private void NotifyCountChanged()
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(ToggleAllText));
        }
    }
}
