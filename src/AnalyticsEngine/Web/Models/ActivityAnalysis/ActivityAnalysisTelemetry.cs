using Common.Entities.ActivityAnalysis;
using Common.Entities.Config;
using DataUtils;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.ActivityAnalysis
{
    /// <summary>
    /// Times every load the Activity analysis page makes - a period's read model, a filtered weekly series - and records
    /// it in Application Insights as an <c>ActivityAnalysisLoad</c> event, so a slow page can be traced to the scan behind
    /// it without turning on SQL logging.
    /// </summary>
    /// <remarks>
    /// Facts only: the stage, how long it took, how many weeks and people it covered, and on failure the exception's type
    /// and SQL error number. Never a name, an id, a filter or a SQL message. Telemetry never fails a load.
    /// <code>
    /// customEvents | where name == "ActivityAnalysisLoad"
    /// | project timestamp, Stage=tostring(customDimensions.Stage), Outcome=tostring(customDimensions.Outcome),
    ///           DurationMs=todouble(customMeasurements.DurationMs), People=todouble(customMeasurements.People),
    ///           Weeks=todouble(customMeasurements.Weeks), Bytes=todouble(customMeasurements.ApproximateBytes)
    /// </code>
    /// </remarks>
    internal sealed class ActivityAnalysisTelemetrySource : IActivityAnalysisSource
    {
        internal const string ReadModelStage = "ReadModel";
        internal const string WeeklyTotalsStage = "WeeklyTotals";

        private readonly IActivityAnalysisSource _inner;
        private readonly Action<Dictionary<string, string>, Dictionary<string, double>> _track;

        internal ActivityAnalysisTelemetrySource(
            IActivityAnalysisSource inner, Action<Dictionary<string, string>, Dictionary<string, double>> track = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _track = track ?? ToApplicationInsights;
        }

        public Task<ActivityAnalysisSchema> ReadSchemaAsync(CancellationToken cancellationToken) =>
            _inner.ReadSchemaAsync(cancellationToken);

        public async Task<ActivityAnalysisReadModel> LoadReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var model = await _inner.LoadReadModelAsync(period, cancellationToken).ConfigureAwait(false);
                Track(ReadModelStage, watch, null, new Dictionary<string, double>
                {
                    ["Weeks"] = period.Weeks,
                    ["People"] = model.PeopleCount,
                    ["AvailableMetrics"] = model.AvailableMetrics.Count,
                    ["ApproximateBytes"] = model.ApproximateBytes,
                });
                return model;
            }
            catch (Exception ex)
            {
                Track(ReadModelStage, watch, ex, new Dictionary<string, double> { ["Weeks"] = period?.Weeks ?? 0 });
                throw;
            }
        }

        public async Task<ActivityAnalysisWeeklyTotals> LoadWeeklyTotalsAsync(
            ActivityAnalysisReadModel model, IReadOnlyList<int> userIds, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            var measurements = new Dictionary<string, double>
            {
                ["Weeks"] = model?.Period.Weeks ?? 0,
                ["People"] = userIds?.Count ?? 0,
            };

            try
            {
                var totals = await _inner.LoadWeeklyTotalsAsync(model, userIds, cancellationToken).ConfigureAwait(false);
                Track(WeeklyTotalsStage, watch, null, measurements);
                return totals;
            }
            catch (Exception ex)
            {
                Track(WeeklyTotalsStage, watch, ex, measurements);
                throw;
            }
        }

        private void Track(string stage, Stopwatch watch, Exception failure, Dictionary<string, double> measurements)
        {
            try
            {
                var dimensions = new Dictionary<string, string>
                {
                    ["Stage"] = stage,
                    ["Outcome"] = failure == null ? "Completed" : "Failed",
                };

                if (failure != null)
                {
                    var root = failure.GetBaseException();
                    dimensions["ExceptionType"] = root.GetType().Name;
                    var sql = root as SqlException ?? failure as SqlException;
                    if (sql != null) dimensions["SqlErrorNumber"] = sql.Number.ToString(CultureInfo.InvariantCulture);
                }

                measurements["DurationMs"] = watch.ElapsedMilliseconds;
                measurements["ManagedHeapBytes"] = GC.GetTotalMemory(false);
                _track(dimensions, measurements);
            }
            catch (Exception)
            {
                // Telemetry must never turn a load into a failure.
            }
        }

        private static void ToApplicationInsights(Dictionary<string, string> dimensions, Dictionary<string, double> measurements)
        {
            var logger = new AnalyticsLogger(new AppConfig().AppInsightsConnectionString, "ActivityAnalysis");
            logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.ActivityAnalysisLoad, dimensions, measurements);

            // Loads are rare - one per period per quarter of an hour at most - and run on their own worker, so the
            // flush costs no reader anything and the event is not lost if the process recycles soon after.
            logger.Flush();
        }
    }
}
