using Common.Entities;
using DataUtils;
using Microsoft.Graph.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.Graph;
using StubGraphHandler = Tests.UnitTests.TeamsCrawlTests.StubGraphHandler;

namespace Tests.UnitTests
{
    [TestClass]
    public class LicenceHistoryReconcileTests
    {
        [TestInitialize]
        public void EnsureSchema()
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                db.Database.Initialize(false);
            }
        }

        [TestMethod]
        public async Task AddAssignments_OrphanedOpenHistoryRow_DoesNotBreakLookupInsert()
        {
            var token = "lhorphan" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var user = new Common.Entities.User { UserPrincipalName = token + "@contoso.local" };
                    var licence = new LicenseType { Name = token + " licence", SKUID = token + "_SKU" };
                    db.users.Add(user);
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(
                        "INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES (@p0, @p1, '2026-09-01', NULL, 1)",
                        user.ID, licence.ID);

                    var store = new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer());
                    var refresh = await store.StartRefresh(DateTime.UtcNow);
                    var written = await store.AddAssignments(new[] { new UserLicenseAssignment(user.ID, licence.ID) }, refresh);

                    Assert.AreEqual(1, written, "The lookup insert should succeed even when history already has an open row.");
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = @p0 AND license_type_id = @p1",
                        user.ID, licence.ID).SingleAsync());
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND license_type_id = @p1 AND valid_to_utc IS NULL",
                        user.ID, licence.ID).SingleAsync(), "The existing open history row should be reused, not duplicated.");
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task ReconcileHistoryWithCurrentLookups_ClosesAndOpensDriftedRows()
        {
            var token = "lhdrift" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                using (var transaction = db.Database.BeginTransaction())
                {
                    // Reconcile counts the entire store. Isolate its inputs, then roll back to preserve other fixtures' rows.
                    await db.Database.ExecuteSqlCommandAsync(
                        "DELETE FROM dbo.user_license_history; DELETE FROM dbo.user_license_type_lookups;");
                    var userA = new Common.Entities.User { UserPrincipalName = token + "-a@contoso.local" };
                    var userB = new Common.Entities.User { UserPrincipalName = token + "-b@contoso.local" };
                    var licence = new LicenseType { Name = token + " licence", SKUID = token + "_SKU" };
                    db.users.AddRange(new[] { userA, userB });
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();

                    await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p2);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES (@p1, @p2, '2026-09-01', NULL, 1);",
                        userA.ID, userB.ID, licence.ID);

                    var store = new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer());
                    var refresh = await store.StartRefresh(new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));
                    var result = await store.ReconcileHistoryWithCurrentLookups(refresh);

                    Assert.AreEqual(1, result.OpenedLookupRowsWithoutHistory, "User A had a lookup but no open history row, so reconcile should seed it.");
                    Assert.AreEqual(1, result.ClosedOpenRowsWithoutLookup, "User B had open history but no lookup, so reconcile should close it.");
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND license_type_id = @p1 AND valid_to_utc IS NULL AND from_source = 0",
                        userA.ID, licence.ID).SingleAsync());
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND license_type_id = @p1 AND valid_to_utc = '2026-10-02T08:00:00'",
                        userB.ID, licence.ID).SingleAsync());
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task PerUserFallback_ReconcilesManyUsersInOneCompletedRun()
        {
            var token = "lhfallback" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var userAadA = Guid.NewGuid().ToString();
            var userAadB = Guid.NewGuid().ToString();
            var skuPart = token + "_SKU";
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.license_seat_count_history; DELETE FROM dbo.license_refresh_runs;");
                    var userA = new Common.Entities.User { UserPrincipalName = token + "-a@contoso.local", AzureAdId = userAadA };
                    var userB = new Common.Entities.User { UserPrincipalName = token + "-b@contoso.local", AzureAdId = userAadB };
                    db.users.AddRange(new[] { userA, userB });
                    await db.SaveChangesAsync();

                    var details = new Dictionary<string, List<LicenseDetails>>
                    {
                        [userAadA] = new List<LicenseDetails> { new LicenseDetails { SkuPartNumber = skuPart } },
                        [userAadB] = new List<LicenseDetails> { new LicenseDetails { SkuPartNumber = skuPart } },
                    };
                    var loader = new FakeUserMetadataLoader(fakeLicenseDetails: details);
                    var processor = new UserLicenseProcessor(
                        AnalyticsLogger.ConsoleOnlyTracer(),
                        loader,
                        new UserMetadataCache(db),
                        new FixedLicenseNameResolver(token + " licence"),
                        null);

                    var users = new[]
                    {
                        (Graph: new GraphUser { Id = userAadA, UserPrincipalName = userA.UserPrincipalName }, Db: userA),
                        (Graph: new GraphUser { Id = userAadB, UserPrincipalName = userB.UserPrincipalName }, Db: userB),
                    };
                    var desired = new HashSet<UserLicenseAssignment>();
                    var userIds = new HashSet<int>();
                    foreach (var user in users)
                    {
                        userIds.Add(user.Db.ID);
                        foreach (var assignment in await processor.BuildDesiredAssignmentsForUser(db, user.Graph, user.Db))
                        {
                            desired.Add(assignment);
                        }
                    }

                    await processor.ReconcileCollectedUserLicenses(db, userIds, desired);

                    Assert.AreEqual(1, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.license_refresh_runs").SingleAsync(),
                        "A per-user fallback cycle should record one completed refresh, not one per user.");
                    Assert.AreEqual(2, await db.Database.SqlQuery<int>(@"
SELECT COUNT(*)
FROM dbo.user_license_history AS history
JOIN dbo.users AS users ON users.id = history.user_id
WHERE users.user_name LIKE @p0 AND history.valid_to_utc IS NULL", token + "%").SingleAsync(),
                        "Every processed user's current assignment should have an open history row.");
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("{")]
        public async Task ScheduledAppOnly_DeniedInventory_SavesMetadataWithoutLicenceReadsOrHistory_ThenRecoversOutsideDelta(string refusalBody)
        {
            var prefix = "appsku" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var existingUpn = prefix + "-existing@contoso.example";
            var newUpn = prefix + "-new@contoso.example";
            var existingId = Guid.NewGuid().ToString();
            var newId = Guid.NewGuid().ToString();
            var part = prefix + "_SKU";
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var existing = db.users.Add(new Common.Entities.User { UserPrincipalName = existingUpn, AzureAdId = existingId, AccountEnabled = true });
                    var licence = db.LicenseTypes.Add(new LicenseType { Name = part, SKUID = part });
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p1);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source)
VALUES (@p0, @p1, '2020-01-01', NULL, 1);", existing.ID, licence.ID);
                    var lookup = db.UserLicenseTypeLookups.Where(l => l.LicenseTypeId == licence.ID);
                    var originalLookup = await lookup.Select(l => l.ID).SingleAsync();
                    Task<int> RefreshCount() => db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.license_refresh_runs").SingleAsync();
                    var before = await RefreshCount();

                    var denied = true;
                    var delta = new StubGraphHandler().Route("/users/delta", request =>
                    {
                        StringAssert.Contains(request.RequestUri.Query, denied ? "previous-checkpoint" : "after-denial");
                        return StubGraphHandler.Response(HttpStatusCode.OK, new JObject
                        {
                            ["@odata.deltaLink"] = "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=" + (denied ? "after-denial" : "after-recovery"),
                            ["value"] = denied ? JArray.FromObject(new[]
                            {
                                new { id = existingId, userPrincipalName = existingUpn, accountEnabled = true, postalCode = "10000" },
                                new { id = newId, userPrincipalName = newUpn, accountEnabled = true, postalCode = "20000" }
                            }) : new JArray()
                        }.ToString());
                    });
                    var sdk = new StubGraphHandler()
                        .Route("/subscribedSkus", _ => !denied
                            ? StubGraphHandler.Response(HttpStatusCode.OK, "{\"value\":[{\"skuId\":\"" + Guid.Empty + "\",\"skuPartNumber\":\"" + part + "\"}]}")
                            : refusalBody == null ? new HttpResponseMessage(HttpStatusCode.Forbidden) : StubGraphHandler.Response(HttpStatusCode.Forbidden, refusalBody))
                        .Json("/users", "{\"value\":[{\"userPrincipalName\":\"" + newUpn + "\"}]}");
                    var logger = AnalyticsLogger.ConsoleOnlyTracer();
                    var settings = new Common.Entities.Config.AppConfig { ConnectionStrings = new Common.Entities.Config.AppConnectionStrings(_ => "") };
                    var credential = new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential();
                    using (var manual = new ManualGraphCallClient(delta, logger))
                    using (var graph = GraphServiceClientFactory.CreateForGraphImport(credential, GraphRequestBudgetOptions.GraphImportDefault, null, sdk))
                    {
                        var updater = new UserMetadataUpdater(logger, settings, credential, manual, null,
                            TestUserScopes.Provider(TestUserScopes.Of(existingUpn, newUpn)), null, default, graph);
                        await updater.UserLoader.DeltaValueProvider.SetDeltaToken("previous-checkpoint");
                        var completed = await updater.InsertAndUpdateDatabaseFromExternalUsers();
                        CollectionAssert.AreEqual(new[] { "/v1.0/subscribedSkus" }, sdk.Requests.Select(u => u.AbsolutePath).ToArray(),
                            "An app-only importer must not call delegated-only licenseDetails, for new or existing users.");
                        Assert.IsFalse(completed, "An unavailable licence refresh must not stamp the scheduled section as complete.");
                        Assert.AreEqual("after-denial", await updater.UserLoader.DeltaValueProvider.GetDeltaToken());
                        Assert.AreEqual("10000", await db.users.Where(u => u.ID == existing.ID).Select(u => u.PostalCode).SingleAsync());
                        Assert.AreEqual("20000", await db.users.Where(u => u.UserPrincipalName == newUpn).Select(u => u.PostalCode).SingleAsync());
                        Assert.AreEqual(originalLookup, await lookup.Select(l => l.ID).SingleAsync());
                        Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                            "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id=@p0 AND license_type_id=@p1 AND valid_from_utc='2020-01-01' AND valid_to_utc IS NULL AND from_source=1",
                            existing.ID, licence.ID).SingleAsync());
                        Assert.AreEqual(before, await RefreshCount(), "No completed licence reading was observed.");

                        denied = false;
                        Assert.IsTrue(await updater.InsertAndUpdateDatabaseFromExternalUsers());
                        CollectionAssert.AreEqual(new[] { "/v1.0/subscribedSkus", "/v1.0/subscribedSkus", "/v1.0/users" },
                            sdk.Requests.Select(u => u.AbsolutePath).ToArray());
                        Assert.AreEqual("after-recovery", await updater.UserLoader.DeltaValueProvider.GetDeltaToken());
                        var addedUserId = await db.users.Where(u => u.UserPrincipalName == newUpn).Select(u => u.ID).SingleAsync();
                        Assert.AreEqual(addedUserId, await lookup.Select(l => l.UserId).SingleAsync(),
                            "The tenant SKU answer covers existing and new users even when their metadata delta is empty.");
                        Assert.AreEqual(before + 1, await RefreshCount());
                    }
                }
            }
            finally
            {
                await CleanupAsync(prefix);
            }
        }

        [DataTestMethod]
        [DataRow(403, "application/json")]
        [DataRow(403, null)]
        [DataRow(403, "text/html")]
        [DataRow(404, "application/json")]
        [DataRow(404, null)]
        [DataRow(404, "text/html")]
        [DataRow(204, null)]
        public async Task PerUserFallback_UnreadableLicenceDetails_KeepsAssignmentAndProcessesNextUser(int status, string mediaType)
        {
            var token = "lhnull" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var userAadA = Guid.NewGuid().ToString();
            var userAadB = Guid.NewGuid().ToString();
            var skuPart = token + "_SKU";
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    var userA = new Common.Entities.User { UserPrincipalName = token + "-a@contoso.local", AzureAdId = userAadA, AccountEnabled = true };
                    var userB = new Common.Entities.User { UserPrincipalName = token + "-b@contoso.local", AzureAdId = userAadB, AccountEnabled = true };
                    var licence = new LicenseType { Name = token + " licence", SKUID = skuPart };
                    db.users.AddRange(new[] { userA, userB });
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p2), (@p1, @p2);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES
(@p0, @p2, '2026-09-01', NULL, 1),
(@p1, @p2, '2026-09-01', NULL, 1);",
                        userA.ID, userB.ID, licence.ID);

                    var lookup = db.UserLicenseTypeLookups.Where(l => l.UserId == userA.ID && l.LicenseTypeId == licence.ID).Select(l => l.ID);
                    var originalLookupId = await lookup.SingleAsync();
                    var deltaPage = new JObject
                    {
                        ["@odata.deltaLink"] = "https://graph.contoso.invalid/v1.0/users/delta?$deltatoken=new-checkpoint",
                        ["value"] = JArray.FromObject(new[] { userA, userB }.Select(u => new
                        {
                            id = u.AzureAdId, userPrincipalName = u.UserPrincipalName, accountEnabled = true
                        }))
                    };
                    var delta = new StubGraphHandler().Json("/users/delta", deltaPage.ToString());
                    var sdk = new StubGraphHandler()
                        .Route("/subscribedSkus", _ => new HttpResponseMessage(HttpStatusCode.Forbidden))
                        .Route("/users/" + userAadA + "/licenseDetails", _ => GraphCallStatusRecoveryTests.ErrorResponse(status, mediaType))
                        .Json("/users/" + userAadB + "/licenseDetails", "{\"value\":[]}");
                    var logger = AnalyticsLogger.ConsoleOnlyTracer();
                    var checkpoint = new FakeDeltaValueProvider();
                    await checkpoint.SetDeltaToken("previous-checkpoint");
                    using (var client = new ManualGraphCallClient(delta, logger))
                    using (var graph = GraphServiceClientFactory.CreateForGraphImport(new GraphBoundedRequestAndDeltaTests.SyntheticTokenCredential(),
                        GraphRequestBudgetOptions.GraphImportDefault, null, sdk))
                    {
                        var loader = new GraphUserLoader(client, checkpoint, logger, graph);
                        Assert.IsTrue(await new UserMetadataUpdater(logger, null, loader).InsertAndUpdateDatabaseFromExternalUsers());
                    }
                    CollectionAssert.AreEqual(new[] { "/v1.0/subscribedSkus", "/v1.0/users/" + userAadA + "/licenseDetails",
                        "/v1.0/users/" + userAadB + "/licenseDetails" }, sdk.Requests.Select(uri => uri.AbsolutePath).ToArray());
                    Assert.AreEqual("new-checkpoint", await checkpoint.GetDeltaToken());

                    Assert.AreEqual(originalLookupId, await lookup.SingleAsync(), "The skipped user's current lookup must remain untouched.");
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND license_type_id = @p1 AND valid_from_utc = '2026-09-01' AND valid_to_utc IS NULL", userA.ID, licence.ID).SingleAsync(),
                        "The skipped user's open history must be retained.");
                    Assert.AreEqual(0, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = @p0", userB.ID).SingleAsync(),
                        "A non-null empty details list still means the user genuinely has no licences.");
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND valid_to_utc IS NOT NULL", userB.ID).SingleAsync(),
                        "The reconciled user's removed licence should be closed.");
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task ProcessUserLicenses_NullLicenceDetails_ChangesNothing()
        {
            var token = "lhdirectnull" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.license_seat_count_history; DELETE FROM dbo.license_refresh_runs;");
                    var user = new Common.Entities.User { UserPrincipalName = token + "@contoso.local", AzureAdId = token };
                    var licence = new LicenseType { Name = token + " licence", SKUID = token + "_SKU" };
                    db.users.Add(user);
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p1);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES (@p0, @p1, '2026-09-01', NULL, 1);",
                        user.ID, licence.ID);

                    var processor = new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), new FakeUserMetadataLoader(), new UserMetadataCache(db), new FixedLicenseNameResolver(token + " licence"), null);
                    await processor.ProcessUserLicenses(db, new GraphUser { Id = token, UserPrincipalName = user.UserPrincipalName }, user);

                    Assert.AreEqual(1, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = @p0", user.ID).SingleAsync());
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND valid_to_utc IS NULL", user.ID).SingleAsync());
                    Assert.AreEqual(0, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.license_refresh_runs").SingleAsync(),
                        "A skipped user is not a completed licence refresh.");
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task ProcessUserLicenses_EmptyLicenceDetails_RemovesLicencesAndClosesHistory()
        {
            var token = "lhdirectempty" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.license_seat_count_history; DELETE FROM dbo.license_refresh_runs;");
                    var user = new Common.Entities.User { UserPrincipalName = token + "@contoso.local", AzureAdId = token };
                    var licence = new LicenseType { Name = token + " licence", SKUID = token + "_SKU" };
                    db.users.Add(user);
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p1);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source) VALUES (@p0, @p1, '2026-09-01', NULL, 1);",
                        user.ID, licence.ID);

                    var loader = new FakeUserMetadataLoader(fakeLicenseDetails: new Dictionary<string, List<LicenseDetails>>
                    {
                        [token] = new List<LicenseDetails>(),
                    });
                    var processor = new UserLicenseProcessor(AnalyticsLogger.ConsoleOnlyTracer(), loader, new UserMetadataCache(db), new FixedLicenseNameResolver(token + " licence"), null);
                    await processor.ProcessUserLicenses(db, new GraphUser { Id = token, UserPrincipalName = user.UserPrincipalName }, user);

                    Assert.AreEqual(0, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.user_license_type_lookups WHERE user_id = @p0", user.ID).SingleAsync());
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.user_license_history WHERE user_id = @p0 AND valid_to_utc IS NOT NULL", user.ID).SingleAsync());
                    Assert.AreEqual(1, await db.Database.SqlQuery<int>("SELECT COUNT(*) FROM dbo.license_refresh_runs").SingleAsync());
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        [TestMethod]
        public async Task FirstHistorySeed_UsesTheFirstRefreshCompletedUtcExactly()
        {
            var token = "lhinvariant" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (var db = new AnalyticsEntitiesContext())
                {
                    await db.Database.ExecuteSqlCommandAsync("DELETE FROM dbo.license_seat_count_history; DELETE FROM dbo.license_refresh_runs;");
                    var user = new Common.Entities.User { UserPrincipalName = token + "@contoso.local" };
                    var licence = new LicenseType { Name = token + " licence", SKUID = token + "_SKU" };
                    db.users.Add(user);
                    db.LicenseTypes.Add(licence);
                    await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlCommandAsync(
                        "INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@p0, @p1);",
                        user.ID, licence.ID);

                    var completedUtc = new DateTime(2026, 10, 2, 9, 30, 0, DateTimeKind.Utc);
                    var store = new SqlUserLicenseStore(db, AnalyticsLogger.ConsoleOnlyTracer());
                    var refresh = await store.StartRefresh(completedUtc);
                    await store.ReconcileHistoryWithCurrentLookups(refresh);
                    var runId = await store.CompleteRefresh(refresh, new List<LicenseSeatCountSnapshot>());

                    var diffSeconds = await db.Database.SqlQuery<int>(@"
SELECT DATEDIFF(second, h.valid_from_utc, r.completed_utc)
FROM dbo.user_license_history h
CROSS JOIN dbo.license_refresh_runs r
WHERE h.user_id = @p0 AND r.id = @p1",
                        user.ID, runId.Value).SingleAsync();
                    Assert.AreEqual(0, diffSeconds,
                        "Seed rows must start exactly at the first completed refresh time so consumers can identify 'held before licence history began'.");
                }
            }
            finally
            {
                await CleanupAsync(token);
            }
        }

        private static async Task CleanupAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(@"
DELETE h FROM dbo.user_license_history h JOIN dbo.users u ON u.id = h.user_id WHERE u.user_name LIKE @p0;
DELETE h FROM dbo.license_seat_count_history h JOIN dbo.license_types l ON l.id = h.license_type_id WHERE l.sku_id LIKE @p1;
DELETE FROM dbo.user_license_type_lookups WHERE user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE @p0);
DELETE FROM dbo.users WHERE user_name LIKE @p0;
DELETE FROM dbo.license_types WHERE sku_id LIKE @p1;",
                    token + "%", token + "%");
            }
        }

        private sealed class FixedLicenseNameResolver : IOfficeLicenseNameResolver
        {
            private readonly string _displayName;
            public FixedLicenseNameResolver(string displayName) => _displayName = displayName;
            public string GetDisplayNameFor(string id) => _displayName;
        }
    }
}
