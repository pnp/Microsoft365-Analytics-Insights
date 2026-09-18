using Common.Entities.Installer;
using DataUtils;
using System;
using UsageReporting;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    public class AnonUsageStatsModelLoader
    {
        /// <summary>
        /// Builds the shell of a telemetry report: its client id, its timestamp, and the settings
        /// description.
        /// </summary>
        /// <param name="tenantId">Used only to recognise (and retire) the legacy client id.</param>
        /// <param name="lastSettings">Last applied installer configuration, or null.</param>
        /// <param name="previousAnonClientId">
        /// The <c>AnonClientId</c> from this install's most recent stored report, or null if it has
        /// never reported. See <see cref="ResolveAnonClientId"/> for why it matters.
        /// </param>
        public static AnonUsageStatsModel Load(
            Guid tenantId, BaseSolutionInstallConfig lastSettings, string previousAnonClientId = null)
        {
            // UTC: Generated.Ticks feeds both the payload signature and the server-side document id, so a
            // local-time value would make those inconsistent across servers in different timezones (and shift
            // by an hour at DST boundaries).
            var model = new AnonUsageStatsModel() { Generated = DateTime.UtcNow };
            model.AnonClientId = ResolveAnonClientId(tenantId, previousAnonClientId);
            if (lastSettings != null && lastSettings.SolutionConfig != null)
            {
                model.ConfiguredImportsEnabledDescription = lastSettings.SolutionConfig.ImportTaskSettings?.ToSettingsString();
            }

            return model;
        }

        /// <summary>
        /// The legacy client id: an UNSALTED SHA-256 of the tenant GUID.
        /// </summary>
        /// <remarks>
        /// Kept only so <see cref="ResolveAnonClientId"/> can recognise it. Do not use it for new
        /// reports - see that method for why.
        /// </remarks>
        internal static string LegacyAnonClientId(Guid tenantId)
        {
            return StringUtils.GetHashedStringSimple(tenantId.ToString());
        }

        /// <summary>
        /// Picks the anonymous client id for this install, retiring the legacy one exactly once.
        /// </summary>
        /// <remarks>
        /// The original id was <c>SHA-256(tenantId)</c> with no salt. Entra tenant GUIDs are publicly
        /// discoverable - <c>login.microsoftonline.com/{domain}/.well-known/openid-configuration</c>
        /// returns one for any domain - so that id could be reversed by hashing a list of candidate
        /// tenants and comparing. It was a lookup, not a brute-force, and it made the "anonymous"
        /// telemetry re-identifiable by anyone holding it.
        ///
        /// That was already wrong; it became urgent when the payload started carrying Copilot seat
        /// counts, adoption rates and SKU mix, which are commercially sensitive when attached to a
        /// named customer.
        ///
        /// So: a random id, generated once and then carried forward from the install's own last stored
        /// report. There is no key/value settings table in this schema, and adding one would mean a
        /// migration plus a manual SQL upgrade script for what is otherwise an additive change - and
        /// the id is already persisted inside the serialised report in <c>sys_telemetry_reports</c>.
        ///
        /// Consequences, accepted deliberately:
        /// <list type="bullet">
        /// <item><description>Every existing install appears ONCE as a new client, and its history
        /// splits there. Unavoidable if the id is to stop being reversible.</description></item>
        /// <item><description>Reports are only stored after a SUCCESSFUL upload, so a failed upload
        /// means a different id is minted next cycle. That churn only happens when nothing reached the
        /// server, so it leaves no orphan documents behind.</description></item>
        /// <item><description>Truncating <c>sys_telemetry_reports</c> rotates the id.</description></item>
        /// </list>
        /// </remarks>
        internal static string ResolveAnonClientId(Guid tenantId, string previousAnonClientId)
        {
            if (string.IsNullOrWhiteSpace(previousAnonClientId))
            {
                // Never reported, or the stored report was unreadable: start fresh.
                return NewAnonClientId();
            }

            // The one-time retirement. Anything else is an id we minted before, so keep it - stability
            // is the whole point of reading it back.
            return string.Equals(previousAnonClientId, LegacyAnonClientId(tenantId), StringComparison.OrdinalIgnoreCase)
                ? NewAnonClientId()
                : previousAnonClientId;
        }

        private static string NewAnonClientId()
        {
            // Random, so it cannot be reversed to a tenant by anyone - including us. "N" keeps it a
            // plain hex string, which is what the Cosmos partition key and the older ids already were.
            return Guid.NewGuid().ToString("N");
        }
    }
}
