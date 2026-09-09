using Common.Entities;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.Entity.SqlServer;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Http;
using Web.AnalyticsWeb.Models.Dlp;

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
        /// The whole page in one call: KPIs, the ranked tables and the trend.
        /// </summary>
        // GET: api/Dlp/summary?days=28
        [HttpGet]
        [Route("summary")]
        public async Task<IHttpActionResult> Summary(int days = 28)
        {
            var windowDays = SnapWindow(days);
            var toUtc = DateTime.UtcNow;
            var fromUtc = toUtc.Date.AddDays(-windowDays);

            using (var db = new AnalyticsEntitiesContext())
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

                return Ok(summary);
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
