using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;

namespace Tests.FakeDataGen.Seeding
{
    /// <summary>
    /// Shared seeder for the user/license/lookup tables used by data generators and
    /// stress tests that need realistic prerequisite rows behind activity, audit, or
    /// copilot data.
    ///
    /// Idempotent: each method either inserts the missing rows or no-ops when the
    /// target table already contains data, so callers can be re-run against the
    /// same database without duplicating or wiping a tenant's existing metadata.
    /// </summary>
    public static class UserMetadataSeeder
    {
        /// <summary>
        /// Seeds every metadata lookup table referenced from the users table.
        /// Safe to call repeatedly; rows are only inserted when missing by name.
        /// </summary>
        public static void EnsureMetadataLookups(SqlConnection conn)
        {
            InsertLookupValues(conn, "user_departments", SeedDataCatalogue.Departments);
            InsertLookupValues(conn, "user_company_name", SeedDataCatalogue.Companies);
            InsertLookupValues(conn, "user_job_titles", SeedDataCatalogue.JobTitles);
            InsertLookupValues(conn, "user_state_or_province", SeedDataCatalogue.StatesOrProvinces);
            InsertLookupValues(conn, "user_country_or_region", SeedDataCatalogue.Countries);
            InsertLookupValues(conn, "user_office_locations", SeedDataCatalogue.OfficeLocations);
            InsertLookupValues(conn, "user_usage_locations", SeedDataCatalogue.UsageLocations);
        }

        /// <summary>
        /// Seeds the license_types catalogue if (and only if) the table is empty.
        /// Never modifies an existing tenant's real license catalogue.
        /// </summary>
        public static void EnsureLicenseTypes(SqlConnection conn)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM license_types;";
                var existing = Convert.ToInt32(cmd.ExecuteScalar());
                if (existing > 0)
                {
                    Console.WriteLine($"  license_types already populated ({existing} rows) - skipping seed.");
                    return;
                }

                Console.WriteLine($"  Seeding license_types ({SeedDataCatalogue.LicenseCatalogue.Length} SKUs)...");
                cmd.CommandText = "INSERT INTO license_types (sku_id, name) VALUES (@sku, @name);";
                var pSku = cmd.Parameters.Add("@sku", SqlDbType.NVarChar, 400);
                var pName = cmd.Parameters.Add("@name", SqlDbType.NVarChar, 100);
                foreach (var lic in SeedDataCatalogue.LicenseCatalogue)
                {
                    pSku.Value = lic.SkuId;
                    pName.Value = lic.Name;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Returns the id of every row currently in license_types so callers can randomly
        /// assign one (or several) to seeded users.
        /// </summary>
        public static List<int> LoadLicenseTypeIds(SqlConnection conn)
        {
            var ids = new List<int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id FROM license_types;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                }
            }
            return ids;
        }

        /// <summary>
        /// Reads ids for a single lookup table keyed by name.
        /// </summary>
        public static List<int> LoadLookupIds(SqlConnection conn, string tableName)
        {
            var ids = new List<int>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT id FROM [{tableName}];";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) ids.Add(reader.GetInt32(0));
                }
            }
            return ids;
        }

        /// <summary>
        /// Inserts <paramref name="count"/> users with random metadata FK ids drawn from
        /// the lookup tables (which must already be seeded via <see cref="EnsureMetadataLookups"/>).
        /// Returns the id + UPN of every user that was actually inserted; users whose UPN already
        /// exists are skipped silently.
        /// </summary>
        public static List<SeededUser> SeedUsers(SqlConnection conn, int count, Random random,
            string upnPrefix = "stressuser", string upnDomain = "contoso.com")
        {
            var departments = LoadLookupIds(conn, "user_departments");
            var companies = LoadLookupIds(conn, "user_company_name");
            var jobTitles = LoadLookupIds(conn, "user_job_titles");
            var states = LoadLookupIds(conn, "user_state_or_province");
            var countries = LoadLookupIds(conn, "user_country_or_region");
            var offices = LoadLookupIds(conn, "user_office_locations");
            var usages = LoadLookupIds(conn, "user_usage_locations");

            var inserted = new List<SeededUser>(count);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM users WHERE user_name = @upn)
BEGIN
    INSERT INTO users (user_name, mail, azure_ad_id, account_enabled, last_updated,
                       department_id, company_name_id, job_title_id,
                       state_or_province_id, country_or_region_id, office_location_id, usage_location_id)
    VALUES (@upn, @mail, @aad, 1, @lastUpdated,
            @dept, @company, @job, @state, @country, @office, @usage);
    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
ELSE
    SELECT NULL;";

                var pUpn = cmd.Parameters.Add("@upn", SqlDbType.NVarChar, 400);
                var pMail = cmd.Parameters.Add("@mail", SqlDbType.NVarChar, 400);
                var pAad = cmd.Parameters.Add("@aad", SqlDbType.NVarChar, 400);
                var pLastUpdated = cmd.Parameters.Add("@lastUpdated", SqlDbType.DateTime);
                var pDept = cmd.Parameters.Add("@dept", SqlDbType.Int);
                var pCompany = cmd.Parameters.Add("@company", SqlDbType.Int);
                var pJob = cmd.Parameters.Add("@job", SqlDbType.Int);
                var pState = cmd.Parameters.Add("@state", SqlDbType.Int);
                var pCountry = cmd.Parameters.Add("@country", SqlDbType.Int);
                var pOffice = cmd.Parameters.Add("@office", SqlDbType.Int);
                var pUsage = cmd.Parameters.Add("@usage", SqlDbType.Int);

                for (int i = 0; i < count; i++)
                {
                    var upn = $"{upnPrefix}{i}@{upnDomain}";
                    pUpn.Value = upn;
                    pMail.Value = upn;
                    pAad.Value = Guid.NewGuid().ToString();
                    pLastUpdated.Value = DateTime.UtcNow;
                    pDept.Value = PickOrDbNull(departments, random);
                    pCompany.Value = PickOrDbNull(companies, random);
                    pJob.Value = PickOrDbNull(jobTitles, random);
                    pState.Value = PickOrDbNull(states, random);
                    pCountry.Value = PickOrDbNull(countries, random);
                    pOffice.Value = PickOrDbNull(offices, random);
                    pUsage.Value = PickOrDbNull(usages, random);

                    var newIdObj = cmd.ExecuteScalar();
                    if (newIdObj != null && newIdObj != DBNull.Value)
                    {
                        inserted.Add(new SeededUser(Convert.ToInt32(newIdObj), upn));
                    }
                }
            }

            return inserted;
        }

        /// <summary>
        /// Randomly assigns up to <paramref name="maxLicensesPerUser"/> distinct licenses
        /// to each user from the provided <paramref name="licenseTypeIds"/> set. Skips
        /// (user, license) pairs that already exist so the call is idempotent and honours
        /// the unique index on (license_type_id, user_id).
        /// </summary>
        public static int AssignRandomLicenses(SqlConnection conn, IEnumerable<int> userIds,
            IList<int> licenseTypeIds, Random random, int maxLicensesPerUser = 2)
        {
            if (licenseTypeIds == null || licenseTypeIds.Count == 0) return 0;

            int assignments = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM user_license_type_lookups WHERE user_id = @userId AND license_type_id = @licenseTypeId)
    INSERT INTO user_license_type_lookups (user_id, license_type_id) VALUES (@userId, @licenseTypeId);";
                var pUser = cmd.Parameters.Add("@userId", SqlDbType.Int);
                var pLic = cmd.Parameters.Add("@licenseTypeId", SqlDbType.Int);

                foreach (var userId in userIds)
                {
                    int licensesForThisUser = Math.Min(maxLicensesPerUser, licenseTypeIds.Count);
                    if (licensesForThisUser <= 0) continue;

                    var picked = new HashSet<int>();
                    int attempts = 0;
                    while (picked.Count < licensesForThisUser && attempts < licensesForThisUser * 4)
                    {
                        picked.Add(licenseTypeIds[random.Next(licenseTypeIds.Count)]);
                        attempts++;
                    }

                    foreach (var licId in picked)
                    {
                        pUser.Value = userId;
                        pLic.Value = licId;
                        cmd.ExecuteNonQuery();
                        assignments++;
                    }
                }
            }
            return assignments;
        }

        /// <summary>
        /// Inserts each value into <paramref name="tableName"/> if a row with that name
        /// does not already exist. Used for all single-column "[id, name]" lookup tables.
        /// </summary>
        public static void InsertLookupValues(SqlConnection conn, string tableName, string[] values)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $@"
IF NOT EXISTS (SELECT 1 FROM [{tableName}] WHERE name = @name)
    INSERT INTO [{tableName}] (name) VALUES (@name);";
                var pName = cmd.Parameters.Add("@name", SqlDbType.NVarChar, 100);
                foreach (var val in values)
                {
                    pName.Value = val;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private static object PickOrDbNull(IList<int> ids, Random random)
        {
            if (ids == null || ids.Count == 0) return DBNull.Value;
            return ids[random.Next(ids.Count)];
        }
    }

    /// <summary>
    /// A user inserted by <see cref="UserMetadataSeeder.SeedUsers"/>.
    /// </summary>
    public class SeededUser
    {
        public int Id { get; }
        public string UserPrincipalName { get; }

        public SeededUser(int id, string upn)
        {
            Id = id;
            UserPrincipalName = upn;
        }
    }
}
