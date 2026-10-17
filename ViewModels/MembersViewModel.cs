using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One row of the members list.</summary>
    public sealed class MemberItem
    {
        public MemberItem(TripMember member, bool viewerIsOwner, DateTime? nowUtc = null)
        {
            Member = member;
            Name = member.IsMe ? string.Format(AppResources.Members_MeFormat, member.Name) : member.Name;
            Email = member.Email ?? string.Empty;
            HasEmail = !string.IsNullOrEmpty(member.Email) && member.Email != member.Name;
            RoleText = SharingTexts.RoleName(member.Role);
            Initials = PersonName.Initials(member.Name);
            if (Initials.Length == 0)
                Initials = "?";
            CanManage = viewerIsOwner && !member.IsMe && member.Role != TripRole.Owner;
            // TP-4: the owner sees when each member joined, so someone unexpected stands out.
            JoinedText = CanManage
                ? string.Format(AppResources.Members_JoinedAgoFormat, SharingTexts.RelativeTime(member.JoinedAtUtc, nowUtc ?? DateTime.UtcNow))
                : string.Empty;
        }

        public TripMember Member { get; }
        public string Name { get; }
        public string Email { get; }
        public bool HasEmail { get; }
        public string RoleText { get; }
        public string Initials { get; }

        /// <summary>The owner can change this member's role or remove them.</summary>
        public bool CanManage { get; }

        /// <summary>"3 sa önce katıldı" - shown to the owner only.</summary>
        public string JoinedText { get; }

        public bool HasJoinedText => JoinedText.Length > 0;
    }

    /// <summary>
    /// "Üyeler ve davet" (G6): who is on the trip, invite someone (code shared through the share sheet), change
    /// roles, remove members, or leave the trip. Everything here needs a connection.
    /// TP-4: a code is single-use and valid for 48 hours unless a group code is asked for on purpose; whoever
    /// joins is a viewer, and the owner makes them an editor here.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class MembersViewModel : ViewModelBase
    {
        private readonly ISharingService _sharing;
        private readonly ITripService _trips;
        private readonly ITripAccessService _access;
        private readonly IAccountService _account;
        private readonly IConnectivity _connectivity;
        private readonly IShare _share;

        public MembersViewModel(
            ISharingService sharing,
            ITripService trips,
            ITripAccessService access,
            IAccountService account,
            IConnectivity connectivity,
            IShare share,
            IDialogService dialogs)
            : base(dialogs)
        {
            _sharing = sharing;
            _trips = trips;
            _access = access;
            _account = account;
            _connectivity = connectivity;
            _share = share;
        }

        /// <summary>Navigation after leaving the trip (it no longer exists here). Replaced in tests.</summary>
        internal Func<Task> AfterLeaveAction { get; set; } = async () =>
        {
            if (Shell.Current is not null)
                await Shell.Current.Navigation.PopToRootAsync();
        };

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private string tripName = string.Empty;

        /// <summary>"Rolün: Düzenleyici".</summary>
        [ObservableProperty]
        private string myRoleText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanInvite))]
        private bool isOwner;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGuest), nameof(CanInvite), nameof(CanLeave))]
        private bool isSignedIn;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOffline), nameof(CanInvite), nameof(CanLeave))]
        private bool isOnline = true;

        [ObservableProperty]
        private bool hasMembers;

        /// <summary>The trip isn't shared with anyone yet (or never uploaded).</summary>
        [ObservableProperty]
        private bool isNotShared;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? errorText;

        /// <summary>"ABCD 2345" - the code just created.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasInvite))]
        private string? lastInviteCode;

        /// <summary>"Tek kullanımlık · 19 Ekim 17:50 tarihine kadar geçerli".</summary>
        [ObservableProperty]
        private string lastInviteText = string.Empty;

        public bool IsGuest => !IsSignedIn;
        public bool IsOffline => !IsOnline;
        public bool HasError => !string.IsNullOrEmpty(ErrorText);
        public bool HasInvite => !string.IsNullOrEmpty(LastInviteCode);
        public bool CanInvite => IsSignedIn && IsOnline && IsOwner;
        public bool CanLeave => IsSignedIn && IsOnline && !IsOwner;

        public ObservableCollection<MemberItem> Members { get; } = new RangeObservableCollection<MemberItem>();

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            var trip = await _trips.GetByIdAsync(TripId);
            TripName = trip?.Name ?? string.Empty;

            var access = await _access.GetAccessAsync(TripId);
            IsOwner = access.IsOwner;
            MyRoleText = string.Format(AppResources.Members_MyRoleFormat, SharingTexts.RoleName(access.Role));
            IsSignedIn = _account.IsSignedIn;
            IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;
            ErrorText = null;

            Members.Clear();
            HasMembers = false;
            IsNotShared = false;
            if (!IsSignedIn || !IsOnline)
                return;

            IReadOnlyList<TripMember> members;
            try
            {
                members = await _sharing.GetMembersAsync(TripId);
            }
            catch (SharingException ex)
            {
                ErrorText = SharingTexts.ErrorFor(ex.Kind);
                return;
            }

            // Clear again right before filling: the list must show each member once even if loads overlap.
            Members.ReplaceAll(members.DistinctBy(m => m.UserId).Select(member => new MemberItem(member, IsOwner)));

            HasMembers = Members.Count > 0;
            IsNotShared = Members.Count <= 1;
        }, AppResources.Members_LoadError);

        /// <summary>The default: a code for one person (single-use, 48 hours, joins as viewer).</summary>
        [RelayCommand]
        private Task InviteAsync() => CreateInviteAsync(forGroup: false);

        /// <summary>A code several people can use - only after the owner confirmed they mean it.</summary>
        [RelayCommand]
        private Task InviteGroupAsync() => RunSafeAsync(async () =>
        {
            if (TripId == 0 || !CheckOnline())
                return;

            if (!await Dialogs.ConfirmAsync(AppResources.Members_InviteGroupTitle,
                    string.Format(AppResources.Members_InviteGroupMessageFormat, SharingService.GroupInviteMaxUses, SharingService.InviteValidHours),
                    AppResources.Members_InviteGroupConfirm, AppResources.Common_Cancel))
                return;

            await CreateInviteAsync(forGroup: true);
        }, AppResources.Members_ActionError);

        private Task CreateInviteAsync(bool forGroup) => RunSafeAsync(async () =>
        {
            if (TripId == 0 || !CheckOnline())
                return;

            TripInvite invite;
            try
            {
                invite = await _sharing.CreateInviteAsync(TripId, forGroup);
            }
            catch (SharingException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, SharingTexts.ErrorFor(ex.Kind));
                return;
            }

            var until = invite.ExpiresAtUtc.ToLocalTime().ToString("d MMMM HH:mm", CultureInfo.CurrentCulture);
            var uses = invite.IsGroupInvite
                ? string.Format(AppResources.Members_InviteUsesGroupFormat, invite.MaxUses)
                : AppResources.Members_InviteUsesSingle;
            LastInviteCode = SharingTexts.FormatCode(invite.Code);
            LastInviteText = string.Format(AppResources.Members_InviteInfoFormat, uses, until);

            await _share.RequestAsync(new ShareTextRequest
            {
                Title = AppResources.Members_InviteShareTitle,
                Text = string.Format(AppResources.Members_InviteShareFormat, TripName, LastInviteCode, until,
                    invite.IsGroupInvite
                        ? string.Format(AppResources.Members_InviteShareGroupFormat, invite.MaxUses)
                        : AppResources.Members_InviteShareSingle),
            });
        }, AppResources.Members_ActionError);

        /// <summary>Owner taps a member: make editor / make viewer / remove.</summary>
        [RelayCommand]
        private Task ManageMemberAsync(MemberItem? item) => RunSafeAsync(async () =>
        {
            if (item is null || !item.CanManage || !CheckOnline())
                return;

            var member = item.Member;
            var toggle = member.Role == TripRole.Viewer ? AppResources.Members_MakeEditor : AppResources.Members_MakeViewer;
            var choice = await Dialogs.ChooseAsync(member.Name, new[] { toggle, AppResources.Members_Remove });
            if (choice is null)
                return;

            try
            {
                if (choice == AppResources.Members_Remove)
                {
                    if (!await Dialogs.ConfirmAsync(AppResources.Members_RemoveTitle,
                            string.Format(AppResources.Members_RemoveMessageFormat, member.Name, TripName),
                            AppResources.Members_Remove, AppResources.Common_Cancel))
                        return;

                    await _sharing.RemoveMemberAsync(TripId, member.UserId);
                }
                else
                {
                    await _sharing.SetRoleAsync(TripId, member.UserId, member.Role == TripRole.Viewer ? TripRole.Editor : TripRole.Viewer);
                }
            }
            catch (SharingException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, SharingTexts.ErrorFor(ex.Kind));
                return;
            }

            await LoadAsync();
        }, AppResources.Members_ActionError);

        [RelayCommand]
        private Task LeaveAsync() => RunSafeAsync(async () =>
        {
            if (TripId == 0 || IsOwner || !CheckOnline())
                return;

            if (!await Dialogs.ConfirmAsync(AppResources.Members_LeaveTitle,
                    string.Format(AppResources.Members_LeaveMessageFormat, TripName),
                    AppResources.Members_Leave, AppResources.Common_Cancel))
                return;

            try
            {
                await _sharing.LeaveTripAsync(TripId);
            }
            catch (SharingException ex)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, SharingTexts.ErrorFor(ex.Kind));
                return;
            }

            await AfterLeaveAction();
        }, AppResources.Members_ActionError);

        [RelayCommand]
        private Task OpenSignInAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Auth));

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        /// <summary>Sharing actions are online-only: say so instead of failing.</summary>
        private bool CheckOnline()
        {
            IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;
            if (IsOnline)
                return true;

            Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Sharing_ErrorOffline).FireAndForget();
            return false;
        }
    }
}
