using DataUtils.Sql;
using System;

namespace WebJob.Office365ActivityImporter.Engine.ActivityAPI.Dlp
{
    /// <summary>
    /// Staging row for one DLP policy match that affected a Copilot interaction - one row per
    /// (accessed resource x policy x rule), produced by <see cref="CopilotDlpRules.ExtractMatches"/>.
    /// </summary>
    /// <remarks>
    /// <c>is_blocked</c> is computed in C# and carried here rather than re-derived in the merge SQL.
    /// "What counts as a block" is the entire meaning of this report - it depends on RuleMode, the
    /// actions list and the resource Status - and having a second implementation of it in T-SQL would be
    /// two things to keep in step, with the SQL one impossible to unit test. The rules live in
    /// <see cref="CopilotDlpRules"/>, which is pure and covered by tests; the SQL just stores the verdict.
    /// </remarks>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_COPILOT_DLP)]
    internal class CopilotDlpLogTempEntity
    {
        /// <summary>The Copilot interaction (audit event id) this match belongs to.</summary>
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("policy_id", true)]
        public string PolicyId { get; set; }

        [Column("policy_name", true)]
        public string PolicyName { get; set; }

        [Column("rule_id", true)]
        public string RuleId { get; set; }

        [Column("rule_name", true)]
        public string RuleName { get; set; }

        [Column("severity", true)]
        public string Severity { get; set; }

        [Column("rule_mode", true)]
        public string RuleMode { get; set; }

        /// <summary>The single most meaningful action of the rule, e.g. "BlockAccess".</summary>
        [Column("action_name", true)]
        public string ActionName { get; set; }

        [Column("resource_name", true)]
        public string ResourceName { get; set; }

        [Column("resource_type", true)]
        public string ResourceType { get; set; }

        [Column("sensitivity_label_id", true)]
        public string SensitivityLabelId { get; set; }

        /// <summary>Whether the user actually did not get the content. See the remarks on this class.</summary>
        [Column("is_blocked")]
        public bool IsBlocked { get; set; }
    }

    /// <summary>
    /// Staging row for one DLP rule match from the standalone <c>DLP.All</c> activity feed - one row per
    /// (audit event x policy x rule).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="CopilotDlpLogTempEntity"/> these records carry no agent identity, so they feed
    /// the tenant-wide policy view rather than the per-agent one.
    /// </remarks>
    [TempTableName(ActivityImportConstants.STAGING_TABLE_DLP_RULE_MATCH)]
    internal class DlpRuleMatchLogTempEntity
    {
        [Column("event_id")]
        public Guid EventId { get; set; }

        [Column("policy_id", true)]
        public string PolicyId { get; set; }

        [Column("policy_name", true)]
        public string PolicyName { get; set; }

        [Column("rule_id", true)]
        public string RuleId { get; set; }

        [Column("rule_name", true)]
        public string RuleName { get; set; }

        [Column("severity", true)]
        public string Severity { get; set; }

        [Column("rule_mode", true)]
        public string RuleMode { get; set; }

        [Column("action_name", true)]
        public string ActionName { get; set; }

        [Column("is_blocked")]
        public bool IsBlocked { get; set; }
    }
}
