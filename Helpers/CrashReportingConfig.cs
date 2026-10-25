namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-70: crash reporting (Sentry). Off while <see cref="SentryDsn"/> is empty - the SDK isn't even started.
    /// To switch it on, create a Sentry project (platform ".NET MAUI"), paste its DSN here and update the Play Console
    /// data safety form (docs/store/DATA_SAFETY.md → "Crash logs", "Diagnostics"). The DSN is public by design: it
    /// only lets the app send events. Nothing personal is sent (see MauiProgram.UseCrashReporting).
    /// </summary>
    public static class CrashReportingConfig
    {
        public const string SentryDsn = "";

        public static bool IsEnabled => !string.IsNullOrWhiteSpace(SentryDsn);

        /// <summary>
        /// Drops the query (and anything after it) from a URL before it goes into a report: queries can carry
        /// coordinates, search text or tokens.
        /// </summary>
        public static string? StripQuery(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return url;
            var cut = url.IndexOfAny(new[] { '?', '#' });
            return cut < 0 ? url : url[..cut];
        }
    }
}
