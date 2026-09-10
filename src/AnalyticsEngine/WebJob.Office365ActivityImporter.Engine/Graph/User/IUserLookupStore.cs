using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Read port for resolving <c>dbo.users</c> rows by user principal name in bulk.
    /// </summary>
    /// <remarks>
    /// Exists to remove the per-user database round-trip in the manager-resolution fallback chain
    /// (#371): <c>UserDataMapper</c> used to issue one
    /// <c>db.users.FirstOrDefaultAsync(u =&gt; u.UserPrincipalName == managerUpn)</c> per user whose
    /// manager could not be resolved from the in-memory dictionaries. During insert enrichment that
    /// happens whenever a manager is inserted in a <b>later</b> batch than their report: the
    /// dictionary starts from pre-existing users and each batch adds its own users before
    /// processing them, so a manager from an earlier or the current batch is found, and one from a
    /// later batch is not. Graph does not order the delta by reporting line, so on the first import
    /// of a large tenant that is a substantial share of everyone who has a manager - on the order
    /// of tens of thousands of individual queries per cycle at the ~200,000-user design target.
    /// Resolving a whole batch in one chunked <c>Contains(...)</c> query makes it one query per
    /// batch instead.
    ///
    /// Implementations must return entities that are <b>tracked</b> by the same context the caller
    /// is saving through, because the caller assigns them straight to a navigation property; a
    /// detached instance would make EF try to INSERT the manager and fail on a duplicate key. That
    /// is why the port hands back entities rather than ids. They must also carry the user's licence
    /// lookups: a returned entity can win EF identity resolution over a snapshot the caller was
    /// about to attach, so it has to be at least as complete - see <see cref="SqlUserLookupStore"/>.
    ///
    /// Internal because its collaborators are internal; <c>InternalsVisibleTo("Tests.UnitTests")</c>
    /// makes it reachable from the test project.
    /// </remarks>
    internal interface IUserLookupStore
    {
        /// <summary>
        /// Returns the users whose <c>user_name</c> matches one of <paramref name="upns"/>. UPNs with
        /// no row are simply absent from the result. Matching is case-insensitive, as SQL Server's
        /// default code-first collation (<c>Latin1_General_CI_AS</c>) makes it.
        /// </summary>
        Task<IReadOnlyList<Common.Entities.User>> GetUsersByUpnAsync(IReadOnlyCollection<string> upns);
    }
}
