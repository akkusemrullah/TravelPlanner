using System.Diagnostics;

namespace TravelPlanner.Helpers
{
    public static class TaskExtensions
    {
        /// <summary>
        /// Safe replacement for "_ = SomeAsync();" - the task still runs in the background, but an
        /// exception is logged (and optionally handled) instead of becoming an unobserved task exception.
        /// </summary>
        public static async void FireAndForget(this Task task, Action<Exception>? onError = null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TravelPlanner] Background task failed: {ex}");
                onError?.Invoke(ex);
            }
        }
    }
}
