using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Wraps Trip CRUD plus the budget math the UI needs (total spent, remaining budget, usage %).
    /// The expense arithmetic lives in ExpenseService so it isn't duplicated - TripService just composes
    /// it with the Trip's PlannedBudget. Also keeps local trip notifications in sync on save/delete.
    /// </summary>
    public class TripService : ITripService
    {
        private readonly IDatabaseService _databaseService;
        private readonly IExpenseService _expenseService;
        private readonly INotificationService _notificationService;
        private readonly IAttachmentService _attachmentService;

        public TripService(
            IDatabaseService databaseService,
            IExpenseService expenseService,
            INotificationService notificationService,
            IAttachmentService attachmentService)
        {
            _databaseService = databaseService;
            _expenseService = expenseService;
            _notificationService = notificationService;
            _attachmentService = attachmentService;
        }

        public async Task<List<Trip>> GetAllAsync()
        {
            var db = await _databaseService.Connection;
            return await db.Table<Trip>().OrderBy(t => t.StartDate).ToListAsync();
        }

        public async Task<Trip?> GetByIdAsync(int id)
        {
            var db = await _databaseService.Connection;
            return await db.Table<Trip>().Where(t => t.Id == id).FirstOrDefaultAsync();
        }

        public async Task<int> SaveAsync(Trip trip)
        {
            Validate(trip);

            var db = await _databaseService.Connection;
            var result = await db.SaveTrackedAsync(trip);

            // Trip dates may have just changed (or this may be a brand-new trip) -
            // cancel whatever was scheduled before and reschedule against the latest data.
            // The trip is already saved at this point: a platform hiccup in the notification plugin (no
            // permission, OEM alarm restrictions...) must not turn that into an error dialog.
            try
            {
                _notificationService.CancelTripNotifications(trip.Id);
                await _notificationService.ScheduleTripNotificationsAsync(trip);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TripService] Scheduling reminders failed for trip {trip.Id}: {ex}");
            }

            return result;
        }

        /// <summary>
        /// Last line of defence behind the trip form (and for any other caller): text trimmed to its column
        /// limits, dates inside the supported range and in order, budget within <see cref="Helpers.InputLimits"/>.
        /// </summary>
        internal static void Validate(Trip trip)
        {
            ArgumentNullException.ThrowIfNull(trip);
            Helpers.InputLimits.EnforceTextLimits(trip);
            // Capitalised in the app language whatever the keyboard did ("roma tatili" -> "Roma tatili", "new york" -> "New York").
            trip.Name = Helpers.TextCase.CapitalizeFirst(trip.Name) ?? string.Empty;
            trip.City = Helpers.TextCase.CapitalizeWords(trip.City) ?? string.Empty;
            trip.Country = Helpers.TextCase.CapitalizeWords(trip.Country) ?? string.Empty;
            trip.Description = Helpers.TextCase.CapitalizeFirst(trip.Description);

            trip.StartDate = trip.StartDate.Date;
            trip.EndDate = trip.EndDate.Date;

            if (!Helpers.InputLimits.IsValidDate(trip.StartDate) || !Helpers.InputLimits.IsValidDate(trip.EndDate))
                throw new ArgumentOutOfRangeException(nameof(trip), "Trip dates are outside the supported range.");
            if (trip.EndDate < trip.StartDate)
                throw new ArgumentException("Trip end date is before its start date.", nameof(trip));
            if (!Helpers.InputLimits.IsValidMinor(trip.PlannedBudgetMinor))
                throw new ArgumentOutOfRangeException(nameof(trip), "Planned budget is outside the supported range.");
        }

        public async Task<(int Todos, int Expenses, int Places, int Activities)> GetRelatedCountsAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            var todos = await db.Table<TodoItem>().Where(t => t.TripId == tripId).CountAsync();
            var expenses = await db.Table<Expense>().Where(e => e.TripId == tripId).CountAsync();
            var places = await db.Table<Place>().Where(p => p.TripId == tripId).CountAsync();
            var activities = await db.Table<TripActivity>().Where(a => a.TripId == tripId).CountAsync();
            return (todos, expenses, places, activities);
        }

        public async Task<int> DeleteAsync(Trip trip)
        {
            var db = await _databaseService.Connection;
            var tripId = trip.Id;
            var deleted = 0;
            var attachments = new List<TripAttachment>();

            // TP-206: one transaction - either the trip and all of its to-dos, expenses, places and plan go,
            // or (if anything fails half-way) nothing does. SQLite doesn't enforce FKs here, so the
            // cascade is done by hand: children first (their trip is still there to resolve), the trip last.
            // G1: everything goes into ONE trash batch (restored together) and every row is queued for sync.
            await db.RunInTransactionAsync(conn =>
            {
                var batch = TrashBatch.For(trip);
                attachments = conn.Table<TripAttachment>().Where(a => a.TripId == tripId).ToList();

                var shares = conn.Query<ExpenseShare>(
                    "SELECT s.* FROM ExpenseShare s INNER JOIN Expense e ON e.Id = s.ExpenseId WHERE e.TripId = ?", tripId);
                conn.DeleteAllTracked(shares, batch);                                       // TP-504
                conn.DeleteAllTracked<Expense>(e => e.TripId == tripId, batch);
                conn.DeleteAllTracked<TodoItem>(t => t.TripId == tripId, batch);
                conn.DeleteAllTracked<TripActivity>(a => a.TripId == tripId, batch);         // TP-401 (before places)
                conn.DeleteAllTracked<Place>(p => p.TripId == tripId, batch);
                conn.DeleteAllTracked<Participant>(p => p.TripId == tripId, batch);          // TP-504
                conn.DeleteAllTracked<TripAttachment>(a => a.TripId == tripId, batch);      // TP-501
                deleted = conn.DeleteTracked(trip, batch);
            });

            _notificationService.CancelTripNotifications(tripId);

            // Files are outside SQLite, so they move only after the rows are safely gone: into the trash folder
            // (restorable with the trip), then the trip's now empty folder is removed.
            if (deleted > 0)
            {
                foreach (var attachment in attachments)
                    _attachmentService.MoveFileToTrash(attachment);
                _attachmentService.DeleteFilesForTrip(tripId);
            }

            return deleted;
        }

        public async Task<Trip?> DuplicateAsync(int tripId, string newName)
        {
            var db = await _databaseService.Connection;
            Trip? copy = null;

            await db.RunInTransactionAsync(conn =>
            {
                var source = conn.Find<Trip>(tripId);
                if (source is null)
                    return;

                copy = new Trip
                {
                    Name = newName,
                    Country = source.Country ?? string.Empty,
                    City = source.City ?? string.Empty,
                    StartDate = source.StartDate,
                    EndDate = source.EndDate,
                    PlannedBudgetMinor = source.PlannedBudgetMinor,
                    Description = source.Description,
                    CurrencyCode = source.EffectiveCurrencyCode,
                };
                Validate(copy);
                conn.InsertTracked(copy);
                var newTripId = copy.Id;

                // Places first: the day plan points at them.
                var placeIds = new Dictionary<int, int>();
                foreach (var place in conn.Table<Place>().Where(p => p.TripId == tripId).ToList())
                {
                    var placeCopy = new Place
                    {
                        TripId = newTripId,
                        Name = place.Name,
                        Description = place.Description,
                        Latitude = place.Latitude,
                        Longitude = place.Longitude,
                        IsVisited = false,
                    };
                    conn.InsertTracked(placeCopy);
                    placeIds[place.Id] = placeCopy.Id;
                }

                foreach (var activity in conn.Table<TripActivity>().Where(a => a.TripId == tripId).ToList())
                {
                    conn.InsertTracked(new TripActivity
                    {
                        TripId = newTripId,
                        Date = activity.Date,
                        StartTime = activity.StartTime,
                        EndTime = activity.EndTime,
                        Title = activity.Title,
                        Note = activity.Note,
                        PlaceId = activity.PlaceId is int placeId && placeIds.TryGetValue(placeId, out var newPlaceId) ? newPlaceId : null,
                        SortOrder = activity.SortOrder,
                    });
                }

                // CreatedDate is kept so the copied lists keep their order.
                foreach (var todo in conn.Table<TodoItem>().Where(t => t.TripId == tripId).ToList())
                {
                    conn.InsertTracked(new TodoItem
                    {
                        TripId = newTripId,
                        Title = todo.Title,
                        IsCompleted = false,
                        CreatedDate = todo.CreatedDate,
                    });
                }

                foreach (var participant in conn.Table<Participant>().Where(p => p.TripId == tripId).ToList())
                {
                    conn.InsertTracked(new Participant
                    {
                        TripId = newTripId,
                        Name = participant.Name,
                        CreatedDate = participant.CreatedDate,
                    });
                }
            });

            if (copy is not null)
            {
                try
                {
                    await _notificationService.ScheduleTripNotificationsAsync(copy);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TripService] Scheduling reminders failed for trip {copy.Id}: {ex}");
                }
            }

            return copy;
        }

        public async Task<Trip> CreateFromTemplateAsync(ExploreTripDetail template, ExploreAddOptions options)
        {
            ArgumentNullException.ThrowIfNull(template);
            ArgumentNullException.ThrowIfNull(options);

            var days = Math.Clamp(template.DurationDays, 1, Helpers.AppConstants.MaxTripDays);
            var start = options.StartDate.Date;
            var name = string.IsNullOrWhiteSpace(options.Name) ? template.Title : options.Name.Trim();
            var trip = new Trip
            {
                Name = name,
                Country = template.Country ?? string.Empty,
                City = template.City ?? string.Empty,
                StartDate = start,
                EndDate = start.AddDays(days - 1),
                PlannedBudgetMinor = options.PlannedBudgetMinor,
                Description = string.IsNullOrWhiteSpace(template.Summary) ? null : template.Summary.Trim(),
                CurrencyCode = Helpers.MoneyFormatter.Normalize(options.CurrencyCode),
            };
            Validate(trip);

            static bool Wanted(IReadOnlyCollection<Guid>? ids, Guid id) => ids is null || ids.Contains(id);

            var db = await _databaseService.Connection;
            await db.RunInTransactionAsync(conn =>
            {
                if (conn.Table<Trip>().Count() >= Helpers.AppConstants.MaxTrips)
                    throw new TripLimitException();

                conn.InsertTracked(trip);

                // Places first: the day plan points at them (template id -> new local id).
                var placeIds = new Dictionary<Guid, int>();
                foreach (var source in template.Places
                             .Where(p => Wanted(options.PlaceIds, p.Id) && !string.IsNullOrWhiteSpace(p.Name) &&
                                         PlaceService.IsValidCoordinate(p.Latitude, p.Longitude))
                             .Take(Helpers.AppConstants.MaxItemsPerTrip))
                {
                    var place = Helpers.InputLimits.EnforceTextLimits(new Place
                    {
                        TripId = trip.Id,
                        Name = source.Name.Trim(),
                        Description = string.IsNullOrWhiteSpace(source.Description) ? null : source.Description.Trim(),
                        Latitude = source.Latitude,
                        Longitude = source.Longitude,
                    });
                    conn.InsertTracked(place);
                    placeIds[source.Id] = place.Id;
                }

                // Day N of the sample = start date + (N - 1); days past the trip's end are left out.
                var sortOrders = new Dictionary<int, int>();
                foreach (var source in template.Activities
                             .Where(a => Wanted(options.ActivityIds, a.Id) && !string.IsNullOrWhiteSpace(a.Title) &&
                                         a.DayIndex >= 1 && a.DayIndex <= days)
                             .Take(Helpers.AppConstants.MaxItemsPerTrip))
                {
                    var startTime = AiPlannerService.ParseTime(source.StartTime);
                    var endTime = startTime is null ? null : AiPlannerService.ParseTime(source.EndTime);
                    if (endTime <= startTime)
                        endTime = null;

                    var order = sortOrders.GetValueOrDefault(source.DayIndex);
                    sortOrders[source.DayIndex] = order + 1;
                    conn.InsertTracked(Helpers.InputLimits.EnforceTextLimits(new TripActivity
                    {
                        TripId = trip.Id,
                        Date = start.AddDays(source.DayIndex - 1),
                        StartTime = startTime,
                        EndTime = endTime,
                        Title = source.Title.Trim(),
                        Note = string.IsNullOrWhiteSpace(source.Note) ? null : source.Note.Trim(),
                        PlaceId = source.PlaceId is Guid placeId && placeIds.TryGetValue(placeId, out var localId) ? localId : null,
                        SortOrder = order,
                    }));
                }

                // To-dos and packing items both become to-dos; CreatedDate keeps the sample's order.
                var created = DateTime.Now;
                var index = 0;
                foreach (var source in template.Todos
                             .Where(t => Wanted(options.TodoIds, t.Id) && !string.IsNullOrWhiteSpace(t.Title))
                             .Take(Helpers.AppConstants.MaxItemsPerTrip))
                {
                    conn.InsertTracked(Helpers.InputLimits.EnforceTextLimits(new TodoItem
                    {
                        TripId = trip.Id,
                        Title = source.Title.Trim(),
                        CreatedDate = created.AddSeconds(index++),
                    }));
                }
            });

            try
            {
                await _notificationService.ScheduleTripNotificationsAsync(trip);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TripService] Scheduling reminders failed for trip {trip.Id}: {ex}");
            }

            return trip;
        }

        // ---- Budget calculations ----

        public Task<decimal> GetTotalExpensesAsync(int tripId) =>
            _expenseService.GetTotalExpensesAsync(tripId);

        public async Task<decimal> GetRemainingBudgetAsync(int tripId)
        {
            var trip = await GetByIdAsync(tripId);
            if (trip is null) return 0;

            return await _expenseService.GetRemainingBudgetAsync(tripId, trip.PlannedBudget);
        }

        public async Task<double> GetBudgetUsagePercentageAsync(int tripId)
        {
            var trip = await GetByIdAsync(tripId);
            if (trip is null) return 0;

            return await _expenseService.GetBudgetUsagePercentageAsync(tripId, trip.PlannedBudget);
        }
    }

    /// <summary>TP-33: the device already has <see cref="Helpers.AppConstants.MaxTrips"/> trips - no new one is created.</summary>
    public sealed class TripLimitException : Exception
    {
        public TripLimitException() : base("Trip limit reached.")
        {
        }
    }
}
