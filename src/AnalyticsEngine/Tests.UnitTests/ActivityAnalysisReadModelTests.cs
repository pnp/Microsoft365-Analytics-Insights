using Common.Entities.ActivityAnalysis;
using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Activity analysis read model and its per-request projection, in memory: who matches, what is summed and
    /// counted, how companies and departments group, what a reader without See PII is shown, and the people list.
    /// </summary>
    /// <remarks>Every name, number and licence here is synthetic.</remarks>
    [TestClass]
    public class ActivityAnalysisReadModelTests
    {
        private const string GreekDepartment = "Καλημέρα κόσμε";
        private const int Floor = 5;

        private static readonly DateTime Week0 = new DateTime(2026, 1, 5);
        private static readonly ActivityAnalysisPeriod FiveWeeks = ActivityAnalysisPeriod.Create(Week0, Week0.AddDays(28));

        #region Fixture

        /// <summary>
        /// Ten people with a compiled week in the period, one in the directory without any, and one with weeks who is
        /// missing from the directory (imported since it was read).
        /// </summary>
        internal static ActivityAnalysisFakeSource Source()
        {
            return new ActivityAnalysisFakeSource()
                .Week(1, Week0, ("teams.calls", 3), ("outlook.emailsSent", 10))
                .Week(1, Week0.AddDays(7), ("teams.calls", 2))
                .Week(2, Week0, ("teams.calls", 1))
                .Week(3, Week0.AddDays(14))
                .Week(4, Week0.AddDays(21), ("teams.calls", 7))
                .Week(5, Week0.AddDays(28), ("teams.calls", 4), ("teams.meetings", 2))
                .Week(6, Week0, ("teams.calls", 2))
                .Week(7, Week0.AddDays(7), ("teams.meetings", 5))
                .Week(8, Week0.AddDays(7), ("teams.calls", 1))
                .Week(9, Week0.AddDays(14), ("teams.calls", 6))
                .Week(10, Week0.AddDays(21), ("teams.calls", 9))
                // A week outside the period is not part of it.
                .Week(1, Week0.AddDays(-7), ("teams.calls", 100))
                .Licence(7, "Microsoft 365 E3", "SPE_E3")
                .Licence(8, "Microsoft 365 E5", "SPE_E5")
                .Licence(9, "Copilot", "Microsoft_365_Copilot")
                .Holds(1, 7).Holds(2, 7, 8).Holds(3, 7).Holds(4, 8).Holds(5, 7).Holds(6, 9)
                .Holds(7, 7).Holds(8, 7).Holds(9, 7).Holds(10, 7).Holds(11, 7);
        }

        internal static UserDirectorySnapshot Directory()
        {
            return ActivityAnalysisTestDirectory.Build(
                (1, "user1@contoso.com", "Sales", "Contoso"),
                (2, "user2@contoso.com", "Sales", "Contoso"),
                (3, "user3@contoso.com", "Sales", "Contoso"),
                (4, "user4@contoso.com", "sales", "Contoso"),
                (5, "user5@contoso.com", "Sales", "Fabrikam"),
                (6, "user6@contoso.com", "Marketing", "Contoso"),
                (7, "user7@contoso.com", "Marketing", "Fabrikam"),
                (8, "user8@contoso.com", null, "Contoso"),
                (9, "user9@contoso.com", GreekDepartment, "Contoso"),
                (11, "user11@contoso.com", "Sales", "Contoso"));
        }

        private static ActivityAnalysisReadModel Model() => Source().Build(FiveWeeks);

        private static ActivityAnalysisQuery Query(string metrics = "teams.calls,teams.meetings", string ranges = null, string licences = null)
        {
            return ActivityAnalysisQuery.Parse("2026-01-05", "2026-02-02", metrics, ranges, licences)
                .Resolve(ActivityAnalysisSchema.Complete(Week0, Week0.AddDays(28)), new DateTime(2026, 2, 9));
        }

        private static ActivityAnalysisAudience Audience(
            bool seePii, UserDirectorySnapshot directory = null, string userFilter = null, string population = null)
        {
            directory = directory ?? Directory();
            return new ActivityAnalysisAudience
            {
                Directory = directory,
                UserFilter = userFilter == null ? null : ActivityAnalysisTestDirectory.Filter(directory, userFilter),
                Population = population == null ? null : ActivityAnalysisTestDirectory.Filter(directory, population),
                SeesIndividuals = seePii,
                MinimumPeopleWithoutSeePii = Floor,
            };
        }

        private static ActivityAnalysisReport Report(ActivityAnalysisReadModel model, ActivityAnalysisQuery query, ActivityAnalysisAudience audience)
        {
            var evaluation = model.Evaluate(query, audience);
            return evaluation.BuildReport(
                evaluation.NeedsWeeklyTotals ? Source().LoadWeeklyTotalsAsync(model, evaluation.MatchingUserIds(), CancellationToken.None).Result : null,
                null);
        }

        private static ActivityAnalysisMetricValue Value(IEnumerable<ActivityAnalysisMetricValue> values, string metric) =>
            values.Single(v => v.Metric == metric);

        #endregion

        #region Population, matching, sums and unique

        [TestMethod]
        public void Everyone_IsCountedOncePerPerson_SummedOverThePeriodsWeeks()
        {
            var report = Report(Model(), Query(), Audience(seePii: true));

            Assert.AreEqual(10, report.PopulationPeople, "Everyone with a compiled week in the period - and nobody without one.");
            Assert.AreEqual(10, report.MatchingPeople);
            Assert.AreEqual(9, report.ActivePeople, "user3's only week is all zeros.");
            Assert.IsFalse(report.Suppressed);
            CollectionAssert.AreEqual(new[] { "teams.calls", "teams.meetings" }, report.Metrics);
            CollectionAssert.AreEqual(new[] { "2026-01-05", "2026-01-12", "2026-01-19", "2026-01-26", "2026-02-02" }, report.WeekStarts);
            Assert.AreEqual("2026-01-05", report.From);
            Assert.AreEqual("2026-02-02", report.To);

            Assert.AreEqual(10, report.Total.People);
            Assert.AreEqual(35, Value(report.Total.Values, "teams.calls").Sum, "The week before the period is not summed.");
            Assert.AreEqual(8, Value(report.Total.Values, "teams.calls").Unique);
            Assert.AreEqual(7, Value(report.Total.Values, "teams.meetings").Sum);
            Assert.AreEqual(2, Value(report.Total.Values, "teams.meetings").Unique);
        }

        [TestMethod]
        public void UserFilter_LicencesAndRanges_AllHaveToMatch()
        {
            var model = Model();

            Assert.AreEqual(2, Report(model, Query(licences: "8"), Audience(true)).MatchingPeople, "Holders of E5: user2 and user4.");
            Assert.AreEqual(3, Report(model, Query(licences: "8,9"), Audience(true)).MatchingPeople, "ANY of the chosen licences.");
            Assert.AreEqual(0, Report(model, Query(licences: "12345"), Audience(true)).MatchingPeople,
                "A licence nobody holds matches nobody - it does not switch the condition off.");

            Assert.AreEqual(4, Report(model, Query(ranges: "teams.calls:5:"), Audience(true)).MatchingPeople, "5, 6, 7 and 9 calls - inclusive.");
            Assert.AreEqual(4, Report(model, Query(ranges: "teams.calls::1"), Audience(true)).MatchingPeople, "0, 0, 1 and 1 calls.");
            Assert.AreEqual(5, Report(model, Query(ranges: "teams.calls:1:5"), Audience(true)).MatchingPeople);
            Assert.AreEqual(3, Report(model, Query(ranges: "teams.calls:4.5:7.5"), Audience(true)).MatchingPeople,
                "Fractional bounds round inwards: 5 to 7 calls - users 1, 4 and 9.");
            Assert.AreEqual(1, Report(model, Query(ranges: "teams.calls:1:,teams.meetings:1:"), Audience(true)).MatchingPeople,
                "Every range must hold: only user5 both called and met.");
            Assert.AreEqual(1, Report(model, Query(ranges: "outlook.emailsSent:1:"), Audience(true)).MatchingPeople,
                "A range may be on a metric that is not selected.");

            var combined = Report(model, Query(licences: "7", ranges: "teams.calls:1:"),
                Audience(true, userFilter: "[{\"d\":\"companyName\",\"v\":[\"Contoso\"]}]"));
            Assert.AreEqual(10, combined.PopulationPeople, "The reader's own conditions never shrink the population.");
            Assert.AreEqual(4, combined.MatchingPeople, "Contoso, holding E3, with at least one call: users 1, 2, 8 and 9.");
            Assert.AreEqual(13, Value(combined.Total.Values, "teams.calls").Sum);
        }

        [TestMethod]
        public void GlobalFilter_IsThePopulation_ForLicencesAndRangeBoundsToo()
        {
            var report = Report(Model(), Query(), Audience(true, population: "[{\"d\":\"department\",\"v\":[\"Sales\"]}]"));

            Assert.AreEqual(5, report.PopulationPeople, "Sales has five people with weeks; user11 has none.");
            Assert.AreEqual(5, report.MatchingPeople);
            Assert.AreEqual(7, report.RangeMaxima.Single(m => m.Metric == "teams.calls").Max,
                "The slider's bound is the population's: user10's 9 calls are outside it.");

            CollectionAssert.AreEqual(new[] { "Microsoft 365 E3", "Microsoft 365 E5" }, report.Licences.Select(l => l.Name).ToList(),
                "Only licences held inside the population, by name.");
            Assert.AreEqual(4, report.Licences[0].People);
            Assert.AreEqual(2, report.Licences[1].People);
        }

        [TestMethod]
        public void Licences_AndRangeMaxima_DescribeThePopulation_NotTheSelection()
        {
            var report = Report(Model(), Query(metrics: "teams.calls", ranges: "teams.calls:100:"), Audience(true));

            Assert.AreEqual(0, report.MatchingPeople);
            CollectionAssert.AreEqual(new[] { "Copilot", "Microsoft 365 E3", "Microsoft 365 E5" }, report.Licences.Select(l => l.Name).ToList());
            CollectionAssert.AreEqual(new[] { 1, 8, 2 }, report.Licences.Select(l => l.People).ToList(),
                "user11 holds E3 but has no week in the period, so is not counted.");
            Assert.AreEqual("SPE_E3", report.Licences[1].SkuId);
            Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, report.RangeMaxima.Count, "Every available metric, whatever is selected.");
            Assert.AreEqual(9, report.RangeMaxima.Single(m => m.Metric == "teams.calls").Max);
            Assert.AreEqual(5, report.RangeMaxima.Single(m => m.Metric == "teams.meetings").Max);
            Assert.AreEqual(10, report.RangeMaxima.Single(m => m.Metric == "outlook.emailsSent").Max);
        }

        [TestMethod]
        public void NobodyMatching_IsAnEmptyReport_NotASuppressedOne()
        {
            var report = Report(Model(), Query(ranges: "teams.calls:1000:"), Audience(seePii: false));

            Assert.IsFalse(report.Suppressed);
            Assert.AreEqual(0, report.MatchingPeople);
            Assert.AreEqual(0, report.ActivePeople);
            Assert.AreEqual(2, report.Series.Count);
            Assert.IsTrue(report.Series.All(s => s.Sum.Length == 5 && s.Sum.All(v => v == 0) && s.ActivePeople.All(v => v == 0)));
            Assert.AreEqual(0, report.Departments.Count);
            Assert.AreEqual(0, report.ByCompany.Rows.Count);
            Assert.AreEqual(0, Value(report.Total.Values, "teams.calls").Sum);
        }

        #endregion

        #region Companies and departments

        [TestMethod]
        public void Departments_SortedByName_NotSetLast_AndEveryDepartmentShownToAReaderWithSeePii()
        {
            var report = Report(Model(), Query(), Audience(seePii: true));

            CollectionAssert.AreEqual(new[] { "Marketing", "Sales", GreekDepartment, null }, report.Departments.Select(d => d.Name).ToList(),
                "The directory's spelling of each department, Greek included; \"sales\" is the same department as \"Sales\".");
            Assert.IsTrue(report.Departments.All(d => !d.Other));
            Assert.AreEqual(0, report.OtherDepartments);

            var sales = report.Departments[1];
            Assert.AreEqual(5, sales.People);
            Assert.AreEqual(17, Value(sales.Values, "teams.calls").Sum);
            Assert.AreEqual(4, Value(sales.Values, "teams.calls").Unique);
            Assert.AreEqual(2, Value(sales.Values, "teams.meetings").Sum);
            Assert.AreEqual(1, Value(sales.Values, "teams.meetings").Unique);

            var notSet = report.Departments[3];
            Assert.AreEqual(2, notSet.People, "No department in the directory, and not in the directory at all.");
            Assert.AreEqual(10, Value(notSet.Values, "teams.calls").Sum);

            Assert.AreEqual(report.Total.People, report.Departments.Sum(d => d.People));
            Assert.AreEqual(Value(report.Total.Values, "teams.calls").Sum, report.Departments.Sum(d => Value(d.Values, "teams.calls").Sum));
        }

        [TestMethod]
        public void Charts_CountActivePeople_LargestFirst_ThenByName()
        {
            var report = Report(Model(), Query(), Audience(seePii: true));

            var companies = report.ByCompany.Rows.Select(r => (r.Name, r.Other, r.ActivePeople)).ToList();
            CollectionAssert.AreEqual(
                new[] { ("Contoso", false, 6), ("Fabrikam", false, 2), ((string)null, false, 1) },
                companies,
                "user3 had no activity; user10 is not in the directory, so their company is not set.");
            Assert.AreEqual(0, report.ByCompany.OtherGroups);

            var departments = report.ByDepartment.Rows.Select(r => (r.Name, r.ActivePeople)).ToList();
            CollectionAssert.AreEqual(
                new[] { ("Sales", 4), ("Marketing", 2), ((string)null, 2), (GreekDepartment, 1) },
                departments,
                "Equal counts are ordered by name, with \"not set\" after the named groups.");
        }

        [TestMethod]
        public void ReaderWithoutSeePii_SeesGroupsOfFewerThanFivePeopleFoldedIntoOneRow()
        {
            var report = Report(Model(), Query(), Audience(seePii: false));

            Assert.IsFalse(report.Suppressed, "Ten people match.");
            CollectionAssert.AreEqual(new[] { "Sales", null }, report.Departments.Select(d => d.Name).ToList());
            var other = report.Departments[1];
            Assert.IsTrue(other.Other);
            Assert.AreEqual(5, other.People, "Marketing (2), the Greek department (1) and \"not set\" (2).");
            Assert.AreEqual(18, Value(other.Values, "teams.calls").Sum);
            Assert.AreEqual(4, Value(other.Values, "teams.calls").Unique);
            Assert.AreEqual(3, report.OtherDepartments);

            CollectionAssert.AreEqual(new[] { ("Sales", false, 4), ((string)null, true, 5) },
                report.ByDepartment.Rows.Select(r => (r.Name, r.Other, r.ActivePeople)).ToList());
            Assert.AreEqual(3, report.ByDepartment.OtherGroups);

            CollectionAssert.AreEqual(new[] { ("Contoso", false, 6), ((string)null, true, 3) },
                report.ByCompany.Rows.Select(r => (r.Name, r.Other, r.ActivePeople)).ToList(),
                "Fabrikam (2 people) and \"not set\" (1) are folded; Contoso has 7 matching people.");
            Assert.AreEqual(2, report.ByCompany.OtherGroups);

            Assert.AreEqual(Value(report.Total.Values, "teams.calls").Sum, report.Departments.Sum(d => Value(d.Values, "teams.calls").Sum),
                "Folding moves figures, it never drops them.");
        }

        [TestMethod]
        public void Charts_HoldAtMostFiftyRows_TheFoldedRowIncluded()
        {
            var source = new ActivityAnalysisFakeSource();
            var people = new List<(int, string, string, string)>();
            for (var i = 1; i <= 60; i++)
            {
                source.Week(i, Week0, ("teams.calls", i));
                people.Add((i, "user" + i + "@contoso.com", "Department " + i.ToString("00"), "Contoso"));
            }

            var report = Report(source.Build(FiveWeeks), Query(), Audience(true, ActivityAnalysisTestDirectory.Build(people.ToArray())));

            Assert.AreEqual(50, report.ByDepartment.Rows.Count, "Forty-nine groups and the folded row.");
            Assert.AreEqual("Department 01", report.ByDepartment.Rows[0].Name, "Equal counts, so by name.");
            Assert.AreEqual("Department 49", report.ByDepartment.Rows[48].Name);
            Assert.IsTrue(report.ByDepartment.Rows[49].Other);
            Assert.IsNull(report.ByDepartment.Rows[49].Name);
            Assert.AreEqual(11, report.ByDepartment.Rows[49].ActivePeople);
            Assert.AreEqual(11, report.ByDepartment.OtherGroups);
            Assert.AreEqual(60, report.Departments.Count, "The matrix lists every department.");

            var fifty = new ActivityAnalysisFakeSource();
            for (var i = 1; i <= 50; i++) fifty.Week(i, Week0, ("teams.calls", 1));
            var exactly = Report(fifty.Build(FiveWeeks), Query(), Audience(true, ActivityAnalysisTestDirectory.Build(people.Take(50).ToArray())));
            Assert.AreEqual(50, exactly.ByDepartment.Rows.Count, "Fifty groups fit without folding any.");
            Assert.IsFalse(exactly.ByDepartment.Rows.Any(r => r.Other));
        }

        [TestMethod]
        public void Charts_ForAReaderWithoutSeePii_FoldSmallGroupsAndTheTailIntoTheSameRow()
        {
            var source = new ActivityAnalysisFakeSource();
            var people = new List<(int, string, string, string)>();
            var userId = 0;
            for (var department = 1; department <= 55; department++)
            {
                for (var member = 0; member < 5; member++)
                {
                    source.Week(++userId, Week0, ("teams.calls", 1));
                    people.Add((userId, "user" + userId + "@contoso.com", "Department " + department.ToString("00"), "Contoso"));
                }
            }

            source.Week(++userId, Week0, ("teams.calls", 1));
            people.Add((userId, "user" + userId + "@contoso.com", "Small team", "Contoso"));

            var report = Report(source.Build(FiveWeeks), Query(), Audience(false, ActivityAnalysisTestDirectory.Build(people.ToArray())));

            Assert.AreEqual(50, report.ByDepartment.Rows.Count);
            Assert.IsTrue(report.ByDepartment.Rows.Last().Other);
            Assert.AreEqual(7, report.ByDepartment.OtherGroups, "The one-person team, and the six departments after the 49th.");
            Assert.AreEqual(31, report.ByDepartment.Rows.Last().ActivePeople);
            Assert.AreEqual(userId, report.ByDepartment.Rows.Sum(r => r.ActivePeople), "Folding moves people, it never drops them.");
        }

        #endregion

        #region Suppression

        [TestMethod]
        public void ReaderWithoutSeePii_OneToFourMatchingPeople_SuppressesEveryFigureButTheCounts()
        {
            var model = Model();
            var marketing = "[{\"d\":\"department\",\"v\":[\"Marketing\"]}]";
            var report = Report(model, Query(), Audience(seePii: false, userFilter: marketing));

            Assert.IsTrue(report.Suppressed);
            Assert.AreEqual(10, report.PopulationPeople);
            Assert.AreEqual(2, report.MatchingPeople);
            Assert.AreEqual(0, report.ActivePeople, "Whether two named-by-filter people were active is itself their record.");
            Assert.AreEqual(0, report.Series.Count);
            Assert.AreEqual(0, report.ByCompany.Rows.Count);
            Assert.AreEqual(0, report.ByDepartment.Rows.Count);
            Assert.AreEqual(0, report.Departments.Count);
            Assert.AreEqual(0, report.Total.Values.Count);
            Assert.AreEqual(2, report.Total.People);
            Assert.AreEqual(3, report.Licences.Count, "The picker's licences describe the population, not the matching people.");
            Assert.AreEqual(ActivityAnalysisMetricCatalogue.Count, report.RangeMaxima.Count);

            var withSeePii = Report(model, Query(), Audience(seePii: true, userFilter: marketing));
            Assert.IsFalse(withSeePii.Suppressed);
            Assert.AreEqual(2, withSeePii.ActivePeople);
            CollectionAssert.AreEqual(new[] { "Marketing" }, withSeePii.Departments.Select(d => d.Name).ToList());
        }

        [TestMethod]
        public void Suppression_UsesTheFloorTheWebTierPasses()
        {
            var model = Model();
            var audience = Audience(seePii: false, userFilter: "[{\"d\":\"department\",\"v\":[\"Sales\"]}]");

            Assert.IsFalse(Report(model, Query(), audience).Suppressed, "Five people is enough.");

            audience.MinimumPeopleWithoutSeePii = 6;
            Assert.IsTrue(Report(model, Query(), audience).Suppressed);

            audience.MinimumPeopleWithoutSeePii = 0;
            Assert.ThrowsException<ArgumentException>(() => model.Evaluate(Query(), audience));
        }

        #endregion

        #region Weekly series

        [TestMethod]
        public void Series_LineUpWithTheWeeks_WithZeroForAWeekNobodyHasRowsIn()
        {
            var sixWeeks = ActivityAnalysisPeriod.Create(Week0, Week0.AddDays(35));
            var query = ActivityAnalysisQuery.Parse("2026-01-05", "2026-02-09", "teams.calls,teams.meetings", null, null)
                .Resolve(ActivityAnalysisSchema.Complete(Week0, Week0.AddDays(28)), new DateTime(2026, 2, 9));
            var evaluation = Source().Build(sixWeeks).Evaluate(query, Audience(true));

            Assert.IsTrue(evaluation.CoversEveryone);
            Assert.IsFalse(evaluation.NeedsWeeklyTotals, "Unfiltered, the read model's own weekly totals are the series - no SQL.");

            var report = evaluation.BuildReport(null, null);
            Assert.AreEqual(6, report.WeekStarts.Count);
            var calls = report.Series.Single(s => s.Metric == "teams.calls");
            CollectionAssert.AreEqual(new long[] { 6, 3, 6, 16, 4, 0 }, calls.Sum);
            CollectionAssert.AreEqual(new[] { 3, 2, 1, 2, 1, 0 }, calls.ActivePeople);
            var meetings = report.Series.Single(s => s.Metric == "teams.meetings");
            CollectionAssert.AreEqual(new long[] { 0, 5, 0, 0, 2, 0 }, meetings.Sum);
        }

        [TestMethod]
        public async Task FilteredSeries_IsReadForExactlyTheMatchingPeople_OncePerSetOfPeople()
        {
            var source = Source();
            var service = new ActivityAnalysisService(source, "synthetic", new ActivityAnalysisCaches());
            var audience = Audience(true, userFilter: "[{\"d\":\"department\",\"v\":[\"Sales\"]}]");

            var calls = await service.GetReportAsync(Query(metrics: "teams.calls"), audience, null, CancellationToken.None);
            CollectionAssert.AreEqual(new long[] { 4, 2, 0, 7, 4 }, calls.Series.Single().Sum);
            CollectionAssert.AreEqual(new[] { 2, 1, 0, 1, 1 }, calls.Series.Single().ActivePeople);
            Assert.AreEqual(1, source.WeeklyTotalsLoads);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, source.WeeklyTotalsRequests.Single(), "Ascending user ids.");

            var meetings = await service.GetReportAsync(Query(metrics: "teams.meetings,teams.calls"), audience, null, CancellationToken.None);
            CollectionAssert.AreEqual(new long[] { 0, 0, 0, 0, 2 }, meetings.Series[0].Sum);
            Assert.AreEqual(1, source.WeeklyTotalsLoads, "Every metric was read the first time: changing the selection reads nothing.");
            Assert.AreEqual(1, source.ReadModelLoads);

            await service.GetReportAsync(Query(metrics: "teams.calls", ranges: "teams.calls:1:"), audience, null, CancellationToken.None);
            Assert.AreEqual(2, source.WeeklyTotalsLoads, "A different set of people is a different series.");
        }

        [TestMethod]
        public async Task FilteredSeries_WhenMostPeopleMatch_ReadsEverybodyElse_AndTakesThemFromThePopulation()
        {
            var source = Source();
            var service = new ActivityAnalysisService(source, "synthetic", new ActivityAnalysisCaches());

            // Eight of the ten people in the period hold licence 7.
            var report = await service.GetReportAsync(Query(licences: "7"), Audience(true), null, CancellationToken.None);

            Assert.AreEqual(8, report.MatchingPeople);
            CollectionAssert.AreEqual(new[] { 4, 6 }, source.WeeklyTotalsRequests.Single(), "Only the two who do not match are read.");
            var calls = report.Series.Single(s => s.Metric == "teams.calls");
            CollectionAssert.AreEqual(new long[] { 4, 3, 6, 9, 4 }, calls.Sum);
            CollectionAssert.AreEqual(new[] { 2, 2, 1, 1, 1 }, calls.ActivePeople);
            var meetings = report.Series.Single(s => s.Metric == "teams.meetings");
            CollectionAssert.AreEqual(new long[] { 0, 5, 0, 0, 2 }, meetings.Sum);
            CollectionAssert.AreEqual(new[] { 0, 1, 0, 0, 1 }, meetings.ActivePeople);
            AssertSameSeries(Report(Model(), Query(licences: "7"), Audience(true)), report);
        }

        [TestMethod]
        public async Task FilteredSeries_WorkedOutFromTheOthers_CountsThoseOutsideTheAdministratorsFilterAsOthers()
        {
            var source = Source();
            var service = new ActivityAnalysisService(source, "synthetic", new ActivityAnalysisCaches());

            // The administrator admits Contoso's seven people; the reader asks for nothing more.
            var audience = Audience(true, population: "[{\"d\":\"companyName\",\"v\":[\"Contoso\"]}]");
            var report = await service.GetReportAsync(Query(), audience, null, CancellationToken.None);

            Assert.AreEqual(7, report.MatchingPeople);
            CollectionAssert.AreEqual(new[] { 5, 7, 10 }, source.WeeklyTotalsRequests.Single(),
                "Fabrikam's two and the person missing from the directory.");
            CollectionAssert.AreEqual(new long[] { 6, 3, 6, 7, 0 }, report.Series.Single(s => s.Metric == "teams.calls").Sum);
            AssertSameSeries(Report(Model(), Query(), audience), report);
        }

        [TestMethod]
        public async Task FilteredSeries_IsReadDirectly_WhenThePopulationsWeeksCountSomebodyThePeopleDoNot()
        {
            var source = Source();
            source.LeaveOutSomebody = true;
            var service = new ActivityAnalysisService(source, "synthetic", new ActivityAnalysisCaches());

            var report = await service.GetReportAsync(Query(licences: "7"), Audience(true), null, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 5, 7, 8, 9, 10 }, source.WeeklyTotalsRequests.Single(),
                "Everyone's figures less the others' would include the person left out.");
            CollectionAssert.AreEqual(new long[] { 4, 3, 6, 9, 4 }, report.Series.Single(s => s.Metric == "teams.calls").Sum);
        }

        [TestMethod]
        public void WeeklyTotals_Except_TakesAwaySlotBySlot_AndNeverGoesBelowZero()
        {
            var whole = new ActivityAnalysisWeeklyTotals(2);
            whole.Add(0, 3, 10, 4);
            whole.Add(1, 3, 5, 2);
            var part = new ActivityAnalysisWeeklyTotals(2);
            part.Add(0, 3, 4, 1);
            part.Add(1, 3, 7, 3);

            var rest = ActivityAnalysisWeeklyTotals.Except(whole, part);

            Assert.AreEqual(6L, rest.SumOf(0, 3));
            Assert.AreEqual(3, rest.ActivePeopleOf(0, 3));
            Assert.AreEqual(0L, rest.SumOf(1, 3), "A week rewritten between the two reads is never negative.");
            Assert.AreEqual(0, rest.ActivePeopleOf(1, 3));
            Assert.ThrowsException<ArgumentException>(() => ActivityAnalysisWeeklyTotals.Except(whole, new ActivityAnalysisWeeklyTotals(3)));
        }

        private static void AssertSameSeries(ActivityAnalysisReport expected, ActivityAnalysisReport actual)
        {
            Assert.AreEqual(expected.Series.Count, actual.Series.Count);
            for (var i = 0; i < expected.Series.Count; i++)
            {
                Assert.AreEqual(expected.Series[i].Metric, actual.Series[i].Metric);
                CollectionAssert.AreEqual(expected.Series[i].Sum, actual.Series[i].Sum, expected.Series[i].Metric);
                CollectionAssert.AreEqual(expected.Series[i].ActivePeople, actual.Series[i].ActivePeople, expected.Series[i].Metric);
            }
        }

        [TestMethod]
        public void FilteredSeries_MustBeSupplied()
        {
            var evaluation = Model().Evaluate(Query(), Audience(true, userFilter: "[{\"d\":\"department\",\"v\":[\"Sales\"]}]"));

            Assert.IsTrue(evaluation.NeedsWeeklyTotals);
            Assert.ThrowsException<InvalidOperationException>(() => evaluation.BuildReport(null, null));
        }

        [TestMethod]
        public void SuppressedReport_NeedsNoSeries()
        {
            var evaluation = Model().Evaluate(Query(), Audience(false, userFilter: "[{\"d\":\"department\",\"v\":[\"Marketing\"]}]"));

            Assert.IsTrue(evaluation.Suppressed);
            Assert.IsFalse(evaluation.NeedsWeeklyTotals, "Nothing is read for figures that will not be shown.");
        }

        #endregion

        #region People

        [TestMethod]
        public void People_InOneDepartment_LargestFirst_AndTruncated()
        {
            var evaluation = Model().Evaluate(Query(), Audience(true));
            var calls = ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == "teams.calls");

            var sales = evaluation.BuildPeople("sales", false, calls, 3);

            Assert.AreEqual(5, sales.TotalPeople);
            Assert.IsTrue(sales.Truncated);
            CollectionAssert.AreEqual(new[] { "user4@contoso.com", "user1@contoso.com", "user5@contoso.com" },
                sales.People.Select(p => p.UserPrincipalName).ToList());
            Assert.IsTrue(sales.People.All(p => p.Department == "Sales"), "The directory's spelling, whatever the request's.");
            Assert.AreEqual(7, sales.People[0].Values.Single(v => v.Metric == "teams.calls").Sum);
            CollectionAssert.AreEqual(new[] { "teams.calls", "teams.meetings" }, sales.People[0].Values.Select(v => v.Metric).ToList());
            Assert.AreEqual("sales", sales.Department);
            Assert.IsFalse(sales.NoDepartment);

            var everyone = evaluation.BuildPeople(null, false, calls, 500);
            Assert.AreEqual(10, everyone.TotalPeople);
            Assert.IsFalse(everyone.Truncated);
            Assert.IsNull(everyone.People[0].UserPrincipalName, "user10 - 9 calls - is not in the directory yet.");
            Assert.AreEqual(9, everyone.People[0].Values[0].Sum);
        }

        [TestMethod]
        public void People_TiesAreOrderedBySignInName()
        {
            var evaluation = Model().Evaluate(Query(), Audience(true));
            var meetings = ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == "teams.meetings");

            var top = evaluation.BuildPeople(null, false, meetings, 4);

            CollectionAssert.AreEqual(
                new[] { "user7@contoso.com", "user5@contoso.com", "user1@contoso.com", "user2@contoso.com" },
                top.People.Select(p => p.UserPrincipalName).ToList());
        }

        [TestMethod]
        public void People_WithNoDepartment_AndInADepartmentNobodyHolds()
        {
            var evaluation = Model().Evaluate(Query(), Audience(true));
            var calls = ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == "teams.calls");

            var notSet = evaluation.BuildPeople(null, true, calls, 100);
            Assert.AreEqual(2, notSet.TotalPeople);
            Assert.IsTrue(notSet.NoDepartment);
            Assert.IsNull(notSet.Department);
            CollectionAssert.AreEqual(new[] { null, "user8@contoso.com" }, notSet.People.Select(p => p.UserPrincipalName).ToList());

            var greek = evaluation.BuildPeople(GreekDepartment, false, calls, 100);
            Assert.AreEqual("user9@contoso.com", greek.People.Single().UserPrincipalName);
            Assert.AreEqual(GreekDepartment, greek.People.Single().Department);

            var nobody = evaluation.BuildPeople("Research", false, calls, 100);
            Assert.AreEqual(0, nobody.TotalPeople, "A department nobody holds matches nobody, not everybody.");
            Assert.AreEqual(0, nobody.People.Count);
        }

        #endregion

        #region Storage

        [TestMethod]
        public void PersonTotals_SaturateAtInt32_WhileSumsAcrossPeopleDoNot()
        {
            var calls = ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == "teams.calls");
            var builder = new ActivityAnalysisReadModelBuilder(FiveWeeks, ActivityAnalysisMetricCatalogue.All);
            var first = builder.AddPerson(1);
            builder.AddTotal(first, calls, int.MaxValue);
            builder.AddTotal(first, calls, 5);
            var second = builder.AddPerson(2);
            builder.AddTotal(second, calls, long.MaxValue);
            var third = builder.AddPerson(3);
            builder.AddTotal(third, calls, 3000000000L);
            builder.AddWeek(Week0, calls, 3L * int.MaxValue, 3);
            var model = builder.Build(DateTime.UtcNow);

            Assert.AreEqual(int.MaxValue, model.TotalOf(first, calls.Index));
            Assert.AreEqual(int.MaxValue, model.TotalOf(second, calls.Index));
            Assert.AreEqual(int.MaxValue, model.TotalOf(third, calls.Index));

            var report = model.Evaluate(Query(), Audience(true)).BuildReport(null, null);
            Assert.AreEqual(3L * int.MaxValue, Value(report.Total.Values, "teams.calls").Sum, "Summed as 64-bit, so three saturated people do not overflow.");
            Assert.AreEqual(3L * int.MaxValue, report.Series.Single(s => s.Metric == "teams.calls").Sum[0]);
        }

        [TestMethod]
        public void MissingColumns_AreUnavailable_AndRefusedRatherThanShownAsZero()
        {
            var available = ActivityAnalysisMetricCatalogue.All.Where(m => !m.Key.StartsWith("copilot.app.", StringComparison.Ordinal)).ToList();
            var model = Source().Build(FiveWeeks, available);

            Assert.IsFalse(model.IsAvailable(ActivityAnalysisMetricCatalogue.All.Single(m => m.Key == "copilot.app.word")));
            Assert.AreEqual(available.Count, model.AvailableMetrics.Count);

            var query = ActivityAnalysisQuery.Parse("2026-01-05", "2026-02-02", "copilot.app.word", null, null)
                .Resolve(ActivityAnalysisSchema.Complete(Week0, Week0), DateTime.UtcNow);
            var refusal = Assert.ThrowsException<ActivityAnalysisQueryException>(() => model.Evaluate(query, Audience(true)));
            Assert.AreEqual(ActivityAnalysisErrorCodes.InvalidMetric, refusal.Code);

            var report = model.Evaluate(Query(), Audience(true)).BuildReport(null, null);
            Assert.AreEqual(available.Count, report.RangeMaxima.Count, "No slider for a metric this database does not compile.");
        }

        [TestMethod]
        public void Evaluate_RefusesAQueryForAnotherPeriod()
        {
            var otherPeriod = ActivityAnalysisQuery.Parse("2026-01-05", "2026-01-12", "teams.calls", null, null)
                .Resolve(ActivityAnalysisSchema.Complete(Week0, Week0), DateTime.UtcNow);

            Assert.ThrowsException<ArgumentException>(() => Model().Evaluate(otherPeriod, Audience(true)));
        }

        /// <summary>
        /// The memory and per-request cost at the 200,000-person baseline: every metric, a year of weeks, a user filter
        /// and a range, so every array in the projection is exercised at full size.
        /// </summary>
        [TestMethod]
        public void TwoHundredThousandPeople_FitTheMemoryBudget_AndProjectInWellUnderASecond()
        {
            const int people = 200000;
            var year = ActivityAnalysisPeriod.Create(new DateTime(2025, 2, 3), new DateTime(2026, 1, 26));
            var builder = new ActivityAnalysisReadModelBuilder(year, ActivityAnalysisMetricCatalogue.All);
            var directory = new UserDirectorySnapshotBuilder();
            var random = new Random(20260209);
            for (var userId = 1; userId <= people; userId++)
            {
                var person = builder.AddPerson(userId);
                foreach (var metric in ActivityAnalysisMetricCatalogue.All)
                {
                    if (random.Next(3) == 0) builder.AddTotal(person, metric, random.Next(1, 5000));
                }

                if (userId % 3 == 0) builder.AddHolding(userId, 1 + userId % 7);
                directory.AddUser(new UserDirectoryEntry
                {
                    UserId = userId,
                    UserPrincipalName = "user" + userId + "@contoso.com",
                    Department = "Department " + (userId % 400),
                    CompanyName = "Company " + (userId % 7),
                    AccountEnabled = true,
                });
            }

            var model = builder.Build(DateTime.UtcNow);
            var snapshot = directory.Build(DateTime.UtcNow);
            Console.WriteLine("Read model: {0:N0} bytes for {1:N0} people x {2} metrics.", model.ApproximateBytes, people, ActivityAnalysisMetricCatalogue.Count);
            Assert.IsTrue(model.ApproximateBytes < 50L * 1024 * 1024, "About 46 MB of totals plus ids and licences.");

            var query = ActivityAnalysisQuery.Parse("2025-02-03", "2026-01-26",
                    string.Join(",", ActivityAnalysisMetricCatalogue.All.Select(m => m.Key)), "teams.calls::4000", "1,2,3,4,5,6,7")
                .Resolve(ActivityAnalysisSchema.Complete(year.From, year.To), DateTime.UtcNow);
            var audience = new ActivityAnalysisAudience
            {
                Directory = snapshot,
                UserFilter = ActivityAnalysisTestDirectory.Filter(snapshot, "[{\"d\":\"companyName\",\"op\":\"isNot\",\"v\":[\"Company 3\"]}]"),
                SeesIndividuals = false,
                MinimumPeopleWithoutSeePii = Floor,
            };

            model.Evaluate(query, audience).BuildReport(new ActivityAnalysisWeeklyTotals(year.Weeks), null);
            var watch = Stopwatch.StartNew();
            var evaluation = model.Evaluate(query, audience);
            var report = evaluation.BuildReport(new ActivityAnalysisWeeklyTotals(year.Weeks), null);
            watch.Stop();
            Console.WriteLine("Evaluate + report for {0:N0} matching people, 58 metrics: {1} ms.", report.MatchingPeople, watch.ElapsedMilliseconds);

            Assert.AreEqual(people, report.PopulationPeople);
            Assert.IsTrue(report.MatchingPeople > 40000, "Every licence holder outside one company, below a generous range.");
            Assert.AreEqual(400, report.Departments.Count, "Every department, none small enough to fold.");
            Assert.IsTrue(watch.ElapsedMilliseconds < 5000, "Generous for a shared build agent; about 100 ms on a developer machine.");
        }

        #endregion
    }
}
