using Common.Entities.Copilot;
using Common.Entities.AgentCosts;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

        /// <summary>
        /// The scheduled period publisher is explicitly off the interactive request path, so it must not
        /// inherit the request-path budget. A closed-period publish deletes and rebuilds up to one row per
        /// Copilot seat and can legitimately run longer than an HTTP request on a 200k-seat tenant.
        /// </summary>
        public const int PublishCommandTimeoutSecs = 0;

        /// <summary>
        /// How many analysis steps may query the database at once.
        /// </summary>
        /// <remarks>
        /// The steps are independent, so overlapping them makes the page cost the slowest step rather than
        /// the sum of all of them. That is worth much less than it sounds, because they all queue on the
        /// same database - overlapping work does not create capacity. Measured on a synthetic
        /// customer-shaped bench over a 28-day window, all else equal:
        /// <list type="table">
        /// <item><description>1 (sequential): 14,674ms total, slowest step 5,483ms</description></item>
        /// <item><description>2:              12,945ms total (-12%), slowest step 11,267ms</description></item>
        /// <item><description>4:              12,470ms total (-15%), slowest step 12,099ms</description></item>
        /// </list>
        /// Both the gain and the per-step inflation have saturated by 2: going to 4 buys another 3% for
        /// twice the concurrent load on a database this report shares with the importer and every other
        /// page. Hence 2.
        /// <para>
        /// The inflation is the reason this is not set higher. Overlapping makes each individual step
        /// about twice as slow, which moves every step twice as close to
        /// <see cref="QueryTimeoutSecs"/> - and a step that hits that timeout does not fail the page, it
        /// degrades to a warning and silently drops a whole section. Trading a complete report for a
        /// slightly faster incomplete one is the wrong way round for a tool used to justify licence spend.
        /// </para>
        /// <para>
        /// Note the bench cannot see the whole picture: its data is entirely in the buffer pool (zero
        /// physical reads), so it measures the CPU-bound worst case for overlapping. A tenant whose fact
        /// tables do not fit in memory has I/O waits to overlap and should do better than these figures,
        /// which is why this is a constructor parameter rather than a hard-coded constant - it can be
        /// raised for a specific deployment once measured there.
        /// </para>
        /// </remarks>
        public const int MaxConcurrentSteps = 2;

        /// <summary>How far back the weekly trend chart looks. Long enough to show whether an enablement push worked.</summary>
        public const int TrendMonths = 6;

        private readonly CopilotAdoptionOptions _options;
        private readonly IAnalyticsDbContextFactory _contextFactory;
        private readonly int _maxConcurrentSteps;
        private readonly ICopilotAdoptionRunTelemetry _telemetry;

        public CopilotAdoptionService(
            CopilotAdoptionOptions options = null,
            IAnalyticsDbContextFactory contextFactory = null,
            int? maxConcurrentSteps = null,
            ICopilotAdoptionRunTelemetry telemetry = null)
        {
            _options = options ?? CopilotAdoptionOptions.Default;
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _maxConcurrentSteps = Math.Max(1, maxConcurrentSteps ?? MaxConcurrentSteps);
            _telemetry = telemetry ?? NullCopilotAdoptionRunTelemetry.Instance;
        }

        public CopilotAdoptionOptions Options => _options;


        /// <summary>Publishes threshold-independent raw facts for a closed period. This is deliberately not called by the interactive analysis path.</summary>
        public async Task<CopilotAdoptionPeriodPublishResult> PublishClosedPeriodAsync(
            DateTime periodEndUtc,
            IEnumerable<int> seatLicenceTypeIdOverride = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var periodEnd = periodEndUtc.Date;
            var latestClosedPeriodEnd = DateTime.UtcNow.Date.AddDays(-1);
            var periodDays = Math.Max(1, _options.WindowDays);
            var from = periodEnd.AddDays(-(periodDays - 1));
            var historyFrom = periodEnd.AddDays(-(Math.Max(periodDays, _options.HistoryDays) - 1));
            var toExclusive = periodEnd.AddDays(1);
            var settled = periodEnd.AddDays(-Math.Max(0, _options.UsageReportLagDays));

            cancellationToken.ThrowIfCancellationRequested();

            var existing = await GetPublishedPeriodRunAsync(periodEnd, periodDays, cancellationToken);
            if (existing != null)
            {
                return CopilotAdoptionPeriodPublishResult.AlreadyPublished(existing);
            }

            if (periodEnd >= DateTime.UtcNow.Date)
            {
                return CopilotAdoptionPeriodPublishResult.NotDue(
                    "Only closed periods can be published; the current incomplete period must never be persisted.");
            }
            if (periodEnd < latestClosedPeriodEnd)
            {
                return CopilotAdoptionPeriodPublishResult.NotDue(
                    "Older Copilot Adoption periods are not backfilled from current licence or user metadata. Publish periods as they close; until seat/metadata history exists, missed historical periods remain unknown.");
            }

            var overlaps = await ScalarAsync(
                CopilotAdoptionSql.OverlappingPublishedPeriodSql,
                cancellationToken,
                new SqlParameter("@periodEnd", periodEnd),
                new SqlParameter("@periodDays", periodDays),
                new SqlParameter("@from", from));
            if (overlaps > 0)
            {
                return CopilotAdoptionPeriodPublishResult.NotDue(
                    "A published Copilot Adoption period with the same length already overlaps this window. The next publish is not due yet.");
            }

            var licenceTypes = await QueryAsync<LicenceTypeRow>(CopilotAdoptionSql.LicenceTypesSql, cancellationToken);
            var seatIds = CopilotLicenceClassifier.ResolveSeatLicenceTypeIds(licenceTypes, seatLicenceTypeIdOverride);

            var backfillPending = await ScalarAsync(
                CopilotAdoptionSql.PendingCopilotBackfillSql,
                cancellationToken) == 1;

            var auditAvailable = await ScalarAsync(
                CopilotAdoptionSql.HasCopilotAuditDataSql,
                cancellationToken,
                new SqlParameter("@from", from),
                new SqlParameter("@toExclusive", toExclusive)) == 1
                && !backfillPending;

            var reportDate = await DateAsync(
                CopilotAdoptionSql.LatestCopilotReportDateSql,
                cancellationToken,
                new SqlParameter("@settled", settled));

            var reportObfuscated = await ScalarAsync(
                CopilotAdoptionSql.CopilotReportObfuscatedSql,
                cancellationToken) == 1;

            var includeReport = reportDate.HasValue && !reportObfuscated;
            var reportPeriodDays = 0;
            if (includeReport)
            {
                reportPeriodDays = await ScalarAsync(
                    CopilotAdoptionSql.LatestCopilotReportPeriodSql,
                    cancellationToken,
                    new SqlParameter("@copilotReportDate", reportDate.Value),
                    new SqlParameter("@windowDays", periodDays));
            }

            var coworkAgentIds = await QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken);
            var publishSql = CopilotAdoptionSql.PublishPeriodFactsSql(seatIds, coworkAgentIds.Select(r => r.Value), includeReport);

            await ExecuteAsync(
                publishSql,
                cancellationToken,
                PublishCommandTimeoutSecs,
                new SqlParameter("@periodEnd", periodEnd),
                new SqlParameter("@periodDays", periodDays),
                new SqlParameter("@from", from),
                new SqlParameter("@historyFrom", historyFrom),
                new SqlParameter("@toExclusive", toExclusive),
                new SqlParameter("@auditAvailable", auditAvailable),
                new SqlParameter("@includeCopilotReport", includeReport),
                new SqlParameter("@reportObfuscated", reportObfuscated),
                new SqlParameter("@licensedUsers", seatIds.Count == 0 ? 0 : await CountLicensedUsersAsync(seatIds, cancellationToken)),
                new SqlParameter("@optionsHash", OptionsHash(_options)),
                new SqlParameter("@copilotReportDate", (object)reportDate ?? DBNull.Value),
                new SqlParameter("@copilotReportPeriodDays", reportPeriodDays));

            return CopilotAdoptionPeriodPublishResult.Published(
                await GetPublishedPeriodRunAsync(periodEnd, periodDays, cancellationToken));
        }

        /// <summary>Reads stored facts and recomputes all bands, actions and rates under the current options.</summary>
        public async Task<CopilotAdoptionPublishedPeriod> ReadPublishedPeriodAsync(
            DateTime periodEndUtc,
            int periodDays,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var periodEnd = periodEndUtc.Date;
            var run = await GetPublishedPeriodRunAsync(periodEnd, periodDays, cancellationToken);
            if (run == null) return null;

            var rows = await QueryAsync<CopilotAdoptionStoredPeriodFactRow>(
                CopilotAdoptionSql.PublishedPeriodFactsSql,
                cancellationToken,
                new SqlParameter("@periodEnd", periodEnd),
                new SqlParameter("@periodDays", periodDays));

            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.GeneratedUtc = DateTime.UtcNow;
            summary.WindowDays = periodDays;
            summary.FromUtc = periodEnd.AddDays(-(Math.Max(1, periodDays) - 1));
            summary.ToUtc = periodEnd;
            var periodOptions = OptionsForStoredPeriod(_options, periodDays);
            summary.Options = periodOptions;
            summary.LicensedUsers = run.LicensedUsers;
            summary.DataSources.UserMetadataAvailable = true;
            summary.DataSources.AuditAvailable = run.AuditAvailable;
            summary.DataSources.CopilotUsageReportObfuscated = run.ReportObfuscated;
            summary.DataSources.CopilotUsageReportPeriodDays = run.ReportPeriodDays;
            summary.DataSources.CopilotUsageReportAvailable = rows.Any(r => r.ReportPrompts.HasValue || r.ReportActiveDays.HasValue);

            if (string.Equals(run.CoverageStatus, CopilotAdoptionSql.PeriodCoverageUnverifiable, StringComparison.OrdinalIgnoreCase))
            {
                summary.MarkFiguresIncomplete("period coverage");
                summary.Warnings.Add("This stored period has unverifiable Copilot coverage, so gaps must be shown as unknown rather than read as zero adoption.");
            }

            analysis.LicensedUsers = rows
                .Select(row => CopilotAdoptionScoring.Score(row, summary.FromUtc, periodEnd, run.AuditAvailable, periodOptions))
                .ToList();

            FinaliseSummary(analysis);
            return new CopilotAdoptionPublishedPeriod { Run = run, Analysis = analysis };
        }

        public async Task<CopilotAdoptionPeriodComparisonGate> ComparePublishedPeriodsAsync(
            DateTime leftPeriodEndUtc,
            DateTime rightPeriodEndUtc,
            int periodDays,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var left = await GetPublishedPeriodRunAsync(leftPeriodEndUtc.Date, periodDays, cancellationToken);
            var right = await GetPublishedPeriodRunAsync(rightPeriodEndUtc.Date, periodDays, cancellationToken);
            var comparable = left != null && right != null
                && string.Equals(left.OptionsHash, right.OptionsHash, StringComparison.OrdinalIgnoreCase);

            return new CopilotAdoptionPeriodComparisonGate
            {
                Left = left,
                Right = right,
                OptionsComparable = comparable,
                Message = comparable
                    ? "The two stored periods were published with the same options hash and may be compared directly."
                    : "The stored periods have different options hashes (or one period is missing). Restate both from raw facts under one option set before comparing them."
            };
        }


        /// <summary>
        /// Adds closed-period movement and customer targets to an already-built analysis. The live
        /// analysis may cover the current partial day; movement deliberately uses only the latest stored
        /// closed period and its selected stored comparator.
        /// </summary>
        public async Task EnrichProgressAsync(
            CopilotAdoptionAnalysis analysis,
            string comparisonMode = CopilotAdoptionComparisonModes.PreviousPeriod,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            var summary = analysis.Summary;
            var periodDays = Math.Max(1, summary.WindowDays > 0 ? summary.WindowDays : _options.WindowDays);
            var mode = NormaliseComparisonMode(comparisonMode);

            var latestRun = await GetLatestPublishedPeriodRunAsync(periodDays, cancellationToken).ConfigureAwait(false);
            if (latestRun == null)
            {
                summary.PeriodMovement = new CopilotAdoptionPeriodMovement
                {
                    Mode = mode,
                    PeriodDays = periodDays,
                    Available = false,
                    Comparable = false,
                    Message = "No closed Copilot Adoption period has been published yet, so period movement is not shown."
                };
                summary.Targets = await ListTargetsAsync(null, null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var comparisonEnd = ComparisonPeriodEnd(latestRun.PeriodEnd, periodDays, mode);
            var current = await ReadPublishedPeriodAsync(latestRun.PeriodEnd, periodDays, cancellationToken).ConfigureAwait(false);
            var prior = await ReadPublishedPeriodAsync(comparisonEnd, periodDays, cancellationToken).ConfigureAwait(false);
            summary.PeriodMovement = BuildMovement(current, prior, mode);
            summary.Targets = await ListTargetsAsync(current, latestRun, cancellationToken).ConfigureAwait(false);
        }

        public async Task<CopilotAdoptionTarget> CreateTargetAsync(
            CopilotAdoptionCreateTargetRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var metric = NormaliseMetric(request.Metric);
            var scopeType = NormaliseScopeType(request.ScopeType);
            var scopeValue = string.IsNullOrWhiteSpace(request.ScopeValue) ? null : request.ScopeValue.Trim();
            var periodDays = Math.Max(1, request.BaselinePeriodDays ?? _options.WindowDays);
            var run = request.BaselinePeriodEnd.HasValue
                ? await GetPublishedPeriodRunAsync(request.BaselinePeriodEnd.Value.Date, periodDays, cancellationToken).ConfigureAwait(false)
                : await GetLatestPublishedPeriodRunAsync(periodDays, cancellationToken).ConfigureAwait(false);
            if (run == null) throw new InvalidOperationException("A target needs a published closed baseline period before it can be created.");

            var baseline = await ReadPublishedPeriodAsync(run.PeriodEnd, run.PeriodDays, cancellationToken).ConfigureAwait(false);
            var baselineValue = MetricValue(baseline.Analysis.Summary, metric, scopeType, scopeValue);
            if (!baselineValue.HasValue) throw new InvalidOperationException("The selected metric and scope are not available in the baseline period.");

            var targetDate = request.TargetDate.Date;
            if (targetDate <= run.PeriodEnd.Date) throw new InvalidOperationException("The target date must be after the frozen baseline period.");

            var owner = string.IsNullOrWhiteSpace(request.Owner) ? "Unassigned" : request.Owner.Trim();
            var createdBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? owner : request.CreatedBy.Trim();
            var id = await ScalarAsync(
                CopilotAdoptionSql.InsertAdoptionTargetSql,
                cancellationToken,
                new SqlParameter("@metric", metric),
                new SqlParameter("@scopeType", scopeType),
                new SqlParameter("@scopeValue", (object)scopeValue ?? DBNull.Value),
                new SqlParameter("@targetValue", request.TargetValue),
                new SqlParameter("@owner", owner),
                new SqlParameter("@baselinePeriodEnd", run.PeriodEnd.Date),
                new SqlParameter("@baselinePeriodDays", run.PeriodDays),
                new SqlParameter("@baselineValue", baselineValue.Value),
                new SqlParameter("@baselineOptionsHash", run.OptionsHash),
                new SqlParameter("@baselineScoringOptionsHash", ScoringOptionsHash(_options)),
                new SqlParameter("@targetDate", targetDate),
                new SqlParameter("@createdBy", createdBy)).ConfigureAwait(false);

            var targets = await ListTargetsAsync(baseline, run, cancellationToken).ConfigureAwait(false);
            return targets.FirstOrDefault(t => t.Id == id);
        }

        private async Task<CopilotAdoptionPeriodRun> GetLatestPublishedPeriodRunAsync(int periodDays, CancellationToken cancellationToken)
        {
            var rows = await QueryAsync<CopilotAdoptionPeriodRun>(
                CopilotAdoptionSql.LatestPublishedPeriodRunSql,
                cancellationToken,
                new SqlParameter("@periodDays", periodDays)).ConfigureAwait(false);
            return rows.FirstOrDefault();
        }

        private static string NormaliseComparisonMode(string mode)
        {
            return string.Equals(mode, CopilotAdoptionComparisonModes.SamePeriodLastQuarter, StringComparison.OrdinalIgnoreCase)
                ? CopilotAdoptionComparisonModes.SamePeriodLastQuarter
                : CopilotAdoptionComparisonModes.PreviousPeriod;
        }

        private static DateTime ComparisonPeriodEnd(DateTime currentEnd, int periodDays, string mode)
        {
            return string.Equals(mode, CopilotAdoptionComparisonModes.SamePeriodLastQuarter, StringComparison.Ordinal)
                ? currentEnd.Date.AddMonths(-3)
                : currentEnd.Date.AddDays(-Math.Max(1, periodDays));
        }

        public static CopilotAdoptionPeriodMovement BuildMovement(
            CopilotAdoptionPublishedPeriod current,
            CopilotAdoptionPublishedPeriod prior,
            string mode)
        {
            var normalisedMode = NormaliseComparisonMode(mode);
            var result = new CopilotAdoptionPeriodMovement
            {
                Mode = normalisedMode,
                CurrentPeriodEnd = current?.Run?.PeriodEnd,
                PriorPeriodEnd = prior?.Run?.PeriodEnd,
                PeriodDays = current?.Run?.PeriodDays ?? prior?.Run?.PeriodDays ?? 0,
                ComparisonLabel = normalisedMode == CopilotAdoptionComparisonModes.SamePeriodLastQuarter
                    ? "same period last quarter"
                    : "previous closed period",
            };

            if (current == null || prior == null)
            {
                result.Available = false;
                result.Comparable = false;
                result.Message = "The selected comparison period has not been published yet, so movement is not shown.";
                return result;
            }

            result.Available = true;
            if (!string.Equals(current.Run.OptionsHash, prior.Run.OptionsHash, StringComparison.OrdinalIgnoreCase))
            {
                result.Comparable = false;
                result.Message = "Movement is not shown because the two stored periods were published with different options hashes. Showing a delta would confuse a period change with a threshold or fact-shaping change.";
                return result;
            }

            result.Comparable = true;
            result.Message = $"Comparing the closed period ending {current.Run.PeriodEnd:yyyy-MM-dd} with the {result.ComparisonLabel} ending {prior.Run.PeriodEnd:yyyy-MM-dd}. The current partial period is not compared.";
            result.Deltas = BuildMetricDeltas(current.Analysis.Summary, prior.Analysis.Summary);
            return result;
        }

        private async Task<List<CopilotAdoptionTarget>> ListTargetsAsync(
            CopilotAdoptionPublishedPeriod current,
            CopilotAdoptionPeriodRun currentRun,
            CancellationToken cancellationToken)
        {
            List<CopilotAdoptionTarget> targets;
            try
            {
                targets = await QueryAsync<CopilotAdoptionTarget>(CopilotAdoptionSql.AdoptionTargetsSql, cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex) when (ex.Number == 208)
            {
                return new List<CopilotAdoptionTarget>();
            }

            var currentHash = ScoringOptionsHash(_options);
            foreach (var target in targets)
            {
                target.Label = MetricLabel(target.Metric);
                if (current == null || currentRun == null)
                {
                    target.Comparable = false;
                    target.Message = "No closed current period has been published yet.";
                    continue;
                }

                if (!string.Equals(target.BaselineOptionsHash, currentRun.OptionsHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(target.BaselineScoringOptionsHash, currentHash, StringComparison.OrdinalIgnoreCase))
                {
                    target.Comparable = false;
                    target.Message = "This target is not comparable under the current Copilot Adoption options; its frozen baseline is preserved rather than silently moved.";
                    continue;
                }

                var value = MetricValue(current.Analysis.Summary, target.Metric, target.ScopeType, target.ScopeValue);
                target.CurrentValue = value;
                target.Comparable = value.HasValue;
                target.Message = value.HasValue
                    ? $"Owned by {target.Owner}. Baseline is frozen at {target.BaselineValue:N1} for {target.BaselinePeriodEnd:yyyy-MM-dd}."
                    : "The target scope is not present in the current closed period.";
                target.ProgressPct = value.HasValue ? ProgressPct(target.BaselineValue, target.TargetValue, value.Value) : (double?)null;
            }

            return targets;
        }

        private static double ProgressPct(double baseline, double target, double current)
        {
            var distance = target - baseline;
            if (Math.Abs(distance) < 0.0001d) return current >= target ? 100d : 0d;
            return Math.Round((current - baseline) / distance * 100d, 1, MidpointRounding.AwayFromZero);
        }

        public static string ScoringOptionsHash(CopilotAdoptionOptions options)
        {
            var json = JsonConvert.SerializeObject(options ?? CopilotAdoptionOptions.Default, Formatting.None);
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static List<CopilotAdoptionMetricDelta> BuildMetricDeltas(CopilotAdoptionSummary current, CopilotAdoptionSummary prior)
        {
            var deltas = new List<CopilotAdoptionMetricDelta>();
            foreach (var metric in HeadlineMetrics())
            {
                var now = MetricValue(current, metric, "tenant", null);
                var before = MetricValue(prior, metric, "tenant", null);
                if (!now.HasValue || !before.HasValue) continue;
                deltas.Add(new CopilotAdoptionMetricDelta
                {
                    Metric = metric,
                    Label = MetricLabel(metric),
                    CurrentValue = now.Value,
                    PriorValue = before.Value,
                    Change = Math.Round(now.Value - before.Value, 1, MidpointRounding.AwayFromZero),
                    Unit = MetricUnit(metric),
                    DenominatorCurrent = IsRateMetric(metric) ? current.ScoredUsers : (double?)null,
                    DenominatorPrior = IsRateMetric(metric) ? prior.ScoredUsers : (double?)null,
                    DenominatorChange = IsRateMetric(metric) ? current.ScoredUsers - prior.ScoredUsers : (double?)null,
                });
            }
            return deltas;
        }

        private static IEnumerable<string> HeadlineMetrics()
        {
            return new[]
            {
                CopilotAdoptionTargetMetricCodes.AdoptionRatePct,
                CopilotAdoptionTargetMetricCodes.HabitRatePct,
                CopilotAdoptionTargetMetricCodes.ReclaimableSeats,
                CopilotAdoptionTargetMetricCodes.ReclaimCertainSeats,
                CopilotAdoptionTargetMetricCodes.ReclaimProbableSeats,
                CopilotAdoptionTargetMetricCodes.ReclaimReviewSeats,
                CopilotAdoptionTargetMetricCodes.NeverUsedUsers,
                CopilotAdoptionTargetMetricCodes.DormantUsers,
                CopilotAdoptionTargetMetricCodes.AverageAdoptionScore,
                CopilotAdoptionTargetMetricCodes.MedianAdoptionScore,
                CopilotAdoptionTargetMetricCodes.UnlicensedActiveUsers,
                CopilotAdoptionTargetMetricCodes.RecommendedForLicence,
            };
        }

        private static string NormaliseMetric(string metric)
        {
            var match = HeadlineMetrics().FirstOrDefault(m => string.Equals(m, metric, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ArgumentException("Unsupported Copilot Adoption target metric.", nameof(metric));
            return match;
        }

        private static string NormaliseScopeType(string scopeType)
        {
            if (string.Equals(scopeType, "department", StringComparison.OrdinalIgnoreCase)) return "department";
            if (string.Equals(scopeType, "cohort", StringComparison.OrdinalIgnoreCase)) return "cohort";
            return "tenant";
        }

        private static bool IsRateMetric(string metric)
        {
            return string.Equals(metric, CopilotAdoptionTargetMetricCodes.AdoptionRatePct, StringComparison.Ordinal)
                || string.Equals(metric, CopilotAdoptionTargetMetricCodes.HabitRatePct, StringComparison.Ordinal);
        }

        private static string MetricUnit(string metric)
        {
            return metric != null && metric.EndsWith("Pct", StringComparison.Ordinal) ? "percent" : "count";
        }

        private static string MetricLabel(string metric)
        {
            switch (metric)
            {
                case CopilotAdoptionTargetMetricCodes.AdoptionRatePct: return "Adoption rate";
                case CopilotAdoptionTargetMetricCodes.HabitRatePct: return "Habit rate";
                case CopilotAdoptionTargetMetricCodes.ReclaimableSeats: return "Reclaimable licences";
                case CopilotAdoptionTargetMetricCodes.ReclaimCertainSeats: return "Reclaim - certain";
                case CopilotAdoptionTargetMetricCodes.ReclaimProbableSeats: return "Reclaim - probable";
                case CopilotAdoptionTargetMetricCodes.ReclaimReviewSeats: return "Reclaim - review";
                case CopilotAdoptionTargetMetricCodes.NeverUsedUsers: return "Never used";
                case CopilotAdoptionTargetMetricCodes.DormantUsers: return "Dormant";
                case CopilotAdoptionTargetMetricCodes.AverageAdoptionScore: return "Average engagement";
                case CopilotAdoptionTargetMetricCodes.MedianAdoptionScore: return "Median engagement";
                case CopilotAdoptionTargetMetricCodes.UnlicensedActiveUsers: return "Using Copilot unlicensed";
                case CopilotAdoptionTargetMetricCodes.RecommendedForLicence: return "Recommended for a licence";
                default: return metric;
            }
        }

        private static double? MetricValue(CopilotAdoptionSummary summary, string metric, string scopeType, string scopeValue)
        {
            if (summary == null) return null;
            if (string.Equals(scopeType, "department", StringComparison.OrdinalIgnoreCase))
            {
                var segment = (summary.AdoptionByDepartment ?? new List<AdoptionSegmentRow>())
                    .FirstOrDefault(r => string.Equals(r.Segment, scopeValue, StringComparison.OrdinalIgnoreCase));
                if (segment == null) return null;
                switch (metric)
                {
                    case CopilotAdoptionTargetMetricCodes.AdoptionRatePct: return segment.AdoptionRatePct;
                    case CopilotAdoptionTargetMetricCodes.HabitRatePct: return CopilotAdoptionScoring.Percentage(segment.HabitualUsers, segment.LicensedUsers);
                    case CopilotAdoptionTargetMetricCodes.NeverUsedUsers: return segment.NeverUsedUsers;
                    case CopilotAdoptionTargetMetricCodes.AverageAdoptionScore: return segment.AverageAdoptionScore;
                    default: return null;
                }
            }
            if (string.Equals(scopeType, "cohort", StringComparison.OrdinalIgnoreCase)) return null;

            switch (metric)
            {
                case CopilotAdoptionTargetMetricCodes.AdoptionRatePct: return summary.AdoptionRatePct;
                case CopilotAdoptionTargetMetricCodes.HabitRatePct: return summary.HabitRatePct;
                case CopilotAdoptionTargetMetricCodes.ReclaimableSeats: return summary.ReclaimableSeats;
                case CopilotAdoptionTargetMetricCodes.ReclaimCertainSeats: return summary.ReclaimCertainSeats;
                case CopilotAdoptionTargetMetricCodes.ReclaimProbableSeats: return summary.ReclaimProbableSeats;
                case CopilotAdoptionTargetMetricCodes.ReclaimReviewSeats: return summary.ReclaimReviewSeats;
                case CopilotAdoptionTargetMetricCodes.NeverUsedUsers: return summary.NeverUsedUsers;
                case CopilotAdoptionTargetMetricCodes.DormantUsers: return summary.DormantUsers;
                case CopilotAdoptionTargetMetricCodes.AverageAdoptionScore: return summary.AverageAdoptionScore;
                case CopilotAdoptionTargetMetricCodes.MedianAdoptionScore: return summary.MedianAdoptionScore;
                case CopilotAdoptionTargetMetricCodes.UnlicensedActiveUsers: return summary.UnlicensedActiveUsers;
                case CopilotAdoptionTargetMetricCodes.RecommendedForLicence: return summary.RecommendedForLicence;
                default: return null;
            }
        }

        public async Task<CopilotAdoptionCohortComparison> ComparePublishedPeriodCohortsAsync(
            DateTime leftPeriodEndUtc,
            DateTime rightPeriodEndUtc,
            int periodDays,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var leftEnd = leftPeriodEndUtc.Date;
            var rightEnd = rightPeriodEndUtc.Date;
            var days = Math.Max(1, periodDays);
            var gate = await ComparePublishedPeriodsAsync(leftEnd, rightEnd, days, cancellationToken);
            var result = new CopilotAdoptionCohortComparison { Gate = gate };

            if (!gate.OptionsComparable)
            {
                result.Summary.Warnings.Add(gate.Message);
                return result;
            }

            var activationWindowDays = Math.Max(1, _options.ActivationWindowDays <= 0
                ? _options.ReclaimGraceDays
                : _options.ActivationWindowDays);
            var historyDays = Math.Max(days, _options.HistoryDays);

            var rows = await QueryAsync<CopilotAdoptionCohortUserRow>(
                CopilotAdoptionSql.PublishedPeriodCohortRowsSql,
                cancellationToken,
                new SqlParameter("@leftPeriodEnd", leftEnd),
                new SqlParameter("@rightPeriodEnd", rightEnd),
                new SqlParameter("@periodDays", days),
                new SqlParameter("@leftFrom", leftEnd.AddDays(-(days - 1))),
                new SqlParameter("@rightFrom", rightEnd.AddDays(-(days - 1))),
                new SqlParameter("@rightHistoryFrom", rightEnd.AddDays(-(historyDays - 1))),
                new SqlParameter("@activationWindowDays", activationWindowDays));

            foreach (var row in rows)
            {
                row.TransitionLabel = TransitionLabel(row.Transition);
            }

            result.Rows = rows;
            FinaliseCohortComparison(result, activationWindowDays);
            return result;
        }

        public async Task<CopilotAdoptionCohortUserPage> ReadPublishedPeriodCohortUsersAsync(
            DateTime leftPeriodEndUtc,
            DateTime rightPeriodEndUtc,
            int periodDays,
            string transition = null,
            string fromBand = null,
            string toBand = null,
            string department = null,
            string activationState = null,
            int skip = 0,
            int take = 50,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var comparison = await ComparePublishedPeriodCohortsAsync(
                leftPeriodEndUtc, rightPeriodEndUtc, periodDays, cancellationToken);
            var matched = ApplyCohortUserFilters(
                comparison.Rows, transition, fromBand, toBand, department, activationState);
            var pageSize = Math.Min(Math.Max(1, take), 500);
            var offset = Math.Max(0, skip);

            return new CopilotAdoptionCohortUserPage
            {
                Total = matched.Count,
                Skip = offset,
                Take = pageSize,
                Rows = matched.Skip(offset).Take(pageSize).ToList(),
                Warnings = comparison.Summary.Warnings,
            };
        }

        public static string OptionsHash(CopilotAdoptionOptions options)
        {
            var o = options ?? CopilotAdoptionOptions.Default;
            var factShapingOptions = new
            {
                WindowDays = Math.Max(1, o.WindowDays),
                HistoryDays = Math.Max(1, o.HistoryDays),
                UsageReportLagDays = Math.Max(0, o.UsageReportLagDays)
            };
            var json = JsonConvert.SerializeObject(factShapingOptions, Formatting.None);
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private static CopilotAdoptionOptions OptionsForStoredPeriod(CopilotAdoptionOptions options, int periodDays)
        {
            var clone = JsonConvert.DeserializeObject<CopilotAdoptionOptions>(
                JsonConvert.SerializeObject(options ?? CopilotAdoptionOptions.Default, Formatting.None));
            clone.WindowDays = Math.Max(1, periodDays);
            return clone;
        }



        public async Task<CopilotAdoptionCohort> CreateCohortFromActionAsync(
            CopilotAdoptionAnalysis analysis,
            CopilotAdoptionCreateCohortRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));
            if (request == null) throw new ArgumentNullException(nameof(request));

            var actionCode = NormaliseActionCode(request.ActionCode);
            var members = (analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
                .Where(u => string.Equals(u.RecommendedActionCode, actionCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(u => u.UserId)
                .ToList();

            if (members.Count == 0)
            {
                throw new InvalidOperationException("No users currently match that Copilot Adoption action, so an empty cohort was not created.");
            }

            var holdoutPercentage = Math.Max(0, Math.Min(50, request.HoldoutPercentage));
            var memberJson = JsonConvert.SerializeObject(members.Select((u, index) => new
            {
                userId = u.UserId,
                baselineBand = (int)u.Band,
                baselineScore = u.AdoptionScore,
                baselineActiveDays = u.ActiveDays,
                department = string.IsNullOrWhiteSpace(u.Department) ? null : u.Department.Trim(),
                holdoutControl = holdoutPercentage > 0 && (index * 100 / members.Count) < holdoutPercentage,
            }));

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? $"{CopilotAdoptionScoring.ActionLabel(actionCode)} cohort - {DateTime.UtcNow:yyyy-MM-dd}"
                : request.Name.Trim();

            var createdBy = string.IsNullOrWhiteSpace(request.CreatedBy) ? "unknown" : request.CreatedBy.Trim();
            var baselineEnd = (analysis.Summary?.ToUtc ?? DateTime.UtcNow).Date;
            var baselineDays = analysis.Summary?.WindowDays > 0 ? analysis.Summary.WindowDays : _options.WindowDays;
            var cohortId = (await QueryAsync<int?>(
                CopilotAdoptionSql.InsertCohortSql,
                cancellationToken,
                new SqlParameter("@name", name),
                new SqlParameter("@actionCode", actionCode),
                new SqlParameter("@createdBy", createdBy),
                new SqlParameter("@baselinePeriodEnd", baselineEnd),
                new SqlParameter("@baselinePeriodDays", baselineDays),
                new SqlParameter("@baselineOptionsHash", OptionsHash(analysis.Summary?.Options ?? _options)),
                new SqlParameter("@membersJson", memberJson))).FirstOrDefault();

            if (!cohortId.HasValue) throw new InvalidOperationException("The cohort insert did not return an id.");
            return await GetCohortAsync(cohortId.Value, cancellationToken);
        }

        public async Task<CopilotAdoptionIntervention> CreateInterventionFromActionAsync(
            CopilotAdoptionAnalysis analysis,
            CopilotAdoptionCreateInterventionRequest request,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.HoldoutPercentage == 0) request.HoldoutPercentage = 10;
            var cohort = await CreateCohortFromActionAsync(analysis, request, cancellationToken);
            var interventionId = (await QueryAsync<int?>(
                CopilotAdoptionSql.InsertInterventionSql,
                cancellationToken,
                new SqlParameter("@cohortId", cohort.CohortId),
                new SqlParameter("@owner", DbValue(request.Owner)),
                new SqlParameter("@interventionType", NormaliseInterventionType(request.InterventionType)),
                new SqlParameter("@guidanceResource", DbValue(request.GuidanceResource)),
                new SqlParameter("@startedUtc", DbValue(request.StartedUtc)),
                new SqlParameter("@dueUtc", DbValue(request.DueUtc)),
                new SqlParameter("@completedUtc", DbValue(request.CompletedUtc)),
                new SqlParameter("@status", NormaliseInterventionStatus(request.Status, request.StartedUtc, request.CompletedUtc)),
                new SqlParameter("@intendedOutcome", DbValue(request.IntendedOutcome)),
                new SqlParameter("@notes", DbValue(request.Notes)),
                new SqlParameter("@intendedReinvestmentType", NormaliseReinvestmentType(request.IntendedReinvestmentType)),
                new SqlParameter("@intendedReinvestmentDescription", DbValue(request.IntendedReinvestmentDescription)))).FirstOrDefault();

            if (!interventionId.HasValue) throw new InvalidOperationException("The intervention insert did not return an id.");
            return (await QueryAsync<CopilotAdoptionIntervention>(
                CopilotAdoptionSql.InterventionByIdSql,
                cancellationToken,
                new SqlParameter("@interventionId", interventionId.Value))).FirstOrDefault();
        }

        public async Task<List<CopilotAdoptionCohort>> GetCohortsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await QueryAsync<CopilotAdoptionCohort>(CopilotAdoptionSql.CohortsSql, cancellationToken);
        }

        public async Task<CopilotAdoptionCohort> GetCohortAsync(int cohortId, CancellationToken cancellationToken = default(CancellationToken))
        {
            return (await QueryAsync<CopilotAdoptionCohort>(
                CopilotAdoptionSql.CohortByIdSql,
                cancellationToken,
                new SqlParameter("@cohortId", cohortId))).FirstOrDefault();
        }

        public async Task<List<CopilotAdoptionCohortMember>> GetCohortMembersAsync(int cohortId, CancellationToken cancellationToken = default(CancellationToken))
        {
            var rows = await QueryAsync<CopilotAdoptionCohortMember>(
                CopilotAdoptionSql.CohortMembersSql,
                cancellationToken,
                new SqlParameter("@cohortId", cohortId));
            foreach (var row in rows)
            {
                row.BaselineBandName = CopilotAdoptionScoring.BandDisplayName(row.BaselineBand);
            }
            return rows;
        }

        public async Task CloseCohortAsync(int cohortId, string closedBy, CancellationToken cancellationToken = default(CancellationToken))
        {
            await ExecuteAsync(
                CopilotAdoptionSql.CloseCohortSql,
                cancellationToken,
                QueryTimeoutSecs,
                new SqlParameter("@cohortId", cohortId),
                new SqlParameter("@closedBy", DbValue(closedBy)));
        }

        public async Task<List<CopilotAdoptionIntervention>> GetInterventionsAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await QueryAsync<CopilotAdoptionIntervention>(CopilotAdoptionSql.InterventionsSql, cancellationToken);
        }

        public async Task<CopilotAdoptionInterventionOutcome> MeasureInterventionAsync(
            int interventionId,
            DateTime? followupPeriodEndUtc = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var intervention = (await QueryAsync<CopilotAdoptionIntervention>(
                CopilotAdoptionSql.InterventionByIdSql,
                cancellationToken,
                new SqlParameter("@interventionId", interventionId))).FirstOrDefault();
            if (intervention == null) return null;

            var cohort = await GetCohortAsync(intervention.CohortId, cancellationToken);
            var members = await GetCohortMembersAsync(intervention.CohortId, cancellationToken);
            var followupEnd = (followupPeriodEndUtc ?? DateTime.UtcNow.Date.AddDays(-1)).Date;
            var outcome = new CopilotAdoptionInterventionOutcome
            {
                Intervention = intervention,
                Cohort = cohort,
                FollowupPeriodEnd = followupEnd,
                MatchingCriteria = "Baseline adoption band, 10-point score bucket and department. Tenure is not matched until licence assignment history is available (#277).",
                Observational = !members.Any(m => m.HoldoutControl),
                MethodLabel = members.Any(m => m.HoldoutControl)
                    ? "Random hold-out comparison (near-causal, not a proof)"
                    : "Matched untreated comparison (observational, not a randomised trial)",
            };

            var baseline = await ReadPublishedPeriodAsync(cohort.BaselinePeriodEnd, cohort.BaselinePeriodDays, cancellationToken);
            var followup = await ReadPublishedPeriodAsync(followupEnd, cohort.BaselinePeriodDays, cancellationToken);
            if (baseline == null || followup == null)
            {
                outcome.Refused = true;
                outcome.RefusalReason = "The baseline and follow-up published period facts are both required before an intervention effect can be reported.";
                return outcome;
            }

            var baselineRows = baseline.Analysis.LicensedUsers.ToDictionary(u => u.UserId);
            var followupRows = followup.Analysis.LicensedUsers.ToDictionary(u => u.UserId);
            var memberIds = new HashSet<int>(members.Select(m => m.UserId));
            var holdout = new HashSet<int>(members.Where(m => m.HoldoutControl).Select(m => m.UserId));
            var treatedIds = members.Where(m => !m.HoldoutControl).Select(m => m.UserId).ToList();
            var controlIds = holdout.Count > 0
                ? holdout.ToList()
                : BuildMatchedControlIds(members, baselineRows.Values, memberIds);

            outcome.TreatedN = treatedIds.Count(id => followupRows.ContainsKey(id));
            outcome.ControlN = controlIds.Count(id => followupRows.ContainsKey(id));
            if (outcome.TreatedN < _options.MinSeatsPerSegment || outcome.ControlN < _options.MinSeatsPerSegment)
            {
                outcome.Refused = true;
                outcome.RefusalReason = $"No effect is reported because the treated group (n={outcome.TreatedN}) or control group (n={outcome.ControlN}) is below the minimum segment size of {_options.MinSeatsPerSegment}.";
                return outcome;
            }

            outcome.TreatedChange = AverageScoreChange(treatedIds, id => members.First(m => m.UserId == id).BaselineScore, followupRows);
            outcome.ControlChange = AverageScoreChange(controlIds, id => baselineRows[id].AdoptionScore, followupRows);
            outcome.DifferenceInDifferences = Math.Round(outcome.TreatedChange - outcome.ControlChange, 1, MidpointRounding.AwayFromZero);
            outcome.EffectSizeLabel = EffectLabel(outcome.DifferenceInDifferences);
            outcome.ControlComposition = BuildControlComposition(controlIds, baselineRows);
            outcome.LeadingIndicators = await BuildLeadingIndicatorOutcomesAsync(cohort, treatedIds, controlIds, followupEnd, cancellationToken);
            return outcome;
        }

        private List<int> BuildMatchedControlIds(
            List<CopilotAdoptionCohortMember> members,
            IEnumerable<LicensedUserAdoptionRow> baselineRows,
            HashSet<int> memberIds)
        {
            var keys = new HashSet<string>(members.Select(m => MatchKey(m.BaselineBand, m.BaselineScore, m.Department)));
            return baselineRows
                .Where(u => !memberIds.Contains(u.UserId) && keys.Contains(MatchKey(u.Band, u.AdoptionScore, u.Department)))
                .Select(u => u.UserId)
                .ToList();
        }

        private static string MatchKey(AdoptionBand band, double score, string department)
        {
            var bucket = Math.Floor(Math.Max(0, Math.Min(100, score)) / 10d) * 10;
            return ((int)band).ToString(CultureInfo.InvariantCulture) + "|" + bucket.ToString(CultureInfo.InvariantCulture) + "|" + (department ?? string.Empty).Trim();
        }

        private static double AverageScoreChange(IEnumerable<int> ids, Func<int, double> baselineScore, Dictionary<int, LicensedUserAdoptionRow> followupRows)
        {
            var changes = ids.Where(followupRows.ContainsKey).Select(id => followupRows[id].AdoptionScore - baselineScore(id)).ToList();
            return changes.Count == 0 ? 0 : Math.Round(changes.Average(), 1, MidpointRounding.AwayFromZero);
        }

        private List<CopilotAdoptionControlCompositionRow> BuildControlComposition(IEnumerable<int> ids, Dictionary<int, LicensedUserAdoptionRow> baselineRows)
        {
            return ids.Where(baselineRows.ContainsKey)
                .Select(id => baselineRows[id])
                .GroupBy(u => new { Department = string.IsNullOrWhiteSpace(u.Department) ? "(no department)" : u.Department.Trim(), u.Band, Bucket = Math.Floor(Math.Max(0, Math.Min(100, u.AdoptionScore)) / 10d) * 10 })
                .Select(g => new CopilotAdoptionControlCompositionRow
                {
                    Department = g.Key.Department,
                    Band = CopilotAdoptionScoring.BandDisplayName(g.Key.Band),
                    ScoreBucket = g.Key.Bucket.ToString("0", CultureInfo.InvariantCulture) + "-" + Math.Min(100, g.Key.Bucket + 9).ToString("0", CultureInfo.InvariantCulture),
                    Users = g.Count(),
                })
                .OrderByDescending(r => r.Users)
                .ThenBy(r => r.Department, StringComparer.OrdinalIgnoreCase)
                .Take(_options.TopSegments)
                .ToList();
        }

        private async Task<List<CopilotAdoptionLeadingIndicatorOutcome>> BuildLeadingIndicatorOutcomesAsync(
            CopilotAdoptionCohort cohort,
            List<int> treatedIds,
            List<int> controlIds,
            DateTime followupEnd,
            CancellationToken cancellationToken)
        {
            var baseline = await WorkloadSnapshotsAsync(cohort, cohort.BaselinePeriodEnd, cancellationToken);
            var followup = await WorkloadSnapshotsAsync(cohort, followupEnd, cancellationToken);
            var indicators = new[]
            {
                new { Code = "teamsMessages", Label = "Teams messages", Selector = new Func<CopilotAdoptionWorkloadSnapshotRow, double>(r => r.TeamsMessages) },
                new { Code = "teamsMeetings", Label = "Teams meetings", Selector = new Func<CopilotAdoptionWorkloadSnapshotRow, double>(r => r.TeamsMeetings) },
                new { Code = "email", Label = "Email sent/read", Selector = new Func<CopilotAdoptionWorkloadSnapshotRow, double>(r => r.EmailsSent + r.EmailsRead) },
                new { Code = "files", Label = "SharePoint/OneDrive file activity", Selector = new Func<CopilotAdoptionWorkloadSnapshotRow, double>(r => r.FilesViewedOrEdited) },
            };
            return indicators.Select(i =>
            {
                var treated = AverageIndicatorChange(treatedIds, baseline, followup, i.Selector);
                var control = AverageIndicatorChange(controlIds, baseline, followup, i.Selector);
                var diff = Math.Round(treated - control, 1, MidpointRounding.AwayFromZero);
                return new CopilotAdoptionLeadingIndicatorOutcome
                {
                    Code = i.Code,
                    Label = i.Label,
                    TreatedChange = treated,
                    ControlChange = control,
                    DifferenceInDifferences = diff,
                    MovementLabel = diff == 0 ? "No movement against the matched control." : (diff > 0 ? "Moved up against the matched control." : "Moved down against the matched control."),
                };
            }).ToList();
        }

        private async Task<Dictionary<int, CopilotAdoptionWorkloadSnapshotRow>> WorkloadSnapshotsAsync(CopilotAdoptionCohort cohort, DateTime periodEnd, CancellationToken cancellationToken)
        {
            var rows = await QueryAsync<CopilotAdoptionWorkloadSnapshotRow>(
                CopilotAdoptionSql.WorkloadSnapshotSql,
                cancellationToken,
                new SqlParameter("@cohortId", cohort.CohortId),
                new SqlParameter("@baselinePeriodEnd", cohort.BaselinePeriodEnd),
                new SqlParameter("@followupPeriodEnd", periodEnd),
                new SqlParameter("@periodDays", cohort.BaselinePeriodDays),
                new SqlParameter("@from", periodEnd.AddDays(-(Math.Max(1, cohort.BaselinePeriodDays) - 1))),
                new SqlParameter("@to", periodEnd));
            return rows.ToDictionary(r => r.UserId);
        }

        private static double AverageIndicatorChange(IEnumerable<int> ids, Dictionary<int, CopilotAdoptionWorkloadSnapshotRow> baseline, Dictionary<int, CopilotAdoptionWorkloadSnapshotRow> followup, Func<CopilotAdoptionWorkloadSnapshotRow, double> selector)
        {
            var changes = ids.Select(id => (followup.ContainsKey(id) ? selector(followup[id]) : 0) - (baseline.ContainsKey(id) ? selector(baseline[id]) : 0)).ToList();
            return changes.Count == 0 ? 0 : Math.Round(changes.Average(), 1, MidpointRounding.AwayFromZero);
        }

        private static string EffectLabel(double effect)
        {
            if (Math.Abs(effect) < 1) return "No meaningful movement against the matched control.";
            return effect > 0
                ? "The treated cohort improved more than the matched control. This is an intervention comparison, not proof that Copilot caused the change."
                : "The treated cohort improved less than the matched control. This is an intervention comparison, not proof of causation.";
        }

        private static object DbValue(object value)
        {
            if (value == null) return DBNull.Value;
            if (value is string text) return string.IsNullOrWhiteSpace(text) ? (object)DBNull.Value : text.Trim();
            return value;
        }

        private static string NormaliseActionCode(string actionCode)
        {
            var code = (actionCode ?? string.Empty).Trim();
            if (!CopilotAdoptionScoring.AllActionCodes.Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException("Unknown Copilot Adoption action code.", nameof(actionCode));
            }
            return CopilotAdoptionScoring.AllActionCodes.First(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormaliseInterventionType(string interventionType)
        {
            var allowed = new[] { "briefing", "scenario workshop", "champion session", "comms", "one-to-one", "licence reassignment" };
            var value = (interventionType ?? "briefing").Trim();
            return allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? "briefing";
        }

        private static string NormaliseInterventionStatus(string status, DateTime? started, DateTime? completed)
        {
            if (completed.HasValue) return "completed";
            var allowed = new[] { "planned", "started", "completed", "cancelled" };
            var value = (status ?? (started.HasValue ? "started" : "planned")).Trim();
            return allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? "planned";
        }

        private static string NormaliseReinvestmentType(string value)
        {
            var allowed = new[] { "customer-facing work", "shorter cycle time", "quality improvement", "employee development", "capacity buffer", "other" };
            var text = (value ?? "other").Trim();
            return allowed.FirstOrDefault(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase)) ?? "other";
        }

        private async Task<CopilotAdoptionPeriodRun> GetPublishedPeriodRunAsync(DateTime periodEnd, int periodDays, CancellationToken cancellationToken)
        {
            var rows = await QueryAsync<CopilotAdoptionPeriodRun>(
                CopilotAdoptionSql.PublishedPeriodRunSql,
                cancellationToken,
                new SqlParameter("@periodEnd", periodEnd.Date),
                new SqlParameter("@periodDays", periodDays));
            return rows.FirstOrDefault();
        }

        private static void FinaliseCohortComparison(CopilotAdoptionCohortComparison result, int activationWindowDays)
        {
            var rows = result.Rows ?? new List<CopilotAdoptionCohortUserRow>();
            var earlier = rows.Where(r => r.ExistedInEarlierPeriod).ToList();
            var current = rows.Where(r => r.ExistsInCurrentPeriod).ToList();

            result.Summary.EarlierPopulation = earlier.Count;
            result.Summary.CurrentPopulation = current.Count;
            result.Summary.NewlyAssigned = rows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase));
            result.Summary.ReclaimCaveat =
                "Reclaimed means a user held a Copilot seat in the earlier published period and has no seat in the current published period. "
                + "When the account remains enabled this is evidence of licence reclaim or reassignment; when the account is disabled it may also be user departure.";

            result.Transitions = new[]
                {
                    CopilotAdoptionCohortTransitions.Retained,
                    CopilotAdoptionCohortTransitions.Reactivated,
                    CopilotAdoptionCohortTransitions.Lapsed,
                    CopilotAdoptionCohortTransitions.Reclaimed,
                    CopilotAdoptionCohortTransitions.StillAtRisk,
                    CopilotAdoptionCohortTransitions.NewlyAssigned,
                }
                .Select(code => new CopilotAdoptionCohortTransitionSummary
                {
                    Code = code,
                    Label = TransitionLabel(code),
                    Description = TransitionDescription(code),
                    Users = rows.Count(r => string.Equals(r.Transition, code, StringComparison.OrdinalIgnoreCase)),
                    ShareOfEarlierPopulationPct = code == CopilotAdoptionCohortTransitions.NewlyAssigned
                        ? 0
                        : CopilotAdoptionScoring.Percentage(rows.Count(r => string.Equals(r.Transition, code, StringComparison.OrdinalIgnoreCase)), result.Summary.EarlierPopulation),
                })
                .Where(t => t.Users > 0 || t.Code != CopilotAdoptionCohortTransitions.NewlyAssigned)
                .ToList();

            result.Summary.EarlierPopulationTransitionTotal = result.Transitions
                .Where(t => t.Code != CopilotAdoptionCohortTransitions.NewlyAssigned)
                .Sum(t => t.Users);
            result.Summary.TransitionsSumToEarlierPopulation =
                result.Summary.EarlierPopulationTransitionTotal == result.Summary.EarlierPopulation;

            result.Flows = rows
                .GroupBy(r => new { r.FromBand, r.ToBand, r.Transition })
                .Select(g => new CopilotAdoptionCohortFlowSummary
                {
                    FromBand = g.Key.FromBand,
                    ToBand = g.Key.ToBand,
                    Transition = g.Key.Transition,
                    Users = g.Count(),
                })
                .OrderByDescending(f => f.Users)
                .ThenBy(f => f.FromBand)
                .ThenBy(f => f.ToBand)
                .ToList();

            result.Activation = BuildActivationSummary(current, activationWindowDays);
        }

        /// <summary>
        /// Internal rather than private so the activation-rate suppression rule (null, not 0%, when no
        /// seat has a known assignment date) can be tested directly without a database.
        /// </summary>
        internal static CopilotAdoptionActivationSummary BuildActivationSummary(
            List<CopilotAdoptionCohortUserRow> currentRows,
            int activationWindowDays)
        {
            var activation = new CopilotAdoptionActivationSummary
            {
                ActivationWindowDays = activationWindowDays,
                SeatDateUnknownUsers = currentRows.Count(r => string.Equals(r.ActivationState, "seatDateUnknown", StringComparison.OrdinalIgnoreCase)),
                AssignedBeforeHistoryUsers = currentRows.Count(r => string.Equals(r.ActivationState, "assignedBeforeHistory", StringComparison.OrdinalIgnoreCase)),
                NewSeatsAssignedInPeriod = currentRows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(r.ActivationState, "seatDateUnknown", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(r.ActivationState, "assignedBeforeHistory", StringComparison.OrdinalIgnoreCase)),
                ActivatedWithinWindow = currentRows.Count(r => string.Equals(r.ActivationState, "activatedWithinWindow", StringComparison.OrdinalIgnoreCase)),
                NeverActivatedUsers = currentRows.Count(r => string.Equals(r.ActivationState, "neverActivated", StringComparison.OrdinalIgnoreCase)),
                TooNewToJudgeUsers = currentRows.Count(r => string.Equals(r.ActivationState, "tooNewToJudge", StringComparison.OrdinalIgnoreCase)),
                Caveat = "Time-to-first-use uses seat_first_observed_utc only. Rows with unknown seat dates are counted separately and excluded; account creation is not substituted for seat assignment.",
            };

            var knownActivations = currentRows
                .Where(r => r.DaysToFirstUse.HasValue)
                .Select(r => (double)r.DaysToFirstUse.Value)
                .ToList();
            activation.KnownSeatStartUsers = currentRows.Count
                - activation.SeatDateUnknownUsers
                - activation.AssignedBeforeHistoryUsers;
            activation.MedianDaysToFirstUse = knownActivations.Count == 0
                ? (double?)null
                : CopilotAdoptionScoring.Median(knownActivations);

            var denominator = currentRows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(r.ActivationState, "seatDateUnknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(r.ActivationState, "assignedBeforeHistory", StringComparison.OrdinalIgnoreCase));
            var activatedNewSeats = currentRows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.ActivationState, "activatedWithinWindow", StringComparison.OrdinalIgnoreCase));
            // Null, not 0, when no seat has a known assignment date. Seat dates come from #277 and are
            // NULL for every row until that lands, so a non-nullable rate published a confident "0%
            // activated" - reading as a failed onboarding programme - for a figure that was simply not
            // measurable. SeatDateUnknownUsers already carries the reason.
            activation.ActivationRatePct = denominator > 0
                ? (double?)CopilotAdoptionScoring.Percentage(activatedNewSeats, denominator)
                : null;

            activation.Distribution = BuildActivationDistribution(knownActivations);
            activation.ByDepartment = currentRows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Department) ? "(no department)" : r.Department.Trim())
                .Select(g =>
                {
                    var groupRows = g.ToList();
                    var newKnown = groupRows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.ActivationState, "seatDateUnknown", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(r.ActivationState, "assignedBeforeHistory", StringComparison.OrdinalIgnoreCase));
                    var activated = groupRows.Count(r => string.Equals(r.Transition, CopilotAdoptionCohortTransitions.NewlyAssigned, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.ActivationState, "activatedWithinWindow", StringComparison.OrdinalIgnoreCase));
                    return new CopilotAdoptionActivationSegment
                    {
                        Segment = g.Key,
                        NewSeatsAssignedInPeriod = newKnown,
                        ActivatedWithinWindow = activated,
                        ActivationRatePct = newKnown > 0
                            ? (double?)CopilotAdoptionScoring.Percentage(activated, newKnown)
                            : null,
                        NeverActivatedUsers = groupRows.Count(r => string.Equals(r.ActivationState, "neverActivated", StringComparison.OrdinalIgnoreCase)),
                        SeatDateUnknownUsers = groupRows.Count(r => string.Equals(r.ActivationState, "seatDateUnknown", StringComparison.OrdinalIgnoreCase)),
                    };
                })
                .Where(s => s.NewSeatsAssignedInPeriod > 0 || s.NeverActivatedUsers > 0 || s.SeatDateUnknownUsers > 0)
                .OrderByDescending(s => s.NeverActivatedUsers)
                // Nulls last: an unmeasurable department must not be ranked as though it were worse
                // than a measured 0%, which is what LINQ's default null-first ordering would do.
                .ThenBy(s => s.ActivationRatePct ?? double.MaxValue)
                .ThenBy(s => s.Segment)
                .ToList();

            return activation;
        }

        private static List<CopilotAdoptionActivationDistributionBucket> BuildActivationDistribution(List<double> days)
        {
            var ranges = new[]
            {
                new { Label = "0-7 days", Min = 0d, Max = 7d },
                new { Label = "8-14 days", Min = 8d, Max = 14d },
                new { Label = "15-30 days", Min = 15d, Max = 30d },
                new { Label = "31-60 days", Min = 31d, Max = 60d },
                new { Label = "61+ days", Min = 61d, Max = double.MaxValue },
            };

            return ranges
                .Select(r =>
                {
                    var count = days.Count(d => d >= r.Min && d <= r.Max);
                    return new CopilotAdoptionActivationDistributionBucket
                    {
                        Label = r.Label,
                        Users = count,
                        SharePct = CopilotAdoptionScoring.Percentage(count, days.Count),
                    };
                })
                .ToList();
        }

        private static List<CopilotAdoptionCohortUserRow> ApplyCohortUserFilters(
            IEnumerable<CopilotAdoptionCohortUserRow> rows,
            string transition,
            string fromBand,
            string toBand,
            string department,
            string activationState)
        {
            var filtered = rows ?? Enumerable.Empty<CopilotAdoptionCohortUserRow>();
            if (!string.IsNullOrWhiteSpace(transition))
            {
                filtered = filtered.Where(r => string.Equals(r.Transition, transition.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(fromBand))
            {
                filtered = filtered.Where(r => string.Equals(r.FromBand, fromBand.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(toBand))
            {
                filtered = filtered.Where(r => string.Equals(r.ToBand, toBand.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(department))
            {
                filtered = filtered.Where(r => string.Equals(r.Department ?? string.Empty, department.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(activationState))
            {
                filtered = filtered.Where(r => string.Equals(r.ActivationState ?? string.Empty, activationState.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            return filtered
                .OrderBy(r => r.Transition)
                .ThenBy(r => r.UserPrincipalName)
                .ToList();
        }

        private static string TransitionLabel(string code)
        {
            switch (code)
            {
                case CopilotAdoptionCohortTransitions.Retained: return "Retained";
                case CopilotAdoptionCohortTransitions.Reactivated: return "Reactivated";
                case CopilotAdoptionCohortTransitions.Lapsed: return "Lapsed";
                case CopilotAdoptionCohortTransitions.Reclaimed: return "Reclaimed / reassigned";
                case CopilotAdoptionCohortTransitions.NewlyAssigned: return "Newly assigned";
                case CopilotAdoptionCohortTransitions.StillAtRisk: return "Still at risk";
                default: return code ?? string.Empty;
            }
        }

        private static string TransitionDescription(string code)
        {
            switch (code)
            {
                case CopilotAdoptionCohortTransitions.Retained: return "Active in both published periods.";
                case CopilotAdoptionCohortTransitions.Reactivated: return "Inactive in the earlier period and active in the current period.";
                case CopilotAdoptionCohortTransitions.Lapsed: return "Active in the earlier period and inactive in the current period.";
                case CopilotAdoptionCohortTransitions.Reclaimed: return "Held a Copilot seat in the earlier period and no longer holds one.";
                case CopilotAdoptionCohortTransitions.NewlyAssigned: return "Did not hold a Copilot seat in the earlier period and holds one now.";
                case CopilotAdoptionCohortTransitions.StillAtRisk: return "Inactive in both published periods.";
                default: return string.Empty;
            }
        }

        private async Task<int> CountLicensedUsersAsync(List<int> seatIds, CancellationToken cancellationToken)
        {
            var sql = "SELECT COUNT(DISTINCT ul.user_id) AS Value FROM dbo.user_license_type_lookups AS ul WHERE ul.license_type_id IN (" + CopilotAdoptionSql.IdList(seatIds) + ");";
            return await ScalarAsync(sql, cancellationToken);
        }

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
            // The heavy steps run concurrently and each can take up to QueryTimeoutSecs, so if the caller
            // has already given up there is no point starting - and no point continuing between the
            // sequential probes either (see RunStepsAsync).
            cancellationToken.ThrowIfCancellationRequested();

            var nowUtc = DateTime.UtcNow;            var windowStart = CopilotAdoptionScoring.WindowStartUtc(nowUtc, _options.WindowDays);
            var historyStart = CopilotAdoptionScoring.WindowStartUtc(
                nowUtc, Math.Max(_options.WindowDays, _options.HistoryDays));
            var settled = nowUtc.Date.AddDays(-Math.Max(0, _options.UsageReportLagDays));
            var trendStart = MondayOf(nowUtc.Date.AddMonths(-TrendMonths));
            var trendEndExclusive = MondayOf(nowUtc.Date);

            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.GeneratedUtc = nowUtc;
            summary.WindowDays = _options.WindowDays;
            summary.FromUtc = windowStart;
            summary.ToUtc = nowUtc;
            summary.Options = _options;

            // ----- 1. Which licence types count as a Copilot seat -------------------------------
            var licenceTypesOperation = _telemetry.StepStarted(CopilotAdoptionSteps.LicenceTypes);
            var licenceTypesWatch = System.Diagnostics.Stopwatch.StartNew();
            var licenceTypes = await SafeAsync(
                () => QueryAsync<LicenceTypeRow>(CopilotAdoptionSql.LicenceTypesSql, cancellationToken),
                CopilotAdoptionSteps.LicenceTypes,
                CopilotAdoptionQueries.LicenceTypes,
                summary.Warnings,
                "licence types", cancellationToken);
            licenceTypesWatch.Stop();

            // Recorded before the early return below, so a tenant whose analysis stops here still reports
            // where its time went. This step and the probes that follow used to have names in
            // CopilotAdoptionSteps but no timing at all, which quietly understated every other step's
            // share of the total and left the first two database round trips invisible to an operator.
            summary.Diagnostics.Record(
                CopilotAdoptionSteps.LicenceTypes, licenceTypesWatch.ElapsedMilliseconds, licenceTypes == null);
            _telemetry.StepCompleted(
                licenceTypesOperation,
                CopilotAdoptionSteps.LicenceTypes,
                licenceTypesWatch.ElapsedMilliseconds,
                licenceTypes == null);

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
            SummarisePurchasedSeatCapacity(summary);

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

                SummarisePurchasedSeatCapacity(summary);
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
            var probesOperation = _telemetry.StepStarted(CopilotAdoptionSteps.DataSourceProbes);
            var probeWatch = System.Diagnostics.Stopwatch.StartNew();

            // Each of these decides whether a whole DATA SOURCE is used. A failure here degrades to the
            // same value as a genuine absence ("this tenant has no audit data"), which silently removes
            // entire sections and changes what the opportunities query is allowed to see. That is the
            // issue #360 defect one level up, so every probe failure marks the figures incomplete.
            summary.DataSources.AuditAvailable = await SafeScalarAsync(
                CopilotAdoptionSql.HasCopilotAuditDataSql,
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.AuditDataProbe,
                summary.Warnings,
                "Copilot audit data probe",
                () => summary.MarkFiguresIncomplete("Copilot audit data"),
                cancellationToken,
                new SqlParameter("@from", windowStart),
                new SqlParameter("@toExclusive", nowUtc)) == 1;

            // Every Copilot query filters on copilot_chats.time_stamp, so an interaction whose denormalised
            // columns have not been written yet is simply missing from the figures. Migration
            // DenormaliseCopilotChatUserAndTime backfills existing rows, and the importer merge repairs any
            // the upgrade window left behind - but until that has caught up the page would report numbers
            // that are quietly too low. Say so rather than presenting them as fact: that is the whole
            // lesson of issue #360.
            var backfillPending = await SafeScalarAsync(
                CopilotAdoptionSql.PendingCopilotBackfillSql,
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.PendingBackfillProbe,
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
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.CopilotReportDate,
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
                    CopilotAdoptionSteps.DataSourceProbes,
                    CopilotAdoptionQueries.CopilotReportPeriod,
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

            summary.DataSources.CoworkUsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestCoworkReportDateSql,
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.CoworkReportDate,
                summary.Warnings,
                "Cowork usage-report snapshot date",
                () => summary.MarkFiguresIncomplete("Cowork usage report"),
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.CoworkUsageReportAvailable = summary.DataSources.CoworkUsageReportDate.HasValue;

            if (summary.DataSources.CoworkUsageReportDate.HasValue)
            {
                summary.DataSources.CoworkUsageReportPeriodDays = await SafeScalarAsync(
                    CopilotAdoptionSql.LatestCoworkReportPeriodSql,
                    CopilotAdoptionSteps.DataSourceProbes,
                    CopilotAdoptionQueries.CoworkReportPeriod,
                    summary.Warnings,
                    "Cowork usage-report snapshot period",
                    () => summary.MarkFiguresIncomplete("Cowork usage-report snapshot period"),
                    cancellationToken,
                    new SqlParameter("@coworkReportDate", summary.DataSources.CoworkUsageReportDate.Value),
                    new SqlParameter("@windowDays", _options.WindowDays));
            }

            summary.DataSources.M365UsageReportDate = await SafeDateAsync(
                CopilotAdoptionSql.LatestM365ReportDateSql,
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.M365ReportDate,
                summary.Warnings,
                "Microsoft 365 usage-report snapshot date",
                () => summary.MarkFiguresIncomplete("Microsoft 365 usage reports"),
                cancellationToken,
                new SqlParameter("@settled", settled));
            summary.DataSources.M365UsageReportsAvailable = summary.DataSources.M365UsageReportDate.HasValue;

            summary.DataSources.CopilotUsageReportObfuscated = await SafeScalarAsync(
                CopilotAdoptionSql.CopilotReportObfuscatedSql,
                CopilotAdoptionSteps.DataSourceProbes,
                CopilotAdoptionQueries.CopilotReportAnonymisation,
                summary.Warnings,
                "Copilot usage-report anonymisation check",
                // Left defaulting to "not obfuscated" on failure rather than failing closed: flipping it
                // would silently discard the per-user report source, trading one invisible degradation
                // for another. Flagging the figures incomplete is the honest signal, and matches how
                // every other failure in this method is handled.
                () => summary.MarkFiguresIncomplete("Copilot usage-report anonymisation check"),
                cancellationToken) == 1;

            probeWatch.Stop();

            // Six sequential round trips, each of which can gate a whole section of the report. Left
            // sequential on purpose: they are cheap existence probes, and the licensed-user and
            // opportunity queries below cannot be shaped until their answers are known.
            summary.Diagnostics.Record(CopilotAdoptionSteps.DataSourceProbes, probeWatch.ElapsedMilliseconds);
            _telemetry.StepCompleted(
                probesOperation,
                CopilotAdoptionSteps.DataSourceProbes,
                probeWatch.ElapsedMilliseconds,
                false);

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

            // ----- 3-5. The heavy steps ---------------------------------------------------------
            // These run CONCURRENTLY. Every one of them depends only on the seat ids and the data-source
            // probes resolved above, and each writes to its own part of the result, so there is no order
            // between them - but they used to run one after another, which made the page cost their SUM.
            // With each step allowed up to QueryTimeoutSecs that is a worst case of ten times the timeout;
            // a tenant where several steps were slow spent minutes on the page and then failed, because
            // the client gave up before the sum finished. Run together, the cost is the SLOWEST step
            // rather than the total, and a step that degrades to a warning no longer delays the rest.
            var steps = new List<AnalysisStep>();

            if (seatIds.Count > 0)
            {
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.LicensedUsers,
                    output => BuildLicensedUsersAsync(analysis, output, seatIds, windowStart, historyStart, nowUtc, cancellationToken)));
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.UsageByApp,
                    output => BuildUsageByAppAsync(analysis, output, seatIds, windowStart, cancellationToken)));
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.WeeklyTrend,
                    output => BuildWeeklyTrendAsync(analysis, output, seatIds, trendStart, trendEndExclusive, cancellationToken)));

                // Cowork readiness only means something for people who hold a Copilot seat: Cowork requires
                // a Copilot licence as a prerequisite, so assessing an unlicensed user for it would produce
                // a recommendation that cannot be acted on. Gated with the other seat-dependent steps.
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.CoworkReadiness,
                    output => BuildCoworkReadinessAsync(analysis, output, seatIds, windowStart, cancellationToken)));
            }

            // Deliberately not gated on seatIds.Count - see BuildOpportunitiesAsync.
            steps.Add(new AnalysisStep(CopilotAdoptionSteps.LicenceOpportunities,
                output => BuildOpportunitiesAsync(analysis, output, seatIds, windowStart, cancellationToken)));

            // The populations Microsoft's own reporting cannot see. Agents and unlicensed Copilot Chat are
            // reported in their own right, not merely as inputs to the seat decision: an agent estate has
            // its own retirement problem, and unlicensed Chat use is the one Copilot population that is
            // invisible in Microsoft's usage reports.
            if (summary.DataSources.AuditAvailable)
            {
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.AgentEstate,
                    output => BuildAgentEstateAsync(analysis, output, seatIds, windowStart, nowUtc, cancellationToken)));
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.UnlicensedPopulation,
                    output => BuildUnlicensedPopulationAsync(analysis, output, seatIds, windowStart, cancellationToken)));
                steps.Add(new AnalysisStep(CopilotAdoptionSteps.ResourceTypes,
                    output => BuildResourceTypesAsync(analysis, output, windowStart, cancellationToken)));
            }

            await RunStepsAsync(analysis, steps, _maxConcurrentSteps, cancellationToken);

            var scoringOperation = _telemetry.StepStarted(CopilotAdoptionSteps.Scoring);
            _telemetry.Checkpoint(CopilotAdoptionTelemetryStages.ScoringStarted);
            var scoringWatch = System.Diagnostics.Stopwatch.StartNew();
            var scoringFailed = false;
            string scoringExceptionType = null;
            try
            {
                FinaliseSummary(analysis);
            }
            catch (Exception ex)
            {
                scoringFailed = true;
                scoringExceptionType = ex.GetBaseException().GetType().Name;
                throw;
            }
            finally
            {
                scoringWatch.Stop();
                summary.Diagnostics.Record(
                    CopilotAdoptionSteps.Scoring, scoringWatch.ElapsedMilliseconds, scoringFailed);
                _telemetry.StepCompleted(
                    scoringOperation,
                    CopilotAdoptionSteps.Scoring,
                    scoringWatch.ElapsedMilliseconds,
                    scoringFailed,
                    scoringExceptionType);
                _telemetry.Checkpoint(
                    CopilotAdoptionTelemetryStages.ScoringCompleted, scoringWatch.ElapsedMilliseconds);
            }

            summary.Diagnostics.TotalMs = (long)(DateTime.UtcNow - nowUtc).TotalMilliseconds;
            return analysis;
        }

        /// <summary>One independent step of the analysis, and the work that produces it.</summary>
        private sealed class AnalysisStep
        {
            public AnalysisStep(string name, Func<StepOutput, Task> work)
            {
                Name = name;
                Work = work;
                Output = new StepOutput();
            }

            public string Name { get; }
            public Func<StepOutput, Task> Work { get; }
            public StepOutput Output { get; }
            public long DurationMs { get; set; }
            public bool Failed { get; set; }
        }

        /// <summary>
        /// The shared side-effects of one step, buffered rather than written straight to the analysis.
        /// </summary>
        /// <remarks>
        /// Steps run concurrently, and <see cref="CopilotAdoptionSummary.Warnings"/>, its incomplete-data
        /// reasons and <see cref="CopilotAdoptionAnalysis.Sql"/> are a plain <c>List</c> and
        /// <c>Dictionary</c> - concurrent writers would corrupt them outright.
        /// <para>
        /// Buffering rather than locking is the deliberate choice. A lock would make the writes safe but
        /// leave their ORDER decided by whichever query happened to finish first, so the same tenant would
        /// get its caveats in a different order on every load. The workbook export exists to be run before
        /// and after an enablement programme and diffed, and that comparison must not fill up with
        /// reordering noise. Merging these buffers in step order instead reproduces exactly the order the
        /// old sequential implementation produced.
        /// </para>
        /// <para>
        /// Everything else a step writes is a property it alone owns (its own rows, its own chart, its own
        /// counters), so those stay direct writes to the analysis.
        /// </para>
        /// </remarks>
        private sealed class StepOutput
        {
            /// <summary>Warnings raised by this step, in the order it raised them.</summary>
            public List<string> Warnings { get; } = new List<string>();

            /// <summary>The queries this step ran, for the SQL tab.</summary>
            public Dictionary<string, string> Sql { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            /// <summary>Datasets this step could not complete. Merged through MarkFiguresIncomplete.</summary>
            public List<string> IncompleteReasons { get; } = new List<string>();

            /// <summary>
            /// Whether any query in this step failed or timed out.
            /// </summary>
            /// <remarks>
            /// Recorded separately from the warnings because a step's warnings are not all failures -
            /// "the agent inventory was capped" is informational - and because SafeAsync turns a failed
            /// query into a warning and returns null, which means the step's own try/catch never sees an
            /// exception. Without this, a query that timed out was reported in the diagnostics as having
            /// succeeded, indistinguishable from one that was simply fast. That is not academic: during
            /// development a syntactically broken query showed up in the performance harness as a step
            /// that got 6.5x faster, because it failed instantly and the failure was invisible.
            /// </remarks>
            public bool QueryFailed { get; private set; }

            /// <summary>Records that a query in this step failed. See <see cref="QueryFailed"/>.</summary>
            public void MarkQueryFailed()
            {
                QueryFailed = true;
            }

            /// <summary>Buffered equivalent of <see cref="CopilotAdoptionSummary.MarkFiguresIncomplete"/>.</summary>
            public void MarkIncomplete(string dataset)
            {
                if (!string.IsNullOrWhiteSpace(dataset) && !IncompleteReasons.Contains(dataset))
                {
                    IncompleteReasons.Add(dataset);
                }
            }
        }

        /// <summary>
        /// Runs the independent steps concurrently, then merges their buffered side-effects in step order.
        /// </summary>
        /// <remarks>
        /// Bounded by <see cref="MaxConcurrentSteps"/>. Unbounded concurrency would fire every heavy query
        /// at the database at once, which on a tier-capped Azure SQL database is a good way to turn one
        /// slow report into a whole-database stall that also hits the importer and every other page.
        /// <para>
        /// The merge happens even when a step throws. Only a cancellation or an outright bug gets this far
        /// (a query failure is already degraded to a warning by SafeAsync), but if one step faults the
        /// warnings the others produced are still the honest description of what was gathered.
        /// </para>
        /// </remarks>
        private async Task RunStepsAsync(
            CopilotAdoptionAnalysis analysis, List<AnalysisStep> steps, int maxConcurrency, CancellationToken cancellationToken)
        {
            if (steps.Count == 0) return;

            using (var gate = new SemaphoreSlim(Math.Min(Math.Max(1, maxConcurrency), steps.Count)))
            {
                var running = steps.Select(step => RunOneStepAsync(step, gate, cancellationToken)).ToArray();

                try
                {
                    await Task.WhenAll(running);
                }
                finally
                {
                    // In step order, NOT completion order - see StepOutput.
                    foreach (var step in steps)
                    {
                        analysis.Summary.Diagnostics.Record(step.Name, step.DurationMs, step.Failed);

                        foreach (var warning in step.Output.Warnings)
                        {
                            analysis.Summary.Warnings.Add(warning);
                        }

                        foreach (var reason in step.Output.IncompleteReasons)
                        {
                            analysis.Summary.MarkFiguresIncomplete(reason);
                        }

                        foreach (var entry in step.Output.Sql)
                        {
                            analysis.Sql[entry.Key] = entry.Value;
                        }
                    }
                }
            }
        }

        /// <summary>Runs and times one step, holding a slot in the concurrency gate while it works.</summary>
        private async Task RunOneStepAsync(
            AnalysisStep step, SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            var operationId = _telemetry.StepStarted(step.Name);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string exceptionType = null;
            try
            {
                await step.Work(step.Output);
            }
            catch (Exception ex)
            {
                step.Failed = true;
                exceptionType = ex.GetBaseException().GetType().Name;
                throw;
            }
            finally
            {
                watch.Stop();
                step.DurationMs = watch.ElapsedMilliseconds;

                // A query that failed or timed out was degraded to a warning by SafeAsync rather than
                // thrown, so the catch above never fires for it. Without this the diagnostics would
                // report the step as successful - see StepOutput.QueryFailed.
                if (step.Output.QueryFailed) step.Failed = true;

                _telemetry.StepCompleted(
                    operationId,
                    step.Name,
                    step.DurationMs,
                    step.Failed,
                    exceptionType);
                gate.Release();
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
            StepOutput output,
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
            output.Sql["agents"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<AgentUsageQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.AgentEstate,
                CopilotAdoptionQueries.AgentUsage,
                output,
                "Copilot agent usage", cancellationToken);

            if (rows == null) return;

            analysis.Agents = rows
                .Select(r => CopilotAdoptionScoring.ScoreAgent(r, nowUtc, _options))
                .OrderByDescending(a => a.Interactions)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (analysis.Agents.Count >= _options.MaxAgents)
            {
                output.Warnings.Add(
                    $"The agent inventory was capped at {_options.MaxAgents} agents, so the agent figures are "
                    + "a floor rather than a total.");
            }

            var byDeptSql = CopilotAdoptionSql.AgentUsageByDepartmentSql();
            var byDeptParameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            output.Sql["agentsByDepartment"] = CopilotAdoptionSql.ForDisplay(byDeptSql, byDeptParameters);

            var byDept = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(byDeptSql, cancellationToken, ToSqlParameters(byDeptParameters)),
                CopilotAdoptionSteps.AgentEstate,
                CopilotAdoptionQueries.AgentUsageByDepartment,
                output,
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
            StepOutput output,
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
            output.Sql["unlicensedUsage"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<UnlicensedUsageQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.UnlicensedPopulation,
                CopilotAdoptionQueries.UnlicensedUsage,
                output,
                "unlicensed Copilot usage", cancellationToken);

            if (rows != null)
            {
                foreach (var row in rows)
                {
                    row.EmailDomain = CopilotAdoptionEmailDomain.From(row.UserPrincipalName);
                }

                analysis.UnlicensedUsers = rows;
                summary.Unlicensed.Truncated = rows.Count >= _options.MaxUnlicensedUsersScored;

                if (summary.Unlicensed.Truncated)
                {
                    output.Warnings.Add(
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
            output.Sql["unlicensedUsageByApp"] = CopilotAdoptionSql.ForDisplay(appSql, appParameters);

            var apps = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(appSql, cancellationToken, ToSqlParameters(appParameters)),
                CopilotAdoptionSteps.UnlicensedPopulation,
                CopilotAdoptionQueries.UnlicensedUsageByApp,
                output,
                "unlicensed Copilot usage by app", cancellationToken);

            if (apps != null)
            {
                summary.Unlicensed.UsageByApp = apps
                    .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                    .ToList();
            }
        }

        /// <summary>
        /// How Microsoft's audit log typed the resources Copilot referenced when answering, with each
        /// value classified by what it actually describes.
        ///
        /// The classification is the point: <c>AccessedResources[].Type</c> mixes file kinds, Graph
        /// entity names, how the resource was used (CITATION) and grounding from outside the tenant
        /// (WebSearchQuery) in one field, so charting the raw values as "kinds of tenant content" was
        /// wrong for the largest bucket. See <see cref="CopilotAccessedResourceTaxonomy"/> and #468.
        /// </summary>
        private async Task BuildResourceTypesAsync(
            CopilotAdoptionAnalysis analysis,
            StepOutput output,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var sql = CopilotAdoptionSql.TopResourceTypesSql();
            var parameters = new Dictionary<string, object>
            {
                { "@from", windowStart },
                { "@top", _options.TopSegments },
            };
            output.Sql["resourceTypes"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.ResourceTypes,
                CopilotAdoptionQueries.ResourceTypes,
                output,
                "Copilot accessed resource types", cancellationToken);

            if (rows == null) return;

            analysis.Summary.TopResourceTypes = rows
                .Select(r => new AdoptionResourceTypeRow
                {
                    Label = r.Label,
                    Value = r.Value,
                    // The query already substituted its own label for a missing type, which the
                    // taxonomy does not recognise and so classifies as Unclassified - which is what a
                    // reference with no type is.
                    Kind = CopilotAccessedResourceTaxonomy.Classify(r.Label),
                })
                .ToList();
        }

        #endregion

        #region Licensed users

        private async Task BuildLicensedUsersAsync(
            CopilotAdoptionAnalysis analysis,
            StepOutput output,
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
            output.Sql["seatAssignments"] = assignmentsSql;

            var assignments = await SafeAsync(
                () => QueryAsync<SeatAssignmentRow>(assignmentsSql, cancellationToken),
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.SeatAssignments,
                output,
                "Copilot licence assignments", cancellationToken);

            if (assignments == null)
            {
                // The seat count comes from here. Falling through with an empty list would report zero
                // licensed users - indistinguishable from a tenant that genuinely has none.
                output.MarkIncomplete("Copilot licence assignments");
                assignments = new List<SeatAssignmentRow>();
            }

            var assignmentsByUser = assignments
                .GroupBy(a => a.UserId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var licencesByUser = assignmentsByUser
                .ToDictionary(
                    g => g.Key,
                    g => string.Join(", ", g.Value.Select(a => a.LicenceName)
                                            .Where(n => !string.IsNullOrWhiteSpace(n))
                                            .Distinct()
                                            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)));

            summary.LicensedUsers = licencesByUser.Count;

            var includeReport = summary.DataSources.CopilotUsageReportDate.HasValue
                                && !summary.DataSources.CopilotUsageReportObfuscated;

            var coworkAgentIds = await SafeAsync(
                () => QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken),
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.CoworkAgentLookup,
                output,
                "Cowork agent lookup", cancellationToken) ?? new List<IntValueRow>();

            var coworkIds = coworkAgentIds.Select(r => r.Value).ToList();

            var includeCoworkReport = summary.DataSources.CoworkUsageReportDate.HasValue;

            var detailSql = CopilotAdoptionSql.LicensedUsersSql(seatIds, coworkIds, includeReport, includeCoworkReport);
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
            if (includeCoworkReport)
            {
                parameters["@coworkReportDate"] = summary.DataSources.CoworkUsageReportDate.Value;
                parameters["@coworkReportPeriodDays"] = summary.DataSources.CoworkUsageReportPeriodDays;
            }

            output.Sql["licensedUsers"] = CopilotAdoptionSql.ForDisplay(detailSql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<LicensedUserUsageRow>(detailSql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.LicensedUsers,
                CopilotAdoptionQueries.LicensedUserDetail,
                output,
                "licensed user detail", cancellationToken);

            if (rows == null)
            {
                // Every rate, funnel stage, band and segment on the page is computed from this list. An
                // empty one renders as "nobody uses Copilot", which is the most damaging thing this tool
                // could say incorrectly.
                output.MarkIncomplete("licensed user detail");
                return;
            }

            if (rows.Count >= _options.MaxLicensedUsersScored)
            {
                output.Warnings.Add(
                    $"Only the first {_options.MaxLicensedUsersScored:N0} licensed users were analysed. "
                    + "The figures below therefore describe that subset, not the whole tenant. The subset is "
                    + "ordered by internal user id for reproducibility, so the oldest user records are "
                    + "over-represented and the newest user records are excluded first.");
            }

            foreach (var row in rows)
            {
                string licences;
                row.SeatLicences = licencesByUser.TryGetValue(row.UserId, out licences) ? licences : null;
                if (assignmentsByUser.TryGetValue(row.UserId, out var userAssignments))
                {
                    row.SeatLicenceTypeIds = userAssignments
                        .Select(a => a.LicenceTypeId)
                        .Distinct()
                        .ToList();
                }
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
            StepOutput output,
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
            output.Sql["usageByApp"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<CategoryQueryRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.UsageByApp,
                CopilotAdoptionQueries.LicensedUsageByApp,
                output,
                "Copilot usage by app", cancellationToken);

            if (rows == null) return;

            analysis.Summary.UsageByApp = rows
                .Select(r => new AdoptionCategory { Label = r.Label, Value = r.Value })
                .ToList();
        }

        private async Task BuildWeeklyTrendAsync(
            CopilotAdoptionAnalysis analysis,
            StepOutput output,
            List<int> seatIds,
            DateTime trendStart,
            DateTime trendEndExclusive,
            CancellationToken cancellationToken)
        {
            if (!analysis.Summary.DataSources.AuditAvailable)
            {
                return;
            }

            var coworkAgentIds = await SafeAsync(
                () => QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken),
                CopilotAdoptionSteps.WeeklyTrend,
                CopilotAdoptionQueries.CoworkAgentLookup,
                output,
                "Cowork agent lookup", cancellationToken) ?? new List<IntValueRow>();

            var sql = CopilotAdoptionSql.WeeklyAdoptionTrendSql(seatIds, coworkAgentIds.Select(r => r.Value));
            var parameters = new Dictionary<string, object>
            {
                { "@trendFrom", trendStart },
                { "@trendTo", trendEndExclusive },
            };
            output.Sql["weeklyTrend"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<NamedWeekRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.WeeklyTrend,
                CopilotAdoptionQueries.WeeklyTrend,
                output,
                "weekly adoption trend", cancellationToken);

            if (rows == null) return;

            var coverageSql = CopilotAdoptionSql.WeeklyCopilotAuditCoverageSql;
            output.Sql["weeklyTrendCoverage"] = CopilotAdoptionSql.ForDisplay(coverageSql, parameters);
            var coverageRows = await SafeAsync(
                () => QueryAsync<WeekCoverageRow>(coverageSql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.WeeklyTrend,
                CopilotAdoptionQueries.WeeklyTrendCoverage,
                output,
                "weekly Copilot audit coverage", cancellationToken);

            if (coverageRows == null)
            {
                output.MarkIncomplete("weekly Copilot audit coverage");
                coverageRows = new List<WeekCoverageRow>();
            }

            var weekSpine = ClipLeadingUnverifiedWeeks(
                CompletedWeekSpine(trendStart, trendEndExclusive),
                coverageRows.Select(r => r.WeekStart.Date),
                rows);
            var coveredWeeks = coverageRows.Select(r => r.WeekStart.Date);

            var series = rows
                .GroupBy(r => r.SeriesName)
                .OrderBy(g => g.Key)
                .Select(g => new AdoptionSeries
                {
                    Name = g.Key,
                    Points = FillWeeks(weekSpine, g.ToList(), coveredWeeks),
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
            StepOutput output,
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
                output.Sql["unlicensedActiveUsers"] = CopilotAdoptionSql.ForDisplay(
                    unlicensedSql, new Dictionary<string, object> { { "@from", windowStart } });

                summary.UnlicensedActiveUsers = await SafeScalarAsync(
                    unlicensedSql,
                    CopilotAdoptionSteps.LicenceOpportunities,
                    CopilotAdoptionQueries.UnlicensedActiveUsers,
                    output,
                    "unlicensed Copilot users",
                    // Published as a headline KPI, and zero is a meaningful answer here - so a failure that
                    // reads as zero is indistinguishable from "nobody uses Copilot without a licence".
                    () => output.MarkIncomplete("unlicensed Copilot users"),
                    cancellationToken,
                    new SqlParameter("@from", windowStart));
            }

            var includeAudit = summary.DataSources.AuditAvailable;
            var includeM365 = summary.DataSources.M365UsageReportsAvailable;
            var includeCoworkReport = summary.DataSources.CoworkUsageReportAvailable;

            if (!includeAudit && !includeM365 && !includeCoworkReport)
            {
                output.Warnings.Add(
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

            output.Sql["licenceOpportunities"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<UnlicensedUserSignalRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.LicenceOpportunities,
                CopilotAdoptionQueries.LicenceOpportunities,
                output,
                "licence opportunities", cancellationToken);

            if (rows == null)
            {
                // RecommendedForLicence is published as a headline KPI and the opportunity list drives a
                // whole tab and a CSV export. This is one of the queries that times out at the median on a
                // large tenant, so a silent empty list here reads as "nobody is worth a licence".
                output.MarkIncomplete("licence opportunities");
                return;
            }

            if (!includeM365)
            {
                output.Warnings.Add(
                    "The Microsoft 365 usage reports are not available, so licence candidates are ranked only "
                    + "on unlicensed Copilot Chat use. Heavy Microsoft 365 users who have never tried Copilot "
                    + "will not appear.");
            }

            analysis.Opportunities = rows
                .Select(r => CopilotAdoptionScoring.ScoreOpportunity(r, _options))
                // Proven demand first. The SQL deliberately sorts proven-demand candidates into the
                // TOP (@maxRows) window ahead of merely busy users so the cap cannot truncate them;
                // ordering on score alone here would quietly undo that in the list the reader sees,
                // ranking somebody who has never opened Copilot above somebody already using it.
                .OrderBy(r => r.QualificationTier == CopilotAdoptionScoring.OpportunityTiers.ProvenDemand ? 0 : 1)
                .ThenByDescending(r => r.OpportunityScore)
                .ThenBy(r => r.UserPrincipalName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        #endregion

        #region Cowork readiness

        /// <summary>
        /// Scores every Copilot seat holder for Cowork readiness.
        ///
        /// <para>Runs concurrently with the other heavy steps and writes only to its own part of the
        /// result, so a failure here costs the Cowork tab and nothing else. That matters more than usual:
        /// this step depends on the Microsoft 365 usage reports, which are a separate import from the
        /// Copilot audit log and routinely absent on a new installation.</para>
        ///
        /// <para>The Copilot engagement score is joined in from the already-scored licensed users rather
        /// than recomputed, so the two tabs can never disagree about whether someone is fluent. That does
        /// make this step depend on <see cref="BuildLicensedUsersAsync"/> having populated
        /// <see cref="CopilotAdoptionAnalysis.LicensedUsers"/> - which is why the join happens in
        /// <see cref="FinaliseCowork"/> during scoring, after every step has completed, rather than here
        /// while they are still running in parallel.</para>
        /// </summary>
        private async Task BuildCoworkReadinessAsync(
            CopilotAdoptionAnalysis analysis,
            StepOutput output,
            List<int> seatIds,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;
            var includeAudit = summary.DataSources.AuditAvailable;
            var includeM365 = summary.DataSources.M365UsageReportsAvailable;
            var includeCoworkReport = summary.DataSources.CoworkUsageReportAvailable;

            if (!includeAudit && !includeM365 && !includeCoworkReport)
            {
                output.Warnings.Add(
                    "Cowork readiness needs the Cowork usage report, the Copilot audit import or the Microsoft 365 usage "
                    + "reports. None has data for this period, so no readiness assessment is possible.");
                return;
            }

            var coworkAgentIds = new List<IntValueRow>();
            if (includeAudit)
            {
                coworkAgentIds = await SafeAsync(
                    () => QueryAsync<IntValueRow>(CopilotAdoptionSql.CoworkAgentIdsSql, cancellationToken),
                    CopilotAdoptionSteps.CoworkReadiness,
                    CopilotAdoptionQueries.CoworkAgentLookup,
                    output,
                    "Cowork agent lookup", cancellationToken);

                if (coworkAgentIds == null)
                {
                    output.MarkIncomplete("Cowork agent lookup");
                    coworkAgentIds = new List<IntValueRow>();
                }
            }

            var agentIds = coworkAgentIds.Select(r => r.Value).ToList();

            var sql = CopilotAdoptionSql.CoworkReadinessSql(
                seatIds, agentIds, _options, includeAudit, includeM365, includeCoworkReport);

            var parameters = new Dictionary<string, object>
            {
                { "@maxRows", _options.MaxCoworkUsersScored },
            };
            if (includeAudit) parameters["@from"] = windowStart;
            if (includeM365)
            {
                // Date-only columns, so the lower bound is the window's first calendar day rather than the
                // timestamp - otherwise the earliest day of the window is silently dropped.
                parameters["@m365From"] = windowStart.Date;
                parameters["@m365ReportDate"] = summary.DataSources.M365UsageReportDate.Value;
            }
            if (includeCoworkReport)
            {
                parameters["@coworkReportDate"] = summary.DataSources.CoworkUsageReportDate.Value;
                parameters["@coworkReportPeriodDays"] = summary.DataSources.CoworkUsageReportPeriodDays;
            }

            output.Sql["coworkReadiness"] = CopilotAdoptionSql.ForDisplay(sql, parameters);

            var rows = await SafeAsync(
                () => QueryAsync<CoworkReadinessSignalRow>(sql, cancellationToken, ToSqlParameters(parameters)),
                CopilotAdoptionSteps.CoworkReadiness,
                CopilotAdoptionQueries.CoworkReadiness,
                output,
                "Cowork readiness", cancellationToken);

            if (rows == null)
            {
                // The prime-candidate count is a headline KPI and this list drives a whole tab and its CSV
                // export. An empty list here would read as "nobody is a candidate for Cowork", which is a
                // finding rather than a fault.
                output.MarkIncomplete("Cowork readiness");
                return;
            }

            foreach (var row in rows)
            {
                row.EmailDomain = CopilotAdoptionEmailDomain.From(row.UserPrincipalName, row.Mail);
            }

            if (!includeM365)
            {
                output.Warnings.Add(
                    "The Microsoft 365 usage reports are not available, so coordination load cannot be "
                    + "measured. Everyone will score zero on that axis and no one will be identified as a "
                    + "Cowork candidate. Enable the Microsoft 365 usage report import to use this tab.");
            }

            if (!includeCoworkReport)
            {
                output.Warnings.Add(
                    "The first-party Cowork usage report is not available, so Cowork task counts, automation ratio "
                    + "and retention cannot be measured. Audit-derived Cowork interactions are retained only as a reconciliation signal.");
            }

            if (!includeAudit)
            {
                output.Warnings.Add(
                    "The Copilot audit import has no data for this period, so Cowork audit interactions cannot be "
                    + "reconciled against Microsoft's Cowork task report.");
            }

            // Credits are a decoration on this tab, not a load-bearing figure, and they come from a
            // SEPARATE import whose tables a database predating the agent-cost migration does not have
            // at all. Probe first: without this, every Cowork analysis on such a database would raise
            // "Invalid object name" as a warning, which reads as a fault rather than as an import that
            // was never enabled - and a page whose warnings cry wolf stops being read.
            var hasCreditTables = await SafeScalarAsync(
                CopilotAdoptionSql.HasCreditTablesSql,
                CopilotAdoptionSteps.CoworkReadiness,
                CopilotAdoptionQueries.CoworkCreditProbe,
                output,
                "Copilot Credit table probe",
                // No MarkIncomplete: an absent optional import does not make the readiness figures wrong.
                null,
                cancellationToken) == 1;

            if (hasCreditTables)
            {
                await AddCoworkCreditsAsync(analysis, output, rows, seatIds, windowStart, cancellationToken);
            }

            // Stored raw. Scoring happens in FinaliseCowork, once the licensed-user step has finished and
            // the engagement scores are available to join against - the same raw-then-finalise shape the
            // unlicensed population already uses.
            analysis.CoworkSignals = rows;
        }

        /// <summary>
        /// Attaches the optional Copilot Credit figures: the tenant's pool position, and each user's
        /// total where the per-user import has rows for them.
        ///
        /// <para><b>Neither figure is Cowork-specific and neither is labelled as such.</b> Microsoft
        /// meters Cowork against the shared Copilot Credits pool with no per-row workload discriminator,
        /// so a Cowork-only figure cannot be produced - see
        /// <see cref="Common.Entities.Entities.AgentCosts.CopilotStudioHarnessClassifier"/>.</para>
        /// </summary>
        private async Task AddCoworkCreditsAsync(
            CopilotAdoptionAnalysis analysis,
            StepOutput output,
            List<CoworkReadinessSignalRow> rows,
            List<int> seatIds,
            DateTime windowStart,
            CancellationToken cancellationToken)
        {
            var summary = analysis.Summary;

            var creditsSql = CopilotAdoptionSql.CoworkUserCreditsSql(seatIds);
            var creditRows = await SafeAsync(
                () => QueryAsync<UserCreditRow>(
                    creditsSql, cancellationToken, new SqlParameter("@from", windowStart)),
                CopilotAdoptionSteps.CoworkReadiness,
                CopilotAdoptionQueries.CoworkUserCredits,
                output,
                "Cowork per-user credits", cancellationToken);

            if (creditRows != null && creditRows.Count > 0)
            {
                output.Sql["coworkUserCredits"] = CopilotAdoptionSql.ForDisplay(
                    creditsSql, new Dictionary<string, object> { { "@from", windowStart } });

                var creditsByUser = new Dictionary<int, decimal>();
                foreach (var credit in creditRows)
                {
                    creditsByUser[credit.UserId] = credit.BilledCredits;
                }

                foreach (var row in rows)
                {
                    if (creditsByUser.TryGetValue(row.UserId, out var credits))
                    {
                        row.TotalCopilotCredits = credits;
                    }
                }

                summary.CoworkCreditPosition.PerUserCreditsAvailable = true;
            }

            var capacity = await SafeAsync(
                () => QueryAsync<CreditCapacityRow>(
                    CopilotAdoptionSql.CoworkCreditCapacitySql, cancellationToken),
                CopilotAdoptionSteps.CoworkReadiness,
                CopilotAdoptionQueries.CoworkCreditCapacity,
                output,
                "Copilot Credit capacity", cancellationToken);

            var snapshot = capacity?.FirstOrDefault();
            if (snapshot != null)
            {
                var position = summary.CoworkCreditPosition;
                position.Available = true;
                position.SnapshotUtc = snapshot.SnapshotUtc;
                position.Entitled = snapshot.Entitled;
                position.Consumed = snapshot.Consumed;
                position.AvailableCredits = snapshot.AvailableCredits;
                position.PayAsYouGoConsumed = snapshot.PayAsYouGoConsumed;
                position.Status = snapshot.Status;
            }
        }

        #endregion

        #region Summary assembly

        /// <summary>
        /// Turns the scored rows into the headline figures and breakdown charts. Pure - no database -
        /// so the whole executive view can be unit-tested from hand-written user rows.
        /// </summary>
        public void FinaliseSummary(CopilotAdoptionAnalysis analysis)        {
            if (analysis == null) throw new ArgumentNullException(nameof(analysis));

            var summary = analysis.Summary;
            var users = analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>();

            // Everything raised up to this point describes a DATA SOURCE - a failed query, a capped
            // result set, a missing import - and stays true of any subset of the population. Everything
            // this method adds describes the population itself. Splitting them here is what lets a
            // domain-scoped view inherit the first set and recompute the second for its own numbers,
            // instead of either losing "the audit import is behind" or quoting the tenant-wide count of
            // report-sourced users next to one subsidiary's figures.
            summary.SourceWarnings = new List<string>(summary.Warnings);

            SummarisePurchasedSeatCapacity(summary);
            summary.GuidanceCatalogueVersion = CopilotAdoptionGuidanceCatalogue.Version;
            summary.GuidanceLinks = CopilotAdoptionGuidanceCatalogue.All.ToList();

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
            var reportSourcedUsers = users
                .Where(IsUsageReportSourced)
                .ToList();
            var auditInteractionUsers = users
                .Where(u => !IsUsageReportSourced(u))
                .ToList();
            var analysisWindowDays = summary.WindowDays > 0 ? summary.WindowDays : _options.WindowDays;
            summary.UsageReportSourcedUsers = reportSourcedUsers.Count;
            summary.UsageReportSourcedUserPct = CopilotAdoptionScoring.Percentage(reportSourcedUsers.Count, denominator);
            summary.UsageReportWindowMismatch = reportSourcedUsers.Count > 0
                && summary.DataSources.CopilotUsageReportPeriodDays > 0
                && summary.DataSources.CopilotUsageReportPeriodDays != analysisWindowDays;

            if (summary.ScoredUsers > 0 && summary.ScoredUsers < summary.LicensedUsers)
            {
                summary.Warnings.Add(
                    $"This tenant holds {summary.LicensedUsers:N0} Copilot licences, but only {summary.ScoredUsers:N0} "
                    + "users could be analysed in one pass. Every rate and breakdown below describes those "
                    + $"{summary.ScoredUsers:N0} users, not the whole tenant - they are not tenant-wide figures "
                    + "and must not be quoted as such. Because the drill-down query is ordered by internal user id, "
                    + "the oldest user records are over-represented and the newest joiners or newly onboarded "
                    + "subsidiaries are excluded first; the subset is reproducible, but not representative.");
            }

            if (summary.UsageReportSourcedUsers > 0)
            {
                summary.Warnings.Add(
                    $"{summary.UsageReportSourcedUsers:N0} licensed user{(summary.UsageReportSourcedUsers == 1 ? string.Empty : "s")} "
                    + $"({summary.UsageReportSourcedUserPct:N1}%) were scored from Microsoft's Copilot usage report because "
                    + "the audit import had no per-user signal for them. Their Microsoft prompt counts are not added to "
                    + "audit interaction totals, concentration, intensity or licensed/unlicensed interaction comparisons.");
            }

            if (summary.UsageReportWindowMismatch)
            {
                summary.Warnings.Add(
                    $"Microsoft's pinned Copilot usage-report period is D{summary.DataSources.CopilotUsageReportPeriodDays}, "
                    + $"but this analysis window is D{analysisWindowDays}. Report-sourced rows are kept in the adoption "
                    + "population so active people are not marked as never used, but a report-sourced row that would "
                    + "otherwise be a PROBABLE reclaim is excluded from reclaimable-seat totals rather than normalising "
                    + "prompt counts across unlike windows. Certain (disabled-account) seats are never held back this "
                    + "way, because a disabled account is not an inference from an absence of use. The band breakdown "
                    + "therefore counts more idle seats than the reclaim figure does; the difference is reported as "
                    + "\"held back for window mismatch\".");
            }

            summary.ActiveUsers = users.Count(u => u.Band > AdoptionBand.Dormant);
            summary.NeverUsedUsers = users.Count(u => u.Band == AdoptionBand.NeverUsed);
            summary.DormantUsers = users.Count(u => u.Band == AdoptionBand.Dormant);
            summary.HabitualUsers = users.Count(u => CopilotAdoptionScoring.IsHabitual(u.Band));
            // ----- Reclaim: tiers first, then the two things held back from the headline -----
            //
            // Written for the combination, not taken from either side. Two independent mechanisms now
            // keep a seat out of "Reclaimable licences", and they compose:
            //
            //   * confidence tiering  - certain + probable only; review and excluded are parked
            //   * window mismatch     - a row scored from Microsoft's report over a period that is not
            //                           the selected window cannot justify taking a licence away
            //
            // Both hold-backs are published, because a headline that quietly disagrees with the band
            // breakdown loses a licence argument however good the reason behind it. The identity that
            // must hold on screen - asserted by CopilotAdoptionTests - is:
            //
            //   NeverUsed + Dormant + ReclaimSeatsFromActiveBands
            //     == ReclaimableSeats + ReclaimSeatsHeldBackForWindowMismatch + ReclaimSeatsHeldBackForReview
            //
            // The left-hand extra term is there because "certain" is not a subset of the idle bands: a
            // disabled account that was active right up to the day it was disabled is the clearest
            // reclaim there is, and it is not in NeverUsed + Dormant.
            summary.DisabledLicensedUsers = users.Count(u => u.AccountEnabled == false);
            summary.ReclaimCertainSeats = users.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain));
            summary.ReclaimProbableSeats = users.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable));
            summary.ReclaimReviewSeats = users.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Review));
            summary.ReclaimExcludedUsers = users.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded));
            summary.ExpiredReclaimExclusions = users.Count(u => u.ReclaimExclusionExpired);
            summary.TooNewToJudgeUsers = users.Count(u => u.TooNewToJudge);

            var reclaimCandidates = users
                .Where(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain)
                         || IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable))
                .ToList();

            // The window-mismatch hold-back only applies to PROBABLE seats. Probable is an inference
            // from an absence of recorded use, and an absence measured over Microsoft's period rather
            // than the selected one is not evidence about the selected one. Certain is not an
            // inference at all - the account is disabled - so a report-period technicality must never
            // remove a disabled seat from the reclaim total. Before this was restricted, the mismatch
            // held back exactly the wrong rows: see the note on IsUsageReportSourced.
            var heldBackForWindowMismatch = summary.UsageReportWindowMismatch
                ? reclaimCandidates.Count(u =>
                    IsUsageReportSourced(u)
                    && IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable))
                : 0;

            summary.ReclaimSeatsHeldBackForWindowMismatch = heldBackForWindowMismatch;
            summary.ReclaimableSeats = users.Count(u => CountsAsReclaimableSeat(u, summary.UsageReportWindowMismatch));
            summary.ReclaimSeatsFromActiveBands = reclaimCandidates.Count(u => !IsIdleBand(u.Band));
            summary.ReclaimSeatsHeldBackForReview = users.Count(u =>
                IsIdleBand(u.Band)
                && (IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Review)
                    || IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded)));

            summary.ReclaimCaveat = "Reclaim excludes admin exclusions and separates review-only cases. Leave, part-time patterns, service/shared accounts and role-based mailboxes are not detectable from Microsoft 365 usage data.";
            PopulateAssignedIdleBySku(summary, users);
            // Report-sourced rows carry Microsoft's prompt count in Interactions. Do not publish a total
            // that adds prompts to audit-log interactions; they are different units over potentially
            // different windows.
            summary.TotalInteractions = auditInteractionUsers.Sum(u => u.Interactions);

            summary.AdoptionRatePct = CopilotAdoptionScoring.Percentage(summary.ActiveUsers, denominator);
            summary.HabitRatePct = CopilotAdoptionScoring.Percentage(summary.HabitualUsers, denominator);
            summary.AverageAdoptionScore = users.Count == 0
                ? 0
                : Math.Round(users.Average(u => u.AdoptionScore), 1, MidpointRounding.AwayFromZero);
            summary.MedianAdoptionScore = CopilotAdoptionScoring.Median(users.Select(u => u.AdoptionScore));

            summary.CoworkAuditUsers = users.Count(u => u.CoworkInteractions > 0);
            summary.CoworkInteractions = users.Sum(u => u.CoworkInteractions);
            summary.CoworkReportUsers = users.Count(u => u.CoworkReportTotalTasks.GetValueOrDefault() > 0);
            summary.CoworkReportTotalTasks = users.Sum(u => u.CoworkReportTotalTasks.GetValueOrDefault());
            summary.CoworkReportScheduledTasks = users.Sum(u => u.CoworkReportScheduledTasks.GetValueOrDefault());
            summary.CoworkReportUserInitiatedTasks = users.Sum(u => u.CoworkReportUserInitiatedTasks.GetValueOrDefault());
            summary.CoworkReportRetainedUsers = users.Any(u => u.CoworkReportRetainedUser.HasValue)
                ? (int?)users.Count(u => u.CoworkReportRetainedUser == true)
                : null;
            summary.CoworkAutomationRatioPct = summary.CoworkReportTotalTasks > 0
                ? (double?)CopilotAdoptionScoring.Percentage(summary.CoworkReportScheduledTasks, summary.CoworkReportTotalTasks)
                : null;
            summary.CoworkTasksPerActiveUser = summary.CoworkReportUsers > 0
                ? (double?)Math.Round(summary.CoworkReportTotalTasks / (double)summary.CoworkReportUsers, 1, MidpointRounding.AwayFromZero)
                : null;
            summary.CoworkReportRetentionPct = summary.CoworkReportRetainedUsers.HasValue && summary.CoworkReportUsers > 0
                ? (double?)CopilotAdoptionScoring.Percentage(summary.CoworkReportRetainedUsers.Value, summary.CoworkReportUsers)
                : null;

            // "Has a row in Microsoft's report" is not the same as "has a task count in it": the report can
            // report active days with a blank task cell, which the parser preserves as unknown rather than
            // zero. CoworkReportUsers stays tasks-only because it is the denominator of tasks-per-user and
            // retention; presence is counted separately so the headline does not deny a user the readiness
            // tab is simultaneously calling Established.
            var coworkReportSignalUsers = users.Count(u => u.CoworkReportTotalTasks.GetValueOrDefault() > 0
                || u.CoworkReportActiveDays.GetValueOrDefault() > 0);

            summary.CoworkUsers = coworkReportSignalUsers > 0 ? coworkReportSignalUsers : summary.CoworkAuditUsers;
            summary.CoworkEligibilityKnown = summary.CoworkEligibleUsers.HasValue;
            summary.CoworkAdoptionPct = summary.CoworkEligibilityKnown
                ? (double?)CopilotAdoptionScoring.Percentage(summary.CoworkUsers, summary.CoworkEligibleUsers.Value)
                : null;
            if (!summary.CoworkEligibilityKnown && summary.CoworkUsers > 0)
            {
                summary.Warnings.Add("Cowork adoption percentage is suppressed because Cowork eligibility is controlled by spending-policy scope and this import does not know that denominator. The deprecated Cowork agent entry is not used as an eligibility source.");
            }
            // Only claim a Cowork signal when Cowork was actually seen in either source. On a tenant that has
            // not been enabled for it, "0% Cowork adoption" reads as a failure rather than as "not available".
            summary.CoworkDetected = coworkReportSignalUsers > 0 || summary.CoworkInteractions > 0;

            summary.Funnel = BuildFunnel(summary, users);
            summary.BandBreakdown = BuildBandBreakdown(users);
            summary.HabitBuckets = BuildHabitBuckets(users.Select(u => (double)u.ActiveDays));
            summary.ActionPlan = BuildActionPlan(users);
            summary.Concentration = CopilotAdoptionScoring.Concentration(
                auditInteractionUsers.Where(CopilotAdoptionScoring.IsActive).Select(u => u.Interactions));
            summary.ScoreProfiles = BuildScoreProfiles(auditInteractionUsers);
            summary.AdoptionByDepartment = BuildSegments(users, u => u.Department, "(no department)", s => s.AdoptionRatePct);
            summary.HabitByDepartment = BuildSegments(users, u => u.Department, "(no department)", HabitRatePct);
            summary.AdoptionByCountry = BuildSegments(users, u => u.Country, "(no country)", s => s.AdoptionRatePct);
            summary.IntensityByDepartment = BuildIntensity(auditInteractionUsers, u => u.Department, "(no department)");
            summary.AccountabilityDimension = NormaliseAccountabilityDimension(_options.AccountabilityDimension);
            summary.AccountabilityDimensionLabel = AccountabilityDimensionLabel(summary.AccountabilityDimension);
            if (summary.AccountabilityRollup.Count == 0)
            {
                summary.AccountabilityRollup = BuildAccountabilityRollup(users, summary.AccountabilityDimension);
            }

            FinaliseAgents(analysis);
            FinaliseUnlicensed(analysis);
            FinaliseCowork(analysis);
            summary.CombinedByDepartment = BuildCombinedSegments(analysis);

            var opportunities = analysis.Opportunities ?? new List<LicenceOpportunityRow>();
            summary.RecommendedForLicence = opportunities.Count(o => o.Recommended);
            summary.OpportunityByDepartment = opportunities
                .Where(o => o.Recommended)
                .GroupBy(o => string.IsNullOrWhiteSpace(o.Department) ? "(no department)" : o.Department.Trim())
                .Select(g => new AdoptionCategory { Label = g.Key, Value = g.Count() })
                .OrderByDescending(c => c.Value)
                .Take(_options.TopSegments)
                .ToList();

            // Last, because it reads the licensed, unlicensed, opportunity and Cowork populations
            // together - the point of the domain view is that those four answer one question per
            // organisation rather than four separate ones.
            summary.EmailDomains = BuildEmailDomains(analysis);
        }

        /// <summary>
        /// Adoption per email domain - i.e. per organisation sharing this tenant.
        /// </summary>
        /// <remarks>
        /// <para>Four populations, one row each. A domain with idle seats <i>and</i> unlicensed Chat use
        /// is a seat-allocation problem inside one company; a domain with strong adoption and a queue of
        /// licence candidates is a business case. Neither is visible when the same people are split
        /// across departments that span every company in the tenant.</para>
        /// <para>Unlike the department breakdown this keeps a row for a domain that has <b>no seats at
        /// all</b> but does have unlicensed users or licence candidates, because that is the single most
        /// actionable thing this view can find: an acquired business that was never given Copilot and is
        /// using Chat anyway. The same <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> floor is
        /// applied to the combined population so one person in a domain is still not a data point.</para>
        /// </remarks>
        private List<AdoptionDomainRow> BuildEmailDomains(CopilotAdoptionAnalysis analysis)
        {
            var licensed = (analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>())
                .GroupBy(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var unlicensed = (analysis.UnlicensedUsers ?? new List<UnlicensedUsageQueryRow>())
                .GroupBy(u => CopilotAdoptionEmailDomain.Label(u.EmailDomain), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var candidates = (analysis.Opportunities ?? new List<LicenceOpportunityRow>())
                .Where(o => o.Recommended)
                .GroupBy(o => CopilotAdoptionEmailDomain.Label(o.EmailDomain), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var cowork = (analysis.CoworkReadiness ?? new List<CoworkReadinessRow>())
                .Where(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.PrimeCandidate)
                .GroupBy(r => CopilotAdoptionEmailDomain.Label(r.EmailDomain), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<AdoptionDomainRow>();

            foreach (var domain in licensed.Keys
                .Concat(unlicensed.Keys)
                .Concat(candidates.Keys)
                .Concat(cowork.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<LicensedUserAdoptionRow> seats;
                licensed.TryGetValue(domain, out seats);
                seats = seats ?? new List<LicensedUserAdoptionRow>();

                List<UnlicensedUsageQueryRow> chat;
                unlicensed.TryGetValue(domain, out chat);
                chat = chat ?? new List<UnlicensedUsageQueryRow>();

                int recommended;
                candidates.TryGetValue(domain, out recommended);

                int primeCandidates;
                cowork.TryGetValue(domain, out primeCandidates);

                if (seats.Count + chat.Count < _options.MinSeatsPerSegment) continue;

                var row = CopilotAdoptionScoring.Summarise(domain, seats);

                rows.Add(new AdoptionDomainRow
                {
                    Segment = row.Segment,
                    LicensedUsers = row.LicensedUsers,
                    ActiveUsers = row.ActiveUsers,
                    HabitualUsers = row.HabitualUsers,
                    NeverUsedUsers = row.NeverUsedUsers,
                    AdoptionRatePct = row.AdoptionRatePct,
                    AverageAdoptionScore = row.AverageAdoptionScore,

                    ReclaimableSeats = seats.Count(u =>
                        CountsAsReclaimableSeat(u, analysis.Summary.UsageReportWindowMismatch)),

                    // Per seat held, not per active seat, so this is comparable with the unlicensed
                    // column on the same row. Report-sourced rows carry Microsoft prompt counts rather
                    // than audit interactions, so they stay in the denominator but never the numerator.
                    InteractionsPerLicensedUser = PerUserPerMonth(
                        seats.Where(u => !IsUsageReportSourced(u)).Sum(u => (double)u.Interactions),
                        seats.Count),

                    UnlicensedActiveUsers = chat.Count,
                    RecommendedForLicence = recommended,
                    CoworkPrimeCandidates = primeCandidates,

                    // Guests are attributed to their home organisation, so an all-guest domain is a
                    // partner rather than part of this business. Judged on the seat holders when there
                    // are any, because they are the population every other column describes.
                    External = seats.Count > 0
                        ? seats.All(u => CopilotAdoptionEmailDomain.IsExternalGuest(u.UserPrincipalName))
                        : chat.Count > 0 && chat.All(u => CopilotAdoptionEmailDomain.IsExternalGuest(u.UserPrincipalName)),
                });
            }

            // Worst adoption first - the reading order of an enablement plan - but a domain with no
            // seats at all has no adoption rate to rank on, so those sort by how much unlicensed use
            // they represent instead. Ties break on size, so a large mediocre org outranks a tiny
            // terrible one.
            return rows
                .OrderBy(r => r.LicensedUsers == 0 ? 1 : 0)
                .ThenBy(r => r.LicensedUsers == 0 ? 0 : r.AdoptionRatePct)
                .ThenByDescending(r => r.LicensedUsers)
                .ThenByDescending(r => r.UnlicensedActiveUsers)
                .ThenBy(r => r.Segment, StringComparer.OrdinalIgnoreCase)
                .Take(_options.TopSegments)
                .ToList();
        }


        private static void PopulateAssignedIdleBySku(CopilotAdoptionSummary summary, IReadOnlyCollection<LicensedUserAdoptionRow> users)
        {
            foreach (var licence in summary.SeatLicenceTypes.Where(l => l.IsCopilotSeat))
            {
                licence.AssignedIdleUsers = users.Count(user =>
                    UserHasLicence(user, licence)
                    && CountsAsReclaimableSeat(user, summary.UsageReportWindowMismatch));
            }
        }

        /// <summary>
        /// Whether this seat counts towards a reclaim total.
        /// </summary>
        /// <remarks>
        /// The single definition of "reclaimable", used by the headline, by the per-SKU idle counts and
        /// by the per-domain breakdown - because a licence conversation goes badly when the table under
        /// the headline adds up to a different number from the headline.
        /// <para>Certain and Probable only; Review and Excluded are parked. The window-mismatch
        /// hold-back applies to PROBABLE alone: probable is an inference from an absence of recorded
        /// use, and an absence measured over Microsoft's report period rather than the selected one is
        /// not evidence about the selected one. Certain is not an inference at all - the account is
        /// disabled - so a report-period technicality must never remove a disabled seat.</para>
        /// </remarks>
        private static bool CountsAsReclaimableSeat(LicensedUserAdoptionRow user, bool usageReportWindowMismatch)
        {
            var certain = IsReclaimTier(user, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain);
            var probable = IsReclaimTier(user, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable);

            if (!certain && !probable) return false;

            return !(usageReportWindowMismatch && probable && IsUsageReportSourced(user));
        }

        internal static bool UserHasLicence(LicensedUserAdoptionRow user, LicenceTypeClassification licence)
        {
            return user.SeatLicenceTypeIds != null && user.SeatLicenceTypeIds.Contains(licence.Id);
        }

        private static void SummarisePurchasedSeatCapacity(CopilotAdoptionSummary summary)
        {
            var copilotSkus = summary.SeatLicenceTypes.Where(l => l.IsCopilotSeat).ToList();
            var hasCopilotSkus = copilotSkus.Count > 0;
            var allPurchasedKnown = hasCopilotSkus && copilotSkus.All(l => l.PurchasedUnits.HasValue);
            var allUnassignedKnown = allPurchasedKnown && copilotSkus.All(l => l.UnassignedUnits.HasValue);

            summary.SubscribedSkusAvailable = allPurchasedKnown;
            summary.PurchasedCopilotSeats = allPurchasedKnown
                ? copilotSkus.Sum(l => l.PurchasedUnits.GetValueOrDefault())
                : (int?)null;
            summary.UnassignedCopilotSeats = allUnassignedKnown
                ? copilotSkus.Sum(l => l.UnassignedUnits.GetValueOrDefault())
                : (int?)null;

            if (hasCopilotSkus && !allPurchasedKnown && !summary.Warnings.Any(w => w.Contains("subscribedSkus/prepaidUnits")))
            {
                summary.Warnings.Add(
                    "Purchased and unassigned Copilot seats are unknown because Graph subscribedSkus/prepaidUnits "
                    + "has not been imported. Grant Organization.Read.All and rerun the user metadata import; the "
                    + "report deliberately does not show zero for unassigned seats when the purchase inventory is missing.");
            }

            foreach (var licence in copilotSkus.Where(l => l.PurchasedUnits.HasValue && !l.UnassignedUnits.HasValue))
            {
                var warning = $"Purchased and assigned Copilot seats disagree for {licence.SkuPartNumber ?? licence.Name}: Graph reports {licence.PurchasedUnits.Value:N0} purchased but {licence.AssignedUsers:N0} assigned, so unassigned seats are shown as Unknown rather than zero.";
                if (!summary.Warnings.Contains(warning))
                {
                    summary.Warnings.Add(warning);
                }
            }
        }

        private static int ReclaimTierRank(string tier)
        {
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain, StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable, StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Review, StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(tier, CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded, StringComparison.OrdinalIgnoreCase)) return 3;
            return 4;
        }

        /// <summary>
        /// Whether this row's engagement was scored from Microsoft's usage report rather than from the
        /// Copilot audit import.
        /// </summary>
        /// <remarks>
        /// Worth knowing when reading the reclaim arithmetic: <c>CopilotAdoptionScoring.Score</c> only
        /// selects the report when the report has a non-zero signal, and a non-zero signal makes the
        /// user active in the window. A report-sourced row therefore can never be banded never-used or
        /// dormant, which means it can never be <c>probable</c> either. The window-mismatch hold-back is
        /// consequently defence-in-depth rather than a live filter today. It is kept because the
        /// alternative - deleting it - would silently remove the guard if the source-selection rule ever
        /// changes, and because a hold-back that is wired up and provably zero is easier to reason about
        /// than one that has to be remembered. See the deferred item in the pull request: the residual
        /// exposure is a user with NO audit signal at all while Microsoft's period is shorter than the
        /// selected window, who is currently banded never-used from audit data that does not cover them.
        /// </remarks>
        private static bool IsUsageReportSourced(LicensedUserAdoptionRow row)
        {
            return row != null
                && string.Equals(row.SignalSource, CopilotAdoptionScoring.SignalSourceUsageReport, StringComparison.Ordinal);
        }

        /// <summary>Whether a row carries the given reclaim confidence tier.</summary>
        private static bool IsReclaimTier(LicensedUserAdoptionRow row, string tier)
        {
            return row != null && string.Equals(row.ReclaimEligibility, tier, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The two bands that make up the idle-seat population the reclaim arithmetic reconciles
        /// against. Kept as one definition so the hold-back counts and the headline cannot drift apart.
        /// </summary>
        private static bool IsIdleBand(AdoptionBand band)
        {
            return band == AdoptionBand.NeverUsed || band == AdoptionBand.Dormant;
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
        /// Scores the raw Cowork signals and rolls them up into the executive view.
        ///
        /// <para>This is where the two data sources are joined: the coordination load comes from this
        /// feature's own query, while the Copilot engagement score is taken from the already-scored
        /// licensed-user list. Joining here rather than in SQL is what guarantees the Cowork tab and the
        /// Licensed users tab can never disagree about whether somebody is fluent - there is exactly one
        /// engagement calculation and both tabs read its output.</para>
        ///
        /// <para>Pure - no database - so the whole tab can be unit-tested from hand-written rows.</para>
        /// </summary>
        private void FinaliseCowork(CopilotAdoptionAnalysis analysis)
        {
            var summary = analysis.Summary;
            var signals = analysis.CoworkSignals ?? new List<CoworkReadinessSignalRow>();

            if (signals.Count == 0)
            {
                // Left explicitly unavailable rather than published as a set of zeros. "0 prime candidates"
                // is a finding; "this analysis did not run" is a fault, and the tab has to tell them apart.
                summary.CoworkReadinessAvailable = false;
                return;
            }

            // The engagement score and agent count are carried across from the licensed-user analysis
            // rather than recalculated, so the two tabs cannot disagree about the same person.
            var licensed = analysis.LicensedUsers ?? new List<LicensedUserAdoptionRow>();

            if (licensed.Count == 0)
            {
                // Cowork signals exist but the licensed-user analysis produced nothing. That combination
                // cannot occur naturally - CoworkReadinessSql semi-joins to seat holders, so signals imply
                // seat holders - which means the licensed-user step failed and SafeAsync degraded it to a
                // warning. Publishing anyway would score every one of these people at zero fluency and band
                // them "build fluency first": an unavailable input rendered as a measured verdict of "not
                // fluent enough", on the tab used to decide who gets access. Unavailable is the honest
                // answer, and it is the same call the empty-signals guard above makes.
                //
                // The warning names Cowork on purpose: the panel filters warnings on that word, so this is
                // what tells the tab's own diagnostic channel that the fault was upstream rather than the
                // missing usage-report import its unavailable card would otherwise blame.
                summary.CoworkReadinessAvailable = false;
                summary.Warnings.Add(
                    "Cowork readiness was measured, but the licensed-user analysis it takes Copilot fluency "
                    + "from did not complete, so the tab could not be scored. This is NOT a missing usage "
                    + "report import - the Cowork signals imported fine. Check the Health page and re-run.");
                return;
            }

            var scoreByUser = new Dictionary<int, LicensedUserAdoptionRow>();
            foreach (var user in licensed)
            {
                scoreByUser[user.UserId] = user;
            }

            var rows = new List<CoworkReadinessRow>(signals.Count);
            var withoutFluency = 0;
            foreach (var signal in signals)
            {
                if (scoreByUser.TryGetValue(signal.UserId, out var scored))
                {
                    signal.AdoptionScore = scored.AdoptionScore;
                    signal.AgentsUsed = scored.AgentsUsed;
                    signal.CopilotActive = CopilotAdoptionScoring.IsActive(scored);
                }
                else
                {
                    // No licensed-user row to take fluency from, so this person is scored on a DEFAULT of
                    // zero rather than a measured one. Same defect as the licensed.Count == 0 guard above,
                    // only partial: "build fluency first" is then a verdict about a missing input.
                    //
                    // It is reachable because the two queries are capped independently and rank
                    // differently - LicensedUsersSql takes TOP (@maxRows) ORDER BY u.id, CoworkReadinessSql
                    // takes TOP (@maxRows) ORDER BY existing-Cowork-use then load - so above
                    // MaxLicensedUsersScored seat holders the two row sets are different subsets of the
                    // same population, and the overlap is partial rather than total.
                    //
                    // Not marked unavailable: the matched majority is correctly scored and withholding the
                    // whole tab from the largest tenants would be a worse answer than naming the gap. The
                    // warning contains "Cowork" deliberately - the panel filters on that word.
                    withoutFluency++;
                }

                rows.Add(CopilotAdoptionScoring.ScoreCoworkReadiness(signal, _options));
            }

            if (withoutFluency > 0)
            {
                // Stated as a fact with its consequence and no remedy, exactly like the licensed-user
                // cap warning this one is downstream of. Neither cap is reachable from the portal, and
                // the excluded users cannot be pulled in by changing the period: SeatUsers is a licence
                // lookup with no date predicate, so the population and its id ordering are the same on
                // every window.
                summary.Warnings.Add(
                    $"Cowork readiness: {withoutFluency:N0} of {signals.Count:N0} seat holders were scored "
                    + "without a Copilot fluency figure, because they fall outside the "
                    + $"{_options.MaxLicensedUsersScored:N0}-row licensed-user analysis this tab joins "
                    + "against. Their fluency reads as 0 rather than as unknown, so they band lower than "
                    + "they should - most will show as \"build fluency first\". Treat the tier of those "
                    + "rows as unreliable; the rest of the tab is unaffected.");
            }

            // Ordered so the people to act on are first: recommended before not, then by the strength of
            // the case. The CSV export inherits this, so a truncated read of it is still the right people.
            analysis.CoworkReadiness = rows
                .OrderByDescending(r => r.RecommendForPolicy)
                .ThenByDescending(r => r.CoordinationLoadScore)
                .ThenByDescending(r => r.FluencyScore)
                .ThenBy(r => r.UserPrincipalName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            summary.CoworkReadinessAvailable = true;
            summary.CoworkScoredUsers = rows.Count;
            summary.CoworkEstablishedUsers =
                rows.Count(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.Established);
            summary.CoworkTriallingUsers =
                rows.Count(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.Trialling);
            summary.CoworkPrimeCandidates =
                rows.Count(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.PrimeCandidate);
            summary.CoworkBuildFluencyFirst =
                rows.Count(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.BuildFluencyFirst);
            summary.CoworkRecommendedForPolicy = rows.Count(r => r.RecommendForPolicy);

            summary.CoworkAverageCoordinationLoad = Math.Round(
                rows.Average(r => r.CoordinationLoadScore), 1, MidpointRounding.AwayFromZero);
            summary.CoworkAverageFluency = Math.Round(
                rows.Average(r => r.FluencyScore), 1, MidpointRounding.AwayFromZero);

            summary.CoworkTiers = BuildCoworkTiers(rows);
            summary.CoworkByDepartment = BuildCoworkSegments(rows);
            summary.CoworkQuadrant = BuildCoworkQuadrant(summary.CoworkByDepartment);

            summary.CoworkValueEstimate = CopilotAdoptionScoring.EstimateCoworkValue(
                rows.Where(r => r.RecommendForPolicy).ToList(), _options);
        }

        /// <summary>
        /// Every tier with its population, stated once with a count rather than repeated per row - the
        /// same reasoning as <see cref="BuildActionPlan"/>.
        /// </summary>
        private List<CoworkTierSummary> BuildCoworkTiers(IReadOnlyCollection<CoworkReadinessRow> rows)
        {
            return CopilotAdoptionScoring.AllCoworkTiers
                .Select(tier =>
                {
                    var count = rows.Count(r => r.Tier == tier);
                    return new CoworkTierSummary
                    {
                        Code = tier,
                        Label = CopilotAdoptionScoring.CoworkTierLabel(tier),
                        Basis = CopilotAdoptionScoring.CoworkTierBasis(tier),
                        Description = CopilotAdoptionScoring.CoworkTierDescription(tier, _options),
                        Users = count,
                        SharePct = CopilotAdoptionScoring.Percentage(count, rows.Count),
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Departments ranked for rollout sequencing: most prime candidates first, so the reader sees the
        /// business unit with the largest concentration of ready users at the top.
        ///
        /// Sorted on the absolute count rather than the rate on purpose. A three-person department where
        /// everyone qualifies is a 100% rate and not somewhere to start a rollout; a 200-person department
        /// at 40% is. <see cref="CopilotAdoptionOptions.MinSeatsPerSegment"/> additionally removes the
        /// segments too small to mean anything, matching the other department tables.
        /// </summary>
        private List<CoworkSegmentRow> BuildCoworkSegments(IReadOnlyCollection<CoworkReadinessRow> rows)
        {
            return rows
                .GroupBy(r => string.IsNullOrWhiteSpace(r.Department) ? "(no department)" : r.Department)
                .Where(g => g.Count() >= _options.MinSeatsPerSegment)
                .Select(g =>
                {
                    var prime = g.Count(r => r.Tier == CopilotAdoptionScoring.CoworkTiers.PrimeCandidate);
                    var regular = g.Count(r => r.RegularCoworkUser);
                    var totalTasks = g.Sum(r => r.CoworkReportTotalTasks.GetValueOrDefault());
                    var scheduledTasks = g.Sum(r => r.CoworkReportScheduledTasks.GetValueOrDefault());
                    var retained = g.Any(r => r.CoworkReportRetainedUser.HasValue)
                        ? (int?)g.Count(r => r.CoworkReportRetainedUser == true)
                        : null;
                    var reportUsers = g.Count(r => r.CoworkReportTotalTasks.GetValueOrDefault() > 0);

                    return new CoworkSegmentRow
                    {
                        Segment = g.Key,
                        LicensedUsers = g.Count(),
                        PrimeCandidates = prime,
                        PrimeCandidateRatePct = CopilotAdoptionScoring.Percentage(prime, g.Count()),
                        RegularCoworkUsers = regular,
                        CoworkReportTotalTasks = totalTasks,
                        CoworkReportScheduledTasks = scheduledTasks,
                        CoworkAutomationRatioPct = totalTasks > 0
                            ? (double?)CopilotAdoptionScoring.Percentage(scheduledTasks, totalTasks)
                            : null,
                        CoworkReportRetainedUsers = retained,
                        CoworkReportRetentionPct = retained.HasValue && reportUsers > 0
                            ? (double?)CopilotAdoptionScoring.Percentage(retained.Value, reportUsers)
                            : null,
                        CoworkAdoptionPct = CopilotAdoptionScoring.Percentage(regular, g.Count()),
                        AverageCoordinationLoad = Math.Round(
                            g.Average(r => r.CoordinationLoadScore), 1, MidpointRounding.AwayFromZero),
                        AverageFluency = Math.Round(
                            g.Average(r => r.FluencyScore), 1, MidpointRounding.AwayFromZero),
                    };
                })
                .OrderByDescending(s => s.PrimeCandidates)
                .ThenByDescending(s => s.AverageCoordinationLoad)
                .ThenBy(s => s.Segment, StringComparer.OrdinalIgnoreCase)
                .Take(_options.TopSegments)
                .ToList();
        }

        /// <summary>
        /// The quadrant points, built from the same department rows the sequencing table shows so the
        /// chart and the table below it cannot describe different populations.
        /// </summary>
        private List<CoworkQuadrantPoint> BuildCoworkQuadrant(IEnumerable<CoworkSegmentRow> segments)
        {
            return segments
                .Select(s => new CoworkQuadrantPoint
                {
                    Segment = s.Segment,
                    LicensedUsers = s.LicensedUsers,
                    CoordinationLoadScore = s.AverageCoordinationLoad,
                    FluencyScore = s.AverageFluency,
                    RegularCoworkUsers = s.RegularCoworkUsers,
                    PrimeCandidates = s.PrimeCandidates,
                })
                .ToList();
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
                    // Report-sourced licensed rows hold Microsoft prompt counts in Interactions, not
                    // audit interaction counts, so they stay in the seat denominator but never in this
                    // numerator. Otherwise this row adds two different units and labels them as one.
                    InteractionsPerLicensedUser = PerUserPerMonth(
                        seats.Where(u => !IsUsageReportSourced(u)).Sum(u => (double)u.Interactions),
                        seats.Count),
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
                        GuidanceLinks = CopilotAdoptionGuidanceCatalogue.ForAction(code).ToList(),
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
            string emptyLabel,
            Func<AdoptionSegmentRow, double> primarySort)
        {
            return users
                .GroupBy(u => string.IsNullOrWhiteSpace(selector(u)) ? emptyLabel : selector(u).Trim())
                .Where(g => g.Count() >= _options.MinSeatsPerSegment)
                .Select(g => CopilotAdoptionScoring.Summarise(g.Key, g.ToList()))
                .OrderBy(primarySort)
                .ThenByDescending(s => s.LicensedUsers)
                .Take(_options.TopSegments)
                .ToList();
        }

        /// <summary>
        /// Aggregates the scored population by the configured accountability unit. This mirrors the
        /// existing segment suppression, but sorts by absolute opportunity rather than by rate so the
        /// biggest fixable spans surface first.
        /// </summary>
        private List<AccountabilityRollupRow> BuildAccountabilityRollup(
            IEnumerable<LicensedUserAdoptionRow> users,
            string dimension)
        {
            var resolved = NormaliseAccountabilityDimension(dimension);
            return users
                .GroupBy(u => AccountabilityKey(u, resolved))
                .Where(g => g.Count() >= _options.MinSeatsPerSegment)
                .Select(g => SummariseAccountability(g.Key, g.ToList()))
                .OrderByDescending(r => r.OpportunityUsers)
                .ThenByDescending(r => r.ReclaimableSeats)
                .ThenByDescending(r => r.NeverUsedUsers)
                .ThenBy(r => r.Segment, StringComparer.OrdinalIgnoreCase)
                .Take(_options.TopSegments)
                .ToList();
        }

        internal static AccountabilityRollupRow SummariseAccountability(
            string segment,
            IEnumerable<LicensedUserAdoptionRow> users)
        {
            var list = users as IList<LicensedUserAdoptionRow> ?? users?.ToList() ?? new List<LicensedUserAdoptionRow>();
            var baseRow = CopilotAdoptionScoring.Summarise(segment, list);
            var row = new AccountabilityRollupRow
            {
                Segment = baseRow.Segment,
                LicensedUsers = baseRow.LicensedUsers,
                ActiveUsers = baseRow.ActiveUsers,
                HabitualUsers = baseRow.HabitualUsers,
                NeverUsedUsers = baseRow.NeverUsedUsers,
                AdoptionRatePct = baseRow.AdoptionRatePct,
                AverageAdoptionScore = baseRow.AverageAdoptionScore,
                ReclaimCertainSeats = list.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Certain)),
                ReclaimProbableSeats = list.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Probable)),
                ReclaimReviewSeats = list.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Review)),
                ReclaimExcludedUsers = list.Count(u => IsReclaimTier(u, CopilotAdoptionScoring.ReclaimEligibilityTiers.Excluded)),
                ReclaimUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Reclaim, StringComparison.Ordinal)),
                ReengageUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Reengage, StringComparison.Ordinal)),
                CoachUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Coach, StringComparison.Ordinal)),
                BroadenUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Broaden, StringComparison.Ordinal)),
                GrowUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Grow, StringComparison.Ordinal)),
                SustainUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Sustain, StringComparison.Ordinal)),
                AdvocateUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Advocate, StringComparison.Ordinal)),
                ReviewUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Review, StringComparison.Ordinal)),
                ExcludedUsers = list.Count(u => string.Equals(u.RecommendedActionCode, CopilotAdoptionScoring.AdoptionActionCodes.Excluded, StringComparison.Ordinal)),
            };

            row.ReclaimableSeats = row.ReclaimCertainSeats + row.ReclaimProbableSeats;
            row.OpportunityUsers = row.ReclaimUsers + row.ReengageUsers + row.CoachUsers
                + row.BroadenUsers + row.GrowUsers + row.ReviewUsers;
            return row;
        }

        internal static string NormaliseAccountabilityDimension(string dimension)
        {
            switch ((dimension ?? string.Empty).Trim())
            {
                case CopilotAdoptionAccountabilityDimensions.Department:
                    return CopilotAdoptionAccountabilityDimensions.Department;
                case CopilotAdoptionAccountabilityDimensions.Country:
                    return CopilotAdoptionAccountabilityDimensions.Country;
                case CopilotAdoptionAccountabilityDimensions.Office:
                    return CopilotAdoptionAccountabilityDimensions.Office;
                case CopilotAdoptionAccountabilityDimensions.Company:
                    return CopilotAdoptionAccountabilityDimensions.Company;
                default:
                    return CopilotAdoptionAccountabilityDimensions.DirectManager;
            }
        }

        internal static string AccountabilityDimensionLabel(string dimension)
        {
            switch (NormaliseAccountabilityDimension(dimension))
            {
                case CopilotAdoptionAccountabilityDimensions.Department: return "Department";
                case CopilotAdoptionAccountabilityDimensions.Country: return "Country";
                case CopilotAdoptionAccountabilityDimensions.Office: return "Office";
                case CopilotAdoptionAccountabilityDimensions.Company: return "Company";
                default: return "Direct manager";
            }
        }

        private static string AccountabilityKey(LicensedUserAdoptionRow user, string dimension)
        {
            Func<string, string, string> clean = (value, emptyLabel) =>
                string.IsNullOrWhiteSpace(value) ? emptyLabel : value.Trim();

            switch (NormaliseAccountabilityDimension(dimension))
            {
                case CopilotAdoptionAccountabilityDimensions.Department:
                    return clean(user?.Department, "(no department)");
                case CopilotAdoptionAccountabilityDimensions.Country:
                    return clean(user?.Country, "(no country)");
                case CopilotAdoptionAccountabilityDimensions.Office:
                    return clean(user?.OfficeLocation, "(no office)");
                case CopilotAdoptionAccountabilityDimensions.Company:
                    return clean(user?.CompanyName, "(no company)");
                default:
                    return clean(user?.ManagerUserPrincipalName, "(no manager)");
            }
        }

        private static double HabitRatePct(AdoptionSegmentRow segment)
        {
            return segment.LicensedUsers > 0
                ? segment.HabitualUsers * 100d / segment.LicensedUsers
                : 0d;
        }

        #endregion

        #region Query plumbing


        private async Task<int> ExecuteAsync(
            string sql,
            CancellationToken cancellationToken,
            int commandTimeoutSecs = QueryTimeoutSecs,
            params SqlParameter[] parameters)
        {
            using (var db = _contextFactory.Create())
            {
                db.Database.CommandTimeout = commandTimeoutSecs;
                return await db.Database.ExecuteSqlCommandAsync(sql, cancellationToken, parameters);
            }
        }

        private async Task<int> ScalarAsync(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
        {
            var rows = await QueryAsync<int?>(sql, cancellationToken, parameters);
            return rows.FirstOrDefault() ?? 0;
        }

        private async Task<DateTime?> DateAsync(string sql, CancellationToken cancellationToken, params SqlParameter[] parameters)
        {
            var rows = await QueryAsync<DateTime?>(sql, cancellationToken, parameters);
            return rows.FirstOrDefault();
        }

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
        private async Task<List<T>> SafeAsync<T>(
            Func<Task<List<T>>> query,
            string step,
            string queryName,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken)
        {
            var operationId = _telemetry.QueryStarted(step, queryName);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var failed = false;
            string exceptionType = null;
            try
            {
                return await query();
            }
            catch (Exception ex)
            {
                failed = true;
                exceptionType = ex.GetBaseException().GetType().Name;

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
            finally
            {
                watch.Stop();
                _telemetry.QueryCompleted(
                    operationId,
                    step,
                    queryName,
                    watch.ElapsedMilliseconds,
                    failed,
                    exceptionType);
            }
        }

        /// <summary>
        /// As above, for a step running in the concurrent phase: also records that the query failed, so
        /// the step's diagnostics say so instead of reporting a silent degradation as a success.
        /// </summary>
        private async Task<List<T>> SafeAsync<T>(
            Func<Task<List<T>>> query,
            string step,
            string queryName,
            StepOutput output,
            string description,
            CancellationToken cancellationToken)
        {
            // SafeAsync returns null exactly when it swallowed a failure, which is what makes this work
            // without every call site having to remember to report it.
            var rows = await SafeAsync(
                query, step, queryName, output.Warnings, description, cancellationToken);
            if (rows == null) output.MarkQueryFailed();
            return rows;
        }

        private async Task<int> SafeScalarAsync(
            string sql,
            string step,
            string queryName,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            return await SafeScalarAsync(
                sql, step, queryName, warnings, description, null, cancellationToken, parameters);
        }

        /// <summary>Step-scoped <see cref="SafeScalarAsync(string, List{string}, string, Action, CancellationToken, SqlParameter[])"/>.</summary>
        private async Task<int> SafeScalarAsync(
            string sql,
            string step,
            string queryName,
            StepOutput output,
            string description,
            Action onFailure,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<int?>(sql, cancellationToken, parameters),
                step,
                queryName,
                output,
                description,
                cancellationToken);

            if (rows == null)
            {
                onFailure?.Invoke();
                return 0;
            }

            return rows.FirstOrDefault() ?? 0;
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
            string step,
            string queryName,
            List<string> warnings,
            string description,
            Action onFailure,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<int?>(sql, cancellationToken, parameters),
                step,
                queryName,
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
            string step,
            string queryName,
            List<string> warnings,
            string description,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            return await SafeDateAsync(
                sql, step, queryName, warnings, description, null, cancellationToken, parameters);
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
            string step,
            string queryName,
            List<string> warnings,
            string description,
            Action onFailure,
            CancellationToken cancellationToken,
            params SqlParameter[] parameters)
        {
            var rows = await SafeAsync(
                () => QueryAsync<DateTime?>(sql, cancellationToken, parameters),
                step,
                queryName,
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
        /// Every completed week from first to the week before the exclusive end. The current partial
        /// week is deliberately absent: plotting it always creates an artificial drop.
        /// </summary>
        internal static List<DateTime> CompletedWeekSpine(DateTime firstMonday, DateTime exclusiveMonday)
        {
            return WeekSpine(firstMonday, exclusiveMonday.AddDays(-7));
        }

        /// <summary>
        /// Projects query rows onto the full completed-week spine. Missing weeks are zero only when the
        /// Audit.General import has evidence in that week; otherwise they remain null so the chart draws
        /// a gap instead of pretending an import outage was zero Copilot use.
        /// </summary>
        internal static List<AdoptionTimePoint> FillWeeks(
            List<DateTime> weekSpine,
            List<NamedWeekRow> rows,
            IEnumerable<DateTime> verifiedCoverageWeeks)
        {
            var byWeek = new Dictionary<DateTime, double>();
            foreach (var row in rows)
            {
                byWeek[row.WeekStart.Date] = row.Value;
            }

            var coveredWeeks = new HashSet<DateTime>(
                (verifiedCoverageWeeks ?? Enumerable.Empty<DateTime>()).Select(w => w.Date));
            foreach (var week in byWeek.Keys)
            {
                coveredWeeks.Add(week);
            }

            return weekSpine
                .Select(week => new AdoptionTimePoint
                {
                    WeekStart = week,
                    Value = byWeek.TryGetValue(week, out var value)
                        ? value
                        : coveredWeeks.Contains(week) ? 0 : (double?)null,
                })
                .ToList();
        }

        /// <summary>
        /// Leading unknown weeks predate the tenant's imported audit history and are not informative;
        /// keep unknown weeks after the first evidence week so true interior coverage holes remain gaps.
        /// </summary>
        internal static List<DateTime> ClipLeadingUnverifiedWeeks(
            List<DateTime> weekSpine,
            IEnumerable<DateTime> verifiedCoverageWeeks,
            IEnumerable<NamedWeekRow> rows)
        {
            if (weekSpine == null || weekSpine.Count == 0) return weekSpine ?? new List<DateTime>();

            var evidenceWeeks = (verifiedCoverageWeeks ?? Enumerable.Empty<DateTime>())
                .Select(w => w.Date)
                .Concat((rows ?? Enumerable.Empty<NamedWeekRow>()).Select(r => r.WeekStart.Date))
                .Where(w => weekSpine.Contains(w))
                .ToList();

            if (evidenceWeeks.Count == 0) return weekSpine;

            var firstEvidenceWeek = evidenceWeeks.Min();
            return weekSpine.Where(w => w >= firstEvidenceWeek).ToList();
        }

        #endregion

        #region Raw query row shapes

        /// <summary>One (user, Copilot seat SKU) assignment.</summary>
        public class SeatAssignmentRow
        {
            public int UserId { get; set; }

            public int LicenceTypeId { get; set; }

            public string SkuPartNumber { get; set; }

            public string LicenceName { get; set; }
        }

        /// <summary>A single integer column, for id lookups.</summary>
        public class IntValueRow
        {
            public int Value { get; set; }
        }

        /// <summary>
        /// One user's total billed Copilot Credits in the window.
        ///
        /// Users with no credit rows are simply absent, which is what lets the Cowork tab render "not
        /// attributable" rather than a zero that would read as "this person costs nothing".
        /// </summary>
        public class UserCreditRow
        {
            public int UserId { get; set; }

            public decimal BilledCredits { get; set; }
        }

        /// <summary>
        /// The tenant's Copilot Credit capacity snapshot. Every figure is nullable because the licensing
        /// API legitimately omits some of them - notably pay-as-you-go consumption, which a tenant on
        /// pre-purchased capacity simply does not have.
        /// </summary>
        public class CreditCapacityRow
        {
            public DateTime SnapshotUtc { get; set; }

            public decimal? Entitled { get; set; }

            public decimal? Consumed { get; set; }

            public decimal? AvailableCredits { get; set; }

            public decimal? PayAsYouGoConsumed { get; set; }

            public string Status { get; set; }
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

        /// <summary>A completed week where Audit.General has at least one imported event.</summary>
        public class WeekCoverageRow
        {
            public DateTime WeekStart { get; set; }
        }

        #endregion
    }
}
