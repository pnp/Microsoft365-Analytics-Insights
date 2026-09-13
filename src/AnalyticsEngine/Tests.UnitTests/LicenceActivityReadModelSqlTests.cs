using Common.Entities.LicenceActivity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    [DoNotParallelize]
    public class LicenceActivityReadModelSqlTests
    {
        private static readonly DateTime Now = LicenceActivitySqlFixture.NowUtc;

        [TestMethod]
        public async Task CachedReadModelMatchesSqlAcrossSkuWorkloadSearchSortAndPageShapes()
        {
            using (var fixture = LicenceActivitySqlTests.CreateMeasuredFixture())
            {
                var sources = Sources();
                var query = Query();
                var sql = fixture.Store();
                var model = await sql.LoadReadModelAsync(query, sources, null, CancellationToken.None);
                await Compare(sql, model, query, sources);
                foreach (var department in new int?[] { 0, 1, 2 })
                    await Compare(sql, model,
                        LicenceActivityQuery.Create(query.From, query.To, Now, departmentId: department), sources);
                await Compare(sql, model,
                    LicenceActivityQuery.Create(query.From, query.To, Now, countryId: 2), sources);
            }
        }

        [TestMethod]
        public async Task MissingDuplicateAndPartialSamplesRemainDistinctFromMeasuredZero()
        {
            using (var fixture = LicenceActivitySqlTests.CreateMeasuredFixture())
            {
                fixture.Execute(@"
DELETE dbo.onedrive_user_activity_log WHERE user_id = 2;
DELETE dbo.sharepoint_user_activity_log WHERE user_id = 3 AND [date] = '2000-05-07';
DELETE dbo.outlook_user_activity_log WHERE [date] = '2000-06-25';
DROP INDEX IX_license_type_id_user_id ON dbo.user_license_type_lookups;
INSERT dbo.user_license_type_lookups(user_id, license_type_id) VALUES(1, 1);");
                LicenceActivitySqlTests.SeedOneUsageTable(fixture, "onedrive_user_activity_log",
                    new[] { "2000-05-07", "2000-05-07" });
                var sql = fixture.Store();
                foreach (var from in new[] { "2000-05-01", "2000-05-03" })
                {
                    var query = LicenceActivityQuery.Create(from, "2000-06-25", Now);
                    var model = await sql.LoadReadModelAsync(query, Sources(), null, CancellationToken.None);
                    await Compare(sql, model, query, Sources());
                }
            }
        }

        [DataTestMethod]
        [DataRow(7, false, false)]
        [DataRow(7, true, false)]
        [DataRow(28, false, false)]
        [DataRow(90, false, false)]
        [DataRow(180, false, false)]
        [DataRow(7, false, true)]
        public async Task OfficialCopilotSourcesPreservePeriodAuthorityAndUnknownEvidence(
            int period, bool customGap, bool invalidCounters)
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCachedCopilot"))
            {
                LicenceActivitySqlTests.SeedDirectory(fixture);
                fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
 (report_name, report_refresh_date, report_version, report_period, imported_utc,
  rows_read, rows_saved, is_upn_obfuscated, error)
VALUES (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-25', N'v2', N'D7',
 '2000-07-01T01:00:00', 5, 5, 0, NULL);");
                var end = new DateTime(2000, 6, 25);
                var start = end.AddDays(1 - (period == 7 ? 56 : period));
                var dates = period == 7
                    ? Enumerable.Range(0, 8).Select(index => start.AddDays(6 + index * 7))
                    : new[] { end };
                foreach (var date in dates)
                {
                    fixture.Execute($@"
INSERT dbo.copilot_usage_user_activity_log
 (report_period_days, prompts_all_apps, active_usage_days, is_upn_obfuscated,
  user_id, [date], last_activity_date)
VALUES
 ({period}, 12, 2, 0, 1, '{date:yyyy-MM-dd}', NULL),
 ({period}, 0, 0, 0, 4, '{date:yyyy-MM-dd}', '{date:yyyy-MM-dd}'),
 ({period}, NULL, NULL, 0, 3, '{date:yyyy-MM-dd}', '{date:yyyy-MM-dd}');");
                }
                if (invalidCounters)
                    fixture.Execute("UPDATE dbo.copilot_usage_user_activity_log SET active_usage_days=-1, prompts_all_apps=-2 WHERE user_id=1 AND [date]='2000-05-07';");
                var query = LicenceActivityQuery.Create(start.AddDays(customGap ? 1 : 0).ToString("yyyy-MM-dd"),
                    end.ToString("yyyy-MM-dd"), Now);
                var sources = Sources(usageReports: false, copilotReports: true);
                var sql = fixture.Store();
                var model = await sql.LoadReadModelAsync(query, sources, null, CancellationToken.None);
                await Compare(sql, model, query, sources);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task DisabledAndUnpopulatedSourcesNeverBecomeMeasuredZero(bool enabled)
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCachedUnavailable"))
            {
                LicenceActivitySqlTests.SeedDirectory(fixture);
                var sources = Sources(enabled, enabled);
                var sql = fixture.Store();
                var model = await sql.LoadReadModelAsync(Query(), sources, null, CancellationToken.None);
                await Compare(sql, model, Query(), sources);
                Assert.IsTrue(model.BuildOverview(Query(), CancellationToken.None).Licences
                    .SelectMany(sku => sku.Workloads).All(workload => workload.Zero == 0));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task CopilotEventFallbacksRemainPositiveOnly(bool interactions)
        {
            using (var fixture = LicenceActivitySqlFixture.Create("LicenceCachedFallback"))
            {
                LicenceActivitySqlTests.SeedDirectory(fixture);
                if (interactions)
                {
                    fixture.Execute(@"
INSERT dbo.copilot_interactions
 (graph_interaction_id, session_id, user_id, created_utc,
  body_char_count, body_word_count, attachment_count, link_count, mention_count, context_count)
VALUES (N'synthetic-interaction', 1, 2, '2000-06-20', 20, 4, 0, 0, 0, 0);");
                }
                else
                {
                    fixture.Execute(@"
INSERT dbo.copilot_usage_report_import_log
 (report_name, report_refresh_date, report_version, report_period, imported_utc,
  rows_read, rows_saved, is_upn_obfuscated, error)
VALUES (N'getMicrosoft365CopilotUsageUserDetail', '2000-06-30', N'v2', N'D28',
 '2000-07-01T01:00:00', 5, 0, 1, NULL);
INSERT dbo.copilot_chats(event_id, app_host, user_id, time_stamp)
VALUES ('00000000-0000-0000-0000-000000000001', N'Teams', 1, '2000-06-20');");
                }
                var sources = Sources(usageReports: false, copilotReports: !interactions);
                sources.CopilotAudit = !interactions;
                sources.CopilotInteractions = interactions;
                var sql = fixture.Store();
                var model = await sql.LoadReadModelAsync(Query(), sources, null, CancellationToken.None);
                await Compare(sql, model, Query(), sources);
                var overview = model.BuildOverview(Query(), CancellationToken.None);
                var users = model.BuildUsers(overview,
                    Query().ForUsers(1, "copilot", null, "activity", "desc", 10, 1, 100, Now),
                    CancellationToken.None);
                Assert.AreEqual(1, users.MostActive.Count);
                Assert.AreEqual(0, users.LeastActive.Count);
                Assert.IsTrue(users.Users.SelectMany(user => user.Workloads).All(workload => workload.Band == "unknown"));
            }
        }

        [TestMethod]
        public async Task FilterAndDrilldownReusePinnedDataWithoutMoreSqlUntilExpiry()
        {
            using (var fixture = LicenceActivitySqlTests.CreateMeasuredFixture())
            {
                var now = Now;
                var opened = 0;
                var loader = fixture.Store(new SqlLicenceActivityStoreInstrumentation
                {
                    ConnectionOpened = _ => Interlocked.Increment(ref opened)
                });
                var cache = new LicenceActivityReadModelCache(utcNow: () => now);
                var store = new CachedLicenceActivityStore(loader, cache, "synthetic-scope");
                var overview = await store.LoadOverviewAsync(Query(), Sources(), null, CancellationToken.None);
                var afterLoad = opened;
                Assert.IsTrue(afterLoad > 0);
                var userQuery = Query().ForUsers(1, "teams", null, "upn", "asc", 10, 1, 100, Now);
                var users = await store.LoadUsersAsync(overview, userQuery, Sources(), null, CancellationToken.None);
                fixture.Execute(@"
DELETE dbo.user_license_type_lookups WHERE user_id=1;
UPDATE dbo.users SET department_id=NULL WHERE id=1;
UPDATE dbo.teams_user_activity_log SET last_activity_date=NULL WHERE user_id=1;");
                var again = await store.LoadUsersAsync(overview, userQuery, Sources(), null, CancellationToken.None);
                AssertUsersEqual(users, again);
                var filtered = await store.LoadOverviewAsync(
                    LicenceActivityQuery.Create(Query().From, Query().To, Now, departmentId: 1),
                    Sources(), null, CancellationToken.None);
                Assert.AreEqual(overview.ReadModelId, filtered.ReadModelId);
                Assert.AreEqual(afterLoad, opened, "Licence, filter, search and ranking changes must not re-query SQL.");
                now = overview.SourceExpiresUtc.Value;
                await Assert.ThrowsExceptionAsync<LicenceActivityReadModelExpiredException>(() =>
                    store.LoadUsersAsync(overview, userQuery, Sources(), null, CancellationToken.None));
                var refreshed = await store.LoadOverviewAsync(Query(), Sources(), null, CancellationToken.None);
                Assert.AreNotEqual(overview.ReadModelId, refreshed.ReadModelId);
                Assert.AreEqual(4, refreshed.DistinctAssignedUsers);
                Assert.IsTrue(opened > afterLoad);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ParallelReadModelBranchesReleaseConnectionsOnSuccessAndFailure(bool failCoverage)
        {
            using (var fixture = LicenceActivitySqlTests.CreateMeasuredFixture())
            {
                if (failCoverage) fixture.Execute("DROP TABLE dbo.outlook_user_activity_log;");
                var measurements = new LicenceActivitySqlMeasurement();
                var pending = fixture.Store(measurements.Instrumentation(includeShowplan: false))
                    .LoadReadModelAsync(Query(), Sources(), null, CancellationToken.None);
                if (failCoverage)
                    await Assert.ThrowsExceptionAsync<Microsoft.Data.SqlClient.SqlException>(() => pending);
                else
                    Assert.AreEqual(5, (await pending).BuildOverview(Query(), CancellationToken.None).DistinctAssignedUsers);
                Assert.AreEqual(0, measurements.ActiveCommands);
                Assert.AreEqual(0, measurements.ActiveConnections);
                Assert.IsTrue(measurements.Operations.Any(operation => operation.StartsWith("directory=", StringComparison.Ordinal)));
            }
        }

        private static async Task Compare(
            SqlLicenceActivityStore sql, LicenceActivityReadModel model,
            LicenceActivityQuery query, LicenceActivitySources sources)
        {
            var expected = await sql.LoadOverviewAsync(query, sources, null, CancellationToken.None);
            var actual = model.BuildOverview(query, CancellationToken.None);
            Assert.AreEqual(OverviewShape(expected), OverviewShape(actual));
            foreach (var sku in expected.Licences)
                foreach (var workload in LicenceActivityQuery.Workloads)
                    foreach (var shape in new[]
                    {
                        new { Sort = "upn", Direction = "asc", Search = "", Page = 1 },
                        new { Sort = "upn", Direction = "desc", Search = "", Page = 2 },
                        new { Sort = "activity", Direction = "desc", Search = "", Page = 1 },
                        new { Sort = "activity", Direction = "asc", Search = "", Page = 1 },
                        new { Sort = "lastActivity", Direction = "desc", Search = "", Page = 2 },
                        new { Sort = "lastActivity", Direction = "asc", Search = "", Page = 1 },
                        new { Sort = "upn", Direction = "asc", Search = "ALIAS.SEARCH", Page = 1 },
                        new { Sort = "upn", Direction = "asc", Search = "_user", Page = 1 },
                        new { Sort = "upn", Direction = "asc", Search = "%[missing]", Page = 1 }
                    })
                    {
                        var userQuery = query.ForUsers(sku.LicenceTypeId, workload, shape.Search,
                            shape.Sort, shape.Direction, 2, shape.Page, 2, Now);
                        var expectedUsers = await sql.LoadUsersAsync(expected, userQuery, sources, null, CancellationToken.None);
                        var actualUsers = model.BuildUsers(actual, userQuery, CancellationToken.None);
                        AssertUsersEqual(expectedUsers, actualUsers);
                    }
        }

        private static string OverviewShape(LicenceActivityOverview overview) => JsonConvert.SerializeObject(new
        {
            overview.DistinctAssignedUsers,
            overview.DemographicsTruncated,
            Licences = overview.Licences.OrderBy(sku => sku.LicenceTypeId).Select(sku => new
            {
                sku.LicenceTypeId, sku.Name, sku.SkuId, sku.AssignedUsers,
                Workloads = sku.Workloads.OrderBy(workload => workload.Workload)
            }),
            Coverage = overview.Coverage.OrderBy(coverage => coverage.Workload),
            Departments = overview.Departments.OrderBy(value => value.Id),
            Countries = overview.Countries.OrderBy(value => value.Id)
        });

        private static void AssertUsersEqual(LicenceActivityUsers expected, LicenceActivityUsers actual)
        {
            Assert.AreEqual(expected.TotalUsers, actual.TotalUsers);
            Assert.AreEqual(expected.RankedUsers, actual.RankedUsers);
            Assert.AreEqual(JsonConvert.SerializeObject(expected.MostActive), JsonConvert.SerializeObject(actual.MostActive));
            Assert.AreEqual(JsonConvert.SerializeObject(expected.LeastActive), JsonConvert.SerializeObject(actual.LeastActive));
            Assert.AreEqual(JsonConvert.SerializeObject(expected.Users), JsonConvert.SerializeObject(actual.Users));
        }

        private static LicenceActivityQuery Query() =>
            LicenceActivityQuery.Create("2000-05-01", "2000-06-25", Now);

        private static LicenceActivitySources Sources(bool usageReports = true, bool copilotReports = false) =>
            new LicenceActivitySources
            {
                UserMetadata = true, UsageReports = usageReports, CopilotUsageReports = copilotReports, NowUtc = Now
            };
    }
}
