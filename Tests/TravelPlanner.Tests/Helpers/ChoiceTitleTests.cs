using System.Globalization;
using Xunit;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// Action sheet titles (IDialogService.ChooseAsync) can be cut off on Android, so they stay short - long
    /// explanations belong in an alert (ConfirmAsync). Covers the questions of TP-10, TP-12, TP-16 and the trip dates.
    /// </summary>
    public class ChoiceTitleTests
    {
        private const int MaxTitle = 45;

        [Theory]
        [InlineData("Backup_SharedTripsFormat", "12")]
        [InlineData("Account_DeleteSharedTripFormat", "Roma 2026", "12")]
        [InlineData("Account_ChooseNewOwnerFormat", "Roma 2026")]
        [InlineData("Account_SignOutPendingFormat", "120")]
        [InlineData("Account_SignOutTitle")]
        [InlineData("TripForm_OutsideTitleFormat", "12")]
        public void ChoiceTitles_AreShort_InBothLanguages(string key, params string[] args)
        {
            foreach (var culture in new[] { "tr", "en" })
            {
                // Read per culture instead of setting AppResources.Culture, which other tests share.
                var template = AppResources.ResourceManager.GetString(key, new CultureInfo(culture));
                Assert.NotNull(template);
                var text = string.Format(CultureInfo.InvariantCulture, template!, args);

                Assert.True(text.Length <= MaxTitle, $"{key} ({culture}) is {text.Length} characters: {text}");
                Assert.DoesNotContain("\n", text);
            }
        }
    }
}
