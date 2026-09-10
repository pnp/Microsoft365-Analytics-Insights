using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace Web.AnalyticsWeb.Models.Dlp
{
    /// <summary>
    /// Whether this deployment can show the DLP page at all, and what is missing if it cannot.
    /// </summary>
    /// <remarks>
    /// There are deliberately TWO independent switches here because the two DLP sources have different
    /// prerequisites, and conflating them is the mistake this model exists to prevent:
    /// <list type="bullet">
    /// <item><see cref="CopilotDlpAvailable"/> needs only the Copilot audit import. DLP policies scoped
    /// to the "Microsoft 365 Copilot and Copilot Chat" location report themselves inside the Copilot
    /// interaction record, so this - the part that names the AGENT - needs no DLP permission.</item>
    /// <item><see cref="TenantDlpAvailable"/> needs the separate DLP.All import and its
    /// <c>ActivityFeed.ReadDlp</c> grant, and it can never name an agent.</item>
    /// </list>
    /// </remarks>
    public class DlpAvailability
    {
        /// <summary>True when the Copilot audit import is on, so agent-attributable DLP data can exist.</summary>
        [JsonProperty("copilotDlpAvailable")]
        public bool CopilotDlpAvailable { get; set; }

        /// <summary>True when the DLP.All import is on, so tenant-wide DLP rule matches can exist.</summary>
        [JsonProperty("tenantDlpAvailable")]
        public bool TenantDlpAvailable { get; set; }

        /// <summary>True when either source can produce data - i.e. the page is worth showing.</summary>
        [JsonProperty("available")]
        public bool Available => CopilotDlpAvailable || TenantDlpAvailable;

        /// <summary>Admin-facing explanation of anything that is switched off.</summary>
        [JsonProperty("reasons")]
        public List<string> Reasons { get; set; } = new List<string>();
    }

    /// <summary>One named thing (agent, user, policy, label) ranked by how often DLP affected it.</summary>
    public class DlpImpactRow
    {
        /// <summary>Stable identifier, for the UI's key. Null when the source did not supply one.</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Policy matches that actually denied content.</summary>
        [JsonProperty("blockedCount")]
        public int BlockedCount { get; set; }

        /// <summary>Policy matches that only matched and audited/notified.</summary>
        [JsonProperty("auditedCount")]
        public int AuditedCount { get; set; }

        /// <summary>Distinct users affected. Null where the dimension is itself a user.</summary>
        [JsonProperty("usersAffected")]
        public int? UsersAffected { get; set; }

        /// <summary>
        /// For an agent row, the individual policies that affected it, ranked the same way. Null on
        /// every other kind of row.
        /// </summary>
        /// <remarks>
        /// Nested rather than returned as a flat (agent, policy) list so the UI does not have to
        /// re-join two collections to answer "which policies blocked THIS agent" - the question the
        /// breakdown exists for. Null (not empty) when it does not apply, so the payload of the
        /// non-agent tables is unchanged.
        /// </remarks>
        [JsonProperty("policies", NullValueHandling = NullValueHandling.Ignore)]
        public List<DlpImpactRow> Policies { get; set; }

        [JsonProperty("totalCount")]
        public int TotalCount => BlockedCount + AuditedCount;
    }

    /// <summary>A day's block/audit counts, for the trend chart.</summary>
    public class DlpTrendPoint
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }
        [JsonProperty("blockedCount")]
        public int BlockedCount { get; set; }
        [JsonProperty("auditedCount")]
        public int AuditedCount { get; set; }
    }

    /// <summary>
    /// Everything the DLP page renders, for one reporting window.
    /// </summary>
    public class DlpSummary
    {
        [JsonProperty("fromUtc")]
        public DateTime FromUtc { get; set; }
        [JsonProperty("toUtc")]
        public DateTime ToUtc { get; set; }

        /// <summary>Copilot policy matches in the window that denied a resource.</summary>
        [JsonProperty("copilotBlockedCount")]
        public int CopilotBlockedCount { get; set; }

        /// <summary>Copilot policy matches in the window that only audited/notified.</summary>
        [JsonProperty("copilotAuditedCount")]
        public int CopilotAuditedCount { get; set; }

        /// <summary>Distinct users whose Copilot use was blocked.</summary>
        [JsonProperty("usersImpacted")]
        public int UsersImpacted { get; set; }

        /// <summary>Distinct agents whose Copilot use was blocked.</summary>
        [JsonProperty("agentsImpacted")]
        public int AgentsImpacted { get; set; }

        /// <summary>Distinct DLP policies responsible for at least one Copilot match.</summary>
        [JsonProperty("policiesInvolved")]
        public int PoliciesInvolved { get; set; }

        [JsonProperty("topAgents")]
        public List<DlpImpactRow> TopAgents { get; set; } = new List<DlpImpactRow>();
        [JsonProperty("topUsers")]
        public List<DlpImpactRow> TopUsers { get; set; } = new List<DlpImpactRow>();
        [JsonProperty("topPolicies")]
        public List<DlpImpactRow> TopPolicies { get; set; } = new List<DlpImpactRow>();
        [JsonProperty("topSensitivityLabels")]
        public List<DlpImpactRow> TopSensitivityLabels { get; set; } = new List<DlpImpactRow>();
        [JsonProperty("trend")]
        public List<DlpTrendPoint> Trend { get; set; } = new List<DlpTrendPoint>();

        /// <summary>
        /// Tenant-wide DLP policies from the DLP.All feed, shown as a SEPARATE section.
        /// </summary>
        /// <remarks>
        /// Kept apart from the Copilot figures on purpose. These records carry the human UserId and no
        /// agent identity at all, so they cannot be attributed to an agent - and they must not be joined
        /// to Copilot interactions on user+time to guess one, because that would manufacture attribution
        /// the audit data does not support.
        /// </remarks>
        [JsonProperty("tenantTopPolicies")]
        public List<DlpImpactRow> TenantTopPolicies { get; set; } = new List<DlpImpactRow>();

        /// <summary>Blocked rule matches across the tenant (all workloads) in the window.</summary>
        [JsonProperty("tenantBlockedCount")]
        public int TenantBlockedCount { get; set; }

        /// <summary>Audited-only rule matches across the tenant in the window.</summary>
        [JsonProperty("tenantAuditedCount")]
        public int TenantAuditedCount { get; set; }
    }
}
