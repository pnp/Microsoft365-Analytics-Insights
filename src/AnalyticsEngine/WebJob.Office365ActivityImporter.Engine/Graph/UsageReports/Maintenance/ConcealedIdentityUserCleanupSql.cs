using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Maintenance
{
    /// <summary>A column that holds a <c>dbo.users</c> id, and whether an index leads with it (so a lookup by it can seek).</summary>
    internal sealed class UserReferenceColumn
    {
        public UserReferenceColumn(string schema, string table, string column, bool hasLeadingIndex)
        {
            Schema = schema;
            Table = table;
            Column = column;
            HasLeadingIndex = hasLeadingIndex;
        }

        public string Schema { get; }
        public string Table { get; }
        public string Column { get; }
        public bool HasLeadingIndex { get; }

        public override string ToString() => $"{Schema}.{Table}.{Column}";
    }

    /// <summary>The T-SQL of <see cref="ConcealedIdentityUserCleanup"/>, built here so its shape can be tested without SQL Server.</summary>
    internal static class ConcealedIdentityUserCleanupSql
    {
        /// <summary>
        /// The tables the user-keyed daily usage-report loaders (<see cref="AbstractUserDailyActivityLoader{TReportDbType, TUserActivityUserDetail}"/>)
        /// write, all keyed on <c>user_id</c>: the only rows a placeholder can legitimately have, and the only ones deleted
        /// with it. A test keeps this list in step with the loaders. The Copilot usage-report tables are deliberately not
        /// here: a reference from them keeps the user, like any other.
        /// </summary>
        internal static readonly IReadOnlyList<string> UsageReportActivityTables = new[]
        {
            "teams_user_activity_log",
            "teams_user_device_usage_log",
            "outlook_user_activity_log",
            "onedrive_usage_activity_log",
            "onedrive_user_activity_log",
            "sharepoint_user_activity_log",
            "yammer_user_activity_log",
            "yammer_device_activity_log",
            "platform_user_activity_log",
        };

        /// <summary>
        /// Columns known to hold a user id without a foreign key in a database built by the migrations. Each one keeps the
        /// user, exactly as a foreign key would. Ignored where the table or column does not exist.
        /// </summary>
        internal static readonly IReadOnlyList<KeyValuePair<string, string>> KnownUserIdColumnsWithoutForeignKey = new[]
        {
            new KeyValuePair<string, string>("audit_events", "user_id"),
            new KeyValuePair<string, string>("copilot_chats", "user_id"),
            new KeyValuePair<string, string>("user_license_history", "user_id"),
            new KeyValuePair<string, string>("user_org_import_changes", "user_id"),
        };

        /// <summary>Most user ids one batch's <c>VALUES</c> list carries: SQL Server's limit for one table value constructor.</summary>
        internal const int MaxBatchSize = 1000;

        /// <summary>
        /// References from tables with no index leading with their user column that are looked for ONCE per run - one plain
        /// read of each table, before the first batch, whose hits are kept - instead of under a shared table lock in every
        /// batch. Scanning <c>copilot_chats</c>, which comes from the audit log and is not touched by concealment, can take
        /// seconds on a large tenant, and holding it locked for every batch would block the importers for the whole run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A reference added to one of these after the read and before its user's batch would not be seen. That is safe only
        /// because no writer of these tables can attach a placeholder, or because a late reference fails instead of being
        /// lost; each entry says which:
        /// </para>
        /// <list type="bullet">
        /// <item><c>copilot_chats.user_id</c>: a copy of the parent audit event's <c>user_id</c>
        /// (<c>common_upsert_copilot_agents.sql</c> and the repair both copy it from <c>audit_events</c>), so no chat can name
        /// a user its audit event does not - and <c>audit_events.user_id</c> is indexed, so it is checked under lock in every
        /// batch.</item>
        /// <item><c>copilot_usage_user_activity_log.user_id</c>: written only by the Copilot per-user usage report, which skips
        /// concealed rows and resolves the rest by a real UPN with an <c>@</c> on a known domain
        /// (<c>CopilotUsageReportPolicy</c>), never a placeholder's name. Its foreign key cascades, so this rests on that.</item>
        /// <item><c>cowork_usage_user_activity_log.user_id</c>: nothing writes it any more (the report it held was retired).</item>
        /// <item><c>event_meta_power_app_share.shared_with_user_id</c> and <c>event_meta_power_automate_flow_share.shared_with_user_id</c>:
        /// written by the Power Platform audit import, earlier in the same cycle, by recipient name. Their foreign keys are
        /// <c>NO ACTION</c>: a late reference makes the batch's <c>DELETE FROM dbo.users</c> fail (error 547) and the whole
        /// batch roll back, and the next run's read sees it.</item>
        /// <item><c>user_license_type_lookups.user_id</c>: written only by the user import and the one-person refresh, from
        /// Graph users, and both only under the users-and-licences lock - which the cleanup holds from this read to its last
        /// batch. Its foreign key cascades.</item>
        /// </list>
        /// <para>
        /// Any other reference without an index - <c>user_org_import_changes.user_id</c> today, whose writer resolves users by
        /// the sign-in name an administrator typed into an organisation CSV, which could be a placeholder's - keeps its locked
        /// check in every batch, as does any table a later release adds, until someone has made the same check for it.
        /// </para>
        /// </remarks>
        internal static readonly IReadOnlyList<KeyValuePair<string, string>> ReferencesCheckedOncePerRun = new[]
        {
            new KeyValuePair<string, string>("copilot_chats", "user_id"),
            new KeyValuePair<string, string>("copilot_usage_user_activity_log", "user_id"),
            new KeyValuePair<string, string>("cowork_usage_user_activity_log", "user_id"),
            new KeyValuePair<string, string>("event_meta_power_app_share", "shared_with_user_id"),
            new KeyValuePair<string, string>("event_meta_power_automate_flow_share", "shared_with_user_id"),
            new KeyValuePair<string, string>("user_license_type_lookups", "user_id"),
        };

        /// <summary>
        /// True for a reference that is read once per run (see <see cref="ReferencesCheckedOncePerRun"/>). One that has an
        /// index leading with its column is always checked in every batch instead: there the check is a seek.
        /// </summary>
        internal static bool IsCheckedOncePerRun(UserReferenceColumn reference) =>
            !reference.HasLeadingIndex
            && string.Equals(reference.Schema, "dbo", StringComparison.OrdinalIgnoreCase)
            && ReferencesCheckedOncePerRun.Any(r => string.Equals(r.Key, reference.Table, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Value, reference.Column, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// One result set per reference: the users the rule matches that it refers to. One scan of each table, hash-joined with
        /// the users the rule matches; plain read committed, so no lock outlasts the read.
        /// </summary>
        internal static string BuildReferencedCandidatesSql(IReadOnlyList<UserReferenceColumn> references)
        {
            var sql = new StringBuilder("SET NOCOUNT ON;").AppendLine();
            foreach (var reference in references)
            {
                var column = Quote(reference.Column);
                sql.AppendLine($@"SELECT DISTINCT r.{column} FROM {Quote(reference.Schema)}.{Quote(reference.Table)} AS r
WHERE r.{column} IN (SELECT u.id FROM dbo.users AS u WHERE {ConcealedIdentityPlaceholderRule.SqlUserNameMatches} AND u.id > @minUserId)
OPTION (HASH JOIN);");
            }
            return sql.ToString();
        }

        private const string HasLeadingIndexSql = @"CAST(CASE WHEN EXISTS (
            SELECT 1 FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.object_id = c.object_id AND ic.column_id = c.column_id AND ic.key_ordinal = 1
              AND i.type IN (1, 2) AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0)
        THEN 1 ELSE 0 END AS bit)";

        /// <summary>
        /// Two result sets: every column that refers to a user - each foreign key to <c>dbo.users</c>, wherever it is, plus
        /// <see cref="KnownUserIdColumnsWithoutForeignKey"/> - and which of <see cref="UsageReportActivityTables"/> exist.
        /// Read from the catalog on every run rather than listed here, so a table added by a later release that refers to
        /// users keeps them without anyone remembering to add it.
        /// </summary>
        internal static string BuildReferenceDiscoverySql()
        {
            var known = string.Join(", ", KnownUserIdColumnsWithoutForeignKey.Select(k => $"(N'{k.Key}', N'{k.Value}')"));
            var usage = string.Join(", ", UsageReportActivityTables.Select(t => $"(N'{t}')"));

            return $@"SET NOCOUNT ON;
SELECT s.name AS schema_name, t.name AS table_name, c.name AS column_name, {HasLeadingIndexSql} AS has_leading_index
FROM sys.foreign_keys AS fk
INNER JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns AS c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
INNER JOIN sys.tables AS t ON t.object_id = c.object_id
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE fk.referenced_object_id = OBJECT_ID(N'dbo.users')
UNION
SELECT s.name, t.name, c.name, {HasLeadingIndexSql}
FROM (VALUES {known}) AS k(table_name, column_name)
INNER JOIN sys.tables AS t ON t.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(k.table_name), N'U')
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.columns AS c ON c.object_id = t.object_id AND c.name = k.column_name;

SELECT k.table_name
FROM (VALUES {usage}) AS k(table_name)
WHERE OBJECT_ID(N'dbo.' + QUOTENAME(k.table_name), N'U') IS NOT NULL
  AND COL_LENGTH(N'dbo.' + QUOTENAME(k.table_name), N'user_id') IS NOT NULL;";
        }

        /// <summary>
        /// Whether there is anything to look at: one scan of <c>IX_users</c> at most, stopping at the first match. Runs before
        /// the writer lock is taken, so an idle database costs this and nothing else.
        /// </summary>
        internal static string BuildAnyCandidateSql() =>
            $@"SET NOCOUNT ON;
SELECT TOP (1) u.id FROM dbo.users AS u
WHERE {ConcealedIdentityPlaceholderRule.SqlUserNameMatches} AND u.id > @minUserId;";

        /// <summary>
        /// Every user the rule could delete, in id order, with the name the caller checks again with
        /// <see cref="ConcealedIdentityPlaceholderRule.IsPlaceholderUserName"/>. A first pass only: each batch re-checks
        /// everything under its locks.
        /// </summary>
        internal static string BuildCandidatesSql() =>
            $@"SET NOCOUNT ON;
SELECT u.id, u.user_name FROM dbo.users AS u
WHERE {ConcealedIdentityPlaceholderRule.SqlUserNameMatches}
  AND {ConcealedIdentityPlaceholderRule.SqlNeverSeenByUserImport}
  AND u.id > @minUserId
ORDER BY u.id;";

        /// <summary>
        /// One all-or-nothing batch: re-checks the rule on each candidate's row under an update lock, drops every candidate
        /// that <paramref name="keepReferences"/> refer to, deletes the rest's usage-report rows, then the users. The caller
        /// passes every reference except those read once per run (<see cref="ReferencesCheckedOncePerRun"/>), whose hits it has
        /// already removed from the candidates. Returns one row: <c>candidates</c>, <c>users_deleted</c>, then the rows deleted
        /// from each activity table, in the order of <paramref name="activityTables"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Safe against anything that writes a reference concurrently. The user rows are update-locked first. Each
        /// reference check is serializable (<c>HOLDLOCK</c>) and holds until the commit: a seek into an index leading with
        /// the column locks just the key ranges of the candidates; a table without one is read under a shared table lock
        /// (<c>TABLOCK</c>), rather than collecting a range lock per row. So a reference written while the batch runs
        /// either commits before the check, which then sees it and keeps the user, or waits until the batch has committed -
        /// and then fails the foreign key (error 547) instead of being deleted by an <c>ON DELETE CASCADE</c>. The columns
        /// without a foreign key (<see cref="KnownUserIdColumnsWithoutForeignKey"/>) have no such backstop: a reference
        /// resolved before a batch and written after it would be orphaned. No current writer resolves a placeholder - its
        /// name is a hash, not a sign-in name - and the ones in this process, the audit and user imports, never run
        /// alongside a batch.
        /// </para>
        /// <para>
        /// The reference checks are separate statements with a join hint each: a nested-loop seek per candidate where the
        /// column is indexed, and one hash join - one scan - where it is not, instead of whatever a combined anti-join would
        /// pick for a table with millions of rows. The <c>DELETE FROM dbo.users</c> still costs SQL Server one scan of each
        /// table whose foreign key to <c>dbo.users</c> has no index on its column, per statement: larger batches spread it.
        /// </para>
        /// <para>
        /// The activity deletes may escalate to a table lock on a usage-report table for the rest of the batch. Those tables'
        /// only writer, the usage-report import, never runs at the same time (see <see cref="ConcealedIdentityUserCleanup"/>),
        /// and on a database with read-committed snapshot - Azure SQL's default - readers are not blocked either.
        /// </para>
        /// </remarks>
        internal static string BuildBatchSql(IReadOnlyList<int> candidateIds, IReadOnlyList<string> activityTables,
            IReadOnlyList<UserReferenceColumn> keepReferences, int lockTimeoutMilliseconds)
        {
            if (candidateIds == null || candidateIds.Count == 0) throw new ArgumentException("A batch needs at least one candidate.", nameof(candidateIds));
            if (candidateIds.Count > MaxBatchSize) throw new ArgumentException($"A batch takes at most {MaxBatchSize} candidates.", nameof(candidateIds));
            if (activityTables == null) throw new ArgumentNullException(nameof(activityTables));
            if (keepReferences == null) throw new ArgumentNullException(nameof(keepReferences));

            var sql = new StringBuilder();
            sql.AppendLine("SET NOCOUNT ON;");
            sql.AppendLine("SET XACT_ABORT ON;");
            sql.AppendLine("SET DEADLOCK_PRIORITY LOW;");
            sql.AppendLine($"SET LOCK_TIMEOUT {lockTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
            sql.AppendLine("CREATE TABLE #candidates (id int NOT NULL PRIMARY KEY);");
            sql.AppendLine("CREATE TABLE #doomed (id int NOT NULL PRIMARY KEY);");

            // Integers formatted here, never text from the database, so inlining them is safe.
            sql.Append("INSERT INTO #candidates (id) VALUES ");
            sql.Append(string.Join(",", candidateIds.Select(id => "(" + id.ToString(CultureInfo.InvariantCulture) + ")")));
            sql.AppendLine(";");

            sql.Append("DECLARE @users int = 0");
            for (var i = 0; i < activityTables.Count; i++)
            {
                sql.Append($", @a{i} int = 0");
            }
            sql.AppendLine(";");

            sql.AppendLine("BEGIN TRY");
            sql.AppendLine("BEGIN TRANSACTION;");
            sql.AppendLine($@"INSERT INTO #doomed (id)
SELECT u.id FROM #candidates AS c
INNER JOIN dbo.users AS u WITH (UPDLOCK, ROWLOCK) ON u.id = c.id
WHERE {ConcealedIdentityPlaceholderRule.SqlUserNameMatches}
  AND {ConcealedIdentityPlaceholderRule.SqlNeverSeenByUserImport}
  AND u.id > @minUserId
OPTION (LOOP JOIN);");

            foreach (var reference in keepReferences)
            {
                var target = $"{Quote(reference.Schema)}.{Quote(reference.Table)}";
                var column = Quote(reference.Column);
                sql.AppendLine(reference.HasLeadingIndex
                    ? $"DELETE d FROM #doomed AS d WHERE EXISTS (SELECT 1 FROM {target} AS r WITH (HOLDLOCK) WHERE r.{column} = d.id) OPTION (LOOP JOIN);"
                    : $"DELETE d FROM #doomed AS d WHERE EXISTS (SELECT 1 FROM {target} AS r WITH (TABLOCK, HOLDLOCK) WHERE r.{column} = d.id) OPTION (HASH JOIN);");
            }

            for (var i = 0; i < activityTables.Count; i++)
            {
                sql.AppendLine($"DELETE a FROM dbo.{Quote(activityTables[i])} AS a WHERE a.[user_id] IN (SELECT d.id FROM #doomed AS d);");
                sql.AppendLine($"SET @a{i} = @@ROWCOUNT;");
            }

            sql.AppendLine("DELETE u FROM dbo.users AS u WHERE u.id IN (SELECT d.id FROM #doomed AS d);");
            sql.AppendLine("SET @users = @@ROWCOUNT;");
            sql.AppendLine("COMMIT TRANSACTION;");
            sql.AppendLine("END TRY");
            // XACT_ABORT already rolls back on any error; this makes it explicit, and re-raises the original error.
            sql.AppendLine("BEGIN CATCH");
            sql.AppendLine("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;");
            sql.AppendLine("THROW;");
            sql.AppendLine("END CATCH;");

            sql.Append("SELECT (SELECT COUNT(*) FROM #candidates) AS candidates, @users AS users_deleted");
            for (var i = 0; i < activityTables.Count; i++)
            {
                sql.Append($", @a{i} AS {Quote(activityTables[i])}");
            }
            sql.AppendLine(";");
            return sql.ToString();
        }

        /// <summary>
        /// Splits what refers to users into the usage-report tables a placeholder's rows are deleted from, and everything
        /// else, any of which keeps the user. Duplicates (a known column that also has a foreign key) are dropped.
        /// </summary>
        internal static IReadOnlyList<UserReferenceColumn> KeepReferences(IEnumerable<UserReferenceColumn> references)
        {
            var usageTables = new HashSet<string>(UsageReportActivityTables, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var keep = new List<UserReferenceColumn>();
            foreach (var reference in references)
            {
                var isUsageReportRow = string.Equals(reference.Schema, "dbo", StringComparison.OrdinalIgnoreCase)
                    && usageTables.Contains(reference.Table)
                    && string.Equals(reference.Column, "user_id", StringComparison.OrdinalIgnoreCase);
                if (isUsageReportRow || !seen.Add(reference.ToString())) continue;
                keep.Add(reference);
            }
            return keep;
        }

        private static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";
    }
}
