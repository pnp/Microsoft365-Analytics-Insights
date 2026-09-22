using Common.Entities.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System.Collections.Generic;

namespace Common.Entities.TeamsExplorer
{
    /// <summary>
    /// Which Teams data sources this deployment has switched on.
    /// </summary>
    /// <remarks>
    /// Read from configuration rather than from the presence of rows, deliberately and for the same
    /// reason the Licence activity page does: an empty table is ambiguous (off, or on but genuinely
    /// quiet?) while a toggle is not. The one exception is the per-team authorisation count, which
    /// really is a data question - see <see cref="TeamsExplorerAvailability.AuthorisedTeams"/>.
    /// </remarks>
    public sealed class TeamsExplorerSources
    {
        /// <summary><c>GraphUsageReports</c> - fills the per-user daily Teams activity and device tables.</summary>
        public bool UsageReports { get; set; }

        /// <summary><c>Calls</c> - fills <c>call_records</c> and friends from the Graph webhook.</summary>
        public bool Calls { get; set; }

        /// <summary><c>GraphTeams</c> - fills teams, channels, tabs, reactions and channel statistics.</summary>
        public bool TeamsAnalytics { get; set; }

        /// <summary>Cognitive services configured, so sentiment / keywords / languages can be produced.</summary>
        public bool Cognitive { get; set; }

        /// <summary><c>GraphUsersMetadata</c> - fills department / country / office, so adoption can be sliced.</summary>
        public bool UserMetadata { get; set; }

        /// <summary>A Service Bus connection string exists - a hard prerequisite of the calls webhook.</summary>
        public bool ServiceBus { get; set; }

        /// <summary>Reads the toggles from application configuration.</summary>
        public static TeamsExplorerSources FromConfig(AppConfig config)
        {
            var settings = config?.ImportJobSettings ?? new ImportTaskSettings();

            return new TeamsExplorerSources
            {
                UsageReports = settings.GraphUsageReports,
                Calls = settings.Calls,
                TeamsAnalytics = settings.GraphTeams,
                UserMetadata = settings.GraphUsersMetadata,
                Cognitive = config != null && config.IsValidCognitiveConfig,
                ServiceBus = config != null
                    && config.ConnectionStrings != null
                    && !string.IsNullOrWhiteSpace(config.ConnectionStrings.ServiceBusConnectionString),
            };
        }
    }

    /// <summary>
    /// What the Teams Explorer can show on this deployment, and - in admin-facing prose - what is
    /// missing and how to switch it on.
    /// </summary>
    /// <remarks>
    /// Sections are never hidden on the strength of this: a hidden tab tells an admin nothing,
    /// whereas a tab that explains "there is no data because the Calls import is off, and here is
    /// what turning it on needs" is actionable. This mirrors <c>DlpAvailability</c>.
    /// </remarks>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public sealed class TeamsExplorerAvailability
    {
        /// <summary>Per-user daily Teams activity and device usage are being imported.</summary>
        public bool UsageReportsAvailable { get; set; }

        /// <summary>Teams call records are being imported.</summary>
        public bool CallsAvailable { get; set; }

        /// <summary>A Service Bus connection is configured for the Teams calls webhook queue.</summary>
        public bool ServiceBusAvailable { get; set; }

        /// <summary>Teams deep analytics (teams, channels, tabs, reactions) is being imported.</summary>
        public bool TeamsAnalyticsAvailable { get; set; }

        /// <summary>Cognitive enrichment (sentiment, key phrases, language) is configured.</summary>
        public bool CognitiveAvailable { get; set; }

        /// <summary>User metadata is being imported, so adoption can be sliced by demographic.</summary>
        public bool UserMetadataAvailable { get; set; }

        /// <summary>
        /// Teams that have been authorised for deep analytics on the admin Teams permissions page.
        /// Channel content only exists for these, so zero means the Teams &amp; channels and
        /// Conversations tabs will be empty however long the import has been running.
        /// </summary>
        public int AuthorisedTeams { get; set; }

        /// <summary>Teams discovered by the import, authorised or not.</summary>
        public int TotalTeams { get; set; }

        /// <summary>True when at least one source can produce data, i.e. the page is worth showing.</summary>
        public bool Available =>
            UsageReportsAvailable || CallsAvailable || TeamsAnalyticsAvailable;

        /// <summary>Admin-facing explanation of anything that is switched off or incomplete.</summary>
        public List<string> Reasons { get; set; } = new List<string>();

        /// <summary>
        /// Builds the availability model. <paramref name="authorisedTeams"/> and
        /// <paramref name="totalTeams"/> are null when the team counts could not be read, which is
        /// reported as "unknown" rather than as zero - claiming zero authorised teams because a query
        /// failed would send an admin to re-authorise teams that are already fine.
        /// </summary>
        public static TeamsExplorerAvailability Build(
            TeamsExplorerSources sources,
            int? authorisedTeams = null,
            int? totalTeams = null)
        {
            sources = sources ?? new TeamsExplorerSources();

            var model = new TeamsExplorerAvailability
            {
                UsageReportsAvailable = sources.UsageReports,
                CallsAvailable = sources.Calls,
                ServiceBusAvailable = sources.ServiceBus,
                TeamsAnalyticsAvailable = sources.TeamsAnalytics,
                CognitiveAvailable = sources.Cognitive,
                UserMetadataAvailable = sources.UserMetadata,
                AuthorisedTeams = authorisedTeams ?? 0,
                TotalTeams = totalTeams ?? 0,
            };

            if (!sources.UsageReports)
            {
                model.Reasons.Add(
                    "The Microsoft 365 usage reports import is switched off, so there is no per-user Teams "
                    + "activity: adoption, reach, engagement segments and the people leaderboards cannot be "
                    + "measured. Enable 'Graph usage reports' in the installer and grant the runtime app the "
                    + "'Reports.Read.All' application permission.");
            }

            if (!sources.Calls)
            {
                model.Reasons.Add(
                    "The Teams calls import is switched off, so there are no call records: meeting size, "
                    + "duration, time-of-day patterns, modalities and call quality cannot be shown. Enable "
                    + "'Teams calls' in the installer and grant the runtime app the 'CallRecords.Read.All' "
                    + "application permission.");
            }
            else if (!sources.ServiceBus)
            {
                model.Reasons.Add(
                    "The Teams calls import is switched on but no Service Bus connection is configured. Call "
                    + "notifications arrive on a Graph webhook that queues to Service Bus, so without it the "
                    + "webhook endpoint returns 503 and no calls are ever imported. Check the Service "
                    + "configuration page.");
            }

            if (!sources.TeamsAnalytics)
            {
                model.Reasons.Add(
                    "Teams deep analytics is switched off, so teams, channels, tabs, reactions and channel "
                    + "statistics are not imported: the Teams & channels and Conversation insights tabs will "
                    + "be empty. Enable 'Teams' in the installer.");
            }
            else if (totalTeams.HasValue && totalTeams.Value == 0)
            {
                model.Reasons.Add(
                    "Teams deep analytics is switched on but no teams have been discovered yet. The group "
                    + "crawl runs on the importer's schedule, so this is expected shortly after a first "
                    + "install.");
            }
            else if (authorisedTeams.HasValue && authorisedTeams.Value == 0)
            {
                model.Reasons.Add(
                    "No team has been authorised for deep analytics, so no channel messages, reactions or "
                    + "sentiment can be read. Channel content needs a delegated token per team - authorise "
                    + "them on the Teams permissions page under Administration.");
            }

            if (!sources.Cognitive)
            {
                model.Reasons.Add(
                    "Cognitive services are not configured, so channel messages are not scored: key phrases, "
                    + "detected languages and sentiment are unavailable. Set 'CognitiveEndpoint' (and either "
                    + "a key or the runtime service principal) to enable the Conversation insights tab.");
            }

            if (!sources.UserMetadata)
            {
                model.Reasons.Add(
                    "The Graph user metadata import is switched off, so department, country, office and job "
                    + "title are unknown: adoption cannot be broken down by demographic. Enable 'Graph users "
                    + "metadata' in the installer and grant 'User.Read.All' and 'Directory.Read.All'.");
            }

            return model;
        }
    }
}
