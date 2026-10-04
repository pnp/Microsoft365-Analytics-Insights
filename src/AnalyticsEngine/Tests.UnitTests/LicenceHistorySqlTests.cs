using Common.Entities;

using Common.Entities.Licences;

using Microsoft.Data.SqlClient;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using System;

using System.Collections.Generic;

using System.Data;

using System.Data.Entity;

using System.Linq;

using System.Threading.Tasks;



namespace Tests.UnitTests

{

    [TestClass]

    public class LicenceHistorySqlTests

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

        public async Task HoldersOnDay_ReturnsUsersWhoseHoldingOverlapsThatUtcDay()

        {

            var token = "lhday" + Guid.NewGuid().ToString("N").Substring(0, 10);

            var ids = await SeedAsync(token);

            try

            {

                var holders = await QueryUsersAsync(

                    LicenceHistorySql.BuildHoldersOnDay(1),

                    ("@dayUtc", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)),

                    ("@license0", ids.LicenceA));



                CollectionAssert.AreEquivalent(new[] { ids.UserA, ids.UserB }, holders.ToArray(),

                    "Both users held licence A at some point during 2026-09-10 UTC; the earlier-ended row and the other licence do not qualify.");

            }

            finally

            {

                await CleanupAsync(token);

            }

        }



        [TestMethod]

        public async Task HoldersInRange_ReturnsOverlappedDaysPerUserAndLicence()

        {

            var token = "lhrange" + Guid.NewGuid().ToString("N").Substring(0, 10);

            var ids = await SeedAsync(token);

            try

            {

                var rows = await QueryRangeAsync(

                    LicenceHistorySql.BuildHoldersInRange(2),

                    ("@fromUtc", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc)),

                    ("@toUtc", new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc)),

                    ("@license0", ids.LicenceA),

                    ("@license1", ids.LicenceB));



                var byKey = rows.ToDictionary(r => (r.UserId, r.LicenseTypeId), r => r.DaysHeld);

                Assert.AreEqual(1.5m, Math.Round(byKey[(ids.UserA, ids.LicenceA)], 2), "User A held licence A for the first day and a half of the range.");

                Assert.AreEqual(2.75m, Math.Round(byKey[(ids.UserB, ids.LicenceA)], 2), "Open holdings are clipped at the range end after the user first received the licence.");

                Assert.AreEqual(2.0m, Math.Round(byKey[(ids.UserA, ids.LicenceB)], 2), "Licence B is included independently when requested.");

            }

            finally

            {

                await CleanupAsync(token);

            }

        }



        private sealed class SeedIds

        {

            public int UserA, UserB, UserC, LicenceA, LicenceB;

        }



        private static async Task<SeedIds> SeedAsync(string token)

        {

            using (var db = new AnalyticsEntitiesContext())

            {

                var userA = new User { UserPrincipalName = token + "-a@contoso.local" };

                var userB = new User { UserPrincipalName = token + "-b@contoso.local" };

                var userC = new User { UserPrincipalName = token + "-c@contoso.local" };

                var licenceA = new LicenseType { Name = token + " A", SKUID = token + "_A" };

                var licenceB = new LicenseType { Name = token + " B", SKUID = token + "_B" };

                db.users.AddRange(new[] { userA, userB, userC });

                db.LicenseTypes.AddRange(new[] { licenceA, licenceB });

                await db.SaveChangesAsync();



                await db.Database.ExecuteSqlCommandAsync(@"

INSERT INTO dbo.user_license_history (user_id, license_type_id, valid_from_utc, valid_to_utc, from_source, valid_from_previous_refresh_utc, valid_to_previous_refresh_utc)

VALUES

(@uA, @lA, '2026-09-09T00:00:00', '2026-09-11T12:00:00', 1, '2026-09-08T00:00:00', '2026-09-11T00:00:00'),

(@uB, @lA, '2026-09-10T06:00:00', NULL, 0, NULL, NULL),

(@uA, @lB, '2026-09-11T00:00:00', '2026-09-13T00:00:00', 1, '2026-09-10T00:00:00', '2026-09-12T00:00:00'),

(@uC, @lA, '2026-09-01T00:00:00', '2026-09-05T00:00:00', 1, NULL, '2026-09-05T00:00:00');",

                    new SqlParameter("@uA", userA.ID),

                    new SqlParameter("@uB", userB.ID),

                    new SqlParameter("@uC", userC.ID),

                    new SqlParameter("@lA", licenceA.ID),

                    new SqlParameter("@lB", licenceB.ID));



                return new SeedIds { UserA = userA.ID, UserB = userB.ID, UserC = userC.ID, LicenceA = licenceA.ID, LicenceB = licenceB.ID };

            }

        }



        private static async Task CleanupAsync(string token)

        {

            using (var db = new AnalyticsEntitiesContext())

            {

                await db.Database.ExecuteSqlCommandAsync(@"

DELETE h FROM dbo.user_license_history h WHERE h.user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE @prefix);

DELETE FROM dbo.user_license_type_lookups WHERE user_id IN (SELECT id FROM dbo.users WHERE user_name LIKE @prefix);

DELETE FROM dbo.users WHERE user_name LIKE @prefix;

DELETE FROM dbo.license_types WHERE sku_id LIKE @skuPrefix;",

                    new SqlParameter("@prefix", token + "%"),

                    new SqlParameter("@skuPrefix", token + "%"));

            }

        }



        private static async Task<List<int>> QueryUsersAsync(string sql, params (string Name, object Value)[] parameters)

        {

            var results = new List<int>();

            using (var db = new AnalyticsEntitiesContext())

            using (var command = db.Database.Connection.CreateCommand())

            {

                command.CommandText = sql;

                foreach (var p in parameters) command.Parameters.Add(new SqlParameter(p.Name, p.Value));

                if (command.Connection.State != ConnectionState.Open) await command.Connection.OpenAsync();

                using (var reader = await command.ExecuteReaderAsync())

                {

                    while (await reader.ReadAsync()) results.Add(reader.GetInt32(0));

                }

            }

            return results;

        }



        private static async Task<List<(int UserId, int LicenseTypeId, decimal DaysHeld)>> QueryRangeAsync(string sql, params (string Name, object Value)[] parameters)

        {

            var results = new List<(int, int, decimal)>();

            using (var db = new AnalyticsEntitiesContext())

            using (var command = db.Database.Connection.CreateCommand())

            {

                command.CommandText = sql;

                foreach (var p in parameters) command.Parameters.Add(new SqlParameter(p.Name, p.Value));

                if (command.Connection.State != ConnectionState.Open) await command.Connection.OpenAsync();

                using (var reader = await command.ExecuteReaderAsync())

                {

                    while (await reader.ReadAsync()) results.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetDecimal(2)));

                }

            }

            return results;

        }

    }

}
