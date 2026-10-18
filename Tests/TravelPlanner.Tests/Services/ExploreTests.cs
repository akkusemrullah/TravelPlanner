using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-33 "Keşfet": the catalog service (fake HTTP), the endless list and the copy into the user's trips.</summary>
    public class ExploreServiceTests : IDisposable
    {
        private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), $"tp-explore-{Guid.NewGuid():N}");

        public ExploreServiceTests() => Directory.CreateDirectory(_cacheDir);

        public void Dispose()
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, true);
        }

        private const string PageJson = """
            {"items": [
                {"id": "11111111-1111-1111-1111-111111111111", "slug": "istanbul-3-gun", "title": "İstanbul 3 gün",
                 "country": "Türkiye", "city": "İstanbul", "duration_days": 3, "budget_level": 2,
                 "tags": ["culture", "food", "sea"], "thumb_path": "thumbs/istanbul-3-gun.webp", "is_featured": true,
                 "published_at": "2026-10-10T10:00:00+00:00", "sort_rank": 10}
             ], "next_cursor": "{\"s\":\"recommended\"}"}
            """;

        private static (ExploreService Service, FakeHttpMessageHandler Http) Create(string? cacheDir, string json = PageJson)
        {
            var http = new FakeHttpMessageHandler()
                .Respond("rpc/explore_list", json)
                .Respond("rpc/explore_detail", DetailJson);
            var client = new SupabaseClient(() => http.CreateClient(), new Mock<IAuthService>().Object);
            return (new ExploreService(client, new ResponseCache(), cacheDir), http);
        }

        internal const string DetailJson = """
            {"id": "11111111-1111-1111-1111-111111111111", "slug": "istanbul-3-gun", "title": "İstanbul 3 gün",
             "country": "Türkiye", "city": "İstanbul", "duration_days": 3, "budget_level": 2, "est_budget_minor": 1500000,
             "currency_code": "TRY", "tags": ["culture"], "months": [4, 5], "tips": ["Müze kart al"],
             "places": [{"id": "22222222-2222-2222-2222-222222222222", "name": "Ayasofya", "latitude": 41.0086, "longitude": 28.9802}],
             "activities": [{"id": "33333333-3333-3333-3333-333333333333", "day_index": 1, "start_time": "09:00", "end_time": "11:00",
                             "title": "Ayasofya", "place_id": "22222222-2222-2222-2222-222222222222"}],
             "todos": [{"id": "44444444-4444-4444-4444-444444444444", "title": "Rahat ayakkabı", "kind": "packing"}]}
            """;

        [Fact]
        public async Task List_SendsTheFilters_AndReadsTheCards()
        {
            var (service, http) = Create(_cacheDir);
            var filter = new ExploreFilter
            {
                Query = "  roma ", Place = "İtalya", MinDays = 4, MaxDays = 7, BudgetLevels = new[] { 1, 3 },
                Tags = new[] { "culture" }, Month = 5, Sort = ExploreSort.Newest,
            };

            var page = await service.ListAsync(filter, "c1");

            var body = JsonNode.Parse(http.Bodies.Single()!)!;
            Assert.Equal("roma", (string?)body["p_query"]);
            Assert.Equal("İtalya", (string?)body["p_country"]);
            Assert.Equal(4, (int?)body["p_min_days"]);
            Assert.Equal(7, (int?)body["p_max_days"]);
            Assert.Equal("[1,3]", body["p_budget_levels"]!.ToJsonString());
            Assert.Equal("newest", (string?)body["p_sort"]);
            Assert.Equal("c1", (string?)body["p_cursor"]);
            Assert.Equal(ExploreService.PageSize, (int?)body["p_limit"]);

            var card = Assert.Single(page.Items);
            Assert.Equal("İstanbul 3 gün", card.Title);
            Assert.Equal(3, card.DurationDays);
            Assert.Equal("₺₺", card.BudgetText);
            Assert.Equal(2, card.TagLabels.Count); // at most 2 on a card
            Assert.Equal("İstanbul, Türkiye", card.Location);
            Assert.EndsWith("/storage/v1/object/public/explore-images/thumbs/istanbul-3-gun.webp", card.ThumbUrl);
            Assert.NotNull(page.NextCursor);
        }

        [Fact]
        public async Task List_AOneLetterSearch_IsNoSearch()
        {
            var (service, http) = Create(null);

            await service.ListAsync(new ExploreFilter { Query = "a" }, null);

            Assert.Null((string?)JsonNode.Parse(http.Bodies.Single()!)!["p_query"]);
        }

        [Fact]
        public async Task FirstPage_IsCached_AndTheUnfilteredOneSurvivesARestartOnDisk()
        {
            var (service, http) = Create(_cacheDir);
            await service.ListAsync(new ExploreFilter(), null);

            Assert.NotNull(service.GetCachedFirstPage(new ExploreFilter()));
            Assert.Null(service.GetCachedFirstPage(new ExploreFilter { Tags = new[] { "sea" } }));

            // a new service (app restart): memory is empty, the disk copy is still there - also offline
            var (restarted, _) = Create(_cacheDir);
            var cached = restarted.GetCachedFirstPage(new ExploreFilter());
            Assert.Equal("istanbul-3-gun", Assert.Single(cached!.Items).Slug);
            Assert.Single(http.Requests);
        }

        [Fact]
        public async Task LaterPages_AreNotCachedAsTheFirstPage()
        {
            var (service, _) = Create(null);
            await service.ListAsync(new ExploreFilter { Sort = ExploreSort.Shortest }, "cursor");

            Assert.Null(service.GetCachedFirstPage(new ExploreFilter { Sort = ExploreSort.Shortest }));
        }

        [Fact]
        public async Task Detail_ReadsTheWholeSample_OnceFromTheNetwork()
        {
            var (service, http) = Create(null);
            var id = Guid.Parse("11111111-1111-1111-1111-111111111111");

            var detail = await service.GetDetailAsync(id);
            await service.GetDetailAsync(id);

            Assert.NotNull(detail);
            Assert.Equal(15000m, MoneyConverter.FromMinor(detail!.EstBudgetMinor!.Value));
            Assert.Equal("09:00", Assert.Single(detail.Activities).StartTime);
            Assert.Equal(ExploreTodo.KindPacking, Assert.Single(detail.Todos).Kind);
            Assert.Equal(new[] { 4, 5 }, detail.Months);
            Assert.Single(http.Requests); // cached
        }

        [Fact]
        public void Filter_CountsActiveFilters_AndComparesByValue()
        {
            var a = new ExploreFilter { Place = "Roma", Tags = new[] { "sea", "food" }, MinDays = 1, MaxDays = 3 };
            var b = new ExploreFilter { Place = "roma", Tags = new[] { "food", "sea" }, MinDays = 1, MaxDays = 3 };

            Assert.Equal(3, a.ActiveCount);
            Assert.Equal(a, b);
            Assert.False(a.IsDefault);
            Assert.True(new ExploreFilter { Query = "x" }.Cleared().ActiveCount == 0);
            Assert.True(new ExploreFilter().IsDefault);
        }

        [Fact]
        public void ImageUrl_RejectsPathTricks()
        {
            Assert.Null(ExploreImages.Url("../secret"));
            Assert.Null(ExploreImages.Url(" "));
            Assert.Contains("/explore-images/covers/roma.webp", ExploreImages.Url("covers/roma.webp"));
        }

        [Fact]
        public void AddRange_RaisesOneEvent()
        {
            var list = new RangeObservableCollection<int>(new[] { 1 });
            var events = new List<NotifyCollectionChangedEventArgs>();
            list.CollectionChanged += (_, e) => events.Add(e);

            list.AddRange(new[] { 2, 3, 4 });
            list.AddRange(Array.Empty<int>());

            var e = Assert.Single(events);
            Assert.Equal(NotifyCollectionChangedAction.Add, e.Action);
            Assert.Equal(1, e.NewStartingIndex);
            Assert.Equal(3, e.NewItems!.Count);
            Assert.Equal(new[] { 1, 2, 3, 4 }, list);
        }
    }

    /// <summary>A catalog whose answers the test controls.</summary>
    internal sealed class FakeExploreService : IExploreService
    {
        public List<(ExploreFilter Filter, string? Cursor)> Calls { get; } = new();
        public Func<ExploreFilter, string?, CancellationToken, Task<ExplorePage>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new ExplorePage(Array.Empty<ExploreTripCard>(), null));
        public ExplorePage? Cached { get; set; }
        public ExploreTripDetail? Detail { get; set; }

        public Task<ExplorePage> ListAsync(ExploreFilter filter, string? cursor, CancellationToken cancellationToken = default)
        {
            Calls.Add((filter, cursor));
            return Handler(filter, cursor, cancellationToken);
        }

        public ExplorePage? GetCachedFirstPage(ExploreFilter filter) => Cached;

        public Task<ExploreTripDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Detail);

        public static ExploreTripCard Card(int n) => new()
        {
            Id = new Guid(n, 0, 0, new byte[8]),
            Title = $"Plan {n}",
            City = "Roma",
            Country = "İtalya",
            DurationDays = 3,
        };

        public static ExplorePage Page(int from, int count, string? next) =>
            new(Enumerable.Range(from, count).Select(Card).ToList(), next);
    }

    public class ExploreViewModelTests
    {
        private readonly FakeExploreService _explore = new();
        private readonly InMemoryPreferences _preferences = new();

        private ExploreViewModel Create(bool online = true) =>
            new(_explore, Fakes.Connectivity(online), _preferences, Fakes.Dialogs().Object) { SearchDelay = TimeSpan.FromMilliseconds(30) };

        [Fact]
        public async Task Start_LoadsTheFirstPage_Once()
        {
            _explore.Handler = (_, _, _) => Task.FromResult(FakeExploreService.Page(1, 20, "c2"));
            var vm = Create();

            await vm.StartAsync();
            await vm.StartAsync(); // coming back to the tab keeps the list

            Assert.Equal(20, vm.Items.Count);
            Assert.True(vm.HasMore);
            Assert.Single(_explore.Calls);
            Assert.False(vm.ShowSkeleton);
        }

        [Fact]
        public async Task LoadMore_AppendsWithoutDuplicates_AndStopsAfterTheLastPage()
        {
            _explore.Handler = (_, cursor, _) => Task.FromResult(cursor switch
            {
                null => FakeExploreService.Page(1, 20, "c2"),
                "c2" => FakeExploreService.Page(16, 20, null), // overlaps 16..20
                _ => throw new InvalidOperationException("no more pages"),
            });
            var vm = Create();
            await vm.StartAsync();

            await vm.LoadMoreCommand.ExecuteAsync(null);
            await vm.LoadMoreCommand.ExecuteAsync(null);

            Assert.Equal(35, vm.Items.Count);
            Assert.Equal(vm.Items.Count, vm.Items.Select(c => c.Id).Distinct().Count());
            Assert.False(vm.HasMore);
            Assert.Equal(2, _explore.Calls.Count); // no request after the last page
        }

        [Fact]
        public async Task LoadMore_OnlyOneRequestAtATime()
        {
            var next = new TaskCompletionSource<ExplorePage>();
            _explore.Handler = (_, cursor, _) => cursor is null ? Task.FromResult(FakeExploreService.Page(1, 20, "c2")) : next.Task;
            var vm = Create();
            await vm.StartAsync();

            var first = vm.LoadMoreCommand.ExecuteAsync(null);
            var second = vm.LoadMoreCommand.ExecuteAsync(null);
            next.SetResult(FakeExploreService.Page(21, 20, null));
            await Task.WhenAll(first, second);

            Assert.Equal(2, _explore.Calls.Count);
            Assert.Equal(40, vm.Items.Count);
        }

        [Fact]
        public async Task FastTyping_OnlyTheLastSearchIsSent()
        {
            _explore.Handler = (f, _, _) => Task.FromResult(FakeExploreService.Page(1, 1, null));
            var vm = Create();
            await vm.StartAsync();

            vm.SearchText = "r";
            vm.SearchText = "ro";
            vm.SearchText = "rom";
            vm.SearchText = "roma";
            await Task.Delay(300);

            Assert.Equal(2, _explore.Calls.Count); // the first page + one search
            Assert.Equal("roma", _explore.Calls[^1].Filter.Query);
        }

        [Fact]
        public async Task ANewSearch_CancelsTheOlderRequest()
        {
            var tokens = new List<CancellationToken>();
            var slow = new TaskCompletionSource<ExplorePage>();
            _explore.Handler = (f, _, token) =>
            {
                tokens.Add(token);
                return f.Query == "roma" ? slow.Task : Task.FromResult(FakeExploreService.Page(50, 1, null));
            };
            var vm = Create();
            await vm.StartAsync();

            vm.SearchText = "roma";
            await Task.Delay(200);
            vm.SearchText = "paris";
            await Task.Delay(200);
            slow.SetResult(FakeExploreService.Page(1, 5, null)); // the old answer arrives late

            Assert.True(tokens[1].IsCancellationRequested);
            Assert.Equal("Plan 50", Assert.Single(vm.Items).Title);
        }

        [Fact]
        public async Task Offline_WithCachedCards_ShowsThemAndTheBanner()
        {
            _explore.Cached = FakeExploreService.Page(1, 3, null);
            var vm = Create(online: false);

            await vm.StartAsync();

            Assert.Equal(3, vm.Items.Count);
            Assert.True(vm.ShowOfflineBanner);
            Assert.Empty(_explore.Calls);
        }

        [Fact]
        public async Task Offline_WithoutCache_ShowsTheOfflineState()
        {
            var vm = Create(online: false);

            await vm.StartAsync();

            Assert.True(vm.IsOfflineEmpty);
            Assert.False(vm.HasNoResults);
        }

        [Fact]
        public async Task AFailure_WithoutCache_IsAnErrorWithRetry()
        {
            _explore.Handler = (_, _, _) => throw new SyncException(SyncErrorKind.Server, "boom");
            var vm = Create();

            await vm.StartAsync();
            Assert.True(vm.HasError);

            _explore.Handler = (_, _, _) => Task.FromResult(FakeExploreService.Page(1, 2, null));
            await vm.RetryCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);
            Assert.Equal(2, vm.Items.Count);
        }

        [Fact]
        public async Task Filters_AreCountedApplied_RememberedAndCleared()
        {
            var vm = Create();
            await vm.StartAsync();

            vm.OpenFilterCommand.Execute(null);
            vm.ToggleChoiceCommand.Execute(vm.DurationChoices[1]);           // 4-7
            vm.ToggleChoiceCommand.Execute(vm.StyleChoices[0]);              // culture
            vm.ToggleChoiceCommand.Execute(vm.StyleChoices[2]);              // sea
            vm.ToggleChoiceCommand.Execute(vm.BudgetChoices[0]);             // ₺
            vm.ToggleChoiceCommand.Execute(vm.SortChoices[2]);               // shortest
            vm.FilterPlace = "Türkiye";
            await vm.ApplyFilterCommand.ExecuteAsync(null);

            var applied = _explore.Calls[^1].Filter;
            Assert.Equal(4, applied.MinDays);
            Assert.Equal(7, applied.MaxDays);
            Assert.Equal(new[] { "culture", "sea" }, applied.Tags);
            Assert.Equal(ExploreSort.Shortest, applied.Sort);
            Assert.Equal(4, vm.ActiveFilterCount);
            Assert.False(vm.IsFilterOpen);

            // remembered on the device
            var again = Create();
            Assert.Equal(applied, again.Filter);

            await vm.ClearFilterCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.ActiveFilterCount);
            Assert.Equal(ExploreSort.Shortest, vm.Filter.Sort); // "Temizle" keeps the sort
        }

        [Fact]
        public async Task EmptyResult_WithFilters_OffersToClearThem()
        {
            var vm = Create();
            vm.Filter = new ExploreFilter { Tags = new[] { "sea" } };
            await vm.StartAsync();

            Assert.True(vm.HasNoResults);
            Assert.False(vm.IsEmptyCatalog);

            _explore.Handler = (_, _, _) => Task.FromResult(FakeExploreService.Page(1, 1, null));
            await vm.ClearAllCommand.ExecuteAsync(null);
            Assert.True(_explore.Calls[^1].Filter.IsDefault);
            Assert.Single(vm.Items);
        }

        [Fact]
        public void Query_IsTrimmedAndLimited()
        {
            Assert.Null(ExploreViewModel.NormalizeQuery(" a "));
            Assert.Equal("ro", ExploreViewModel.NormalizeQuery(" ro "));
            Assert.Equal(100, ExploreViewModel.NormalizeQuery(new string('x', 150))!.Length);
        }
    }

    public class ExploreCreateTripTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly Mock<INotificationService> _notifications = new();
        private TripService _trips = null!;

        public Task InitializeAsync()
        {
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), _notifications.Object,
                                     new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static readonly Guid Ayasofya = Guid.NewGuid(), Topkapi = Guid.NewGuid();
        private static readonly Guid A1 = Guid.NewGuid(), A2 = Guid.NewGuid(), A3 = Guid.NewGuid(), A9 = Guid.NewGuid();
        private static readonly Guid T1 = Guid.NewGuid(), T2 = Guid.NewGuid();

        private static ExploreTripDetail Sample(int days = 3) => new()
        {
            Id = Guid.NewGuid(),
            Title = "İstanbul 3 gün",
            Country = "Türkiye",
            City = "İstanbul",
            DurationDays = days,
            Summary = "Klasik İstanbul",
            Places =
            {
                new ExplorePlace { Id = Ayasofya, Name = "Ayasofya", Latitude = 41.0086, Longitude = 28.9802 },
                new ExplorePlace { Id = Topkapi, Name = "Topkapı Sarayı", Latitude = 41.0115, Longitude = 28.9834 },
            },
            Activities =
            {
                new ExploreActivity { Id = A1, DayIndex = 1, StartTime = "09:00", EndTime = "11:00", Title = "Ayasofya", PlaceId = Ayasofya },
                new ExploreActivity { Id = A2, DayIndex = 1, StartTime = "12:00", Title = "Topkapı", PlaceId = Topkapi },
                new ExploreActivity { Id = A3, DayIndex = 3, Title = "Boğaz turu" },
                new ExploreActivity { Id = A9, DayIndex = 9, Title = "Out of range" },
            },
            Todos =
            {
                new ExploreTodo { Id = T1, Title = "Müze kart al" },
                new ExploreTodo { Id = T2, Title = "Rahat ayakkabı", Kind = ExploreTodo.KindPacking },
            },
        };

        [Fact]
        public async Task Add_CopiesEverything_OnTheChosenDates()
        {
            var start = new DateTime(2027, 5, 10);

            var trip = await _trips.CreateFromTemplateAsync(Sample(), new ExploreAddOptions("Benim İstanbul", start, "EUR", 50000));

            var db = await _db.Connection;
            Assert.Equal(start, trip.StartDate);
            Assert.Equal(start.AddDays(2), trip.EndDate);
            Assert.Equal("EUR", trip.CurrencyCode);
            Assert.Equal("Klasik İstanbul", trip.Description);

            var places = await db.Table<Place>().Where(p => p.TripId == trip.Id).ToListAsync();
            var activities = await db.Table<TripActivity>().Where(a => a.TripId == trip.Id).OrderBy(a => a.Date).ThenBy(a => a.SortOrder).ToListAsync();
            var todos = await db.Table<TodoItem>().Where(t => t.TripId == trip.Id).OrderBy(t => t.CreatedDate).ToListAsync();

            Assert.Equal(2, places.Count);
            Assert.Equal(3, activities.Count); // day 9 doesn't fit a 3-day trip
            Assert.Equal(start, activities[0].Date);
            Assert.Equal(TimeSpan.FromHours(9), activities[0].StartTime);
            Assert.Equal(TimeSpan.FromHours(11), activities[0].EndTime);
            Assert.Equal(places.Single(p => p.Name == "Ayasofya").Id, activities[0].PlaceId);
            Assert.Equal(places.Single(p => p.Name == "Topkapı Sarayı").Id, activities[1].PlaceId);
            Assert.Equal(1, activities[1].SortOrder);
            Assert.Equal(start.AddDays(2), activities[2].Date);
            Assert.Equal(new[] { "Müze kart al", "Rahat ayakkabı" }, todos.Select(t => t.Title));

            // every row goes into the outbox (synced when there is an account)
            Assert.Equal(1 + 2 + 3 + 2, await db.Table<PendingChange>().CountAsync());
            _notifications.Verify(n => n.ScheduleTripNotificationsAsync(It.Is<Trip>(t => t.Id == trip.Id)), Times.Once);
        }

        [Fact]
        public async Task EditAndAdd_CopiesOnlyTheTickedRows()
        {
            var options = new ExploreAddOptions("Kısa", new DateTime(2027, 5, 10), "TRY",
                ActivityIds: new HashSet<Guid> { A1, A2 }, PlaceIds: new HashSet<Guid> { Topkapi }, TodoIds: new HashSet<Guid>());

            var trip = await _trips.CreateFromTemplateAsync(Sample(), options);

            var db = await _db.Connection;
            var place = Assert.Single(await db.Table<Place>().Where(p => p.TripId == trip.Id).ToListAsync());
            var activities = await db.Table<TripActivity>().Where(a => a.TripId == trip.Id).ToListAsync();
            Assert.Equal("Topkapı Sarayı", place.Name);
            Assert.Equal(2, activities.Count);
            Assert.Null(activities.Single(a => a.Title == "Ayasofya").PlaceId); // its place wasn't copied
            Assert.Equal(place.Id, activities.Single(a => a.Title == "Topkapı").PlaceId);
            Assert.Equal(0, await db.Table<TodoItem>().Where(t => t.TripId == trip.Id).CountAsync());
        }

        [Fact]
        public async Task TheCopy_IsIndependentOfTheSample()
        {
            var sample = Sample();
            var trip = await _trips.CreateFromTemplateAsync(sample, new ExploreAddOptions("", new DateTime(2027, 5, 10), "TRY"));

            sample.Activities.Clear();
            sample.Title = "Değişti";

            var db = await _db.Connection;
            Assert.Equal("İstanbul 3 gün", trip.Name); // empty name → the sample's title
            Assert.Equal(3, await db.Table<TripActivity>().Where(a => a.TripId == trip.Id).CountAsync());
        }

        [Fact]
        public async Task LongSamples_AreCutTo90Days()
        {
            var trip = await _trips.CreateFromTemplateAsync(Sample(days: 120), new ExploreAddOptions("Uzun", new DateTime(2027, 1, 1), "TRY"));

            Assert.Equal(AppConstants.MaxTripDays, trip.DurationInDays);
        }

        [Fact]
        public async Task AtTheTripLimit_NothingIsCreated()
        {
            var db = await _db.Connection;
            for (var i = 0; i < AppConstants.MaxTrips; i++)
                await db.InsertAsync(new Trip { Name = $"T{i}", City = "X", StartDate = DateTime.Today, EndDate = DateTime.Today });

            await Assert.ThrowsAsync<TripLimitException>(() =>
                _trips.CreateFromTemplateAsync(Sample(), new ExploreAddOptions("Fazla", DateTime.Today, "TRY")));

            Assert.Equal(AppConstants.MaxTrips, await db.Table<Trip>().CountAsync());
            Assert.Equal(0, await db.Table<Place>().CountAsync());
        }
    }

    public class ExploreDetailViewModelTests
    {
        private readonly FakeExploreService _explore = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly InMemoryPreferences _preferences = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        private ExploreDetailViewModel Create()
        {
            var settings = new Mock<ISettingsService>();
            settings.SetupGet(s => s.CurrencyCode).Returns("TRY");
            var vm = new ExploreDetailViewModel(_explore, _trips.Object, settings.Object, _preferences, Fakes.Connectivity(), _dialogs.Object)
            {
                ShowNotice = _ => { },
                AfterAdded = _ => Task.CompletedTask,
            };
            return vm;
        }

        private static ExploreTripDetail Detail() =>
            System.Text.Json.JsonSerializer.Deserialize<ExploreTripDetail>(ExploreServiceTests.DetailJson, ExploreService.Json)!;

        [Fact]
        public void Rows_ListDaysPlacesPackingAndTips()
        {
            var rows = ExploreDetailViewModel.BuildRows(Detail()).ToList();

            Assert.Contains(rows, r => r is ExploreDayRow { Header: "Gün 1" });
            Assert.Single(rows.OfType<ExploreActivityRow>());
            Assert.Single(rows.OfType<ExplorePlaceRow>());
            Assert.Contains(rows, r => r is ExploreSectionRow s && s.Title == Resources.Strings.AppResources.Explore_SectionPacking);
            Assert.Single(rows.OfType<ExploreTipRow>());
            Assert.Equal("Ayasofya", rows.OfType<ExploreActivityRow>().Single().PlaceName);
        }

        [Fact]
        public async Task Add_CopiesEverything_AndRemembersTheSample()
        {
            var detail = Detail();
            _explore.Detail = detail;
            _trips.Setup(t => t.CreateFromTemplateAsync(It.IsAny<ExploreTripDetail>(), It.IsAny<ExploreAddOptions>()))
                  .ReturnsAsync(new Trip { Id = 7 });
            var vm = Create();
            vm.Id = detail.Id.ToString();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.StartAddCommand.Execute(null);
            Assert.True(vm.IsAddSheetOpen);
            Assert.Equal("İstanbul 3 gün", vm.TripName);
            Assert.Equal("15000", vm.BudgetText); // the estimate, in the same currency
            await vm.ConfirmAddCommand.ExecuteAsync(null);

            _trips.Verify(t => t.CreateFromTemplateAsync(detail, It.Is<ExploreAddOptions>(o =>
                o.ActivityIds == null && o.PlaceIds == null && o.TodoIds == null && o.PlannedBudgetMinor == 1500000)), Times.Once);
            Assert.False(vm.IsAddSheetOpen);
            Assert.True(vm.WasAddedBefore(detail.Id));
        }

        [Fact]
        public async Task EditAndAdd_SendsOnlyTheTickedRows()
        {
            var detail = Detail();
            _explore.Detail = detail;
            ExploreAddOptions? sent = null;
            _trips.Setup(t => t.CreateFromTemplateAsync(It.IsAny<ExploreTripDetail>(), It.IsAny<ExploreAddOptions>()))
                  .Callback<ExploreTripDetail, ExploreAddOptions>((_, o) => sent = o)
                  .ReturnsAsync(new Trip { Id = 7 });
            var vm = Create();
            vm.Id = detail.Id.ToString();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.StartEditCommand.Execute(null);
            vm.Rows.OfType<ExplorePlaceRow>().Single().IsSelected = false;
            vm.ContinueEditCommand.Execute(null);
            await vm.ConfirmAddCommand.ExecuteAsync(null);

            Assert.NotNull(sent);
            Assert.Empty(sent!.PlaceIds!);
            Assert.Single(sent.ActivityIds!);
            Assert.Single(sent.TodoIds!);
        }

        [Fact]
        public async Task AddingTheSameSampleAgain_AsksFirst()
        {
            var detail = Detail();
            _explore.Detail = detail;
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
            _preferences.Set(ExploreDetailViewModel.AddedPreferenceKey, detail.Id.ToString("N"));
            var vm = Create();
            vm.Id = detail.Id.ToString();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.StartAddCommand.Execute(null);
            await vm.ConfirmAddCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ConfirmAsync(Resources.Strings.AppResources.Explore_AgainTitle, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _trips.Verify(t => t.CreateFromTemplateAsync(It.IsAny<ExploreTripDetail>(), It.IsAny<ExploreAddOptions>()), Times.Never);
        }

        [Fact]
        public async Task AtTheTripLimit_SaysSo()
        {
            var detail = Detail();
            _explore.Detail = detail;
            _trips.Setup(t => t.CreateFromTemplateAsync(It.IsAny<ExploreTripDetail>(), It.IsAny<ExploreAddOptions>()))
                  .ThrowsAsync(new TripLimitException());
            var vm = Create();
            vm.Id = detail.Id.ToString();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.StartAddCommand.Execute(null);
            await vm.ConfirmAddCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), Resources.Strings.AppResources.Explore_TripLimit), Times.Once);
            Assert.True(vm.IsAddSheetOpen);
        }

        [Fact]
        public async Task AnArchivedSample_IsGone()
        {
            var vm = Create();
            vm.Id = Guid.NewGuid().ToString();

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.IsGone);
            Assert.False(vm.HasTrip);
        }

        [Fact]
        public void Budget_MustBeAValidAmount()
        {
            var vm = Create();
            vm.BudgetText = "abc";
            Assert.Null(vm.ParseBudget());
            vm.BudgetText = "1.250,50";
            Assert.Equal(125050, vm.ParseBudget());
            vm.BudgetText = "";
            Assert.Equal(0, vm.ParseBudget());
        }
    }
}
