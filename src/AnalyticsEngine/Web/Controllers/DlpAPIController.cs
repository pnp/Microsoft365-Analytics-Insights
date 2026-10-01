using Common.Entities;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.SqlServer;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
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
    [RoutePrefix("api/Dlp")]
    public class DlpAPIController : ApiController
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
        public IHttpActionResult Availability()
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
        public async Task<IHttpActionResult> Summary(int days = 28)
        {
            // The administrator's global filter, resolved for this reader and enforced here whatever the page
            // sends. Everything below describes only the people it leaves them seeing.
            var scope = await _scopes.ResolveAsync(Request, User, null, CancellationToken.None);
            return Ok(await BuildSummaryAsync(
                days,
                includeIndividuals: PortalAccess.Evaluate(Request, User).SeePii,
                inScope: scope.IsRestricted ? (Func<int, bool>)scope.Includes : null));
        }

        /// <summary>
        /// The query work behind <see cref="Summary"/>, separated so it can be executed against a real
        /// database in tests without an ASP.NET request pipeline.
        /// </summary>
        /// <param name="inScope">
        /// The people the summary may describe - the administrator's global filter - or <c>null</c> for
        /// everyone. Narrowed through <see cref="BuildScopedSummaryAsync"/>; with no filter the summary is
        /// built exactly as before.
        /// </param>
        internal async Task<DlpSummary> BuildSummaryAsync(int days, bool includeIndividuals = true, Func<int, bool> inScope = null)
        {
            if (inScope != null)
            {
                return await BuildScopedSummaryAsync(days, includeIndividuals, inScope);
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

        /// <summary>
        /// One grouped fact for the narrowed summary: a dimension value, one person, blocked or not, and how
        /// many matches. Small by construction - bounded by people x values, never by interactions.
        /// </summary>
        private sealed class ScopedFact
        {
            public int? DbId { get; set; }
            public string Id { get; set; }
            public string Name { get; set; }
            public string PolicyId { get; set; }
            public string PolicyName { get; set; }
            public DateTime? Day { get; set; }
            public int UserId { get; set; }
            public bool IsBlocked { get; set; }
            public int Count { get; set; }
        }

        /// <summary>
        /// The summary narrowed to the people the administrator's global filter leaves this reader seeing.
        /// </summary>
        /// <remarks>
        /// <para><b>Same figures, narrowed.</b> Every figure and ranking is the one <see cref="BuildSummaryAsync"/>
        /// computes, with the same ordering, from facts grouped in SQL by dimension, person and outcome and
        /// then filtered to the scope in memory. The scope is a set of up to hundreds of thousands of user
        /// ids, which EF cannot send to SQL Server as a parameter - and an <c>IN</c> list that size is a query
        /// SQL Server may refuse to compile. Grouping by person first keeps every result small: it is bounded
        /// by the people and values involved, not by the number of interactions.</para>
        /// <para><b>Fails closed.</b> A match with no user cannot be attributed to anyone, so a filtered summary
        /// leaves it out - the same rule as every other report under a filter.</para>
        /// </remarks>
        private async Task<DlpSummary> BuildScopedSummaryAsync(int days, bool includeIndividuals, Func<int, bool> inScope)
        {
            var windowDays = SnapWindow(days);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.Date.AddDays(-windowDays);

            using (var db = _contextFactory.Create())
            {
                var summary = new DlpSummary { FromUtc = fromUtc, ToUtc = toUtc };

                var events = db.copilot_dlp_events
                    .Where(e => e.RelatedChat.TimeStampUtc >= fromUtc && e.RelatedChat.TimeStampUtc <= toUtc
                                && e.RelatedChat.UserId != null);

                var perUser = InScope(await events
                    .GroupBy(e => new { e.RelatedChat.UserId, e.IsBlocked })
                    .Select(g => new ScopedFact { UserId = g.Key.UserId.Value, IsBlocked = g.Key.IsBlocked, Count = g.Count() })
                    .ToListAsync(), inScope);

                summary.CopilotBlockedCount = perUser.Where(f => f.IsBlocked).Sum(f => f.Count);
                summary.CopilotAuditedCount = perUser.Where(f => !f.IsBlocked).Sum(f => f.Count);
                summary.UsersImpacted = perUser.Where(f => f.IsBlocked).Select(f => f.UserId).Distinct().Count();

                var agents = InScope(await events
                    .Where(e => e.RelatedChat.AgentId != null)
                    .GroupBy(e => new
                    {
                        e.RelatedChat.AgentId,
                        Id = e.RelatedChat.Agent.AgentID,
                        e.RelatedChat.Agent.Name,
                        e.RelatedChat.UserId,
                        e.IsBlocked,
                    })
                    .Select(g => new ScopedFact
                    {
                        DbId = g.Key.AgentId,
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        UserId = g.Key.UserId.Value,
                        IsBlocked = g.Key.IsBlocked,
                        Count = g.Count(),
                    })
                    .ToListAsync(), inScope);

                summary.AgentsImpacted = agents.Where(f => f.IsBlocked).Select(f => f.DbId).Distinct().Count();
                summary.TopAgents = Rank(agents, countUsers: true);
                await AttachScopedAgentPolicyBreakdownAsync(events, summary.TopAgents, inScope);

                var policies = InScope(await events
                    .Where(e => e.DlpPolicyId != null)
                    .GroupBy(e => new { e.DlpPolicyId, Id = e.Policy.PolicyId, e.Policy.Name, e.RelatedChat.UserId, e.IsBlocked })
                    .Select(g => new ScopedFact
                    {
                        DbId = g.Key.DlpPolicyId,
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        UserId = g.Key.UserId.Value,
                        IsBlocked = g.Key.IsBlocked,
                        Count = g.Count(),
                    })
                    .ToListAsync(), inScope);

                summary.PoliciesInvolved = policies.Select(f => f.DbId).Distinct().Count();
                summary.TopPolicies = Rank(policies, countUsers: true);

                var labels = InScope(await events
                    .Where(e => e.SensitivityLabelId != null)
                    .GroupBy(e => new { e.SensitivityLabel.LabelId, e.RelatedChat.UserId, e.IsBlocked })
                    .Select(g => new ScopedFact
                    {
                        Id = g.Key.LabelId,
                        Name = g.Key.LabelId,
                        UserId = g.Key.UserId.Value,
                        IsBlocked = g.Key.IsBlocked,
                        Count = g.Count(),
                    })
                    .ToListAsync(), inScope);

                summary.TopSensitivityLabels = Rank(labels, countUsers: true);

                if (includeIndividuals)
                {
                    var ranked = perUser
                        .GroupBy(f => f.UserId)
                        .Select(g => new
                        {
                            UserId = g.Key,
                            Blocked = g.Where(f => f.IsBlocked).Sum(f => f.Count),
                            Audited = g.Where(f => !f.IsBlocked).Sum(f => f.Count),
                        })
                        .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited).ThenBy(r => r.UserId)
                        .Take(TopN)
                        .ToList();

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
                    await AttachUserPolicyBreakdownAsync(events, summary.TopUsers);
                }

                var daily = InScope(await events
                    .GroupBy(e => new { Day = DbFunctions.TruncateTime(e.RelatedChat.TimeStampUtc), e.RelatedChat.UserId, e.IsBlocked })
                    .Select(g => new ScopedFact { Day = g.Key.Day, UserId = g.Key.UserId.Value, IsBlocked = g.Key.IsBlocked, Count = g.Count() })
                    .ToListAsync(), inScope);

                summary.Trend = daily
                    .Where(f => f.Day.HasValue)
                    .GroupBy(f => f.Day.Value)
                    .OrderBy(g => g.Key)
                    .Select(g => new DlpTrendPoint
                    {
                        Date = g.Key,
                        BlockedCount = g.Where(f => f.IsBlocked).Sum(f => f.Count),
                        AuditedCount = g.Where(f => !f.IsBlocked).Sum(f => f.Count),
                    })
                    .ToList();

                // Tenant-wide DLP.All activity, narrowed the same way through the audit event's user. Separate
                // query, separate fields, never joined to the Copilot figures above.
                var tenantMatches = db.dlp_rule_matches
                    .Where(m => m.AuditEvent.TimeStamp >= fromUtc && m.AuditEvent.TimeStamp <= toUtc
                                && m.AuditEvent.UserId != null);

                var tenantPerUser = InScope(await tenantMatches
                    .GroupBy(m => new { m.AuditEvent.UserId, m.IsBlocked })
                    .Select(g => new ScopedFact { UserId = g.Key.UserId.Value, IsBlocked = g.Key.IsBlocked, Count = g.Count() })
                    .ToListAsync(), inScope);

                summary.TenantBlockedCount = tenantPerUser.Where(f => f.IsBlocked).Sum(f => f.Count);
                summary.TenantAuditedCount = tenantPerUser.Where(f => !f.IsBlocked).Sum(f => f.Count);

                var tenantPolicies = InScope(await tenantMatches
                    .Where(m => m.DlpPolicyId != null)
                    .GroupBy(m => new { Id = m.Policy.PolicyId, m.Policy.Name, m.AuditEvent.UserId, m.IsBlocked })
                    .Select(g => new ScopedFact
                    {
                        Id = g.Key.Id,
                        Name = g.Key.Name,
                        UserId = g.Key.UserId.Value,
                        IsBlocked = g.Key.IsBlocked,
                        Count = g.Count(),
                    })
                    .ToListAsync(), inScope);

                summary.TenantTopPolicies = Rank(tenantPolicies, countUsers: false);

                return summary;
            }
        }

        private static List<ScopedFact> InScope(List<ScopedFact> facts, Func<int, bool> inScope)
        {
            return facts.Where(f => inScope(f.UserId)).ToList();
        }

        /// <summary>The narrowed equivalent of a ranked SQL query: the top values by blocked, then audited.</summary>
        private static List<DlpImpactRow> Rank(List<ScopedFact> facts, bool countUsers)
        {
            return ToRows(facts
                .GroupBy(f => new { f.Id, f.Name })
                .Select(g => new RankProjection
                {
                    Id = g.Key.Id,
                    Name = g.Key.Name,
                    Blocked = g.Where(f => f.IsBlocked).Sum(f => f.Count),
                    Audited = g.Where(f => !f.IsBlocked).Sum(f => f.Count),
                    Users = g.Select(f => f.UserId).Distinct().Count(),
                })
                .OrderByDescending(r => r.Blocked).ThenByDescending(r => r.Audited).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(TopN)
                .ToList(), countUsers);
        }

        /// <summary>
        /// <see cref="AttachAgentPolicyBreakdownAsync"/> for a narrowed summary: an agent's policies, counted
        /// over the matches of people in scope only.
        /// </summary>
        private static async Task AttachScopedAgentPolicyBreakdownAsync(
            IQueryable<Common.Entities.Entities.AuditLog.CopilotDlpEvent> events,
            List<DlpImpactRow> agents,
            Func<int, bool> inScope)
        {
            var agentIds = agents.Select(a => a.Id).Where(id => !string.IsNullOrEmpty(id)).ToList();
            if (agentIds.Count == 0)
            {
                return;
            }

            var facts = InScope(await events
                .Where(e => e.RelatedChat.AgentId != null
                            && e.DlpPolicyId != null
                            && agentIds.Contains(e.RelatedChat.Agent.AgentID))
                .GroupBy(e => new
                {
                    AgentId = e.RelatedChat.Agent.AgentID,
                    PolicyId = e.Policy.PolicyId,
                    e.Policy.Name,
                    e.RelatedChat.UserId,
                    e.IsBlocked,
                })
                .Select(g => new ScopedFact
                {
                    Id = g.Key.AgentId,
                    PolicyId = g.Key.PolicyId,
                    PolicyName = g.Key.Name,
                    UserId = g.Key.UserId.Value,
                    IsBlocked = g.Key.IsBlocked,
                    Count = g.Count(),
                })
                .ToListAsync(), inScope);

            AttachPolicies(agents, facts
                .GroupBy(f => new { f.Id, f.PolicyId, f.PolicyName })
                .Select(g => new PolicyBreakdownRow
                {
                    Key = g.Key.Id,
                    PolicyId = g.Key.PolicyId,
                    Name = g.Key.PolicyName,
                    Blocked = g.Where(f => f.IsBlocked).Sum(f => f.Count),
                    Audited = g.Where(f => !f.IsBlocked).Sum(f => f.Count),
                }));
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
