using Microsoft.Maui.ApplicationModel.Communication;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Opens the user's mail app with a message to <see cref="AppConstants.SupportEmail"/> (help centre, data requests,
    /// reporting AI content). A short line with the app version and platform is added so a reply can be specific;
    /// nothing else about the user or their trips is included.
    /// </summary>
    public static class SupportContact
    {
        /// <summary>Opens the mail composer; returns false when no mail app could be opened.</summary>
        public static async Task<bool> ComposeAsync(string subject, string? body = null)
        {
            var text = (body ?? string.Empty) + DiagnosticsFooter();

            try
            {
                if (Email.Default.IsComposeSupported)
                {
                    await Email.Default.ComposeAsync(new EmailMessage
                    {
                        Subject = subject,
                        Body = text,
                        To = new List<string> { AppConstants.SupportEmail },
                    });
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupportContact] Email composer failed: {ex.Message}");
            }

            try
            {
                var uri = new Uri($"mailto:{AppConstants.SupportEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(text)}");
                return await Launcher.Default.TryOpenAsync(uri);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupportContact] mailto failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Mail app first; when there is none, the address is copied and shown so the user can write from anywhere.
        /// </summary>
        public static async Task ComposeOrShowAddressAsync(IDialogService dialogs, string subject, string? body = null)
        {
            if (await ComposeAsync(subject, body))
                return;

            try
            {
                await Clipboard.Default.SetTextAsync(AppConstants.SupportEmail);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SupportContact] Clipboard failed: {ex.Message}");
            }

            await dialogs.ShowMessageAsync(AppResources.Help_ContactTitle,
                string.Format(AppResources.Help_NoMailAppFormat, AppConstants.SupportEmail));
        }

        private static string DiagnosticsFooter()
        {
            try
            {
                return $"\n\n—\nTravel Planner {AppInfo.Current.VersionString} ({AppInfo.Current.BuildString}) · " +
                       $"{DeviceInfo.Current.Platform} {DeviceInfo.Current.VersionString}";
            }
            catch
            {
                return string.Empty; // not available (tests, unsupported platform)
            }
        }
    }
}
