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
            "id, name, source_kind, entra_attribute_name, is_enabled, source_generation, created_utc, modified_utc";

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
            // The job columns come from the job store so the two lists cannot drift: a hand-written
            // copy here is exactly how a newly added column turns into a null-read inside ReadJob.
            // The column list is shared with the single-row reads so ReadType's ordinals cannot drift
            // from what any one query selects - a duplicated list is how the last added column turned
            // into a null-read deep inside a reader.
            var sql = @"
SELECT " + SelectColumns + @"
FROM dbo.user_org_types
ORDER BY name;

SELECT t.id,
       (SELECT COUNT_BIG(*) FROM dbo.user_org_assignments a WHERE a.org_type_id = t.id) AS assigned_users,
       (SELECT COUNT_BIG(*) FROM dbo.user_org_values v WHERE v.org_type_id = t.id) AS distinct_values
FROM dbo.user_org_types t;

SELECT " + SqlUserOrgImportJobStore.JobColumnsFor("j") + @"
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

        public async Task UpdateAsync(
            UserOrgType type,
            bool clearAssignments,
            bool bumpGeneration,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Validate(type);

            // The clear is in the same statement batch, and therefore the same transaction, as the
            // update. Done as two round trips the pair is not atomic, and the failure is one-way: a
            // committed repoint whose clear did not run leaves values from the OLD source in place,
            // and a retry no longer clears them because the stored configuration already matches what
            // the admin is submitting. The stale values then never go away.
            const string sql = @"
DECLARE @affected INT, @lockResult INT, @lockName NVARCHAR(255) = N'user_org_type_' + CAST(@id AS NVARCHAR(20));

-- Same application lock as the import paths, so a reconfiguration cannot interleave with a queue or
-- an apply for this org type.
EXEC @lockResult = sp_getapplock
    @Resource = @lockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;

IF @lockResult < 0
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END

UPDATE dbo.user_org_types
SET name = @name,
    source_kind = @sourceKind,
    entra_attribute_name = @attr,
    is_enabled = @enabled,
    modified_utc = SYSUTCDATETIME(),
    -- Bumped whenever this cycle's stored delta token stops being safe to reuse, which is any change
    -- that makes the merge fence a type's updates out: the values being discarded, the source kind
    -- changing, or the type being disabled. The last one is not obvious. A type disabled mid-cycle
    -- has its updates dropped by the merge, but the cycle still commits its delta token - so if the
    -- generation did not move, re-enabling would rebuild the SAME cache key and resume from a token
    -- that has already advanced past those users. They would never be re-read.
    source_generation = source_generation + CASE WHEN @bumpGeneration = 1 THEN 1 ELSE 0 END
WHERE id = @id;

SET @affected = @@ROWCOUNT;

IF @affected > 0 AND @clearAssignments = 1
BEGIN
    -- Assignments first: they are what points at the values.
    DELETE FROM dbo.user_org_assignments WHERE org_type_id = @id;
    -- The values go too. They are labels read out of a source that is no longer this dimension's
    -- source of truth, so leaving them would keep offering an admin a list of organisations that
    -- nothing is in and nothing will ever repopulate.
    DELETE FROM dbo.user_org_values WHERE org_type_id = @id;
END

SELECT @affected;";

            try
            {
                using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
                using (var tx = connection.BeginTransaction())
                {
                    int affected;
                    using (var cmd = Command(connection, sql, tx))
                    {
                        AddTypeParameters(cmd, type);
                        cmd.Parameters.Add("@id", SqlDbType.Int).Value = type.Id;
                        cmd.Parameters.Add("@clearAssignments", SqlDbType.Bit).Value = clearAssignments;
                        cmd.Parameters.Add("@bumpGeneration", SqlDbType.Bit).Value = bumpGeneration;

                        affected = Convert.ToInt32(
                            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
                    }

                    if (affected == 0)
                    {
                        throw new UserOrgValidationException(
                            "That organisation type no longer exists - it may have been deleted in another session.");
                    }

                    tx.Commit();
                }
            }
            catch (SqlException ex) when (IsDuplicateKey(ex))
            {
                throw new UserOrgValidationException(
                    $"An organisation type called '{type.Name}' already exists.", ex);
            }
            catch (SqlException ex) when (ex.Message.IndexOf("USERORG_ACTIVE_JOB", StringComparison.Ordinal) >= 0)
            {
                // Same marker DeleteAsync translates. Without this the admin waits out the lock
                // timeout and then gets a generic 500 instead of being told an import is running.
                throw new UserOrgValidationException(
                    $"An import for '{type.Name}' is running. Wait for it to finish before changing the type.", ex);
            }
        }

        public async Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken))
        {
            // Children are deleted explicitly, innermost first, because the assignments table already
            // carries the one cascade path SQL Server allows it (from dbo.users). One transaction, so a
            // failure part-way cannot leave values orphaned from their type.
            //
            // Two orderings are load-bearing here. The application lock is the same one the import job
            // store takes, so this cannot run alongside a queue or an apply for this org type. And the
            // TYPE row is locked first, before any child, which is the order every other writer uses -
            // the Entra merge holds the type row while it writes values and assignments, and the type
            // update changes the type row before clearing its children. Deleting the children first
            // and reaching the type row last is the reverse of that, which is how an org-type delete
            // and a user-metadata merge for the same type deadlock. The application lock does not
            // cover it, because the merge is the one writer that does not take one.
            const string sql = @"
DECLARE @lockResult INT, @locked INT, @lockName NVARCHAR(255) = N'user_org_type_' + CAST(@id AS NVARCHAR(20));

EXEC @lockResult = sp_getapplock
    @Resource = @lockName, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000;

IF @lockResult < 0
BEGIN
    RAISERROR('USERORG_ACTIVE_JOB', 16, 1);
    RETURN;
END

-- Take the type row before any of its children, and hold it for the transaction.
SELECT @locked = id FROM dbo.user_org_types WITH (UPDLOCK, HOLDLOCK) WHERE id = @id;

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

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (SqlException ex) when (ex.Message.IndexOf("USERORG_ACTIVE_JOB", StringComparison.Ordinal) >= 0)
                    {
                        throw new UserOrgValidationException(
                            "An import for this organisation type is running. Wait for it to finish before "
                            + "deleting the type.", ex);
                    }
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
                SourceGeneration = reader.GetInt32(5),
                CreatedUtc = reader.GetDateTime(6),
                ModifiedUtc = ReadNullableDate(reader, 7),
            };
        }
    }
}
