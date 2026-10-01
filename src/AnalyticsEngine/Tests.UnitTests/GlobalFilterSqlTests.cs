extern alias AnalyticsWeb;

using Common.Entities.Migrations;
using Common.Entities.UserFilters;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Results;
using GlobalFilterProviders = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.GlobalFilterProviders;
using IUserDirectorySource = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.IUserDirectorySource;
using PortalAccessPolicy = AnalyticsWeb::Web.AnalyticsWeb.Security.PortalAccessPolicy;
using ReportAreaData = AnalyticsWeb::Web.AnalyticsWeb.Models.ReportAreaData;
using ReportScopeResolver = AnalyticsWeb::Web.AnalyticsWeb.Models.UserFilters.ReportScopeResolver;
using ReportsAPIController = AnalyticsWeb::Web.AnalyticsWeb.Controllers.ReportsAPIController;

namespace Tests.UnitTests
{
    /// <summary>
    /// The administrator's global filter against SQL Server: where it is kept, the by-hand upgrade that
    /// creates that table, and the Reports area's SQL run for real under the filter.
    /// </summary>
    [TestClass]
    [TestCategory("SqlIntegration")]
    public class GlobalFilterSqlTests
    {
        private const string MigrationId = "202610011330001_PortalGlobalFilter";
        private const string PredecessorId = "202609221200001_UserOrganisations";

        /// <summary>A stand-in for the predecessor's gzipped EDMX snapshot; only its equality matters here.</summary>
        private const string PredecessorModel = "0x1F8B0800AABBCCDD";

        #region Where it is kept

        [TestMethod]
        public async Task Store_BeforeTheUpgrade_ReadsAsNoFilter_AndRefusesToSave()
        {
            using (var db = ScratchDatabase.Create("gfstore"))
            {
                var store = GlobalFilterStores.Create(db.ConnectionString);

                var record = await store.GetAsync(CancellationToken.None);
                Assert.IsFalse(record.StorageAvailable);
                Assert.AreEqual(string.Empty, record.FilterJson);

                await Assert.ThrowsExceptionAsync<GlobalFilterStorageMissingException>(
                    () => store.SaveAsync("[]", 0, "admin@contoso.com", CancellationToken.None));
            }
        }

        [TestMethod]
        public async Task Store_SavesOnlyOverTheRevisionItWasOpenedAt_AndKeepsNonLatinValues()
        {
            using (var db = ScratchDatabase.Create("gfstore"))
            {
                db.Execute(PortalGlobalFilter.Up_Sql);
                var store = GlobalFilterStores.Create(db.ConnectionString);

                var empty = await store.GetAsync(CancellationToken.None);
                Assert.IsTrue(empty.StorageAvailable);
                Assert.AreEqual(0, empty.Revision);
                Assert.AreEqual(string.Empty, empty.FilterJson);

                const string greek = "[{\"d\":\"department\",\"v\":[\"Οικονομικά\"]}]";
                var first = await store.SaveAsync(greek, 0, "admin@contoso.com", CancellationToken.None);
                Assert.AreEqual(1, first.Revision);

                var read = await store.GetAsync(CancellationToken.None);
                Assert.AreEqual(greek, read.FilterJson, "The definition holds tenant text and must survive as Unicode.");
                Assert.AreEqual("admin@contoso.com", read.ModifiedBy);
                Assert.IsNotNull(read.ModifiedUtc);

                Assert.IsNull(
                    await store.SaveAsync(string.Empty, 0, "other@contoso.com", CancellationToken.None),
                    "A save from before someone else's change is refused.");
                Assert.AreEqual(greek, (await store.GetAsync(CancellationToken.None)).FilterJson);

                var second = await store.SaveAsync(string.Empty, 1, "other@contoso.com", CancellationToken.None);
                Assert.AreEqual(2, second.Revision);
                Assert.AreEqual(string.Empty, (await store.GetAsync(CancellationToken.None)).FilterJson);
            }
        }

        [TestMethod]
        public async Task Store_TwoAdministratorsSavingTogether_OnlyOneWins()
        {
            using (var db = ScratchDatabase.Create("gfstore"))
            {
                db.Execute(PortalGlobalFilter.Up_Sql);
                var store = GlobalFilterStores.Create(db.ConnectionString);

                var saves = Enumerable.Range(0, 8)
                    .Select(i => Task.Run(() => store.SaveAsync(
                        "[{\"d\":\"department\",\"v\":[\"D" + i + "\"]}]", 0, "admin" + i + "@contoso.com", CancellationToken.None)))
                    .ToArray();
                var results = await Task.WhenAll(saves);

                Assert.AreEqual(1, results.Count(r => r != null), "Exactly one save from revision 0 may succeed, first one or not.");
                Assert.AreEqual(1, (await store.GetAsync(CancellationToken.None)).Revision);
            }
        }

        #endregion

        #region The by-hand upgrade

        [TestMethod]
        public void Script_EmbedsTheMigrationsOwnUpSqlVerbatim()
        {
            StringAssert.Contains(
                Script(),
                PortalGlobalFilter.Up_Sql.Trim(),
                "The manual script must embed the migration's Up_Sql verbatim, or a by-hand upgrade applies something different.");
        }

        [TestMethod]
        public void Script_CreatesTheTableAndStampsTheMigration_WithThePredecessorsModel()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.IsNotNull(db.Scalar("SELECT OBJECT_ID(N'dbo.portal_global_filters', N'U')") as int?);
                Assert.AreEqual(1, StampCount(db));
                Assert.AreEqual(
                    "yes",
                    db.Scalar(
                        "SELECT CASE WHEN (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'" + MigrationId
                        + "') = (SELECT Model FROM dbo.__MigrationHistory WHERE MigrationId = N'" + PredecessorId
                        + "') THEN 'yes' ELSE 'no' END"),
                    "This migration changes no entity, so its model snapshot is the predecessor's.");
            }
        }

        [TestMethod]
        public void Script_ReRunIsACleanNoOp()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);

                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                db.Execute("INSERT INTO dbo.portal_global_filters (id, filter_json) VALUES (1, N'[]');");
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                Assert.AreEqual(1, StampCount(db));
                Assert.AreEqual(1, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM dbo.portal_global_filters")), "A re-run keeps the saved filter.");
            }
        }

        [TestMethod]
        public void Script_WithoutThePredecessor_CreatesTheTableButRefusesToStamp()
        {
            using (var db = NewDatabase())
            {
                var refusal = Assert.ThrowsException<SqlException>(() => db.ExecuteScript(Script(), quotedIdentifierOn: false));

                StringAssert.Contains(refusal.Message, PredecessorId);
                StringAssert.Contains(refusal.Message, "NOT stamped");
                Assert.AreEqual(0, StampCount(db));

                // Once the chain is repaired, the same script stamps.
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);
                Assert.AreEqual(1, StampCount(db));
            }
        }

        [TestMethod]
        public void Script_ATableOfAnOlderShape_IsNotStamped()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.Execute("CREATE TABLE dbo.portal_global_filters (id int NOT NULL PRIMARY KEY, filter_json nvarchar(max) NOT NULL);");

                var refusal = Assert.ThrowsException<SqlException>(() => db.ExecuteScript(Script(), quotedIdentifierOn: false));

                StringAssert.Contains(refusal.Message, "older shape");
                Assert.AreEqual(0, StampCount(db));
            }
        }

        [TestMethod]
        public void Schema_HoldsOneFilterOnly()
        {
            using (var db = NewDatabase())
            {
                StampPredecessor(db);
                db.ExecuteScript(Script(), quotedIdentifierOn: false);

                db.Execute("INSERT INTO dbo.portal_global_filters (id, filter_json) VALUES (1, N'[]');");
                Assert.ThrowsException<SqlException>(
                    () => db.Execute("INSERT INTO dbo.portal_global_filters (id, filter_json) VALUES (2, N'[]');"),
                    "A second row would leave two filters, and no way to say which applies.");
            }
        }

        private static string Script()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Common", "Entities", "Migrations", MigrationId + ".manual.sql");
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {MigrationId}.manual.sql by walking up from the test assembly.");
            return null;
        }

        private static ScratchDatabase NewDatabase()
        {
            var db = ScratchDatabase.Create("gfmanual");
            db.Execute(@"
CREATE TABLE dbo.__MigrationHistory (
    MigrationId nvarchar(150) NOT NULL CONSTRAINT PK___MigrationHistory PRIMARY KEY,
    ContextKey nvarchar(300) NOT NULL,
    Model varbinary(max) NOT NULL,
    ProductVersion nvarchar(32) NOT NULL
);");
            return db;
        }

        private static void StampPredecessor(ScratchDatabase db)
        {
            db.Execute(
                "INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion) VALUES ("
                + $"N'{PredecessorId}', N'Common.Entities.Migrations.Configuration', {PredecessorModel}, N'6.5.2');");
        }

        private static int StampCount(ScratchDatabase db)
        {
            return Convert.ToInt32(db.Scalar($"SELECT COUNT(*) FROM dbo.__MigrationHistory WHERE MigrationId = N'{MigrationId}'"));
        }

        #endregion

        #region Reports under the filter

        /// <summary>
        /// Every chart in every Reports area, run against the migrated test database under a global filter.
        /// The markers that narrow each chart are only uncommented under a restricted scope, so this is the
        /// one place that SQL is ever executed. A chart may fail here for reasons of its own - so each is
        /// compared with the same chart run with no filter, and only a chart that the filter broke fails.
        /// </summary>
        [TestMethod]
        public async Task Reports_EveryAreaRunsUnderTheGlobalFilter()
        {
            var filtered = new ReportScopeResolver(
                GlobalFilterProviders.Fixed(new GlobalFilterRecord
                {
                    StorageAvailable = true,
                    FilterJson = "[{\"d\":\"department\",\"v\":[\"Sales\"]}]",
                    Revision = 1,
                }),
                new Directory());
            var unfiltered = new ReportScopeResolver(GlobalFilterProviders.None, new Directory());

            var areas = new Dictionary<string, Func<ReportsAPIController, Task<IHttpActionResult>>>
            {
                ["copilot"] = c => c.Copilot(1),
                ["copilot-agents"] = c => c.CopilotAgents(1),
                ["usage"] = c => c.Usage(1),
                ["spo-audit"] = c => c.SpoAudit(1),
                ["web-traffic"] = c => c.WebTraffic(1),
                ["calls"] = c => c.Calls(1),
                ["emails"] = c => c.Emails(1),
                ["office-apps"] = c => c.OfficeApps(1),
            };

            var broken = new List<string>();
            foreach (var area in areas)
            {
                var baseline = (await Area(unfiltered, area.Value)).Charts.ToDictionary(c => c.Key, c => c.Error);
                var scoped = await Area(filtered, area.Value);

                foreach (var chart in scoped.Charts)
                {
                    baseline.TryGetValue(chart.Key, out var unfilteredError);
                    if (chart.Error != null && unfilteredError == null)
                    {
                        broken.Add($"{area.Key}/{chart.Key}: {chart.Error}");
                    }
                }

                Assert.IsTrue(
                    scoped.Charts.Any(c => c.Sql != null && c.Sql.Contains("DECLARE @scopeUsers")),
                    $"No chart in '{area.Key}' says it was narrowed by the filter.");
            }

            Assert.AreEqual(0, broken.Count, string.Join(Environment.NewLine, broken));
        }

        private static async Task<ReportAreaData> Area(ReportScopeResolver scopes, Func<ReportsAPIController, Task<IHttpActionResult>> call)
        {
            var configuration = new HttpConfiguration();
            configuration.Properties[typeof(PortalAccessPolicy)] = PortalAccessPolicy.Enforcing;
            var request = new HttpRequestMessage(HttpMethod.Get, "https://contoso.example/api/Reports");
            request.SetConfiguration(configuration);

            var identity = new ClaimsIdentity("Test");
            identity.AddClaim(new Claim("upn", "rep@contoso.com"));

            var controller = new ReportsAPIController(scopes)
            {
                Request = request,
                Configuration = configuration,
                User = new ClaimsPrincipal(identity),
            };

            var result = await call(controller) as OkNegotiatedContentResult<ReportAreaData>;
            Assert.IsNotNull(result, "The area did not answer 200.");
            return result.Content;
        }

        /// <summary>Five people in Sales - the fewest a reader without See PII may be shown - and one elsewhere.</summary>
        private sealed class Directory : IUserDirectorySource
        {
            private readonly UserDirectorySnapshot _snapshot;

            public Directory()
            {
                var builder = new UserDirectorySnapshotBuilder();
                builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "director@contoso.com", Department = "Sales" });
                builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "rep@contoso.com", Department = "Sales" });
                builder.AddUser(new UserDirectoryEntry { UserId = 3, UserPrincipalName = "peer@contoso.com", Department = "Sales" });
                builder.AddUser(new UserDirectoryEntry { UserId = 5, UserPrincipalName = "analyst@contoso.com", Department = "Sales" });
                builder.AddUser(new UserDirectoryEntry { UserId = 6, UserPrincipalName = "planner@contoso.com", Department = "Sales" });
                builder.AddUser(new UserDirectoryEntry { UserId = 4, UserPrincipalName = "engineer@contoso.com", Department = "Engineering" });
                _snapshot = builder.Build(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
            }

            public Task<UserDirectorySnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

            public void Prefetch()
            {
            }

            public void Invalidate()
            {
            }
        }

        #endregion
    }
}
