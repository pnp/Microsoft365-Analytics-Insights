using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Common.DataUtils.Sql.Inserts
{
    /// <summary>
    /// Build SQL defintion from a InsertBatchType class
    /// </summary>
    public class InsertBatchTypeFieldCache<T>
    {
        private List<InsertBatchPropertyMapping> _fieldInfoPropertyInfoCache = null;

        static List<Type> _validTempColumnTypes = new List<Type>() { typeof(string), typeof(DateTime), typeof(int), typeof(float), typeof(double), typeof(bool), typeof(Guid), typeof(int?),
            typeof(int?), typeof(double?) };

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
                            fieldInfo.SqlColDefinition = fileColName;
                            fieldInfo.Nullable = attribute.Nullable ? true : propTypeIsNullable;
                            fieldInfo.SqlType = SqlHelper.GetDbType(property.PropertyType);
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
            else if (propertyType == typeof(Guid))
            {
                return ("uniqueidentifier", false);
            }
            return ("[nvarchar] (max)", false);
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
