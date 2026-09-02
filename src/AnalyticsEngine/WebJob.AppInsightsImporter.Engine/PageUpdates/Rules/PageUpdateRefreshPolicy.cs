using System;

namespace WebJob.AppInsightsImporter.Engine.PageUpdates.Rules
{
    /// <summary>
    /// The metadata refresh-suppression rule: a URL whose metadata was refreshed recently is skipped, so a
    /// page that emits page-update events all day is not re-written on every import cycle.
    ///
    /// Extracted from <c>PageUpdateManager.SaveChunk</c>, which read <c>DateTime.UtcNow</c> inline inside the
    /// EF query and so could only be observed by running the import against a database. See issue #369.
    /// Takes the instant as a parameter, following the <c>ImportCadenceGate.ShouldRun(..., DateTime nowUtc)</c>
    /// convention.
    /// </summary>
    public static class PageUpdateRefreshPolicy
    {
        /// <summary>
        /// URLs whose <c>MetadataLastRefreshed</c> is <c>null</c> or strictly older than this instant are
        /// re-read; everything else is left alone until the window elapses.
        ///
        /// <paramref name="metadataRefreshMinutes"/> comes from <c>AppConfig.MetadataRefreshMinutes</c>, which
        /// already falls back to its 24-hour default for a missing, unparseable or negative app setting
        /// (see <c>AppConfigDefaultsTests.MetadataRefreshMinutes_*</c>), so this does not re-validate it.
        /// </summary>
        public static DateTime StaleBeforeUtc(int metadataRefreshMinutes, DateTime nowUtc)
        {
            return nowUtc.AddMinutes(-metadataRefreshMinutes);
        }
    }
}
