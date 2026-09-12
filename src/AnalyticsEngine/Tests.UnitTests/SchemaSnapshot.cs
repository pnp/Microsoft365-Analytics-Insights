using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// A canonical, comparable description of a SQL Server database's schema.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built for the EF Core baseline work (issue #528), where the whole approach rests on one claim:
    /// that a database created by the EF Core V1 baseline is <b>identical</b> to one created by applying
    /// every EF6 migration. If that is not exactly true the baseline is a lie, and every later EF Core
    /// migration is a diff against the wrong starting point.
    /// </para>
    /// <para>
    /// It is equally the guard for the period when both branches are live: while .NET Framework ships EF6
    /// migrations and the .NET 10 branch ships EF Core ones, every schema change has to be made twice.
    /// Comparing the two resulting schemas detects a missed change regardless of whether anyone
    /// remembered to mirror it, which is the only form of enforcement that actually works.
    /// </para>
    /// <para>
    /// The snapshot is a multiset of canonical strings rather than an object graph, because the useful
    /// output is a readable diff. A multiset rather than a set because cardinality is meaningful: two
    /// structurally identical foreign keys are not the same thing as one.
    /// </para>
    /// <para>
    /// What it captures is driven by what this product's migrations actually do, not by completeness for
    /// its own sake. Index options such as <c>IGNORE_DUP_KEY</c>, foreign-key trust state, and view bodies
    /// are all in because migrations in this repository deliberately set them and depend on them. Things
    /// that legitimately differ between two correctly-built databases - object ids, fragmentation,
    /// statistics, fill factor, identity <c>last_value</c> - are deliberately out.
    /// </para>
    /// </remarks>
    internal sealed class SchemaSnapshot
    {
        private readonly List<string> _lines;

        public string Label { get; }

        private SchemaSnapshot(string label, IEnumerable<string> lines)
        {
            Label = label;
            _lines = lines.ToList();
        }

        public IReadOnlyList<string> Lines => _lines;

        public static SchemaSnapshot Capture(string connectionString, string label)
        {
            var lines = new List<string>();

            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();

                lines.AddRange(Query(connection, TablesSql));
                lines.AddRange(Query(connection, ColumnsSql));
                lines.AddRange(Query(connection, IndexesSql));
                lines.AddRange(Query(connection, ForeignKeysSql));
                lines.AddRange(Query(connection, CheckConstraintsSql));
                lines.AddRange(QueryModules(connection));
            }

            return new SchemaSnapshot(label, lines);
        }

        /// <summary>
        /// Differences between the two snapshots, as a multiset comparison so that a duplicated structure
        /// is reported rather than collapsed.
        /// </summary>
        public IReadOnlyList<string> DifferencesFrom(SchemaSnapshot other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            var here = Counts(_lines);
            var there = Counts(other._lines);

            var differences = new List<string>();

            foreach (var line in here.Keys.Union(there.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                here.TryGetValue(line, out var hereCount);
                there.TryGetValue(line, out var thereCount);

                if (hereCount == thereCount) continue;

                if (thereCount == 0) differences.Add($"only in {Label}: {line}");
                else if (hereCount == 0) differences.Add($"only in {other.Label}: {line}");
                else differences.Add($"count differs ({Label}={hereCount}, {other.Label}={thereCount}): {line}");
            }

            return differences;
        }

        private static Dictionary<string, int> Counts(IEnumerable<string> lines)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                counts.TryGetValue(line, out var n);
                counts[line] = n + 1;
            }
            return counts;
        }

        /// <summary>Formats a diff for a test failure message, truncated so the output stays usable.</summary>
        public static string Describe(IReadOnlyList<string> differences, int maxLines = 40)
        {
            if (differences.Count == 0) return "(no differences)";

            var shown = differences.Take(maxLines).ToList();
            var text = new StringBuilder()
                .AppendLine($"{differences.Count} schema difference(s):")
                .AppendLine(string.Join(Environment.NewLine, shown.Select(d => "  " + d)));

            if (differences.Count > shown.Count)
                text.AppendLine($"  ... and {differences.Count - shown.Count} more");

            return text.ToString();
        }

        private static IEnumerable<string> Query(SqlConnection connection, string sql)
        {
            var results = new List<string>();
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 0 })
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) results.Add(reader.GetString(0));
            }
            return results;
        }

        /// <summary>
        /// Views, procedures, functions and triggers. Read as separate columns so the body can be
        /// whitespace-normalised in C# - collapsing runs of whitespace in T-SQL is painful, and CRLF
        /// versus LF alone would otherwise report every module as different.
        /// </summary>
        private static IEnumerable<string> QueryModules(SqlConnection connection)
        {
            var results = new List<string>();

            using (var command = new SqlCommand(ModulesSql, connection) { CommandTimeout = 0 })
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var kind = reader.GetString(0);
                    var name = reader.GetString(1);
                    var ansiNulls = reader.GetBoolean(2);
                    var quotedIdentifier = reader.GetBoolean(3);
                    var body = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                    results.Add(
                        $"MODULE {kind} {name}" +
                        $" ANSI_NULLS({(ansiNulls ? 1 : 0)})" +
                        $" QUOTED_IDENTIFIER({(quotedIdentifier ? 1 : 0)})" +
                        $" BODY({NormaliseSql(body)})");
                }
            }

            return results;
        }

        /// <summary>
        /// Collapses whitespace so that reformatting a view body is not reported as a schema change, while
        /// any real change to its logic still is.
        /// </summary>
        internal static string NormaliseSql(string sql) =>
            string.IsNullOrEmpty(sql) ? string.Empty : Regex.Replace(sql, @"\s+", " ").Trim();

        // EF's own history tables are excluded because the two frameworks deliberately use different ones
        // (__MigrationHistory for EF6, __EFMigrationsHistory for EF Core) and that difference is expected.
        // Qualified by schema so a history table sitting in the wrong schema is NOT silently ignored.
        const string ExcludedTables =
            "NOT (s.name = 'dbo' AND t.name IN ('__MigrationHistory', '__EFMigrationsHistory', 'sysdiagrams'))";

        const string TablesSql = @"
SELECT 'TABLE ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE " + ExcludedTables;

        // Column type is rendered with its real length/precision so that nvarchar(850) and varchar(850)
        // - a distinction this codebase cares about a great deal - never compare equal.
        //
        // IDENTITY carries its seed and increment, and a computed column carries its expression and
        // persisted state: reducing either to a boolean would let genuinely different schemas compare
        // equal. identity last_value is deliberately NOT captured; that is data state, not schema.
        const string ColumnsSql = @"
SELECT 'COLUMN ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + '.' + c.name COLLATE DATABASE_DEFAULT
     + ' ' + ty.name COLLATE DATABASE_DEFAULT
     + CASE
         WHEN ty.name IN ('varchar','char','varbinary','binary')
              THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
         WHEN ty.name IN ('nvarchar','nchar')
              THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
         WHEN ty.name IN ('decimal','numeric')
              THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
         WHEN ty.name IN ('datetime2','time','datetimeoffset')
              THEN '(' + CAST(c.scale AS varchar(10)) + ')'
         ELSE ''
       END
     + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
     + CASE WHEN c.is_identity = 1
            THEN ' IDENTITY(' + ISNULL(CAST(CAST(ic.seed_value AS bigint) AS varchar(40)), '?') + ','
                              + ISNULL(CAST(CAST(ic.increment_value AS bigint) AS varchar(40)), '?') + ')'
            ELSE '' END
     + CASE WHEN c.is_computed = 1
            THEN ' COMPUTED(' + ISNULL(cmp.definition COLLATE DATABASE_DEFAULT, '?') + ')'
                 + CASE WHEN cmp.is_persisted = 1 THEN ' PERSISTED' ELSE '' END
            ELSE '' END
     + CASE WHEN c.is_rowguidcol = 1 THEN ' ROWGUIDCOL' ELSE '' END
     + CASE WHEN c.is_sparse = 1 THEN ' SPARSE' ELSE '' END
     + ISNULL(' COLLATE ' + c.collation_name COLLATE DATABASE_DEFAULT, '')
     + ISNULL(' DEFAULT ' + dc.definition COLLATE DATABASE_DEFAULT, '')
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cmp ON cmp.object_id = c.object_id AND cmp.column_id = c.column_id
WHERE " + ExcludedTables;

        // Key column ORDER matters for an index and is a common source of silent difference, so the key
        // list is ordered rather than sorted. INCLUDE columns are unordered by definition, so they are.
        //
        // IGNORE_DUP_KEY is captured because migrations in this repository depend on it: the unique index
        // on urls.full_url sets it deliberately so that one racing duplicate cannot abort an entire insert
        // batch, and uses ignore_dup_key = 1 as part of its target-state check. Two indexes differing only
        // in that option are NOT the same index.
        const string IndexesSql = @"
SELECT 'INDEX ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + '.' + i.name COLLATE DATABASE_DEFAULT
     + CASE WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END
     + CASE WHEN i.is_primary_key = 1 THEN ' PRIMARY_KEY' ELSE '' END
     + CASE WHEN i.is_unique_constraint = 1 THEN ' UNIQUE_CONSTRAINT' ELSE '' END
     + ' TYPE(' + i.type_desc COLLATE DATABASE_DEFAULT + ')'
     + ' IGNORE_DUP_KEY(' + CAST(i.ignore_dup_key AS varchar(1)) + ')'
     + ' DISABLED(' + CAST(i.is_disabled AS varchar(1)) + ')'
     + ' KEYS(' + ISNULL(STUFF((
         SELECT ',' + kc.name COLLATE DATABASE_DEFAULT + CASE WHEN kic.is_descending_key = 1 THEN ' DESC' ELSE '' END
         FROM sys.index_columns kic
         JOIN sys.columns kc ON kc.object_id = kic.object_id AND kc.column_id = kic.column_id
         WHERE kic.object_id = i.object_id AND kic.index_id = i.index_id AND kic.is_included_column = 0
         ORDER BY kic.key_ordinal
         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''), '') + ')'
     + ' INCLUDE(' + ISNULL(STUFF((
         SELECT ',' + ic2.name COLLATE DATABASE_DEFAULT
         FROM sys.index_columns iic
         JOIN sys.columns ic2 ON ic2.object_id = iic.object_id AND ic2.column_id = iic.column_id
         WHERE iic.object_id = i.object_id AND iic.index_id = i.index_id AND iic.is_included_column = 1
         ORDER BY ic2.name
         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''), '') + ')'
     + CASE WHEN i.has_filter = 1 THEN ' FILTER(' + i.filter_definition COLLATE DATABASE_DEFAULT + ')' ELSE '' END
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE i.name IS NOT NULL AND " + ExcludedTables;

        // Constraint NAMES are excluded: EF6 and EF Core generate different auto-names for the same
        // relationship, and a name difference is not a schema difference that affects the product.
        // Cardinality is preserved by the multiset comparison, so two structurally identical foreign keys
        // are still distinguishable from one.
        //
        // Trust and enabled state ARE captured. A migration in this repository creates a foreign key WITH
        // NOCHECK and then runs the expensive WITH CHECK CHECK CONSTRAINT specifically to make it trusted;
        // without these flags the intermediate state would certify as identical to the finished one, even
        // though an untrusted constraint is unusable by the optimiser and may be hiding invalid rows.
        const string ForeignKeysSql = @"
SELECT 'FOREIGNKEY ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT
     + '(' + ISNULL(STUFF((
         SELECT ',' + pc.name COLLATE DATABASE_DEFAULT
         FROM sys.foreign_key_columns fkc2
         JOIN sys.columns pc ON pc.object_id = fkc2.parent_object_id AND pc.column_id = fkc2.parent_column_id
         WHERE fkc2.constraint_object_id = fk.object_id
         ORDER BY fkc2.constraint_column_id
         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''), '') + ')'
     + ' -> ' + rs.name COLLATE DATABASE_DEFAULT + '.' + rt.name COLLATE DATABASE_DEFAULT
     + '(' + ISNULL(STUFF((
         SELECT ',' + rc.name COLLATE DATABASE_DEFAULT
         FROM sys.foreign_key_columns fkc3
         JOIN sys.columns rc ON rc.object_id = fkc3.referenced_object_id AND rc.column_id = fkc3.referenced_column_id
         WHERE fkc3.constraint_object_id = fk.object_id
         ORDER BY fkc3.constraint_column_id
         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''), '') + ')'
     + ' ONDELETE(' + fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT + ')'
     + ' ONUPDATE(' + fk.update_referential_action_desc COLLATE DATABASE_DEFAULT + ')'
     + ' DISABLED(' + CAST(fk.is_disabled AS varchar(1)) + ')'
     + ' NOTTRUSTED(' + CAST(fk.is_not_trusted AS varchar(1)) + ')'
     + ' NOTFORREPLICATION(' + CAST(fk.is_not_for_replication AS varchar(1)) + ')'
FROM sys.foreign_keys fk
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
WHERE " + ExcludedTables;

        const string CheckConstraintsSql = @"
SELECT 'CHECK ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT
     + ' ' + cc.definition COLLATE DATABASE_DEFAULT
     + ' DISABLED(' + CAST(cc.is_disabled AS varchar(1)) + ')'
     + ' NOTTRUSTED(' + CAST(cc.is_not_trusted AS varchar(1)) + ')'
     + ' NOTFORREPLICATION(' + CAST(cc.is_not_for_replication AS varchar(1)) + ')'
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id = cc.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE " + ExcludedTables;

        // Views, procedures, functions and triggers are part of the migration chain in this repository -
        // migrations create the reporting views and later rewrite them, and existing tests treat
        // uses_quoted_identifier as part of a view's required state. A database with a missing or stale
        // view must not compare equal to one with the right view.
        const string ModulesSql = @"
SELECT o.type_desc COLLATE DATABASE_DEFAULT AS Kind,
       s.name COLLATE DATABASE_DEFAULT + '.' + o.name COLLATE DATABASE_DEFAULT AS FullName,
       m.uses_ansi_nulls AS UsesAnsiNulls,
       m.uses_quoted_identifier AS UsesQuotedIdentifier,
       m.definition AS Body
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0";
    }
}
