using DataUtils;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WebJob.AppInsightsImporter.Engine
{
    /// <summary>How an import cycle ended.</summary>
    public enum AppInsightsCycleOutcome
    {
        /// <summary>Everything that was read saved.</summary>
        Succeeded,

        /// <summary>
        /// The cycle ran to the end, but not cleanly: a day stopped being retried, or the ingestion cursor could not be
        /// read or saved (so the next cycle re-reads more than it needs to).
        /// </summary>
        Degraded,

        /// <summary>The cycle stopped early. The day it stopped at is retried next cycle; the cursor did not move.</summary>
        Failed
    }

    /// <summary>How one day of a cycle ended.</summary>
    public enum AppInsightsDayOutcome
    {
        /// <summary>Everything read for the day saved.</summary>
        Saved,

        /// <summary>Nothing to save once out-of-scope rows were dropped.</summary>
        NoData,

        /// <summary>The day failed and the cycle stopped at it, to retry it next cycle before any later day.</summary>
        Failed,

        /// <summary>The day failed again after its forced retries stopped; the cycle carried on with later days.</summary>
        RetriesStopped
    }

    /// <summary>What one day of a cycle did. One <c>AppInsightsDay</c> event carries it.</summary>
    public sealed class AppInsightsDayResult
    {
        public AppInsightsDayResult(DateTime dayUtc)
        {
            DayUtc = dayUtc.Date;
        }

        public DateTime DayUtc { get; }
        public AppInsightsDayOutcome Outcome { get; set; }
        public int Windows { get; set; }
        public int PageViews { get; set; }
        public int Events { get; set; }
        public double FetchSeconds { get; set; }
        public double SaveSeconds { get; set; }

        /// <summary>"Download" or "Save" when the day failed.</summary>
        public string FailedStage { get; set; }

        /// <summary>The custom-event sections that failed, when it was those.</summary>
        public IReadOnlyList<string> FailedSections { get; set; } = new string[0];

        /// <summary>How many times in a row the day has now failed; 0 when it did not fail.</summary>
        public int ConsecutiveFailures { get; set; }

        public Exception Exception { get; set; }
    }

    /// <summary>What one import cycle did. The host reports it as an <c>AppInsightsImportCycle</c> event.</summary>
    public sealed class AppInsightsImportCycleResult
    {
        public AppInsightsReadMode Mode { get; set; }
        public AppInsightsCycleOutcome Outcome { get; set; } = AppInsightsCycleOutcome.Succeeded;

        /// <summary>Why the outcome is not <see cref="AppInsightsCycleOutcome.Succeeded"/>: stable keys, e.g. <c>DayFailed</c>.</summary>
        public List<string> Reasons { get; } = new List<string>();

        public List<AppInsightsDayResult> Days { get; } = new List<AppInsightsDayResult>();

        /// <summary>The ingestion cursor this cycle stored, when it stored one.</summary>
        public DateTime? CursorAdvancedToUtc { get; set; }

        public int PageViews => Days.Sum(d => d.PageViews);
        public int Events => Days.Sum(d => d.Events);
        public int Windows => Days.Sum(d => d.Windows);

        internal void Degrade(string reason)
        {
            if (Outcome == AppInsightsCycleOutcome.Succeeded) Outcome = AppInsightsCycleOutcome.Degraded;
            if (!Reasons.Contains(reason)) Reasons.Add(reason);
        }

        internal void Fail(string reason)
        {
            Outcome = AppInsightsCycleOutcome.Failed;
            if (!Reasons.Contains(reason)) Reasons.Add(reason);
        }
    }

    /// <summary>
    /// The one outcome event per cycle and per day, so a log export from any deployment says which cycles and days
    /// failed or were slow without reading every trace.
    /// </summary>
    public static class AppInsightsImportTelemetry
    {
        public static void TrackDay(AnalyticsLogger logger, AppInsightsDayResult day, AppInsightsReadMode mode)
        {
            var context = new Dictionary<string, string>
            {
                { "Day", day.DayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "Outcome", day.Outcome.ToString() },
                { "Mode", mode.ToString() },
            };
            if (!string.IsNullOrEmpty(day.FailedStage)) context["FailedStage"] = day.FailedStage;
            if (day.FailedSections.Count > 0) context["FailedSections"] = string.Join(",", day.FailedSections);
            if (day.Exception != null)
            {
                foreach (var dimension in ExceptionDescriber.Dimensions(day.Exception)) context[dimension.Key] = dimension.Value;
            }
            var metrics = new Dictionary<string, double>
            {
                { "Windows", day.Windows },
                { "PageViews", day.PageViews },
                { "Events", day.Events },
                { "FetchSeconds", Math.Round(day.FetchSeconds, 3) },
                { "SaveSeconds", Math.Round(day.SaveSeconds, 3) },
                { "ConsecutiveFailures", day.ConsecutiveFailures },
            };
            logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.AppInsightsDay, context, metrics);
        }

        /// <summary>Sent once, when a day's forced retries stop - not on every later failure of the same day.</summary>
        public static void TrackDayRetriesStopped(AnalyticsLogger logger, AppInsightsDayResult day)
        {
            var context = new Dictionary<string, string>
            {
                { "Day", day.DayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                { "FailedStage", day.FailedStage ?? string.Empty },
                { "FailedSections", string.Join(",", day.FailedSections) },
            };
            if (day.Exception != null)
            {
                foreach (var dimension in ExceptionDescriber.Dimensions(day.Exception)) context[dimension.Key] = dimension.Value;
            }
            var metrics = new Dictionary<string, double> { { "Attempts", day.ConsecutiveFailures } };
            logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.AppInsightsDayGivenUp, context, metrics);
        }

        /// <summary>
        /// The cycle's outcome: one trace line (Information, Warning for Degraded, Error for Failed) and one
        /// <c>AppInsightsImportCycle</c> event. <paramref name="result"/> is null when the cycle threw
        /// <paramref name="exception"/>.
        /// </summary>
        public static AppInsightsCycleOutcome TrackCycle(AnalyticsLogger logger, AppInsightsImportCycleResult result, TimeSpan elapsed, Exception exception = null)
        {
            var outcome = result?.Outcome ?? AppInsightsCycleOutcome.Failed;
            var reasons = result?.Reasons.ToList() ?? new List<string>();
            if (result == null) reasons.Add("Exception");

            var context = new Dictionary<string, string>
            {
                { "Outcome", outcome.ToString() },
                { "Mode", result?.Mode.ToString() ?? string.Empty },
                { "Reasons", string.Join(",", reasons) },
                { "FailedDays", result == null ? string.Empty : string.Join(",", result.Days
                    .Where(d => d.Outcome == AppInsightsDayOutcome.Failed || d.Outcome == AppInsightsDayOutcome.RetriesStopped)
                    .Select(d => d.DayUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) },
            };
            if (result?.CursorAdvancedToUtc != null) context["CursorUtc"] = result.CursorAdvancedToUtc.Value.ToString("o", CultureInfo.InvariantCulture);
            if (exception != null)
            {
                foreach (var dimension in ExceptionDescriber.Dimensions(exception)) context[dimension.Key] = dimension.Value;
            }
            var metrics = new Dictionary<string, double>
            {
                { "Days", result?.Days.Count ?? 0 },
                { "Windows", result?.Windows ?? 0 },
                { "PageViews", result?.PageViews ?? 0 },
                { "Events", result?.Events ?? 0 },
                { "ElapsedSeconds", Math.Round(elapsed.TotalSeconds, 3) },
            };
            logger.TrackEvent(AnalyticsLogger.AnalyticsEvent.AppInsightsImportCycle, context, metrics);

            var line = string.Format(CultureInfo.InvariantCulture,
                "AppInsightsImportCycle: Outcome={0}; Mode={1}; Days={2}; Windows={3}; PageViews={4}; Events={5}; ElapsedMs={6:0}; Reasons={7}; FailedDays={8}.",
                outcome, context["Mode"], metrics["Days"], metrics["Windows"], metrics["PageViews"], metrics["Events"], elapsed.TotalMilliseconds,
                context["Reasons"], context["FailedDays"]);
            switch (outcome)
            {
                case AppInsightsCycleOutcome.Succeeded: logger.LogInformation(line); break;
                case AppInsightsCycleOutcome.Degraded: logger.LogWarning(line); break;
                default: logger.LogError(line); break;
            }
            return outcome;
        }
    }
}
