using Common.Entities.CopilotCosts;
using Common.Entities.Entities.AgentCosts;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotInvestment
{
    /// <summary>
    /// One person's (or one unresolved directory object's) latest preview credit balance as of the report window:
    /// the newest observation in the snapshot band, aggregated across its service categories.
    /// </summary>
    /// <remarks>
    /// A balance, not spend. The source's accounting period is unverified, so nothing here is a monthly or daily
    /// cost, successive observations are never added together, and a missing row is "not observed", never zero.
    /// </remarks>
    public sealed class CreditUserFact
    {
        public Guid ObjectId { get; set; }
        public int? UserId { get; set; }
        public DateTime ObservationDate { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public byte Quality { get; set; }
        public string PeriodStatus { get; set; }
        public DateTime? SourceMonth { get; set; }
        public int ServiceRows { get; set; }
        public int CreditRows { get; set; }
        /// <summary>Consumed quantity across categories reported in <c>copilotCredit</c>; null when none was.</summary>
        public double? Credits { get; set; }
        public int OtherUnitRows { get; set; }
        public int UnknownConsumptionRows { get; set; }
        public int NotAllowedRows { get; set; }
        public int LimitExhaustedRows { get; set; }
        public int NotEntitledRows { get; set; }
        public int NearLimitRows { get; set; }
        public DateTimeOffset? OldestFreshness { get; set; }
        public DateTimeOffset? NewestFreshness { get; set; }
        public int UnevaluatedRows { get; set; }
        public int NoPolicyRows { get; set; }

        public bool Partial => Quality < 2 || UnevaluatedRows > NoPolicyRows;
    }

    /// <summary>One category of a latest balance that matters to the report: consumption, an unknown amount, a refusal or a non-credit unit.</summary>
    public sealed class CreditServiceFact
    {
        public Guid ObjectId { get; set; }
        public string Category { get; set; }
        public string PolicyId { get; set; }
        public double? Consumed { get; set; }
        public string Unit { get; set; }
        public bool? Allowed { get; set; }
        public string DecisionReason { get; set; }
        public DateTimeOffset? SourceModified { get; set; }
        public string PerUserLimitType { get; set; }
        public double? PerUserLimit { get; set; }
        public string RemainingType { get; set; }
        public double? Remaining { get; set; }

        public bool IsCredit => string.Equals(Unit, CopilotInvestmentSql.CreditUnit, StringComparison.Ordinal);
    }

    /// <summary>A category the source could not evaluate for one person, with the source's reason.</summary>
    public sealed class CreditUnevaluatedFact
    {
        public Guid ObjectId { get; set; }
        public string Category { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>One collector sweep (run) inside the window: did it start, finish the inventory, and complete.</summary>
    public sealed class CreditSweepFact
    {
        public DateTime InventoryDate { get; set; }
        public bool InventoryComplete { get; set; }
        public int ContextPhase { get; set; }
        public DateTimeOffset? LastCompleted { get; set; }
        public string Outcome { get; set; }
    }

    /// <summary>The newest complete policy and tenant-balance context. Tenant-wide by nature: never narrowed to a filter.</summary>
    public sealed class CreditContextFact
    {
        public DateTimeOffset? ObservedAt { get; set; }
        public List<CostPolicyContext> Policies { get; set; } = new List<CostPolicyContext>();
        public Dictionary<string, List<CostPolicyServiceContext>> PolicyServices { get; set; } = new Dictionary<string, List<CostPolicyServiceContext>>(StringComparer.Ordinal);
        public Dictionary<string, int> PolicyGroupCounts { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<CostTenantBalance> TenantBalances { get; set; } = new List<CostTenantBalance>();
    }

    /// <summary>Everything the report reads from the preview credit store for one window.</summary>
    public sealed class CreditFacts
    {
        /// <summary>False when the preview tables do not exist yet (database not upgraded).</summary>
        public bool TablesPresent { get; set; }
        public DateTime? FirstObservationDate { get; set; }
        public DateTime? SnapshotDate { get; set; }
        public DateTime? SnapshotBandFrom { get; set; }
        public List<CreditUserFact> Users { get; set; } = new List<CreditUserFact>();
        public List<CreditServiceFact> Services { get; set; } = new List<CreditServiceFact>();
        public List<CreditUnevaluatedFact> Unevaluated { get; set; } = new List<CreditUnevaluatedFact>();
        public List<CreditSweepFact> Sweeps { get; set; } = new List<CreditSweepFact>();
        public CostCollectorStatus Collector { get; set; }
        public CreditContextFact Context { get; set; }
        public string FailureCode { get; set; }
    }

    /// <summary>One person's actual billed Copilot Studio credits from the daily per-user ledger.</summary>
    public sealed class StudioUserFact
    {
        public int? UserId { get; set; }
        public double Credits { get; set; }
        public double PriorCredits { get; set; }
        public int ActiveDays { get; set; }
    }

    public sealed class StudioFacts
    {
        public bool HasData { get; set; }
        public DateTime? LastImportUtc { get; set; }
        public string LastImportError { get; set; }
        public List<StudioUserFact> Users { get; set; } = new List<StudioUserFact>();
        public string FailureCode { get; set; }

        /// <summary>The ledger's first day, whatever the window.</summary>
        public DateTime? EarliestDate { get; set; }

        /// <summary>The ledger's latest stored day, whatever the window: how far it reaches, not a promise of completeness.</summary>
        public DateTime? LatestDate { get; set; }

        /// <summary>Whether the ledger covers the whole equal-length period before the window, so the two compare like for like.</summary>
        public bool PriorComplete { get; set; }
    }

    /// <summary>A sampled snapshot of balances: users observed on one date and the credits their balances showed.</summary>
    public sealed class CreditTrendPoint
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("observedUsers")]
        public long ObservedUsers { get; set; }

        [JsonProperty("consumingUsers")]
        public long ConsumingUsers { get; set; }

        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("limitExhaustedUsers")]
        public long LimitExhaustedUsers { get; set; }
    }

    /// <summary>One person's evidence detail for the drill-down, read on demand by user id.</summary>
    public sealed class PersonDetailFacts
    {
        public List<PersonActivityRow> Activity { get; set; } = new List<PersonActivityRow>();
        public bool ActivityAssessed { get; set; }
        public List<PersonPromptRow> Prompts { get; set; } = new List<PersonPromptRow>();
        public bool PromptsAssessed { get; set; }
        public List<PersonCategoryRow> PromptCategories { get; set; } = new List<PersonCategoryRow>();
        public List<CreditServiceFact> CreditServices { get; set; } = new List<CreditServiceFact>();
        public List<CreditUnevaluatedFact> CreditUnevaluated { get; set; } = new List<CreditUnevaluatedFact>();
        public List<PersonBalancePoint> Balances { get; set; } = new List<PersonBalancePoint>();
        public List<PersonStudioEnvironmentRow> StudioEnvironments { get; set; } = new List<PersonStudioEnvironmentRow>();
        public HashSet<int> CoworkAgentIds { get; set; } = new HashSet<int>();
    }

    public sealed class PersonActivityRow
    {
        public DateTime ActivityDate { get; set; }
        public string AppHost { get; set; }
        public int? AgentId { get; set; }
        public long Turns { get; set; }
    }

    public sealed class PersonPromptRow
    {
        public string AppClass { get; set; }
        public string InteractionType { get; set; }
        public long Interactions { get; set; }
        public int Days { get; set; }
    }

    public sealed class PersonCategoryRow
    {
        public string CategoryId { get; set; }
        public string HumanMode { get; set; }
        public long Prompts { get; set; }
    }

    public sealed class PersonBalancePoint
    {
        [JsonProperty("date")]
        public DateTime Date { get; set; }

        [JsonProperty("credits")]
        public double? Credits { get; set; }

        [JsonProperty("quality")]
        public int Quality { get; set; }

        [JsonProperty("limitExhausted")]
        public bool LimitExhausted { get; set; }
    }

    /// <summary>
    /// One person's billed Studio credits in one Power Platform environment. The per-user ledger is kept by day, person and
    /// environment: Microsoft's per-user route does not name the agent, so the importer never stores one there.
    /// </summary>
    public sealed class PersonStudioEnvironmentRow
    {
        public string EnvironmentId { get; set; }
        public string EnvironmentName { get; set; }
        public double Credits { get; set; }
        public int ActiveDays { get; set; }
    }

    /// <summary>The SQL behind the investment report's own facts. Every statement reads existing tables only.</summary>
    public static class CopilotInvestmentSql
    {
        /// <summary>The only unit the report adds up as credits. Matched exactly: another spelling is another unit.</summary>
        public const string CreditUnit = "copilotCredit";

        /// <summary>
        /// Days before the newest observation that still count as "latest". A sweep over a large directory runs
        /// in bounded slices and can straddle midnight, so one person's latest reading may be a day or two older
        /// than another's; older readings are reported as not recently observed rather than shown as current.
        /// </summary>
        public const int SnapshotBandDays = 3;

        /// <summary>How far back from the window end the newest observation may be and still be called the snapshot.</summary>
        public const int SnapshotLookbackDays = 31;

        /// <summary>At most this many sample dates in a balance trend, so a long window never reads every day.</summary>
        public const int MaxTrendPoints = 13;

        public const string TablesPresentSql =
            "SELECT CASE WHEN OBJECT_ID(N'dbo.copilot_cost_observations', N'U') IS NOT NULL\r\n" +
            "             AND OBJECT_ID(N'dbo.copilot_cost_services', N'U') IS NOT NULL\r\n" +
            "             AND OBJECT_ID(N'dbo.copilot_cost_runs', N'U') IS NOT NULL THEN 1 ELSE 0 END;";

        /// <summary>The newest observation date on or before the window end, and the first one ever recorded.</summary>
        public const string SnapshotDateSql =
            "SELECT (SELECT MAX(o.observation_date) FROM dbo.copilot_cost_observations AS o\r\n" +
            "        WHERE o.observation_date <= @to AND o.observation_date >= @lookbackFrom) AS SnapshotDate,\r\n" +
            "       (SELECT MIN(o.observation_date) FROM dbo.copilot_cost_observations AS o) AS FirstObservationDate;";

        /// <summary>
        /// The latest observation per directory object within the snapshot band, then three result sets: one row
        /// per object, the categories that matter (consumption, an unknown amount, a refusal or a non-credit unit),
        /// and the categories the source could not evaluate. Tenant-wide by design: rows carry the user id and
        /// are narrowed in memory, so every reader's filter applies to the same cached read. The work table is keyed
        /// like the observation and service tables (date, then object) so it joins them in their own order: at 200,000
        /// synthetic users, 1.80M logical reads and 2.45 s on LocalDB against 2.38M and 2.89 s keyed on the object alone.
        /// </summary>
        public const string LatestBalancesSql =
            "SET NOCOUNT ON;\r\n" +
            "IF OBJECT_ID(N'tempdb..#latest_balance') IS NOT NULL DROP TABLE #latest_balance;\r\n" +
            "CREATE TABLE #latest_balance (object_id uniqueidentifier NOT NULL, observation_date date NOT NULL, PRIMARY KEY (observation_date, object_id));\r\n" +
            "INSERT INTO #latest_balance (object_id, observation_date)\r\n" +
            "SELECT o.object_id, MAX(o.observation_date)\r\n" +
            "FROM dbo.copilot_cost_observations AS o\r\n" +
            "WHERE o.observation_date >= @bandFrom AND o.observation_date <= @bandTo\r\n" +
            "GROUP BY o.object_id;\r\n" +
            "\r\n" +
            "SELECT l.object_id AS ObjectId, o.user_id AS UserId, l.observation_date AS ObservationDate, o.observed_at AS ObservedAt,\r\n" +
            "       o.quality AS Quality, o.period_status AS PeriodStatus, o.source_month AS SourceMonth,\r\n" +
            "       ISNULL(sv.ServiceRows, 0) AS ServiceRows, ISNULL(sv.CreditRows, 0) AS CreditRows, sv.Credits AS Credits,\r\n" +
            "       ISNULL(sv.OtherUnitRows, 0) AS OtherUnitRows, ISNULL(sv.UnknownConsumptionRows, 0) AS UnknownConsumptionRows,\r\n" +
            "       ISNULL(sv.NotAllowedRows, 0) AS NotAllowedRows, ISNULL(sv.LimitExhaustedRows, 0) AS LimitExhaustedRows,\r\n" +
            "       ISNULL(sv.NotEntitledRows, 0) AS NotEntitledRows, ISNULL(sv.NearLimitRows, 0) AS NearLimitRows,\r\n" +
            "       sv.OldestFreshness AS OldestFreshness, sv.NewestFreshness AS NewestFreshness,\r\n" +
            "       ISNULL(ue.UnevaluatedRows, 0) AS UnevaluatedRows, ISNULL(ue.NoPolicyRows, 0) AS NoPolicyRows\r\n" +
            "FROM #latest_balance AS l\r\n" +
            "JOIN dbo.copilot_cost_observations AS o ON o.observation_date = l.observation_date AND o.object_id = l.object_id\r\n" +
            "LEFT JOIN (\r\n" +
            "    SELECT s.observation_date, s.object_id, COUNT(*) AS ServiceRows,\r\n" +
            "           SUM(CASE WHEN s.unit COLLATE Latin1_General_100_BIN2 = N'" + CreditUnit + "' AND s.consumed_quantity IS NOT NULL THEN 1 ELSE 0 END) AS CreditRows,\r\n" +
            "           SUM(CASE WHEN s.unit COLLATE Latin1_General_100_BIN2 = N'" + CreditUnit + "' THEN s.consumed_quantity END) AS Credits,\r\n" +
            "           SUM(CASE WHEN s.unit IS NULL OR s.unit COLLATE Latin1_General_100_BIN2 <> N'" + CreditUnit + "' THEN 1 ELSE 0 END) AS OtherUnitRows,\r\n" +
            "           SUM(CASE WHEN s.consumed_quantity IS NULL THEN 1 ELSE 0 END) AS UnknownConsumptionRows,\r\n" +
            "           SUM(CASE WHEN s.allowed = 0 THEN 1 ELSE 0 END) AS NotAllowedRows,\r\n" +
            "           SUM(CASE WHEN s.decision_reason COLLATE Latin1_General_100_BIN2 = N'limitExhausted' THEN 1 ELSE 0 END) AS LimitExhaustedRows,\r\n" +
            "           SUM(CASE WHEN s.decision_reason COLLATE Latin1_General_100_BIN2 = N'notEntitled' THEN 1 ELSE 0 END) AS NotEntitledRows,\r\n" +
            "           SUM(CASE WHEN JSON_VALUE(s.per_user_limit, '$.Type') = N'#microsoft.graph.limitedCostManagementQuantity'\r\n" +
            "                     AND TRY_CONVERT(float, JSON_VALUE(s.per_user_limit, '$.Value')) > 0\r\n" +
            "                     AND s.consumed_quantity >= 0.8 * TRY_CONVERT(float, JSON_VALUE(s.per_user_limit, '$.Value')) THEN 1 ELSE 0 END) AS NearLimitRows,\r\n" +
            "           MIN(s.source_modified) AS OldestFreshness, MAX(s.source_modified) AS NewestFreshness\r\n" +
            "    FROM dbo.copilot_cost_services AS s\r\n" +
            "    JOIN #latest_balance AS x ON x.observation_date = s.observation_date AND x.object_id = s.object_id\r\n" +
            "    GROUP BY s.observation_date, s.object_id) AS sv ON sv.observation_date = l.observation_date AND sv.object_id = l.object_id\r\n" +
            "LEFT JOIN (\r\n" +
            "    SELECT u.observation_date, u.object_id, COUNT(*) AS UnevaluatedRows,\r\n" +
            "           SUM(CASE WHEN u.reason COLLATE Latin1_General_100_BIN2 = N'noApplicablePolicy' THEN 1 ELSE 0 END) AS NoPolicyRows\r\n" +
            "    FROM dbo.copilot_cost_unevaluated AS u\r\n" +
            "    JOIN #latest_balance AS x ON x.observation_date = u.observation_date AND x.object_id = u.object_id\r\n" +
            "    GROUP BY u.observation_date, u.object_id) AS ue ON ue.observation_date = l.observation_date AND ue.object_id = l.object_id;\r\n" +
            "\r\n" +
            "SELECT s.object_id AS ObjectId, s.category AS Category, s.policy_id AS PolicyId, s.consumed_quantity AS Consumed, s.unit AS Unit,\r\n" +
            "       s.allowed AS Allowed, s.decision_reason AS DecisionReason, s.source_modified AS SourceModified,\r\n" +
            "       JSON_VALUE(s.per_user_limit, '$.Type') AS PerUserLimitType, TRY_CONVERT(float, JSON_VALUE(s.per_user_limit, '$.Value')) AS PerUserLimit,\r\n" +
            "       JSON_VALUE(s.remaining, '$.Type') AS RemainingType, TRY_CONVERT(float, JSON_VALUE(s.remaining, '$.Value')) AS Remaining\r\n" +
            "FROM dbo.copilot_cost_services AS s\r\n" +
            "JOIN #latest_balance AS x ON x.observation_date = s.observation_date AND x.object_id = s.object_id\r\n" +
            "WHERE s.consumed_quantity IS NULL OR s.consumed_quantity <> 0 OR s.allowed = 0\r\n" +
            "   OR s.unit IS NULL OR s.unit COLLATE Latin1_General_100_BIN2 <> N'" + CreditUnit + "';\r\n" +
            "\r\n" +
            "SELECT u.object_id AS ObjectId, u.category AS Category, u.reason AS Reason\r\n" +
            "FROM dbo.copilot_cost_unevaluated AS u\r\n" +
            "JOIN #latest_balance AS x ON x.observation_date = u.observation_date AND x.object_id = u.object_id;\r\n" +
            "DROP TABLE #latest_balance;";

        /// <summary>The collector sweeps that started inside the window. One small row per day.</summary>
        public const string SweepsSql =
            "SELECT r.inventory_date AS InventoryDate, r.inventory_complete AS InventoryComplete, r.context_phase AS ContextPhase,\r\n" +
            "       r.last_completed AS LastCompleted, r.outcome AS Outcome\r\n" +
            "FROM dbo.copilot_cost_runs AS r\r\n" +
            "WHERE r.inventory_date >= @from AND r.inventory_date <= @to\r\n" +
            "ORDER BY r.inventory_date;";

        /// <summary>The documents of the newest sweep whose policy and tenant context completed by the window end.</summary>
        public const string ContextSql =
            "SELECT c.kind AS Kind, c.source_url AS SourceUrl, c.observed_at AS ObservedAt, c.document AS Document\r\n" +
            "FROM dbo.copilot_cost_context AS c\r\n" +
            "WHERE c.run_id = (SELECT TOP (1) r.run_id FROM dbo.copilot_cost_runs AS r\r\n" +
            "                  WHERE r.context_phase = 2 AND r.inventory_date <= @to\r\n" +
            "                  ORDER BY r.inventory_date DESC, r.last_served DESC)\r\n" +
            "ORDER BY c.context_id;";

        /// <summary>
        /// Balances on a bounded set of sample dates. Each sample covers only the people observed ON that date and
        /// is never added to another: a balance curve, not a running total. Each date is read on its own, in the
        /// tables' key order (a merge join and two stream aggregates, a 1 MB memory grant), so a narrow scope reads
        /// only its own people. At 200,000 synthetic users on LocalDB, with identical results: 28 days 0.45 s and 42K
        /// logical reads against 14.1 s and 20.8M with a lookup per observation; 90 days 273K reads against 29.7M and
        /// 3-11 s against 17.5 s (it reads every service row of 13 days, which LocalDB's capped buffer pool cannot
        /// hold); scoped to 20,000 people 0.74 s against 1.15 s. The result is cached per date set and scope.
        /// </summary>
        public const string TrendSql =
            "SELECT t.observation_date AS Date, a.ObservedUsers, a.ConsumingUsers, a.Credits, a.LimitExhaustedUsers\r\n" +
            "FROM (SELECT DISTINCT CONVERT(date, d.[value]) AS observation_date FROM OPENJSON(@dates) AS d) AS t\r\n" +
            "CROSS APPLY (\r\n" +
            "    SELECT COUNT_BIG(*) AS ObservedUsers,\r\n" +
            "           SUM(CASE WHEN p.Credits > 0 THEN 1 ELSE 0 END) AS ConsumingUsers,\r\n" +
            "           SUM(p.Credits) AS Credits,\r\n" +
            "           SUM(CASE WHEN p.LimitRows > 0 THEN 1 ELSE 0 END) AS LimitExhaustedUsers\r\n" +
            "    FROM (\r\n" +
            "        SELECT o.object_id,\r\n" +
            "               SUM(CASE WHEN s.unit COLLATE Latin1_General_100_BIN2 = N'" + CreditUnit + "' THEN s.consumed_quantity END) AS Credits,\r\n" +
            "               SUM(CASE WHEN s.decision_reason COLLATE Latin1_General_100_BIN2 = N'limitExhausted' THEN 1 ELSE 0 END) AS LimitRows\r\n" +
            "        FROM dbo.copilot_cost_observations AS o\r\n" +
            "        LEFT JOIN dbo.copilot_cost_services AS s ON s.observation_date = o.observation_date AND s.object_id = o.object_id\r\n" +
            "        WHERE o.observation_date = t.observation_date\r\n" +
            "          /*scope: AND o.user_id IN {scopeUsers}*/\r\n" +
            "        GROUP BY o.object_id) AS p) AS a\r\n" +
            "WHERE a.ObservedUsers > 0\r\n" +
            "ORDER BY t.observation_date;";

        /// <summary>
        /// Billed Copilot Studio credits per person in the window and the equal-length period before it, with days counted
        /// from a per-day aggregate rather than with COUNT(DISTINCT CASE ...), which SQL Server answers with table spools.
        /// Measured at 200,000 synthetic users (600K ledger rows) while this statement also counted each person's agents:
        /// 52K logical reads and 0.94 s for 90 days, 50K and 0.26 s for 28. That count has since gone, because the per-user
        /// ledger never names an agent, so those figures are an upper bound. People the user import never matched share one
        /// NULL user id.
        /// </summary>
        public const string StudioUsersSql =
            "WITH per_day AS (\r\n" +
            "    SELECT r.user_id, r.usage_date, SUM(r.billed_credits) AS credits\r\n" +
            "    FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "    WHERE r.usage_date >= @priorFrom AND r.usage_date < @toExclusive\r\n" +
            "    GROUP BY r.user_id, r.usage_date)\r\n" +
            "SELECT p.user_id AS UserId,\r\n" +
            "       CAST(SUM(CASE WHEN p.usage_date >= @from THEN p.credits ELSE 0 END) AS float) AS Credits,\r\n" +
            "       CAST(SUM(CASE WHEN p.usage_date < @from THEN p.credits ELSE 0 END) AS float) AS PriorCredits,\r\n" +
            "       SUM(CASE WHEN p.usage_date >= @from THEN 1 ELSE 0 END) AS ActiveDays\r\n" +
            "FROM per_day AS p\r\n" +
            "GROUP BY p.user_id;";

        public const string StudioImportSql =
            "SELECT TOP (1) l.imported_utc AS ImportedUtc, l.error AS Error\r\n" +
            "FROM dbo.agent_cost_import_log AS l\r\n" +
            "WHERE l.import_name = @name\r\n" +
            "ORDER BY l.imported_utc DESC;";

        /// <summary>
        /// The ledger's first and latest stored days: two ends of the (usage_date, dimension_hash) index, so free at any size.
        /// The latest stored day is how far the ledger reaches, not proof that every earlier day was imported.
        /// </summary>
        public const string StudioEarliestSql =
            "SELECT MIN(r.usage_date), MAX(r.usage_date) FROM dbo.copilot_studio_credit_user_daily AS r;";

        /// <summary>One person's Copilot turns per day, host and agent, through their own audit rows (an index seek on user id).</summary>
        public static readonly string PersonActivitySql =
            "SELECT CAST(c.time_stamp AS date) AS ActivityDate,\r\n" +
            "       CAST(ISNULL(c.app_host, N'') AS nvarchar(100)) AS AppHost,\r\n" +
            "       c.agent_id AS AgentId,\r\n" +
            "       COUNT_BIG(*) AS Turns\r\n" +
            "FROM dbo.audit_events AS a\r\n" +
            "JOIN dbo.copilot_chats AS c ON c.event_id = a.id\r\n" +
            "WHERE a.user_id = @userId AND a.time_stamp >= @from AND a.time_stamp < @toExclusive\r\n" +
            "  AND c.time_stamp >= @from AND c.time_stamp < @toExclusive\r\n" +
            "  AND " + Copilot.CopilotTurnSql.CountedTurn("c") + "\r\n" +
            "GROUP BY CAST(c.time_stamp AS date), CAST(ISNULL(c.app_host, N'') AS nvarchar(100)), c.agent_id;";

        public const string CoworkAgentIdsSql = CopilotAdoption.CopilotAdoptionSql.CoworkAgentIdsSql;

        /// <summary>One person's prompts and responses from the interaction-history import, by app.</summary>
        public const string PersonPromptsSql =
            "SELECT ISNULL(ac.name, N'') AS AppClass, ISNULL(t.name, N'') AS InteractionType,\r\n" +
            "       COUNT_BIG(*) AS Interactions, COUNT(DISTINCT CAST(i.created_utc AS date)) AS Days\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "LEFT JOIN dbo.copilot_interaction_types AS t ON t.id = i.interaction_type_id\r\n" +
            "LEFT JOIN dbo.copilot_interaction_app_classes AS ac ON ac.id = i.app_class_id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "GROUP BY ac.name, t.name;";

        /// <summary>One person's classified prompt goals in the window. Counts only; no prompt text exists to read.</summary>
        public const string PersonCategoriesSql =
            "SELECT pc.category_id AS CategoryId, MAX(pc.human_mode) AS HumanMode, COUNT_BIG(*) AS Prompts\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "JOIN dbo.copilot_prompt_classifications AS pc ON pc.interaction_id = i.id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "GROUP BY pc.category_id;";

        /// <summary>Every category of one directory object's latest balance, including zero rows.</summary>
        public const string PersonCreditServicesSql =
            "SELECT s.object_id AS ObjectId, s.category AS Category, s.policy_id AS PolicyId, s.consumed_quantity AS Consumed, s.unit AS Unit,\r\n" +
            "       s.allowed AS Allowed, s.decision_reason AS DecisionReason, s.source_modified AS SourceModified,\r\n" +
            "       JSON_VALUE(s.per_user_limit, '$.Type') AS PerUserLimitType, TRY_CONVERT(float, JSON_VALUE(s.per_user_limit, '$.Value')) AS PerUserLimit,\r\n" +
            "       JSON_VALUE(s.remaining, '$.Type') AS RemainingType, TRY_CONVERT(float, JSON_VALUE(s.remaining, '$.Value')) AS Remaining\r\n" +
            "FROM dbo.copilot_cost_services AS s\r\n" +
            "WHERE s.object_id = @objectId AND s.observation_date = @date;\r\n" +
            "SELECT u.object_id AS ObjectId, u.category AS Category, u.reason AS Reason\r\n" +
            "FROM dbo.copilot_cost_unevaluated AS u\r\n" +
            "WHERE u.object_id = @objectId AND u.observation_date = @date;";

        /// <summary>One directory object's balance on every day it was observed in the window (an index seek on object id).</summary>
        public const string PersonBalancesSql =
            "SELECT o.observation_date AS Date, o.quality AS Quality,\r\n" +
            "       SUM(CASE WHEN s.unit COLLATE Latin1_General_100_BIN2 = N'" + CreditUnit + "' THEN s.consumed_quantity END) AS Credits,\r\n" +
            "       CAST(MAX(CASE WHEN s.decision_reason COLLATE Latin1_General_100_BIN2 = N'limitExhausted' THEN 1 ELSE 0 END) AS bit) AS LimitExhausted\r\n" +
            "FROM dbo.copilot_cost_observations AS o\r\n" +
            "LEFT JOIN dbo.copilot_cost_services AS s ON s.observation_date = o.observation_date AND s.object_id = o.object_id\r\n" +
            "WHERE o.object_id = @objectId AND o.observation_date >= @from AND o.observation_date <= @to\r\n" +
            "GROUP BY o.observation_date, o.quality\r\n" +
            "ORDER BY o.observation_date;";

        /// <summary>
        /// One person's billed Copilot Studio credits by Power Platform environment in the window (an index seek on user id).
        /// The per-user ledger names no agent, so environment is the finest breakdown it can give. A renamed environment
        /// shows one of its names.
        /// </summary>
        public const string PersonStudioSql =
            "SELECT r.environment_id AS EnvironmentId, MAX(r.environment_name) AS EnvironmentName,\r\n" +
            "       CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.usage_date) AS ActiveDays\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.user_id = @userId AND r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "GROUP BY r.environment_id;";

        /// <summary>The sample dates for a balance trend: every k-th day back from the snapshot, at most <see cref="MaxTrendPoints"/>.</summary>
        public static List<DateTime> TrendDates(DateTime from, DateTime snapshot)
        {
            var dates = new List<DateTime>();
            if (snapshot < from) return dates;
            var span = (snapshot.Date - from.Date).Days + 1;
            var step = Math.Max(1, (int)Math.Ceiling(span / (double)MaxTrendPoints));
            for (var d = snapshot.Date; d >= from.Date && dates.Count < MaxTrendPoints; d = d.AddDays(-step)) dates.Add(d);
            dates.Reverse();
            return dates;
        }
    }

    /// <summary>Reads the report's own facts. Implemented over SQL in production and replaced in tests.</summary>
    public interface ICopilotInvestmentFactsSource
    {
        Task<CreditFacts> GetCreditFactsAsync(DateTime from, DateTime to, CancellationToken cancellationToken);
        Task<StudioFacts> GetStudioFactsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken);
        Task<List<CreditTrendPoint>> GetCreditTrendAsync(IReadOnlyList<DateTime> dates, ReportUserScope scope, CancellationToken cancellationToken);
        /// <summary>
        /// One person's evidence. Activity and prompts are timestamps read over [<paramref name="from"/>,
        /// <paramref name="toExclusive"/>); balances and the Studio ledger are kept by day and read through
        /// <paramref name="lastDay"/>, the window's last calendar day (<see cref="CopilotInvestmentWindow.LastDay"/>).
        /// </summary>
        Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from, DateTime toExclusive,
            DateTime lastDay, CancellationToken cancellationToken);
    }

    /// <summary>The SQL implementation. Each read is one bounded statement; nothing loops a query per person.</summary>
    public sealed class SqlCopilotInvestmentFactsSource : ICopilotInvestmentFactsSource
    {
        /// <summary>The same per-statement budget the adoption analysis uses.</summary>
        public const int CommandTimeoutSeconds = 90;

        private readonly Func<SqlConnection> _connection;

        public SqlCopilotInvestmentFactsSource(Func<SqlConnection> connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public async Task<CreditFacts> GetCreditFactsAsync(DateTime from, DateTime to, CancellationToken cancellationToken)
        {
            var facts = new CreditFacts();
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                facts.TablesPresent = Convert.ToInt32(await ScalarAsync(db, CopilotInvestmentSql.TablesPresentSql, cancellationToken)) == 1;
                if (!facts.TablesPresent) return facts;

                using (var cmd = Command(db, CopilotInvestmentSql.SnapshotDateSql, "@to", to.Date, "@lookbackFrom", to.Date.AddDays(-CopilotInvestmentSql.SnapshotLookbackDays)))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        facts.SnapshotDate = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                        facts.FirstObservationDate = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                    }
                }

                if (facts.SnapshotDate.HasValue)
                {
                    facts.SnapshotBandFrom = facts.SnapshotDate.Value.AddDays(-(CopilotInvestmentSql.SnapshotBandDays - 1));
                    using (var cmd = Command(db, CopilotInvestmentSql.LatestBalancesSql, "@bandFrom", facts.SnapshotBandFrom.Value, "@bandTo", facts.SnapshotDate.Value))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            facts.Users.Add(new CreditUserFact
                            {
                                ObjectId = reader.GetGuid(0),
                                UserId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1),
                                ObservationDate = reader.GetDateTime(2),
                                ObservedAt = reader.GetDateTimeOffset(3),
                                Quality = reader.GetByte(4),
                                PeriodStatus = reader.IsDBNull(5) ? null : reader.GetString(5),
                                SourceMonth = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6),
                                ServiceRows = reader.GetInt32(7),
                                CreditRows = reader.GetInt32(8),
                                Credits = reader.IsDBNull(9) ? (double?)null : reader.GetDouble(9),
                                OtherUnitRows = reader.GetInt32(10),
                                UnknownConsumptionRows = reader.GetInt32(11),
                                NotAllowedRows = reader.GetInt32(12),
                                LimitExhaustedRows = reader.GetInt32(13),
                                NotEntitledRows = reader.GetInt32(14),
                                NearLimitRows = reader.GetInt32(15),
                                OldestFreshness = reader.IsDBNull(16) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(16),
                                NewestFreshness = reader.IsDBNull(17) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(17),
                                UnevaluatedRows = reader.GetInt32(18),
                                NoPolicyRows = reader.GetInt32(19),
                            });
                        }

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) facts.Services.Add(ReadService(reader));

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            facts.Unevaluated.Add(new CreditUnevaluatedFact { ObjectId = reader.GetGuid(0), Category = reader.GetString(1), Reason = reader.GetString(2) });
                    }
                }

                using (var cmd = Command(db, CopilotInvestmentSql.SweepsSql, "@from", from.Date, "@to", to.Date))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        facts.Sweeps.Add(new CreditSweepFact
                        {
                            InventoryDate = reader.GetDateTime(0),
                            InventoryComplete = reader.GetBoolean(1),
                            ContextPhase = reader.GetInt32(2),
                            LastCompleted = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(3),
                            Outcome = reader.IsDBNull(4) ? null : reader.GetString(4),
                        });
                    }
                }

                facts.Collector = await ReadCollectorAsync(db, cancellationToken).ConfigureAwait(false);
                facts.Context = await ReadContextAsync(db, to.Date, cancellationToken).ConfigureAwait(false);
            }
            return facts;
        }

        private static async Task<CostCollectorStatus> ReadCollectorAsync(SqlConnection db, CancellationToken cancellationToken)
        {
            using (var cmd = Command(db, SqlCostCollectorStore.StatusSql))
            using (var r = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
                var result = new CostCollectorStatus
                {
                    InventoryComplete = r.GetBoolean(0), LastAttempt = r.IsDBNull(2) ? (DateTimeOffset?)null : r.GetDateTimeOffset(2),
                    LastCompleted = r.IsDBNull(3) ? (DateTimeOffset?)null : r.GetDateTimeOffset(3), LastOutcome = r.GetString(4),
                    DirectoryUsers = r.GetInt64(5), Pending = r.GetInt64(6), Retrying = r.GetInt64(7),
                    Observed = r.GetInt64(8), Failed = r.GetInt64(9), Denied = r.GetInt64(10),
                    Measured = r.GetInt64(11), Empty = r.GetInt64(12), Partial = r.GetInt64(13), Unevaluated = r.GetInt64(14),
                };
                result.Phase = !result.InventoryComplete ? "inventory" : r.GetInt32(1) < 2 ? "context"
                    : result.Pending + result.Retrying > 0 ? "users" : "complete";
                return result;
            }
        }

        private static async Task<CreditContextFact> ReadContextAsync(SqlConnection db, DateTime to, CancellationToken cancellationToken)
        {
            var context = new CreditContextFact();
            var any = false;
            using (var cmd = Command(db, CopilotInvestmentSql.ContextSql, "@to", to))
            using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    any = true;
                    var kind = reader.GetString(0);
                    var url = reader.GetString(1);
                    var observedAt = reader.GetDateTimeOffset(2);
                    var document = reader.GetString(3);
                    if (!context.ObservedAt.HasValue || observedAt > context.ObservedAt) context.ObservedAt = observedAt;
                    try
                    {
                        switch (kind)
                        {
                            case "policies":
                                context.Policies.AddRange(JsonConvert.DeserializeObject<List<CostPolicyContext>>(document) ?? new List<CostPolicyContext>());
                                break;
                            case "services":
                                var policy = PolicyIdFromUrl(url);
                                if (policy != null)
                                {
                                    if (!context.PolicyServices.TryGetValue(policy, out var list))
                                        context.PolicyServices[policy] = list = new List<CostPolicyServiceContext>();
                                    list.AddRange(JsonConvert.DeserializeObject<List<CostPolicyServiceContext>>(document) ?? new List<CostPolicyServiceContext>());
                                }
                                break;
                            case "groups":
                                var owner = PolicyIdFromUrl(url);
                                if (owner != null)
                                {
                                    var groups = JsonConvert.DeserializeObject<List<Guid>>(document) ?? new List<Guid>();
                                    context.PolicyGroupCounts.TryGetValue(owner, out var count);
                                    context.PolicyGroupCounts[owner] = count + groups.Count;
                                }
                                break;
                            case "tenant":
                                context.TenantBalances.AddRange(JsonConvert.DeserializeObject<List<CostTenantBalance>>(document) ?? new List<CostTenantBalance>());
                                break;
                        }
                    }
                    catch (JsonException)
                    {
                        // A stored document this build cannot read is reported as missing context, never guessed at.
                    }
                }
            }
            return any ? context : null;
        }

        /// <summary>The policy id inside a policy's own child URL (<c>.../spendingPolicies/{id}/...</c>).</summary>
        internal static string PolicyIdFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            const string marker = "/spendingPolicies/";
            var start = url.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return null;
            start += marker.Length;
            var end = url.IndexOf('/', start);
            var id = end < 0 ? url.Substring(start) : url.Substring(start, end - start);
            var query = id.IndexOf('?');
            if (query >= 0) id = id.Substring(0, query);
            return Uri.UnescapeDataString(id);
        }

        public async Task<StudioFacts> GetStudioFactsAsync(DateTime from, DateTime toExclusive, CancellationToken cancellationToken)
        {
            var facts = new StudioFacts();
            var days = Math.Max(1, (toExclusive.Date - from.Date).Days);
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var cmd = Command(db, CopilotInvestmentSql.StudioUsersSql,
                    "@from", from.Date, "@toExclusive", toExclusive.Date, "@priorFrom", from.Date.AddDays(-days)))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        facts.Users.Add(new StudioUserFact
                        {
                            UserId = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0),
                            Credits = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                            PriorCredits = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                            ActiveDays = reader.GetInt32(3),
                        });
                    }
                }
                facts.HasData = facts.Users.Count > 0;
                using (var cmd = Command(db, CopilotInvestmentSql.StudioEarliestSql))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        facts.EarliestDate = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                        facts.LatestDate = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                    }
                }
                // Only a ledger that reaches back over the whole previous period can be compared with it: a ledger that
                // starts part-way through would read as growth that is only the import starting.
                facts.PriorComplete = facts.EarliestDate.HasValue && facts.EarliestDate.Value.Date <= from.Date.AddDays(-days);
                using (var cmd = Command(db, CopilotInvestmentSql.StudioImportSql, "@name", AgentCostImportNames.CopilotStudioUserCredits))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        facts.LastImportUtc = reader.GetDateTime(0);
                        facts.LastImportError = reader.IsDBNull(1) ? null : reader.GetString(1);
                    }
                }
            }
            return facts;
        }

        public async Task<List<CreditTrendPoint>> GetCreditTrendAsync(IReadOnlyList<DateTime> dates, ReportUserScope scope, CancellationToken cancellationToken)
        {
            var points = new List<CreditTrendPoint>();
            if (dates == null || dates.Count == 0) return points;
            var sql = ReportScopeSql.Apply(CopilotInvestmentSql.TrendSql, scope ?? ReportUserScope.Everyone);
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var cmd = Command(db, sql, "@dates", JsonConvert.SerializeObject(dates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))))
                {
                    var scopeParameter = ReportScopeSql.CreateParameter(scope);
                    if (scopeParameter != null) cmd.Parameters.Add(scopeParameter);
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            points.Add(new CreditTrendPoint
                            {
                                Date = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                                ObservedUsers = reader.GetInt64(1),
                                ConsumingUsers = reader.GetInt32(2),
                                Credits = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
                                LimitExhaustedUsers = reader.GetInt32(4),
                            });
                        }
                    }
                }
            }
            return points;
        }

        public async Task<PersonDetailFacts> GetPersonDetailAsync(int userId, Guid? objectId, DateTime? creditDate, DateTime from,
            DateTime toExclusive, DateTime lastDay, CancellationToken cancellationToken)
        {
            var detail = new PersonDetailFacts();
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (await TableExistsAsync(db, "copilot_chats", cancellationToken))
                {
                    using (var cmd = Command(db, CopilotInvestmentSql.CoworkAgentIdsSql))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) detail.CoworkAgentIds.Add(reader.GetInt32(0));

                    using (var cmd = Command(db, CopilotInvestmentSql.PersonActivitySql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            detail.Activity.Add(new PersonActivityRow
                            {
                                ActivityDate = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                                AppHost = reader.GetString(1),
                                AgentId = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2),
                                Turns = reader.GetInt64(3),
                            });
                        }
                    }
                    detail.ActivityAssessed = true;
                }

                if (await TableExistsAsync(db, "copilot_interactions", cancellationToken))
                {
                    using (var cmd = Command(db, CopilotInvestmentSql.PersonPromptsSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            detail.Prompts.Add(new PersonPromptRow
                            {
                                AppClass = reader.GetString(0), InteractionType = reader.GetString(1),
                                Interactions = reader.GetInt64(2), Days = reader.GetInt32(3),
                            });
                        }
                    }
                    detail.PromptsAssessed = true;

                    if (await TableExistsAsync(db, "copilot_prompt_classifications", cancellationToken))
                    {
                        using (var cmd = Command(db, CopilotInvestmentSql.PersonCategoriesSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                detail.PromptCategories.Add(new PersonCategoryRow
                                {
                                    CategoryId = reader.GetString(0), HumanMode = reader.IsDBNull(1) ? null : reader.GetString(1), Prompts = reader.GetInt64(2),
                                });
                            }
                        }
                    }
                }

                if (objectId.HasValue && await TableExistsAsync(db, "copilot_cost_observations", cancellationToken))
                {
                    if (creditDate.HasValue)
                    {
                        using (var cmd = Command(db, CopilotInvestmentSql.PersonCreditServicesSql, "@objectId", objectId.Value, "@date", creditDate.Value.Date))
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) detail.CreditServices.Add(ReadService(reader));
                            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                detail.CreditUnevaluated.Add(new CreditUnevaluatedFact { ObjectId = reader.GetGuid(0), Category = reader.GetString(1), Reason = reader.GetString(2) });
                        }
                    }

                    using (var cmd = Command(db, CopilotInvestmentSql.PersonBalancesSql, "@objectId", objectId.Value, "@from", from.Date, "@to", lastDay.Date))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            detail.Balances.Add(new PersonBalancePoint
                            {
                                Date = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                                Quality = reader.GetByte(1),
                                Credits = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2),
                                LimitExhausted = !reader.IsDBNull(3) && reader.GetBoolean(3),
                            });
                        }
                    }
                }

                if (await TableExistsAsync(db, "copilot_studio_credit_user_daily", cancellationToken))
                {
                    using (var cmd = Command(db, CopilotInvestmentSql.PersonStudioSql, "@userId", userId, "@from", from.Date, "@toExclusive", lastDay.Date.AddDays(1)))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            detail.StudioEnvironments.Add(new PersonStudioEnvironmentRow
                            {
                                EnvironmentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                                EnvironmentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Credits = reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                                ActiveDays = reader.GetInt32(3),
                            });
                        }
                    }
                }
            }
            return detail;
        }

        private static CreditServiceFact ReadService(SqlDataReader reader) => new CreditServiceFact
        {
            ObjectId = reader.GetGuid(0),
            Category = reader.GetString(1),
            PolicyId = reader.GetString(2),
            Consumed = reader.IsDBNull(3) ? (double?)null : reader.GetDouble(3),
            Unit = reader.IsDBNull(4) ? null : reader.GetString(4),
            Allowed = reader.IsDBNull(5) ? (bool?)null : reader.GetBoolean(5),
            DecisionReason = reader.IsDBNull(6) ? null : reader.GetString(6),
            SourceModified = reader.IsDBNull(7) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(7),
            PerUserLimitType = reader.IsDBNull(8) ? null : reader.GetString(8),
            PerUserLimit = reader.IsDBNull(9) ? (double?)null : reader.GetDouble(9),
            RemainingType = reader.IsDBNull(10) ? null : reader.GetString(10),
            Remaining = reader.IsDBNull(11) ? (double?)null : reader.GetDouble(11),
        };

        private static async Task<bool> TableExistsAsync(SqlConnection db, string table, CancellationToken cancellationToken)
        {
            using (var cmd = Command(db, "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", "@name", "dbo." + table))
                return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) == 1;
        }

        private static async Task<object> ScalarAsync(SqlConnection db, string sql, CancellationToken cancellationToken)
        {
            using (var cmd = Command(db, sql)) return await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        private static SqlCommand Command(SqlConnection db, string sql, params object[] args)
        {
            var cmd = new SqlCommand(sql, db) { CommandTimeout = CommandTimeoutSeconds };
            for (var i = 0; i < args.Length; i += 2)
            {
                var name = (string)args[i];
                var value = args[i + 1];
                SqlParameter parameter;
                switch (value)
                {
                    case DateTime date:
                        parameter = new SqlParameter(name, SqlDbType.DateTime2) { Value = date };
                        break;
                    case Guid guid:
                        parameter = new SqlParameter(name, SqlDbType.UniqueIdentifier) { Value = guid };
                        break;
                    case int number:
                        parameter = new SqlParameter(name, SqlDbType.Int) { Value = number };
                        break;
                    case string text:
                        parameter = new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = text };
                        break;
                    default:
                        parameter = new SqlParameter(name, value ?? DBNull.Value);
                        break;
                }
                cmd.Parameters.Add(parameter);
            }
            return cmd;
        }
    }
}
