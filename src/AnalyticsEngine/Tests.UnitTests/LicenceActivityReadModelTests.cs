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
            // No licence asks for everyone holding any licence - here the one person holding licence 2.
            var everyone = model.BuildUsers(overview, Query(), CancellationToken.None);
            Assert.AreEqual(1, everyone.TotalUsers);
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

        /// <summary>
        /// Three people, two overlapping licences: user 1 holds both, user 2 only the first, user 3 only the
        /// second (and sits in another department). Teams and Outlook have readings; OneDrive and SharePoint
        /// are fully measured with nobody active; Copilot comes from the presence-gated usage report and
        /// nobody is in it, so it is Unknown for everyone.
        /// </summary>
        private static LicenceActivityReadModel ScoredModel() => Model(
            new[] { Sku(1, "Contoso E3"), Sku(2, "Contoso F3"), Sku(3, "Contoso unused") },
            new[] { User(1), User(2), User(3, departmentId: 2, department: "Contoso Sales") },
            new[] { Member(1, 1), Member(1, 2), Member(2, 1), Member(3, 2) },
            AvailableCoverage(),
            Scores(("teams", 1, Score(4, 4)), ("teams", 2, Score(2, 4)), ("outlook", 1, Score(4, 4))));

        [TestMethod]
        public void Overview_ComparesEveryLicenceWithEveryoneHoldingALicence_CountingEachPersonOnce()
        {
            var overview = ScoredModel().BuildOverview(Query(), CancellationToken.None);

            var everyone = overview.AllLicences;
            Assert.IsNotNull(everyone);
            Assert.AreEqual(3, everyone.AssignedUsers, "User 1 holds two licences but is one person.");
            var teams = everyone.Workloads.Single(item => item.Workload == "teams");
            Assert.AreEqual(1, teams.High);
            Assert.AreEqual(1, teams.Moderate);
            Assert.AreEqual(1, teams.Zero);
            Assert.AreEqual(0, teams.Unknown);
            Assert.AreEqual(3, everyone.Workloads.Single(item => item.Workload == "copilot").Unknown);
            Assert.AreEqual(overview.DistinctAssignedUsers, everyone.AssignedUsers);
        }

        [TestMethod]
        public void AdoptionScore_IsActiveOverMeasuredWeeks_LeavingUnmeasuredServicesOut()
        {
            var overview = ScoredModel().BuildOverview(Query(), CancellationToken.None);

            // Four measured services of four weeks each per person; Copilot is unmeasured and left out
            // rather than counted as unused. User 1: 4 + 4 active weeks; user 2: 2; user 3: none.
            Assert.AreEqual(31.3, overview.Licences.Single(sku => sku.LicenceTypeId == 1).AdoptionScore,
                "(8 + 2) / 32 = 31.25%, rounded half away from zero.");
            Assert.AreEqual(25.0, overview.Licences.Single(sku => sku.LicenceTypeId == 2).AdoptionScore, "(8 + 0) / 32");
            Assert.AreEqual(20.8, overview.AllLicences.AdoptionScore, "(8 + 2 + 0) / 48 = 20.83%");
            Assert.IsNull(overview.Licences.Single(sku => sku.LicenceTypeId == 3).AdoptionScore,
                "Nobody holds it, so there is nothing to score - not a score of zero.");
        }

        [TestMethod]
        public void AdoptionScore_WeighsEveryMeasuredServiceEqually_WhateverItsUnit()
        {
            // Copilot measured from Microsoft's 28-day report counts days used against the period's 20 working
            // days; the Microsoft 365 services count weeks. Each of these two people fully used two of their
            // five measured services and nothing else, so each must score 40 - however many samples a service
            // happens to be counted in.
            var coverage = AvailableCoverage();
            var copilot = coverage.Single(item => item.Workload == "copilot");
            copilot.Granularity = "singleRollingWindow";
            copilot.ReportPeriodDays = 28;
            copilot.ExpectedSamples = 20;
            copilot.ObservedSamples = 20;
            var model = Model(
                new[] { Sku(1, "Contoso E5"), Sku(2, "Contoso E3") },
                Users(1, 2),
                new[] { Member(1, 1), Member(2, 2) },
                coverage,
                Scores(
                    ("teams", 1, Score(4, 4)), ("copilot", 1, Score(20, 20)),
                    ("teams", 2, Score(4, 4)), ("outlook", 2, Score(4, 4)), ("copilot", 2, Score(0, 20))));

            var overview = model.BuildOverview(Query(), CancellationToken.None);

            Assert.AreEqual(40.0, overview.Licences.Single(sku => sku.LicenceTypeId == 1).AdoptionScore,
                "Teams and Copilot fully used: 2 of 5 services. Adding raw samples gave (4 + 20) / (16 + 20) = 66.7.");
            Assert.AreEqual(40.0, overview.Licences.Single(sku => sku.LicenceTypeId == 2).AdoptionScore,
                "Teams and Outlook fully used, Copilot not at all: still 2 of 5. Adding raw samples gave 8 / 36 = 22.2.");
            Assert.AreEqual(40.0, overview.AllLicences.AdoptionScore);
        }

        [TestMethod]
        public void AdoptionScore_AndEveryoneFollowTheDemographicFilter()
        {
            var model = ScoredModel();
            var filtered = model.BuildOverview(
                LicenceActivityQuery.Create(Query().From, Query().To, Now, departmentId: 1), CancellationToken.None);

            Assert.AreEqual(2, filtered.AllLicences.AssignedUsers);
            Assert.AreEqual(31.3, filtered.AllLicences.AdoptionScore, "Users 1 and 2 only: (8 + 2) / 32.");
            Assert.AreEqual(50.0, filtered.Licences.Single(sku => sku.LicenceTypeId == 2).AdoptionScore,
                "Only user 1 holds the second licence in this department: 8 / 16.");
        }

        [TestMethod]
        public void AdoptionScore_IsNullWhenNothingCouldBeMeasured()
        {
            var coverage = AvailableCoverage();
            foreach (var item in coverage) item.Status = "disabled";
            var model = Model(new[] { Sku(1, "Contoso") }, Users(1), new[] { Member(1, 1) }, coverage, Scores());

            var overview = model.BuildOverview(Query(), CancellationToken.None);

            Assert.IsNull(overview.Licences.Single().AdoptionScore);
            Assert.IsNull(overview.AllLicences.AdoptionScore);
            Assert.IsNull(LicenceActivityRules.Score(0, 0));
            Assert.AreEqual(0d, LicenceActivityRules.Score(0, 16));
            Assert.AreEqual(100d, LicenceActivityRules.Score(16, 16));
        }

        [TestMethod]
        public void Users_WithoutALicence_RankEveryoneHoldingAnyLicence()
        {
            var model = ScoredModel();
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            var query = Query().ForUsers(null, "teams", null, "activity", "desc", 10, 1, 100, Now);

            var result = model.BuildUsers(overview, query, CancellationToken.None);

            Assert.IsNull(result.Query.LicenceTypeId);
            Assert.AreEqual(3, result.TotalUsers, "Each person once, whichever licences they hold.");
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result.MostActive.Select(user => user.UserId).ToArray());
            CollectionAssert.AreEqual(new[] { 3, 2, 1 }, result.LeastActive.Select(user => user.UserId).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, result.Users.Select(user => user.UserId).ToArray());

            var sales = model.BuildUsers(
                model.BuildOverview(LicenceActivityQuery.Create(Query().From, Query().To, Now, departmentId: 2), CancellationToken.None),
                LicenceActivityQuery.Create(Query().From, Query().To, Now, departmentId: 2)
                    .ForUsers(null, "teams", null, "upn", "asc", 10, 1, 100, Now),
                CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 3 }, sales.Users.Select(user => user.UserId).ToArray());
        }

        [TestMethod]
        public void Workbook_CarriesEveryoneAndTheAdoptionScores()
        {
            var model = ScoredModel();
            var overview = model.BuildOverview(Query(), CancellationToken.None);
            overview.SnapshotId = "synthetic-overview";
            var users = model.BuildUsers(
                overview, Query().ForUsers(null, "teams", null, "activity", "desc", 10, 1, 100, Now), CancellationToken.None);
            users.OverviewId = overview.SnapshotId;

            using (var archive = new System.IO.Compression.ZipArchive(
                new System.IO.MemoryStream(LicenceActivityWorkbook.Build(overview, users))))
            {
                var xml = string.Join("\n", archive.Entries
                    .Where(e => e.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal))
                    .Select(e => new System.IO.StreamReader(e.Open()).ReadToEnd()));
                StringAssert.Contains(xml, "Everyone holding a licence");
                StringAssert.Contains(xml, "Adoption score");
                StringAssert.Contains(xml, "31.3");
            }
        }

        // --- A reader without See PII (#661, #680): no figure about fewer than five people -------------------------

        [TestMethod]
        public void WithoutSeePii_GroupsOfOneToFourAreFoldedOrWithheld_AndAGroupOfFiveIsShown()
        {
            var overview = SmallGroupsModel(Query()).BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);

            Assert.IsFalse(overview.Suppressed, "Twenty people hold a licence.");
            Assert.AreEqual(20, overview.DistinctAssignedUsers);
            Assert.AreEqual(5, overview.Query.MinimumGroupSize);

            foreach (var (groups, otherGroups, suffix) in new[]
            {
                (overview.Departments, overview.OtherDepartments, "Team"),
                (overview.Countries, overview.OtherCountries, "Land"),
            })
            {
                CollectionAssert.AreEqual(
                    new[] { "Contoso Ten " + suffix, "Contoso Five " + suffix, null },
                    groups.Select(group => group.Name).ToArray(),
                    "The five-person group keeps its name; the one- and four-person groups do not.");
                var other = groups.Last();
                Assert.IsTrue(other.Other);
                Assert.AreEqual(LicenceActivityDemographic.OtherId, other.Id);
                Assert.AreEqual(5, other.AssignedUsers, "1 + 4 people.");
                Assert.AreEqual(2, otherGroups);
                var teams = other.Workloads.Single(item => item.Workload == "teams");
                Assert.AreEqual(1, teams.High, "The one person's own activity is only ever one of five.");
                Assert.AreEqual(5, teams.High + teams.Moderate + teams.Low + teams.Zero + teams.Unknown);
                Assert.IsTrue(groups.Where(group => !group.Other).All(group => group.AssignedUsers >= 5));
            }

            var one = overview.Licences.Single(sku => sku.LicenceTypeId == 2);
            var four = overview.Licences.Single(sku => sku.LicenceTypeId == 3);
            var five = overview.Licences.Single(sku => sku.LicenceTypeId == 4);
            foreach (var withheld in new[] { one, four })
            {
                Assert.IsTrue(withheld.Withheld, withheld.Name);
                Assert.AreEqual(0, withheld.Workloads.Count, withheld.Name);
                Assert.IsNull(withheld.AdoptionScore, withheld.Name);
            }
            Assert.AreEqual(1, one.AssignedUsers, "How many hold a licence is not anybody's activity.");
            Assert.AreEqual(4, four.AssignedUsers);
            Assert.IsFalse(five.Withheld);
            Assert.AreEqual(5, five.Workloads.Count);
            Assert.IsNotNull(five.AdoptionScore);
            Assert.AreEqual(5, overview.AllLicences.Workloads.Count, "Everyone holding a licence is twenty people.");

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(overview);
            foreach (var hidden in new[] { "Contoso One Team", "Contoso Four Team", "Contoso One Land", "Contoso Four Land" })
                Assert.IsFalse(json.Contains(hidden), hidden + " must not reach a reader without See PII.");
        }

        [TestMethod]
        public void WithSeePii_EveryGroupIsShownAsBefore()
        {
            var overview = SmallGroupsModel(Query()).BuildOverview(Query(), CancellationToken.None);

            Assert.IsNull(overview.Query.MinimumGroupSize);
            Assert.IsFalse(overview.Suppressed);
            CollectionAssert.AreEqual(
                new[] { "Contoso Ten Team", "Contoso Five Team", "Contoso Four Team", "Contoso One Team" },
                overview.Departments.Select(group => group.Name).ToArray());
            CollectionAssert.AreEqual(new[] { 10, 5, 4, 1 }, overview.Countries.Select(group => group.AssignedUsers).ToArray());
            Assert.IsFalse(overview.Departments.Concat(overview.Countries).Any(group => group.Other));
            Assert.AreEqual(0, overview.OtherDepartments);
            Assert.AreEqual(0, overview.OtherCountries);
            Assert.AreEqual(1, overview.Departments.Single(group => group.Name == "Contoso One Team")
                .Workloads.Single(item => item.Workload == "teams").High);
            var one = overview.Licences.Single(sku => sku.LicenceTypeId == 2);
            Assert.IsFalse(one.Withheld);
            Assert.AreEqual(5, one.Workloads.Count);
            Assert.AreEqual(100.0 * 4 / 16, one.AdoptionScore, "Four of the one holder's sixteen measured weeks were active.");
        }

        [TestMethod]
        public void WithoutSeePii_WhenTheSmallGroupsComeToFewerThanFive_TheSmallestNamedGroupIsFoldedToo()
        {
            // Otherwise the folded row - and everyone less the named rows - would be just those two people.
            var users = Enumerable.Range(1, 17).Select(id => User(
                id,
                departmentId: id <= 2 ? 1 : id <= 7 ? 2 : 3,
                department: id <= 2 ? "Contoso Pair" : id <= 7 ? "Contoso Five" : "Contoso Ten")).ToArray();
            var model = Model(new[] { Sku(1, "Contoso Suite") }, users,
                users.Select(user => Member(user.UserId, 1)).ToArray(), AvailableCoverage(), Scores());

            var overview = model.BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "Contoso Ten", null }, overview.Departments.Select(group => group.Name).ToArray());
            Assert.AreEqual(7, overview.Departments.Last().AssignedUsers);
            Assert.AreEqual(2, overview.OtherDepartments);
        }

        [TestMethod]
        public void WithoutSeePii_FewerThanFivePeopleWithALicence_WithholdsEveryFigure_ButFiveDoNot()
        {
            foreach (var people in new[] { 1, 4 })
            {
                var model = Model(new[] { Sku(1, "Contoso Suite") }, Users(Enumerable.Range(1, people).ToArray()),
                    Enumerable.Range(1, people).Select(id => Member(id, 1)).ToArray(), AvailableCoverage(),
                    Scores(("teams", 1, Score(4, 4))));

                var overview = model.BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);

                Assert.IsTrue(overview.Suppressed, people + " people");
                Assert.AreEqual(people, overview.DistinctAssignedUsers, "The count stays.");
                Assert.AreEqual(people, overview.AllLicences.AssignedUsers);
                Assert.IsNull(overview.AllLicences.AdoptionScore);
                Assert.AreEqual(0, overview.AllLicences.Workloads.Count);
                Assert.IsTrue(overview.Licences.Single().Withheld);
                Assert.AreEqual(people, overview.Licences.Single().AssignedUsers);
                Assert.AreEqual(0, overview.Departments.Count);
                Assert.AreEqual(0, overview.Countries.Count);

                var withSeePii = model.BuildOverview(Query(), CancellationToken.None);
                Assert.IsFalse(withSeePii.Suppressed, "A See PII reader sees the few people's figures.");
                Assert.AreEqual(5, withSeePii.AllLicences.Workloads.Count);
                Assert.AreEqual(1, withSeePii.Departments.Count);
            }

            var five = Model(new[] { Sku(1, "Contoso Suite") }, Users(1, 2, 3, 4, 5),
                Enumerable.Range(1, 5).Select(id => Member(id, 1)).ToArray(), AvailableCoverage(), Scores())
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            Assert.IsFalse(five.Suppressed);
            Assert.IsFalse(five.Licences.Single().Withheld);
            Assert.AreEqual(5, five.Departments.Single().AssignedUsers);
        }

        [TestMethod]
        public void WithoutSeePii_TheWorkbookIsTheSameFoldedFigures()
        {
            var overview = SmallGroupsModel(Query()).BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            overview.SnapshotId = "synthetic-overview";

            var xml = WorksheetXml(LicenceActivityWorkbook.Build(overview));

            StringAssert.Contains(xml, "Smaller groups combined (2 groups of fewer than 5 people)");
            StringAssert.Contains(xml, "Not shown: fewer than 5 people hold this licence");
            StringAssert.Contains(xml, "Contoso Five Team");
            foreach (var hidden in new[] { "Contoso One Team", "Contoso Four Team", "Contoso One Land", "Contoso Four Land" })
                Assert.IsFalse(xml.Contains(hidden), hidden);

            var full = SmallGroupsModel(Query()).BuildOverview(Query(), CancellationToken.None);
            full.SnapshotId = "synthetic-overview";
            var fullXml = WorksheetXml(LicenceActivityWorkbook.Build(full));
            StringAssert.Contains(fullXml, "Contoso One Team");
            Assert.IsFalse(fullXml.Contains("Not shown"));
        }

        // --- Complementary withholding across licences (#844): "Everyone holding a licence" is the union of the licences'
        // holders, published beside them, so withholding a small licence has to hold against it -----------------------

        [TestMethod]
        public void WithoutSeePii_ASmallLicence_IsWithheldWithTheSmallestLicenceThatWouldOtherwiseGiveItBack()
        {
            // A, B and C are held by different people, and only B's three are active: everyone less A less C is B.
            var overview = ComplementModel(Query()).BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(overview);

            var a = overview.Licences.Single(sku => sku.Name == "Contoso A");
            var b = overview.Licences.Single(sku => sku.Name == "Contoso B");
            var c = overview.Licences.Single(sku => sku.Name == "Contoso C");
            Assert.IsTrue(b.Withheld);
            Assert.AreEqual(LicenceActivityWithheldReasons.FewHolders, b.WithheldReason);
            Assert.IsTrue(c.Withheld, "With C shown, everyone less A less C would be B's three people.");
            Assert.AreEqual(LicenceActivityWithheldReasons.RevealsFewPeople, c.WithheldReason);
            Assert.AreEqual(0, c.Workloads.Count, "Withheld, never 0.");
            Assert.IsNull(c.AdoptionScore);
            Assert.IsFalse(a.Withheld);
            Assert.IsNull(a.WithheldReason);
            CollectionAssert.AreEqual(new[] { 12, 3, 6 }, new[] { a, b, c }.Select(sku => sku.AssignedUsers).ToArray(),
                "How many hold each licence stays.");
            Assert.AreEqual(21, overview.AllLicences.AssignedUsers, "Everyone holding a licence stays, with its figures.");
            Assert.AreEqual(5, overview.AllLicences.Workloads.Count);

            // All the reader can now take away from everyone is A: B's three and C's six together.
            var teams = Difference(overview.AllLicences.Workloads, a.Workloads, "teams");
            Assert.AreEqual(9, teams.Sum());
            Assert.AreEqual(3, teams[0], "B's three active people, among nine.");
        }

        [TestMethod]
        public void WithoutSeePii_OverlappingLicences_TheLicenceWithheldIsOneThatActuallyHidesTheSmallOne()
        {
            // C's six holders all hold A as well. Withholding C - the smallest other licence - would hide nobody: everyone
            // less A would still be B's three. A has to go instead, and C, inside A, can stay.
            var nested = Licensed(new[] { 13, 14, 15 },
                (1, "Contoso A", People(1, 12)), (2, "Contoso B", People(13, 15)), (3, "Contoso C", People(1, 6)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(nested);
            Assert.AreEqual(LicenceActivityWithheldReasons.RevealsFewPeople, Licence(nested, "Contoso A").WithheldReason);
            Assert.AreEqual(LicenceActivityWithheldReasons.FewHolders, Licence(nested, "Contoso B").WithheldReason);
            Assert.IsFalse(Licence(nested, "Contoso C").Withheld);
            Assert.AreEqual(9, Difference(nested.AllLicences.Workloads, Licence(nested, "Contoso C").Workloads, "teams").Sum());

            // Two small licences held by the same three people are still three people: one licence goes with them.
            var same = Licensed(new[] { 13, 14, 15 },
                (1, "Contoso A", People(1, 12)), (2, "Contoso B1", People(13, 15)), (3, "Contoso B2", People(13, 15)),
                (4, "Contoso C", People(16, 21)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(same);
            CollectionAssert.AreEqual(
                new[] { null, LicenceActivityWithheldReasons.FewHolders, LicenceActivityWithheldReasons.FewHolders, LicenceActivityWithheldReasons.RevealsFewPeople },
                same.Licences.Select(sku => sku.WithheldReason).ToArray());

            // Two small licences of three different people each are six people together: nothing else needs to go.
            var six = Licensed(new[] { 13, 14, 15 },
                (1, "Contoso A", People(1, 12)), (2, "Contoso B1", People(13, 15)), (3, "Contoso B2", People(16, 18)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(six);
            Assert.IsFalse(Licence(six, "Contoso A").Withheld);
            Assert.AreEqual(6, Difference(six.AllLicences.Workloads, Licence(six, "Contoso A").Workloads, "teams").Sum());
        }

        [TestMethod]
        public void WithoutSeePii_ALicenceNearlyEveryoneHolds_IsWithheldWhenEveryoneLessItIsAHandfulOfPeople()
        {
            // Everyone holds Phone, so leaving out the people with no licence shown proves nothing: everyone less E5 is
            // still E3's three.
            var small = Licensed(new[] { 13, 14, 15 },
                (1, "Contoso E3", People(13, 15)), (2, "Contoso E5", People(1, 12)), (3, "Contoso Phone", People(1, 15)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(small);
            Assert.AreEqual(LicenceActivityWithheldReasons.FewHolders, Licence(small, "Contoso E3").WithheldReason);
            Assert.AreEqual(LicenceActivityWithheldReasons.RevealsFewPeople, Licence(small, "Contoso E5").WithheldReason);
            Assert.IsFalse(Licence(small, "Contoso Phone").Withheld);

            // The same three people with no licence of their own beside Phone: no licence is small, but E5 still goes.
            var none = Licensed(new[] { 13, 14, 15 },
                (1, "Contoso E5", People(1, 12)), (2, "Contoso Phone", People(1, 15)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(none);
            Assert.AreEqual(LicenceActivityWithheldReasons.RevealsFewPeople, Licence(none, "Contoso E5").WithheldReason);
            Assert.IsFalse(Licence(none, "Contoso Phone").Withheld);
        }

        [TestMethod]
        public void WithoutSeePii_ALicenceLessTheLicencesInsideIt_NeverGivesBackASmallOne()
        {
            // X (4, small) and Y (6) are both inside E5; nothing taken from everyone isolates X, but E5 less Y is X.
            var overview = Licensed(new[] { 1, 2, 3, 4 },
                (1, "Contoso E5", People(1, 10)), (2, "Contoso W", new[] { 5 }.Concat(People(11, 20)).ToArray()),
                (3, "Contoso X", People(1, 4)), (4, "Contoso Y", People(5, 10)))
                .BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            AssertNoDifferenceGivesBackFewerThanFive(overview);

            Assert.AreEqual(LicenceActivityWithheldReasons.FewHolders, Licence(overview, "Contoso X").WithheldReason);
            Assert.AreEqual(LicenceActivityWithheldReasons.RevealsFewPeople, Licence(overview, "Contoso Y").WithheldReason,
                "The smaller of E5 and Y.");
            Assert.IsFalse(Licence(overview, "Contoso E5").Withheld);
            Assert.IsFalse(Licence(overview, "Contoso W").Withheld);
        }

        [TestMethod]
        public void WithSeePii_NoLicenceIsWithheld_AsBefore()
        {
            var models = new[]
            {
                ComplementModel(Query()),
                Licensed(new[] { 13, 14, 15 },
                    (1, "Contoso A", People(1, 12)), (2, "Contoso B", People(13, 15)), (3, "Contoso C", People(1, 6))),
                Licensed(new[] { 13, 14, 15 }, (1, "Contoso E5", People(1, 12)), (2, "Contoso Phone", People(1, 15))),
            };
            foreach (var model in models)
            {
                var overview = model.BuildOverview(Query(), CancellationToken.None);
                Assert.IsTrue(overview.Licences.All(sku => !sku.Withheld && sku.WithheldReason == null && sku.Workloads.Count == 5));
                Assert.IsTrue(overview.Licences.All(sku => sku.AdoptionScore != null));
            }

            var b = ComplementModel(Query()).BuildOverview(Query(), CancellationToken.None).Licences.Single(sku => sku.Name == "Contoso B");
            Assert.AreEqual(3, b.Workloads.Single(item => item.Workload == "teams").High, "B's three people's own activity.");
        }

        [TestMethod]
        public void WithoutSeePii_TheWorkbookSaysWhyEachLicenceIsWithheld_AsTheScreenDoes()
        {
            var overview = ComplementModel(Query()).BuildOverview(Query().WithMinimumGroupSize(5), CancellationToken.None);
            overview.SnapshotId = "synthetic-overview";

            var xml = WorksheetXml(LicenceActivityWorkbook.Build(overview));

            StringAssert.Contains(xml, "Not shown: fewer than 5 people hold this licence");
            StringAssert.Contains(xml,
                "and the other licences, it would reveal the activity of fewer than 5 people");
            Assert.AreEqual(1, Occurrences(xml, "it would reveal the activity of fewer than 5 people"), "C's row, once.");
            Assert.AreEqual(2, Occurrences(xml, "Not shown: "), "B's and C's rows, each without a figure.");

            var full = ComplementModel(Query()).BuildOverview(Query(), CancellationToken.None);
            full.SnapshotId = "synthetic-overview";
            Assert.IsFalse(WorksheetXml(LicenceActivityWorkbook.Build(full)).Contains("Not shown"));
        }

        [TestMethod]
        public void WhenTheSearchRunsOutOfBudget_TheLicencesInsideTheFigureAreWithheld_NeverShownUnchecked()
        {
            // Suite (slot 0) is everyone's; C (slot 1) five of them. Nothing leaks, and a finished search withholds nothing.
            var profiles = new List<ulong[]> { new[] { 3UL }, new[] { 1UL } };
            var people = new[] { 5, 15 };
            var holders = new[] { 20, 5 };

            var withheld = new bool[2];
            var complement = LicenceActivityComplements.Withhold(profiles, people, holders, withheld, 5, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { false, false }, complement);

            // A search that can't finish proves nothing, so the licences it could not clear are withheld, smallest first,
            // until enough people hold none of those left: here, all of them.
            withheld = new bool[2];
            complement = LicenceActivityComplements.Withhold(profiles, people, holders, withheld, 5, CancellationToken.None, searchBudget: 0);
            CollectionAssert.AreEqual(new[] { true, true }, complement);
            CollectionAssert.AreEqual(new[] { true, true }, withheld);
        }

        /// <summary>
        /// What a reader without See PII can work out from the licence table alone: each figure shown - a licence, or
        /// everyone - less any selection of the other licences shown. Fails on a difference that is 1-4 people: every band
        /// of every service at least 0, and 1-4 people in it. A selection that overlaps can come out non-negative by
        /// coincidence, so the data these tests use keeps that from happening.
        /// </summary>
        private static void AssertNoDifferenceGivesBackFewerThanFive(LicenceActivityOverview overview)
        {
            var shown = overview.Licences.Where(sku => !sku.Withheld && sku.AssignedUsers > 0).ToList();
            var figures = shown.Select(sku => (sku.Name, sku.Workloads)).ToList();
            figures.Add(("Everyone holding a licence", overview.AllLicences.Workloads));
            foreach (var figure in figures)
            {
                var others = shown.Where(sku => sku.Name != figure.Name).ToList();
                for (var selection = 1; selection < 1 << others.Count; selection++)
                {
                    var taken = others.Where((sku, index) => (selection & (1 << index)) != 0).ToList();
                    var bands = LicenceActivityQuery.Workloads.Select(workload =>
                        taken.Aggregate(Bands(figure.Workloads, workload),
                            (left, sku) => left.Zip(Bands(sku.Workloads, workload), (x, y) => x - y).ToArray())).ToList();
                    var people = bands[0].Sum();
                    Assert.IsFalse(people > 0 && people < 5 && bands.All(service => service.All(count => count >= 0)),
                        figure.Name + " less " + string.Join(", ", taken.Select(sku => sku.Name)) + " is " + people + " people's activity.");
                }
            }
        }

        private static int[] Bands(List<LicenceActivityDistribution> workloads, string workload)
        {
            var item = workloads.Single(distribution => distribution.Workload == workload);
            return new[] { item.High, item.Moderate, item.Low, item.Zero, item.Unknown };
        }

        private static int[] Difference(List<LicenceActivityDistribution> from, List<LicenceActivityDistribution> less, string workload) =>
            Bands(from, workload).Zip(Bands(less, workload), (x, y) => x - y).ToArray();

        private static LicenceActivitySku Licence(LicenceActivityOverview overview, string name) =>
            overview.Licences.Single(sku => sku.Name == name);

        private static int Occurrences(string text, string value)
        {
            var count = 0;
            for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        private static int[] People(int from, int to) => Enumerable.Range(from, to - from + 1).ToArray();

        /// <summary>
        /// People holding the licences listed, everyone fully measured; only <paramref name="active"/> used Teams, every week.
        /// </summary>
        private static LicenceActivityReadModel Licensed(int[] active, params (int Id, string Name, int[] Holders)[] licences) =>
            Licensed(Query(), active, licences);

        private static LicenceActivityReadModel Licensed(
            LicenceActivityQuery range, int[] active, params (int Id, string Name, int[] Holders)[] licences) =>
            new LicenceActivityReadModel(
                range,
                licences.Select(licence => Sku(licence.Id, licence.Name)).ToArray(),
                Users(licences.SelectMany(licence => licence.Holders).Distinct().OrderBy(id => id).ToArray()),
                licences.SelectMany(licence => licence.Holders.Select(id => Member(id, licence.Id))).ToArray(),
                AvailableCoverage(),
                Scores(active.Select(id => ("teams", id, Score(4, 4))).ToArray()));

        /// <summary>
        /// Licences A, B and C, held by 12, 3 and 6 different people; B's three are the only ones with any Teams activity.
        /// </summary>
        internal static LicenceActivityReadModel ComplementModel(LicenceActivityQuery range) =>
            Licensed(range, new[] { 13, 14, 15 },
                (1, "Contoso A", People(1, 12)), (2, "Contoso B", People(13, 15)), (3, "Contoso C", People(16, 21)));

        private static string WorksheetXml(byte[] workbook)
        {
            using (var archive = new System.IO.Compression.ZipArchive(new System.IO.MemoryStream(workbook)))
                return string.Join("\n", archive.Entries
                    .Where(e => e.FullName.StartsWith("xl/", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                    .Select(e => new System.IO.StreamReader(e.Open()).ReadToEnd()));
        }

        /// <summary>
        /// Twenty people in departments - each its own country - of 1, 4, 5 and 10. Everyone holds the suite; the one
        /// person in the smallest department also holds licence 2, four people licence 3 and five licence 4. The
        /// one person is the only one with any Teams activity.
        /// </summary>
        internal static LicenceActivityReadModel SmallGroupsModel(LicenceActivityQuery range)
        {
            var users = new List<LicenceActivityDirectoryUser>();
            foreach (var (id, size, name) in new[] { (1, 1, "One"), (2, 4, "Four"), (3, 5, "Five"), (4, 10, "Ten") })
                for (var member = 0; member < size; member++)
                    users.Add(User(users.Count + 1, departmentId: id, department: "Contoso " + name + " Team",
                        countryId: id, country: "Contoso " + name + " Land"));
            var memberships = users.Select(user => Member(user.UserId, 1)).ToList();
            memberships.Add(Member(1, 2));
            memberships.AddRange(new[] { 2, 3, 4, 5 }.Select(id => Member(id, 3)));
            memberships.AddRange(new[] { 6, 7, 8, 9, 10 }.Select(id => Member(id, 4)));
            return new LicenceActivityReadModel(
                range,
                new[] { Sku(1, "Contoso Suite"), Sku(2, "Contoso Add-on A"), Sku(3, "Contoso Add-on B"), Sku(4, "Contoso Add-on C") },
                users, memberships, AvailableCoverage(), Scores(("teams", 1, Score(4, 4))));
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
