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
        public bool CopilotDlpAvailable { get; set; }

        /// <summary>True when the DLP.All import is on, so tenant-wide DLP rule matches can exist.</summary>
        public bool TenantDlpAvailable { get; set; }

        /// <summary>True when either source can produce data - i.e. the page is worth showing.</summary>
        public bool Available => CopilotDlpAvailable || TenantDlpAvailable;

        /// <summary>Admin-facing explanation of anything that is switched off.</summary>
        public List<string> Reasons { get; set; } = new List<string>();
    }

    /// <summary>One named thing (agent, user, policy, label) ranked by how often DLP affected it.</summary>
    public class DlpImpactRow
    {
        /// <summary>Stable identifier, for the UI's key. Null when the source did not supply one.</summary>
        public string Id { get; set; }

        public string Name { get; set; }

        /// <summary>Policy matches that actually denied content.</summary>
        public int BlockedCount { get; set; }

        /// <summary>Policy matches that only matched and audited/notified.</summary>
        public int AuditedCount { get; set; }

        /// <summary>Distinct users affected. Null where the dimension is itself a user.</summary>
        public int? UsersAffected { get; set; }

        public int TotalCount => BlockedCount + AuditedCount;
    }

    /// <summary>A day's block/audit counts, for the trend chart.</summary>
    public class DlpTrendPoint
    {
        public DateTime Date { get; set; }
        public int BlockedCount { get; set; }
        public int AuditedCount { get; set; }
    }

    /// <summary>
    /// Everything the DLP page renders, for one reporting window.
    /// </summary>
    public class DlpSummary
    {
        public DateTime FromUtc { get; set; }
        public DateTime ToUtc { get; set; }

        /// <summary>Copilot policy matches in the window that denied a resource.</summary>
        public int CopilotBlockedCount { get; set; }

        /// <summary>Copilot policy matches in the window that only audited/notified.</summary>
        public int CopilotAuditedCount { get; set; }

        /// <summary>Distinct users whose Copilot use was blocked.</summary>
        public int UsersImpacted { get; set; }

        /// <summary>Distinct agents whose Copilot use was blocked.</summary>
        public int AgentsImpacted { get; set; }

        /// <summary>Distinct DLP policies responsible for at least one Copilot match.</summary>
        public int PoliciesInvolved { get; set; }

        public List<DlpImpactRow> TopAgents { get; set; } = new List<DlpImpactRow>();
        public List<DlpImpactRow> TopUsers { get; set; } = new List<DlpImpactRow>();
        public List<DlpImpactRow> TopPolicies { get; set; } = new List<DlpImpactRow>();
        public List<DlpImpactRow> TopSensitivityLabels { get; set; } = new List<DlpImpactRow>();
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
        public List<DlpImpactRow> TenantTopPolicies { get; set; } = new List<DlpImpactRow>();

        /// <summary>Blocked rule matches across the tenant (all workloads) in the window.</summary>
        public int TenantBlockedCount { get; set; }

        /// <summary>Audited-only rule matches across the tenant in the window.</summary>
        public int TenantAuditedCount { get; set; }
    }
}
