namespace TravelPlanner.Services
{
    /// <summary>
    /// Thin wrapper over the platform alert dialogs so ViewModels can ask for confirmation or report an
    /// error without reaching into Shell/Page directly (keeps them testable - TP-103 / TP-106).
    /// </summary>
    public interface IDialogService
    {
        /// <summary>
        /// Shows a yes/no dialog and returns true when the user accepts. Button texts default to the
        /// localized "Sil" / "Vazgeç" when not given.
        /// </summary>
        Task<bool> ConfirmAsync(string title, string message, string? accept = null, string? cancel = null);

        /// <summary>Logs <paramref name="exception"/> (if any) and shows a friendly, localized error message.</summary>
        Task ShowErrorAsync(string message, Exception? exception = null);

        /// <summary>Shows an informational message with an OK button.</summary>
        Task ShowMessageAsync(string title, string message);

        /// <summary>Asks for a line of text. Returns null when cancelled.</summary>
        Task<string?> PromptAsync(string title, string message, string? initialValue = null, string? accept = null, string? cancel = null);

        /// <summary>Lets the user pick one of <paramref name="options"/>. Returns null when cancelled.</summary>
        Task<string?> ChooseAsync(string title, IReadOnlyList<string> options, string? cancel = null);
    }
}
