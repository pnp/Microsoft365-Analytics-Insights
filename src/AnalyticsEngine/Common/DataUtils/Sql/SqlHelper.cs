using System;
using System.Collections.Generic;
using System.Data;

namespace Common.DataUtils.Sql
{
    /// <summary>
    /// Turn .Net types into generic SQL types
    /// </summary>
    public static class SqlHelper
    {
        private static Dictionary<Type, DbType> typeMap;

        // Create and populate the dictionary in the static constructor
        static SqlHelper()
        {
            typeMap = new Dictionary<Type, DbType>();

            typeMap[typeof(string)] = DbType.String;
            typeMap[typeof(int)] = DbType.Int32;
            typeMap[typeof(long)] = DbType.Int64;
            typeMap[typeof(float)] = DbType.Single;
            typeMap[typeof(double)] = DbType.Double;
            typeMap[typeof(bool)] = DbType.Boolean;
            typeMap[typeof(DateTime)] = DbType.DateTime2;
            typeMap[typeof(Guid)] = DbType.Guid;
            /* ... and so on ... */
        }

        // Non-generic argument-based method
        public static DbType GetDbType(Type giveType)
        {
            // Allow nullable types to be handled
            giveType = Nullable.GetUnderlyingType(giveType) ?? giveType;

            if (typeMap.ContainsKey(giveType))
            {
                return typeMap[giveType];
            }

            throw new ArgumentException($"{giveType.FullName} is not a supported .NET class");
        }

        // Generic version
        public static DbType GetDbType<T>()
        {
            return GetDbType(typeof(T));
        }
    }

}
