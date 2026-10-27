using System.Text.Json.Nodes;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// ChangeTracker.CurrentUserId is static: tests that sign a user in run on their own, never next to other tests.
    /// </summary>
    [CollectionDefinition("SignedInUser", DisableParallelization = true)]
    public class SignedInUserCollection { }

    internal static class SharingFakes
    {
        public const string Me = "5a1f0c4e-1111-4222-8333-444455556666";
        public const string Ayse = "6b2f0c4e-1111-4222-8333-444455557777";

        public static async Task<Trip> NewTripAsync(TripService trips, string name = "Roma")
        {
            var trip = new Trip { Name = name, City = name, StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            await trips.SaveAsync(trip);
            return trip;
        }

        public static async Task SetRoleAsync(TestDatabase db, Trip trip, string role)
        {
            var conn = await db.Connection;
            await conn.InsertOrReplaceAsync(new TripMembership { TripSyncId = trip.SyncId, Role = role });
            await conn.ExecuteAsync("UPDATE Trip SET ChangeSeq = 10 WHERE Id = ?", trip.Id); // "came from the cloud"
        }
    }

    /// <summary>G6: roles are enforced at the single write path, whatever screen the write comes from.</summary>
    [Collection("SignedInUser")]
    public class TripRoleEnforcementTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private TripService _trips = null!;
        private TodoService _todos = null!;
        private TrashService _trash = null!;

        public Task InitializeAsync()
        {
            var attachments = new AttachmentService(_db.Service, Path.GetTempPath());
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), new Mock<INotificationService>().Object, attachments);
            _todos = new TodoService(_db.Service);
            _trash = new TrashService(_db.Service, attachments);
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            ChangeTracker.CurrentUserId = null;
            await _db.DisposeAsync();
        }

        [Fact]
        public async Task Viewer_CannotAddEditOrDelete()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            var todo = new TodoItem { TripId = trip.Id, Title = "Pasaport" };
            await _todos.SaveAsync(todo);
            await SharingFakes.SetRoleAsync(_db, trip, TripRoles.Viewer);

            var add = await Assert.ThrowsAsync<TripAccessException>(() => _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Bilet" }));
            Assert.Equal(TripAccessDenied.ReadOnly, add.Reason);

            todo.IsCompleted = true;
            await Assert.ThrowsAsync<TripAccessException>(() => _todos.SaveAsync(todo));
            await Assert.ThrowsAsync<TripAccessException>(() => _todos.DeleteAsync(todo));

            var stored = Assert.Single(await _todos.GetByTripIdAsync(trip.Id));
            Assert.False(stored.IsCompleted);
        }

        [Fact]
        public async Task Viewer_SavingWithoutChanges_IsNotAnError()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await SharingFakes.SetRoleAsync(_db, trip, TripRoles.Viewer);

            await _trips.SaveAsync((await _trips.GetByIdAsync(trip.Id))!); // e.g. a reorder that moved nothing
        }

        [Fact]
        public async Task Editor_EditsContent_ButCannotDeleteTheTrip()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await SharingFakes.SetRoleAsync(_db, trip, TripRoles.Editor);

            await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Bilet" });

            var ex = await Assert.ThrowsAsync<TripAccessException>(() => _trips.DeleteAsync(trip));
            Assert.Equal(TripAccessDenied.OwnerOnly, ex.Reason);

            // One transaction: nothing was deleted, nothing went to the trash.
            Assert.NotNull(await _trips.GetByIdAsync(trip.Id));
            Assert.Single(await _todos.GetByTripIdAsync(trip.Id));
            Assert.Empty(await _trash.GetEntriesAsync());
        }

        [Fact]
        public async Task Owner_AndOwnUnsharedTrips_HaveFullAccess()
        {
            var shared = await SharingFakes.NewTripAsync(_trips, "Roma");
            await SharingFakes.SetRoleAsync(_db, shared, TripRoles.Owner);
            var mine = await SharingFakes.NewTripAsync(_trips, "Paris"); // no membership row yet

            await _todos.SaveAsync(new TodoItem { TripId = shared.Id, Title = "Bilet" });
            await _trips.DeleteAsync(mine);
            await _trips.DeleteAsync(shared);

            Assert.Empty(await _trips.GetAllAsync());
        }

        [Fact]
        public async Task Guest_IsNeverRestricted()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await SharingFakes.SetRoleAsync(_db, trip, TripRoles.Viewer);
            ChangeTracker.CurrentUserId = null;

            await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Bilet" });
        }

        [Fact]
        public async Task AccessService_ReportsTheRole()
        {
            var access = new TripAccessService(_db.Service);
            var trip = await SharingFakes.NewTripAsync(_trips);

            var own = await access.GetAccessAsync(trip.Id);
            Assert.Equal(TripRole.Owner, own.Role);
            Assert.False(own.IsUploaded);

            await SharingFakes.SetRoleAsync(_db, trip, TripRoles.Viewer);
            var viewer = await access.GetAccessAsync(trip.Id);
            Assert.Equal(TripRole.Viewer, viewer.Role);
            Assert.False(viewer.CanEdit);
            Assert.True(viewer.IsUploaded);
        }

        [Fact]
        public async Task AccessService_FindsTheLastChange_AndTheName()
        {
            var access = new TripAccessService(_db.Service);
            var trip = await SharingFakes.NewTripAsync(_trips);
            var conn = await _db.Connection;
            var todo = new TodoItem { TripId = trip.Id, Title = "Bilet" };
            await _todos.SaveAsync(todo);
            await conn.ExecuteAsync("UPDATE Todo SET UpdatedBy = ?, UpdatedAtUtc = ? WHERE Id = ?",
                SharingFakes.Ayse, DateTime.UtcNow.AddMinutes(5).Ticks, todo.Id);
            await access.RememberUsersAsync(new[] { new KnownUser { UserId = SharingFakes.Ayse, DisplayName = "Ayşe" } });

            var last = await access.GetLastChangeAsync(trip.Id);

            Assert.Equal(SharingFakes.Ayse, last!.UserId);
            Assert.Equal("Ayşe", await access.GetUserNameAsync(last.UserId));
            Assert.Null(await access.GetUserNameAsync("unknown"));
        }
    }

    [Collection("SignedInUser")]
    public class SharingServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly FakeSupabaseClient _client = new();
        private readonly Mock<ISyncService> _sync = new();
        private TripService _trips = null!;
        private TripAccessService _access = null!;

        public Task InitializeAsync()
        {
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), new Mock<INotificationService>().Object,
                                     new AttachmentService(_db.Service, Path.GetTempPath()));
            _access = new TripAccessService(_db.Service);
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            ChangeTracker.CurrentUserId = null;
            await _db.DisposeAsync();
        }

        private SharingService Create(bool online = true) => new(_client, _db.Service, _sync.Object, _access, Fakes.Connectivity(online));

        private void SyncMarksUploaded(Trip trip) =>
            _sync.Setup(s => s.SyncNowAsync(It.IsAny<CancellationToken>()))
                 .Returns(async () =>
                 {
                     await (await _db.Connection).ExecuteAsync("UPDATE Trip SET ChangeSeq = 7 WHERE Id = ?", trip.Id);
                     return new SyncResult(1, 0, 0, 0, 0, 0);
                 });

        [Fact]
        public async Task Invite_UploadsANewTripFirst_ThenAsksForACode()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            SyncMarksUploaded(trip);
            _client.Handler = (fn, _) => fn == "create_invite"
                ? """[ { "code": "ABCD2345", "expires_at": "2026-10-17T10:00:00+00:00" } ]"""
                : "[]";

            var invite = await Create().CreateInviteAsync(trip.Id);

            Assert.Equal("ABCD2345", invite.Code);
            Assert.Equal(new DateTime(2026, 10, 17, 10, 0, 0, DateTimeKind.Utc), invite.ExpiresAtUtc);
            _sync.Verify(s => s.SyncNowAsync(It.IsAny<CancellationToken>()), Times.Once);
            var call = Assert.Single(_client.Calls);
            Assert.Equal(trip.SyncId, call.Arguments["p_trip_id"]!.GetValue<string>());
        }

        [Fact]
        public async Task TP12_OwnedSharedTrips_AreListed_WithTheirMemberCount()
        {
            _client.Handler = (fn, _) => fn == "get_owned_shared_trips"
                ? """[ { "trip_id": "11111111-1111-1111-1111-111111111111", "name": "Roma", "other_members": 3 }, { "trip_id": "22222222-2222-2222-2222-222222222222", "name": "Boş", "other_members": 0 } ]"""
                : "[]";

            var trip = Assert.Single(await Create().GetOwnedSharedTripsAsync());

            Assert.Equal(new OwnedSharedTrip("11111111-1111-1111-1111-111111111111", "Roma", 3), trip);
        }

        [Fact]
        public async Task TP12_TransferOwnership_CallsTheServer_AndSyncs()
        {
            _client.Handler = (_, _) => "null";

            await Create().TransferOwnershipAsync("11111111-1111-1111-1111-111111111111", SharingFakes.Ayse);

            var call = Assert.Single(_client.Calls);
            Assert.Equal("transfer_trip_ownership", call.Function);
            Assert.Equal(SharingFakes.Ayse, call.Arguments["p_new_owner"]!.GetValue<string>());
            _sync.Verify(s => s.RequestSync(TimeSpan.Zero), Times.Once);
        }

        [Fact]
        public async Task TP12_TransferOwnership_Offline_Throws()
        {
            var ex = await Assert.ThrowsAsync<SharingException>(() => Create(online: false).TransferOwnershipAsync("11111111-1111-1111-1111-111111111111", SharingFakes.Ayse));
            Assert.Equal(SharingErrorKind.Offline, ex.Kind);
        }

        [Fact]
        public async Task TP4_AnInvite_IsSingleUse_For48Hours_AndMakesAViewer()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            SyncMarksUploaded(trip);
            _client.Handler = (_, _) => """[ { "code": "ABCD2345", "expires_at": "2026-10-19T10:00:00+00:00" } ]""";

            var invite = await Create().CreateInviteAsync(trip.Id);

            var call = Assert.Single(_client.Calls);
            Assert.Equal("viewer", call.Arguments["p_role"]!.GetValue<string>());
            Assert.Equal(1, call.Arguments["p_max_uses"]!.GetValue<int>());
            Assert.Equal(48, call.Arguments["p_valid_hours"]!.GetValue<int>());
            Assert.Equal(TripRole.Viewer, invite.Role);
            Assert.Equal(1, invite.MaxUses);
            Assert.False(invite.IsGroupInvite);
        }

        [Fact]
        public async Task TP4_AGroupCode_IsAskedForOnPurpose()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            SyncMarksUploaded(trip);
            _client.Handler = (_, _) => """[ { "code": "ABCD2345", "expires_at": "2026-10-19T10:00:00+00:00" } ]""";

            var invite = await Create().CreateInviteAsync(trip.Id, forGroup: true);

            var call = Assert.Single(_client.Calls);
            Assert.Equal(SharingService.GroupInviteMaxUses, call.Arguments["p_max_uses"]!.GetValue<int>());
            Assert.Equal("viewer", call.Arguments["p_role"]!.GetValue<string>());
            Assert.True(invite.IsGroupInvite);
            Assert.Equal(10, invite.MaxUses);
        }

        [Fact]
        public async Task Invite_ForATripThatCantBeUploaded_Fails()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            _sync.Setup(s => s.SyncNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncResult(0, 0, 0, 0, 0, 1));

            var ex = await Assert.ThrowsAsync<SharingException>(() => Create().CreateInviteAsync(trip.Id));

            Assert.Equal(SharingErrorKind.NotUploaded, ex.Kind);
            Assert.Empty(_client.Calls);
        }

        [Fact]
        public async Task Offline_AndGuest_AreReported()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);

            Assert.Equal(SharingErrorKind.Offline,
                (await Assert.ThrowsAsync<SharingException>(() => Create(online: false).CreateInviteAsync(trip.Id))).Kind);

            ChangeTracker.CurrentUserId = null;
            Assert.Equal(SharingErrorKind.NotSignedIn,
                (await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABCD2345"))).Kind);
        }

        [Fact]
        public async Task Accept_NormalizesTheCode_SyncsAndFindsTheTrip()
        {
            const string tripSyncId = "11111111-1111-1111-1111-111111111111";
            _client.Handler = (fn, _) => fn == "accept_invite"
                ? $$"""[ { "trip_id": "{{tripSyncId}}", "role": "viewer", "trip_name": "Paris" } ]"""
                : "[]";
            _sync.Setup(s => s.SyncNowAsync(It.IsAny<CancellationToken>()))
                 .Returns(async () =>
                 {
                     // what the pull would do: the trip arrives
                     await (await _db.Connection).InsertAsync(new Trip
                     {
                         SyncId = tripSyncId, Name = "Paris", City = "Paris", ChangeSeq = 3,
                         StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 2)
                     });
                     return new SyncResult(0, 0, 0, 0, 1, 0);
                 });

            var joined = await Create().AcceptInviteAsync(" abcd-2345 ");

            Assert.Equal("ABCD2345", _client.Calls.Single().Arguments["p_code"]!.GetValue<string>());
            Assert.Equal(TripRole.Viewer, joined.Role);
            Assert.Equal("Paris", joined.TripName);
            Assert.Equal((await _trips.GetAllAsync()).Single().Id, joined.LocalTripId);
        }

        [Fact]
        public async Task Accept_MapsServerRefusals()
        {
            _client.Handler = (_, _) => throw new SyncException(SyncErrorKind.Rejected, "HTTP 400", status: 400, serverCode: "P0002");
            var invalid = await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABCD2345"));
            Assert.Equal(SharingErrorKind.InvalidInvite, invalid.Kind);

            var tooShort = await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABC"));
            Assert.Equal(SharingErrorKind.InvalidInvite, tooShort.Kind);
        }

        [Theory]
        [InlineData("trip_deleted", SharingErrorKind.InviteTripDeleted)]
        [InlineData("expired", SharingErrorKind.InviteClosed)]
        [InlineData("used_up", SharingErrorKind.InviteClosed)]
        [InlineData("revoked", SharingErrorKind.InviteClosed)]
        [InlineData("something_new", SharingErrorKind.InvalidInvite)]
        public async Task Deleted28_ARealCodeThatNoLongerWorks_SaysWhy(string reason, SharingErrorKind expected)
        {
            _client.Handler = (_, _) => $$"""[ { "trip_id": null, "role": null, "trip_name": null, "reason": "{{reason}}" } ]""";

            var error = await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABCD2345"));

            Assert.Equal(expected, error.Kind);
            _sync.Verify(s => s.SyncNowAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public void Deleted28_TheTextsAreShort()
        {
            Assert.Equal("Bu seyahat silinmiş, kod artık geçersiz.", SharingTexts.ErrorFor(SharingErrorKind.InviteTripDeleted));
            Assert.Equal("Bu kodun süresi dolmuş. Sahibinden yeni kod iste.", SharingTexts.ErrorFor(SharingErrorKind.InviteClosed));
        }

        [Fact]
        public async Task TP4_AWrongCode_ComesBackEmpty_AndTooManyTriesAreReported()
        {
            // the server counts a wrong code and answers with no rows (an error would undo the count)
            _client.Handler = (_, _) => "[]";
            var invalid = await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABCD2345"));
            Assert.Equal(SharingErrorKind.InvalidInvite, invalid.Kind);
            _sync.Verify(s => s.SyncNowAsync(It.IsAny<CancellationToken>()), Times.Never);

            // after 10 wrong codes: HTTP 429 / PT429 for an hour
            _client.Handler = (_, _) => throw new SyncException(SyncErrorKind.Server, "HTTP 429", status: 429, serverCode: "PT429");
            var blocked = await Assert.ThrowsAsync<SharingException>(() => Create().AcceptInviteAsync("ABCD2345"));
            Assert.Equal(SharingErrorKind.TooManyAttempts, blocked.Kind);
            Assert.Equal(AppResources.Sharing_ErrorTooManyAttempts, SharingTexts.ErrorFor(blocked.Kind));
        }

        [Fact]
        public async Task Members_AreListed_AndRemembered()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await (await _db.Connection).ExecuteAsync("UPDATE Trip SET ChangeSeq = 5 WHERE Id = ?", trip.Id);
            _client.Handler = (_, _) => $$"""
                [ { "user_id": "{{SharingFakes.Me}}", "role": "owner", "display_name": "Emrullah", "email": "e@example.com", "joined_at": "2026-10-10T09:00:00+00:00" },
                  { "user_id": "{{SharingFakes.Ayse}}", "role": "editor", "display_name": null, "email": "ayse@example.com", "joined_at": "2026-10-10T10:00:00+00:00" } ]
                """;

            var members = await Create().GetMembersAsync(trip.Id);

            Assert.Equal(2, members.Count);
            Assert.True(members[0].IsMe);
            Assert.Equal(TripRole.Editor, members[1].Role);
            Assert.Equal("ayse@example.com", members[1].Name);
            Assert.Equal("ayse@example.com", await _access.GetUserNameAsync(SharingFakes.Ayse));
        }

        [Fact]
        public async Task TP4_AViewer_GetsNoEmails_AndOldOnesAreForgotten()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await (await _db.Connection).ExecuteAsync("UPDATE Trip SET ChangeSeq = 5 WHERE Id = ?", trip.Id);
            // what an older version remembered
            await _access.RememberUsersAsync(new[] { new KnownUser { UserId = SharingFakes.Ayse, DisplayName = "Ayşe", Email = "ayse@example.com" } });
            _client.Handler = (_, _) => $$"""
                [ { "user_id": "{{SharingFakes.Ayse}}", "role": "owner", "display_name": "Ayşe", "email": null, "joined_at": "2026-10-10T09:00:00+00:00" },
                  { "user_id": "{{SharingFakes.Me}}", "role": "viewer", "display_name": "Emrullah", "email": "e@example.com", "joined_at": "2026-10-10T10:00:00+00:00" } ]
                """;

            var members = await Create().GetMembersAsync(trip.Id);

            Assert.Null(members[0].Email);
            Assert.Equal("Ayşe", members[0].Name);
            var remembered = await (await _db.Connection).FindAsync<KnownUser>(SharingFakes.Ayse);
            Assert.Null(remembered!.Email);
            Assert.Equal("Ayşe", await _access.GetUserNameAsync(SharingFakes.Ayse));
        }

        [Fact]
        public async Task TP4_RecentJoins_AreRead()
        {
            _client.Handler = (_, _) => $$"""
                [ { "trip_id": "11111111-1111-1111-1111-111111111111", "trip_name": "Roma", "user_id": "{{SharingFakes.Ayse}}",
                    "display_name": "Ayşe", "role": "viewer", "joined_at": "2026-10-17T12:30:00.123456+00:00" },
                  { "trip_id": null, "user_id": "x", "joined_at": "2026-10-17T12:31:00+00:00" } ]
                """;
            var since = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

            var joins = await Create().GetRecentJoinsAsync(since);

            var join = Assert.Single(joins);
            Assert.Equal("Roma", join.TripName);
            Assert.Equal("Ayşe", join.DisplayName);
            Assert.Equal(new DateTime(2026, 10, 17, 12, 30, 0, DateTimeKind.Utc).AddTicks(1234560), join.JoinedAtUtc);
            var call = Assert.Single(_client.Calls);
            Assert.Equal("get_recent_joins", call.Function);
            Assert.Equal(since, DateTime.Parse(call.Arguments["p_since"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind));
        }

        [Fact]
        public async Task SetRole_And_Forbidden()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await Create().SetRoleAsync(trip.Id, SharingFakes.Ayse, TripRole.Viewer);
            var call = _client.Calls.Single();
            Assert.Equal("set_member_role", call.Function);
            Assert.Equal("viewer", call.Arguments["p_role"]!.GetValue<string>());

            _client.Handler = (_, _) => throw new SyncException(SyncErrorKind.Rejected, "HTTP 403", status: 403, serverCode: "42501");
            var ex = await Assert.ThrowsAsync<SharingException>(() => Create().RemoveMemberAsync(trip.Id, SharingFakes.Ayse));
            Assert.Equal(SharingErrorKind.Forbidden, ex.Kind);
        }

        [Fact]
        public void Codes_AreNormalized()
        {
            Assert.Equal("ABCD2345", SharingService.NormalizeCode(" abcd 2345 "));
            Assert.Equal("ABCD 2345", SharingTexts.FormatCode("ABCD2345"));
        }
    }

    [Collection("SignedInUser")]
    public class SplitMembersTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private SplitService _split = null!;
        private TripService _trips = null!;

        public Task InitializeAsync()
        {
            _split = new SplitService(_db.Service);
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), new Mock<INotificationService>().Object,
                                     new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static TripMember Member(string id, string? name, string email) => new(id, TripRole.Editor, name, email, DateTime.UtcNow, false);

        [Fact]
        public async Task Members_AreLinkedOrAdded_Once()
        {
            var trip = await SharingFakes.NewTripAsync(_trips);
            await _split.SaveParticipantAsync(trip.Id, "Ayşe");   // typed in before the trip was shared
            await _split.SaveParticipantAsync(trip.Id, "Can");

            const string can1 = "7c3f0c4e-1111-4222-8333-444455558888";
            const string can2 = "8d4f0c4e-1111-4222-8333-444455559999";
            var members = new[]
            {
                Member(SharingFakes.Ayse, "Ayşe", "ayse@example.com"),
                Member(can1, "Can", "can@example.com"),
                Member(can2, "Can", "can.yilmaz@example.com"),       // a second Can
                Member(SharingFakes.Me, null, "deniz@example.com"),   // no profile name
            };

            Assert.Equal(4, await _split.AddMembersAsParticipantsAsync(trip.Id, members));
            Assert.Equal(0, await _split.AddMembersAsParticipantsAsync(trip.Id, members)); // nothing new the second time

            var participants = await _split.GetParticipantsAsync(trip.Id);
            Assert.Equal(4, participants.Count);
            Assert.Equal(SharingFakes.Ayse, participants.Single(p => p.Name == "Ayşe").UserId); // linked, not duplicated
            Assert.Equal(can1, participants.Single(p => p.Name == "Can").UserId);
            Assert.Equal(can2, participants.Single(p => p.Name == "Can (2)").UserId);         // names stay unique
            Assert.Equal(SharingFakes.Me, participants.Single(p => p.Name == "Deniz").UserId); // from the e-mail
        }
    }

    public class MembersViewModelTests
    {
        private readonly Mock<ISharingService> _sharing = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<ITripAccessService> _access = new();
        private readonly Mock<IAccountService> _account = new();
        private readonly Mock<IShare> _share = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        private MembersViewModel Create(TripRole role, bool online = true)
        {
            _trips.Setup(t => t.GetByIdAsync(3)).ReturnsAsync(new Trip { Id = 3, Name = "Roma" });
            _access.Setup(a => a.GetAccessAsync(3)).ReturnsAsync(new TripAccessInfo(role, "s", true, true));
            _account.SetupGet(a => a.IsSignedIn).Returns(true);
            _sharing.Setup(s => s.GetMembersAsync(3)).ReturnsAsync(new[]
            {
                new TripMember(SharingFakes.Me, role, "Emrullah", "e@example.com", DateTime.UtcNow, true),
                new TripMember(SharingFakes.Ayse, TripRole.Viewer, "Ayşe", "a@example.com", DateTime.UtcNow, false),
            });
            return new MembersViewModel(_sharing.Object, _trips.Object, _access.Object, _account.Object,
                                        Fakes.Connectivity(online), _share.Object, _dialogs.Object) { TripId = 3 };
        }

        [Fact]
        public async Task Owner_SeesMembers_AndCanInvite()
        {
            var vm = Create(TripRole.Owner);
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.CanInvite);
            Assert.False(vm.CanLeave);
            Assert.Equal(2, vm.Members.Count);
            Assert.False(vm.Members[0].CanManage); // me
            Assert.True(vm.Members[1].CanManage);

            _sharing.Setup(s => s.CreateInviteAsync(3, false))
                    .ReturnsAsync(new TripInvite("ABCD2345", TripRole.Viewer, DateTime.UtcNow.AddHours(48)));
            await vm.InviteCommand.ExecuteAsync(null);

            Assert.Equal("ABCD 2345", vm.LastInviteCode);
            Assert.Contains(AppResources.Members_InviteUsesSingle, vm.LastInviteText);
            _share.Verify(s => s.RequestAsync(It.Is<ShareTextRequest>(r => r.Text!.Contains("ABCD 2345") && r.Text.Contains("Roma")
                                                                           && r.Text.Contains(AppResources.Members_InviteShareSingle))), Times.Once);
        }

        [Fact]
        public async Task TP4_AGroupCode_NeedsAConfirmation()
        {
            var vm = Create(TripRole.Owner);
            await vm.LoadCommand.ExecuteAsync(null);
            _sharing.Setup(s => s.CreateInviteAsync(3, true))
                    .ReturnsAsync(new TripInvite("WXYZ2345", TripRole.Viewer, DateTime.UtcNow.AddHours(48), 10));

            _dialogs.Setup(d => d.ConfirmAsync(AppResources.Members_InviteGroupTitle, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(false);
            await vm.InviteGroupCommand.ExecuteAsync(null);
            _sharing.Verify(s => s.CreateInviteAsync(It.IsAny<int>(), It.IsAny<bool>()), Times.Never);

            _dialogs.Setup(d => d.ConfirmAsync(AppResources.Members_InviteGroupTitle, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync(true);
            await vm.InviteGroupCommand.ExecuteAsync(null);

            _sharing.Verify(s => s.CreateInviteAsync(3, true), Times.Once);
            Assert.Contains("10", vm.LastInviteText);
            _share.Verify(s => s.RequestAsync(It.Is<ShareTextRequest>(r => r.Text!.Contains("WXYZ 2345") && r.Text.Contains("10"))), Times.Once);
        }

        [Fact]
        public async Task TP4_TheOwner_SeesWhenMembersJoined()
        {
            var vm = Create(TripRole.Owner);
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.False(vm.Members[0].HasJoinedText);    // me
            Assert.True(vm.Members[1].HasJoinedText);     // Ayşe
            Assert.Equal(string.Format(AppResources.Members_JoinedAgoFormat, AppResources.Time_JustNow), vm.Members[1].JoinedText);

            var asEditor = Create(TripRole.Editor);
            await asEditor.LoadCommand.ExecuteAsync(null);
            Assert.False(asEditor.Members[1].HasJoinedText);
        }

        [Fact]
        public async Task Owner_PromotesAViewer()
        {
            var vm = Create(TripRole.Owner);
            await vm.LoadCommand.ExecuteAsync(null);
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(AppResources.Members_MakeEditor);

            await vm.ManageMemberCommand.ExecuteAsync(vm.Members[1]);

            _sharing.Verify(s => s.SetRoleAsync(3, SharingFakes.Ayse, TripRole.Editor), Times.Once);
        }

        [Fact]
        public async Task Editor_CanLeave()
        {
            var vm = Create(TripRole.Editor);
            var left = false;
            vm.AfterLeaveAction = () => { left = true; return Task.CompletedTask; };
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.False(vm.CanInvite);
            Assert.True(vm.CanLeave);
            Assert.False(vm.Members[1].CanManage);

            await vm.LeaveCommand.ExecuteAsync(null);

            _sharing.Verify(s => s.LeaveTripAsync(3), Times.Once);
            Assert.True(left);
        }

        [Fact]
        public async Task Offline_DoesNotCallTheServer()
        {
            var vm = Create(TripRole.Owner, online: false);
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.IsOffline);
            Assert.False(vm.CanInvite);
            _sharing.Verify(s => s.GetMembersAsync(It.IsAny<int>()), Times.Never);
        }
    }

    public class TrashViewModelTests
    {
        [Fact]
        public async Task ListsDeletedItems_AndRestores()
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var trash = new Mock<ITrashService>();
            trash.Setup(t => t.GetEntriesAsync(null)).ReturnsAsync(new List<TrashEntry>
            {
                new("b1", "Expense", "Taksi", "trip-1", now.AddDays(-2), null, 2),
                new("b2", "Trip", "Paris", "trip-2", now.AddDays(-29).AddHours(-1), null, 5),
            });
            trash.Setup(t => t.RestoreAsync("b1")).ReturnsAsync(2);
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetAllAsync()).ReturnsAsync(new List<Trip> { new() { Id = 1, SyncId = "trip-1", Name = "Roma" } });
            var dialogs = Fakes.Dialogs();
            var vm = new TrashViewModel(trash.Object, trips.Object, dialogs.Object) { UtcNow = () => now };

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Items.Count);
            Assert.Contains("Roma", vm.Items[0].Subtitle);
            Assert.Contains("28", vm.Items[0].ExpiryText);
            Assert.Contains("1", vm.Items[1].ExpiryText);

            await vm.RestoreCommand.ExecuteAsync(vm.Items[0]);

            trash.Verify(t => t.RestoreAsync("b1"), Times.Once);
            dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Taksi"))), Times.Once);
        }

        [Fact]
        public async Task ARefusedRestore_ExplainsTheRole()
        {
            var trash = new Mock<ITrashService>();
            trash.Setup(t => t.GetEntriesAsync(null)).ReturnsAsync(new List<TrashEntry>());
            trash.Setup(t => t.RestoreAsync(It.IsAny<string>())).ThrowsAsync(new TripAccessException(TripAccessDenied.ReadOnly));
            var dialogs = Fakes.Dialogs();
            var vm = new TrashViewModel(trash.Object, new Mock<ITripService>().Object, dialogs.Object);

            await vm.RestoreCommand.ExecuteAsync(new TrashItemView(new TrashEntry("b", "Todo", "x", null, DateTime.UtcNow, null, 1), null, DateTime.UtcNow));

            dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), AppResources.Access_ReadOnly), Times.Once);
            dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception?>()), Times.Never);
        }

        [Fact]
        public void TP105_TrashRow_RedWhenAWeekOrLessIsLeft()
        {
            var now = DateTime.UtcNow;
            var soon = new TrashItemView(new TrashEntry("a", "Todo", "x", null, now.AddDays(-25), null, 1), null, now);
            var later = new TrashItemView(new TrashEntry("b", "Todo", "y", null, now.AddDays(-10), null, 1), null, now);

            Assert.True(soon.IsExpiringSoon);
            Assert.False(later.IsExpiringSoon);
        }
    }

    public class TrashPermanentDeleteTests
    {
        private static (TrashViewModel Vm, Mock<ITrashService> Trash, Mock<IDialogService> Dialogs) Create(bool confirm = true)
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            var trash = new Mock<ITrashService>();
            trash.Setup(t => t.GetEntriesAsync(null)).ReturnsAsync(new List<TrashEntry>
            {
                new("b1", "Expense", "Taksi", null, now.AddDays(-2), null, 1) { RootItemId = 11 },
                new("b2", "Trip", "Paris", null, now.AddDays(-3), null, 5) { RootItemId = 12 },
            });
            trash.Setup(t => t.DeleteForeverAsync(It.IsAny<IReadOnlyCollection<string>>())).ReturnsAsync(1);
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetAllAsync()).ReturnsAsync(new List<Trip>());
            var dialogs = Fakes.Dialogs(confirm);
            var vm = new TrashViewModel(trash.Object, trips.Object, dialogs.Object) { UtcNow = () => now };
            return (vm, trash, dialogs);
        }

        [Fact]
        public async Task SelectedItems_AreDeletedForGood_AfterOneConfirmation()
        {
            var (vm, trash, dialogs) = Create();
            await vm.LoadCommand.ExecuteAsync(null);
            Assert.True(vm.ShowDeleteMenu);

            vm.Selection.Start();
            vm.ItemTappedCommand.Execute(vm.Items[0]);
            Assert.True(vm.Items[0].IsSelected);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            trash.Verify(t => t.DeleteForeverAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 1 && ids.Contains("b1"))), Times.Once);
            dialogs.Verify(d => d.ConfirmAsync(AppResources.Trash_DeleteForeverTitle, It.Is<string>(m => m.Contains("Taksi")),
                AppResources.Trash_DeleteForever, It.IsAny<string?>()), Times.Once);
            Assert.Single(vm.Items);
            Assert.False(vm.Selection.IsActive);
        }

        [Fact]
        public async Task SelectAll_DeletesEverything_AndShowsTheEmptyState()
        {
            var (vm, trash, _) = Create();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.Start();
            vm.Selection.ToggleAll();
            Assert.Equal(2, vm.Selection.Count);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            trash.Verify(t => t.DeleteForeverAsync(It.Is<IReadOnlyCollection<string>>(ids => ids.Count == 2)), Times.Once);
            Assert.Empty(vm.Items);
            Assert.True(vm.IsEmpty);
            Assert.False(vm.ShowDeleteMenu);
        }

        [Fact]
        public async Task Declining_KeepsEverything()
        {
            var (vm, trash, _) = Create(confirm: false);
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.Start(vm.Items[1]);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            trash.Verify(t => t.DeleteForeverAsync(It.IsAny<IReadOnlyCollection<string>>()), Times.Never);
            Assert.Equal(2, vm.Items.Count);
        }

        [Fact]
        public async Task InSelectionMode_ATapTicks_AndRestoreIsOff()
        {
            var (vm, trash, _) = Create();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.Start();
            await vm.RestoreCommand.ExecuteAsync(vm.Items[0]);

            trash.Verify(t => t.RestoreAsync(It.IsAny<string>()), Times.Never);
        }
    }

    public class SharingTextTests
    {
        [Fact]
        public void RelativeTimes()
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(AppResources.Time_JustNow, SharingTexts.RelativeTime(now.AddSeconds(-20), now));
            Assert.Equal("5 dk önce", SharingTexts.RelativeTime(now.AddMinutes(-5), now));
            Assert.Equal("3 sa önce", SharingTexts.RelativeTime(now.AddHours(-3), now));
            Assert.Equal("2 gün önce", SharingTexts.RelativeTime(now.AddDays(-2), now));
        }

        [Theory]
        [InlineData(TripRole.Owner, "accent", true)]
        [InlineData(TripRole.Editor, "success", false)]
        [InlineData(TripRole.Viewer, "neutral", false)]
        public void TP104_MemberRow_RoleBadgeTone(TripRole role, string tone, bool crown)
        {
            var item = new MemberItem(new TripMember("u1", role, "Ayşe Yılmaz", null, DateTime.UtcNow, true), viewerIsOwner: true);

            Assert.Equal(tone, item.RoleTone);
            Assert.Equal(crown, item.IsOwnerRole);
            Assert.Equal("Ayşe Yılmaz", item.AvatarName); // without "(sen)"
        }
    }
}
