using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceActivityReadModelTests
    {
        private static readonly DateTime Now =
            new DateTime(2000, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void OverviewUsesUniqueUsersForOverlappingAndDuplicateAssignments()
        {
            var users = Users(1, 2, 3);
            var model = Model(
                new[]
                {
                    Sku(1, "Contoso E1"),
                    Sku(2, "Contoso E3")
                },
                users,
                new[]
                {
                    Member(1, 1), Member(1, 1), Member(1, 2),
                    Member(2, 1), Member(3, 2)
                },
                AvailableCoverage(),
                Scores(("teams", 1, Score(4, 4)), ("teams", 2, Score(0, 4))));

            var overview = model.BuildOverview(Query(), CancellationToken.None);

            Assert.AreEqual(3, overview.DistinctAssignedUsers);
            Assert.AreEqual(2, overview.Licences.Single(sku => sku.LicenceTypeId == 1).AssignedUsers);
            Assert.AreEqual(2, overview.Licences.Single(sku => sku.LicenceTypeId == 2).AssignedUsers);
            Assert.AreEqual(3, overview.Departments.Sum(value => value.AssignedUsers));
            Assert.AreEqual(5, overview.Licences[0].Workloads.Count);
        }

        [TestMethod]
        public void EvidenceSeparatesMeasuredZeroAbsentPartialDisabledAndPositiveFallback()
        {
            var coverage = AvailableCoverage();
            coverage.Single(item => item.Workload == "outlook").Status = "disabled";
            coverage.Single(item => item.Workload == "outlook").Message = "Import is disabled.";
            var copilot = coverage.Single(item => item.Workload == "copilot");
            copilot.Status = "partial";
            copilot.Source = "copilotAudit";
            copilot.Message = "Event evidence is positive-only.";
            var model = Model(
                new[] { Sku(1, "Contoso") },
                Users(1, 2, 3, 4),
                Enumerable.Range(1, 4).Select(id => Member(id, 1)).ToArray(),
                coverage,
                Scores(
                    ("teams", 1, Score(0, 4)),
                    ("teams", 3, Score(2, 3)),
                    ("outlook", 4, Score(2, 4)),
                    ("copilot", 2, Score(1, 0, false))));
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            var query = Query().ForUsers(
                1, "teams", null, "activity", "desc", 10, 1, 100, Now);

            var result = model.BuildUsers(overview, query, CancellationToken.None);

            AssertEvidence(result, 1, "teams", "available", "zero");
            AssertEvidence(result, 2, "teams", "available", "zero");
            AssertEvidence(result, 3, "teams", "partial", "unknown");
            AssertEvidence(result, 4, "outlook", "disabled", "unknown");
            AssertEvidence(result, 2, "copilot", "partial", "unknown");
            CollectionAssert.Contains(result.MostActive.Select(user => user.UserId).ToList(), 3);
            CollectionAssert.Contains(result.LeastActive.Select(user => user.UserId).ToList(), 2,
                "Someone with no rows across a fully measured period did nothing, provably, so they "
                + "belong in the least-active list rather than being withheld as unmeasured.");
            CollectionAssert.DoesNotContain(result.LeastActive.Select(user => user.UserId).ToList(), 3,
                "A short reading count is still incomplete evidence and stays out of least-active.");

            var teams = overview.Licences.Single().Workloads.Single(item => item.Workload == "teams");
            Assert.AreEqual(3, teams.Zero,
                "The explicit zero plus the two people with no rows at all are all measured zeros.");
            Assert.AreEqual(1, teams.Unknown,
                "Only the person whose own evidence is incomplete stays Unknown.");
            Assert.AreEqual(0, overview.Licences.Single().Workloads
                .Single(item => item.Workload == "copilot").Zero);
        }

        [TestMethod]
        public void AGroupFilteredUsageImport_LeavesUnmeasuredPeopleUnknownRatherThanInactive()
        {
            // UserGroupsFilter scopes the usage-report import but NOT the user import, so the two
            // populations differ. Calling someone inactive because they hold a licence and have no
            // rows would then be a confident wrong answer about someone nobody ever looked at.
            var model = Model(
                new[] { Sku(1, "Contoso") },
                Users(1, 2),
                new[] { Member(1, 1), Member(2, 1) },
                AvailableCoverage(),
                Scores(("teams", 1, Score(4, 4))),
                usageReportsGroupFiltered: true);
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            var result = model.BuildUsers(
                overview,
                Query().ForUsers(1, "teams", null, "activity", "desc", 10, 1, 100, Now),
                CancellationToken.None);

            AssertEvidence(result, 1, "teams", "available", "high");
            AssertEvidence(result, 2, "teams", "missingCoverage", "unknown");
            var teams = overview.Licences.Single().Workloads.Single(item => item.Workload == "teams");
            Assert.AreEqual(0, teams.Zero);
            Assert.AreEqual(1, teams.Unknown);
            CollectionAssert.DoesNotContain(result.LeastActive.Select(user => user.UserId).ToList(), 2);
            CollectionAssert.Contains(overview.Messages,
                LicenceActivityRules.Notes.UsageReportsGroupFiltered,
                "The reader has to be told why those people are Unknown.");
        }

        [TestMethod]
        public void ExactRangeAndOverviewScopeAreRequired()
        {
            var model = Model(
                new[] { Sku(1, "Contoso") },
                new[] { User(1, departmentId: 7) },
                new[] { Member(1, 1) },
                AvailableCoverage(),
                Scores());
            var filtered = LicenceActivityQuery.Create(
                Query().From, Query().To, Now, departmentId: 7);
            var overview = model.BuildOverview(filtered, CancellationToken.None);

            Assert.ThrowsException<ArgumentException>(() =>
                model.BuildOverview(
                    LicenceActivityQuery.Create("2000-05-02", "2000-06-25", Now),
                    CancellationToken.None));
            Assert.ThrowsException<ArgumentException>(() =>
                model.BuildUsers(
                    overview,
                    Query().ForUsers(1, "teams", null, "upn", "asc", 10, 1, 50, Now),
                    CancellationToken.None));
            var other = Model(
                new[] { Sku(1, "Contoso") }, new[] { User(1) },
                new[] { Member(1, 1) }, AvailableCoverage(), Scores());
            Assert.ThrowsException<ArgumentException>(() =>
                other.BuildUsers(
                    overview,
                    filtered.ForUsers(1, "teams", null, "upn", "asc", 10, 1, 50, Now),
                    CancellationToken.None));
        }

        [TestMethod]
        public void DemographicsAreBoundedAndSelectedCohortsRemainVisible()
        {
            var users = Enumerable.Range(1, 51)
                .Select(id => User(
                    id,
                    departmentId: id,
                    department: "Department " + id,
                    countryId: id,
                    country: "Country " + id))
                .ToArray();
            var model = Model(
                new[] { Sku(1, "Contoso") },
                users,
                users.Select(user => Member(user.UserId, 1)).ToArray(),
                AvailableCoverage(),
                Scores());

            var all = model.BuildOverview(Query(), CancellationToken.None);
            var selectedQuery = LicenceActivityQuery.Create(
                Query().From, Query().To, Now, departmentId: 51);
            var selected = model.BuildOverview(selectedQuery, CancellationToken.None);

            Assert.AreEqual(50, all.Departments.Count);
            Assert.AreEqual(50, all.Countries.Count);
            Assert.IsTrue(all.DemographicsTruncated);
            Assert.AreEqual(1, selected.Departments.Count);
            Assert.AreEqual(51, selected.Departments[0].Id);
            Assert.AreEqual(1, selected.Departments[0].AssignedUsers);
        }

        [TestMethod]
        public void RankingIsDeterministicAcrossTiesAndPaging()
        {
            var users = new[]
            {
                User(3, upn: "same@contoso.example"),
                User(1, upn: "SAME@contoso.example"),
                User(4, upn: "z@contoso.example"),
                User(2, upn: "a@contoso.example")
            };
            var model = Model(
                new[] { Sku(1, "Contoso") },
                users,
                users.Select(user => Member(user.UserId, 1)).ToArray(),
                AvailableCoverage(),
                Scores(
                    ("teams", 1, Score(2, 4, true, 5, Now.AddDays(-1))),
                    ("teams", 2, Score(2, 4, true, 5, Now.AddDays(-1))),
                    ("teams", 3, Score(2, 4, true, 5, Now.AddDays(-1))),
                    ("teams", 4, Score(2, 4, true, 5, Now.AddDays(-1)))));
            var overview = model.BuildOverview(Query(), CancellationToken.None);

            var first = model.BuildUsers(overview,
                Query().ForUsers(1, "teams", null, "activity", "desc", 4, 1, 2, Now),
                CancellationToken.None);
            var second = model.BuildUsers(overview,
                Query().ForUsers(1, "teams", null, "activity", "desc", 4, 2, 2, Now),
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 2, 1 }, first.Users.Select(user => user.UserId).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 4 }, second.Users.Select(user => user.UserId).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 1, 3, 4 },
                first.MostActive.Select(user => user.UserId).ToArray());
        }

        [TestMethod]
        public void SearchIsLiteralCaseInsensitiveAndChecksUnicodeMail()
        {
            var users = new[]
            {
                User(1, upn: "percent%_user@contoso.example", mail: "alias@contoso.example",
                    department: "Καλημέρα κόσμε"),
                User(2, upn: "other@contoso.example", mail: "Καλημέρα@contoso.example")
            };
            var model = Model(
                new[] { Sku(1, "Contoso") },
                users,
                users.Select(user => Member(user.UserId, 1)).ToArray(),
                AvailableCoverage(),
                Scores());
            var overview = model.BuildOverview(Query(), CancellationToken.None);

            var literal = model.BuildUsers(overview,
                Query().ForUsers(1, "teams", "%_", "upn", "asc", 10, 1, 50, Now),
                CancellationToken.None);
            var unicode = model.BuildUsers(overview,
                Query().ForUsers(1, "teams", "ΚΑΛΗΜΈΡΑ", "upn", "asc", 10, 1, 50, Now),
                CancellationToken.None);

            CollectionAssert.AreEqual(new[] { 1 }, literal.Users.Select(user => user.UserId).ToArray());
            CollectionAssert.AreEqual(new[] { 2 }, unicode.Users.Select(user => user.UserId).ToArray());
            Assert.AreEqual("Καλημέρα κόσμε",
                model.BuildOverview(Query(), CancellationToken.None).Departments.Single().Name);
        }

        [TestMethod]
        public void EmptySkuAndNullNameRemainInTheFixedProjection()
        {
            var model = Model(
                new[] { Sku(2, "Contoso"), Sku(1, null) },
                new[] { User(1, departmentId: 0, department: "Must not leak") },
                new[] { Member(1, 2) },
                AvailableCoverage(),
                Scores());

            var overview = model.BuildOverview(Query(), CancellationToken.None);

            Assert.AreEqual(1, overview.Licences[0].LicenceTypeId);
            Assert.IsNull(overview.Licences[0].Name);
            Assert.AreEqual(0, overview.Licences[0].AssignedUsers);
            Assert.IsTrue(overview.Licences[0].Workloads.All(item =>
                item.High == 0 && item.Moderate == 0 && item.Low == 0
                && item.Zero == 0 && item.Unknown == 0));
            Assert.AreEqual("Unknown", overview.Departments.Single().Name);
            var empty = model.BuildUsers(overview,
                Query().ForUsers(1, "teams", null, "upn", "asc", 10, 1, 50, Now), CancellationToken.None);
            Assert.AreEqual(0, empty.TotalUsers);
            Assert.AreEqual(0, empty.Users.Count);
            Assert.ThrowsException<ArgumentException>(() =>
                model.BuildUsers(overview, Query(), CancellationToken.None));
            Assert.ThrowsException<ArgumentException>(() =>
                model.BuildUsers(overview,
                    Query().ForUsers(99, "teams", null, "upn", "asc", 10, 1, 50, Now), CancellationToken.None));
        }

        [TestMethod]
        public void CancellationIsObservedBeforeProjectionWork()
        {
            var model = Model(
                new[] { Sku(1, "Contoso") }, Users(1),
                new[] { Member(1, 1) }, AvailableCoverage(), Scores());
            var cancelled = new CancellationToken(true);

            Assert.ThrowsException<OperationCanceledException>(() =>
                model.BuildOverview(Query(), cancelled));
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            Assert.ThrowsException<OperationCanceledException>(() =>
                model.BuildUsers(
                    overview,
                    Query().ForUsers(1, "teams", null, "upn", "asc", 10, 1, 50, Now),
                    cancelled));
        }

        [TestMethod]
        public void ReturnedMutableCollectionsNeverExposePinnedModelState()
        {
            var coverage = AvailableCoverage();
            coverage[0].SnapshotDates.Add(new DateTime(2000, 6, 25));
            var model = Model(
                new[] { Sku(1, "Contoso") }, Users(1),
                new[] { Member(1, 1) }, coverage,
                Scores(("teams", 1, Score(4, 4))));
            var first = model.BuildOverview(Query(), CancellationToken.None);
            first.Coverage[0].Status = "mutated";
            first.Coverage[0].SnapshotDates.Clear();
            first.Licences[0].Workloads[0].High = 999;
            var second = model.BuildOverview(Query(), CancellationToken.None);

            Assert.AreEqual("available", second.Coverage[0].Status);
            Assert.AreEqual(1, second.Coverage[0].SnapshotDates.Count);
            Assert.AreEqual(1, second.Licences[0].Workloads[0].High);

            var query = Query().ForUsers(
                1, "teams", null, "upn", "asc", 10, 1, 50, Now);
            var firstUsers = model.BuildUsers(second, query, CancellationToken.None);
            firstUsers.Users[0].Workloads[0].Status = "mutated";
            firstUsers.Users.Clear();
            var secondUsers = model.BuildUsers(second, query, CancellationToken.None);
            Assert.AreEqual(1, secondUsers.Users.Count);
            Assert.AreEqual("available", secondUsers.Users[0].Workloads[0].Status);
        }

        [TestMethod]
        [TestCategory("Performance")]
        public void ThreeHundredThousandUsersReuseBoundedGlobalRankIndexes()
        {
            const int count = 300000;
            const int licenceCount = 50;
            const int assignmentsPerUser = 7;
            var users = new LicenceActivityDirectoryUser[count];
            var memberships = new LicenceActivityMembership[count * assignmentsPerUser];
            for (var index = 0; index < count; index++)
            {
                var id = index + 1;
                users[index] = User(id, upn: "user" + id.ToString("D6") + "@contoso.example");
                for (var assignment = 0; assignment < assignmentsPerUser; assignment++)
                    memberships[index * assignmentsPerUser + assignment] =
                        Member(id, ((index + assignment) % licenceCount) + 1);
            }
            var model = Model(
                Enumerable.Range(1, licenceCount)
                    .Select(id => Sku(id, "Contoso " + id))
                    .ToArray(),
                users, memberships,
                AvailableCoverage("inMemoryCounter"), Scores());
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            var query = Query().ForUsers(
                1, "teams", null, "activity", "desc", 100, 420, 100, Now);

            var first = model.BuildUsers(overview, query, CancellationToken.None);
            var second = model.BuildUsers(overview, query, CancellationToken.None);
            var anotherLicence = model.BuildUsers(
                overview,
                Query().ForUsers(2, "teams", "USER2", "activity", "desc", 100, 1, 100, Now),
                CancellationToken.None);

            Assert.AreEqual(count, overview.DistinctAssignedUsers);
            Assert.AreEqual(42000, first.TotalUsers);
            Assert.AreEqual(42000, first.RankedUsers);
            Assert.AreEqual(100, first.Users.Count);
            Assert.IsTrue(anotherLicence.TotalUsers > 0);
            CollectionAssert.AreEqual(
                first.Users.Select(user => user.UserId).ToArray(),
                second.Users.Select(user => user.UserId).ToArray());
        }

        private static void AssertEvidence(
            LicenceActivityUsers result, int userId, string workload, string status, string band)
        {
            var evidence = result.Users.Single(user => user.UserId == userId)
                .Workloads.Single(item => item.Workload == workload);
            Assert.AreEqual(status, evidence.Status);
            Assert.AreEqual(band, evidence.Band);
        }

        private static LicenceActivityReadModel Model(
            IReadOnlyList<LicenceActivitySku> licences,
            IReadOnlyList<LicenceActivityDirectoryUser> users,
            IReadOnlyList<LicenceActivityMembership> memberships,
            IReadOnlyList<LicenceActivityCoverage> coverage,
            IReadOnlyDictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>> scores,
            bool usageReportsGroupFiltered = false) =>
            new LicenceActivityReadModel(
                Query(), licences, users, memberships, coverage, scores, usageReportsGroupFiltered);

        private static LicenceActivityQuery Query() =>
            LicenceActivityQuery.Create("2000-05-01", "2000-06-25", Now);

        private static LicenceActivitySku Sku(int id, string name) =>
            new LicenceActivitySku { LicenceTypeId = id, Name = name, SkuId = "SYNTHETIC_" + id };

        private static LicenceActivityDirectoryUser[] Users(params int[] ids) =>
            ids.Select(id => User(id)).ToArray();

        private static LicenceActivityDirectoryUser User(
            int id, string upn = null, string mail = null,
            int departmentId = 1, string department = "Contoso Engineering",
            int countryId = 1, string country = "Contoso Country") =>
            new LicenceActivityDirectoryUser
            {
                UserId = id,
                UserPrincipalName = upn ?? "user" + id + "@contoso.example",
                Mail = mail,
                DepartmentId = departmentId,
                Department = department,
                CountryId = countryId,
                Country = country,
                AccountEnabled = true
            };

        private static LicenceActivityMembership Member(int userId, int licenceTypeId) =>
            new LicenceActivityMembership(userId, licenceTypeId);

        private static LicenceActivityScore Score(
            int active, int observed, bool frequencyKnown = true,
            double? average = null, DateTime? last = null) =>
            new LicenceActivityScore(active, observed, frequencyKnown, average, last);

        private static List<LicenceActivityCoverage> AvailableCoverage(
            string source = "microsoftGraphUsageReport") =>
            LicenceActivityQuery.Workloads.Select(workload => new LicenceActivityCoverage
            {
                Workload = workload,
                Status = "available",
                Source = workload == "copilot" && source == "microsoftGraphUsageReport"
                    ? "microsoftGraphCopilotUsageReport"
                    : source,
                Measure = "synthetic activity",
                Granularity = "weekly",
                ExpectedSamples = 4,
                ObservedSamples = 4
            }).ToList();

        private static IReadOnlyDictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>> Scores(
            params (string Workload, int UserId, LicenceActivityScore Score)[] values)
        {
            var result = LicenceActivityQuery.Workloads.ToDictionary(
                workload => workload,
                workload => (IReadOnlyDictionary<int, LicenceActivityScore>)
                    new Dictionary<int, LicenceActivityScore>(),
                StringComparer.Ordinal);
            foreach (var value in values)
                ((Dictionary<int, LicenceActivityScore>)result[value.Workload])
                    .Add(value.UserId, value.Score);
            return result;
        }
    }
}
