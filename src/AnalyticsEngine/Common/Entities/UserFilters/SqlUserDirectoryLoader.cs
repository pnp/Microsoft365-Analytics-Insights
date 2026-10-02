using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// Reads every person's filterable attributes into a <see cref="UserDirectorySnapshot"/>.
    /// </summary>
    /// <remarks>
    /// A port, so the filter - and every report that narrows by it - can be tested with a hand-built
    /// directory and no database. <see cref="SqlUserDirectoryLoader"/> is the SQL Server adapter.
    /// </remarks>
    public interface IUserDirectoryLoader
    {
        Task<UserDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>Builds the SQL Server adapters for the user-filter ports.</summary>
    public static class UserFilterStores
    {
        public static IUserDirectoryLoader CreateDirectoryLoader(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string to the Analytics database is required.", nameof(connectionString));
            }

            return new SqlUserDirectoryLoader(connectionString);
        }
    }

    /// <summary>
    /// SQL Server implementation of <see cref="IUserDirectoryLoader"/>.
    /// </summary>
    /// <remarks>
    /// <para>One round trip, integer keys throughout. The user rows carry lookup ids rather than joined
    /// names, and each small lookup table is read once and resolved in memory: joining the names in SQL
    /// would send the same department name across the wire once per person - 200,000 times over for
    /// a large tenant - where the ids cost four bytes each. The organisation assignments are sent the
    /// same way, as three integers per row with the value names in their own result set.</para>
    /// <para>Plain scans, no new index: every table is read whole, which is what a scan is for. Read in
    /// one SNAPSHOT transaction where the database allows it, so all the result sets describe the same
    /// moment; otherwise under READ COMMITTED, which takes only momentary shared locks.</para>
    /// <para>The user organisation tables are optional. A database that has not been upgraded to the
    /// migration that creates them still yields a snapshot of the Entra attributes rather than an error.</para>
    /// </remarks>
    internal sealed class SqlUserDirectoryLoader : IUserDirectoryLoader
    {
        /// <summary>Generous for a 200,000-row read, but bounded so a blocked read fails rather than hangs.</summary>
        private const int CommandTimeoutSeconds = 180;

        internal const string Sql = @"
SET NOCOUNT ON;

SELECT id, name FROM dbo.user_departments;
SELECT id, name FROM dbo.user_job_titles;
SELECT id, name FROM dbo.user_company_name;
SELECT id, name FROM dbo.user_office_locations;
SELECT id, name FROM dbo.user_country_or_region;
SELECT id, name FROM dbo.user_state_or_province;
SELECT id, name FROM dbo.user_usage_locations;

SELECT u.id, u.user_name, u.mail, u.account_enabled, u.manager_id,
       u.department_id, u.job_title_id, u.company_name_id, u.office_location_id,
       u.country_or_region_id, u.state_or_province_id, u.usage_location_id,
       u.azure_ad_id
FROM dbo.users u;

IF OBJECT_ID(N'dbo.user_org_assignments', N'U') IS NOT NULL
BEGIN
    SELECT t.id, t.name
    FROM dbo.user_org_types t
    WHERE t.is_enabled = 1;

    -- Only the values somebody holds: they are what the assignments below are resolved against. A value
    -- stays in the table after everyone has moved off it, until the type's source changes, so reading
    -- every one would grow each load with the type's history rather than with its current membership.
    SELECT v.id, v.name
    FROM dbo.user_org_values v
    JOIN dbo.user_org_types t ON t.id = v.org_type_id
    WHERE t.is_enabled = 1
      AND EXISTS (SELECT 1 FROM dbo.user_org_assignments a
                  WHERE a.org_type_id = v.org_type_id AND a.org_value_id = v.id);

    SELECT a.user_id, a.org_type_id, a.org_value_id
    FROM dbo.user_org_assignments a
    JOIN dbo.user_org_types t ON t.id = a.org_type_id
    WHERE t.is_enabled = 1;
END";

        private readonly string _connectionString;

        public SqlUserDirectoryLoader(string connectionString)
        {
            _connectionString = connectionString;
        }

        public async Task<UserDirectorySnapshot> LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var builder = new UserDirectorySnapshotBuilder();

            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // Eleven statements, and without a transaction each reads a different moment. An import
                // committing between the organisation values and the assignments leaves assignments
                // naming a value the loader never saw, and they are dropped; the same goes for a new
                // department between the lookups and the users. Under a SNAPSHOT transaction every
                // statement reads the same moment. Azure SQL allows it by default; a database that does
                // not is read as before.
                var snapshot = await SnapshotIsolationAllowedAsync(connection, cancellationToken).ConfigureAwait(false);
                try
                {
                    using (var tx = snapshot ? connection.BeginTransaction(IsolationLevel.Snapshot) : null)
                    {
                        using (var cmd = new SqlCommand(Sql, connection, tx) { CommandTimeout = CommandTimeoutSeconds })
                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await ReadAllAsync(reader, builder, cancellationToken).ConfigureAwait(false);
                        }

                        tx?.Commit();
                    }
                }
                finally
                {
                    if (snapshot)
                    {
                        await ResetIsolationLevelAsync(connection).ConfigureAwait(false);
                    }
                }
            }

            return builder.Build(DateTime.UtcNow);
        }

        /// <summary>
        /// Puts a pooled connection back to READ COMMITTED - after a failed load as well as a good one.
        /// </summary>
        /// <remarks>
        /// SQL Server before 2014 does not reset the isolation level when a pooled connection is reused, and
        /// the next user of this one expects READ COMMITTED. Best effort: after a fault the connection may
        /// already be unusable, and that must not hide the fault that got it there.
        /// </remarks>
        private static async Task ResetIsolationLevelAsync(SqlConnection connection)
        {
            if (connection.State != ConnectionState.Open)
            {
                return;
            }

            try
            {
                using (var reset = new SqlCommand("SET TRANSACTION ISOLATION LEVEL READ COMMITTED;", connection))
                {
                    // Not the caller's token: a cancelled load still hands its connection back to the pool.
                    await reset.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // See the remarks.
            }
        }

        private static async Task<bool> SnapshotIsolationAllowedAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            using (var cmd = new SqlCommand(
                "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();", connection))
            {
                var state = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                return state != null && state != DBNull.Value && Convert.ToInt32(state) == 1;
            }
        }

        private static async Task ReadAllAsync(SqlDataReader reader, UserDirectorySnapshotBuilder builder, CancellationToken cancellationToken)
        {
            var departments = await ReadLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var jobTitles = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var companies = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var offices = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var countries = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var states = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);
            var usageLocations = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);

            // Read synchronously row by row: SqlClient's per-row ReadAsync costs more than the row
            // itself for a result this narrow, and this whole load already runs on a thread-pool
            // thread of its own (see CachedUserDirectorySource), never on a request's.
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (reader.Read())
            {
                builder.AddUser(new UserDirectoryEntry
                {
                    UserId = reader.GetInt32(0),
                    UserPrincipalName = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Mail = reader.IsDBNull(2) ? null : reader.GetString(2),
                    AccountEnabled = reader.IsDBNull(3) ? (bool?)null : reader.GetBoolean(3),
                    ManagerUserId = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    Department = Lookup(departments, reader, 5),
                    JobTitle = Lookup(jobTitles, reader, 6),
                    CompanyName = Lookup(companies, reader, 7),
                    OfficeLocation = Lookup(offices, reader, 8),
                    Country = Lookup(countries, reader, 9),
                    StateOrProvince = Lookup(states, reader, 10),
                    UsageLocation = Lookup(usageLocations, reader, 11),
                    EntraObjectId = reader.IsDBNull(12) ? null : reader.GetString(12),
                });
            }

            // Absent on a database that predates user organisations - see the remarks.
            if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    builder.AddOrgType(reader.GetInt32(0), reader.GetString(1));
                }

                var values = await NextLookupAsync(reader, cancellationToken).ConfigureAwait(false);

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (reader.Read())
                {
                    if (values.TryGetValue(reader.GetInt32(2), out var value))
                    {
                        builder.AddOrgAssignment(reader.GetInt32(0), reader.GetInt32(1), value);
                    }
                }
            }
        }

        private static async Task<Dictionary<int, string>> NextLookupAsync(SqlDataReader reader, CancellationToken cancellationToken)
        {
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLookupAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<Dictionary<int, string>> ReadLookupAsync(SqlDataReader reader, CancellationToken cancellationToken)
        {
            var map = new Dictionary<int, string>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(1)) map[reader.GetInt32(0)] = reader.GetString(1);
            }

            return map;
        }

        private static string Lookup(Dictionary<int, string> map, SqlDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            return map.TryGetValue(reader.GetInt32(ordinal), out var name) ? name : null;
        }
    }
}
