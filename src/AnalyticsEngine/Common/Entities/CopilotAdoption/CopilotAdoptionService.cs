using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The complete, scored output of one adoption analysis: the executive summary, every scored
    /// licensed user and every ranked licence candidate.
    ///
    /// Produced in a single pass and then sliced in memory, so paging, filtering, sorting and CSV
    /// export never re-run the heavy queries - and, more importantly, so the list an admin exports is
    /// guaranteed to be the same data the summary on screen was calculated from. A CSV that quietly
    /// disagrees with the chart above it is worse than no CSV.
    /// </summary>
    public class CopilotAdoptionAnalysis
    {
        public CopilotAdoptionSummary Summary { get; set; } = new CopilotAdoptionSummary();

        public List<LicensedUserAdoptionRow> LicensedUsers { get; set; } = new List<LicensedUserAdoptionRow>();

        public List<LicenceOpportunityRow> Opportunities { get; set; } = new List<LicenceOpportunityRow>();

        /// <summary>
        /// The queries that produced this analysis, keyed by a short name, for the SQL popover the rest
        /// of the admin site uses. Showing the working is part of the point: these numbers get quoted
        /// in licence negotiations, so an admin has to be able to verify them independently.
        /// </summary>
        public Dictionary<string, string> Sql { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Runs the Copilot licence-adoption analysis.
    ///
    /// Lives in Common.Entities rather than in the web project so the same analysis can be driven from
    /// a web-job later (scheduled adoption e-mails were an explicit requirement) without any of it
    /// having to be lifted out of a controller first. The controller's only job is caching, paging and
    /// serialisation.
    ///
    /// Every query is individually guarded: a failure degrades to a warning on the result rather than
    /// failing the whole report, matching how the Reports area handles its heavy queries. A partially
    /// complete report with an explicit caveat is far more useful to someone preparing a licence review
    /// than an error page.
    /// </summary>
    public class CopilotAdoptionService
    {
        /// <summary>
        /// Per-query timeout. Higher than the Reports area's 25s because this analysis is deliberate
        /// and on-demand rather than a dashboard refresh, but still far below the ~230s at which Azure
        /// App Service kills the request - so a struggling database produces a warning, not a 500.
        /// </summary>
        public const int QueryTimeoutSecs = 90;

        /// <summary>How far back the weekly trend chart looks. Long enough to show whether an enablement push worked.</summary>
        public const int TrendMonths = 6;

        private readonly CopilotAdoptionOptions _options;
        private readonly Func<AnalyticsEntitiesContext> _contextFactory;

        public CopilotAdoptionService(
            CopilotAdoptionOptions options = null,
            Func<AnalyticsEntitiesContext> contextFactory = null)
        {
            _options = options ?? CopilotAdoptionOptions.Default;
            _contextFactory = contextFactory ?? (() => new AnalyticsEntitiesContext());
        }

        public CopilotAdoptionOptions Options => _options;

        /// <summary>
        /// Runs the whole analysis.
        /// </summary>
        /// <param name="seatLicenceTypeIdOverride">
        /// Optional explicit set of licence-type ids to treat as Copilot seats, for the case where the
        /// automatic classification gets a new or unusual SKU wrong. Null means "classify automatically".
        /// </param>
        public async Task<CopilotAdoptionAnalysis> AnalyseAsync(
            IEnumerable<int> seatLicenceTypeIdOverride = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var nowUtc = DateTime.UtcNow;
            var windowStart = nowUtc.Date.AddDays(-Math.Max(1, _options.WindowDays));
            var historyStart = nowUtc.Date.AddDays(-Math.Max(_options.WindowDays, _options.HistoryDays));
            var settled = nowUtc.Date.AddDays(-Math.Max(0, _options.UsageReportLagDays));
            var trendStart = MondayOf(nowUtc.Date.AddMonths(-TrendMonths));

            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.GeneratedUtc = nowUtc;
            summary.WindowDays = _options.WindowDays;
            summary.FromUtc = windowStart;
            summary.ToUtc = nowUtc;
            summary.Options = _options;

            // ----- 1. Which licence types count as a Copilot seat -------------------------------
            var licenceTypes = await SafeAsync(
                () => QueryAsync<LicenceTypeRow>(CopilotAdoptionSql.LicenceTypesSql, cancellationToken),
                summary.Warnings,
                "licence types");

            if (licenceTypes == null || licenceTypes.Count == 0)
            {
                summary.Warnings.Add(
                    "No licence information has been imported, so Copilot seats cannot be identified. "
                    + "Enable the user metadata import to use this tool.");
                return analysis;
            }

            summary.SeatLicenceTypes = CopilotLicenceClassifier.Classify(licenceTypes);
            var seatIds = CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, seatLicenceTypeIdOverride);
            summary.DataSources.UserMetadataAvailable = true;

            if (seatIds.Count == 0)
            {
                summary.Warnings.Add(
                    "No Microsoft 365 Copilot licences were found in this tenant. Adoption cannot be reported "
                    + "until at least one Copilot seat is assigned and the user import has run.");
                // The licence-opportunity side still works with no seats at all - that is exactly the
                // "should we buy Copilot?" case - so carry on rather than returning here.
            }

            // ----- 2. Availability probes -------------------------------------------------------
            summary.DataSources.AuditAvailable = await SafeScalarAsync(
                CopilotAdoptionSql.HasCopilotAuditDataSql,
                summary.Warnings,
                "Copilot audit data probe",
                cancellationToken,
                new SqlParameter("@from", windowStart)) == 1;

            summary.DataSources.CopilotUsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestCopilotReportDateSql,
                summary.Warnings,
                "Copilot usage-report snapshot date",
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.CopilotUsageReportAvailable = summary.DataSources.CopilotUsageReportDate.HasValue;

            summary.DataSources.M365UsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestM365ReportDateSql,
                summary.Warnings,
                "Microsoft 365 usage-report snapshot date",
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.M365UsageReportsAvailable = summary.DataSources.M365UsageReportDate.HasValue;

            summary.DataSources.CopilotUsageReportObfuscated = await SafeScalarAsync(
                CopilotAdoptionSql.CopilotReportObfuscatedSql,
                summary.Warnings,
                "Copilot usage-report anonymisation check",
                cancellationToken) == 1;

            if (summary.DataSources.CopilotUsageReportObfuscated)
            {
                summary.Warnings.Add(
                    "This tenant has 'concealed user information' enabled, so Microsoft's per-user Copilot "
                    + "report returns hashed identities and cannot be used. Per-user figures below come from "
                    + "the Copilot audit log, which is unaffected by that setting.");
            }

            if (!summary.DataSources.AuditAvailable && !summary.DataSources.CopilotUsageReportAvailable)
            {
                summary.Warnings.Add(
                    "Neither the Copilot audit import nor Microsoft's Copilot usage report has any data for "
                    + "this period, so every licensed user will appear as unused. Check the Health page before "
                    + "acting on these numbers.");
            }

            if (!summary.DataSources.AuditAvailable && summary.DataSources.CopilotUsageReportAvailable)
            {
                summary.Warnings.Add(
                    "The Copilot audit import has no data for this period, so per-user engagement is derived "
                    + "from Microsoft's own usage report. That report covers Microsoft's aggregation window "
                    + "rather than the period selected here, and excludes unlicensed Copilot Chat use entirely.");
            }

            // ----- 3. Licensed users ------------------------------------------------------------
            if (seatIds.Count > 0)
            {
                await BuildLicensedUsersAsync(analysis, seatIds, windowStart, historyStart, nowUtc, cancellationToken);
                await BuildUsageByAppAsync(analysis, seatIds, windowStart, cancellationToken);
                await BuildWeeklyTrendAsync(analysis, seatIds, trendStart, cancellationToken);
            }

            // ----- 4. Licence opportunities -----------------------------------------------------
            await BuildOpportunitiesAsync(analysis, seatIds, windowStart, cancellationToken);

            FinaliseSummary(analysis);
            return analysis;
        }

        #region Licensed users

        private async Task BuildLicensedUsersAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime windowStart,
            DateTime historyStart,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;

            // Seat assignments are read separately from the detail query for two reasons: it is the
            // only way to name every seat SKU a user holds (a user can hold more than one), and it
            // gives an exact licensed-user count that is not subject to the detail query's row cap.
            var assignmentsSql = CopilotAdoptionSql.SeatAssignmentsSql(seatIds);
            analysis.Sql["seatAssignments"] = assignmentsSql;

            var assignments = await SafeAsync(
                () => QueryAsync<SeatAssignmentRow>(assignmentsSql, cancellationToken),
                summary.Warnings,
                "Copilot seat assignments") ?? new List<SeatAssignmentRow>();

            var licencesByUser = assignments
                .GroupBy(a => a.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(", ", g.Select(a => a.LicenceName)
                                            .Where(n => !string.IsNullOrWhiteSpace(n))
                                            .Distinct()
                                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));

            summary.LicensedUsers = licencesByUser.Count;

            var includeReport = summary.DataSources.CopilotUsageReportDate.HasValue
                                && !summary.DataSources.CopilotUsageReportObfuscated;

            var coworkAgentIds = await SafeAsync(
                () => QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken),
                summary.Warnings,
                "Cowork agent lookup") ?? new List<IntValueRow>();

            var coworkIds = coworkAgentIds.Select(r => r.Value).ToList();

            var detailSql = CopilotAdoptionSql.LicensedUsersSql(seatIds, coworkIds, includeReport);
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@historyFrom", historyStart },
                { "@maxRows", _options.MaxLicensedUsersScored },
            };
            if (includeReport)
            {
                parameters["@copilotReportDate"] = summary.DataSources.CopilotUsageReportDate.Value;
            }

            analysis.Sql["licensedUsers"] = CopilotAdoptionSql.ForDisplay(detailSql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<LicensedUserUsageRow>(detailSql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "licensed user detail");

            if (rows == null)
            {
                return;
            }

            if (rows.Count >= _options.MaxLicensedUsersScored)
            {
                summary.Warnings.Add(
                    $"Only the first {_options.MaxLicensedUsersScored:N0} licensed users were analysed. "
                    + "The figures below therefore describe that subset, not the whole tenant.");
            }

            foreach (var row in rows)
            {
                string licences;
                row.SeatLicences = licencesByUser.TryGetValue(row.UserId, out licences) ? licences : null;
            }

            analysis.LicensedUsers = rows
                .Select(row => CopilotAdoptionScoring.Score(
                    row, windowStart, nowUtc, summary.DataSources.AuditAvailable, _options))
                .ToList();

            // The exact count comes from the seat-assignment read; fall back to what was scored if
            // that query is the one that failed, so the percentages still have a sane denominator.
            if (summary.LicensedUsers == 0)
            {
                summary.LicensedUsers = analysis.LicensedUsers.Count;
            }
        }

        private async Task BuildUsageByAppAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            if (!analysis.Summary.DataSources.AuditAvailable)
            {
                return;
            }

            var sql = CopilotAdoptionSql.UsageByAppSql(seatIds);
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            analysis.Sql["usageByApp"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                analysis.Summary.Warnings,
                "Copilot usage by app");

            if (rows == null) return;

            analysis.Summary.UsageByApp = rows
                .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                .ToList();
        }

        private async Task BuildWeeklyTrendAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime trendStart,
            CancellationToken cancellationToken)
        {
            if (!analysis.Summary.DataSources.AuditAvailable)
            {
                return;
            }

            var coworkAgentIds = await SafeAsync(
                () => QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken),
                analysis.Summary.Warnings,
                "Cowork agent lookup") ?? new List<IntValueRow>();

            var sql = CopilotAdoptionSql.WeeklyAdoptionTrendSql(seatIds, coworkAgentIds.Select(r => r.Value));
            var parameters = new Dictionary<string, object> { { "@trendFrom", trendStart } };
            analysis.Sql["weeklyTrend"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<NamedWeekRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                analysis.Summary.Warnings,
                "weekly adoption trend");

            if (rows == null) return;

            var weekSpine = WeekSpine(trendStart, MondayOf(DateTime.UtcNow.Date));

            analysis.Summary.WeeklyTrend = rows
                .GroupBy(r => r.SeriesName)
                .OrderBy(g => g.Key)
                .Select(g => new AdoptionSeries
                {
                    Name = g.Key,
                    Points = FillWeeks(weekSpine, g.ToList()),
                })
                .ToList();
        }

        #endregion

        #region Licence opportunities

        private async Task BuildOpportunitiesAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;

            if (summary.DataSources.AuditAvailable && seatIds.Count > 0)
            {
                var unlicensedSql = CopilotAdoptionSql.UnlicensedActiveUsersSql(seatIds);
                analysis.Sql["unlicensedActiveUsers"] = CopilotAdoptionSql.ForDisplay(
                    unlicensedSql, new Dictionary<string, object> { { "@from", windowStart } });

                summary.UnlicensedActiveUsers = await SafeScalarAsync(
                    unlicensedSql,
                    summary.Warnings,
                    "unlicensed Copilot users",
                    cancellationToken,
                    new SqlParameter("@from", windowStart));
            }

            var includeAudit = summary.DataSources.AuditAvailable;
            var includeM365 = summary.DataSources.M365UsageReportsAvailable;

            if (!includeAudit && !includeM365)
            {
                summary.Warnings.Add(
                    "Licence opportunities need either the Copilot audit import or the Microsoft 365 usage "
                    + "reports. Neither has data, so no candidates can be identified.");
                return;
            }

            var sql = CopilotAdoptionSql.LicenceOpportunitiesSql(seatIds, _options, includeAudit, includeM365);
            var parameters = new Dictionary<string, object>
            {
                { "@maxRows", _options.MaxOpportunityCandidates },
            };
            if (includeAudit) parameters["@from"] = windowStart;
            if (includeM365) parameters["@m365ReportDate"] = summary.DataSources.M365UsageReportDate.Value;

            analysis.Sql["licenceOpportunities"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<UnlicensedUserSignalRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "licence opportunities");

            if (rows == null) return;

            if (!includeM365)
            {
                summary.Warnings.Add(
                    "The Microsoft 365 usage reports are not available, so licence candidates are ranked only "
                    + "on unlicensed Copilot Chat use. Heavy Microsoft 365 users who have never tried Copilot "
                    + "will not appear.");
            }

            analysis.Opportunities = rows
                .Select(r => CopilotAdoptionScoring.ScoreOpportunity(r, _options))
                .OrderByDescending(r => r.OpportunityScore)
                .ThenBy(r => r.UserPrincipalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion

        #region Summary assembly

        /// <summary>
        /// Turns the scored rows into the headline figures and breakdown charts. Pure - no database -
        /// so the whole executive view can be unit-tested from hand-written user rows.
        /// </summary>
        public void FinaliseSummary(CopilotAdoptionAnalysis analysis)
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var summary = analysis.Summary;
            var users = analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>();

            if (summary.LicensedUsers == 0)
            {
                summary.LicensedUsers = users.Count;
            }

            summary.ActiveUsers = users.Count(u => u.Band > AdoptionBand.Dormant);
            summary.NeverUsedUsers = users.Count(u => u.Band == AdoptionBand.NeverUsed);
            summary.DormantUsers = users.Count(u => u.Band == AdoptionBand.Dormant);
            summary.HabitualUsers = users.Count(u => CopilotAdoptionScoring.IsHabitual(u.Band));
            summary.ReclaimableSeats = summary.NeverUsedUsers + summary.DormantUsers;
            summary.TotalInteractions = users.Sum(u => u.Interactions);

            summary.AdoptionRatePct = CopilotAdoptionScoring.Percentage(summary.ActiveUsers, summary.LicensedUsers);
            summary.HabitRatePct = CopilotAdoptionScoring.Percentage(summary.HabitualUsers, summary.LicensedUsers);
            summary.AverageAdoptionScore = users.Count == 0
                ? 0
                : Math.Round(users.Average(u => u.AdoptionScore), 1, MidpointRounding.AwayFromZero);
            summary.MedianAdoptionScore = CopilotAdoptionScoring.Median(users.Select(u => u.AdoptionScore));

            summary.CoworkUsers = users.Count(u => u.UsedCowork);
            summary.CoworkInteractions = users.Sum(u => u.CoworkInteractions);
            summary.CoworkAdoptionPct = CopilotAdoptionScoring.Percentage(summary.CoworkUsers, summary.LicensedUsers);
            // Only claim a Cowork adoption rate when Cowork was actually seen. On a tenant that has not
            // been enabled for it, "0% Cowork adoption" reads as a failure rather than as "not available".
            summary.CoworkDetected = summary.CoworkInteractions > 0;

            summary.Funnel = BuildFunnel(summary, users);
            summary.BandBreakdown = BuildBandBreakdown(users);
            summary.AdoptionByDepartment = BuildSegments(users, u => u.Department, "(no department)");
            summary.AdoptionByCountry = BuildSegments(users, u => u.Country, "(no country)");

            var opportunities = analysis.Opportunities ?? new List<LicenceOpportunityRow>();
            summary.RecommendedForLicence = opportunities.Count(o => o.Recommended);
            summary.OpportunityByDepartment = opportunities
                .Where(o => o.Recommended)
                .GroupBy(o => string.IsNullOrWhiteSpace(o.Department) ? "(no department)" : o.Department)
                .Select(g => new AdoptionCategory { Label = g.Key, Value = g.Count() })
                .OrderByDescending(c => c.Value)
                .Take(_options.TopSegments)
                .ToList();
        }

        /// <summary>
        /// The adoption funnel: every stage is a subset of the one before it, so the biggest drop-off
        /// is visible at a glance and points straight at the intervention that is needed.
        /// </summary>
        private static List<AdoptionCategory> BuildFunnel(
            CopilotAdoptionSummary summary,
            IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            var everUsed = users.Count(u => u.Band != AdoptionBand.NeverUsed);
            var champions = users.Count(u => u.Band == AdoptionBand.Champion);

            return new List<AdoptionCategory>
            {
                new AdoptionCategory { Label = "Licensed", Value = summary.LicensedUsers },
                new AdoptionCategory { Label = "Ever used Copilot", Value = everUsed },
                new AdoptionCategory { Label = "Active this period", Value = summary.ActiveUsers },
                new AdoptionCategory { Label = "Habitual users", Value = summary.HabitualUsers },
                new AdoptionCategory { Label = "Champions", Value = champions },
            };
        }

        /// <summary>Band distribution, always including empty bands - an empty "Champions" bar is itself the finding.</summary>
        private static List<AdoptionCategory> BuildBandBreakdown(IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            return CopilotAdoptionScoring.AllBands
                .Select(band => new AdoptionCategory
                {
                    Label = CopilotAdoptionScoring.BandDisplayName(band),
                    Value = users.Count(u => u.Band == band),
                })
                .ToList();
        }

        /// <summary>
        /// Adoption per organisational segment, worst first - the running order for an enablement plan.
        /// Segments below <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> are dropped because a
        /// 0%-of-two-seats department at the top of an executive chart is noise that invites a bad call.
        /// </summary>
        private List<AdoptionSegmentRow> BuildSegments(
            IEnumerable<LicensedUserAdoptionRow> users,
            Func<LicensedUserAdoptionRow, string> selector,
            string emptyLabel)
        {
            return users
                .GroupBy(u => string.IsNullOrWhiteSpace(selector(u)) ? emptyLabel : selector(u).Trim())
                .Where(g => g.Count() >= _options.MinSeatsPerSegment)
                .Select(g => CopilotAdoptionScoring.Summarise(g.Key, g.ToList()))
                .OrderBy(s => s.AdoptionRatePct)
                .ThenByDescending(s => s.LicensedUsers)
                .Take(_options.TopSegments)
                .ToList();
        }

        #endregion

        #region Query plumbing

        /// <summary>Runs a query on its own short-lived context, so one slow report cannot hold a context open.</summary>
        private async Task<List<T>> QueryAsync<T>(
            string sql,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            using (var db = _contextFactory())
            {
                db.Database.CommandTimeout = QueryTimeoutSecs;
                return await db.Database.SqlQuery<T>(sql, parameters).ToListAsync(cancellationToken);
            }
        }

        /// <summary>
        /// Runs a query, turning any failure into a warning on the result. One heavy query timing out
        /// should cost that one chart, not the whole licence review.
        /// </summary>
        private static async Task<List<T>> SafeAsync<T>(
            Func<Task<List<T>>> query,
            List<string> warnings,
            string description)
        {
            try
            {
                return await query();
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not load {description}: {InnermostMessage(ex)}");
                return null;
            }
        }

        private async Task<int> SafeScalarAsync(
            string sql,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<int?>(sql, cancellationToken, parameters),
                warnings,
                description);

            return rows?.FirstOrDefault() ?? 0;
        }

        private async Task<DateTime?> SafeDateAsync(
            string sql,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<DateTime?>(sql, cancellationToken, parameters),
                warnings,
                description);

            return rows?.FirstOrDefault();
        }

        private static SqlParameter[] ToSqlParameters(IDictionary<string, object> parameters)
        {
            return parameters
                .Select(p => new SqlParameter(p.Key, p.Value ?? DBNull.Value))
                .ToArray();
        }

        /// <summary>The innermost exception message - the one that actually says what went wrong.</summary>
        internal static string InnermostMessage(Exception ex)
        {
            var current = ex;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }
            return current.Message;
        }

        #endregion

        #region Week helpers

        /// <summary>The Monday of the week containing <paramref name="date"/>.</summary>
        internal static DateTime MondayOf(DateTime date)
        {
            return date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }

        /// <summary>Every Monday from first to last inclusive.</summary>
        internal static List<DateTime> WeekSpine(DateTime firstMonday, DateTime lastMonday)
        {
            var weeks = new List<DateTime>();
            for (var week = firstMonday; week <= lastMonday; week = week.AddDays(7))
            {
                weeks.Add(week);
            }
            return weeks;
        }

        /// <summary>
        /// Projects query rows onto the full week spine, filling missing weeks with zero. Zero (rather
        /// than a gap) is right here: these series count audit events, and no events genuinely does
        /// mean nobody used Copilot that week.
        /// </summary>
        internal static List<AdoptionTimePoint> FillWeeks(List<DateTime> weekSpine, List<NamedWeekRow> rows)
        {
            var byWeek = new Dictionary<DateTime, double>();
            foreach (var row in rows)
            {
                byWeek[row.WeekStart.Date] = row.Value;
            }

            return weekSpine
                .Select(week => new AdoptionTimePoint
                {
                    WeekStart = week,
                    Value = byWeek.TryGetValue(week, out var value) ? value : 0,
                })
                .ToList();
        }

        #endregion

        #region Raw query row shapes

        /// <summary>One (user, Copilot seat SKU) assignment.</summary>
        public class SeatAssignmentRow
        {
            public int UserId { get; set; }

            public string LicenceName { get; set; }
        }

        /// <summary>A single integer column, for id lookups.</summary>
        public class IntValueRow
        {
            public int Value { get; set; }
        }

        /// <summary>A label/value pair for the categorical charts.</summary>
        public class CategoryQueryRow
        {
            public string Label { get; set; }

            public double Value { get; set; }
        }

        /// <summary>A point of a named weekly series.</summary>
        public class NamedWeekRow
        {
            public string SeriesName { get; set; }

            public DateTime WeekStart { get; set; }

            public double Value { get; set; }
        }

        #endregion
    }
}
