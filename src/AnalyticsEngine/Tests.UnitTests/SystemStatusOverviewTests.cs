extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models;
using AnalyticsWeb::Web.AnalyticsWeb.Models.Health;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Data.Entity.Infrastructure.Interception;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the home page's import-aware figures (api/SystemStatus).
    ///
    /// The point of the gating is that a permanent "Teams calls: 0" on a tenant that never switched the
    /// calls import on is indistinguishable from a broken import - and it is the first thing an admin
    /// sees. It is also a performance decision: a figure that is not shown is never counted, so a
    /// deployment with the web-traffic import off no longer pays for a COUNT(*) over <c>hits</c> on
    /// every home page load.
    /// </summary>
    [TestClass]
    public class SystemStatusOverviewTests
    {
        private static ImportTaskSettings NothingEnabled() => new ImportTaskSettings();

        [TestMethod]
        public void ApplicableCountKeys_NothingEnabled_KeepsOnlyTheImportAgnosticFigures()
        {
            var keys = SystemStatusAPIController.ApplicableCountKeys(NothingEnabled());

            // Users are filled by every import, so the figure is meaningful whatever is switched on.
            CollectionAssert.AreEqual(new List<string> { "users" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_UnknownSettings_ShowsEverything()
        {
            // No saved config yet: showing every figure beats showing an empty home page, and it is the
            // behaviour the page reports to the user ("import settings couldn't be read").
            var keys = SystemStatusAPIController.ApplicableCountKeys(null);

            Assert.IsTrue(keys.Contains("teamsCalls"));
            Assert.IsTrue(keys.Contains("webHits"));
            Assert.IsTrue(keys.Contains("dlpMatches"));
        }

        [TestMethod]
        public void ApplicableCountKeys_ActivityLogOnly_DoesNotOfferWebTrafficFigures()
        {
            var keys = SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ActivityLog = true });

            CollectionAssert.AreEqual(new List<string> { "users", "auditEvents", "sharePointSites" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_WebTrafficOnly_AlsoOffersSharePointSites()
        {
            // sites are populated by either the audit feed or the web tracker, so either switch is enough.
            var keys = SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { WebTraffic = true });

            CollectionAssert.AreEqual(new List<string> { "users", "webHits", "trackedUrls", "sharePointSites" }, keys);
        }

        [TestMethod]
        public void ApplicableCountKeys_EachImportUnlocksItsOwnFigure()
        {
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { Copilot = true }).Contains("copilotInteractions"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { CopilotInteractionHistory = true }).Contains("copilotAiInteractions"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { SentEmails = true }).Contains("sentEmails"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphTeams = true }).Contains("teams"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphTeams = true }).Contains("teamsTracked"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { Calls = true }).Contains("teamsCalls"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ImportPowerPlatform = true }).Contains("powerApps"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { ImportDlp = true }).Contains("dlpMatches"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { GraphUsersMetadata = true }).Contains("licenceTypes"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { CopilotStudioCredits = true }).Contains("copilotStudioCreditDays"));
            Assert.IsTrue(SystemStatusAPIController.ApplicableCountKeys(new ImportTaskSettings { AzureCostManagement = true }).Contains("azureCostDays"));
        }

        [TestMethod]
        public void ApplicableCountKeys_AreUnique()
        {
            // The SPA keys its icon map - and the server keys its per-shape cache entry - off these, so a
            // duplicate would silently collide.
            var keys = SystemStatusAPIController.ApplicableCountKeys(null);

            Assert.AreEqual(keys.Count, keys.Distinct().Count());
        }

        /// <summary>
        /// A count that never finishes - a blocked scan - must not hold the landing page: it fails at its deadline, a
        /// request that gives up stops waiting at once, and the failure is reused briefly rather than started again by
        /// the next burst of requests.
        /// </summary>
        [TestMethod]
        [Timeout(30000)]
        public async Task SharedCount_ThatNeverFinishes_FailsAtItsDeadline_AndIsNotStartedAgainByTheNextRequest()
        {
            var calls = 0;
            var never = new TaskCompletionSource<List<NamedCountModel>>();
            Func<CancellationToken, Task<List<NamedCountModel>>> stalled = ct =>
            {
                Interlocked.Increment(ref calls);
                return never.Task;
            };

            using (var cache = new MemoryCache("SystemStatusOverviewTests.Deadline"))
            using (var gaveUp = new CancellationTokenSource())
            {
                var shared = new SharedComputation<List<NamedCountModel>>(cache, "test::", TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(300));

                var abandoned = shared.GetAsync("users", stalled, gaveUp.Token);
                var patient = shared.GetAsync("users", stalled, CancellationToken.None);
                gaveUp.Cancel();

                try
                {
                    await abandoned;
                    Assert.Fail("A request that gave up must stop waiting.");
                }
                catch (OperationCanceledException)
                {
                }

                await Assert.ThrowsExceptionAsync<TimeoutException>(() => patient);
                await Assert.ThrowsExceptionAsync<TimeoutException>(() => shared.GetAsync("users", stalled, CancellationToken.None));
                Assert.AreEqual(1, calls, "One count for every request, including the one after it failed.");
            }
        }

        /// <summary>
        /// Against a real database: every figure comes back, the whole-table ones from one read of the
        /// <c>sys.partitions</c> catalog view, and no table is counted row by row.
        /// </summary>
        [TestMethod]
        public async Task Figures_ComeFromOneCatalogRead_NotACountPerTable()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
            }

            var recorder = new CommandRecorder();
            List<NamedCountModel> figures;
            DbInterception.Add(recorder);
            try
            {
                figures = await SystemStatusAPIController.CountFiguresAsync(SystemStatusAPIController.AllCountDefinitions, CancellationToken.None);
            }
            finally
            {
                DbInterception.Remove(recorder);
            }

            CollectionAssert.AreEqual(
                SystemStatusAPIController.AllCountDefinitions.Select(d => d.Key).ToList(),
                figures.Select(f => f.Key).ToList());
            Assert.IsTrue(figures.All(f => f.Count.HasValue && f.Count >= 0));
            Assert.AreEqual(1, recorder.Commands.Count(c => c.Contains("sys.partitions")));
            Assert.IsFalse(recorder.Commands.Any(c => c.Contains("sys.dm_")),
                "A dynamic management view needs VIEW DATABASE STATE, which the web app's managed identity is not given.");
            Assert.IsFalse(recorder.Commands.Any(c => c.Contains("COUNT_BIG(*) FROM [dbo]")),
                "With the row counts available, no table is scanned to be counted.");
        }

        /// <summary>
        /// On an Entra-only server the web app signs in as its managed identity, which the installer makes only
        /// db_datareader, db_datawriter and db_ddladmin. The row counts were read from a DMV that needs VIEW DATABASE
        /// STATE, so they failed on every such deployment. Read here as a database user holding db_datareader alone.
        /// </summary>
        [TestMethod]
        public async Task RowCounts_NeedOnlyTheReadPermissionTheWebAppIsGiven()
        {
            const string reader = "ws_systemstatus_reader";
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                await AzureSqlTokenAuth.OpenAsync(db.Database.Connection);
                await ExecuteAsync(db, $"IF USER_ID(N'{reader}') IS NULL CREATE USER [{reader}] WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER [{reader}];");
                try
                {
                    await ExecuteAsync(db, $"EXECUTE AS USER = N'{reader}';");
                    Dictionary<string, long> counts;
                    try
                    {
                        // The test only proves something if this principal really is restricted.
                        Exception denied = null;
                        try
                        {
                            await ExecuteAsync(db, "SELECT TOP (1) 1 FROM sys.dm_db_partition_stats;");
                        }
                        catch (Exception ex)
                        {
                            denied = ex;
                        }

                        Assert.IsNotNull(denied, "A db_datareader member must not be able to read the DMV.");
                        StringAssert.Contains(denied.GetBaseException().Message, "VIEW DATABASE");

                        counts = await SqlHealthDataSource.LoadApproxRowCounts(db);
                    }
                    finally
                    {
                        await ExecuteAsync(db, "REVERT;");
                    }

                    Assert.IsTrue(counts.ContainsKey("users"), "Every table a reader may read is in the catalog view.");
                    Assert.IsTrue(counts.ContainsKey("audit_events"));
                }
                finally
                {
                    await ExecuteAsync(db, $"IF USER_ID(N'{reader}') IS NOT NULL DROP USER [{reader}];");
                }
            }
        }

        /// <summary>
        /// The row counts are keyed by table name, and the figures behind them read <c>[dbo]</c>. A same-named table in
        /// another schema (a reporting copy, an add-on's) used to be added into the dbo table's count.
        /// </summary>
        [TestMethod]
        public async Task RowCounts_IgnoreSameNamedTablesInOtherSchemas()
        {
            const string schema = "s10_rowcount_other";
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
                await AzureSqlTokenAuth.OpenAsync(db.Database.Connection);
                await ExecuteAsync(db, $"IF OBJECT_ID(N'[{schema}].[users]') IS NOT NULL DROP TABLE [{schema}].[users]; IF SCHEMA_ID(N'{schema}') IS NOT NULL DROP SCHEMA [{schema}];");
                var before = (await SqlHealthDataSource.LoadApproxRowCounts(db))["users"];
                await ExecuteAsync(db, $"EXEC(N'CREATE SCHEMA [{schema}]'); CREATE TABLE [{schema}].[users] (id int NOT NULL); INSERT [{schema}].[users] (id) VALUES (1),(2),(3),(4),(5);");
                try
                {
                    var after = await SqlHealthDataSource.LoadApproxRowCounts(db);

                    Assert.AreEqual(before, after["users"], "Only the [dbo] table is counted.");
                }
                finally
                {
                    await ExecuteAsync(db, $"DROP TABLE [{schema}].[users]; DROP SCHEMA [{schema}];");
                }
            }
        }

        /// <summary>
        /// A figure that cannot be counted keeps its place, without a count: dropped, it reads as an import that is
        /// switched off, and the page shows the audit and web freshness badges only when those figures are there.
        /// </summary>
        [TestMethod]
        public async Task AFigureThatCannotBeCounted_KeepsItsPlaceWithoutACount()
        {
            var users = SystemStatusAPIController.AllCountDefinitions.Single(d => d.Key == "users");
            var broken = new SystemStatusAPIController.DataCountDefinition(
                "auditEvents", "Audit events", "synthetic",
                (db, ct) => Task.FromException<int>(new TimeoutException("synthetic: Execution Timeout Expired")));

            var figures = await SystemStatusAPIController.CountFiguresAsync(new[] { users, broken }, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { "users", "auditEvents" }, figures.Select(f => f.Key).ToArray());
            Assert.IsTrue(figures[0].Count.HasValue);
            Assert.IsNull(figures[1].Count);
            Assert.AreEqual(JTokenType.Null, JObject.FromObject(figures[1])["count"].Type, "The page is sent a null, not a zero.");
            Assert.IsTrue(SystemStatusAPIController.AnyFigureUncounted(figures));

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => SystemStatusAPIController.CountFiguresAsync(new[] { broken }, CancellationToken.None),
                "Not one figure counted means the database is unreachable: an error, not a page of blanks.");
        }

        [TestMethod]
        public async Task AnAnswerWithAFigureMissing_IsReusedOnlyBriefly()
        {
            var calls = 0;
            Func<CancellationToken, Task<List<NamedCountModel>>> count = ct =>
            {
                var call = Interlocked.Increment(ref calls);
                return Task.FromResult(new List<NamedCountModel> { new NamedCountModel("users", "Users", call == 1 ? (long?)null : 3) });
            };

            using (var cache = new MemoryCache("SystemStatusOverviewTests.Degraded"))
            {
                var shared = new SharedComputation<List<NamedCountModel>>(cache, "test::",
                    TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30), SystemStatusAPIController.AnyFigureUncounted);

                Assert.IsNull((await shared.GetAsync("users", count, CancellationToken.None))[0].Count);
                await Task.Delay(400);
                Assert.AreEqual((long?)3, (await shared.GetAsync("users", count, CancellationToken.None))[0].Count);
                Assert.AreEqual((long?)3, (await shared.GetAsync("users", count, CancellationToken.None))[0].Count);
                Assert.AreEqual(2, calls, "The gap is filled soon after; the complete answer is then reused.");
            }
        }

        private static Task ExecuteAsync(AnalyticsEntitiesContext db, string sql)
            => db.Database.ExecuteSqlCommandAsync(System.Data.Entity.TransactionalBehavior.DoNotEnsureTransaction, sql);

        private sealed class CommandRecorder : IDbCommandInterceptor
        {
            private readonly List<string> _commands = new List<string>();

            internal IReadOnlyList<string> Commands
            {
                get { lock (_commands) return _commands.ToList(); }
            }

            private void Record(DbCommand command)
            {
                lock (_commands) _commands.Add(command.CommandText);
            }

            public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) => Record(command);
            public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) => Record(command);
            public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) => Record(command);
            public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext) { }
            public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext) { }
            public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext) { }
        }

        [TestMethod]
        public void DescribeEnabledImports_NullSettings_IsEmptyRatherThanThrowing()
        {
            CollectionAssert.AreEqual(new List<string>(), HealthService.DescribeEnabledImports(null));
        }

        /// <summary>
        /// A cold cache used to mean every request counted for itself - the burst after a recycle, or several admins
        /// opening the landing page, each ran every COUNT(*) in turn.
        /// </summary>
        [TestMethod]
        public async Task ConcurrentColdRequests_ShareOneCount()
        {
            var counts = 0;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<IReadOnlyList<SystemStatusAPIController.DataCountDefinition>, CancellationToken, Task<List<NamedCountModel>>> count = async (figures, ct) =>
            {
                Interlocked.Increment(ref counts);
                await release.Task;
                return figures.Select(f => new NamedCountModel(f.Key, f.Name, 3_000_000_000L, f.Hint)).ToList();
            };

            using (var cache = new MemoryCache("SystemStatusOverviewTests"))
            {
                var shared = new SharedComputation<List<NamedCountModel>>(cache, "test::", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
                using (var host = new PortalTestHost(
                    new[] { typeof(SystemStatusAPIController) },
                    PortalTestHost.SignedIn(),
                    PortalAccessPolicy.Enforcing,
                    _ => new SystemStatusAPIController(count, shared)))
                {
                    var requests = Enumerable.Range(0, 6).Select(_ => host.Client.GetAsync("api/SystemStatus")).ToArray();
                    for (var waited = 0; Volatile.Read(ref counts) == 0 && waited < 100; waited++) await Task.Delay(50);
                    await Task.Delay(200);
                    release.SetResult(true);

                    foreach (var response in await Task.WhenAll(requests))
                    {
                        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                        var figure = JObject.Parse(await response.Content.ReadAsStringAsync())["dataCounts"].First;
                        Assert.AreEqual(3_000_000_000L, (long)figure["count"], "A fact table can outgrow an int.");
                    }

                    Assert.AreEqual(1, counts, "Six cold requests must share one count.");
                    Assert.AreEqual(HttpStatusCode.OK, (await host.Client.GetAsync("api/SystemStatus")).StatusCode);
                    Assert.AreEqual(1, counts, "And the next request is answered from the cache.");
                }
            }
        }

        /// <summary>
        /// Whole-table figures come from the partition statistics, read once, and are tied to the right tables. Only
        /// the filtered tracked-Teams figure is a query of its own, on a small table.
        /// </summary>
        [TestMethod]
        public void WholeTableFigures_ReadTheirEntitysMappedTable_NotACountQuery()
        {
            var expected = new Dictionary<string, Type>
            {
                ["users"] = typeof(User),
                ["auditEvents"] = typeof(CommonAuditEvent),
                ["webHits"] = typeof(Hit),
            };

            foreach (var figure in SystemStatusAPIController.AllCountDefinitions)
            {
                if (figure.Key == "teamsTracked")
                {
                    Assert.IsNull(figure.Table);
                    Assert.IsNotNull(figure.ExactCountAsync);
                    continue;
                }

                Assert.IsNotNull(figure.Table, figure.Key + " must come from the partition statistics, not a COUNT(*) scan.");
                Assert.IsNull(figure.ExactCountAsync, figure.Key);
            }

            foreach (var pair in expected)
            {
                var figure = SystemStatusAPIController.AllCountDefinitions.Single(f => f.Key == pair.Key);
                Assert.AreEqual(pair.Value.GetCustomAttribute<TableAttribute>().Name, figure.Table, pair.Key);
            }
        }

        [TestMethod]
        public void DescribeEnabledImports_ReportsOnlyTheSwitchedOnImports()
        {
            var labels = HealthService.DescribeEnabledImports(new ImportTaskSettings { ActivityLog = true, Copilot = true });

            CollectionAssert.AreEqual(new List<string> { "Activity/audit", "Copilot" }, labels);
        }

        [TestMethod]
        public void DescribeEnabledImports_UsesTheSameLabelsAsTheHealthPage()
        {
            // The home page and Health -> Configuration must never disagree about what is switched on, so
            // they share one map. This guards the sharing, not the wording.
            var all = new ImportTaskSettings
            {
                ActivityLog = true,
                Copilot = true,
                WebTraffic = true,
                Calls = true,
            };

            var labels = HealthService.DescribeEnabledImports(all);

            foreach (var label in labels)
            {
                Assert.IsTrue(
                    HealthService.ImportLabelsBySettingProperty.Values.Contains(label),
                    $"'{label}' is not one of the shared import labels");
            }
        }
    }
}
