using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Text;

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
        /// Reads a single lookup table into a case-insensitive name -&gt; id map so callers can
        /// resolve the specific value a coherent <see cref="SeedDataCatalogue.UserProfile"/>
        /// asked for (rather than picking a random id and breaking geo consistency).
        /// </summary>
        public static Dictionary<string, int> LoadLookupIdsByName(SqlConnection conn, string tableName)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT id, name FROM [{tableName}];";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!reader.IsDBNull(1)) map[reader.GetString(1)] = reader.GetInt32(0);
                    }
                }
            }
            return map;
        }

        /// <summary>
        /// Inserts <paramref name="count"/> users with realistic, internally-consistent metadata
        /// drawn from <see cref="SeedDataCatalogue"/>: a coherent geo locale (country / state / city
        /// / office / usage location / postal code all agree), a job title that fits the department,
        /// a company, a realistic account-enabled state, and a UPN spread across several tenant
        /// domains. After insertion a manager hierarchy is built via <see cref="AssignManagers"/>.
        /// The lookup tables must already be seeded via <see cref="EnsureMetadataLookups"/>.
        /// Returns the id + UPN of every user that was actually inserted; users whose UPN already
        /// exists are skipped silently.
        ///
        /// When <paramref name="upnDomain"/> is null (the default) users are spread deterministically
        /// across <see cref="SeedDataCatalogue.Domains"/> by index; pass a value to force a single domain.
        /// </summary>
        public static List<SeededUser> SeedUsers(SqlConnection conn, int count, Random random,
            string upnPrefix = "stressuser", string upnDomain = null)
        {
            var departments = LoadLookupIdsByName(conn, "user_departments");
            var companies = LoadLookupIdsByName(conn, "user_company_name");
            var jobTitles = LoadLookupIdsByName(conn, "user_job_titles");
            var states = LoadLookupIdsByName(conn, "user_state_or_province");
            var countries = LoadLookupIdsByName(conn, "user_country_or_region");
            var offices = LoadLookupIdsByName(conn, "user_office_locations");
            var usages = LoadLookupIdsByName(conn, "user_usage_locations");

            var inserted = new List<SeededUser>(count);

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM users WHERE user_name = @upn)
BEGIN
    INSERT INTO users (user_name, mail, azure_ad_id, account_enabled, last_updated, postalcode,
                       department_id, company_name_id, job_title_id,
                       state_or_province_id, country_or_region_id, office_location_id, usage_location_id)
    VALUES (@upn, @mail, @aad, @enabled, @lastUpdated, @postal,
            @dept, @company, @job, @state, @country, @office, @usage);
    SELECT CAST(SCOPE_IDENTITY() AS INT);
END
ELSE
    SELECT NULL;";

                var pUpn = cmd.Parameters.Add("@upn", SqlDbType.NVarChar, 400);
                var pMail = cmd.Parameters.Add("@mail", SqlDbType.NVarChar, 400);
                var pAad = cmd.Parameters.Add("@aad", SqlDbType.NVarChar, 400);
                var pEnabled = cmd.Parameters.Add("@enabled", SqlDbType.Bit);
                var pLastUpdated = cmd.Parameters.Add("@lastUpdated", SqlDbType.DateTime);
                var pPostal = cmd.Parameters.Add("@postal", SqlDbType.NVarChar, 50);
                var pDept = cmd.Parameters.Add("@dept", SqlDbType.Int);
                var pCompany = cmd.Parameters.Add("@company", SqlDbType.Int);
                var pJob = cmd.Parameters.Add("@job", SqlDbType.Int);
                var pState = cmd.Parameters.Add("@state", SqlDbType.Int);
                var pCountry = cmd.Parameters.Add("@country", SqlDbType.Int);
                var pOffice = cmd.Parameters.Add("@office", SqlDbType.Int);
                var pUsage = cmd.Parameters.Add("@usage", SqlDbType.Int);

                for (int i = 0; i < count; i++)
                {
                    var profile = SeedDataCatalogue.NextUserProfile(random);
                    var upn = SeedDataCatalogue.BuildUpn(upnPrefix, i, upnDomain);
                    pUpn.Value = upn;
                    pMail.Value = upn;
                    pAad.Value = Guid.NewGuid().ToString();
                    pEnabled.Value = profile.AccountEnabled;
                    pLastUpdated.Value = DateTime.UtcNow;
                    pPostal.Value = string.IsNullOrEmpty(profile.PostalCode) ? (object)DBNull.Value : profile.PostalCode;
                    pDept.Value = LookupOrDbNull(departments, profile.Department);
                    pCompany.Value = LookupOrDbNull(companies, profile.Company);
                    pJob.Value = LookupOrDbNull(jobTitles, profile.JobTitle);
                    pState.Value = LookupOrDbNull(states, profile.StateOrProvince);
                    pCountry.Value = LookupOrDbNull(countries, profile.Country);
                    pOffice.Value = LookupOrDbNull(offices, profile.OfficeLocation);
                    pUsage.Value = LookupOrDbNull(usages, profile.UsageLocation);

                    var newIdObj = cmd.ExecuteScalar();
                    if (newIdObj != null && newIdObj != DBNull.Value)
                    {
                        inserted.Add(new SeededUser(Convert.ToInt32(newIdObj), upn));
                    }
                }
            }

            // Give the tenant a realistic reporting hierarchy (people report to a manager in
            // their own company). Keyed on the UPN prefix so it spans every domain we just used.
            AssignManagers(conn, upnPrefix, random);

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
        /// Builds a realistic reporting hierarchy across users whose UPN starts with
        /// <paramref name="upnPrefix"/> (matched across every domain). Within each company a
        /// small fraction are made managers (left top-level); everyone else reports to a random
        /// manager in the same company. Only touches users whose <c>manager_id</c> is still NULL,
        /// so it is idempotent and safe to re-run.
        ///
        /// Scale note (tenants of ~200k users): the reporting pairs are applied with set-based,
        /// parameter-chunked <c>UPDATE ... FROM (VALUES ...)</c> statements (~500 pairs each, well
        /// under SQL Server's 2100-parameter limit) rather than one round-trip per user, so a full
        /// tenant is assigned in a few hundred statements instead of hundreds of thousands.
        /// </summary>
        public static int AssignManagers(SqlConnection conn, string upnPrefix, Random random,
            double managerFraction = 0.1)
        {
            // Load candidate users (this generator's prefix, no manager yet), grouped by company
            // so nobody reports across company boundaries. Company -1 means "no company set".
            var byCompany = new Dictionary<int, List<int>>();
            var total = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, company_name_id FROM users
WHERE manager_id IS NULL AND user_name LIKE @prefix + '%';";
                cmd.Parameters.Add("@prefix", SqlDbType.NVarChar, 400).Value = upnPrefix;
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = reader.GetInt32(0);
                        int companyId = reader.IsDBNull(1) ? -1 : reader.GetInt32(1);
                        if (!byCompany.TryGetValue(companyId, out var list))
                        {
                            list = new List<int>();
                            byCompany[companyId] = list;
                        }
                        list.Add(id);
                        total++;
                    }
                }
            }
            if (total < 2) return 0;

            // Decide who reports to whom (in memory).
            var assignments = new List<KeyValuePair<int, int>>(total);
            foreach (var kvp in byCompany)
            {
                var users = kvp.Value;
                if (users.Count < 2) continue; // a lone user in a company has nobody to report to

                int managerCount = Math.Max(1, (int)Math.Round(users.Count * managerFraction));
                managerCount = Math.Min(managerCount, users.Count - 1); // always leave at least one report
                var managers = users.GetRange(0, managerCount);
                for (int i = managerCount; i < users.Count; i++)
                {
                    assignments.Add(new KeyValuePair<int, int>(users[i], managers[random.Next(managers.Count)]));
                }
            }
            if (assignments.Count == 0) return 0;

            // Apply set-based in parameter-bounded chunks (2 params per pair).
            const int chunkPairs = 500;
            int updated = 0;
            for (int start = 0; start < assignments.Count; start += chunkPairs)
            {
                int take = Math.Min(chunkPairs, assignments.Count - start);
                using (var cmd = conn.CreateCommand())
                {
                    var sb = new StringBuilder("UPDATE u SET manager_id = v.mgr FROM users u JOIN (VALUES ");
                    for (int i = 0; i < take; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append($"(@u{i},@m{i})");
                        cmd.Parameters.Add($"@u{i}", SqlDbType.Int).Value = assignments[start + i].Key;
                        cmd.Parameters.Add($"@m{i}", SqlDbType.Int).Value = assignments[start + i].Value;
                    }
                    sb.Append(") AS v(id, mgr) ON u.id = v.id;");
                    cmd.CommandText = sb.ToString();
                    updated += cmd.ExecuteNonQuery();
                }
            }
            return updated;
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

        private static object LookupOrDbNull(Dictionary<string, int> map, string name)
        {
            if (name != null && map.TryGetValue(name, out var id)) return id;
            return DBNull.Value;
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
