using Common.DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System.Data.Entity;

namespace Common.Entities
{
    /// <summary>
    /// A batch insert that takes db connection from an EF context
    /// </summary>
    public class EFInsertBatch<T> : InsertBatch<T> where T : class
    {
        public EFInsertBatch(DbContext context, ILogger debugTracer) :
            base(context.Database.Connection.ConnectionString, debugTracer)
        {
        }
    }
}
