using System.Diagnostics;
using TravelPlanner.Resources.Strings;

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

        public Task<bool> ConfirmAsync(string title, string message, string? accept = null, string? cancel = null)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = CurrentPage;
                if (page is null)
                    return false;

                return await page.DisplayAlertAsync(title, message, accept ?? AppResources.Common_DeleteConfirm, cancel ?? AppResources.Common_Cancel);
            });
        }

        public Task ShowErrorAsync(string message, Exception? exception = null)
        {
            if (exception is not null)
                Debug.WriteLine($"[TravelPlanner] {message} :: {exception}");

            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = CurrentPage;
                if (page is null)
                    return;

                await page.DisplayAlertAsync(AppResources.Common_Error, message, AppResources.Common_Ok);
            });
        }
    
        public Task ShowMessageAsync(string title, string message)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = CurrentPage;
                if (page is null)
                    return;

                await page.DisplayAlertAsync(title, message, AppResources.Common_Ok);
            });
        }

        public Task<string?> PromptAsync(string title, string message, string? initialValue = null, string? accept = null, string? cancel = null)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = CurrentPage;
                if (page is null)
                    return null;

                var text = await page.DisplayPromptAsync(title, message,
                    accept ?? AppResources.Common_SaveShort, cancel ?? AppResources.Common_Cancel, initialValue: initialValue ?? string.Empty);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            });
        }

        public Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string? cancel = null)
        {
            return MainThread.InvokeOnMainThreadAsync(async () =>
            {
                var page = CurrentPage;
                if (page is null || options.Count == 0)
                    return null;

                var cancelText = cancel ?? AppResources.Common_Cancel;
                var choice = await page.DisplayActionSheetAsync(title, cancelText, null, options.ToArray());
                return choice is null || choice == cancelText || !options.Contains(choice) ? null : choice;
            });
        }
    }
}
