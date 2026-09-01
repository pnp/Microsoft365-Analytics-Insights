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
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public CopilotAdoptionService(
            CopilotAdoptionOptions options = null,
            IAnalyticsDbContextFactory contextFactory = null)
        {
            _options = options ?? CopilotAdoptionOptions.Default;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
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
            // Ten sequential steps of up to 90 seconds each: if the caller has already given up there is
            // no point starting, and no point continuing between steps either (see TimedAsync).
            cancellationToken.ThrowIfCancellationRequested();

            var nowUtc = DateTime.UtcNow;            var windowStart = CopilotAdoptionScoring.WindowStartUtc(nowUtc, _options.WindowDays);
            var historyStart = CopilotAdoptionScoring.WindowStartUtc(
                nowUtc, Math.Max(_options.WindowDays, _options.HistoryDays));
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
                "licence types", cancellationToken);

            if (licenceTypes == null || licenceTypes.Count == 0)
            {
                if (licenceTypes == null)
                {
                    // The query FAILED, which is not the same as a tenant with no licence data. Without
                    // this the page says "the analysis completed and found no users", which reads as a
                    // finding rather than a fault.
                    summary.MarkFiguresIncomplete("licence types");
                }

                summary.Warnings.Add(
                    "No licence information has been imported, so Copilot licences cannot be identified. "
                    + "Enable the user metadata import to use this tool.");
                return analysis;
            }

            summary.SeatLicenceTypes = CopilotLicenceClassifier.Classify(licenceTypes);
            var seatIds = CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, seatLicenceTypeIdOverride);
            summary.DataSources.UserMetadataAvailable = true;

            // When an explicit override is supplied, the classification shown to the admin has to reflect
            // what was ACTUALLY counted. Leaving the automatic verdict in place meant the methodology page
            // and the workbook asserted that a SKU was excluded while every figure on the page included it.
            if (seatLicenceTypeIdOverride != null)
            {
                var effective = new HashSet<int>(seatIds);
                foreach (var licence in summary.SeatLicenceTypes)
                {
                    licence.IsCopilotSeat = effective.Contains(licence.Id);
                }
            }

            if (seatIds.Count == 0)
            {
                summary.Warnings.Add(
                    "No Microsoft 365 Copilot licences were found in this tenant. Adoption cannot be reported "
                    + "until at least one Copilot licence is assigned and the user import has run.");
                // The licence-opportunity side still works with no seats at all - that is exactly the
                // "should we buy Copilot?" case - so carry on rather than returning here.
            }

            // ----- 2. Availability probes -------------------------------------------------------
            // Each of these decides whether a whole DATA SOURCE is used. A failure here degrades to the
            // same value as a genuine absence ("this tenant has no audit data"), which silently removes
            // entire sections and changes what the opportunities query is allowed to see. That is the
            // issue #360 defect one level up, so every probe failure marks the figures incomplete.
            summary.DataSources.AuditAvailable = await SafeScalarAsync(
                CopilotAdoptionSql.HasCopilotAuditDataSql,
                summary.Warnings,
                "Copilot audit data probe",
                () => summary.MarkFiguresIncomplete("Copilot audit data"),
                cancellationToken,
                new SqlParameter("@from", windowStart)) == 1;

            // Every Copilot query filters on copilot_chats.time_stamp, so an interaction whose denormalised
            // columns have not been written yet is simply missing from the figures. Migration
            // DenormaliseCopilotChatUserAndTime backfills existing rows, and the importer merge repairs any
            // the upgrade window left behind - but until that has caught up the page would report numbers
            // that are quietly too low. Say so rather than presenting them as fact: that is the whole
            // lesson of issue #360.
            var backfillPending = await SafeScalarAsync(
                CopilotAdoptionSql.PendingCopilotBackfillSql,
                summary.Warnings,
                "Copilot interaction backfill probe",
                () => summary.MarkFiguresIncomplete("Copilot interaction backfill check"),
                cancellationToken) == 1;

            if (backfillPending)
            {
                summary.MarkFiguresIncomplete("Copilot interactions awaiting backfill");
                summary.Warnings.Add(
                    "Some Copilot interactions have not finished being upgraded to the new reporting format, "
                    + "so every Copilot figure below is currently too low. This repairs itself automatically "
                    + "on the next few import cycles - re-run this report once the importer has caught up. "
                    + "If it persists, check that the Office 365 activity importer web job is running.");
            }

            summary.DataSources.CopilotUsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestCopilotReportDateSql,
                summary.Warnings,
                "Copilot usage-report snapshot date",
                () => summary.MarkFiguresIncomplete("Copilot usage report"),
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.CopilotUsageReportAvailable = summary.DataSources.CopilotUsageReportDate.HasValue;

            if (summary.DataSources.CopilotUsageReportDate.HasValue)
            {
                // Pin the report period as well as the date. Without this the snapshot join fans every
                // licensed user out across D7/D28/D90/D180 - see LatestCopilotReportPeriodSql.
                summary.DataSources.CopilotUsageReportPeriodDays = await SafeScalarAsync(
                    CopilotAdoptionSql.LatestCopilotReportPeriodSql,
                    summary.Warnings,
                    "Copilot usage-report snapshot period",
                    // Failing to zero here does NOT disable the report join - it pins it to
                    // report_period_days IS NULL, which no current row matches, so every licensed user
                    // scores as unused while the page still claims the report was used.
                    () => summary.MarkFiguresIncomplete("Copilot usage-report snapshot period"),
                    cancellationToken,
                    new SqlParameter("@copilotReportDate", summary.DataSources.CopilotUsageReportDate.Value),
                    new SqlParameter("@windowDays", _options.WindowDays));
            }

            summary.DataSources.M365UsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestM365ReportDateSql,
                summary.Warnings,
                "Microsoft 365 usage-report snapshot date",
                () => summary.MarkFiguresIncomplete("Microsoft 365 usage reports"),
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.M365UsageReportsAvailable = summary.DataSources.M365UsageReportDate.HasValue;

            summary.DataSources.CopilotUsageReportObfuscated = await SafeScalarAsync(
                CopilotAdoptionSql.CopilotReportObfuscatedSql,
                summary.Warnings,
                "Copilot usage-report anonymisation check",
                // Left defaulting to "not obfuscated" on failure rather than failing closed: flipping it
                // would silently discard the per-user report source, trading one invisible degradation
                // for another. Flagging the figures incomplete is the honest signal, and matches how
                // every other failure in this method is handled.
                () => summary.MarkFiguresIncomplete("Copilot usage-report anonymisation check"),
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
                await TimedAsync(analysis, CopilotAdoptionSteps.LicensedUsers,
                    () => BuildLicensedUsersAsync(analysis, seatIds, windowStart, historyStart, nowUtc, cancellationToken));
                await TimedAsync(analysis, CopilotAdoptionSteps.UsageByApp,
                    () => BuildUsageByAppAsync(analysis, seatIds, windowStart, cancellationToken));
                await TimedAsync(analysis, CopilotAdoptionSteps.WeeklyTrend,
                    () => BuildWeeklyTrendAsync(analysis, seatIds, trendStart, cancellationToken));
            }

            // ----- 4. Licence opportunities -----------------------------------------------------
            await TimedAsync(analysis, CopilotAdoptionSteps.LicenceOpportunities,
                () => BuildOpportunitiesAsync(analysis, seatIds, windowStart, cancellationToken));

            // ----- 5. The populations Microsoft's own reporting cannot see ----------------------
            // Agents and unlicensed Copilot Chat are reported in their own right, not merely as inputs
            // to the seat decision: an agent estate has its own retirement problem, and unlicensed
            // Chat use is the one Copilot population that is invisible in Microsoft's usage reports.
            if (summary.DataSources.AuditAvailable)
            {
                await TimedAsync(analysis, CopilotAdoptionSteps.AgentEstate,
                    () => BuildAgentEstateAsync(analysis, seatIds, windowStart, nowUtc, cancellationToken));
                await TimedAsync(analysis, CopilotAdoptionSteps.UnlicensedPopulation,
                    () => BuildUnlicensedPopulationAsync(analysis, seatIds, windowStart, cancellationToken));
                await TimedAsync(analysis, CopilotAdoptionSteps.ResourceTypes,
                    () => BuildResourceTypesAsync(analysis, windowStart, cancellationToken));
            }

            var scoringWatch = System.Diagnostics.Stopwatch.StartNew();
            FinaliseSummary(analysis);
            scoringWatch.Stop();
            summary.Diagnostics.Record(CopilotAdoptionSteps.Scoring, scoringWatch.ElapsedMilliseconds);

            summary.Diagnostics.TotalMs = (long)(DateTime.UtcNow - nowUtc).TotalMilliseconds;
            return analysis;
        }

        /// <summary>
        /// Runs one step of the analysis and records how long it took.
        ///
        /// Timed in a finally block on purpose: a step that threw, or that a query timeout degraded into
        /// a warning, is precisely the one an operator needs to see the duration of. Recording only
        /// successful steps would hide the 90-second failures that this instrumentation exists to expose.
        /// </summary>
        private static async Task TimedAsync(CopilotAdoptionAnalysis analysis, string step, Func<Task> work)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failed = false;
            try
            {
                await work();
            }
            catch (Exception)
            {
                failed = true;
                throw;
            }
            finally
            {
                watch.Stop();
                analysis.Summary.Diagnostics.Record(step, watch.ElapsedMilliseconds, failed);
            }
        }

        #region Agents and the unlicensed population

        /// <summary>
        /// The agent estate: every agent used in the history window, with the verdict on each.
        ///
        /// Uses the history window rather than the reporting period on purpose - an agent that has not
        /// been touched for six months is precisely the thing an inventory review is looking for, and
        /// it would be invisible in a 28-day window.
        /// </summary>
        private async Task BuildAgentEstateAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime windowStart,
            DateTime nowUtc,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;

            // The inventory reads its own, much shorter history than the rest of the analysis - see
            // CopilotAdoptionOptions.AgentHistoryDays. Never shorter than the reporting window, or an
            // agent used inside the period could be missing from its own inventory.
            var agentHistoryDays = Math.Max(
                _options.WindowDays, Math.Max(_options.AgentRetireInactiveDays, _options.AgentHistoryDays));
            var agentHistoryStart = CopilotAdoptionScoring.WindowStartUtc(nowUtc, agentHistoryDays);

            summary.Agents.HistoryDays = agentHistoryDays;

            var sql = CopilotAdoptionSql.AgentUsageSql(seatIds);
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@historyFrom", agentHistoryStart },
                { "@maxRows", _options.MaxAgents },
            };
            analysis.Sql["agents"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<AgentUsageQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "Copilot agent usage", cancellationToken);

            if (rows == null) return;

            analysis.Agents = rows
                .Select(r => CopilotAdoptionScoring.ScoreAgent(r, nowUtc, _options))
                .OrderByDescending(a => a.Interactions)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (analysis.Agents.Count >= _options.MaxAgents)
            {
                summary.Warnings.Add(
                    $"The agent inventory was capped at {_options.MaxAgents} agents, so the agent figures are "
                    + "a floor rather than a total.");
            }

            var byDeptSql = CopilotAdoptionSql.AgentUsageByDepartmentSql();
            var byDeptParameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            analysis.Sql["agentsByDepartment"] = CopilotAdoptionSql.ForDisplay(byDeptSql, byDeptParameters);

            var byDept = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(byDeptSql, cancellationToken, ToSqlParameters(byDeptParameters)),
                summary.Warnings,
                "agent usage by department", cancellationToken);

            if (byDept != null)
            {
                summary.Agents.UsageByDepartment = byDept
                    .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                    .ToList();
            }
        }

        /// <summary>
        /// Unlicensed Copilot Chat users, described the same way the licensed population is so the two
        /// can be compared directly.
        ///
        /// Deliberately a separate query from the licence-opportunity ranking: that one is capped and
        /// ordered by score, so its rows are a biased sample and must never be used to describe a
        /// population's shape.
        /// </summary>
        private async Task BuildUnlicensedPopulationAsync(
            CopilotAdoptionAnalysis analysis,
            List<int> seatIds,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;

            var sql = CopilotAdoptionSql.UnlicensedUsageRowsSql(seatIds);
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@maxRows", _options.MaxUnlicensedUsersScored },
            };
            analysis.Sql["unlicensedUsage"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<UnlicensedUsageQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "unlicensed Copilot usage", cancellationToken);

            if (rows != null)
            {
                analysis.UnlicensedUsers = rows;
                summary.Unlicensed.Truncated = rows.Count >= _options.MaxUnlicensedUsersScored;

                if (summary.Unlicensed.Truncated)
                {
                    summary.Warnings.Add(
                        $"Unlicensed Copilot usage was capped at {_options.MaxUnlicensedUsersScored} users, so "
                        + "those figures are a floor rather than a total.");
                }
            }

            var appSql = CopilotAdoptionSql.UnlicensedUsageByAppSql(seatIds);
            var appParameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            analysis.Sql["unlicensedUsageByApp"] = CopilotAdoptionSql.ForDisplay(appSql, appParameters);

            var apps = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(appSql, cancellationToken, ToSqlParameters(appParameters)),
                summary.Warnings,
                "unlicensed Copilot usage by app", cancellationToken);

            if (apps != null)
            {
                summary.Unlicensed.UsageByApp = apps
                    .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                    .ToList();
            }
        }

        /// <summary>
        /// What kinds of tenant content Copilot actually grounded its answers in. The clearest
        /// available evidence that Copilot is doing work on the organisation's own data rather than
        /// answering generic questions any free chatbot could.
        /// </summary>
        private async Task BuildResourceTypesAsync(
            CopilotAdoptionAnalysis analysis,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var sql = CopilotAdoptionSql.TopResourceTypesSql();
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            analysis.Sql["resourceTypes"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                analysis.Summary.Warnings,
                "Copilot accessed resource types", cancellationToken);

            if (rows == null) return;

            analysis.Summary.TopResourceTypes = rows
                .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                .ToList();
        }

        #endregion

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
                "Copilot licence assignments", cancellationToken);

            if (assignments == null)
            {
                // The seat count comes from here. Falling through with an empty list would report zero
                // licensed users - indistinguishable from a tenant that genuinely has none.
                summary.MarkFiguresIncomplete("Copilot licence assignments");
                assignments = new List<SeatAssignmentRow>();
            }

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
                "Cowork agent lookup", cancellationToken) ?? new List<IntValueRow>();

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
                parameters["@copilotReportPeriodDays"] = summary.DataSources.CopilotUsageReportPeriodDays;
            }

            analysis.Sql["licensedUsers"] = CopilotAdoptionSql.ForDisplay(detailSql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<LicensedUserUsageRow>(detailSql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "licensed user detail", cancellationToken);

            if (rows == null)
            {
                // Every rate, funnel stage, band and segment on the page is computed from this list. An
                // empty one renders as "nobody uses Copilot", which is the most damaging thing this tool
                // could say incorrectly.
                summary.MarkFiguresIncomplete("licensed user detail");
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
                "Copilot usage by app", cancellationToken);

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
                "Cowork agent lookup", cancellationToken) ?? new List<IntValueRow>();

            var sql = CopilotAdoptionSql.WeeklyAdoptionTrendSql(seatIds, coworkAgentIds.Select(r => r.Value));
            var parameters = new Dictionary<string, object> { { "@trendFrom", trendStart } };
            analysis.Sql["weeklyTrend"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<NamedWeekRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                analysis.Summary.Warnings,
                "weekly adoption trend", cancellationToken);

            if (rows == null) return;

            var weekSpine = WeekSpine(trendStart, MondayOf(DateTime.UtcNow.Date));

            var series = rows
                .GroupBy(r => r.SeriesName)
                .OrderBy(g => g.Key)
                .Select(g => new AdoptionSeries
                {
                    Name = g.Key,
                    Points = FillWeeks(weekSpine, g.ToList()),
                })
                .ToList();

            // Headcounts and interaction volumes come from one pass over the same rows, but they cannot
            // share an axis - a few hundred users plotted against tens of thousands of interactions
            // flattens the user line onto zero. Split into two charts.
            analysis.Summary.WeeklyTrend = series
                .Where(s => !CopilotAdoptionSql.VolumeTrendSeries.Contains(s.Name))
                .ToList();

            analysis.Summary.WeeklyVolumeTrend = series
                .Where(s => CopilotAdoptionSql.VolumeTrendSeries.Contains(s.Name))
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

            // Deliberately not gated on seatIds.Count: the "should we buy Copilot?" case has no seat SKUs at
            // all, and that is exactly when this count matters most. UnlicensedActiveUsersSql renders an
            // empty id list as IN (-1), so every active user correctly counts as unlicensed. Requiring seats
            // here reported a flat zero while the candidate list below simultaneously showed real users.
            if (summary.DataSources.AuditAvailable)
            {
                var unlicensedSql = CopilotAdoptionSql.UnlicensedActiveUsersSql(seatIds);
                analysis.Sql["unlicensedActiveUsers"] = CopilotAdoptionSql.ForDisplay(
                    unlicensedSql, new Dictionary<string, object> { { "@from", windowStart } });

                summary.UnlicensedActiveUsers = await SafeScalarAsync(
                    unlicensedSql,
                    summary.Warnings,
                    "unlicensed Copilot users",
                    // Published as a headline KPI, and zero is a meaningful answer here - so a failure that
                    // reads as zero is indistinguishable from "nobody uses Copilot without a licence".
                    () => summary.MarkFiguresIncomplete("unlicensed Copilot users"),
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
            if (includeM365)
            {
                // The Microsoft 365 figures are read across the whole window, not from one report date.
                // Date-only column, so the bound is the window's first calendar day rather than the
                // timestamp - otherwise the earliest day of the window is silently dropped.
                parameters["@m365From"] = windowStart.Date;
                parameters["@m365ReportDate"] = summary.DataSources.M365UsageReportDate.Value;
            }

            analysis.Sql["licenceOpportunities"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<UnlicensedUserSignalRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                summary.Warnings,
                "licence opportunities", cancellationToken);

            if (rows == null)
            {
                // RecommendedForLicence is published as a headline KPI and the opportunity list drives a
                // whole tab and a CSV export. This is one of the queries that times out at the median on a
                // large tenant, so a silent empty list here reads as "nobody is worth a licence".
                summary.MarkFiguresIncomplete("licence opportunities");
                return;
            }

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

            // Every rate below divides by the users actually scored, NOT by the seat count. Those are
            // the same number unless the detail query hit its row cap - and when it does, dividing by
            // the seat count is arithmetically wrong rather than merely approximate: a 200,000-seat
            // tenant scored 50,000 deep could never report adoption above 25%, however healthy it
            // really was, and the funnel would open with a 75% drop that is pure measurement artefact.
            summary.ScoredUsers = users.Count;
            var denominator = summary.ScoredUsers;

            if (summary.ScoredUsers > 0 && summary.ScoredUsers < summary.LicensedUsers)
            {
                summary.Warnings.Add(
                    $"This tenant holds {summary.LicensedUsers:N0} Copilot licences, but only {summary.ScoredUsers:N0} "
                    + "users could be analysed in one pass. Every rate and breakdown below describes those "
                    + $"{summary.ScoredUsers:N0} users, not the whole tenant - they are not tenant-wide figures "
                    + "and must not be quoted as such.");
            }

            summary.ActiveUsers = users.Count(u => u.Band > AdoptionBand.Dormant);
            summary.NeverUsedUsers = users.Count(u => u.Band == AdoptionBand.NeverUsed);
            summary.DormantUsers = users.Count(u => u.Band == AdoptionBand.Dormant);
            summary.HabitualUsers = users.Count(u => CopilotAdoptionScoring.IsHabitual(u.Band));
            summary.ReclaimableSeats = summary.NeverUsedUsers + summary.DormantUsers;
            summary.TotalInteractions = users.Sum(u => u.Interactions);

            summary.AdoptionRatePct = CopilotAdoptionScoring.Percentage(summary.ActiveUsers, denominator);
            summary.HabitRatePct = CopilotAdoptionScoring.Percentage(summary.HabitualUsers, denominator);
            summary.AverageAdoptionScore = users.Count == 0
                ? 0
                : Math.Round(users.Average(u => u.AdoptionScore), 1, MidpointRounding.AwayFromZero);
            summary.MedianAdoptionScore = CopilotAdoptionScoring.Median(users.Select(u => u.AdoptionScore));

            summary.CoworkUsers = users.Count(u => u.UsedCowork);
            summary.CoworkInteractions = users.Sum(u => u.CoworkInteractions);
            summary.CoworkAdoptionPct = CopilotAdoptionScoring.Percentage(summary.CoworkUsers, denominator);
            // Only claim a Cowork adoption rate when Cowork was actually seen. On a tenant that has not
            // been enabled for it, "0% Cowork adoption" reads as a failure rather than as "not available".
            summary.CoworkDetected = summary.CoworkInteractions > 0;

            summary.Funnel = BuildFunnel(summary, users);
            summary.BandBreakdown = BuildBandBreakdown(users);
            summary.HabitBuckets = BuildHabitBuckets(users.Select(u => (double)u.ActiveDays));
            summary.ActionPlan = BuildActionPlan(users);
            summary.Concentration = CopilotAdoptionScoring.Concentration(
                users.Where(CopilotAdoptionScoring.IsActive).Select(u => u.Interactions));
            summary.ScoreProfiles = BuildScoreProfiles(users);
            summary.AdoptionByDepartment = BuildSegments(users, u => u.Department, "(no department)");
            summary.AdoptionByCountry = BuildSegments(users, u => u.Country, "(no country)");
            summary.IntensityByDepartment = BuildIntensity(users, u => u.Department, "(no department)");

            FinaliseAgents(analysis);
            FinaliseUnlicensed(analysis);
            summary.CombinedByDepartment = BuildCombinedSegments(analysis);

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
        ///
        /// The first stage is the scored population rather than the seat count. They are the same
        /// unless the detail query hit its row cap, and if it did, opening the funnel with the seat
        /// count would draw a huge drop between stage one and stage two that is entirely an artefact
        /// of how many users were read - the most misleading thing this chart could possibly say.
        /// </summary>
        private static List<AdoptionCategory> BuildFunnel(
            CopilotAdoptionSummary summary,
            IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            var everUsed = users.Count(u => u.Band != AdoptionBand.NeverUsed);
            var champions = users.Count(u => u.Band == AdoptionBand.Champion);

            return new List<AdoptionCategory>
            {
                new AdoptionCategory { Label = "Licensed", Value = summary.ScoredUsers },
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
        /// The shape of engagement for the typical active user, next to the shape for the Champions.
        ///
        /// The comparison is what makes it useful: the gap between the two profiles says which of the
        /// three behaviours an enablement programme should actually target here. A tenant whose
        /// average user matches its Champions on frequency but not breadth has a completely different
        /// problem from one where the gap is depth, and the overall score is identical in both cases.
        ///
        /// Averaged over active users only - an idle seat contributes zero to all three components,
        /// which drags the whole profile towards the origin and says nothing about shape.
        /// </summary>
        private static List<AdoptionScoreProfile> BuildScoreProfiles(
            IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            var profiles = new List<AdoptionScoreProfile>();

            var active = users.Where(CopilotAdoptionScoring.IsActive).ToList();
            if (active.Count == 0) return profiles;

            profiles.Add(Profile("Typical active user", active));

            var champions = active.Where(u => u.Band == AdoptionBand.Champion).ToList();
            if (champions.Count > 0)
            {
                profiles.Add(Profile("Your Champions", champions));
            }

            return profiles;
        }

        private static AdoptionScoreProfile Profile(string label, IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            return new AdoptionScoreProfile
            {
                Label = label,
                Users = users.Count,
                FrequencyScore = Math.Round(users.Average(u => u.FrequencyScore), 1, MidpointRounding.AwayFromZero),
                DepthScore = Math.Round(users.Average(u => u.DepthScore), 1, MidpointRounding.AwayFromZero),
                BreadthScore = Math.Round(users.Average(u => u.BreadthScore), 1, MidpointRounding.AwayFromZero),
            };
        }

        /// <summary>
        /// Rolls the scored agents up into the estate headline figures.
        ///
        /// "Active" means used inside the reporting period; the inventory itself covers the longer
        /// history window, because an agent nobody has touched for six months is exactly what an
        /// inventory review is looking for and would be invisible in a 28-day count.
        /// </summary>
        private void FinaliseAgents(CopilotAdoptionAnalysis analysis)
        {
            var summary = analysis.Summary;
            var agents = analysis.Agents ?? new List<AgentUsageRow>();
            var estate = summary.Agents;

            estate.KnownAgents = agents.Count;
            estate.CustomAgents = agents.Count(a => a.IsCustomAgent);

            var activeInWindow = agents
                .Where(a => a.LastUsedUtc.HasValue && a.LastUsedUtc.Value >= summary.FromUtc)
                .ToList();

            estate.ActiveAgents = activeInWindow.Count;
            // Window-scoped, to match the window-scoped user count it is divided by. Using the
            // history-wide interaction total here inflated the KPI by the ratio of the two windows.
            estate.AgentInteractions = activeInWindow.Sum(a => a.WindowInteractions);

            // Agent users cannot be summed across agents without double-counting anyone who uses two,
            // so the figure comes from the per-user rows instead: licensed users carry AgentsUsed, and
            // the unlicensed rows carry the same. Reported as a floor when either set was capped.
            var licensedAgentUsers = (analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
                .Count(u => u.AgentsUsed > 0);
            var unlicensedAgentUsers = (analysis.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>())
                .Count(u => u.AgentsUsed > 0);

            estate.LicensedAgentUsers = licensedAgentUsers;
            estate.AgentUsers = licensedAgentUsers + unlicensedAgentUsers;
            estate.InteractionsPerAgentUser = estate.AgentUsers == 0
                ? 0
                : Math.Round(estate.AgentInteractions / (double)estate.AgentUsers, 1, MidpointRounding.AwayFromZero);

            estate.MostPopularAgent = agents
                .OrderByDescending(a => a.Users)
                .ThenByDescending(a => a.Interactions)
                .Select(a => a.Name)
                .FirstOrDefault();

            // Versatility is breadth of surface, not volume - an agent used everywhere by a few people
            // is doing a broader job than one used constantly in a single host.
            estate.MostVersatileAgent = agents
                .OrderByDescending(a => a.AppsUsed)
                .ThenByDescending(a => a.Users)
                .Select(a => a.Name)
                .FirstOrDefault();

            estate.HealthBreakdown = CopilotAdoptionScoring.AllAgentHealthStates
                .Select(health => new AdoptionCategory
                {
                    Label = CopilotAdoptionScoring.AgentHealthDisplayName(health),
                    Value = agents.Count(a => a.Health == health),
                })
                .ToList();

            estate.UsageByAgent = agents
                .Where(a => a.Interactions > 0)
                .OrderByDescending(a => a.Interactions)
                .Take(_options.TopSegments)
                .Select(a => new AdoptionCategory { Label = a.Name, Value = a.Interactions })
                .ToList();

            estate.Agents = agents;
        }

        /// <summary>
        /// Rolls the unlicensed rows up, using exactly the same habit rules as the licensed population
        /// so the two distributions can be read against each other.
        /// </summary>
        private void FinaliseUnlicensed(CopilotAdoptionAnalysis analysis)
        {
            var summary = analysis.Summary;
            var rows = analysis.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>();
            var unlicensed = summary.Unlicensed;

            unlicensed.ActiveUsers = rows.Count;
            unlicensed.Interactions = rows.Sum(r => r.Interactions);
            unlicensed.AgentUsers = rows.Count(r => r.AgentsUsed > 0);
            unlicensed.InteractionsPerUserPerMonth = rows.Count == 0
                ? 0
                : Math.Round(
                    CopilotAdoptionScoring.NormaliseToMonth(
                        unlicensed.Interactions / (double)rows.Count, _options.WindowDays, _options),
                    1,
                    MidpointRounding.AwayFromZero);

            unlicensed.HabitBuckets = BuildHabitBuckets(rows.Select(r => (double)r.ActiveDays));

            unlicensed.UsageByDepartment = rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Department) ? "(no department)" : r.Department.Trim())
                .Select(g => new AdoptionCategory { Label = g.Key, Value = g.Sum(r => (double)r.Interactions) })
                .OrderByDescending(c => c.Value)
                .Take(_options.TopSegments)
                .ToList();

            // The headline "unlicensed active users" is its own uncapped COUNT query, so only fall back
            // to the row count when that query did not run - never overwrite a true total with a capped one.
            if (summary.UnlicensedActiveUsers == 0 && rows.Count > 0)
            {
                summary.UnlicensedActiveUsers = rows.Count;
            }
        }

        /// <summary>
        /// Licensed and unlicensed Copilot use per department, side by side.
        ///
        /// This is the view that turns two separate reports into a decision: a department with idle
        /// seats <i>and</i> heavy unlicensed Chat use is not an adoption problem, it is a
        /// seat-allocation problem, and it can usually be fixed at no cost.
        /// </summary>
        private List<AdoptionCombinedSegmentRow> BuildCombinedSegments(CopilotAdoptionAnalysis analysis)
        {
            var licensed = (analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
                .GroupBy(u => string.IsNullOrWhiteSpace(u.Department) ? "(no department)" : u.Department.Trim())
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var unlicensed = (analysis.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>())
                .GroupBy(u => string.IsNullOrWhiteSpace(u.Department) ? "(no department)" : u.Department.Trim())
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var segments = licensed.Keys
                .Concat(unlicensed.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<AdoptionCombinedSegmentRow>();

            foreach (var segment in segments)
            {
                List<LicensedUserAdoptionRow> seats;
                licensed.TryGetValue(segment, out seats);
                seats = seats ?? new List<LicensedUserAdoptionRow>();

                List<UnlicensedUsageQueryRow> chat;
                unlicensed.TryGetValue(segment, out chat);
                chat = chat ?? new List<UnlicensedUsageQueryRow>();

                // A department needs enough of one population or the other to be worth a row. Without
                // this a single unlicensed Chat user in a department with no seats appears alongside
                // real departments and reads as a finding.
                if (seats.Count < _options.MinSeatsPerSegment && chat.Count < _options.MinSeatsPerSegment)
                {
                    continue;
                }

                rows.Add(new AdoptionCombinedSegmentRow
                {
                    Segment = segment,
                    LicensedUsers = seats.Count,
                    LicensedActiveUsers = seats.Count(CopilotAdoptionScoring.IsActive),
                    // Per seat held, not per active seat: this column exists to be compared with the
                    // unlicensed one, and an idle seat is the whole point of the comparison.
                    InteractionsPerLicensedUser = PerUserPerMonth(seats.Sum(u => (double)u.Interactions), seats.Count),
                    LicensedAgentUserPct = CopilotAdoptionScoring.Percentage(
                        seats.Count(u => u.AgentsUsed > 0), seats.Count),
                    UnlicensedActiveUsers = chat.Count,
                    InteractionsPerUnlicensedUser = PerUserPerMonth(chat.Sum(u => (double)u.Interactions), chat.Count),
                    UnlicensedAgentUserPct = CopilotAdoptionScoring.Percentage(
                        chat.Count(u => u.AgentsUsed > 0), chat.Count),
                });
            }

            return rows
                .OrderByDescending(r => r.LicensedUsers)
                .ThenByDescending(r => r.UnlicensedActiveUsers)
                .Take(_options.TopSegments)
                .ToList();
        }

        /// <summary>Interactions per user, normalised to a month so the column does not change meaning with the period.</summary>
        private double PerUserPerMonth(double interactions, int users)
        {
            if (users <= 0) return 0;

            return Math.Round(
                CopilotAdoptionScoring.NormaliseToMonth(interactions / users, _options.WindowDays, _options),
                1,
                MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The habit strip: active licensed users bucketed by how many days a month they actually open
        /// Copilot.
        ///
        /// Reported as a share of <i>active</i> users rather than of all seats, because the question it
        /// answers is "of the people who use it, how many have a habit?" - mixing in the never-used
        /// seats would answer a question the reclaim figures already answer better.
        /// </summary>
        private List<AdoptionHabitBucket> BuildHabitBuckets(IEnumerable<double> activeDaysPerUser)
        {
            var bucketed = (activeDaysPerUser ?? Enumerable.Empty<double>())
                .Select(days => CopilotAdoptionScoring.HabitBucketFor(
                    CopilotAdoptionScoring.NormalisedActiveDaysPerMonth(days, _options.WindowDays, _options),
                    _options))
                .Where(b => b != null)
                .ToList();

            return CopilotAdoptionScoring.AllHabitBuckets
                .Select(bucket =>
                {
                    var count = bucketed.Count(b => b == bucket);
                    return new AdoptionHabitBucket
                    {
                        Label = bucket,
                        RangeLabel = CopilotAdoptionScoring.HabitBucketRangeLabel(bucket, _options),
                        Users = count,
                        SharePct = CopilotAdoptionScoring.Percentage(count, bucketed.Count),
                    };
                })
                .ToList();
        }

        /// <summary>
        /// How many licensed users need each recommended action, biggest job first.
        ///
        /// This is the same information the per-user list carries, aggregated - and it is the form an
        /// admin can actually plan from. It also lets the list itself stop repeating an identical
        /// paragraph on every row of a band.
        ///
        /// The shares are of the users actually scored, which is normally every licensed user but is
        /// capped by <see cref="CopilotAdoptionOptions.MaxLicensedUsersScored"/>. When that cap bites
        /// the analysis already carries an explicit warning, so the denominator is stated rather than
        /// silently different from the licence count.
        /// </summary>
        private List<AdoptionActionSummary> BuildActionPlan(IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            return CopilotAdoptionScoring.AllActionCodes
                .Select(code =>
                {
                    var count = users.Count(u => u.RecommendedActionCode == code);
                    return new AdoptionActionSummary
                    {
                        Code = code,
                        Label = CopilotAdoptionScoring.ActionLabel(code),
                        // Passed the real options, not the defaults: the descriptions quote thresholds,
                        // and a tuned deployment must not be shown the shipped numbers.
                        Description = CopilotAdoptionScoring.ActionDescription(code, _options),
                        Users = count,
                        SharePct = CopilotAdoptionScoring.Percentage(count, users.Count),
                    };
                })
                .Where(a => a.Users > 0)
                .OrderByDescending(a => a.Users)
                .ToList();
        }

        /// <summary>
        /// Frequency vs intensity per segment: how many days a month its active users open Copilot,
        /// against how many interactions they run on each of those days.
        ///
        /// Two departments on the same adoption percentage sit in completely different places on this
        /// plot, and the intervention differs accordingly - a high-frequency/low-intensity department
        /// needs richer scenarios, a low-frequency/high-intensity one needs a reason to come back
        /// tomorrow. Only active users are averaged, so the never-used seats (already counted in the
        /// reclaim figures) do not drag every department towards the origin.
        /// </summary>
        private List<AdoptionIntensityPoint> BuildIntensity(
            IEnumerable<LicensedUserAdoptionRow> users,
            Func<LicensedUserAdoptionRow, string> selector,
            string emptyLabel)
        {
            return users
                .GroupBy(u => string.IsNullOrWhiteSpace(selector(u)) ? emptyLabel : selector(u).Trim())
                .Where(g => g.Count() >= _options.MinSeatsPerSegment)
                .Select(g =>
                {
                    var active = g.Where(CopilotAdoptionScoring.IsActive).ToList();
                    var activeDayTotal = active.Sum(u => u.ActiveDays);

                    return new AdoptionIntensityPoint
                    {
                        Segment = g.Key,
                        LicensedUsers = g.Count(),
                        ActiveUsers = active.Count,
                        ActiveDaysPerUser = active.Count == 0
                            ? 0
                            : Math.Round(
                                CopilotAdoptionScoring.NormaliseToMonth(
                                    activeDayTotal / (double)active.Count, _options.WindowDays, _options),
                                1,
                                MidpointRounding.AwayFromZero),
                        ActionsPerActiveDay = active.Count == 0
                            ? 0
                            : Math.Round(
                                active.Sum(u => (double)u.Interactions) / Math.Max(1, activeDayTotal),
                                1,
                                MidpointRounding.AwayFromZero),
                        ActiveUserAverageScore = active.Count == 0
                            ? 0
                            : Math.Round(active.Average(u => u.AdoptionScore), 1, MidpointRounding.AwayFromZero),
                    };
                })
                .Where(p => p.ActiveUsers > 0)
                .OrderByDescending(p => p.LicensedUsers)
                .Take(_options.TopSegments)
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
            using (var db = _contextFactory.Create())
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
            string description,
            CancellationToken cancellationToken)
        {
            try
            {
                return await query();
            }
            catch (Exception ex)
            {
                // Cancellation is decided by the TOKEN, not by the exception type. A token-triggered
                // abort surfaces from EF6 / SqlClient as any of TaskCanceledException, SqlException
                // ("Operation cancelled by user") or InvalidOperationException, so type-matching here
                // misses cases. When the caller has cancelled, the analysis must NOT complete: it is
                // cached as a shared Task, so degrading this to a warning would let an aborted run
                // finish as a "successful" empty result and be served to every other caller until the
                // entry expired. Faulting the task is what makes the cache evict it.
                if (cancellationToken.IsCancellationRequested)
                {
                    if (ex is OperationCanceledException) throw;

                    throw new OperationCanceledException(
                        $"Copilot adoption analysis was cancelled while loading {description}.",
                        ex,
                        cancellationToken);
                }

                // Not cancelled, so this is a genuine query failure - INCLUDING a TaskCanceledException
                // nobody asked for. EF6 / SqlClient surface an async command timeout either as a
                // SqlException ("The wait operation timed out") or as a TaskCanceledException,
                // unpredictably. Rethrowing the latter faulted the whole analysis and returned a 500, so
                // the same timeout produced a degraded page on one run and an error on the next - see
                // issue #360.
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
            return await SafeScalarAsync(sql, warnings, description, null, cancellationToken, parameters);
        }

        /// <summary>
        /// As <see cref="SafeScalarAsync(string, List{string}, string, CancellationToken, SqlParameter[])"/>,
        /// but tells the caller when the query FAILED rather than legitimately returning zero.
        /// </summary>
        /// <remarks>
        /// The distinction matters wherever zero is a meaningful answer. Collapsing a failure to zero is how
        /// a timed-out query becomes an authoritative-looking headline figure - the defect issue #360 is
        /// about. <paramref name="onFailure"/> lets the caller mark the affected figures incomplete.
        /// </remarks>
        private async Task<int> SafeScalarAsync(
            string sql,
            List<string> warnings,
            string description,
            Action onFailure,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<int?>(sql, cancellationToken, parameters),
                warnings,
                description,
                cancellationToken);

            if (rows == null)
            {
                onFailure?.Invoke();
                return 0;
            }

            return rows.FirstOrDefault() ?? 0;
        }

        private async Task<DateTime?> SafeDateAsync(
            string sql,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            return await SafeDateAsync(sql, warnings, description, null, cancellationToken, parameters);
        }

        /// <summary>
        /// As <see cref="SafeDateAsync(string, List{string}, string, CancellationToken, SqlParameter[])"/>,
        /// but tells the caller when the query FAILED rather than legitimately finding no snapshot.
        /// </summary>
        /// <remarks>
        /// These dates drive availability decisions: a null reads as "this tenant has no usage report",
        /// which silently removes a whole data source from the analysis. That is the same
        /// failure-looks-like-absence defect as issue #360, one level up.
        /// </remarks>
        private async Task<DateTime?> SafeDateAsync(
            string sql,
            List<string> warnings,
            string description,
            Action onFailure,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<DateTime?>(sql, cancellationToken, parameters),
                warnings,
                description,
                cancellationToken);

            if (rows == null)
            {
                onFailure?.Invoke();
                return null;
            }

            return rows.FirstOrDefault();
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
