using Common.Entities.Copilot;
using Common.Entities.CopilotAdoption;
using Common.Entities.PromptCategories;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotSpend
{
    /// <summary>
    /// The Copilot Spend report's own SQL: billed Copilot Studio usage by day and by environment, narrowed to the reader's
    /// scope in the statement itself, and, for an unfiltered view only, the tenant's per-agent ledger. Everything else the
    /// report shows comes from the facts the investment report already reads and caches tenant-wide (latest balances,
    /// per-person Studio totals), narrowed in memory.
    /// </summary>
    public static class CopilotSpendSql
    {
        /// <summary>
        /// Billed credits and people per day: a daily ledger, so these add up over the period. People the user import never
        /// matched share a NULL user id: an unfiltered view counts their credits (the tenant's whole bill) but not as people,
        /// and a filtered view, which cannot show they belong to it, leaves them out.
        /// <para>Measured on LocalDB over a synthetic 200,000-user ledger (1.2M rows, 180 days): a clustered index scan of
        /// 57.6K logical reads whatever the window, 0.16 s for 28 days and 0.59 s for 90; 0.10 s and 0.17 s narrowed to
        /// 20,000 people. The ledger has no index covering this query, so it reads the table; no index is added for it
        /// here, as none has been measured.</para>
        /// </summary>
        public const string StudioDaysSql =
            "SELECT r.usage_date AS UsageDate, CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.user_id) AS People\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "  /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "GROUP BY r.usage_date\r\n" +
            "ORDER BY r.usage_date;";

        /// <summary>
        /// Billed credits per Power Platform environment, from the same per-person ledger and scope as the days, so the two
        /// add up to the same total. The ledger is kept by day, person and environment; it has no agent.
        /// <para>Two distinct counts (people and days) make SQL Server spool the rows: on the same synthetic 200,000-user
        /// ledger 626K logical reads (mostly the worktable) and 0.58 s for 28 days, 1.94M and 1.96 s for 90; 97K / 0.14 s and
        /// 221K / 0.30 s narrowed to 20,000 people. GROUPING SETS, a per-person pre-aggregation and a single distinct count
        /// were measured too and none was faster (1.9M reads and 4.5 s, 115K reads and 6.2 s, 58K reads and 3.4 s for 90
        /// days), so this form stays. The result is cached per window and scope.</para>
        /// </summary>
        public const string StudioEnvironmentsSql =
            "SELECT r.environment_id AS EnvironmentId, MAX(r.environment_name) AS EnvironmentName,\r\n" +
            "       CAST(SUM(r.billed_credits) AS float) AS Credits, COUNT(DISTINCT r.user_id) AS People, COUNT(DISTINCT r.usage_date) AS Days\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "  /*scope: AND r.user_id IN {scopeUsers}*/\r\n" +
            "GROUP BY r.environment_id\r\n" +
            "ORDER BY Credits DESC, r.environment_id;";

        /// <summary>
        /// Billed credits per agent from the tenant's agent ledger, with each agent's newest name in the window. That ledger
        /// is recorded per agent and not per person, through a different route from the per-person one, so it is read only
        /// for an unfiltered view and its total is reported on its own, never added to or reconciled with the people's.
        /// Measured over a synthetic 86,400-row ledger (40 agents, 4 environments, 180 days): 9.6K logical reads, 0.02 s for
        /// 28 days and 0.35 s for 90.
        /// </summary>
        public const string LedgerAgentsSql =
            ReportScopeSql.TenantWideMarker + "WITH win AS (\r\n" +
            "    SELECT d.agent_id, d.agent_name, d.usage_date, d.billed_credits\r\n" +
            "    FROM dbo.copilot_studio_credit_daily AS d\r\n" +
            "    WHERE d.usage_date >= @from AND d.usage_date < @toExclusive),\r\n" +
            "totals AS (\r\n" +
            "    SELECT agent_id, SUM(billed_credits) AS credits, COUNT(DISTINCT usage_date) AS days FROM win GROUP BY agent_id),\r\n" +
            "names AS (\r\n" +
            "    SELECT agent_id, agent_name, ROW_NUMBER() OVER (PARTITION BY agent_id ORDER BY usage_date DESC, agent_name) AS newest\r\n" +
            "    FROM win WHERE agent_name IS NOT NULL)\r\n" +
            "SELECT t.agent_id AS AgentId, n.agent_name AS AgentName, CAST(t.credits AS float) AS Credits, t.days AS Days\r\n" +
            "FROM totals AS t\r\n" +
            "LEFT JOIN names AS n ON n.agent_id = t.agent_id AND n.newest = 1\r\n" +
            "ORDER BY t.credits DESC, t.agent_id;";

        public const string LedgerPresentSql =
            ReportScopeSql.TenantWideMarker + "SELECT CASE WHEN OBJECT_ID(N'dbo.copilot_studio_credit_user_daily') IS NOT NULL AND OBJECT_ID(N'dbo.copilot_studio_credit_daily') IS NOT NULL THEN 1 ELSE 0 END;";

        #region One person's drill-down

        // Every statement below reads one person's own rows, by user id, and is only ever run for a person the controller has
        // already found in the reader's view: the scope is enforced there, so these carry no scope marker and never run
        // tenant-wide. None reads prompt or response text, which the product does not store.
        //
        // Each statement that takes @userId carries OPTION (RECOMPILE), because one person's rows can outnumber another's ten
        // thousand times and the right plan differs between them. Measured on a synthetic database of 200,000 people (2M
        // interactions, 3M phrase links, 1.6M Copilot turns, 3.7M audit rows) plus one outlier with 10,000 prompts and 10,000
        // turns in 120 days; medians of 3 warm runs, plan cache cleared per case. Without it, the plan compiled for the
        // outlier was reused for the next typical person and scanned whole tables: their prompt totals went from 3 logical
        // reads (0.3 ms) to 29,688 (74 ms). With it, a typical person's statements each read under 160 pages in under 8 ms,
        // compile included (the compile is 1-7 ms of that), and the outlier's each stay within about 45K reads and 0.3 s over
        // 90 days.

        /// <summary>Which of the optional tables the drill-down reads exist: one round trip, so a database that never imported them reads "not assessed".</summary>
        public const string PersonTablesSql =
            "SELECT\r\n" +
            "  CASE WHEN OBJECT_ID(N'dbo.copilot_studio_credit_user_daily', N'U') IS NULL THEN 0 ELSE 1 END AS Studio,\r\n" +
            "  CASE WHEN OBJECT_ID(N'dbo.copilot_interactions', N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_interaction_types', N'U') IS NULL\r\n" +
            "         OR OBJECT_ID(N'dbo.copilot_interaction_app_classes', N'U') IS NULL THEN 0 ELSE 1 END AS Interactions,\r\n" +
            "  CASE WHEN OBJECT_ID(N'dbo.copilot_interaction_keywords', N'U') IS NULL OR OBJECT_ID(N'dbo.keywords', N'U') IS NULL THEN 0 ELSE 1 END AS Keywords,\r\n" +
            "  CASE WHEN OBJECT_ID(N'dbo.copilot_prompt_classifications', N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_prompt_taxonomies', N'U') IS NULL THEN 0 ELSE 1 END AS Categories,\r\n" +
            "  CASE WHEN OBJECT_ID(N'dbo.audit_events', N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_chats', N'U') IS NULL\r\n" +
            "         OR OBJECT_ID(N'dbo.copilot_chat_duplicates', N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_event_accessed_resources', N'U') IS NULL\r\n" +
            "         OR OBJECT_ID(N'dbo.copilot_event_accessed_resource_types', N'U') IS NULL THEN 0 ELSE 1 END AS Resources;";

        /// <summary>
        /// One person's billed Studio credits per day: their own rows of the daily ledger, which sum over the period. A seek
        /// on user id: 1.4K reads for the outlier's 480 rows, 12 for a typical person.
        /// </summary>
        public const string PersonStudioDaysSql =
            "SELECT r.usage_date AS UsageDate, CAST(SUM(r.billed_credits) AS float) AS Credits\r\n" +
            "FROM dbo.copilot_studio_credit_user_daily AS r\r\n" +
            "WHERE r.user_id = @userId AND r.usage_date >= @from AND r.usage_date < @toExclusive\r\n" +
            "GROUP BY r.usage_date\r\n" +
            "ORDER BY r.usage_date\r\n" +
            "OPTION (RECOMPILE);";

        /// <summary>
        /// What one person's prompts were like in the window, from the interaction-history import: how many, in how many
        /// conversations and days, how long, and how many carried files, links or context. Counts only. A typical person is a
        /// seek on user id and time (70 reads over 90 days). For the outlier, whose 15,000 interactions in the window were 0.7%
        /// of the synthetic table, the optimiser scanned the table rather than look up each row: 45K reads, 91 ms. On a larger
        /// table the same person's share shrinks and the seek wins again.
        /// </summary>
        public const string PersonPromptTotalsSql =
            "SELECT COUNT_BIG(*) AS Prompts, COUNT(DISTINCT i.session_id) AS Conversations,\r\n" +
            "       COUNT(DISTINCT CAST(i.created_utc AS date)) AS Days, AVG(CAST(i.body_word_count AS float)) AS AverageWords,\r\n" +
            "       SUM(CASE WHEN i.attachment_count > 0 THEN 1 ELSE 0 END) AS WithAttachments,\r\n" +
            "       SUM(CASE WHEN i.link_count > 0 THEN 1 ELSE 0 END) AS WithLinks,\r\n" +
            "       SUM(CASE WHEN i.mention_count > 0 THEN 1 ELSE 0 END) AS WithMentions,\r\n" +
            "       SUM(CASE WHEN i.context_count > 0 THEN 1 ELSE 0 END) AS WithContext,\r\n" +
            "       MIN(i.created_utc) AS FirstUtc, MAX(i.created_utc) AS LastUtc\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "JOIN dbo.copilot_interaction_types AS t ON t.id = i.interaction_type_id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "  AND t.name = N'" + PromptInteractionType + "'\r\n" +
            "OPTION (RECOMPILE);";

        /// <summary>
        /// One person's prompts by classified goal and taxonomy version, with the latest of each. Only prompts are classified,
        /// so no interaction-type join is needed. Their interactions are a seek on user id and time; a typical person's goals
        /// are then seeks too (33 reads over 90 days), while the outlier's merge with a scan of the classifications (24K
        /// reads, 79 ms).
        /// </summary>
        public const string PersonCategoriesSql =
            "SELECT pc.category_id AS CategoryId, pc.taxonomy_version AS TaxonomyVersion, MAX(pc.human_mode) AS HumanMode,\r\n" +
            "       COUNT_BIG(*) AS Prompts, MAX(i.created_utc) AS LastUtc\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "JOIN dbo.copilot_prompt_classifications AS pc ON pc.interaction_id = i.id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "GROUP BY pc.category_id, pc.taxonomy_version\r\n" +
            "OPTION (RECOMPILE);";

        /// <summary>
        /// The key phrases Azure AI Language found most often in one person's prompts, with how many prompts carried each.
        /// Only prompts are analysed. Starts from the person's own interactions (a seek on user id and time); a typical
        /// person's phrases are then seeks too (157 reads over 90 days), while the outlier's 22,500 phrase links in 90 days merge
        /// with a scan of the phrase index instead (9.5K reads, 0.18 s).
        /// </summary>
        public const string PersonPhrasesSql =
            "SELECT TOP (@top) k.name AS Phrase, COUNT_BIG(DISTINCT ik.interaction_id) AS Prompts, MAX(i.created_utc) AS LastUtc\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "JOIN dbo.copilot_interaction_keywords AS ik ON ik.interaction_id = i.id\r\n" +
            "JOIN dbo.keywords AS k ON k.id = ik.keyword_id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "  AND k.name IS NOT NULL\r\n" +
            "GROUP BY k.name\r\n" +
            "ORDER BY Prompts DESC, k.name\r\n" +
            "OPTION (RECOMPILE);";

        /// <summary>
        /// One person's latest prompts - when, in which app, how long and with what attached - then each one's classified goal
        /// and its key phrases. Never the text: the product does not store it. The person's newest prompts are taken once into
        /// a keyed table variable so the goal and phrase reads touch only those rows: under 470 reads and 4 ms for the outlier.
        /// </summary>
        public static string PersonRecentPromptsSql(bool categories, bool phrases) =>
            "SET NOCOUNT ON;\r\n" +
            "DECLARE @recent TABLE (id int NOT NULL PRIMARY KEY, created_utc datetime NOT NULL, app_class nvarchar(200) NULL,\r\n" +
            "    words int NOT NULL, attachments int NOT NULL, links int NOT NULL, mentions int NOT NULL, context int NOT NULL);\r\n" +
            "INSERT INTO @recent (id, created_utc, app_class, words, attachments, links, mentions, context)\r\n" +
            "SELECT TOP (@take) i.id, i.created_utc, ac.name, ISNULL(i.body_word_count, 0), ISNULL(i.attachment_count, 0), ISNULL(i.link_count, 0),\r\n" +
            "       ISNULL(i.mention_count, 0), ISNULL(i.context_count, 0)\r\n" +
            "FROM dbo.copilot_interactions AS i\r\n" +
            "JOIN dbo.copilot_interaction_types AS t ON t.id = i.interaction_type_id\r\n" +
            "LEFT JOIN dbo.copilot_interaction_app_classes AS ac ON ac.id = i.app_class_id\r\n" +
            "WHERE i.user_id = @userId AND i.created_utc >= @from AND i.created_utc < @toExclusive\r\n" +
            "  AND t.name = N'" + PromptInteractionType + "'\r\n" +
            "ORDER BY i.created_utc DESC, i.id DESC\r\n" +
            "OPTION (RECOMPILE);\r\n" +
            (categories
                ? "SELECT r.id, r.created_utc, r.app_class, r.words, r.attachments, r.links, r.mentions, r.context,\r\n" +
                  "       pc.category_id AS CategoryId, pc.taxonomy_version AS TaxonomyVersion\r\n" +
                  "FROM @recent AS r\r\n" +
                  "LEFT JOIN dbo.copilot_prompt_classifications AS pc ON pc.interaction_id = r.id\r\n"
                : "SELECT r.id, r.created_utc, r.app_class, r.words, r.attachments, r.links, r.mentions, r.context,\r\n" +
                  "       CAST(NULL AS nvarchar(80)) AS CategoryId, CAST(NULL AS nvarchar(64)) AS TaxonomyVersion\r\n" +
                  "FROM @recent AS r\r\n") +
            "ORDER BY r.created_utc DESC, r.id DESC;\r\n" +
            (phrases
                ? "SELECT ik.interaction_id AS InteractionId, k.name AS Phrase\r\n" +
                  "FROM @recent AS r\r\n" +
                  "JOIN dbo.copilot_interaction_keywords AS ik ON ik.interaction_id = r.id\r\n" +
                  "JOIN dbo.keywords AS k ON k.id = ik.keyword_id\r\n" +
                  "WHERE k.name IS NOT NULL\r\n" +
                  "ORDER BY ik.interaction_id, ik.id;"
                : "SELECT CAST(NULL AS int) AS InteractionId, CAST(NULL AS nvarchar(200)) AS Phrase WHERE 1 = 0;");

        /// <summary>
        /// How the audit log typed the resources Copilot referenced for one person, through their own audit rows, each
        /// resource counted once per turn as the Copilot Adoption breakdown counts it. A typical person is 115 reads; the
        /// outlier's 15,000 resources over 90 days were 35K reads and 0.29 s, the slowest statement in the drill-down.
        /// </summary>
        public static readonly string PersonResourceTypesSql =
            "SELECT ISNULL(rt.name, N'" + Copilot.CopilotAccessedResourceTaxonomy.UnknownTypeLabel + "') AS Label, CAST(COUNT_BIG(*) AS float) AS Value\r\n" +
            "FROM dbo.audit_events AS a\r\n" +
            "JOIN dbo.copilot_chats AS c ON c.event_id = a.id\r\n" +
            "JOIN dbo.copilot_event_accessed_resources AS ar ON ar.copilot_chat_id = c.event_id\r\n" +
            "LEFT JOIN dbo.copilot_event_accessed_resource_types AS rt ON rt.id = ar.resource_type_id\r\n" +
            Copilot.CopilotTurnSql.ResourceTurnJoin("c", "ar_dup") + "\r\n" +
            "WHERE a.user_id = @userId AND a.time_stamp >= @from AND a.time_stamp < @toExclusive\r\n" +
            "  AND c.time_stamp >= @from AND c.time_stamp < @toExclusive\r\n" +
            "  AND " + Copilot.CopilotTurnSql.ResourceCountedOnce("ar", "ar_dup") + "\r\n" +
            "GROUP BY ISNULL(rt.name, N'" + Copilot.CopilotAccessedResourceTaxonomy.UnknownTypeLabel + "')\r\n" +
            "ORDER BY Value DESC\r\n" +
            "OPTION (RECOMPILE);";

        /// <summary>The agents a drill-down names, by their stored ids (a JSON array): a clustered-key seek each.</summary>
        public const string AgentsByIdSql =
            "IF OBJECT_ID(N'dbo.copilot_agents', N'U') IS NOT NULL\r\n" +
            "    SELECT a.id AS Id, a.agent_id AS AgentId, a.name AS Name, a.is_custom_agent AS IsCustomAgent\r\n" +
            "    FROM dbo.copilot_agents AS a\r\n" +
            "    WHERE a.id IN (SELECT CAST(j.[value] AS int) FROM OPENJSON(@ids) AS j);";

        /// <summary>The prompt-category taxonomies a drill-down names, by version (a JSON array).</summary>
        public const string TaxonomiesSql =
            "SELECT t.version AS Version, t.categories_json AS CategoriesJson\r\n" +
            "FROM dbo.copilot_prompt_taxonomies AS t\r\n" +
            "WHERE t.version IN (SELECT CAST(j.[value] AS nvarchar(64)) FROM OPENJSON(@versions) AS j);";

        /// <summary>The stored name of a prompt in <c>copilot_interaction_types</c>; responses are <c>aiResponse</c>.</summary>
        public const string PromptInteractionType = "userPrompt";

        #endregion
    }

    public interface ICopilotSpendFactsSource
    {
        /// <summary>
        /// Billed Studio credits by day and by environment over [<paramref name="from"/>, <paramref name="toExclusive"/>), in
        /// scope; and by agent from the tenant agent ledger when <paramref name="scope"/> is not restricted.
        /// </summary>
        Task<CopilotSpendStudioBreakdown> GetStudioBreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope, CancellationToken cancellationToken);

        /// <summary>
        /// One person's own usage for the drill-down: their billed Studio credits per day from <paramref name="from"/> to
        /// <paramref name="lastDay"/>, and over [<paramref name="from"/>, <paramref name="toExclusive"/>) their prompt totals,
        /// classified goals, key phrases and latest prompts (never their text), and the resource types the audit log recorded.
        /// It reads by user id and applies no scope, so call it only for a person already found in the reader's view.
        /// </summary>
        Task<CopilotSpendPersonFacts> GetPersonUsageAsync(int userId, DateTime from, DateTime toExclusive, DateTime lastDay, CancellationToken cancellationToken);

        /// <summary>The stored Copilot agents with these ids; an id with no row, or a database without the table, returns nothing for it.</summary>
        Task<List<CopilotSpendAgentFact>> GetAgentsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken);
    }

    /// <summary>
    /// One person's own usage facts for the Copilot Spend drill-down. A part whose tables the database does not have is not
    /// assessed (its flag is false), which the page shows as not available, never as zero.
    /// </summary>
    public sealed class CopilotSpendPersonFacts
    {
        public bool StudioAssessed { get; set; }

        /// <summary>Billed Studio credits on each day the person has ledger rows; the days in between are filled in later.</summary>
        public List<CopilotSpendDay> StudioDays { get; set; } = new List<CopilotSpendDay>();

        public bool PromptsAssessed { get; set; }
        public CopilotSpendPromptTotals PromptTotals { get; set; } = new CopilotSpendPromptTotals();

        /// <summary>The person's latest prompts, newest first.</summary>
        public List<CopilotSpendRecentPrompt> Recent { get; set; } = new List<CopilotSpendRecentPrompt>();

        public bool CategoriesAssessed { get; set; }
        public List<CopilotSpendCategoryFact> Categories { get; set; } = new List<CopilotSpendCategoryFact>();

        /// <summary>The categories of every taxonomy version the person's prompts were classified with, by version.</summary>
        public Dictionary<string, List<PromptCategoryDefinition>> Taxonomies { get; set; } =
            new Dictionary<string, List<PromptCategoryDefinition>>(StringComparer.Ordinal);

        public bool PhrasesAssessed { get; set; }
        public List<CopilotSpendPhrase> Phrases { get; set; } = new List<CopilotSpendPhrase>();

        public bool ResourcesAssessed { get; set; }
        public List<AdoptionResourceTypeRow> ResourceTypes { get; set; } = new List<AdoptionResourceTypeRow>();
    }

    /// <summary>One person's prompts in one classified goal of one taxonomy version.</summary>
    public sealed class CopilotSpendCategoryFact
    {
        public string CategoryId { get; set; }
        public string TaxonomyVersion { get; set; }
        public string HumanMode { get; set; }
        public long Prompts { get; set; }
        public DateTime? LastUtc { get; set; }
    }

    /// <summary>One stored Copilot agent, as the drill-down names it.</summary>
    public sealed class CopilotSpendAgentFact
    {
        public int Id { get; set; }
        public string AgentId { get; set; }
        public string Name { get; set; }

        /// <summary>As stored: true, false or null. Read through <see cref="CopilotAgentClassifier.ResolveStoredOrigin"/>, never on its own.</summary>
        public bool? IsCustomAgent { get; set; }
    }

    public sealed class SqlCopilotSpendFactsSource : ICopilotSpendFactsSource
    {
        /// <summary>How many of a person's latest prompts the drill-down lists.</summary>
        public const int RecentPromptCount = 20;

        /// <summary>How many of a person's most frequent key phrases the drill-down lists.</summary>
        public const int TopPhraseCount = 30;

        /// <summary>How many key phrases one listed prompt shows.</summary>
        public const int PhrasesPerPrompt = 3;

        private const int CommandTimeoutSeconds = 120;

        private readonly Func<SqlConnection> _connection;

        public SqlCopilotSpendFactsSource(Func<SqlConnection> connection)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        }

        public async Task<CopilotSpendStudioBreakdown> GetStudioBreakdownAsync(DateTime from, DateTime toExclusive, ReportUserScope scope,
            CancellationToken cancellationToken)
        {
            var breakdown = new CopilotSpendStudioBreakdown();
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var probe = new SqlCommand(CopilotSpendSql.LedgerPresentSql, db))
                {
                    if (Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 1) return breakdown;
                }

                using (var cmd = Command(db, CopilotSpendSql.StudioDaysSql, from, toExclusive, scope))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        breakdown.Days.Add(new CopilotSpendDay
                        {
                            Date = reader.GetDateTime(0),
                            Credits = Math.Round(reader.IsDBNull(1) ? 0 : reader.GetDouble(1), 6),
                            People = reader.GetInt32(2),
                        });
                }

                using (var cmd = Command(db, CopilotSpendSql.StudioEnvironmentsSql, from, toExclusive, scope))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        breakdown.Environments.Add(new CopilotSpendEnvironment
                        {
                            EnvironmentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                            EnvironmentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Credits = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 6),
                            People = reader.GetInt32(3),
                            Days = reader.GetInt32(4),
                        });
                }

                // The agent ledger is not kept per person, so it cannot be narrowed to a filtered view: it is not read for one.
                if (scope == null || !scope.IsRestricted)
                {
                    breakdown.Agents = new List<CopilotSpendAgent>();
                    using (var cmd = Command(db, CopilotSpendSql.LedgerAgentsSql, from, toExclusive, ReportUserScope.Everyone))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            breakdown.Agents.Add(new CopilotSpendAgent
                            {
                                AgentId = reader.IsDBNull(0) ? null : reader.GetString(0),
                                AgentName = reader.IsDBNull(1) ? null : reader.GetString(1),
                                Credits = Math.Round(reader.IsDBNull(2) ? 0 : reader.GetDouble(2), 6),
                                Days = reader.GetInt32(3),
                            });
                    }
                }
            }
            return breakdown;
        }

        public async Task<CopilotSpendPersonFacts> GetPersonUsageAsync(int userId, DateTime from, DateTime toExclusive, DateTime lastDay,
            CancellationToken cancellationToken)
        {
            var facts = new CopilotSpendPersonFacts();
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                bool studio, interactions, keywords, categories, resources;
                using (var probe = new SqlCommand(CopilotSpendSql.PersonTablesSql, db) { CommandTimeout = CommandTimeoutSeconds })
                using (var reader = await probe.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    studio = reader.GetInt32(0) == 1;
                    interactions = reader.GetInt32(1) == 1;
                    keywords = interactions && reader.GetInt32(2) == 1;
                    categories = interactions && reader.GetInt32(3) == 1;
                    resources = reader.GetInt32(4) == 1;
                }

                if (studio)
                {
                    using (var cmd = Command(db, CopilotSpendSql.PersonStudioDaysSql, "@userId", userId, "@from", from.Date, "@toExclusive", lastDay.Date.AddDays(1)))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            facts.StudioDays.Add(new CopilotSpendDay
                            {
                                Date = DateTime.SpecifyKind(reader.GetDateTime(0).Date, DateTimeKind.Utc),
                                Credits = Math.Round(reader.IsDBNull(1) ? 0 : reader.GetDouble(1), 6),
                                People = null,
                            });
                    }
                    facts.StudioAssessed = true;
                }

                if (interactions)
                {
                    using (var cmd = Command(db, CopilotSpendSql.PersonPromptTotalsSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            facts.PromptTotals = new CopilotSpendPromptTotals
                            {
                                Prompts = reader.GetInt64(0),
                                Conversations = reader.GetInt32(1),
                                Days = reader.GetInt32(2),
                                AverageWords = reader.IsDBNull(3) ? (double?)null : Math.Round(reader.GetDouble(3), 1),
                                WithAttachments = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                                WithLinks = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                                WithMentions = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                                WithContext = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                                FirstUtc = reader.IsDBNull(8) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc),
                                LastUtc = reader.IsDBNull(9) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(9), DateTimeKind.Utc),
                            };
                    }
                    facts.PromptsAssessed = true;

                    if (categories)
                    {
                        using (var cmd = Command(db, CopilotSpendSql.PersonCategoriesSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                facts.Categories.Add(new CopilotSpendCategoryFact
                                {
                                    CategoryId = reader.GetString(0),
                                    TaxonomyVersion = reader.GetString(1),
                                    HumanMode = reader.IsDBNull(2) ? null : reader.GetString(2),
                                    Prompts = reader.GetInt64(3),
                                    LastUtc = reader.IsDBNull(4) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                                });
                        }
                        facts.CategoriesAssessed = true;
                    }

                    if (keywords)
                    {
                        using (var cmd = Command(db, CopilotSpendSql.PersonPhrasesSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive, "@top", TopPhraseCount))
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                facts.Phrases.Add(new CopilotSpendPhrase
                                {
                                    Phrase = reader.GetString(0),
                                    Prompts = reader.GetInt64(1),
                                    LastUtc = reader.IsDBNull(2) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                                });
                        }
                        facts.PhrasesAssessed = true;
                    }

                    var byId = new Dictionary<int, CopilotSpendRecentPrompt>();
                    using (var cmd = Command(db, CopilotSpendSql.PersonRecentPromptsSql(categories, keywords),
                        "@userId", userId, "@from", from, "@toExclusive", toExclusive, "@take", RecentPromptCount))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var prompt = new CopilotSpendRecentPrompt
                            {
                                InteractionId = reader.GetInt32(0),
                                CreatedUtc = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc),
                                AppClass = reader.IsDBNull(2) ? null : reader.GetString(2),
                                Words = reader.GetInt32(3),
                                Attachments = reader.GetInt32(4),
                                Links = reader.GetInt32(5),
                                Mentions = reader.GetInt32(6),
                                Context = reader.GetInt32(7),
                                CategoryId = reader.IsDBNull(8) ? null : reader.GetString(8),
                                TaxonomyVersion = reader.IsDBNull(9) ? null : reader.GetString(9),
                            };
                            facts.Recent.Add(prompt);
                            byId[prompt.InteractionId] = prompt;
                        }
                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (reader.IsDBNull(0) || reader.IsDBNull(1) || !byId.TryGetValue(reader.GetInt32(0), out var prompt)) continue;
                            var phrase = reader.GetString(1);
                            if (prompt.Phrases.Count < PhrasesPerPrompt && !prompt.Phrases.Contains(phrase, StringComparer.OrdinalIgnoreCase))
                                prompt.Phrases.Add(phrase);
                        }
                    }

                    var versions = facts.Categories.Select(c => c.TaxonomyVersion).Concat(facts.Recent.Select(r => r.TaxonomyVersion))
                        .Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList();
                    if (categories && versions.Count > 0)
                    {
                        using (var cmd = Command(db, CopilotSpendSql.TaxonomiesSql, "@versions", JsonConvert.SerializeObject(versions)))
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                var definitions = ParseTaxonomy(reader.IsDBNull(1) ? null : reader.GetString(1));
                                if (definitions != null) facts.Taxonomies[reader.GetString(0)] = definitions;
                            }
                        }
                    }
                }

                if (resources)
                {
                    using (var cmd = Command(db, CopilotSpendSql.PersonResourceTypesSql, "@userId", userId, "@from", from, "@toExclusive", toExclusive))
                    using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var label = reader.IsDBNull(0) ? CopilotAccessedResourceTaxonomy.UnknownTypeLabel : reader.GetString(0);
                            facts.ResourceTypes.Add(new AdoptionResourceTypeRow
                            {
                                Label = label,
                                Value = reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                                Kind = CopilotAccessedResourceTaxonomy.Classify(label),
                            });
                        }
                    }
                    facts.ResourcesAssessed = true;
                }
            }
            return facts;
        }

        public async Task<List<CopilotSpendAgentFact>> GetAgentsAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        {
            var agents = new List<CopilotSpendAgentFact>();
            if (ids == null || ids.Count == 0) return agents;
            using (var db = _connection())
            {
                await db.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var cmd = Command(db, CopilotSpendSql.AgentsByIdSql, "@ids", JsonConvert.SerializeObject(ids.Distinct().OrderBy(i => i))))
                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        agents.Add(new CopilotSpendAgentFact
                        {
                            Id = reader.GetInt32(0),
                            AgentId = reader.IsDBNull(1) ? null : reader.GetString(1),
                            Name = reader.IsDBNull(2) ? null : reader.GetString(2),
                            IsCustomAgent = reader.IsDBNull(3) ? (bool?)null : reader.GetBoolean(3),
                        });
                }
            }
            return agents;
        }

        /// <summary>A stored taxonomy's categories, or null when its JSON cannot be read: the prompts then show their category ids.</summary>
        internal static List<PromptCategoryDefinition> ParseTaxonomy(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                return JsonConvert.DeserializeObject<List<PromptCategoryDefinition>>(json)?.Where(c => c != null && !string.IsNullOrEmpty(c.Id)).ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// A command for the drill-down's statements. Every date column they compare is <c>datetime</c>, so a parameter of the
        /// same type keeps each predicate a plain range seek.
        /// </summary>
        private static SqlCommand Command(SqlConnection db, string sql, params object[] args)
        {
            var cmd = new SqlCommand(sql, db) { CommandTimeout = CommandTimeoutSeconds };
            for (var i = 0; i < args.Length; i += 2)
            {
                var name = (string)args[i];
                switch (args[i + 1])
                {
                    case DateTime date:
                        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.DateTime) { Value = date });
                        break;
                    case int number:
                        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.Int) { Value = number });
                        break;
                    case string text:
                        cmd.Parameters.Add(new SqlParameter(name, SqlDbType.NVarChar, -1) { Value = text });
                        break;
                    default:
                        throw new ArgumentException("Unsupported parameter type for " + name, nameof(args));
                }
            }
            return cmd;
        }

        private static SqlCommand Command(SqlConnection db, string sql, DateTime from, DateTime toExclusive, ReportUserScope scope)
        {
            var cmd = new SqlCommand(ReportScopeSql.Apply(sql, scope ?? ReportUserScope.Everyone), db) { CommandTimeout = 120 };
            // The ledger's usage_date is datetime: a parameter of the same type keeps the date predicate a plain range seek.
            cmd.Parameters.Add(new SqlParameter("@from", SqlDbType.DateTime) { Value = from.Date });
            cmd.Parameters.Add(new SqlParameter("@toExclusive", SqlDbType.DateTime) { Value = toExclusive.Date });
            var scopeParameter = ReportScopeSql.CreateParameter(scope);
            if (scopeParameter != null) cmd.Parameters.Add(scopeParameter);
            return cmd;
        }
    }
}
