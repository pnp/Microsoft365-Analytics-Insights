using System;
using System.Data;

namespace DataUtils.Sql
{
    public class ColumnSqlInfo
    {
        public ColumnSqlInfo(ColumnAttribute attribute)
        {
            ColationOverride = attribute.ColationOverride;
            FieldName = attribute.FieldName;
            SqlTypeOverride = attribute.SqlTypeOverride;
        }

        public string FieldName { get; set; }
        public string ColationOverride { get; set; }
        public string SqlColDefinition { get; set; }
        public bool Nullable { get; set; } = false;
        public DbType SqlType { get; internal set; }

        /// <summary>
        /// Declared maximum length, in characters, parsed once from a bounded string column
        /// definition such as <c>nvarchar(850)</c>. Null for unbounded (<c>nvarchar(max)</c>) or
        /// non-length-bounded types (int, datetime2, ...). Lets <see cref="Inserts.InsertBatch{T}"/>
        /// skip an over-width row in memory before the INSERT, instead of catching a per-row SQL
        /// truncation error (8152/2628) on every doomed row.
        /// </summary>
        public int? MaxLength { get; set; }

        /// <summary>
        /// Optional explicit SQL column type (e.g. "nvarchar(850)") that overrides the default
        /// <c>[nvarchar] (max)</c> emitted for <see cref="string"/> properties by
        /// <see cref="Inserts.InsertBatchTypeFieldCache{T}"/>. Set via <see cref="ColumnAttribute.SqlTypeOverride"/>
        /// on properties whose staging-column type must match an indexed target column to avoid
        /// implicit conversion (which defeats indexes on the join target — see issue #122 / #108 / #109
        /// for <c>urls.full_url</c>).
        /// </summary>
        public string SqlTypeOverride { get; set; }
    }
    public class ColumnAttribute : Attribute
    {
        const bool DEFAULT_NULLABLE = false;
        public ColumnAttribute(string name) : this(name, DEFAULT_NULLABLE)
        {
        }
        public ColumnAttribute(string name, bool nullable)
        {
            FieldName = name;
            Nullable = nullable;
        }
        public string FieldName { get; set; } = string.Empty;
        public bool Nullable { get; set; } = DEFAULT_NULLABLE;
        public string ColationOverride { get; set; }

        /// <summary>
        /// Optional explicit SQL column type definition for the generated staging column
        /// (e.g. <c>"nvarchar(850)"</c>). When set, overrides the type
        /// <see cref="Inserts.InsertBatchTypeFieldCache{T}"/> would otherwise infer from the
        /// property's CLR type. Only meaningful for <see cref="string"/> properties today; the
        /// generator still emits <c>nvarchar(max)</c> as the default when this is null/empty.
        /// </summary>
        public string SqlTypeOverride { get; set; }

        public bool IsValid => !string.IsNullOrEmpty(FieldName);

    }
    public class TempTableNameAttribute : Attribute
    {
        public TempTableNameAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; set; } = string.Empty;

        public bool IsValid => !string.IsNullOrEmpty(Name);
    }

    public class BatchSaveException : Exception
    {
        public BatchSaveException(string message) : base(message)
        {
        }

        // Keep the originating SqlException so callers (e.g. TransientSqlRetry) can tell a dropped-connection
        // fault apart from a constraint violation and decide whether to retry.
        public BatchSaveException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
