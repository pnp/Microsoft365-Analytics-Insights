using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// SQL Server implementation of <see cref="IUserOrgAssignmentStore"/>: bulk-copies a batch into a
    /// session temp table and applies it with four set-based statements.
    /// </summary>
    /// <remarks>
    /// Shaped for a ~200,000-user tenant with several org types configured, so roughly a million
    /// assignment rows. There is deliberately no per-user query anywhere in this class: the batch goes
    /// up once via <see cref="SqlBulkCopy"/> and every subsequent step is a single set-based statement
    /// joined against the temp table, mirroring <c>SqlUserBulkUpdateWriter</c>.
    /// </remarks>
    internal sealed class SqlUserOrgAssignmentStore : SqlUserOrgStoreBase, IUserOrgAssignmentStore, IUserOrgUserLookup
    {
        internal const string TempTableName = "#user_org_updates";

        public SqlUserOrgAssignmentStore(string connectionString) : base(connectionString)
        {
        }

        /// <summary>
        /// The temp table the batch is bulk-copied into.
        /// </summary>
        /// <remarks>
        /// The primary key is load-bearing, not decoration. <c>user_org_assignments</c> is keyed on
        /// (user_id, org_type_id), so two rows for the same slot in one batch would be a silent
        /// double-apply at best and a primary key violation at worst. Callers de-duplicate with
        /// <see cref="UserOrgRules.DeduplicateUpdates"/>; this key means a caller that forgets fails
        /// immediately and obviously, at the bulk copy, instead of corrupting the merge.
        /// </remarks>
        internal const string CreateTempTableSql = @"
CREATE TABLE " + TempTableName + @" (
    user_id     INT            NOT NULL,
    org_type_id INT            NOT NULL,
    org_value   NVARCHAR(200)  NULL,
    expected_generation INT    NULL,
    PRIMARY KEY CLUSTERED (user_id, org_type_id)
);";

        /// <summary>
        /// Applies the staged batch.
        /// </summary>
        /// <remarks>
        /// Four statements rather than a single <c>MERGE</c>. <c>MERGE</c> has a long history of
        /// concurrency and cardinality defects, and the house style for the equivalent user-metadata
        /// path is already <c>UPDATE ... FROM</c> against a temp table.
        ///
        /// The insert filters on a user still existing. A user deleted between the caller building the
        /// batch and this statement running is not an error worth failing a 200,000-row import over -
        /// the foreign key would otherwise abort the entire merge for one racing row.
        /// </remarks>
        internal const string MergeSql = @"
SET NOCOUNT ON;

DECLARE @valuesCreated INT = 0, @cleared INT = 0, @applied INT = 0;

-- 0. Drop anything whose org type is no longer sourced the way the caller believed when it built
--    this batch. A user-metadata cycle reads its org types at the start and can then spend many
--    minutes loading 200,000 users from Graph; an administrator who switches a type to CSV,
--    disables it, or repoints it at a different attribute during that window would otherwise have
--    their change quietly undone by values read from the attribute they just stopped using - and a
--    later CSV Merge never touches users the file does not mention, so those values would then
--    survive indefinitely.
--
--    The generation is checked as well as the source kind, because source kind alone is too weak:
--    repointing a type from one attribute to another leaves it enabled and Entra-sourced the whole
--    time, so a batch loaded from the OLD attribute passes that test and writes back exactly the
--    values the repoint had just discarded.
--
--    UPDLOCK, HOLDLOCK rather than a plain read. Under READ COMMITTED an ordinary shared lock is
--    released the moment this statement ends, so a reconfiguration could commit between here and
--    the writes below and be undone by them anyway - the check would look right and prove nothing.
--    Holding the lock for the transaction is what actually fences it.
--
--    It also puts this in step with the rest of the feature's lock ordering: every writer takes the
--    TYPE row before that type's values and assignments. This merge is the one writer that does not
--    take the per-type application lock - it can span many types at once - so row-lock ordering is
--    all that stands between it and a concurrent org-type delete.
IF @expectedSourceKind IS NOT NULL
BEGIN
    DELETE u
    FROM " + TempTableName + @" u
    WHERE NOT EXISTS (SELECT 1 FROM dbo.user_org_types t WITH (UPDLOCK, HOLDLOCK)
                      WHERE t.id = u.org_type_id
                        AND t.source_kind = @expectedSourceKind
                        AND t.is_enabled = 1
                        AND (u.expected_generation IS NULL OR t.source_generation = u.expected_generation));
END

-- 1. Register any org value we have not seen before. Matching uses the database collation, which is
--    case-insensitive, so this agrees with UX_user_org_values_type_name rather than fighting it.
INSERT INTO dbo.user_org_values (org_type_id, name)
SELECT DISTINCT u.org_type_id, u.org_value
FROM " + TempTableName + @" u
WHERE u.org_value IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_values v
                  WHERE v.org_type_id = u.org_type_id AND v.name = u.org_value);
SET @valuesCreated = @@ROWCOUNT;

-- 2. A NULL incoming value means the user no longer has a value for this org type.
DELETE a
FROM dbo.user_org_assignments a
JOIN " + TempTableName + @" u ON u.user_id = a.user_id AND u.org_type_id = a.org_type_id
WHERE u.org_value IS NULL;
SET @cleared = @@ROWCOUNT;

-- 3. Repoint the users whose value changed.
UPDATE a
SET a.org_value_id = v.id,
    a.last_updated_utc = SYSUTCDATETIME()
FROM dbo.user_org_assignments a
JOIN " + TempTableName + @" u ON u.user_id = a.user_id AND u.org_type_id = a.org_type_id
JOIN dbo.user_org_values v ON v.org_type_id = u.org_type_id AND v.name = u.org_value
WHERE u.org_value IS NOT NULL
  AND a.org_value_id <> v.id;
SET @applied = @@ROWCOUNT;

-- 4. Add the users who did not have a value for this org type yet.
INSERT INTO dbo.user_org_assignments (user_id, org_type_id, org_value_id)
SELECT u.user_id, u.org_type_id, v.id
FROM " + TempTableName + @" u
JOIN dbo.user_org_values v ON v.org_type_id = u.org_type_id AND v.name = u.org_value
WHERE u.org_value IS NOT NULL
  AND EXISTS (SELECT 1 FROM dbo.users usr WHERE usr.id = u.user_id)
  AND NOT EXISTS (SELECT 1 FROM dbo.user_org_assignments a
                  WHERE a.user_id = u.user_id AND a.org_type_id = u.org_type_id);
SET @applied = @applied + @@ROWCOUNT;

SELECT @applied AS applied, @cleared AS cleared, @valuesCreated AS values_created;";

        public async Task<UserOrgMergeResult> MergeAsync(
            IReadOnlyList<UserOrgAssignmentUpdate> updates,
            UserOrgSourceKind? expectedSourceKind = null,
            IReadOnlyDictionary<int, int> expectedGenerations = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            int duplicatesCollapsed;
            var deduplicated = UserOrgRules.DeduplicateUpdates(updates, out duplicatesCollapsed);

            var result = new UserOrgMergeResult { DuplicatesCollapsed = duplicatesCollapsed };
            if (deduplicated.Count == 0)
            {
                return result;
            }

            var batch = BuildBatchTable(deduplicated, expectedGenerations);

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                using (var cmd = Command(connection, CreateTempTableSql))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (var bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = TempTableName;
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = CommandTimeoutSeconds;
                    bulkCopy.ColumnMappings.Add("user_id", "user_id");
                    bulkCopy.ColumnMappings.Add("org_type_id", "org_type_id");
                    bulkCopy.ColumnMappings.Add("org_value", "org_value");
                    bulkCopy.ColumnMappings.Add("expected_generation", "expected_generation");

                    await bulkCopy.WriteToServerAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                // One transaction around the four statements. They are not independent: the clear
                // happens before the upsert, so a failure between them would leave the batch torn -
                // users whose value was being changed left with none at all. The CSV path is already
                // transactional for the same reason.
                using (var tx = connection.BeginTransaction())
                {
                    using (var cmd = Command(connection, MergeSql, tx))
                    {
                        cmd.Parameters.Add("@expectedSourceKind", SqlDbType.TinyInt).Value =
                            expectedSourceKind.HasValue ? (object)(byte)expectedSourceKind.Value : DBNull.Value;

                        using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                result.Applied = reader.GetInt32(0);
                                result.Cleared = reader.GetInt32(1);
                                result.ValuesCreated = reader.GetInt32(2);
                            }
                        }
                    }

                    tx.Commit();
                }

                using (var cmd = Command(connection, "DROP TABLE " + TempTableName))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }

        public async Task<IReadOnlyList<UserOrgValueForUser>> GetForUserAsync(
            int userId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            const string sql = @"
SELECT t.id, t.name, v.name, a.last_updated_utc
FROM dbo.user_org_assignments a
JOIN dbo.user_org_types t ON t.id = a.org_type_id
JOIN dbo.user_org_values v ON v.id = a.org_value_id
WHERE a.user_id = @userId
ORDER BY t.name;";

            var results = new List<UserOrgValueForUser>();

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, sql))
            {
                cmd.Parameters.Add("@userId", SqlDbType.Int).Value = userId;

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        results.Add(new UserOrgValueForUser
                        {
                            OrgTypeId = reader.GetInt32(0),
                            OrgTypeName = reader.GetString(1),
                            Value = reader.GetString(2),
                            LastUpdatedUtc = reader.GetDateTime(3),
                        });
                    }
                }
            }

            return results;
        }

        public async Task<int> ClearAllForTypeAsync(
            int orgTypeId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, "DELETE FROM dbo.user_org_assignments WHERE org_type_id = @id"))
            {
                cmd.Parameters.Add("@id", SqlDbType.Int).Value = orgTypeId;
                return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Which of these UPNs exist.
        /// </summary>
        /// <remarks>
        /// Sent as a table-valued batch through a temp table rather than an IN clause: a preview can
        /// carry a few hundred UPNs and SQL Server caps a statement at 2,100 parameters, so an IN
        /// clause would fail on exactly the large file this is most useful for.
        /// </remarks>
        public async Task<IReadOnlyCollection<string>> FindExistingUpnsAsync(
            IReadOnlyCollection<string> upns,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await ProbeUpnsAsync(
                "SELECT p.upn FROM #user_org_upn_probe p WHERE EXISTS (SELECT 1 FROM dbo.users u WHERE u.user_name = p.upn);",
                null,
                upns,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyCollection<string>> FindAssignedUpnsAsync(
            int orgTypeId,
            IReadOnlyCollection<string> upns,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return await ProbeUpnsAsync(
                @"SELECT p.upn
                  FROM #user_org_upn_probe p
                  JOIN dbo.users u ON u.user_name = p.upn
                  JOIN dbo.user_org_assignments a ON a.user_id = u.id AND a.org_type_id = @orgTypeId;",
                cmd => cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId,
                upns,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Bulk-copies a set of UPNs into a temp table and runs a caller-supplied query against it.
        /// </summary>
        /// <remarks>
        /// A table-valued batch rather than an IN clause: a full-file preflight carries every UPN in
        /// the upload and SQL Server caps a statement at 2,100 parameters, so an IN clause would fail
        /// on exactly the large file this is most useful for.
        /// </remarks>
        private async Task<IReadOnlyCollection<string>> ProbeUpnsAsync(
            string sql,
            Action<SqlCommand> addParameters,
            IReadOnlyCollection<string> upns,
            CancellationToken cancellationToken)
        {
            var found = new List<string>();
            if (upns == null || upns.Count == 0)
            {
                return found;
            }

            var table = new DataTable();
            table.Columns.Add("upn", typeof(string));
            foreach (var upn in upns.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                table.Rows.Add(upn);
            }

            if (table.Rows.Count == 0)
            {
                return found;
            }

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            {
                using (var cmd = Command(connection, "CREATE TABLE #user_org_upn_probe (upn NVARCHAR(250) NOT NULL);"))
                {
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                using (var bulkCopy = new SqlBulkCopy(connection))
                {
                    bulkCopy.DestinationTableName = "#user_org_upn_probe";
                    bulkCopy.BatchSize = 10000;
                    bulkCopy.BulkCopyTimeout = CommandTimeoutSeconds;
                    bulkCopy.ColumnMappings.Add("upn", "upn");
                    await bulkCopy.WriteToServerAsync(table, cancellationToken).ConfigureAwait(false);
                }

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
                            found.Add(reader.GetString(0));
                        }
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Builds the <see cref="DataTable"/> that is bulk-copied up. Column order and types must match
        /// <see cref="CreateTempTableSql"/>.
        /// </summary>
        internal static DataTable BuildBatchTable(
            IReadOnlyList<UserOrgAssignmentUpdate> updates,
            IReadOnlyDictionary<int, int> expectedGenerations = null)
        {
            var table = new DataTable();
            table.Columns.Add("user_id", typeof(int));
            table.Columns.Add("org_type_id", typeof(int));
            table.Columns.Add("org_value", typeof(string));
            table.Columns.Add("expected_generation", typeof(int));

            foreach (var update in updates)
            {
                // Normalised again here rather than trusted. This is the last point before the value
                // reaches an nvarchar(200) column, and a caller that skipped normalisation would
                // otherwise get a truncation error from SQL Server instead of a stored value.
                var value = UserOrgRules.NormaliseOrgValue(update.OrgValue);

                var generation = 0;
                var haveGeneration = expectedGenerations != null
                    && expectedGenerations.TryGetValue(update.OrgTypeId, out generation);

                table.Rows.Add(
                    update.UserId,
                    update.OrgTypeId,
                    value == null ? (object)DBNull.Value : value,
                    haveGeneration ? (object)generation : DBNull.Value);
            }

            return table;
        }
    }
}
