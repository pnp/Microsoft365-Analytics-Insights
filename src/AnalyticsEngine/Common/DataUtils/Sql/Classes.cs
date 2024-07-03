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
        }

        public string FieldName { get; set; }
        public string ColationOverride { get; set; }
        public string SqlColDefinition { get; set; }
        public bool Nullable { get; set; } = false;
        public DbType SqlType { get; internal set; }
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
    }
}
