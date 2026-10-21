extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDataLookup;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserDiagnostics;
using Common.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// <see cref="SqlUserDiagnosticsQuery"/> against a real (LocalDB) database: the raw SQL behind the per-user licence
    /// diagnostics maps onto its result types, and the report verdict's history rule includes and excludes the same
    /// rows the Copilot Adoption report does.
    /// </summary>
    [TestClass]
    public class UserDiagnosticsSqlTests
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
        public async Task Queries_ReadStoredLicencesHistoryAndSeatEvidenceForOneUser()
        {
            var token = "udiag" + Guid.NewGuid().ToString("N").Substring(0, 10);
            var seed = await SeedAsync(token);
            try
            {
                var query = new SqlUserDiagnosticsQuery();

                var user = await query.FindUserAsync(token + "-a@contoso.local");
                Assert.AreEqual(seed.UserA, user.Id);

                var stored = await query.StoredLicenceTypesForUserAsync(seed.UserA);
                CollectionAssert.AreEquivalent(new[] { seed.LicenceA }, stored.Select(s => s.Id).ToArray());
                Assert.AreEqual(token + "_A", stored.Single().SkuPartNumber);

                var history = await query.HistoryAsync(seed.UserA, 1);
                Assert.IsTrue(history.Available);
                Assert.IsNotNull(history.TrackingBeganUtc);
                Assert.AreEqual(1, history.Rows.Count);
                Assert.IsTrue(history.Truncated, "Two rows exist; one was asked for.");
                Assert.IsNull(history.Rows[0].ValidToUtc, "Open holdings come first.");
                Assert.AreEqual(DateTimeKind.Utc, history.Rows[0].ValidFromUtc.Kind);

                var current = await query.SeatEvidenceAsync(seed.UserA, new[] { seed.LicenceA, seed.LicenceB }, false, DateTime.UtcNow, DateTime.UtcNow, null);
                CollectionAssert.AreEquivalent(new[] { seed.LicenceA }, current.Select(r => r.LicenceTypeId).ToArray());

                // 10-12 September: the A row is open from the 9th; the B row ended on the 5th, before the period.
                var period = await query.SeatEvidenceAsync(seed.UserA, new[] { seed.LicenceA, seed.LicenceB }, true,
                    new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));
                Assert.AreEqual(1, period.Count);
                Assert.AreEqual(seed.LicenceA, period[0].LicenceTypeId);
                Assert.AreEqual((byte)1, period[0].FromSource);

                var types = await query.LicenceTypesAsync();
                Assert.AreEqual(1, types.Single(t => t.Id == seed.LicenceA).AssignedUsers);

                Assert.IsNotNull(await query.LicenceHistoryStartAsync());

                var latest = await query.LatestAsync(seed.UserA, UserDataLookupRules.CatAuditEvents);
                Assert.IsNull(latest.LatestUtc, "No audit rows: nothing stored, which is not an error.");
                var report = await query.LatestAsync(seed.UserA, UserCoverageRules.CopilotUsageReportCategory);
                Assert.IsNull(report.LatestUtc);
            }
            finally
            {
                await CleanupAsync(token, seed.RunId);
            }
        }

        private sealed class Seed
        {
            public int UserA;
            public int LicenceA;
            public int LicenceB;
            public int RunId;
        }

        private static async Task<Seed> SeedAsync(string token)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                var user = new User { UserPrincipalName = token + "-a@contoso.local" };
                var licenceA = new LicenseType { Name = token + " A", SKUID = token + "_A" };
                var licenceB = new LicenseType { Name = token + " B", SKUID = token + "_B" };
                db.users.Add(user);
                db.LicenseTypes.AddRange(new[] { licenceA, licenceB });
                await db.SaveChangesAsync();

                var runId = await db.Database.SqlQuery<int>(
                    "INSERT INTO dbo.license_refresh_runs (completed_utc, previous_completed_utc) OUTPUT inserted.id VALUES ('2026-09-01T00:00:00', NULL);")
                    .SingleAsync();
                await db.Database.ExecuteSqlCommandAsync(@"
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id) VALUES (@u, @lA);
INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source, valid_from_previous_refresh_utc, valid_to_previous_refresh_utc)
VALUES
(@u, @lA, '2026-09-09T00:00:00', NULL, 1, '2026-09-08T00:00:00', NULL),
(@u, @lB, '2026-09-01T00:00:00', '2026-09-05T00:00:00', 1, NULL, '2026-09-04T00:00:00');",
                    new SqlParameter("@u", user.ID),
                    new SqlParameter("@lA", licenceA.ID),
                    new SqlParameter("@lB", licenceB.ID));
                return new Seed { UserA = user.ID, LicenceA = licenceA.ID, LicenceB = licenceB.ID, RunId = runId };
            }
        }

        private static async Task CleanupAsync(string token, int runId)
        {
            using (var db = new AnalyticsEntitiesContext())
            {
                await db.Database.ExecuteSqlCommandAsync(@"
DELETE h FROM dbo.user_license_history h WHERE h.user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE @prefix);
DELETE FROM dbo.user_license_type_lookups WHERE user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE @prefix);
DELETE FROM dbo.users WHERE user_name LIKE @prefix;
DELETE FROM dbo.license_types WHERE sku_id LIKE @skuPrefix;
DELETE FROM dbo.license_refresh_runs WHERE id = @runId;",
                    new SqlParameter("@prefix", token + "%"),
                    new SqlParameter("@skuPrefix", token + "%"),
                    new SqlParameter("@runId", runId));
            }
        }
    }
}
