using Common.Entities;
using Common.Entities.Config;
using Common.Entities.UserFilters;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.SqlServer;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Web.AnalyticsWeb.Models.Dlp;
using Web.AnalyticsWeb.Models.UserFilters;
using Web.AnalyticsWeb.Security;

namespace Web.AnalyticsWeb.Controllers
{
    /// <summary>
    /// Reporting API for "which Copilot actions and agents are being blocked by DLP policies, and how
    /// often".
    /// </summary>
    /// <remarks>
    /// <para>
    /// The agent-attributable figures all come from <c>copilot_dlp_events</c>, which is populated from
    /// the policy detail Microsoft embeds in each Copilot interaction record. That is the only source
    /// that names the agent: DLP policies scoped to the "Microsoft 365 Copilot and Copilot Chat"
    /// location do not emit records on the <c>DLP.All</c> feed, and the records that feed does emit
    /// carry the literal <c>UserKey = "DlpAgent"</c> with no agent identity.
    /// https://learn.microsoft.com/en-us/purview/audit-copilot
    /// </para>
    /// <para>
    /// The tenant-wide figures come from <c>dlp_rule_matches</c> and are returned in their own fields so
    /// the UI can keep them visually separate. They are deliberately never joined to Copilot
    /// interactions on user+time: that would invent agent attribution the audit data does not support.
    /// </para>
    /// <para>
    /// Every Copilot query filters on <c>copilot_chats.time_stamp</c> rather than joining back to
    /// <c>audit_events</c> - that is what the denormalised column added by
    /// <c>DenormaliseCopilotChatUserAndTime</c> exists for - and every aggregate is computed in SQL, so
    /// nothing here materialises a per-interaction row set into memory.
    /// </para>
    /// </remarks>
    [Authorize]
    [Route("api/Dlp")]
    [ApiReplyExceptionFilter]
    public class DlpAPIController  : ControllerBase
    {
        /// <summary>Windows the UI offers. Anything else snaps to the nearest, so a hand-edited URL cannot force a huge scan.</summary>
        private static readonly int[] AllowedWindowDays = { 7, 28, 90, 180 };

        /// <summary>Rows returned per ranked table. Small - these are "top offenders" lists, not exports.</summary>
        private const int TopN = 20;

        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly ReportScopeResolver _scopes;

        public DlpAPIController()
            : this(DefaultAnalyticsDbContextFactory.Instance, ReportScopeResolver.Default)
        {
        }

        /// <summary>
        /// Testable entry point. The queries below are EF LINQ, so a translation failure (an unsupported
        /// expression, a bad navigation) only appears when they actually run - which is why the
        /// integration test points this at a real, migrated database rather than trusting a compile.
        /// No global filter unless <paramref name="scopes"/> supplies one.
        /// </summary>
        internal DlpAPIController(IAnalyticsDbContextFactory contextFactory, ReportScopeResolver scopes = null)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            _scopes = scopes ?? new ReportScopeResolver(GlobalFilterProviders.None, CachedUserDirectorySource.Default);
        }

        // GET: api/Dlp/availability
        [HttpGet]
        [Route("availability")]
        public IActionResult Availability()
        {
            return Ok(BuildAvailability());
        }

        internal static DlpAvailability BuildAvailability()
        {
            var settings = new AppConfig().ImportJobSettings ?? new ImportTaskSettings();

            var model = new DlpAvailability
            {
                CopilotDlpAvailable = settings.Copilot,
                TenantDlpAvailable = settings.ImportDlp,
            };

            if (!model.CopilotDlpAvailable)
            {
                model.Reasons.Add(
                    "The Microsoft 365 Copilot audit import is switched off, so there is no per-agent DLP data. "
                    + "Enable 'Copilot interactions' in the installer. This does NOT need the DLP permission - "
                    + "Copilot DLP blocks are carried on the Copilot interaction records themselves.");
            }

            if (!model.TenantDlpAvailable)
            {
                model.Reasons.Add(
                    "The Data Loss Prevention import (DLP.All) is switched off, so tenant-wide policy activity "
                    + "for Exchange, SharePoint/OneDrive and Endpoint is not shown. Enable 'DLP policy events' in "
                    + "the installer and grant the runtime app the 'ActivityFeed.ReadDlp' application permission, "
                    + "which is separate from 'ActivityFeed.Read' and needs its own admin consent.");
            }

            return model;
        }

        /// <summary>
        /// The whole page in one call: KPIs, the ranked tables and the trend. The "top users" table names
        /// people, so a reader without the portal's See PII permission gets it empty (#661) - and the query
        /// behind it is not run for them at all.
        /// </summary>
        // GET: api/Dlp/summary?days=28
        [HttpGet]
        [Route("summary")]
        public async Task<IActionResult> Summary(int days = 28)
        {
            // The administrator's global filter, resolved for this reader and enforced here whatever the page
            // sends. Everything below describes only the people it leaves them seeing.
            var scope = await _scopes.ResolveAsync(Request, User, null, CancellationToken.None);
            return Ok(await BuildSummaryAsync(
                days,
                includeIndividuals: PortalAccess.Evaluate(Request, User).SeePii,
                scope: scope.IsRestricted ? scope.Sql : null));
        }

        /// <summary>
        /// The query work behind <see cref="Summary"/>, separated so it can be executed against a real
        /// database in tests without an ASP.NET request pipeline.
        /// </summary>
        /// <param name="scope">
        /// The people the summary may describe - the administrator's global filter - or <c>null</c> for
        /// everyone. Narrowed in SQL through <see cref="BuildScopedSummaryAsync"/>; with no filter the summary
        /// is built exactly as before.
        /// </param>
        internal async Task<DlpSummary> BuildSummaryAsync(int days, bool includeIndividuals = true, ReportUserScope scope = null)
        {
            if (scope != null && scope.IsRestricted)
            {
                return await BuildScopedSummaryAsync(days, includeIndividuals, scope);
            }

            var windowDays = SnapWindow(days);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.Date.AddDays(-windowDays);

            using (var db = _contextFactory.Create())
            {
                var summary = new DlpSummary { FromUtc = fromUtc, ToUtc = toUtc };

                var events = db.copilot_dlp_events
                    .Where(e => e.RelatedChat.TimeStampUtc >= fromUtc && e.RelatedChat.TimeStampUtc <= toUtc);

                summary.CopilotBlockedCount = await events.CountAsync(e => e.IsBlocked);
                summary.CopilotAuditedCount = await events.CountAsync(e => !e.IsBlocked);

                summary.UsersImpacted = await events
                    .Where(e => e.IsBlocked && e.RelatedChat.UserId != null)
                    .Select(e => e.RelatedChat.UserId).Distinct().CountAsync();

                summary.AgentsImpacted = await events
                    .Where(e => e.IsBlocked && e.RelatedChat.AgentId != null)
                    .Select(e => e.RelatedChat.AgentId).Distinct().CountAsync();

                summary.PoliciesInvolved = await events
                    .Where(e => e.DlpPolicyId != null)
                    .Select(e => e.DlpPolicyId).Distinct().CountAsync();

                summary.TopAgents = ToRows(await events
                    .Where(e => e.RelatedChat.AgentId != null)
                    .GroupBy(e => new { Id = e.RelatedChat.Agent.AgentID, e.RelatedChat.Agent.Name })
                    .Select(g => new RankProjection
                    {
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        Blocked = g.Count(e => e.IsBlocked),
                        Audited = g.Count(e => !e.IsBlocked),
                        Users = g.Where(e => e.RelatedChat.UserId != null).Select(e => e.RelatedChat.UserId).Distinct().Count(),
                    })
                    .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                    .Take(TopN).ToListAsync(), countUsers: true);

                // "Which policies blocked THIS agent" - the cross-tab the four independent tables
                // cannot answer on their own.
                await AttachAgentPolicyBreakdownAsync(events, summary.TopAgents);

                summary.TopPolicies = ToRows(await events
                    .Where(e => e.DlpPolicyId != null)
                    .GroupBy(e => new { Id = e.Policy.PolicyId, e.Policy.Name })
                    .Select(g => new RankProjection
                    {
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        Blocked = g.Count(e => e.IsBlocked),
                        Audited = g.Count(e => !e.IsBlocked),
                        Users = g.Where(e => e.RelatedChat.UserId != null).Select(e => e.RelatedChat.UserId).Distinct().Count(),
                    })
                    .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                    .Take(TopN).ToListAsync(), countUsers: true);

                summary.TopSensitivityLabels = ToRows(await events
                    .Where(e => e.SensitivityLabelId != null)
                    .GroupBy(e => e.SensitivityLabel.LabelId)
                    .Select(g => new RankProjection
                    {
                        Id = g.Key,
                        Name = g.Key,
                        Blocked = g.Count(e => e.IsBlocked),
                        Audited = g.Count(e => !e.IsBlocked),
                        Users = g.Where(e => e.RelatedChat.UserId != null).Select(e => e.RelatedChat.UserId).Distinct().Count(),
                    })
                    .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                    .Take(TopN).ToListAsync(), countUsers: true);

                // copilot_chats carries only the user's integer id (there is no User navigation on it),
                // so the UPN is joined in here. The grouping is still done in SQL.
                if (includeIndividuals)
                {
                    summary.TopUsers = ToRows(await (from e in events
                                                     where e.RelatedChat.UserId != null
                                                     join u in db.users on e.RelatedChat.UserId equals u.ID
                                                     group e by new { Id = u.ID, u.UserPrincipalName } into g
                                                     select new RankProjection
                                                     {
                                                         Id = SqlFunctions.StringConvert((double)g.Key.Id),
                                                         Name = g.Key.UserPrincipalName,
                                                         Blocked = g.Count(e => e.IsBlocked),
                                                         Audited = g.Count(e => !e.IsBlocked),
                                                     })
                                              .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                                              .Take(TopN).ToListAsync(), countUsers: false);

                    // "Which policies blocked THIS person", the same cross-tab as the agents table.
                    await AttachUserPolicyBreakdownAsync(events, summary.TopUsers);
                }

                // Grouped by date part in SQL so the trend is one aggregate query rather than pulling
                // every matching row back to group in memory.
                var trend = await events
                    .GroupBy(e => DbFunctions.TruncateTime(e.RelatedChat.TimeStampUtc))
                    .Select(g => new
                    {
                        Date = g.Key,
                        Blocked = g.Count(e => e.IsBlocked),
                        Audited = g.Count(e => !e.IsBlocked),
                    })
                    .ToListAsync();

                summary.Trend = trend
                    .Where(p => p.Date.HasValue)
                    .OrderBy(p => p.Date.Value)
                    .Select(p => new DlpTrendPoint
                    {
                        Date = p.Date.Value,
                        BlockedCount = p.Blocked,
                        AuditedCount = p.Audited,
                    })
                    .ToList();

                // Tenant-wide DLP.All activity. Separate query, separate fields, never joined to the
                // Copilot figures above.
                var tenantMatches = db.dlp_rule_matches
                    .Where(m => m.AuditEvent.TimeStamp >= fromUtc && m.AuditEvent.TimeStamp <= toUtc);

                summary.TenantBlockedCount = await tenantMatches.CountAsync(m => m.IsBlocked);
                summary.TenantAuditedCount = await tenantMatches.CountAsync(m => !m.IsBlocked);

                summary.TenantTopPolicies = ToRows(await tenantMatches
                    .Where(m => m.DlpPolicyId != null)
                    .GroupBy(m => new { Id = m.Policy.PolicyId, m.Policy.Name })
                    .Select(g => new RankProjection
                    {
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        Blocked = g.Count(m => m.IsBlocked),
                        Audited = g.Count(m => !m.IsBlocked),
                    })
                    .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                    .Take(TopN).ToListAsync(), countUsers: false);

                return summary;
            }
        }

        /// <summary>Flat shape every ranked query projects into, so one mapper serves them all.</summary>
        private class RankProjection
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public int Blocked { get; set; }
            public int Audited { get; set; }
            public int Users { get; set; }
        }

        /// <summary>The narrowed summary's whole-window Copilot figures.</summary>
        private sealed class ScopedTotalsRow
        {
            public int Blocked { get; set; }
            public int Audited { get; set; }
            public int UsersImpacted { get; set; }
            public int AgentsImpacted { get; set; }
            public int PoliciesInvolved { get; set; }
        }

        /// <summary>One person's matches, for the narrowed top-users table.</summary>
        private sealed class ScopedUserRow
        {
            public int UserId { get; set; }
            public int Blocked { get; set; }
            public int Audited { get; set; }
        }

        /// <summary>One day of the narrowed trend.</summary>
        private sealed class ScopedDayRow
        {
            public DateTime Day { get; set; }
            public int Blocked { get; set; }
            public int Audited { get; set; }
        }

        /// <summary>One (agent, policy) pair of the narrowed agent breakdown.</summary>
        private sealed class ScopedAgentPolicyRow
        {
            public string AgentId { get; set; }
            public string PolicyId { get; set; }
            public string Name { get; set; }
            public int Blocked { get; set; }
            public int Audited { get; set; }
        }

        /// <summary>The narrowed DLP.All totals.</summary>
        private sealed class ScopedTenantTotalsRow
        {
            public int Blocked { get; set; }
            public int Audited { get; set; }
        }

        // The narrowed summary's statements. Each is the unfiltered summary's aggregate with the scope marker
        // on the person the match belongs to - the Copilot interaction's user, the DLP.All audit event's user -
        // so the narrowing happens in SQL, before anything is grouped or returned. A match with no user cannot
        // be attributed to anyone, so it is left out, as under every other report's filter.

        internal const string ScopedTotalsSql = @"
SELECT ISNULL(SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END), 0) AS Blocked,
       ISNULL(SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END), 0) AS Audited,
       COUNT(DISTINCT CASE WHEN e.is_blocked = 1 THEN c.user_id END) AS UsersImpacted,
       COUNT(DISTINCT CASE WHEN e.is_blocked = 1 THEN c.agent_id END) AS AgentsImpacted,
       COUNT(DISTINCT e.dlp_policy_id) AS PoliciesInvolved
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/;";

        internal const string ScopedAgentsSql = @"
SELECT a.agent_id AS Id, a.name AS Name,
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited,
       COUNT(DISTINCT c.user_id) AS Users
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
INNER JOIN dbo.copilot_agents AS a ON a.id = c.agent_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
GROUP BY a.agent_id, a.name;";

        /// <summary>The policies that affected each of the ranked agents; <c>{agentIds}</c> becomes their parameters.</summary>
        internal const string ScopedAgentPoliciesSql = @"
SELECT a.agent_id AS AgentId, p.policy_id AS PolicyId, p.name AS Name,
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
INNER JOIN dbo.copilot_agents AS a ON a.id = c.agent_id
INNER JOIN dbo.dlp_policies AS p ON p.id = e.dlp_policy_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
  AND a.agent_id IN ({agentIds})
GROUP BY a.agent_id, p.policy_id, p.name;";

        internal const string ScopedPoliciesSql = @"
SELECT p.policy_id AS Id, p.name AS Name,
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited,
       COUNT(DISTINCT c.user_id) AS Users
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
INNER JOIN dbo.dlp_policies AS p ON p.id = e.dlp_policy_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
GROUP BY p.policy_id, p.name;";

        internal const string ScopedLabelsSql = @"
SELECT l.label_id AS Id, l.label_id AS Name,
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited,
       COUNT(DISTINCT c.user_id) AS Users
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
INNER JOIN dbo.sensitivity_labels AS l ON l.id = e.sensitivity_label_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
GROUP BY l.label_id;";

        internal const string ScopedTopUsersSql = @"
SELECT TOP (@top) c.user_id AS UserId,
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
GROUP BY c.user_id
ORDER BY Blocked DESC, Audited DESC, c.user_id;";

        internal const string ScopedTrendSql = @"
SELECT CAST(CAST(c.time_stamp AS date) AS datetime2) AS [Day],
       SUM(CASE WHEN e.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN e.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited
FROM dbo.copilot_dlp_events AS e
INNER JOIN dbo.copilot_chats AS c ON c.event_id = e.copilot_chat_id
WHERE c.time_stamp >= @from AND c.time_stamp <= @to AND c.user_id IS NOT NULL/*scope: AND c.user_id IN {scopeUsers}*/
GROUP BY CAST(c.time_stamp AS date)
ORDER BY [Day];";

        internal const string ScopedTenantTotalsSql = @"
SELECT ISNULL(SUM(CASE WHEN m.is_blocked = 1 THEN 1 ELSE 0 END), 0) AS Blocked,
       ISNULL(SUM(CASE WHEN m.is_blocked = 0 THEN 1 ELSE 0 END), 0) AS Audited
FROM dbo.dlp_rule_matches AS m
INNER JOIN dbo.audit_events AS ae ON ae.id = m.event_id
WHERE ae.time_stamp >= @from AND ae.time_stamp <= @to AND ae.user_id IS NOT NULL/*scope: AND ae.user_id IN {scopeUsers}*/;";

        internal const string ScopedTenantPoliciesSql = @"
SELECT p.policy_id AS Id, p.name AS Name,
       SUM(CASE WHEN m.is_blocked = 1 THEN 1 ELSE 0 END) AS Blocked,
       SUM(CASE WHEN m.is_blocked = 0 THEN 1 ELSE 0 END) AS Audited,
       0 AS Users
FROM dbo.dlp_rule_matches AS m
INNER JOIN dbo.audit_events AS ae ON ae.id = m.event_id
INNER JOIN dbo.dlp_policies AS p ON p.id = m.dlp_policy_id
WHERE ae.time_stamp >= @from AND ae.time_stamp <= @to AND ae.user_id IS NOT NULL/*scope: AND ae.user_id IN {scopeUsers}*/
GROUP BY p.policy_id, p.name;";

        /// <summary>
        /// The summary narrowed to the people the administrator's global filter leaves this reader seeing.
        /// </summary>
        /// <remarks>
        /// <para><b>Narrowed in SQL.</b> Every figure and ranking is the one <see cref="BuildSummaryAsync"/>
        /// computes, from the same tables, with the scope applied in each statement's WHERE clause - the same
        /// marker and temporary table every other SQL report uses (<see cref="ReportScopeSql"/>). Nothing per
        /// person or per interaction comes back: each statement returns one row, one per day, or one per agent,
        /// policy or label, so a filter covering most of a 200,000-person tenant costs what the unfiltered
        /// summary does, not a row per person and value.</para>
        /// <para><b>One session.</b> The scope's ids are sent and indexed once, on a connection held open for
        /// every statement, rather than once per statement.</para>
        /// <para><b>Fails closed.</b> A match with no user cannot be attributed to anyone, so a filtered summary
        /// leaves it out - the same rule as every other report under a filter.</para>
        /// </remarks>
        private async Task<DlpSummary> BuildScopedSummaryAsync(int days, bool includeIndividuals, ReportUserScope scope)
        {
            var windowDays = SnapWindow(days);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.Date.AddDays(-windowDays);

            using (var db = _contextFactory.Create())
            {
                // Opened here, not by EF, so the temporary table lives for every statement below. Created by a
                // command with no parameters, which runs as a plain batch: one with parameters runs inside
                // sp_executesql, and a temporary table created there is gone when it returns.
                await AzureSqlTokenAuth.OpenAsync(db.Database.Connection);
                await db.Database.ExecuteSqlCommandAsync(TransactionalBehavior.DoNotEnsureTransaction, ReportScopeSql.SessionCreateSql);
                await db.Database.ExecuteSqlCommandAsync(
                    TransactionalBehavior.DoNotEnsureTransaction, ReportScopeSql.SessionFillSql, ReportScopeSql.CreateParameter(scope));

                Task<List<T>> QueryAsync<T>(string sql, params SqlParameter[] extra)
                {
                    var parameters = new object[] { new SqlParameter("@from", fromUtc), new SqlParameter("@to", toUtc) }
                        .Concat(extra)
                        .ToArray();
                    return db.Database.SqlQuery<T>(ReportScopeSql.ApplyInSession(sql, scope), parameters).ToListAsync();
                }

                var summary = new DlpSummary { FromUtc = fromUtc, ToUtc = toUtc };

                var totals = (await QueryAsync<ScopedTotalsRow>(ScopedTotalsSql)).Single();
                summary.CopilotBlockedCount = totals.Blocked;
                summary.CopilotAuditedCount = totals.Audited;
                summary.UsersImpacted = totals.UsersImpacted;
                summary.AgentsImpacted = totals.AgentsImpacted;
                summary.PoliciesInvolved = totals.PoliciesInvolved;

                summary.TopAgents = Rank(await QueryAsync<RankProjection>(ScopedAgentsSql), countUsers: true);

                // Which policies affected each ranked agent - the same cross-tab as the unfiltered summary's,
                // counted over the matches of people in scope only.
                var agentIds = summary.TopAgents.Select(a => a.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
                if (agentIds.Count > 0)
                {
                    var agentParameters = agentIds
                        .Select((id, i) => new SqlParameter("@agent" + i.ToString(CultureInfo.InvariantCulture), id))
                        .ToArray();
                    var breakdown = await QueryAsync<ScopedAgentPolicyRow>(
                        ScopedAgentPoliciesSql.Replace("{agentIds}", string.Join(", ", agentParameters.Select(p => p.ParameterName))),
                        agentParameters);

                    AttachPolicies(summary.TopAgents, breakdown.Select(r => new PolicyBreakdownRow
                    {
                        Key = r.AgentId,
                        PolicyId = r.PolicyId,
                        Name = r.Name,
                        Blocked = r.Blocked,
                        Audited = r.Audited,
                    }));
                }

                summary.TopPolicies = Rank(await QueryAsync<RankProjection>(ScopedPoliciesSql), countUsers: true);
                summary.TopSensitivityLabels = Rank(await QueryAsync<RankProjection>(ScopedLabelsSql), countUsers: true);

                if (includeIndividuals)
                {
                    var ranked = await QueryAsync<ScopedUserRow>(ScopedTopUsersSql, new SqlParameter("@top", TopN));
                    var ids = ranked.Select(r => r.UserId).ToList();
                    var names = ids.Count == 0
                        ? new Dictionary<int, string>()
                        : await db.users.Where(u => ids.Contains(u.ID)).ToDictionaryAsync(u => u.ID, u => u.UserPrincipalName);

                    summary.TopUsers = ToRows(ranked.Select(r => new RankProjection
                    {
                        Id = r.UserId.ToString(CultureInfo.InvariantCulture),
                        Name = names.TryGetValue(r.UserId, out var upn) ? upn : null,
                        Blocked = r.Blocked,
                        Audited = r.Audited,
                    }).ToList(), countUsers: false);

                    // Every person listed is in scope, so their own breakdown needs no further narrowing.
                    var events = db.copilot_dlp_events
                        .Where(e => e.RelatedChat.TimeStampUtc >= fromUtc && e.RelatedChat.TimeStampUtc <= toUtc
                                    && e.RelatedChat.UserId != null);
                    await AttachUserPolicyBreakdownAsync(events, summary.TopUsers);
                }

                summary.Trend = (await QueryAsync<ScopedDayRow>(ScopedTrendSql))
                    .OrderBy(d => d.Day)
                    .Select(d => new DlpTrendPoint { Date = d.Day, BlockedCount = d.Blocked, AuditedCount = d.Audited })
                    .ToList();

                // Tenant-wide DLP.All activity, narrowed the same way through the audit event's user. Separate
                // statements, separate fields, never joined to the Copilot figures above.
                var tenant = (await QueryAsync<ScopedTenantTotalsRow>(ScopedTenantTotalsSql)).Single();
                summary.TenantBlockedCount = tenant.Blocked;
                summary.TenantAuditedCount = tenant.Audited;
                summary.TenantTopPolicies = Rank(await QueryAsync<RankProjection>(ScopedTenantPoliciesSql), countUsers: false);

                return summary;
            }
        }

        /// <summary>
        /// The narrowed equivalent of a ranked query: every agent, policy or label's figures - grouped in SQL,
        /// so one row each - ranked by blocked, then audited, then id, so equal rows always come in the same order.
        /// </summary>
        private static List<DlpImpactRow> Rank(List<RankProjection> rows, bool countUsers)
        {
            return ToRows(rows
                .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(TopN)
                .ToList(), countUsers);
        }

        /// <summary>One (subject, policy) pair with its counts, before it is attached to a ranked row.</summary>
        private class PolicyBreakdownRow
        {
            /// <summary>The ranked row this belongs to, matched against <see cref="DlpImpactRow.Id"/>.</summary>
            public string Key { get; set; }
            public string PolicyId { get; set; }
            public string Name { get; set; }
            public int Blocked { get; set; }
            public int Audited { get; set; }
        }

        /// <summary>
        /// Fills in, for each agent already ranked, the policies that affected it.
        /// </summary>
        /// <remarks>
        /// One grouped query for all of them rather than one per agent - a per-row query would be
        /// N+1 against the largest tables in the schema. It is scoped to the agents actually being
        /// displayed (at most <see cref="TopN"/>), so the IN clause stays far below SQL Server's
        /// parameter limit and the result set is bounded by agents x policies, not by interactions.
        /// </remarks>
        private static async Task AttachAgentPolicyBreakdownAsync(
            IQueryable<Common.Entities.Entities.AuditLog.CopilotDlpEvent> events,
            List<DlpImpactRow> agents)
        {
            var agentIds = agents.Select(a => a.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (agentIds.Count == 0)
            {
                return;
            }

            var breakdown = await events
                .Where(e => e.RelatedChat.AgentId != null
                            && e.DlpPolicyId != null
                            && agentIds.Contains(e.RelatedChat.Agent.AgentID))
                .GroupBy(e => new
                {
                    AgentId = e.RelatedChat.Agent.AgentID,
                    PolicyId = e.Policy.PolicyId,
                    e.Policy.Name,
                })
                .Select(g => new
                {
                    g.Key.AgentId,
                    g.Key.PolicyId,
                    g.Key.Name,
                    Blocked = g.Count(e => e.IsBlocked),
                    Audited = g.Count(e => !e.IsBlocked),
                })
                .ToListAsync();

            AttachPolicies(agents, breakdown.Select(r => new PolicyBreakdownRow
            {
                Key = r.AgentId,
                PolicyId = r.PolicyId,
                Name = r.Name,
                Blocked = r.Blocked,
                Audited = r.Audited,
            }));
        }

        /// <summary>
        /// Fills in, for each person already ranked, the policies that affected them.
        /// </summary>
        /// <remarks>
        /// Same one-query-for-all shape as the agent breakdown. Users are keyed by their integer id
        /// rather than a string, so the grouping is done on the id and only converted to the ranked
        /// row's string form when matching - the ranked ids come from SQL's STR(), so comparing the
        /// integers avoids depending on that formatting.
        /// </remarks>
        private static async Task AttachUserPolicyBreakdownAsync(
            IQueryable<Common.Entities.Entities.AuditLog.CopilotDlpEvent> events,
            List<DlpImpactRow> users)
        {
            var userIds = new List<int?>();
            foreach (var user in users)
            {
                int parsed;
                if (int.TryParse(user.Id, out parsed))
                {
                    userIds.Add(parsed);
                }
            }

            if (userIds.Count == 0)
            {
                return;
            }

            var breakdown = await events
                .Where(e => e.RelatedChat.UserId != null
                            && e.DlpPolicyId != null
                            && userIds.Contains(e.RelatedChat.UserId))
                .GroupBy(e => new
                {
                    UserId = e.RelatedChat.UserId,
                    PolicyId = e.Policy.PolicyId,
                    e.Policy.Name,
                })
                .Select(g => new
                {
                    g.Key.UserId,
                    g.Key.PolicyId,
                    g.Key.Name,
                    Blocked = g.Count(e => e.IsBlocked),
                    Audited = g.Count(e => !e.IsBlocked),
                })
                .ToListAsync();

            AttachPolicies(users, breakdown.Select(r => new PolicyBreakdownRow
            {
                Key = r.UserId.HasValue ? r.UserId.Value.ToString(CultureInfo.InvariantCulture) : null,
                PolicyId = r.PolicyId,
                Name = r.Name,
                Blocked = r.Blocked,
                Audited = r.Audited,
            }));
        }

        /// <summary>
        /// Hangs each ranked row's policies off it, ordered the same way as every other table.
        /// </summary>
        private static void AttachPolicies(List<DlpImpactRow> rows, IEnumerable<PolicyBreakdownRow> breakdown)
        {
            var byKey = breakdown
                .Where(r => r.Key != null)
                .GroupBy(r => r.Key)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var row in rows)
            {
                List<PolicyBreakdownRow> matches;
                if (row.Id == null || !byKey.TryGetValue(row.Id, out matches))
                {
                    // No policy detail for this row: an empty list, not null, so the UI can tell
                    // "expanded and there is nothing" apart from "this row has no breakdown".
                    row.Policies = new List<DlpImpactRow>();
                    continue;
                }

                row.Policies = matches
                    .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited)
                    .Select(r => new DlpImpactRow
                    {
                        Id = r.PolicyId,
                        Name = string.IsNullOrEmpty(r.Name) ? r.PolicyId : r.Name,
                        BlockedCount = r.Blocked,
                        AuditedCount = r.Audited,
                    })
                    .ToList();
            }
        }

        private static List<DlpImpactRow> ToRows(List<RankProjection> projections, bool countUsers)
        {
            return projections.Select(p => new DlpImpactRow
            {
                Id = p.Id == null ? null : p.Id.Trim(),
                // Agents, policies and labels can arrive with an id but no friendly name; showing the id
                // beats showing a blank row the admin cannot act on.
                Name = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name,
                BlockedCount = p.Blocked,
                AuditedCount = p.Audited,
                UsersAffected = countUsers ? (int?)p.Users : null,
            }).ToList();
        }

        /// <summary>Snaps an arbitrary day count to the nearest offered window.</summary>
        internal static int SnapWindow(int days)
        {
            var snapped = AllowedWindowDays[0];
            var bestDistance = int.MaxValue;

            foreach (var allowed in AllowedWindowDays)
            {
                var distance = Math.Abs(allowed - days);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    snapped = allowed;
                }
            }

            return snapped;
        }
    }
}