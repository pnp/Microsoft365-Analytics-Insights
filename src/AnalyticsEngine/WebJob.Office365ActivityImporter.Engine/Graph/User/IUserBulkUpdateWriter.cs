using System.Data;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Write port for the existing-user metadata bulk update: hands a fully-resolved batch of
    /// <c>dbo.users</c> rows to storage and returns once they have been applied.
    /// </summary>
    /// <remarks>
    /// Extracted from <c>UserBatchProcessor</c> for issues #371 / #381 so the batching, the
    /// foreign-key resolution and the shape of the <see cref="DataTable"/> can be tested with zero
    /// SQL Server dependency. The production implementation,
    /// <see cref="SqlUserBulkUpdateWriter"/>, is the original <c>SqlConnection</c> +
    /// <c>SqlBulkCopy</c> + temp-table code <b>relocated verbatim</b> - it is deliberately not
    /// rewritten.
    ///
    /// Internal because <c>UserBatchProcessor</c> is internal;
    /// <c>InternalsVisibleTo("Tests.UnitTests")</c> makes it reachable from the test project.
    /// </remarks>
    internal interface IUserBulkUpdateWriter
    {
        /// <summary>
        /// Applies one batch of user updates. The table's columns are the ones described by
        /// <see cref="UserBulkUpdateRules.CreateUpdateTable"/>; an empty table is a no-op.
        /// </summary>
        Task ExecuteAsync(DataTable userUpdates);
    }
}
