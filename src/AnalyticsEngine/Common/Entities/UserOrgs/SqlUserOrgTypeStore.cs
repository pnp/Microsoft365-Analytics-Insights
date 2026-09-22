using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Shared plumbing for the user-org SQL adapters: one place that knows how to open a connection and
    /// how to turn a duplicate-key violation into a message an admin can act on.
    /// </summary>
    /// <remarks>
    /// Raw <see cref="SqlConnection"/> rather than Entity Framework, deliberately. These tables are not
    /// in the EF model (see migration 202609221200001_UserOrganisations), the assignment merge needs
    /// <see cref="SqlBulkCopy"/> and a session-scoped temp table, and the import job needs explicit
    /// transactions - none of which EF would make easier here.
    /// </remarks>
    internal abstract class SqlUserOrgStoreBase
    {
        /// <summary>
        /// Generous, because a merge on a 200,000-user tenant is a genuinely large statement, but not
        /// unbounded: a merge that has not finished in ten minutes is stuck, and failing lets the
        /// importer retry on its next cycle rather than pinning a connection indefinitely.
        /// </summary>
        protected const int CommandTimeoutSeconds = 600;

        private readonly string _connectionString;

        protected SqlUserOrgStoreBase(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentNullException(nameof(connectionString));
            }

            _connectionString = connectionString;
        }

        /// <summary>Opens a connection, honouring Entra token auth for Azure SQL.</summary>
        protected async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
        {
            var connection = AzureSqlTokenAuth.CreateConnection(_connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        protected static SqlCommand Command(SqlConnection connection, string sql, SqlTransaction transaction = null)
        {
            var cmd = new SqlCommand(sql, connection, transaction);
            cmd.CommandTimeout = CommandTimeoutSeconds;
            return cmd;
        }

        /// <summary>
        /// SQL Server's two duplicate-key error numbers: 2601 (unique index) and 2627 (unique
        /// constraint). Which one you get depends on how the uniqueness was declared, so both must be
        /// handled or the same collision surfaces as an unhandled error half the time.
        /// </summary>
        protected static bool IsDuplicateKey(SqlException ex)
        {
            return ex != null && (ex.Number == 2601 || ex.Number == 2627);
        }

        protected static object DbValue(string value)
        {
            return string.IsNullOrEmpty(value) ? (object)DBNull.Value : value;
        }

        protected static object DbValue(DateTime? value)
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        protected static string ReadString(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        protected static DateTime? ReadNullableDate(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? (DateTime?)null : reader.GetDateTime(ordinal);
        }
    }

    /// <summary>
    /// SQL Server implementation of <see cref="IUserOrgTypeStore"/>.
    /// </summary>
    internal sealed class SqlUserOrgTypeStore : SqlUserOrgStoreBase, IUserOrgTypeStore
    {
        private const string SelectColumns =
            "id, name, source_kind, entra_attribute_name, is_enabled, created_utc, modified_utc";

        public SqlUserOrgTypeStore(string connectionString) : base(connectionString)
        {
        }

        public async Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await QueryTypesAsync(
                "SELECT " + SelectColumns + " FROM dbo.user_org_types ORDER BY name",
                null,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            return await QueryTypesAsync(
                "SELECT " + SelectColumns + " FROM dbo.user_org_types "
                + "WHERE is_enabled = 1 AND source_kind = @entra AND entra_attribute_name IS NOT NULL "
                + "ORDER BY name",
                cmd => cmd.Parameters.Add("@entra", SqlDbType.TinyInt).Value = (byte)UserOrgSourceKind.EntraAttribute,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
        {
            var results = await QueryTypesAsync(
                "SELECT " + SelectColumns + " FROM dbo.user_org_types WHERE id = @id",
                cmd => cmd.Parameters.Add("@id", SqlDbType.Int).Value = id,
                cancellationToken).ConfigureAwait(false);

            return results.Count == 0 ? null : results[0];
        }

        public async Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            // One round trip, three result sets: the types, their counts, and the latest import per
            // type. Counting inside the type query would make it a correlated subquery per row, and the
            // assignment table is the largest of the three.
            const string sql = @"
SELECT id, name, source_kind, entra_attribute_name, is_enabled, created_utc, modified_utc
FROM dbo.user_org_types
ORDER BY name;

SELECT t.id,
       (SELECT COUNT_BIG(*) FROM dbo.user_org_assignments a WHERE a.org_type_id = t.id) AS assigned_users,
       (SELECT COUNT_BIG(*) FROM dbo.user_org_values v WHERE v.org_type_id = t.id) AS distinct_values
FROM dbo.user_org_types t;

SELECT j.id, j.org_type_id, j.mode, j.status, j.file_name, j.started_by, j.queued_utc, j.started_utc,
       j.finished_utc, j.heartbeat_utc, j.rows_total, j.rows_applied, j.rows_cleared,
       j.rows_unknown_upn, j.rows_invalid, j.error_message
FROM dbo.user_org_import_jobs j
JOIN (SELECT org_type_id, MAX(id) AS id FROM dbo.user_org_import_jobs GROUP BY org_type_id) latest
  ON latest.id = j.id;";

            var summaries = new List<UserOrgTypeSummary>();
            var byId = new Dictionary<int, UserOrgTypeSummary>();

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var summary = new UserOrgTypeSummary { Type = ReadType(reader) };
                    summaries.Add(summary);
                    byId[summary.Type.Id] = summary;
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    UserOrgTypeSummary summary;
                    if (byId.TryGetValue(reader.GetInt32(0), out summary))
                    {
                        summary.AssignedUserCount = (int)Math.Min(reader.GetInt64(1), int.MaxValue);
                        summary.DistinctValueCount = (int)Math.Min(reader.GetInt64(2), int.MaxValue);
                    }
                }

                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var job = SqlUserOrgImportJobStore.ReadJob(reader);
                    UserOrgTypeSummary summary;
                    if (byId.TryGetValue(job.OrgTypeId, out summary))
                    {
                        summary.LastImport = job;
                    }
                }
            }

            return summaries;
        }

        public async Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(type);

            const string sql = @"
INSERT INTO dbo.user_org_types (name, source_kind, entra_attribute_name, is_enabled, created_utc)
OUTPUT INSERTED.id
VALUES (@name, @sourceKind, @attr, @enabled, SYSUTCDATETIME());";

            try
            {
                using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                using (var cmd = Command(connection, sql))
                {
                    AddTypeParameters(cmd, type);
                    var id = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                    return Convert.ToInt32(id);
                }
            }
            catch (SqlException ex) when (IsDuplicateKey(ex))
            {
                throw new UserOrgValidationException(
                    $"An organisation type called '{type.Name}' already exists.", ex);
            }
        }

        public async Task UpdateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(type);

            const string sql = @"
UPDATE dbo.user_org_types
SET name = @name,
    source_kind = @sourceKind,
    entra_attribute_name = @attr,
    is_enabled = @enabled,
    modified_utc = SYSUTCDATETIME()
WHERE id = @id;";

            try
            {
                using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                using (var cmd = Command(connection, sql))
                {
                    AddTypeParameters(cmd, type);
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = type.Id;

                    var affected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    if (affected == 0)
                    {
                        throw new UserOrgValidationException(
                            "That organisation type no longer exists - it may have been deleted in another session.");
                    }
                }
            }
            catch (SqlException ex) when (IsDuplicateKey(ex))
            {
                throw new UserOrgValidationException(
                    $"An organisation type called '{type.Name}' already exists.", ex);
            }
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Children are deleted explicitly, innermost first, because the assignments table already
            // carries the one cascade path SQL Server allows it (from dbo.users). One transaction, so a
            // failure part-way cannot leave values orphaned from their type.
            const string sql = @"
DELETE s FROM dbo.user_org_import_staging s
    JOIN dbo.user_org_import_jobs j ON j.id = s.job_id
    WHERE j.org_type_id = @id;
DELETE FROM dbo.user_org_import_jobs WHERE org_type_id = @id;
DELETE FROM dbo.user_org_assignments WHERE org_type_id = @id;
DELETE FROM dbo.user_org_values WHERE org_type_id = @id;
DELETE FROM dbo.user_org_types WHERE id = @id;";

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var tx = connection.BeginTransaction())
            {
                using (var cmd = Command(connection, sql, tx))
                {
                    cmd.Parameters.Add("@id", SqlDbType.Int).Value = id;
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                tx.Commit();
            }
        }

        private static void Validate(UserOrgType type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            string normalisedName;
            string error;
            if (!UserOrgRules.TryNormaliseOrgTypeName(type.Name, out normalisedName, out error))
            {
                throw new UserOrgValidationException(error);
            }

            type.Name = normalisedName;

            if (type.SourceKind == UserOrgSourceKind.EntraAttribute)
            {
                EntraOrgAttributeSpec spec;
                string attrError;
                if (!EntraOrgAttributeSpec.TryParse(type.EntraAttributeName, out spec, out attrError))
                {
                    throw new UserOrgValidationException(attrError);
                }

                // Store the canonical spelling, so the same slot cannot be configured twice under two
                // different-looking names and the delta-token hash stays stable.
                type.EntraAttributeName = spec.Canonical;
            }
            else
            {
                // A CSV-sourced type has no attribute. Leaving a stale one behind would quietly add a
                // property to the Graph $select for a type that no longer reads from Graph.
                type.EntraAttributeName = null;
            }
        }

        private static void AddTypeParameters(SqlCommand cmd, UserOrgType type)
        {
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, UserOrgRules.MaxOrgTypeNameLength).Value = type.Name;
            cmd.Parameters.Add("@sourceKind", SqlDbType.TinyInt).Value = (byte)type.SourceKind;
            cmd.Parameters.Add("@attr", SqlDbType.NVarChar, 200).Value = DbValue(type.EntraAttributeName);
            cmd.Parameters.Add("@enabled", SqlDbType.Bit).Value = type.IsEnabled;
        }

        private async Task<IReadOnlyList<UserOrgType>> QueryTypesAsync(
            string sql,
            Action<SqlCommand> addParameters,
            CancellationToken cancellationToken)
        {
            var results = new List<UserOrgType>();

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                if (addParameters != null)
                {
                    addParameters(cmd);
                }

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        results.Add(ReadType(reader));
                    }
                }
            }

            return results;
        }

        private static UserOrgType ReadType(SqlDataReader reader)
        {
            return new UserOrgType
            {
                Id = reader.GetInt32(0),
                Name = reader.GetString(1),
                SourceKind = (UserOrgSourceKind)reader.GetByte(2),
                EntraAttributeName = ReadString(reader, 3),
                IsEnabled = reader.GetBoolean(4),
                CreatedUtc = reader.GetDateTime(5),
                ModifiedUtc = ReadNullableDate(reader, 6),
            };
        }
    }
}
