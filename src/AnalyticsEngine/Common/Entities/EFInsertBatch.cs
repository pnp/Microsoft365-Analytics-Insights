using DataUtils.Sql.Inserts;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using System.Threading;

namespace Common.Entities
{
    /// <summary>
    /// A batch insert that takes db connection from an EF context
    /// </summary>
    public class EFInsertBatch<T> : InsertBatch<T> where T : class
    {
        public EFInsertBatch(DbContext context, ILogger logger) :
            this(context, logger, CancellationToken.None)
        {
        }

        public EFInsertBatch(DbContext context, ILogger logger, CancellationToken cancellationToken) :
            base(context.Database.Connection.ConnectionString, logger, cancellationToken)
        {
        }
    }
}
