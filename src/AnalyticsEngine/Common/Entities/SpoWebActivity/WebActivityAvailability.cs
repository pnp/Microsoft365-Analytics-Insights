using Common.Entities.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;

namespace Common.Entities.SpoWebActivity
{
    /// <summary>
    /// Which data sources the SharePoint web-activity page depends on, as this deployment has them
    /// configured.
    /// </summary>
    /// <remarks>
    /// Read from configuration rather than from the presence of rows, for the same reason the Teams
    /// Explorer and Licence activity pages do: an empty table is ambiguous (switched off, or switched
    /// on and genuinely quiet?) while a toggle is not. The two data questions that configuration
    /// cannot answer - has a hit ever arrived, and is the optional click capture on - are asked of the
    /// database separately.
    /// </remarks>
    public sealed class WebActivitySources
    {
        /// <summary><c>WebTraffic</c> - the App Insights page-view import that fills <c>dbo.hits</c>.</summary>
        public bool WebTraffic { get; set; }

        /// <summary><c>GraphUsersMetadata</c> - fills the directory, so reach has a denominator.</summary>
        public bool UserMetadata { get; set; }

        /// <summary>An Application Insights connection string is configured, without which nothing is imported.</summary>
        public bool AppInsightsConfigured { get; set; }

        /// <summary>Reads the toggles from application configuration.</summary>
        public static WebActivitySources FromConfig(AppConfig config)
        {
            var settings = config?.ImportJobSettings ?? new ImportTaskSettings();

            return new WebActivitySources
            {
                WebTraffic = settings.WebTraffic,
                UserMetadata = settings.GraphUsersMetadata,

                // The importer resolves the Application Insights resource from this connection string
                // (AppInsightsImporter.cs builds its AppInsightsAPIClient from it), so it is the single
                // thing whose absence means "no page views can ever arrive".
                AppInsightsConfigured = config != null
                    && !string.IsNullOrWhiteSpace(config.AppInsightsConnectionString),
            };
        }
    }

    /// <summary>
    /// What the web-activity page can show on this deployment, and - in admin-facing prose - what is
    /// missing and how to switch it on.
    /// </summary>
    /// <remarks>
    /// Tabs are never hidden on the strength of this. A hidden tab tells an admin nothing, whereas a
    /// tab that explains "there is no data because the tracker is not deployed, and here is what that
    /// needs" is actionable. This mirrors <c>TeamsExplorerAvailability</c> and <c>DlpAvailability</c>.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class WebActivityAvailability
    {
        /// <summary>The web-traffic (page hits) import is switched on.</summary>
        public bool WebTrafficAvailable { get; set; }

        /// <summary>Directory metadata is imported, so reach can be expressed as a share of the org.</summary>
        public bool UserMetadataAvailable { get; set; }

        /// <summary>An Application Insights resource is configured for the importer to read.</summary>
        public bool AppInsightsConfigured { get; set; }

        /// <summary>At least one page hit exists, ignoring the reporting window.</summary>
        public bool HasAnyHits { get; set; }

        /// <summary>The most recent page hit of any age, so an admin can see whether collection stalled.</summary>
        public DateTime? LastHitUtc { get; set; }

        /// <summary>At least one search has been recorded - the Search tab is otherwise always empty.</summary>
        public bool SearchAvailable { get; set; }

        /// <summary>At least one element click has been recorded by the tracker's optional click capture.</summary>
        public bool ClickTrackingAvailable { get; set; }

        /// <summary>True when the page has something to report.</summary>
        public bool Available => WebTrafficAvailable && HasAnyHits;

        /// <summary>Admin-facing explanation of anything that is switched off or incomplete.</summary>
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>
        /// Builds the availability model.
        /// </summary>
        /// <param name="sources">The configured import toggles.</param>
        /// <param name="lastHitUtc">
        /// The newest page hit of any age, or null when it could not be read. Null is reported as
        /// "unknown" rather than as "no data": telling an admin the tracker has collected nothing
        /// because a query failed would send them to redeploy a tracker that is working.
        /// </param>
        /// <param name="anySearches">Whether any search has ever been recorded, or null when unknown.</param>
        /// <param name="anyClicks">Whether any element click has ever been recorded, or null when unknown.</param>
        public static WebActivityAvailability Build(
            WebActivitySources sources,
            DateTime? lastHitUtc = null,
            bool? anySearches = null,
            bool? anyClicks = null,
            DateTime? nowUtc = null)
        {
            sources = sources ?? new WebActivitySources();

            var model = new WebActivityAvailability
            {
                WebTrafficAvailable = sources.WebTraffic,
                UserMetadataAvailable = sources.UserMetadata,
                AppInsightsConfigured = sources.AppInsightsConfigured,
                HasAnyHits = lastHitUtc.HasValue,
                LastHitUtc = lastHitUtc,
                SearchAvailable = anySearches ?? true,
                ClickTrackingAvailable = anyClicks ?? true,
            };

            if (!sources.WebTraffic)
            {
                model.Reasons.Add(
                    "The web traffic import is switched off, so no SharePoint page views are collected and "
                    + "every tab on this page will be empty. Enable 'Web traffic' in the installer.");
            }
            else if (!sources.AppInsightsConfigured)
            {
                model.Reasons.Add(
                    "The web traffic import is switched on but no Application Insights connection string is "
                    + "configured. Page views are read from Application Insights, so nothing can be imported "
                    + "until it is set. Check the Service configuration page.");
            }
            else if (!lastHitUtc.HasValue)
            {
                model.Reasons.Add(
                    "The web traffic import is switched on and configured, but no page view has ever arrived. "
                    + "The usual cause is that the SharePoint tracker is not deployed: add the AI Tracker app "
                    + "to the site collections you want reported on, and confirm it points at the same "
                    + "Application Insights resource this deployment reads.");
            }
            else
            {
                var reference = (nowUtc ?? DateTime.UtcNow);
                var staleDays = (reference - lastHitUtc.Value).TotalDays;

                if (staleDays >= StaleCollectionDays)
                {
                    model.Reasons.Add(string.Format(
                        "The most recent page view is {0:N0} days old ({1:yyyy-MM-dd}). Collection appears to "
                        + "have stopped - check the importer on the Service health page, and confirm the "
                        + "Application Insights resource still has data retained for the period you are asking "
                        + "about.",
                        staleDays,
                        lastHitUtc.Value));
                }
            }

            if (!sources.UserMetadata)
            {
                model.Reasons.Add(
                    "The Graph user metadata import is switched off, so the size of the organisation is "
                    + "unknown and reach cannot be shown as a percentage. Visitor counts are still accurate. "
                    + "Enable 'Graph users metadata' in the installer and grant 'User.Read.All'.");
            }

            if (anySearches.HasValue && !anySearches.Value)
            {
                model.Reasons.Add(
                    "No searches have ever been recorded, so the Web searches tab will be empty. Search terms "
                    + "are captured by the SharePoint tracker on the search results page - if your search "
                    + "centre is on a site the tracker is not deployed to, it will never see them.");
            }

            if (anyClicks.HasValue && !anyClicks.Value)
            {
                model.Reasons.Add(
                    "No element clicks have ever been recorded. Click capture is an optional part of the "
                    + "SharePoint tracker, so this is expected unless it has been enabled - only the "
                    + "'what visitors clicked' panel on the Journeys tab depends on it.");
            }

            return model;
        }

        /// <summary>
        /// Days without a page view after which collection is reported as having stopped.
        /// </summary>
        /// <remarks>
        /// Three days rather than one. The import runs on a schedule and Application Insights itself
        /// publishes with a short delay, so a one-day threshold would cry wolf every weekend on a
        /// quiet intranet.
        /// </remarks>
        public const int StaleCollectionDays = 3;
    }
}
