using System.Globalization;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>User-facing texts for sharing (G6), shared by the members, trip and settings screens.</summary>
    public static class SharingTexts
    {
        public static string ErrorFor(SharingErrorKind kind) => kind switch
        {
            SharingErrorKind.Offline => AppResources.Sharing_ErrorOffline,
            SharingErrorKind.NotSignedIn => AppResources.Sharing_ErrorNotSignedIn,
            SharingErrorKind.NotUploaded => AppResources.Sharing_ErrorNotUploaded,
            SharingErrorKind.InvalidInvite => AppResources.Sharing_ErrorInvalidInvite,
            SharingErrorKind.Forbidden => AppResources.Sharing_ErrorForbidden,
            _ => AppResources.Sharing_ErrorUnknown,
        };

        public static string RoleName(TripRole role) => role switch
        {
            TripRole.Owner => AppResources.Role_Owner,
            TripRole.Editor => AppResources.Role_Editor,
            _ => AppResources.Role_Viewer,
        };

        /// <summary>"ABCD2345" → "ABCD 2345" (easier to read out / type).</summary>
        public static string FormatCode(string code) =>
            code.Length == SharingService.CodeLength ? $"{code[..4]} {code[4..]}" : code;

        /// <summary>"az önce", "5 dk önce", "3 sa önce", "2 gün önce", "12 Eylül".</summary>
        public static string RelativeTime(DateTime utc, DateTime nowUtc)
        {
            var elapsed = nowUtc - utc;
            if (elapsed < TimeSpan.FromMinutes(1))
                return AppResources.Time_JustNow;
            if (elapsed < TimeSpan.FromHours(1))
                return string.Format(CultureInfo.CurrentCulture, AppResources.Time_MinutesAgoFormat, (int)elapsed.TotalMinutes);
            if (elapsed < TimeSpan.FromDays(1))
                return string.Format(CultureInfo.CurrentCulture, AppResources.Time_HoursAgoFormat, (int)elapsed.TotalHours);
            if (elapsed < TimeSpan.FromDays(7))
                return string.Format(CultureInfo.CurrentCulture, AppResources.Time_DaysAgoFormat, (int)elapsed.TotalDays);
            return utc.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentCulture);
        }
    }
}
