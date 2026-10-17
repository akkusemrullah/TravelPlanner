using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// TP-11: a shared trip's currency changes in one server transaction, and every expense remembers which currency
    /// its amount is in, so an expense added meanwhile in the old currency is never shown under the new label.
    /// </summary>
    [Collection("SignedInUser")]
    public class TripCurrencyTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly Mock<ISupabaseClient> _client = new();
        private readonly Mock<ISyncService> _sync = new();
        private TripService _trips = null!;
        private ExpenseService _expenses = null!;

        public Task InitializeAsync()
        {
            _expenses = new ExpenseService(_db.Service);
            _trips = new TripService(_db.Service, _expenses, new Mock<INotificationService>().Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            _sync.Setup(s => s.SyncNowAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SyncResult(0, 0, 0, 0, 0, 0));
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            ChangeTracker.CurrentUserId = null;
            await _db.DisposeAsync();
        }

        private TripCurrencyService Service(bool online = true) =>
            new(_expenses, new TripAccessService(_db.Service), _client.Object, _sync.Object, Fakes.Connectivity(online));

        private async Task<(Trip Trip, Expense Expense)> TripWithExpenseAsync(bool uploaded)
        {
            var trip = new Trip { Name = "Viyana", City = "Viyana", CurrencyCode = "TRY", StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 3) };
            await _trips.SaveAsync(trip);
            var expense = new Expense { TripId = trip.Id, Amount = 1000m, OriginalAmount = 1000m, OriginalCurrencyCode = "TRY" };
            await _expenses.SaveAsync(expense);
            if (uploaded)
            {
                var conn = await _db.Connection;
                await conn.ExecuteAsync("UPDATE Trip SET ChangeSeq = 10 WHERE Id = ?", trip.Id);
            }
            return (trip, expense);
        }

        [Fact]
        public async Task SavedExpenses_RememberTheCurrencyOfTheirAmount_AndConversionUpdatesIt()
        {
            var (trip, expense) = await TripWithExpenseAsync(uploaded: false);
            Assert.Equal("TRY", (await _expenses.GetByIdAsync(expense.Id))!.AmountCurrencyCode);

            await _expenses.ConvertTripExpensesAsync(trip.Id, "TRY", "EUR", 0.025m);

            var converted = (await _expenses.GetByIdAsync(expense.Id))!;
            Assert.Equal(25m, converted.Amount);
            Assert.Equal("EUR", converted.AmountCurrencyCode);
        }

        [Fact]
        public void Amount_IsShownInItsOwnCurrency_EvenUnderAnotherTripCurrency()
        {
            // Added offline in TRY while another member converted the trip to EUR: never "€1.500".
            var late = new Expense { Amount = 1500m, AmountCurrencyCode = "TRY", TripCurrencyCode = "EUR" };
            Assert.Equal(MoneyFormatter.FormatIn(1500m, "TRY"), late.FormattedAmount);
        }

        [Fact]
        public async Task AmountEdits_AlwaysSendTheAmountsCurrency()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            var (_, expense) = await TripWithExpenseAsync(uploaded: false);
            var conn = await _db.Connection;
            await conn.ExecuteAsync("DELETE FROM PendingChange");

            expense.Amount = 1200m;
            expense.OriginalAmount = 1200m;
            await _expenses.SaveAsync(expense);

            var change = Assert.Single(await conn.Table<PendingChange>().ToListAsync());
            Assert.Contains(nameof(Expense.AmountCurrencyCode), change.ChangedFields!.Split(','));
            Assert.Contains(nameof(Expense.AmountMinor), change.ChangedFields!.Split(','));
        }

        [Fact]
        public async Task GuestTrip_IsConvertedOnTheDevice()
        {
            var (trip, expense) = await TripWithExpenseAsync(uploaded: false);

            var outcome = await Service().ConvertAsync(trip.Id, "TRY", "EUR", 0.025m);

            Assert.Equal(TripCurrencyChangeOutcome.Converted, outcome);
            Assert.Equal(25m, (await _expenses.GetByIdAsync(expense.Id))!.Amount);
            _client.Verify(c => c.RpcAsync(It.IsAny<string>(), It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task CloudTrip_IsConvertedByTheServer_AfterUploadingThisDevicesChanges()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            var (trip, expense) = await TripWithExpenseAsync(uploaded: true);
            JsonObject? sent = null;
            _client.Setup(c => c.RpcAsync(TripCurrencyService.RpcName, It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
                   .Callback<string, JsonObject, CancellationToken>((_, args, _) => sent = args)
                   .ReturnsAsync(JsonDocument.Parse("{\"converted\":1}").RootElement);

            var outcome = await Service().ConvertAsync(trip.Id, "TRY", "EUR", 0.025m);

            Assert.Equal(TripCurrencyChangeOutcome.Converted, outcome);
            Assert.Equal(trip.SyncId, sent!["p_trip_id"]!.GetValue<string>());
            Assert.Equal("TRY", sent["p_from"]!.GetValue<string>());
            Assert.Equal("EUR", sent["p_to"]!.GetValue<string>());
            Assert.Equal(0.025m, sent["p_rate"]!.GetValue<decimal>());
            _sync.Verify(s => s.SyncNowAsync(It.IsAny<CancellationToken>()), Times.Exactly(2)); // push before, pull after

            // Nothing was converted locally: the server's result arrives with the pull.
            Assert.Equal(1000m, (await _expenses.GetByIdAsync(expense.Id))!.Amount);
        }

        [Fact]
        public async Task CloudTrip_Offline_IsNotConverted()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            var (trip, expense) = await TripWithExpenseAsync(uploaded: true);

            var outcome = await Service(online: false).ConvertAsync(trip.Id, "TRY", "EUR", 0.025m);

            Assert.Equal(TripCurrencyChangeOutcome.Offline, outcome);
            Assert.Equal(1000m, (await _expenses.GetByIdAsync(expense.Id))!.Amount);
            Assert.Equal("TRY", (await _trips.GetByIdAsync(trip.Id))!.CurrencyCode);
        }

        [Fact]
        public async Task CloudTrip_ConvertedByAnotherMemberFirst_IsReported()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            var (trip, _) = await TripWithExpenseAsync(uploaded: true);
            _client.Setup(c => c.RpcAsync(TripCurrencyService.RpcName, It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new SyncException(SyncErrorKind.Rejected, "the trip is in USD now", status: 400, serverCode: "TPC01"));

            var outcome = await Service().ConvertAsync(trip.Id, "TRY", "EUR", 0.025m);

            Assert.Equal(TripCurrencyChangeOutcome.ChangedElsewhere, outcome);
            _sync.Verify(s => s.RequestSync(TimeSpan.Zero), Times.Once);
        }

        [Fact]
        public async Task Viewer_CannotConvert()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            var (trip, _) = await TripWithExpenseAsync(uploaded: true);
            var conn = await _db.Connection;
            await conn.InsertOrReplaceAsync(new TripMembership { TripSyncId = trip.SyncId, Role = TripRoles.Viewer });

            Assert.Equal(TripCurrencyChangeOutcome.Forbidden, await Service().ConvertAsync(trip.Id, "TRY", "EUR", 0.025m));
            _client.Verify(c => c.RpcAsync(It.IsAny<string>(), It.IsAny<JsonObject>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task TripForm_Offline_ExplainsAndSavesNothing()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(9)).ReturnsAsync(new Trip
            {
                Id = 9, Name = "Roma", City = "Roma", CurrencyCode = "TRY",
                StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27), PlannedBudget = 1000m
            });
            trips.Setup(t => t.GetRelatedCountsAsync(9)).ReturnsAsync((0, 3, 0, 0));
            var rates = new Mock<IExchangeRateService>();
            rates.Setup(r => r.GetRateAsync("TRY", "EUR", null, It.IsAny<CancellationToken>()))
                 .ReturnsAsync(new ExchangeRateQuote("TRY", "EUR", 0.027m, DateTime.Today));
            var currency = new Mock<ITripCurrencyService>();
            currency.Setup(c => c.ConvertAsync(9, "TRY", "EUR", 0.027m)).ReturnsAsync(TripCurrencyChangeOutcome.Offline);

            var vm = new TripFormViewModel(trips.Object, new Mock<IGeocodingService>().Object, new Mock<IExpenseService>().Object, rates.Object,
                new Mock<IBudgetAlertService>().Object, new SettingsService(new InMemoryPreferences()), Fakes.Connectivity(), Fakes.Dialogs(confirm: true).Object,
                tripCurrency: currency.Object);
            vm.TripId = 9;
            await Task.Delay(50);

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "EUR");
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(AppResources.TripForm_CurrencyChangeOffline, vm.ValidationMessage);
            trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }
    }
}
