using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AuditLog
{
    #region Shared DLP dimensions

    /// <summary>
    /// A Microsoft Purview Data Loss Prevention policy, as named in audit data.
    /// </summary>
    /// <remarks>
    /// Shared by both DLP sources: the policy detail embedded in a Copilot interaction
    /// (<see cref="CopilotDlpEvent"/>) and the standalone DLP rule matches from the
    /// <c>DLP.All</c> activity feed (<see cref="DlpRuleMatch"/>). One dimension means the same policy
    /// is one row however it was observed, so "which policies are biting" is a single group-by.
    /// </remarks>
    [Table("dlp_policies")]
    public class DlpPolicy : AbstractEFEntity
    {
        /// <summary>
        /// The policy GUID from the audit payload. nvarchar rather than uniqueidentifier: the audit feed
        /// is not schema-guaranteed to send a parseable GUID here (the Copilot-embedded PolicyDetails
        /// shape is documented only in prose), and an unparseable id must still be recorded rather than
        /// dropping the match.
        /// </summary>
        [Column("policy_id")]
        [MaxLength(200)]
        public string PolicyId { get; set; }

        /// <summary>
        /// Admin-authored policy name. nvarchar - a customer names their policies in their own language.
        /// </summary>
        [Column("name")]
        [MaxLength(400)]
        public string Name { get; set; }
    }

    /// <summary>
    /// A rule within a <see cref="DlpPolicy"/>.
    /// </summary>
    [Table("dlp_rules")]
    public class DlpRule : AbstractEFEntity
    {
        [Column("rule_id")]
        [MaxLength(200)]
        public string RuleId { get; set; }

        [Column("name")]
        [MaxLength(400)]
        public string Name { get; set; }

        [ForeignKey(nameof(Policy))]
        [Column("dlp_policy_id")]
        public int? DlpPolicyId { get; set; }
        public DlpPolicy Policy { get; set; }

        /// <summary>"Low", "Medium" or "High".</summary>
        [Column("severity")]
        [MaxLength(50)]
        public string Severity { get; set; }

        /// <summary>
        /// "Enforce", "Audit with Notify" or "Audit only". Stored because it is what separates a rule
        /// that actually blocked a user from one that only reported - see <c>CopilotDlpRules</c>.
        /// </summary>
        [Column("rule_mode")]
        [MaxLength(50)]
        public string RuleMode { get; set; }
    }

    /// <summary>
    /// Lookup of the action a DLP rule took, e.g. "BlockAccess", "NotifyUser", "GenerateIncidentReport".
    /// Dimensioned because the value set is tiny and repeats on every match row.
    /// </summary>
    /// <remarks>
    /// The Management Activity API schema does not enumerate the permitted values, so this is a lookup
    /// populated from whatever the feed sends rather than a fixed enum.
    /// </remarks>
    [Table("dlp_actions")]
    public class DlpAction : AbstractEFEntityWithName
    {
    }

    #endregion

    #region Copilot-embedded DLP (agent attribution)

    /// <summary>
    /// A DLP policy match that affected a Microsoft 365 Copilot interaction - one row per
    /// (accessed resource x policy x rule).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only table that can answer "which AGENT is being blocked". DLP policies scoped to the
    /// "Microsoft 365 Copilot and Copilot Chat" location do not emit standalone <c>DlpRuleMatch</c>
    /// records on the <c>DLP.All</c> feed - Microsoft documents that feed's workloads as Exchange
    /// Online, Endpoint and SharePoint/OneDrive only, and a DLP record carries the human <c>UserId</c>
    /// with no agent identity. The Copilot block signal is embedded in the interaction record instead
    /// (<c>AccessedResources[].Status</c> / <c>PolicyDetails</c>), which is where the agent is named.
    /// https://learn.microsoft.com/en-us/purview/audit-copilot
    /// </para>
    /// <para>
    /// Deliberately a SEPARATE, sparse table rather than extra columns on
    /// <c>copilot_event_accessed_resources</c>. That table is the largest Copilot table (rows =
    /// interactions x resources) and its de-duplication merge seeks on a measured composite index
    /// (see migration <c>WidenCopilotAccessedResourceDedupIndex</c>); widening its de-dup tuple would
    /// invalidate that benchmark and slow the hot import path for every customer, to carry columns that
    /// are NULL on the overwhelming majority of rows. Policy-affected resources are rare, so a side
    /// table keyed on the chat is both cheaper and purely additive.
    /// </para>
    /// </remarks>
    [Table("copilot_dlp_events")]
    public class CopilotDlpEvent : AbstractEFEntity
    {
        /// <summary>
        /// The Copilot interaction this match belongs to. Joining here gives the user, timestamp and -
        /// critically - the agent, all of which are already on <c>copilot_chats</c>.
        /// </summary>
        [ForeignKey(nameof(RelatedChat))]
        [Column("copilot_chat_id")]
        public Guid ChatId { get; set; }
        public CopilotChat RelatedChat { get; set; }

        [ForeignKey(nameof(Policy))]
        [Column("dlp_policy_id")]
        public int? DlpPolicyId { get; set; }
        public DlpPolicy Policy { get; set; }

        [ForeignKey(nameof(Rule))]
        [Column("dlp_rule_id")]
        public int? DlpRuleId { get; set; }
        public DlpRule Rule { get; set; }

        [ForeignKey(nameof(Action))]
        [Column("dlp_action_id")]
        public int? DlpActionId { get; set; }
        public DlpAction Action { get; set; }

        /// <summary>
        /// Name of the resource Copilot was stopped from using. Reuses the existing Copilot
        /// accessed-resource-name dimension so a file blocked in one interaction and read in another is
        /// the same row.
        /// </summary>
        [ForeignKey(nameof(ResourceName))]
        [Column("resource_name_id")]
        public int? ResourceNameId { get; set; }
        public CopilotAccessedResourceName ResourceName { get; set; }

        [ForeignKey(nameof(ResourceType))]
        [Column("resource_type_id")]
        public int? ResourceTypeId { get; set; }
        public CopilotAccessedResourceType ResourceType { get; set; }

        /// <summary>
        /// Sensitivity label on the blocked resource. The most common Copilot DLP policy shape is
        /// "prevent Copilot processing content with label X", so this is the usual explanation of why.
        /// </summary>
        [ForeignKey(nameof(SensitivityLabel))]
        [Column("sensitivity_label_id")]
        public int? SensitivityLabelId { get; set; }
        public SensitivityLabel SensitivityLabel { get; set; }

        /// <summary>
        /// True when the user did NOT get the content. False when the policy matched but only audited or
        /// notified - reporting the two separately is the difference between "your users are blocked"
        /// and "your policies would block if you switched them to Enforce".
        /// </summary>
        [Column("is_blocked")]
        public bool IsBlocked { get; set; }
    }

    #endregion

    #region DLP.All feed (tenant-wide policy dimension)

    /// <summary>
    /// A DLP rule match from the <c>DLP.All</c> activity feed - one row per (audit event x policy x rule).
    /// </summary>
    /// <remarks>
    /// Covers Exchange Online, SharePoint/OneDrive and Endpoint DLP. These records carry the human
    /// <c>UserId</c> and no agent identity, so they answer "which policies are firing across the tenant
    /// and who is hitting them", NOT "which agent was blocked". The two must not be joined on user+time
    /// to guess an agent - see <see cref="CopilotDlpEvent"/> for the agent-attributable source.
    /// https://learn.microsoft.com/en-us/office/office-365-management-api/office-365-management-activity-api-schema#dlp-schema
    /// </remarks>
    [Table("dlp_rule_matches")]
    public class DlpRuleMatch : AbstractEFEntity
    {
        [ForeignKey(nameof(AuditEvent))]
        [Column("event_id")]
        public Guid EventId { get; set; }
        public CommonAuditEvent AuditEvent { get; set; }

        [ForeignKey(nameof(Policy))]
        [Column("dlp_policy_id")]
        public int? DlpPolicyId { get; set; }
        public DlpPolicy Policy { get; set; }

        [ForeignKey(nameof(Rule))]
        [Column("dlp_rule_id")]
        public int? DlpRuleId { get; set; }
        public DlpRule Rule { get; set; }

        [ForeignKey(nameof(Action))]
        [Column("dlp_action_id")]
        public int? DlpActionId { get; set; }
        public DlpAction Action { get; set; }

        /// <summary>
        /// True when the rule was enforcing a blocking action. See <c>CopilotDlpRules</c> for the
        /// classification, which is shared with the Copilot-embedded source so both report "blocked"
        /// the same way.
        /// </summary>
        [Column("is_blocked")]
        public bool IsBlocked { get; set; }
    }

    #endregion
}
