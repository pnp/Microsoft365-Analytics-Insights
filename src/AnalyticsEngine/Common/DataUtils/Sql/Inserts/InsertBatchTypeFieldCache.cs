using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DataUtils.Sql.Inserts
{
    /// <summary>
    /// Build SQL defintion from a InsertBatchType class
    /// </summary>
    public class InsertBatchTypeFieldCache<T>
    {
        private List<InsertBatchPropertyMapping> _fieldInfoPropertyInfoCache = null;

        static List<Type> _validTempColumnTypes = new List<Type>()
        {
            typeof(string), typeof(DateTime), typeof(DateTime?), typeof(int), typeof(float), typeof(double), typeof(bool), typeof(Guid), typeof(int?),
            typeof(int?), typeof(double?), typeof(bool?)
        };

        public List<InsertBatchPropertyMapping> PropertyMappingInfo
        {
            get
            {
                // Extract/validate schema
                var typeParameterType = typeof(T);

                if (_fieldInfoPropertyInfoCache == null)
                {
                    _fieldInfoPropertyInfoCache = new List<InsertBatchPropertyMapping>();
                    foreach (var property in typeParameterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var attribute = property.GetCustomAttributes(false).OfType<ColumnAttribute>().FirstOrDefault();
                        if (attribute != null && attribute.IsValid)
                        {
                            // Verify valid types
                            if (!_validTempColumnTypes.Contains(property.PropertyType))
                            {
                                const string SEP = ", ";
                                var typesString = string.Empty;
                                foreach (var t in _validTempColumnTypes)
                                {
                                    typesString += t.Name + SEP;
                                }
                                typesString = typesString.TrimEnd(SEP.ToCharArray());
                                throw new BatchSaveException($"Only the following types for properties are supported: {typesString}");
                            }

                            var fieldInfo = new ColumnSqlInfo(attribute);

                            // Override type definition
                            var (fileColName, propTypeIsNullable) = GetSqlFieldTypeDefAndNullability(property.PropertyType);

                            // Per-property SQL type override (e.g. "nvarchar(850)" on a staging join
                            // column that must match an indexed target column to avoid implicit
                            // conversion). Only honoured for string properties — for other CLR types
                            // the inferred type is authoritative (int, datetime2, etc.).
                            if (property.PropertyType == typeof(string) && !string.IsNullOrEmpty(attribute.SqlTypeOverride))
                            {
                                fieldInfo.SqlColDefinition = attribute.SqlTypeOverride;
                            }
                            else
                            {
                                fieldInfo.SqlColDefinition = fileColName;
                            }
                            fieldInfo.Nullable = attribute.Nullable ? true : propTypeIsNullable;
                            fieldInfo.SqlType = SqlHelper.GetDbType(property.PropertyType);

                            // Parse the bounded length once here (not per row) so the hot insert
                            // loop can skip an over-width value with a single integer comparison
                            // instead of a per-row SQL truncation error. Only meaningful for string
                            // columns; inferred numeric/date types have no character length to enforce.
                            if (property.PropertyType == typeof(string))
                            {
                                fieldInfo.MaxLength = ParseDeclaredLength(fieldInfo.SqlColDefinition);
                            }

                            _fieldInfoPropertyInfoCache.Add(new InsertBatchPropertyMapping { Property = property, SqlInfo = fieldInfo });
                        }
                    }
                }

                return _fieldInfoPropertyInfoCache;
            }
        }
        private (string, bool) GetSqlFieldTypeDefAndNullability(Type propertyType)
        {
            if (propertyType == typeof(DateTime))
            {
                return ("datetime2", false);
            }
            else if (propertyType == typeof(DateTime?))
            {
                return ("datetime2", true);
            }
            else if (propertyType == typeof(int))
            {
                return ("int", false);
            }
            else if (propertyType == typeof(int?))
            {
                return ("int", true);
            }
            else if (propertyType == typeof(double?))
            {
                return ("float", true);
            }
            else if (propertyType == typeof(float))
            {
                return ("float", false);
            }
            else if (propertyType == typeof(double))
            {
                return ("float", false);
            }
            else if (propertyType == typeof(bool))
            {
                return ("bit", false);
            }
            else if (propertyType == typeof(bool?))
            {
                return ("bit", true);
            }
            else if (propertyType == typeof(Guid))
            {
                return ("uniqueidentifier", false);
            }
            return ("[nvarchar] (max)", false);
        }

        // Parses the declared length from a column definition such as "nvarchar(850)" -> 850.
        // Returns null for unbounded ("nvarchar(max)") or definitions without an explicit length.
        private static int? ParseDeclaredLength(string sqlColDefinition)
        {
            if (string.IsNullOrEmpty(sqlColDefinition)) return null;
            if (sqlColDefinition.IndexOf("max", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            var match = Regex.Match(sqlColDefinition, @"\((\d+)\)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var len) ? (int?)len : null;
        }
    }

    public class InsertBatchPropertyMapping
    {
        public PropertyInfo Property { get; set; }
        public ColumnSqlInfo SqlInfo { get; set; }

        public override string ToString()
        {
            return $"{Property?.Name}";
        }
    }
}
