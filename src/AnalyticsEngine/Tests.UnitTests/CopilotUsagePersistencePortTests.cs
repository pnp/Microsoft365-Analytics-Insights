using Common.Entities.Entities.UsageReports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Copilot usage-report import driven entirely through <see cref="ICopilotUsagePersistenceManager"/>
    /// (issue #370): zero Graph, zero SQL Server.
    ///
    /// The point of the port is the <b>Unchanged</b> count. Both Copilot upserts carry an "only write when a
    /// value actually moved" rule - Graph gap-fills the last few days, so re-importing an overlapping window
    /// is normal - and before this the rule was invisible: a regression that rewrote every row on every cycle
    /// (up to 180 days x every app, or every licensed user) returned exactly the same numbers.
    /// </summary>
    [TestClass]
    public class CopilotUsagePersistencePortTests
    {
        private static readonly DateTime ReportDate = new DateTime(2031, 3, 19);

        private sealed class StubReportSource : ICopilotReportSource
        {
            private readonly List<JObject> _report;
            public StubReportSource(List<JObject> report) { _report = report; }
            public Task<List<JObject>> LoadReportAsync(CopilotReportRequest request) => Task.FromResult(_report);
        }

        private static List<JObject> TrendReport(int activeUsers, string refreshDate = null)
        {
            var date = ReportDate.ToString("yyyy-MM-dd");
            return new List<JObject>
            {
                new JObject
                {
                    ["reportRefreshDate"] = refreshDate ?? date,
                    ["reportPeriod"] = 28,
                    ["adoptionByDate"] = new JArray
                    {
                        new JObject
                        {
                            ["reportDate"] = date,
                            ["anyAppEnabledUsers"] = 250,
                            ["anyAppActiveUsers"] = activeUsers,
                        }
                    },
                }
            };
        }

        private static List<JObject> UserDetailReport(string upn, int periodDays, int prompts, int activeDays)
        {
            var date = ReportDate.ToString("yyyy-MM-dd");
            return new List<JObject>
            {
                new JObject
                {
                    ["reportRefreshDate"] = date,
                    ["userPrincipalName"] = upn,
                    ["displayName"] = "Καλημέρα Κόσμε",
                    ["lastActivityDate"] = date,
                    ["copilotActivityUserDetailsByPeriod"] = new JArray
                    {
                        new JObject
                        {
                            ["reportPeriod"] = periodDays,
                            ["promptsSubmitted"] = prompts,
                            ["activeUsageDays"] = activeDays,
                        }
                    },
                }
            };
        }

        private static CopilotUserCountReportLoader CountLoader(List<JObject> report, ICopilotUsagePersistenceManager persistence)
            => new CopilotUserCountReportLoader(new StubReportSource(report), NullLogger.Instance, persistence);

        private static CopilotUsageUserDetailLoader DetailLoader(List<JObject> report, ICopilotUsagePersistenceManager persistence)
            => new CopilotUsageUserDetailLoader(new StubReportSource(report), NullLogger.Instance, null, null, persistence);

        private static CopilotReportRequest TrendRequest() =>
            new CopilotReportRequest(CopilotReportNames.UserCountTrend, "D28", CopilotReportVersions.V2);

        private static CopilotReportRequest DetailRequest(string period = "D28") =>
            new CopilotReportRequest(CopilotReportNames.UsageUserDetail, period, CopilotReportVersions.V2);

        // ---- The "only update when values change" rule -------------------------------------------------

        [TestMethod]
        public async Task CopilotUserCount_ValuesUnchanged_ReportsUnchanged_AndDoesNotRewriteRows()
        {
            var store = new InMemoryCopilotUsagePersistenceManager();

            Assert.AreEqual(1, await CountLoader(TrendReport(180), store).LoadAndSaveAsync(TrendRequest()),
                "The first import inserts the row.");

            var written = await CountLoader(TrendReport(180), store).LoadAndSaveAsync(TrendRequest());

            Assert.AreEqual(0, written, "Re-importing identical values must write nothing.");

            // The SECOND import is the interesting event, and this is its own result - not the store's rows
            // fed back into itself, which would compare each row against its own reference and pass
            // regardless of what the rule does.
            Assert.AreEqual(1, store.LastUserCountUpsert.Unchanged);
            Assert.AreEqual(0, store.LastUserCountUpsert.Updated);
            Assert.AreEqual(0, store.LastUserCountUpsert.Inserted);
        }

        [TestMethod]
        public async Task CopilotUserCount_ValueChanged_ReportsUpdated()
        {
            var store = new InMemoryCopilotUsagePersistenceManager();
            await CountLoader(TrendReport(180), store).LoadAndSaveAsync(TrendRequest());

            var written = await CountLoader(TrendReport(191), store).LoadAndSaveAsync(TrendRequest());

            Assert.AreEqual(1, written);
            Assert.AreEqual(1, store.LastUserCountUpsert.Updated);
            Assert.AreEqual(0, store.LastUserCountUpsert.Unchanged);
            Assert.AreEqual(191, store.UserCounts.Values.Single().ActiveUsers);
        }

        [TestMethod]
        public async Task CopilotUserCount_OnlyTheRefreshDateMoved_IsStillUnchanged()
        {
            // Deliberate: the refresh date advances every day, so counting it as a change would rewrite every
            // row in the window daily purely to restamp provenance. Report-level freshness lives on the
            // import log instead.
            var store = new InMemoryCopilotUsagePersistenceManager();
            await CountLoader(TrendReport(180, "2031-03-20"), store).LoadAndSaveAsync(TrendRequest());

            var written = await CountLoader(TrendReport(180, "2031-03-21"), store).LoadAndSaveAsync(TrendRequest());

            Assert.AreEqual(0, written);
        }

        // ---- Per-user detail --------------------------------------------------------------------------

        [TestMethod]
        public async Task CopilotUsage_SameReportImportedTwice_IsIdempotent()
        {
            var store = new InMemoryCopilotUsagePersistenceManager();
            store.SeedUser("ada@contoso.onmicrosoft.com");

            Assert.AreEqual(1, await DetailLoader(UserDetailReport("ada@contoso.onmicrosoft.com", 28, 142, 19), store)
                .LoadAndSaveAsync(DetailRequest()));

            Assert.AreEqual(0, await DetailLoader(UserDetailReport("ada@contoso.onmicrosoft.com", 28, 142, 19), store)
                .LoadAndSaveAsync(DetailRequest()), "Nothing moved, so nothing should be rewritten.");

            Assert.AreEqual(1, store.UserDetail.Count);
        }

        [TestMethod]
        public async Task CopilotUsage_DifferentPeriodsAreDifferentFacts_NotAConflict()
        {
            // D7 and D28 describe the SAME user and date with different prompt counts, so both must be
            // stored. Collapsing them would make one period silently overwrite the other.
            var store = new InMemoryCopilotUsagePersistenceManager();
            store.SeedUser("ada@contoso.onmicrosoft.com");

            await DetailLoader(UserDetailReport("ada@contoso.onmicrosoft.com", 7, 31, 4), store).LoadAndSaveAsync(DetailRequest("D7"));
            await DetailLoader(UserDetailReport("ada@contoso.onmicrosoft.com", 28, 142, 19), store).LoadAndSaveAsync(DetailRequest("D28"));

            Assert.AreEqual(2, store.UserDetail.Count);
            CollectionAssert.AreEquivalent(new[] { 7, 28 }, store.UserDetail.Values.Select(r => r.ReportPeriodDays).ToArray());
        }

        [TestMethod]
        public async Task CopilotUsage_UserNotInDatabase_OnAKnownDomain_IsCreated()
        {
            var store = new InMemoryCopilotUsagePersistenceManager();
            store.SeedUser("someone.else@contoso.onmicrosoft.com");   // establishes the known domain

            var written = await DetailLoader(UserDetailReport("καλημέρα@contoso.onmicrosoft.com", 28, 10, 2), store)
                .LoadAndSaveAsync(DetailRequest());

            Assert.AreEqual(1, written);
            Assert.IsTrue(store.Users.ContainsKey("καλημέρα@contoso.onmicrosoft.com"),
                "A non-Latin UPN on a recognised domain must be created, not dropped.");
        }

        [TestMethod]
        public async Task CopilotUsage_UserOnAnUnknownDomain_IsSkippedWithoutThrowing()
        {
            // An unrecognised domain is how a pseudonymised report would look, so the identity is skipped
            // rather than invented - but the import must still complete.
            var store = new InMemoryCopilotUsagePersistenceManager();
            store.SeedUser("ada@contoso.onmicrosoft.com");

            var written = await DetailLoader(UserDetailReport("mallory@someone-else.example", 28, 10, 2), store)
                .LoadAndSaveAsync(DetailRequest());

            Assert.AreEqual(0, written);
            Assert.AreEqual(1, store.Users.Count, "No user should have been created on the unknown domain.");
            Assert.AreEqual(0, store.UserDetail.Count);
        }

        // ---- Diagnostics ------------------------------------------------------------------------------

        [TestMethod]
        public async Task CopilotUsage_PersistenceFailure_IsRecordedOnTheFailurePath()
        {
            // In production that path writes on a FRESH context, because the one that just failed a
            // SaveChanges can be left holding entities in a broken state - losing the very diagnostic the
            // Health page needs.
            var store = new InMemoryCopilotUsagePersistenceManager
            {
                FailUserDetailUpsertWith = new InvalidOperationException("SQL went away")
            };
            store.SeedUser("ada@contoso.onmicrosoft.com");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                DetailLoader(UserDetailReport("ada@contoso.onmicrosoft.com", 28, 1, 1), store).LoadAndSaveAsync(DetailRequest()));

            Assert.AreEqual(1, store.ImportLogsRecordedAfterFailure.Count);
            StringAssert.Contains(store.ImportLogsRecordedAfterFailure.Single().Error, "SQL went away");
        }

        [TestMethod]
        public async Task CopilotUsage_ConcealedTenant_AbortsWithoutWritingAnyUsageRow()
        {
            // The highest-stakes decision in this import: importing a concealed report would create one
            // placeholder user per licensed account. Only the diagnostic may be written.
            var date = ReportDate.ToString("yyyy-MM-dd");
            var concealed = new List<JObject>
            {
                new JObject
                {
                    ["reportRefreshDate"] = date,
                    ["userPrincipalName"] = "6DA1F2E0A1E8D5B1C4F7A9B2C3D4E5F6",
                    ["displayName"] = "1B2C3D4E5F60718293A4B5C6D7E8F901",
                    ["lastActivityDate"] = date,
                    ["copilotActivityUserDetailsByPeriod"] = new JArray
                    {
                        new JObject { ["reportPeriod"] = 28, ["promptsSubmitted"] = 5, ["activeUsageDays"] = 1 }
                    },
                }
            };

            var store = new InMemoryCopilotUsagePersistenceManager();
            store.SeedUser("ada@contoso.onmicrosoft.com");

            var written = await DetailLoader(concealed, store).LoadAndSaveAsync(DetailRequest());

            Assert.AreEqual(0, written);
            Assert.AreEqual(0, store.UserDetail.Count);
            Assert.AreEqual(1, store.Users.Count, "No placeholder user may be created for a concealed identity.");
            Assert.IsTrue(store.ImportLogs.Single().IsUpnObfuscated,
                "The reason must reach the Health page rather than looking like an empty tenant.");
        }

        // ---- The period-key rule ----------------------------------------------------------------------

        [TestMethod]
        public void CopilotUsage_PeriodKeyNormalisation_FillsFromTheRequestAndDropsWhatItCannotKey()
        {
            var rows = new List<CopilotUsageUserDetailRow>
            {
                new CopilotUsageUserDetailRow { UserPrincipalName = "a@contoso.com", ReportPeriodDays = 7 },
                new CopilotUsageUserDetailRow { UserPrincipalName = "b@contoso.com", ReportPeriodDays = null },
            };

            var dropped = CopilotUsageReportPolicy.ApplyPeriodKeys(rows, 28);

            Assert.AreEqual(0, dropped);
            Assert.AreEqual(7, rows[0].ReportPeriodDays, "A row that states its own period keeps it.");
            Assert.AreEqual(28, rows[1].ReportPeriodDays, "A row without one takes the requested period.");
        }

        [TestMethod]
        public void CopilotUsage_PeriodKeyNormalisation_RowWithNoPeriodAndNoRequestPeriod_IsDroppedInPlace()
        {
            // Period ALL supplies no number, so such a row has no identity at all and would otherwise be
            // stored under the meaningless period 0.
            var keepable = new CopilotUsageUserDetailRow { UserPrincipalName = "a@contoso.com", ReportPeriodDays = 7 };
            var rows = new List<CopilotUsageUserDetailRow>
            {
                new CopilotUsageUserDetailRow { UserPrincipalName = "b@contoso.com", ReportPeriodDays = null },
                keepable,
                new CopilotUsageUserDetailRow { UserPrincipalName = "c@contoso.com", ReportPeriodDays = null },
            };

            var dropped = CopilotUsageReportPolicy.ApplyPeriodKeys(rows, null);

            Assert.AreEqual(2, dropped);
            Assert.AreEqual(1, rows.Count);
            Assert.AreSame(keepable, rows[0], "Filtering must compact the caller's own list, not replace it.");
        }

        // ---- The new-user domain rule -----------------------------------------------------------------

        [TestMethod]
        public void CopilotUsage_PlanNewUsers_OnlyCreatesIdentitiesOnDomainsTheDatabaseAlreadyKnows()
        {
            var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["ada@contoso.onmicrosoft.com"] = 1 };
            var knownDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "contoso.onmicrosoft.com" };

            var plan = CopilotUsageReportPolicy.PlanNewUsers(
                new[] { "ada@contoso.onmicrosoft.com", "grace@contoso.onmicrosoft.com", "mallory@elsewhere.example", "6DA1F2E0A1E8D5B1" },
                existing, knownDomains);

            CollectionAssert.AreEqual(new[] { "grace@contoso.onmicrosoft.com" }, plan.ToCreate.ToArray());
            Assert.AreEqual(2, plan.SkippedUnknownDomain, "The foreign domain and the bare hash are both rejected.");
        }

        [TestMethod]
        public void CopilotUsage_PlanNewUsers_EmptyDatabase_CreatesEverythingSoAFreshInstallCanProgress()
        {
            var plan = CopilotUsageReportPolicy.PlanNewUsers(
                new[] { "ada@contoso.onmicrosoft.com", "grace@contoso.onmicrosoft.com" },
                new Dictionary<string, int>(), new HashSet<string>());

            Assert.AreEqual(2, plan.ToCreate.Count);
            Assert.AreEqual(0, plan.SkippedUnknownDomain);
        }

        [TestMethod]
        public void CopilotUsage_PlanNewUsers_IsCaseInsensitiveSoOneUserIsNotCreatedTwice()
        {
            var plan = CopilotUsageReportPolicy.PlanNewUsers(
                new[] { "Ada@Contoso.OnMicrosoft.com", "ada@contoso.onmicrosoft.com" },
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new HashSet<string>());

            Assert.AreEqual(1, plan.ToCreate.Count);
        }

        [TestMethod]
        public void CopilotUsage_DomainOf_HandlesTheShapesAConcealedReportProduces()
        {
            Assert.AreEqual("contoso.onmicrosoft.com", CopilotUsageReportPolicy.DomainOf("ada@contoso.onmicrosoft.com"));
            Assert.IsNull(CopilotUsageReportPolicy.DomainOf("6DA1F2E0A1E8D5B1C4F7A9B2C3D4E5F6"), "A bare hash has no domain.");
            Assert.IsNull(CopilotUsageReportPolicy.DomainOf("@contoso.com"));
            Assert.IsNull(CopilotUsageReportPolicy.DomainOf("ada@"));
            Assert.IsNull(CopilotUsageReportPolicy.DomainOf(null));
        }
    }
}
