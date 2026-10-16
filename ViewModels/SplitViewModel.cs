using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One row of the balances list.</summary>
    public sealed class BalanceItem
    {
        public BalanceItem(ParticipantBalance balance, string currencyCode)
        {
            Participant = balance.Participant;
            Name = balance.Participant.Name;
            PaidText = string.Format(AppResources.Split_PaidFormat, MoneyFormatter.FormatIn(MoneyConverter.FromMinor(balance.PaidMinor), currencyCode));
            ShareText = string.Format(AppResources.Split_ShareFormat, MoneyFormatter.FormatIn(MoneyConverter.FromMinor(balance.ShareMinor), currencyCode));
            NetText = MoneyFormatter.FormatIn(MoneyConverter.FromMinor(Math.Abs(balance.NetMinor)), currencyCode);
            IsCreditor = balance.NetMinor > 0;
            IsDebtor = balance.NetMinor < 0;
            StatusText = IsCreditor ? AppResources.Split_GetsBack : IsDebtor ? AppResources.Split_Owes : AppResources.Split_Settled;
        }

        public Participant Participant { get; }
        public string Name { get; }
        public string PaidText { get; }
        public string ShareText { get; }
        public string NetText { get; }
        public string StatusText { get; }
        public bool IsCreditor { get; }
        public bool IsDebtor { get; }
    }

    /// <summary>
    /// Cost splitting for a trip (TP-504): manage participants, see what each paid vs. their share, and the
    /// short "who pays whom" list that settles everything.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class SplitViewModel : ViewModelBase
    {
        private readonly ITripService _tripService;
        private readonly ISplitService _splitService;
        private readonly ISharingService? _sharing;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private string newParticipantName = string.Empty;

        [ObservableProperty]
        private bool hasParticipants;

        [ObservableProperty]
        private bool hasNoParticipants;

        [ObservableProperty]
        private bool hasSettlements;

        /// <summary>Shown when there are participants and split expenses but everyone is square.</summary>
        [ObservableProperty]
        private bool isAllSettled;

        [ObservableProperty]
        private string splitTotalText = string.Empty;

        /// <summary>"3 harcama henüz bölüşülmedi" - expenses without a payer or participants.</summary>
        [ObservableProperty]
        private string unsplitText = string.Empty;

        [ObservableProperty]
        private bool hasUnsplit;

        public ObservableCollection<BalanceItem> Balances { get; } = new RangeObservableCollection<BalanceItem>();

        public ObservableCollection<string> Settlements { get; } = new RangeObservableCollection<string>();

        /// <param name="sharing">G6: for "add the trip's members" (optional so older call sites keep working).</param>
        public SplitViewModel(ITripService tripService, ISplitService splitService, IDialogService dialogs, ISharingService? sharing = null)
            : base(dialogs)
        {
            _tripService = tripService;
            _splitService = splitService;
            _sharing = sharing;
        }

        /// <summary>G6: the trip is shared and in the cloud - its members can be added as participants.</summary>
        [ObservableProperty]
        private bool canAddMembers;

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            var trip = await _tripService.GetByIdAsync(TripId);
            if (trip is null) return;

            await RefreshAccessAsync(TripId);
            CanAddMembers = _sharing is not null && Access is { IsSignedIn: true, IsUploaded: true, CanEdit: true };

            var currency = trip.EffectiveCurrencyCode;
            var summary = await _splitService.GetSummaryAsync(TripId);

            Balances.ReplaceAll(summary.Balances.Select(balance => new BalanceItem(balance, currency)));

            Settlements.ReplaceAll(summary.Settlements.Select(settlement => TripReportService.SettlementLine(settlement, currency)));

            HasParticipants = Balances.Count > 0;
            HasNoParticipants = !HasParticipants;
            HasSettlements = Settlements.Count > 0;
            IsAllSettled = HasParticipants && !HasSettlements && summary.SplitTotalMinor > 0;
            SplitTotalText = string.Format(AppResources.Split_TotalFormat, MoneyFormatter.FormatIn(MoneyConverter.FromMinor(summary.SplitTotalMinor), currency));
            HasUnsplit = HasParticipants && summary.UnsplitExpenseCount > 0;
            UnsplitText = string.Format(AppResources.Split_UnsplitFormat, summary.UnsplitExpenseCount);
        }, AppResources.Split_LoadError);

        [RelayCommand]
        private Task AddParticipantAsync() => RunSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(NewParticipantName) || TripId == 0) return;

            try
            {
                await _splitService.SaveParticipantAsync(TripId, NewParticipantName);
            }
            catch (DuplicateParticipantException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, string.Format(AppResources.Split_DuplicateFormat, ex.Name));
                return;
            }

            NewParticipantName = string.Empty;
            await LoadAsync();
        }, AppResources.Split_SaveError);

        /// <summary>G6: every member of the shared trip becomes a participant linked to their account.</summary>
        [RelayCommand]
        private Task AddMembersAsync() => RunSafeAsync(async () =>
        {
            if (_sharing is null || TripId == 0) return;

            IReadOnlyList<TripMember> members;
            try
            {
                members = await _sharing.GetMembersAsync(TripId);
            }
            catch (SharingException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, SharingTexts.ErrorFor(ex.Kind));
                return;
            }

            var added = await _splitService.AddMembersAsParticipantsAsync(TripId, members);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, added > 0
                ? string.Format(AppResources.Split_MembersAddedFormat, added)
                : AppResources.Split_MembersAlreadyAdded);

            await LoadAsync();
        }, AppResources.Split_SaveError);

        [RelayCommand]
        private Task RenameParticipantAsync(BalanceItem? item) => RunSafeAsync(async () =>
        {
            if (item is null) return;

            var name = await Dialogs.PromptAsync(AppResources.Split_RenameTitle, AppResources.Split_RenameMessage, item.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            try
            {
                await _splitService.SaveParticipantAsync(TripId, name, item.Participant.Id);
            }
            catch (DuplicateParticipantException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, string.Format(AppResources.Split_DuplicateFormat, ex.Name));
                return;
            }

            await LoadAsync();
        }, AppResources.Split_SaveError);

        [RelayCommand]
        private Task DeleteParticipantAsync(BalanceItem? item) => RunSafeAsync(async () =>
        {
            if (item is null) return;

            var used = await _splitService.GetUsageCountAsync(item.Participant.Id);
            var message = used > 0
                ? string.Format(AppResources.Split_DeleteUsedFormat, item.Name, used)
                : string.Format(AppResources.Split_DeleteFormat, item.Name);

            if (!await Dialogs.ConfirmAsync(AppResources.Split_DeleteTitle, message))
                return;

            await _splitService.DeleteParticipantAsync(item.Participant);
            await LoadAsync();
        }, AppResources.Split_SaveError);

        [RelayCommand]
        private async Task GoToExpensesAsync() => await Shell.Current.GoToAsync($"{Routes.ExpenseList}?TripId={TripId}");

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
