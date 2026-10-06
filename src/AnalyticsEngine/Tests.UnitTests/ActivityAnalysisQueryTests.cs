using Common.Entities.ActivityAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Reading an Activity analysis request: the metrics, the activity ranges, the licences and the period - and the
    /// stable code each refusal carries, which is what the portal translates.
    /// </summary>
    [TestClass]
    public class ActivityAnalysisQueryTests
    {
        private static readonly DateTime Today = new DateTime(2026, 10, 6);

        private static ActivityAnalysisSchema Schema(DateTime? earliest = null, DateTime? latest = null) =>
            ActivityAnalysisSchema.Complete(earliest ?? new DateTime(2024, 1, 1), latest ?? new DateTime(2026, 9, 28));

        private static string CodeOf(Action action)
        {
            try
            {
                action();
            }
            catch (ActivityAnalysisQueryException ex)
            {
                return ex.Code;
            }

            throw new AssertFailedException("The request was accepted.");
        }

        #region Catalogue

        [TestMethod]
        public void Catalogue_HasEveryWeeklyColumn_OnceEach_WithStableKeys()
        {
            var all = ActivityAnalysisMetricCatalogue.All;

            Assert.AreEqual(58, all.Count);
            Assert.AreEqual(all.Count, all.Select(m => m.Key).Distinct().Count());
            Assert.AreEqual(all.Count, all.Select(m => m.Column).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            CollectionAssert.AreEqual(Enumerable.Range(0, all.Count).ToList(), all.Select(m => m.Index).ToList());
            Assert.IsTrue(all.All(m => ActivityAnalysisCategories.All.Contains(m.Category)));
            CollectionAssert.AreEqual(new[] { "teams", "outlook", "onedrive", "sharepoint", "copilot", "vivaEngage" }, ActivityAnalysisCategories.All.ToList());

            CollectionAssert.AreEqual(
                new[] { "teams.audioDuration", "teams.videoDuration", "teams.screenshareDuration" },
                all.Where(m => m.Unit == ActivityAnalysisUnits.Seconds).Select(m => m.Key).ToList());
            Assert.AreEqual(25, all.Count(m => m.Core), "The Power BI filter page's 21 metrics, Teams meetings and Copilot's three.");
            CollectionAssert.AreEqual(
                new[] { "teams.privateChats", "teams.teamChats", "teams.calls", "teams.meetings", "teams.meetingsAttended", "teams.meetingsOrganized" },
                ActivityAnalysisMetricCatalogue.DefaultSelection.ToList());

            Assert.IsTrue(ActivityAnalysisMetricCatalogue.TryGet("onedrive.viewedEdited", out var viewed));
            Assert.AreEqual("OneDrive Viewed/Edited", viewed.Column);
            Assert.IsTrue(ActivityAnalysisMetricCatalogue.TryGetByColumn("yammer posted", out var posted), "Column names compare like SQL Server's.");
            Assert.AreEqual("vivaEngage.posted", posted.Key);
            Assert.IsFalse(ActivityAnalysisMetricCatalogue.TryGet("Teams.Calls", out _), "Keys are a contract: exact.");
        }

        /// <summary>
        /// The catalogue and the shipped profiling script must name the same columns: a column the script adds needs a
        /// catalogue entry (and a translation in the portal), and a catalogue entry for a column the script never
        /// creates would be unavailable everywhere.
        /// </summary>
        [TestMethod]
        public void Catalogue_NamesExactlyTheMetricColumnsTheShippedProfilingScriptCreates()
        {
            var definitions = ActivityAnalysisSqlTests.ProfilingScriptBatches()
                .Where(b => b.Contains("profiling.ActivitiesWeeklyColumns") && !b.Contains("#ActivitiesStaging") && !b.Contains("CREATE PROCEDURE"))
                .ToList();
            var scriptColumns = definitions
                .SelectMany(b => System.Text.RegularExpressions.Regex.Matches(b, "\"([^\"]+)\" BIGINT NOT NULL DEFAULT 0").Cast<System.Text.RegularExpressions.Match>())
                .Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();

            CollectionAssert.AreEqual(
                ActivityAnalysisMetricCatalogue.All.Select(m => m.Column).OrderBy(c => c, StringComparer.Ordinal).ToList(),
                scriptColumns);
        }

        #endregion

        #region Metrics

        [TestMethod]
        public void Metrics_AreRequired_KnownAndKeptInOrderOnce()
        {
            var query = ActivityAnalysisQuery.Parse(null, null, " teams.calls , outlook.emailsSent,teams.calls,, ", null, null);
            CollectionAssert.AreEqual(new[] { "teams.calls", "outlook.emailsSent" }, query.Metrics.Select(m => m.Key).ToList());

            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric, CodeOf(() => ActivityAnalysisQuery.Parse(null, null, null, null, null)));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric, CodeOf(() => ActivityAnalysisQuery.Parse(null, null, " , ", null, null)));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric, CodeOf(() => ActivityAnalysisQuery.Parse(null, null, "teams.calls,teams.nope", null, null)));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric, CodeOf(() => ActivityAnalysisQuery.Parse(null, null, "[Teams Calls]", null, null)));

            var every = string.Join(",", ActivityAnalysisMetricCatalogue.All.Select(m => m.Key));
            Assert.AreEqual(58, ActivityAnalysisQuery.Parse(null, null, every, null, null).Metrics.Count);
        }

        [TestMethod]
        public void UnavailableMetrics_AreRefusedOnceTheSchemaIsKnown()
        {
            var columns = ActivityAnalysisMetricCatalogue.All.Where(m => m.Key != "teams.urgentMessages").Select(m => m.Column);
            var older = new ActivityAnalysisSchema(true, columns, new DateTime(2026, 1, 5), new DateTime(2026, 9, 28));

            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric,
                CodeOf(() => ActivityAnalysisQuery.Parse(null, null, "teams.urgentMessages", null, null).Resolve(older, Today)));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidRange,
                CodeOf(() => ActivityAnalysisQuery.Parse(null, null, "teams.calls", "teams.urgentMessages:1:", null).Resolve(older, Today)));
            Assert.IsNotNull(ActivityAnalysisQuery.Parse(null, null, "teams.calls", null, null).Resolve(older, Today).Period);
        }

        #endregion

        #region Ranges and licences

        [TestMethod]
        public void Ranges_AcceptOpenBounds_AndRoundFractionsInwards()
        {
            var ranges = ActivityAnalysisQuery.ParseRanges("teams.calls:5:, outlook.emailsSent::100 ,teams.audioDuration:1800.5:7200,teams.meetings::");

            Assert.AreEqual(3, ranges.Count, "A range with neither bound is no condition at all.");
            Assert.AreEqual(5, ranges[0].Minimum);
            Assert.IsNull(ranges[0].Maximum);
            Assert.IsNull(ranges[1].Minimum);
            Assert.AreEqual(100, ranges[1].Maximum);
            Assert.AreEqual(1801, ranges[2].Minimum, "At least 1800.5 seconds is at least 1801 whole seconds.");
            Assert.AreEqual(7200, ranges[2].Maximum);

            Assert.IsTrue(ranges[0].Admits(5));
            Assert.IsFalse(ranges[0].Admits(4));
            Assert.IsTrue(ranges[1].Admits(0));
            Assert.IsFalse(ranges[1].Admits(101));

            Assert.AreEqual(0, ActivityAnalysisQuery.ParseRanges(null).Count);
            Assert.AreEqual(0, ActivityAnalysisQuery.ParseRanges("").Count);
        }

        [DataTestMethod]
        [DataRow("teams.calls")]
        [DataRow("teams.calls:5")]
        [DataRow("teams.calls:1:2:3")]
        [DataRow("teams.nope:1:")]
        [DataRow("teams.calls:-1:")]
        [DataRow("teams.calls:five:")]
        [DataRow("teams.calls:1e3:")]
        [DataRow("teams.calls:1,000:")]
        [DataRow("teams.calls:10:5")]
        [DataRow("teams.calls:1:,teams.calls::5")]
        [DataRow("teams.calls:9999999999999999999:")]
        public void Ranges_RefuseAnythingElse(string ranges)
        {
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidRange, CodeOf(() => ActivityAnalysisQuery.Parse(null, null, "teams.calls", ranges, null)));
        }

        [TestMethod]
        public void Licences_AreIdsSeparatedByCommas()
        {
            CollectionAssert.AreEqual(new[] { 7, 12 }, ActivityAnalysisQuery.ParseLicences(" 7,12,7 ").ToList());
            Assert.AreEqual(0, ActivityAnalysisQuery.ParseLicences(null).Count);

            foreach (var invalid in new[] { "seven", "-1", "0", "1.5", "+3", "99999999999" })
            {
                Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidFilter, CodeOf(() => ActivityAnalysisQuery.ParseLicences(invalid)), invalid);
            }

            var tooMany = string.Join(",", Enumerable.Range(1, ActivityAnalysisQuery.MaximumLicences + 1));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidFilter, CodeOf(() => ActivityAnalysisQuery.ParseLicences(tooMany)));
        }

        #endregion

        #region Period

        [TestMethod]
        public void Period_SnapsEachDateToTheMondayOnOrBeforeIt()
        {
            var query = ActivityAnalysisQuery.Parse("2026-01-07", "2026-02-08", "teams.calls", null, null).Resolve(Schema(), Today);

            Assert.AreEqual(new DateTime(2026, 1, 5), query.Period.From, "Wednesday to its Monday.");
            Assert.AreEqual(new DateTime(2026, 2, 2), query.Period.To, "Sunday to the Monday before it - the week it belongs to.");
            Assert.AreEqual(5, query.Period.Weeks);
            Assert.AreEqual(new DateTime(2026, 2, 2), query.Period.WeekStarts.Last());
            Assert.AreEqual(1, query.Period.WeekIndexOf(new DateTime(2026, 1, 14)));
            Assert.AreEqual(-1, query.Period.WeekIndexOf(new DateTime(2026, 2, 9)));
            Assert.AreEqual("2026-01-05..2026-02-02", query.Period.Key);
        }

        [TestMethod]
        public void Period_DefaultsToTheLatestCompiledWeekAndTheFiftyOneBefore()
        {
            var query = ActivityAnalysisQuery.Parse(null, null, "teams.calls", null, null).Resolve(Schema(), Today);
            Assert.AreEqual(new DateTime(2026, 9, 28), query.Period.To);
            Assert.AreEqual(new DateTime(2025, 10, 6), query.Period.From);
            Assert.AreEqual(52, query.Period.Weeks);

            var young = ActivityAnalysisQuery.Parse(null, null, "teams.calls", null, null)
                .Resolve(Schema(new DateTime(2026, 6, 1)), Today);
            Assert.AreEqual(new DateTime(2026, 6, 1), young.Period.From, "Never before the earliest compiled week.");

            var onlyFrom = ActivityAnalysisQuery.Parse("2026-03-02", null, "teams.calls", null, null).Resolve(Schema(), Today);
            Assert.AreEqual(new DateTime(2026, 9, 28), onlyFrom.Period.To);

            var onlyTo = ActivityAnalysisQuery.Parse(null, "2025-06-30", "teams.calls", null, null).Resolve(Schema(), Today);
            Assert.AreEqual(new DateTime(2024, 7, 8), onlyTo.Period.From, "52 weeks ending on the chosen week.");

            var empty = ActivityAnalysisQuery.Parse(null, null, "teams.calls", null, null)
                .Resolve(new ActivityAnalysisSchema(true, ActivityAnalysisMetricCatalogue.All.Select(m => m.Column), null, null), Today);
            Assert.AreEqual(new DateTime(2026, 10, 5), empty.Period.To, "No compiled week yet: the current one.");
        }

        [DataTestMethod]
        [DataRow("2026-13-01", null)]
        [DataRow("06/10/2026", null)]
        [DataRow("2026-10-06T10:00:00", null)]
        [DataRow("2026-02-02", "2026-01-05")]
        [DataRow("2024-01-01", "2026-01-05")]
        [DataRow("1899-12-25", "1900-01-08")]
        public void Period_RefusesUnreadableReversedOrTooLongPeriods(string from, string to)
        {
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidPeriod,
                CodeOf(() => ActivityAnalysisQuery.Parse(from, to, "teams.calls", null, null).Resolve(Schema(), Today)));
        }

        [TestMethod]
        public void Period_OfExactlyTheMaximumWeeksIsAccepted()
        {
            var from = new DateTime(2024, 9, 30);
            var query = ActivityAnalysisQuery.Parse("2024-09-30", ActivityAnalysisWeeks.Format(from.AddDays(7 * 104)), "teams.calls", null, null)
                .Resolve(Schema(), Today);

            Assert.AreEqual(ActivityAnalysisPeriod.MaximumWeeks, query.Period.Weeks);
        }

        #endregion

        #region Availability

        [TestMethod]
        public void Availability_SaysWhyThereAreNoFigures_WithStableKeys()
        {
            var notInstalled = ActivityAnalysisAvailability.From(ActivityAnalysisSchema.NotInstalled);
            Assert.IsFalse(notInstalled.Available);
            Assert.AreEqual("notInstalled", notInstalled.Reason);
            Assert.IsNull(notInstalled.EarliestWeek);
            Assert.AreEqual(58, notInstalled.Metrics.Count);
            Assert.IsTrue(notInstalled.Metrics.All(m => !m.Available));

            var noData = ActivityAnalysisAvailability.From(new ActivityAnalysisSchema(true, new[] { "Teams Calls" }, null, null));
            Assert.IsFalse(noData.Available);
            Assert.AreEqual("noData", noData.Reason);
            Assert.IsNull(noData.DefaultFrom);
            Assert.IsTrue(noData.Metrics.Single(m => m.Key == "teams.calls").Available);
            Assert.IsFalse(noData.Metrics.Single(m => m.Key == "teams.meetings").Available);

            var ready = ActivityAnalysisAvailability.From(Schema(new DateTime(2025, 10, 8), new DateTime(2026, 10, 1)));
            Assert.IsTrue(ready.Available);
            Assert.IsNull(ready.Reason);
            Assert.AreEqual("2025-10-06", ready.EarliestWeek, "Snapped to the week's Monday.");
            Assert.AreEqual("2026-09-28", ready.LatestWeek);
            Assert.AreEqual("2025-10-06", ready.DefaultFrom, "51 weeks before the latest would be 2025-10-06 - which is also the earliest.");
            Assert.AreEqual("2026-09-28", ready.DefaultTo);
            Assert.AreEqual(105, ready.MaximumWeeks);
        }

        [TestMethod]
        public void Availability_SerialisesToTheContractsCamelCaseJson()
        {
            var json = JObject.FromObject(ActivityAnalysisAvailability.From(Schema(new DateTime(2025, 10, 6), new DateTime(2026, 9, 28))));

            CollectionAssert.AreEqual(
                new[] { "available", "reason", "earliestWeek", "latestWeek", "defaultFrom", "defaultTo", "maximumWeeks", "categories", "metrics" },
                json.Properties().Select(p => p.Name).ToList());
            Assert.AreEqual(JTokenType.Null, json["reason"].Type);
            var calls = json["metrics"].Single(m => (string)m["key"] == "teams.calls");
            CollectionAssert.AreEqual(new[] { "key", "category", "unit", "core", "available", "label" },
                ((JObject)calls).Properties().Select(p => p.Name).ToList());
            Assert.AreEqual("Teams Calls", (string)calls["label"]);
            Assert.AreEqual("count", (string)calls["unit"]);
        }

        #endregion
    }
}
