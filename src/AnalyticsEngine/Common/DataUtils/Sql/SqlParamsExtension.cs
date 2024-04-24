using System;
using System.Data;

namespace Common.DataUtils.Sql
{
    public static class SqlParamsExtension
    {
        public static void ParamUp(this System.Data.Common.DbCommand cmd, string param, object val)
        {
            cmd.ParamUp(param, val, DbType.String);
        }
        public static void ParamUp(this System.Data.Common.DbCommand cmd, string param, object val, DbType type)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = param;
            p.DbType = type;
            if (val == null)
            {
                p.Value = DBNull.Value;
            }
            else
            {
                p.Value = val;
            }
            cmd.Parameters.Add(p);
        }
    }
}
