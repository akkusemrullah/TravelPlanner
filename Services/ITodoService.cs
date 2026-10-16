using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>To-do CRUD for a trip. Implemented by TodoService.</summary>
    public interface ITodoService
    {
        Task<List<TodoItem>> GetByTripIdAsync(int tripId);
        Task<int> SaveAsync(TodoItem todo);
        Task<int> DeleteAsync(TodoItem todo);
    }
}
