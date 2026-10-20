using System.Diagnostics;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Views;

namespace TravelPlanner.Services
{
    public class DialogService : IDialogService
    {
        // The topmost page: a modal (e.g. the sign-in page, which asks "move the guest's trips?" while it is still
        // open) wins over the Shell page underneath it. iOS refuses to present an alert from a page that is
        // covered by a modal - the alert never appears and the awaiting code would hang.
        private static Page? CurrentPage
        {
            get
            {
                var shell = Shell.Current;
                var modal = shell?.Navigation.ModalStack.LastOrDefault();
                return modal ?? shell?.CurrentPage ?? Application.Current?.Windows.FirstOrDefault()?.Page;
            }
        }

        // One dialog at a time: a second request waits until the first is answered.
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static async Task<T> ShowAsync<T>(T whenNoPage, Func<INavigation, Task<T>> show)
        {
            await Gate.WaitAsync();
            try
            {
                return await MainThread.InvokeOnMainThreadAsync(async () =>
                    CurrentPage is { } page ? await show(page.Navigation) : whenNoPage);
            }
            finally
            {
                Gate.Release();
            }
        }

        public Task<bool> ConfirmAsync(string title, string message, string? accept = null, string? cancel = null) =>
            ShowAsync(false, async navigation =>
                await AppDialogPage.ShowCardAsync(navigation, title, message, accept ?? AppResources.Common_DeleteConfirm,
                    cancel ?? AppResources.Common_Cancel,
                    AppDialogPage.IsDestructive(accept) ? AppDialogPage.Tone.Danger : AppDialogPage.Tone.Info) is not null);

        public Task ShowErrorAsync(string message, Exception? exception = null)
        {
            if (exception is not null)
                Debug.WriteLine($"[TravelPlanner] {message} :: {exception}");

            return ShowAsync<string?>(null, navigation =>
                AppDialogPage.ShowCardAsync(navigation, AppResources.Common_Error, message, AppResources.Common_Ok, null, AppDialogPage.Tone.Error));
        }

        public Task ShowMessageAsync(string title, string message) =>
            ShowAsync<string?>(null, navigation =>
                AppDialogPage.ShowCardAsync(navigation, title, message, AppResources.Common_Ok, null, AppDialogPage.Tone.Info));

        public Task<string?> PromptAsync(string title, string message, string? initialValue = null, string? accept = null, string? cancel = null) =>
            ShowAsync<string?>(null, async navigation =>
            {
                var text = await AppDialogPage.ShowCardAsync(navigation, title, message, accept ?? AppResources.Common_SaveShort,
                    cancel ?? AppResources.Common_Cancel, AppDialogPage.Tone.Info, withInput: true, initialValue: initialValue);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            });

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string? cancel = null)
        {
            if (options.Count == 0)
                return Task.FromResult<string?>(null);
            return ShowAsync<string?>(null, async navigation =>
            {
                var choice = await AppDialogPage.ShowSheetAsync(navigation, title, options, cancel ?? AppResources.Common_Cancel);
                return choice is not null && options.Contains(choice) ? choice : null;
            });
        }
    }
}
