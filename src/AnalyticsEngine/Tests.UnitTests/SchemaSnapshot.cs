using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;

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
    /// The snapshot is a sorted set of canonical strings rather than an object graph, because the useful
    /// output is a readable diff, not a tree. Every line is self-describing, so a failure message can be
    /// pasted straight into a bug report.
    /// </para>
    /// <para>
    /// Deliberately captures only what the product depends on being right: tables, column definitions
    /// (including nullability and collation), indexes with their key order and INCLUDE lists, foreign
    /// keys, defaults, and check constraints. It ignores things that legitimately differ between two
    /// correctly-built databases, such as object ids, index fragmentation, statistics and fill factor.
    /// </para>
    /// </remarks>
    internal sealed class SchemaSnapshot
    {
        private readonly SortedSet<string> _lines;

        public string Label { get; }

        private SchemaSnapshot(string label, IEnumerable<string> lines)
        {
            Label = label;
            _lines = new SortedSet<string>(lines, StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> Lines => _lines;

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
            }

            return new SchemaSnapshot(label, lines);
        }

        /// <summary>
        /// Lines present in this snapshot but not the other, and vice versa, in a form meant to be read
        /// by a human in a test failure.
        /// </summary>
        public IReadOnlyList<string> DifferencesFrom(SchemaSnapshot other)
        {
            if (other == null) throw new ArgumentNullException(nameof(other));

            var onlyHere = _lines.Except(other._lines, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);
            var onlyThere = other._lines.Except(_lines, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal);

            return onlyHere.Select(l => $"only in {Label}: {l}")
                .Concat(onlyThere.Select(l => $"only in {other.Label}: {l}"))
                .ToList();
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

        // Only user tables; EF's own history tables are excluded because the two branches deliberately
        // use different ones (__MigrationHistory for EF6, __EFMigrationsHistory for EF Core) and that
        // difference is expected rather than a defect.
        const string ExcludedTables =
            "t.name NOT IN ('__MigrationHistory', '__EFMigrationsHistory', 'sysdiagrams')";

        const string TablesSql = @"
SELECT 'TABLE ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE " + ExcludedTables;

        // Column type is rendered with its real length/precision so that nvarchar(850) and varchar(850)
        // - a distinction this codebase cares about a great deal - never compare equal.
        const string ColumnsSql = @"
SELECT 'COLUMN ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + '.' + c.name COLLATE DATABASE_DEFAULT
     + ' ' + ty.name COLLATE DATABASE_DEFAULT
     + CASE
         WHEN ty.name COLLATE DATABASE_DEFAULT IN ('varchar','char','varbinary','binary')
              THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length AS varchar(10)) END + ')'
         WHEN ty.name COLLATE DATABASE_DEFAULT IN ('nvarchar','nchar')
              THEN '(' + CASE WHEN c.max_length = -1 THEN 'max' ELSE CAST(c.max_length / 2 AS varchar(10)) END + ')'
         WHEN ty.name COLLATE DATABASE_DEFAULT IN ('decimal','numeric')
              THEN '(' + CAST(c.precision AS varchar(10)) + ',' + CAST(c.scale AS varchar(10)) + ')'
         WHEN ty.name COLLATE DATABASE_DEFAULT IN ('datetime2','time','datetimeoffset')
              THEN '(' + CAST(c.scale AS varchar(10)) + ')'
         ELSE ''
       END
     + CASE WHEN c.is_nullable = 1 THEN ' NULL' ELSE ' NOT NULL' END
     + CASE WHEN c.is_identity = 1 THEN ' IDENTITY' ELSE '' END
     + CASE WHEN c.is_computed = 1 THEN ' COMPUTED' ELSE '' END
     + ISNULL(' COLLATE ' + c.collation_name, '')
     + ISNULL(' DEFAULT ' + dc.definition COLLATE DATABASE_DEFAULT, '')
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
WHERE " + ExcludedTables;

        // Key column ORDER matters for an index and is a common source of silent difference, so the key
        // list is ordered rather than sorted. INCLUDE columns are unordered by definition, so they are.
        const string IndexesSql = @"
SELECT 'INDEX ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + '.' + i.name COLLATE DATABASE_DEFAULT
     + CASE WHEN i.is_unique = 1 THEN ' UNIQUE' ELSE '' END
     + CASE WHEN i.is_primary_key = 1 THEN ' PRIMARY_KEY' ELSE '' END
     + CASE WHEN i.is_unique_constraint = 1 THEN ' UNIQUE_CONSTRAINT' ELSE '' END
     + ' TYPE(' + i.type_desc COLLATE DATABASE_DEFAULT + ')'
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
         ORDER BY ic2.name COLLATE DATABASE_DEFAULT
         FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''), '') + ')'
     + CASE WHEN i.has_filter = 1 THEN ' FILTER(' + i.filter_definition COLLATE DATABASE_DEFAULT + ')' ELSE '' END
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE i.name COLLATE DATABASE_DEFAULT IS NOT NULL AND " + ExcludedTables;

        // Constraint NAMES are deliberately excluded: EF6 and EF Core generate different auto-names for
        // the same relationship, and a name difference is not a schema difference that affects the
        // product. What matters is which columns reference which.
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
FROM sys.foreign_keys fk
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
WHERE " + ExcludedTables;

        const string CheckConstraintsSql = @"
SELECT 'CHECK ' + s.name COLLATE DATABASE_DEFAULT + '.' + t.name COLLATE DATABASE_DEFAULT + ' ' + cc.definition COLLATE DATABASE_DEFAULT
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id = cc.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE " + ExcludedTables;
    }
}
