using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Common.Entities.Entities.AgentCosts
{
    /// <summary>
    /// Which Copilot Studio "harness" a billed row belongs to.
    ///
    /// The licensing API does not return a harness field. It is inferred from
    /// <see cref="CopilotStudioCreditDaily.FeatureName"/>, so an unrecognised feature must land on
    /// <see cref="Unknown"/> rather than being guessed into one of the real harnesses - the same reasoning as
    /// <c>CopilotTenantGroundingBasis</c>: a value we cannot classify must stay visibly unclassified.
    ///
    /// Stored as a string rather than an enum so an older reader of a persisted row cannot silently coerce a
    /// value it does not know into the first enum member.
    /// </summary>
    public static class CopilotStudioHarness
    {
        /// <summary>The feature name was absent, so no inference was attempted.</summary>
        public const string NotAssessed = "NotAssessed";

        /// <summary>
        /// A feature name we do recognise, but which does not identify a harness. This is the honest answer
        /// for the classic/generative Copilot Studio features that both the Standard and Copilot Chat
        /// harnesses produce - the API gives no way to tell those two apart.
        /// </summary>
        public const string StandardOrCopilotChat = "StandardOrCopilotChat";

        /// <summary>
        /// Agents running on the GitHub Copilot harness.
        /// </summary>
        /// <remarks>
        /// Inferred from the "Process Agent" feature name. This mapping is <b>community-observed, not
        /// documented by Microsoft</b>, so it is deliberately a single well-known string rather than a
        /// pattern - if Microsoft changes or extends it, rows land on <see cref="Unknown"/> and the gap is
        /// visible, instead of quietly mis-attributing spend to a harness.
        /// </remarks>
        public const string GitHubCopilot = "GitHubCopilot";

        /// <summary>A feature name this product does not recognise at all.</summary>
        public const string Unknown = "Unknown";
    }

    /// <summary>
    /// One row of billed Microsoft Copilot Studio consumption ("Copilot Credits"), as reported by the
    /// Power Platform licensing API for the <c>MCSMessages</c> entitlement.
    ///
    /// <para><b>Grain:</b> usage date x environment x agent x (feature, channel, model, tool, knowledge
    /// source). The same agent on the same day produces several rows when those dimensions differ.</para>
    ///
    /// <para><b>There is deliberately no user FK on this table.</b> This is the per-AGENT view: Microsoft
    /// reports it at the environment and agent level, with a distinct-user <i>count</i>
    /// (<see cref="DistinctUsers"/>) rather than identities. Per-user figures come from a different endpoint
    /// and live in <see cref="CopilotStudioCreditUserDaily"/>; a nullable user FK here would suggest this
    /// table could be attributed per user, which it cannot.</para>
    ///
    /// <para>This is <b>billed</b> consumption from Microsoft, and is not the same thing as the per-conversation
    /// <c>CopilotCreditEstimation</c> this product derives from audit events. The two will disagree - the
    /// estimate is an inference over audit data, this is what Microsoft charged - so they are kept in separate
    /// tables rather than reconciled into one.</para>
    /// </summary>
    [Table("copilot_studio_credit_daily")]
    public class CopilotStudioCreditDaily : AbstractEFEntity
    {
        /// <summary>The usage day this consumption belongs to (date only; the API reports a daily grain).</summary>
        [Column("usage_date")]
        public DateTime UsageDate { get; set; }

        [Column("environment_id")]
        [MaxLength(200)]
        public string EnvironmentId { get; set; }

        /// <summary>
        /// Resolved separately - the consumption rows carry only the environment id, so this is populated from
        /// the environment-management API and stays null when that lookup is unavailable.
        /// </summary>
        [Column("environment_name")]
        [MaxLength(255)]
        public string EnvironmentName { get; set; }

        /// <summary>
        /// The agent ("resource") the credits were billed against.
        /// </summary>
        /// <remarks>
        /// Held as a plain string with <b>no foreign key to <c>copilot_studio_bots</c></b>. That table's
        /// <c>bot_id</c> comes from the Management Activity audit feed, and nothing verifies that it uses the
        /// same identifier as the licensing API's <c>resourceId</c>. Populating a FK on an unproven equality
        /// would manufacture a join that may be silently wrong, and a FK that never matches reads as "this
        /// agent had no usage".
        /// </remarks>
        [Column("agent_id")]
        [MaxLength(200)]
        public string AgentId { get; set; }

        [Column("agent_name")]
        [MaxLength(255)]
        public string AgentName { get; set; }

        /// <summary>
        /// Inferred harness - one of the <see cref="CopilotStudioHarness"/> constants. See that type for why
        /// this is an inference rather than a reported field.
        /// </summary>
        [Column("harness")]
        [MaxLength(50)]
        public string Harness { get; set; }

        /// <summary>
        /// The billing feature the credits were charged under, verbatim from the API - e.g. "Generative
        /// answer", "Tenant graph grounding", "Process Agent". Kept raw as well as classified into
        /// <see cref="Harness"/> so a future harness mapping can be recomputed from stored data.
        /// </summary>
        [Column("feature_name")]
        [MaxLength(200)]
        public string FeatureName { get; set; }

        [Column("channel_id")]
        [MaxLength(200)]
        public string ChannelId { get; set; }

        [Column("llm_model")]
        [MaxLength(200)]
        public string LlmModel { get; set; }

        [Column("tool_invoked")]
        [MaxLength(400)]
        public string ToolInvoked { get; set; }

        [Column("knowledge_sources")]
        [MaxLength(400)]
        public string KnowledgeSources { get; set; }

        /// <summary>Copilot Credits actually charged for this slice.</summary>
        [Column("billed_credits")]
        public decimal BilledCredits { get; set; }

        /// <summary>
        /// Credits consumed but not charged (for example covered by an included allowance). Reported by the
        /// API as <c>NonBillableQuantity</c>; null when the API did not return it.
        /// </summary>
        [Column("non_billed_credits")]
        public decimal? NonBilledCredits { get; set; }

        /// <summary>
        /// How many distinct users contributed to this slice. A <b>count only</b> - the API never returns who
        /// they were. Null when the caller did not request the users field or the API omitted it.
        /// </summary>
        [Column("distinct_users")]
        public int? DistinctUsers { get; set; }

        /// <summary>When Microsoft last recalculated this row, if reported.</summary>
        [Column("last_refreshed_utc")]
        public DateTime? LastRefreshedUtc { get; set; }

        /// <summary>
        /// SHA-256 (hex) of the dimension values that identify this slice within its usage date. The
        /// dimensions are collectively far too wide for a SQL Server index key (the limit is 1700 bytes, and
        /// these are Unicode at 2 bytes per character), so the natural key is expressed as this fixed-width
        /// hash instead. Together with <see cref="UsageDate"/> it is the upsert key.
        /// </summary>
        [Column("dimension_hash")]
        [MaxLength(64)]
        [Required]
        public string DimensionHash { get; set; }

        [Column("imported_utc")]
        public DateTime ImportedUtc { get; set; }

        public override string ToString()
        {
            return $"{UsageDate:yyyy-MM-dd} {AgentName ?? AgentId} [{FeatureName}]: {BilledCredits} credit(s)";
        }
    }

    /// <summary>
    /// Billed Copilot Studio credit consumption attributed to an individual user, per day.
    ///
    /// <para>Microsoft added the per-user entitlement routes
    /// (<c>/licensing/entitlements/{id}/users</c> and friends) in July 2026. Before that, Copilot Studio
    /// consumption could only be seen per agent and per environment, which is why
    /// <see cref="CopilotStudioCreditDaily"/> deliberately carries no user column - the two tables come from
    /// different endpoints and neither is derived from the other.</para>
    ///
    /// <para><b>The user is stored as the raw identifier the API returns, with no foreign key to
    /// <c>dbo.users</c>.</b> Microsoft documents <c>userId</c> only as a string; whether it is an Entra
    /// object id, a UPN or something else is not stated, so joining it to the user table would rest on an
    /// assumption rather than a fact. A join built on a guess produces confidently wrong attribution, which
    /// on a spend report is worse than no join at all.</para>
    /// </summary>
    [Table("copilot_studio_credit_user_daily")]
    public class CopilotStudioCreditUserDaily : AbstractEFEntity
    {
        [Column("usage_date")]
        public DateTime UsageDate { get; set; }

        /// <summary>
        /// The user identifier exactly as the licensing API reported it. See the class remarks for why this
        /// is not a foreign key.
        /// </summary>
        [Column("user_id")]
        [MaxLength(200)]
        public string UserId { get; set; }

        [Column("environment_id")]
        [MaxLength(200)]
        public string EnvironmentId { get; set; }

        [Column("environment_name")]
        [MaxLength(255)]
        public string EnvironmentName { get; set; }

        /// <summary>
        /// The agent these credits were spent on, when the row came from the per-agent user breakdown.
        /// Null when the row is the user's total across all agents.
        /// </summary>
        [Column("agent_id")]
        [MaxLength(200)]
        public string AgentId { get; set; }

        [Column("billed_credits")]
        public decimal BilledCredits { get; set; }

        [Column("unit")]
        [MaxLength(50)]
        public string Unit { get; set; }

        /// <summary>
        /// SHA-256 (hex) of the identifying dimensions, for the same reason as
        /// <see cref="CopilotStudioCreditDaily.DimensionHash"/> - the natural key is wider than a SQL Server
        /// index key allows, and the import re-reads a trailing window.
        /// </summary>
        [Column("dimension_hash")]
        [MaxLength(64)]
        [Required]
        public string DimensionHash { get; set; }

        [Column("imported_utc")]
        public DateTime ImportedUtc { get; set; }

        public override string ToString()
        {
            return $"{UsageDate:yyyy-MM-dd} {UserId}: {BilledCredits} credit(s)";
        }
    }

    /// <summary>
    /// A daily snapshot of the tenant's whole <c>MCSMessages</c> (Copilot Credits) entitlement - what it is
    /// entitled to, what it has consumed, and whether it is in overage.
    ///
    /// Kept separate from <see cref="CopilotStudioCreditDaily"/> because it answers a different question
    /// ("are we about to run out of capacity?" rather than "where did the spend go?") and because it is a
    /// point-in-time tenant total that must not be summed alongside per-agent rows.
    /// </summary>
    [Table("copilot_studio_credit_capacity")]
    public class CopilotStudioCreditCapacity : AbstractEFEntity
    {
        /// <summary>When this product read the figures.</summary>
        [Column("snapshot_utc")]
        public DateTime SnapshotUtc { get; set; }

        /// <summary>
        /// The usage day Microsoft's consumption figure is current as of, which normally lags
        /// <see cref="SnapshotUtc"/>. Null when the API did not report it.
        /// </summary>
        [Column("consumption_as_of")]
        public DateTime? ConsumptionAsOf { get; set; }

        [Column("entitled")]
        public decimal? Entitled { get; set; }

        /// <summary>Consumed so far, on the basis named by <see cref="ConsumptionType"/>.</summary>
        [Column("consumed")]
        public decimal? Consumed { get; set; }

        /// <summary>What the consumed figure is measured over, e.g. "MonthToDate".</summary>
        [Column("consumption_type")]
        [MaxLength(50)]
        public string ConsumptionType { get; set; }

        [Column("allocated")]
        public decimal? Allocated { get; set; }

        [Column("available")]
        public decimal? Available { get; set; }

        /// <summary>Consumption billed as pay-as-you-go rather than against pre-purchased capacity.</summary>
        [Column("pay_as_you_go_consumed")]
        public decimal? PayAsYouGoConsumed { get; set; }

        /// <summary>Capacity status as reported, e.g. "WithinCapacity", "Overage", "CoveredOverage".</summary>
        [Column("status")]
        [MaxLength(50)]
        public string Status { get; set; }

        public override string ToString()
        {
            return $"{SnapshotUtc:u}: consumed {Consumed} of {Entitled} ({Status})";
        }
    }

    /// <summary>
    /// One row of Azure spend from Microsoft Cost Management, at a daily grain.
    ///
    /// <para><b>There is deliberately no user FK.</b> Azure billing is resource-scoped: no Cost Management
    /// surface - including the full cost-details export - carries a user identity. Cost can be attributed to a
    /// meter, a resource and a subscription, and no further.</para>
    ///
    /// <para><b>Rows are restated.</b> Azure re-estimates an open billing period several times a day and only
    /// finalises it after the invoice, so an importer must re-read a trailing window and <i>upsert</i> on
    /// <see cref="RowHash"/> rather than append. <see cref="IsEstimated"/> records whether the figure was
    /// still provisional when it was read.</para>
    /// </summary>
    [Table("azure_cost_daily")]
    public class AzureCostDaily : AbstractEFEntity
    {
        /// <summary>The usage day the cost was incurred on.</summary>
        [Column("usage_date")]
        public DateTime UsageDate { get; set; }

        /// <summary>
        /// The Cost Management scope the row was read from, e.g. <c>/subscriptions/{id}</c>. Stored because a
        /// deployment may import more than one scope, and costs from different scopes must not be conflated.
        /// </summary>
        [Column("scope")]
        [MaxLength(400)]
        public string Scope { get; set; }

        [Column("subscription_id")]
        [MaxLength(100)]
        public string SubscriptionId { get; set; }

        [Column("resource_id")]
        [MaxLength(850)]
        public string ResourceId { get; set; }

        [Column("resource_group")]
        [MaxLength(255)]
        public string ResourceGroup { get; set; }

        [Column("service_name")]
        [MaxLength(255)]
        public string ServiceName { get; set; }

        [Column("meter_category")]
        [MaxLength(255)]
        public string MeterCategory { get; set; }

        [Column("meter_sub_category")]
        [MaxLength(255)]
        public string MeterSubCategory { get; set; }

        [Column("meter_name")]
        [MaxLength(255)]
        public string MeterName { get; set; }

        /// <summary>
        /// Cost in the billing currency named by <see cref="Currency"/> - the figure that appears on the
        /// invoice. Never aggregate across rows with different currencies without converting first.
        /// </summary>
        [Column("cost")]
        public decimal Cost { get; set; }

        [Column("currency")]
        [MaxLength(10)]
        public string Currency { get; set; }

        /// <summary>Metered quantity, when the query returned it.</summary>
        [Column("quantity")]
        public decimal? Quantity { get; set; }

        /// <summary>
        /// True when the row was read from a billing period Azure had not yet finalised, so the figure can
        /// still change. Recorded rather than inferred from the date, because the finalisation lag depends on
        /// the agreement type.
        /// </summary>
        [Column("is_estimated")]
        public bool IsEstimated { get; set; }

        /// <summary>
        /// SHA-256 (hex) of the identifying columns for this row. Same reasoning as
        /// <see cref="CopilotStudioCreditDaily.DimensionHash"/>: the natural key (scope + resource id + meter
        /// + currency) is far wider than a SQL Server index key allows. With <see cref="UsageDate"/> this is
        /// the upsert key that makes re-importing a restated window idempotent.
        /// </summary>
        [Column("row_hash")]
        [MaxLength(64)]
        [Required]
        public string RowHash { get; set; }

        [Column("imported_utc")]
        public DateTime ImportedUtc { get; set; }

        public override string ToString()
        {
            return $"{UsageDate:yyyy-MM-dd} {MeterName}: {Cost} {Currency}";
        }
    }

    /// <summary>
    /// One row per agent-cost import attempt, so the Health page can answer "did this work, and why not"
    /// without scanning the fact tables. Modelled on <c>CopilotUsageReportImportLog</c>, and for the same
    /// reason: several of the failure modes here return a perfectly successful-looking empty result.
    ///
    /// A tenant with no Copilot Studio agents, an app registration that has not been granted the Power
    /// Platform reader role, and a cost query whose meter filter matches nothing all produce zero rows. Only
    /// an explicit record tells those apart.
    /// </summary>
    [Table("agent_cost_import_log")]
    public class AgentCostImportLog : AbstractEFEntity
    {
        /// <summary>Which import ran - one of the <see cref="AgentCostImportNames"/> constants.</summary>
        [Column("import_name")]
        [MaxLength(100)]
        public string ImportName { get; set; }

        [Column("imported_utc")]
        public DateTime ImportedUtc { get; set; }

        /// <summary>First usage day requested (inclusive).</summary>
        [Column("window_from")]
        public DateTime? WindowFrom { get; set; }

        /// <summary>Last usage day requested (inclusive).</summary>
        [Column("window_to")]
        public DateTime? WindowTo { get; set; }

        /// <summary>Rows parsed out of the API response.</summary>
        [Column("rows_read")]
        public int RowsRead { get; set; }

        /// <summary>Rows inserted or updated in SQL.</summary>
        [Column("rows_saved")]
        public int RowsSaved { get; set; }

        /// <summary>
        /// Populated when the import failed, so the Health page can show the reason rather than an
        /// indistinguishable "no data".
        /// </summary>
        [Column("error")]
        [MaxLength(1000)]
        public string Error { get; set; }

        public override string ToString()
        {
            return $"{ImportName} @ {ImportedUtc:u}: read {RowsRead}, saved {RowsSaved}"
                + (string.IsNullOrEmpty(Error) ? string.Empty : $" - ERROR: {Error}");
        }
    }

    /// <summary>Stable <see cref="AgentCostImportLog.ImportName"/> values.</summary>
    public static class AgentCostImportNames
    {
        public const string CopilotStudioCredits = "CopilotStudioCredits";
        public const string CopilotStudioCapacity = "CopilotStudioCapacity";
        public const string CopilotStudioUserCredits = "CopilotStudioUserCredits";
        public const string AzureCostManagement = "AzureCostManagement";
    }
}
