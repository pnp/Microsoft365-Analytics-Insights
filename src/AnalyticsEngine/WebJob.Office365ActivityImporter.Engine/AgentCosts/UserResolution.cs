using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// A directory user reduced to what linking a billing row needs: who they are, and the object id the
    /// billing feed refers to them by.
    /// </summary>
    /// <remarks>
    /// A value rather than a Graph SDK type so the resolution logic can be tested without HTTP, and so the
    /// set of fields this import actually depends on is explicit.
    /// </remarks>
    public class EntraUserRef
    {
        public string ObjectId { get; set; }
        public string UserPrincipalName { get; set; }
        public string Mail { get; set; }
        public bool? AccountEnabled { get; set; }
    }

    /// <summary>
    /// Read port for looking a person up in Entra by object id, used only when <c>dbo.users</c> does not
    /// already know them.
    /// </summary>
    /// <remarks>
    /// Deliberately one user at a time. This is an exception path, not a bulk one: the user import already
    /// loads the whole directory through <c>/users/delta</c>, so anyone appearing here is either newer than
    /// the last user import or has been deleted from the directory. Making it a bulk port would invite
    /// callers to treat it as a substitute for that import.
    /// </remarks>
    public interface IEntraUserLookup
    {
        /// <summary>
        /// The user with this object id, or <c>null</c> when the directory has no such object.
        /// </summary>
        /// <remarks>
        /// Null means "asked, and Entra says there is no such user" - a deleted account, which is a normal
        /// and permanent state for a billing row from a past period. Implementations must throw for
        /// "could not ask" (throttling, a network failure, a refused token) so the two are never conflated:
        /// treating a transient failure as a deletion would permanently abandon a resolvable user.
        /// </remarks>
        Task<EntraUserRef> GetUserByObjectIdAsync(string objectId);
    }

    /// <summary>
    /// Store port for linking <c>copilot_studio_credit_user_daily</c> rows to <c>dbo.users</c>.
    /// </summary>
    public interface IAgentCostUserLinkStore
    {
        /// <summary>
        /// Maps Entra object id =&gt; <c>dbo.users.id</c> for those already known. Ids with no user are simply
        /// absent from the result rather than present with a null.
        /// </summary>
        Task<IReadOnlyDictionary<string, int>> GetUserIdsByObjectIdAsync(IReadOnlyCollection<string> objectIds);

        /// <summary>
        /// Returns the id of the <c>dbo.users</c> row for this person, creating a minimal one if needed.
        /// </summary>
        /// <remarks>
        /// Matches on the <b>UPN first</b> and only inserts if that finds nothing. Users reach
        /// <c>dbo.users</c> from several places - the audit-activity staging merge inserts them by user name
        /// alone, with no object id - so a person can easily be present with an empty <c>azure_ad_id</c>.
        /// Inserting on object id alone would create a duplicate row for someone already there. When an
        /// existing row is found without an object id, this fills it in, which permanently removes the need
        /// for a Graph call for that user.
        /// </remarks>
        Task<int> EnsureUserAsync(EntraUserRef user);

        /// <summary>
        /// Distinct object ids on rows that are still unlinked, most recent usage first, capped at
        /// <paramref name="max"/>.
        /// </summary>
        /// <remarks>
        /// Recent first because an unlinked row is most likely to be a user created since the last user
        /// import, and those are the ones a fresh attempt will actually resolve. A row whose user was
        /// deleted years ago will never resolve, and must not be allowed to consume the whole budget on
        /// every cycle for ever.
        /// </remarks>
        Task<IReadOnlyList<string>> GetUnresolvedObjectIdsAsync(int max);

        /// <summary>
        /// Points every row carrying one of these object ids at the matching user. Returns rows updated.
        /// </summary>
        Task<int> ApplyUserLinksAsync(IReadOnlyDictionary<string, int> userIdsByObjectId);
    }

    /// <summary>
    /// What one resolution pass managed to do. Counts are kept apart because they mean different things to
    /// an admin: <see cref="NotInDirectory"/> is a settled fact, while <see cref="Deferred"/> and
    /// <see cref="LookupFailures"/> both mean "ask again next cycle".
    /// </summary>
    public class AgentCostUserResolution
    {
        public AgentCostUserResolution(IReadOnlyDictionary<string, int> userIdsByObjectId,
            int resolvedFromDatabase, int resolvedFromDirectory, int notInDirectory, int deferred, int lookupFailures)
        {
            UserIdsByObjectId = userIdsByObjectId ?? new Dictionary<string, int>();
            ResolvedFromDatabase = resolvedFromDatabase;
            ResolvedFromDirectory = resolvedFromDirectory;
            NotInDirectory = notInDirectory;
            Deferred = deferred;
            LookupFailures = lookupFailures;
        }

        public IReadOnlyDictionary<string, int> UserIdsByObjectId { get; }

        /// <summary>Matched against an existing <c>azure_ad_id</c>, with no Graph call.</summary>
        public int ResolvedFromDatabase { get; }

        /// <summary>Not in <c>dbo.users</c>, fetched from Entra and linked.</summary>
        public int ResolvedFromDirectory { get; }

        /// <summary>Entra has no such object - almost always a deleted account. Will not resolve later.</summary>
        public int NotInDirectory { get; }

        /// <summary>Left for the next cycle because this run's directory-lookup budget ran out.</summary>
        public int Deferred { get; }

        /// <summary>Could not be asked about - throttling, a network failure. Retried next cycle.</summary>
        public int LookupFailures { get; }

        public int TotalResolved => ResolvedFromDatabase + ResolvedFromDirectory;
    }
}
