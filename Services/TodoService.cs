using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class TodoService : ITodoService
    {
        private readonly IDatabaseService _databaseService;

        public TodoService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<TodoItem>> GetByTripIdAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            return await db.Table<TodoItem>()
                .Where(t => t.TripId == tripId)
                .OrderBy(t => t.CreatedDate)
                .ToListAsync();
        }

        public async Task<int> SaveAsync(TodoItem todo)
        {
            Helpers.InputLimits.EnforceTextLimits(todo);
            todo.Title = Helpers.TextCase.CapitalizeFirst(todo.Title) ?? string.Empty;
            var db = await _databaseService.Connection;
            var saved = await db.SaveTrackedAsync(todo);
            Helpers.TripPlanChanges.Notify(todo.TripId);
            return saved;
        }

        public async Task<int> DeleteAsync(TodoItem todo)
        {
            var db = await _databaseService.Connection;
            var deleted = await db.DeleteTrackedAsync(todo);
            Helpers.TripPlanChanges.Notify(todo.TripId);
            return deleted;
        }
    }
}
