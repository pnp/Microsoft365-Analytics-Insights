using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// Reads and writes the admin-defined org types.
    /// </summary>
    /// <remarks>
    /// A port, so the portal's CRUD and the importer's "which attributes do I need?" question can both
    /// be exercised without SQL Server. <c>SqlUserOrgTypeStore</c> is the adapter.
    /// </remarks>
    public interface IUserOrgTypeStore
    {
        /// <summary>Every org type, enabled or not, ordered by name.</summary>
        Task<IReadOnlyList<UserOrgType>> GetAllAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>One org type, or <c>null</c> when it does not exist.</summary>
        Task<UserOrgType> GetAsync(int id, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Every org type with the counts and last-import detail the admin page shows.</summary>
        Task<IReadOnlyList<UserOrgTypeSummary>> GetSummariesAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Only the enabled, Entra-sourced types. This is what decides the Graph <c>$select</c>, and
        /// therefore the delta-token cache key.
        /// </summary>
        Task<IReadOnlyList<UserOrgType>> GetEnabledEntraTypesAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Creates an org type and returns its new id.
        /// </summary>
        /// <exception cref="UserOrgValidationException">The name is already taken, or the configuration is invalid.</exception>
        Task<int> CreateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Updates an org type's name, attribute and enabled flag, optionally discarding everything it
        /// currently holds in the same transaction.
        /// </summary>
        /// <param name="clearAssignments">
        /// Whether the values this type holds are invalidated by the change. They are whenever the
        /// source changes - a different Entra attribute, or a switch between Entra and CSV - because
        /// the stored values were read from somewhere that is no longer this dimension's source of
        /// truth. Atomic with the update on purpose: see the note on the implementation.
        /// </param>
        /// <param name="bumpGeneration">
        /// Whether a delta token minted under the old configuration stops being safe to reuse. True
        /// for every change that makes the Entra merge fence a type's updates out - the values being
        /// discarded, the source kind changing, <b>and</b> the type being disabled. A type disabled
        /// mid-cycle has its updates dropped but the cycle still commits its token, so without this
        /// re-enabling would rebuild the same cache key and resume past the users it skipped.
        /// </param>
        /// <param name="expectedGeneration">
        /// The <see cref="UserOrgType.SourceGeneration"/> the caller read when it decided
        /// <paramref name="clearAssignments"/> and <paramref name="bumpGeneration"/>, or <c>null</c> not to
        /// check. The generation moves on every change of source, attribute or enabled flag - exactly the
        /// changes those two decisions depend on - so an update whose read has since gone stale is refused
        /// with <see cref="UserOrgMessageCodes.TypeChangedElsewhere"/> rather than applied with the wrong
        /// side effects.
        /// </param>
        /// <param name="expectedRevision">
        /// The <see cref="UserOrgType.Revision"/> the admin's page showed when they opened the type, or
        /// <c>null</c> not to check. Every save moves it, so a save from a dialog opened before a
        /// colleague's change - a rename included - is refused with
        /// <see cref="UserOrgMessageCodes.TypeChangedElsewhere"/> rather than putting their change back.
        /// </param>
        /// <param name="confirmedDiscardCount">
        /// For a save that discards the type's values (<paramref name="clearAssignments"/>): how many the
        /// admin was shown it would discard, or <c>null</c> not to check. Imports move neither the generation
        /// nor the revision, so a type that has gained values since the dialog opened is refused with
        /// <see cref="UserOrgMessageCodes.DiscardExceedsConfirmed"/> rather than emptied of values nobody saw.
        /// </param>
        /// <exception cref="UserOrgValidationException">The name is already taken, the configuration is invalid, or the type changed since it was read.</exception>
        Task UpdateAsync(
            UserOrgType type,
            bool clearAssignments,
            bool bumpGeneration,
            CancellationToken cancellationToken = default(CancellationToken),
            int? expectedGeneration = null,
            int? expectedRevision = null,
            int? confirmedDiscardCount = null);

        /// <summary>
        /// Deletes an org type and everything hanging off it - assignments, values, import jobs and any
        /// staged rows - in one transaction.
        /// </summary>
        /// <remarks>
        /// Done explicitly rather than by cascade: <c>user_org_assignments</c> already cascades from
        /// <c>dbo.users</c>, and SQL Server refuses a second cascade path into the same table.
        /// </remarks>
        /// <param name="expectedRevision">
        /// The <see cref="UserOrgType.Revision"/> the admin's page showed when they chose to delete it, or
        /// <c>null</c> not to check. A type a colleague has saved since is refused with
        /// <see cref="UserOrgMessageCodes.TypeChangedBeforeDelete"/> rather than deleted.
        /// </param>
        Task DeleteAsync(
            int id,
            CancellationToken cancellationToken = default(CancellationToken),
            int? expectedRevision = null);

        /// <summary>
        /// Records that a user import which began at <paramref name="refreshedUtc"/> brought these
        /// Entra-sourced types up to date, and returns how many were stamped.
        /// </summary>
        /// <param name="expectedGenerations">
        /// Org type id to the source generation the cycle read before loading users. A type is only
        /// stamped if it is still Entra-sourced, enabled and on that generation - the same fence the
        /// merge applies - so a type reconfigured mid-cycle is never reported as refreshed by it.
        /// </param>
        /// <param name="refreshedUtc">When the cycle began. A type's time never moves backwards.</param>
        /// <remarks>
        /// Called even when the cycle changed nobody's value: a delta query that returns no changes
        /// is still a confirmation that the stored values are current, and it is the usual case. Never
        /// stamps a type whose attribute was found holding lists (<see cref="RecordListValuedAsync"/>).
        /// </remarks>
        Task<int> RecordEntraRefreshAsync(
            IReadOnlyDictionary<int, int> expectedGenerations,
            System.DateTime refreshedUtc,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Records that a user import found these Entra-sourced types' attributes holding lists of values,
        /// and returns how many were marked.
        /// </summary>
        /// <param name="expectedGenerations">
        /// Org type id to the source generation the cycle read, with the same fence as
        /// <see cref="RecordEntraRefreshAsync"/>. The mark holds only at that generation, so any later
        /// change of source, attribute or enabled flag lifts it (<see cref="UserOrgType.AttributeHoldsLists"/>).
        /// </param>
        /// <remarks>
        /// Persisted rather than simply not stamping the type that cycle: the delta token still moves on, so
        /// the next cycle - often one in which nobody with a list changed - would otherwise stamp as
        /// refreshed a type whose values were never read.
        /// </remarks>
        Task<int> RecordListValuedAsync(
            IReadOnlyDictionary<int, int> expectedGenerations,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Writes user-to-org assignments in bulk and reads them back for one user.
    /// </summary>
    public interface IUserOrgAssignmentStore
    {
        /// <summary>
        /// Applies a batch of updates: creates any org values that do not exist yet, upserts the
        /// assignments, and removes the assignments whose incoming value is <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Merge semantics only - a user absent from <paramref name="updates"/> is left alone. This is
        /// required for the Entra path, because <c>/users/delta</c> returns only the users that
        /// changed, so treating absence as "no value" would wipe the entire tenant. Wholesale
        /// replacement is a CSV-only operation and lives on <see cref="IUserOrgImportJobStore"/>.
        /// </remarks>
        Task<UserOrgMergeResult> MergeAsync(
            IReadOnlyList<UserOrgAssignmentUpdate> updates,
            UserOrgSourceKind? expectedSourceKind = null,
            IReadOnlyDictionary<int, int> expectedGenerations = null,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Every org value held by one user, ordered by org type name.</summary>
        Task<IReadOnlyList<UserOrgValueForUser>> GetForUserAsync(
            int userId,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Removes every assignment for an org type. Used when an Entra-sourced type is repointed at a
        /// different attribute, where the previous values no longer mean anything.
        /// </summary>
        Task<int> ClearAllForTypeAsync(
            int orgTypeId,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Answers "who is in each organisation?" for the admin page.
    /// </summary>
    /// <remarks>
    /// Read-only, and paged throughout: one organisation on a 200,000-user tenant can hold tens of
    /// thousands of people, so nothing here returns a whole membership in one call.
    /// </remarks>
    public interface IUserOrgMembershipReader
    {
        /// <summary>
        /// One page of an org type's organisations, largest first, each with how many users are in it.
        /// </summary>
        /// <param name="search">
        /// Matched anywhere in the organisation name, literally and case-insensitively; <c>null</c> or
        /// blank for every organisation. Normalised with <see cref="UserOrgRules.NormaliseSearch"/>.
        /// </param>
        Task<UserOrgValuePage> GetValuesAsync(
            int orgTypeId,
            string search,
            int skip,
            int take,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// One page of the users in one organisation, ordered by user principal name, or <c>null</c> when
        /// the organisation does not exist or belongs to a different org type.
        /// </summary>
        /// <param name="search">
        /// Matched anywhere in the user principal name, literally and case-insensitively; <c>null</c> or
        /// blank for every member. Normalised with <see cref="UserOrgRules.NormaliseSearch"/>.
        /// </param>
        Task<UserOrgMemberPage> GetMembersAsync(
            int orgTypeId,
            int orgValueId,
            string search,
            int skip,
            int take,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Looks up which of a set of UPNs actually exist, so a CSV preview can tell an administrator that
    /// half their file will not match anybody <b>before</b> they import it.
    /// </summary>
    public interface IUserOrgUserLookup
    {
        /// <summary>
        /// The subset of <paramref name="upns"/> that match a row in <c>dbo.users</c>, compared the way
        /// the database compares them (case-insensitively).
        /// </summary>
        Task<IReadOnlyCollection<string>> FindExistingUpnsAsync(
            IReadOnlyCollection<string> upns,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// The subset of <paramref name="upns"/> whose users <b>currently hold a value</b> for
        /// <paramref name="orgTypeId"/>.
        /// </summary>
        /// <remarks>
        /// Needed to answer "how many users would this Replace clear?" honestly. Counting the users a
        /// file covers is not the same question: a file can cover a large population that is almost
        /// entirely disjoint from the one currently assigned, and subtracting one from the other then
        /// reports zero at precisely the moment the import is about to wipe everybody.
        /// </remarks>
        Task<IReadOnlyCollection<string>> FindAssignedUpnsAsync(
            int orgTypeId,
            IReadOnlyCollection<string> upns,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// Owns the CSV import job lifecycle: staging the parsed rows, claiming a job to run, applying it,
    /// and recording the outcome.
    /// </summary>
    public interface IUserOrgImportJobStore
    {
        /// <summary>
        /// Creates a <see cref="UserOrgImportStatus.Pending"/> job and bulk-inserts its staged rows,
        /// returning the new job id.
        /// </summary>
        Task<int> CreateJobWithRowsAsync(
            UserOrgImportJob job,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Stages a previewed file as a <see cref="UserOrgImportStatus.Draft"/>, returning its id.
        /// </summary>
        /// <remarks>
        /// Also throws away expired drafts, and this admin's earlier drafts for the same org type, so
        /// previewing a file repeatedly does not pile up copies of every UPN in it. "Earlier" means asked
        /// for before this one: <paramref name="draft"/>'s <see cref="UserOrgImportJob.QueuedUtc"/> is when
        /// the preview request arrived (now, when unset), so a slow preview finishing after a newer one
        /// cannot throw the newer draft away.
        /// </remarks>
        Task<int> CreateDraftAsync(
            UserOrgImportJob draft,
            IReadOnlyList<UserOrgStagedRow> rows,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// What importing a draft would do, computed in SQL with exactly the matching the apply uses,
        /// so the preview and the import cannot disagree. <c>null</c> when the draft does not exist.
        /// </summary>
        Task<UserOrgDraftSummary> SummariseDraftAsync(
            int draftId,
            int sampleRows,
            int unknownRowLimit,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Turns a draft into a <see cref="UserOrgImportStatus.Pending"/> job, once every check has
        /// passed inside the transaction that admits it. Only <paramref name="startedBy"/> - the
        /// administrator who previewed the file - can import a draft; anyone else is told it does not exist.
        /// </summary>
        /// <exception cref="UserOrgValidationException">
        /// With a code from <see cref="UserOrgImportRefusalCodes"/>: the draft expired, was already
        /// imported or belongs to another administrator, the type changed, another import is running, no
        /// row matches a user, or the import would clear more users than <paramref name="confirmedClearCount"/>.
        /// </exception>
        Task CommitDraftAsync(
            int draftId,
            int orgTypeId,
            UserOrgImportMode mode,
            int confirmedClearCount,
            string startedBy,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>One job, or <c>null</c> when it does not exist.</summary>
        Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>An org type's most recent imports, newest first. Drafts are not imports and are left out.</summary>
        Task<IReadOnlyList<UserOrgImportJob>> ListJobsAsync(
            int orgTypeId,
            int take,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Moves a job from <see cref="UserOrgImportStatus.Pending"/> to
        /// <see cref="UserOrgImportStatus.Running"/> - or takes over a Running job whose heartbeat has
        /// gone stale - returning the claim's attempt number, or <c>null</c> if somebody else already
        /// claimed it. Atomic, so two web instances cannot run the same import twice.
        /// </summary>
        /// <remarks>
        /// The attempt number is the claim's identity. A takeover moves it, so a worker that was presumed
        /// gone but was merely slow can be told apart from the one that took its job over - see
        /// <see cref="CompleteJobAsync"/>'s <c>claimedAttempt</c>.
        /// </remarks>
        Task<int?> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Records that the worker is still alive.</summary>
        Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Applies a claimed job's staged rows to the assignments, honouring its
        /// <see cref="UserOrgImportMode"/>, and returns the row counts.
        /// </summary>
        /// <remarks>
        /// The job is marked <see cref="UserOrgImportStatus.Succeeded"/> in the same transaction as the
        /// changes, so a job left anything but succeeded - however its worker died - changed nothing.
        /// </remarks>
        Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Marks a job finished and discards its staged rows.</summary>
        /// <param name="errorCode">A key from <see cref="UserOrgImportErrorCodes"/> for a job that did not succeed.</param>
        /// <param name="claimedAttempt">
        /// The attempt number <see cref="TryClaimJobAsync"/> gave the worker reporting, or <c>null</c> not to
        /// check. A worker taken over because it went quiet is merely slow, and its verdict is not the live
        /// claim's: with this it changes nothing, and leaves the staged rows the live claim is working from.
        /// </param>
        Task CompleteJobAsync(
            int jobId,
            UserOrgImportStatus status,
            string errorMessage,
            CancellationToken cancellationToken = default(CancellationToken),
            string errorCode = null,
            int? claimedAttempt = null);

        /// <summary>
        /// Finds jobs whose worker died - a dispatch lost with the web process, or a stale heartbeat -
        /// and returns the ids to dispatch again; <see cref="TryClaimJobAsync"/> takes them over. Jobs
        /// that have been claimed too often, or are too old to resume, are stopped instead. Also tidies
        /// up staged rows of finished jobs and expired drafts.
        /// </summary>
        Task<IReadOnlyList<int>> ResumeStaleJobsAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Whether this org type already has a job waiting or running, so the portal can refuse to queue
        /// a second one.
        /// </summary>
        Task<UserOrgImportJob> GetActiveJobForTypeAsync(
            int orgTypeId,
            CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>What importing a staged draft would do.</summary>
    /// <summary>
    /// The change lists of applied imports: captured in SQL in the transaction that makes the changes,
    /// and held there until they are written to the change log (<see cref="IUserOrgChangeLog"/>).
    /// </summary>
    public interface IUserOrgChangeOutbox
    {
        /// <summary>
        /// Takes the right to write one import's change log, or returns <c>null</c> when another worker -
        /// in this process or on another instance - is already writing it. Disposing the lease gives it
        /// up, and a worker that dies gives it up with its connection.
        /// </summary>
        Task<IUserOrgChangeLease> TryLeaseAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>One page of an import's changes, in user id order, starting after <paramref name="afterUserId"/>.</summary>
        Task<IReadOnlyList<UserOrgChangeRecord>> ReadAsync(
            int jobId,
            int afterUserId,
            int take,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Applied imports whose change log has not been written yet, oldest first.</summary>
        Task<IReadOnlyList<int>> ListPendingAsync(int take, CancellationToken cancellationToken = default(CancellationToken));
    }

    /// <summary>
    /// The right to write one import's change log: a transaction-owned application lock on a connection
    /// of its own.
    /// </summary>
    /// <remarks>
    /// The lock goes with its connection, and a connection can go without the worker noticing - a
    /// failover, a network break - letting another worker take the log over. Both writes that matter are
    /// therefore fenced by the lease itself: <see cref="IsHeldAsync"/> before the summary, and
    /// <see cref="CompleteAsync"/> in the lease's own transaction.
    /// </remarks>
    public interface IUserOrgChangeLease : IDisposable
    {
        /// <summary>
        /// Whether the lease is still held. A transaction-owned lock is never given up and taken back, so
        /// held now means held since it was taken - and nobody else can have emptied the outbox meanwhile.
        /// </summary>
        Task<bool> IsHeldAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Records where the import's change log was written and empties its outbox, in the lease's own
        /// transaction, then gives the lease up. Throws, changing nothing, once the lease has gone - so a
        /// worker that lost it can never empty an outbox another worker is still reading.
        /// </summary>
        Task CompleteAsync(UserOrgChangeLogStatus writtenTo, CancellationToken cancellationToken = default(CancellationToken));
    }

    public sealed class UserOrgDraftSummary
    {
        public int DraftId { get; set; }

        public int OrgTypeId { get; set; }

        /// <summary>Staged rows.</summary>
        public int RowsTotal { get; set; }

        /// <summary>Staged rows whose UPN matches nobody.</summary>
        public int UnknownRows { get; set; }

        /// <summary>Distinct users the file gives a value to, after the last line per person wins.</summary>
        public int MatchedUsersWithValue { get; set; }

        /// <summary>Distinct users the file names at all.</summary>
        public int MatchedUsers { get; set; }

        public int CurrentlyAssigned { get; set; }

        /// <summary>Users a Replace would clear: everyone assigned today whom the file does not give a value to.</summary>
        public int ReplaceWouldClear { get; set; }

        /// <summary>Users a Merge would clear: people the file lists with an empty value who have one today.</summary>
        public int MergeWouldClear { get; set; }

        /// <summary>The first staged rows, with whether each matches a user.</summary>
        public IReadOnlyList<UserOrgDraftRow> SampleRows { get; set; } = new UserOrgDraftRow[0];

        /// <summary>Staged rows naming nobody, in file order, capped.</summary>
        public IReadOnlyList<UserOrgStagedRow> UnknownRowList { get; set; } = new UserOrgStagedRow[0];
    }

    public sealed class UserOrgDraftRow
    {
        public int LineNumber { get; set; }

        public string Upn { get; set; }

        public string OrgValue { get; set; }

        public bool UserExists { get; set; }
    }

    /// <summary>
    /// Why queueing an import was refused. Carried on <see cref="UserOrgValidationException.Code"/>,
    /// and an API contract: the portal words each one.
    /// </summary>
    public static class UserOrgImportRefusalCodes
    {
        public const string ImportInProgress = "importInProgress";
        public const string DraftNotFound = "draftNotFound";
        public const string TypeChanged = "typeChanged";
        public const string TypeNotFound = "typeNotFound";
        public const string TypeNotCsv = "typeNotCsv";
        public const string TypeDisabled = "typeDisabled";
        public const string NoMatchingUsers = "noMatchingUsers";
        public const string ClearExceedsConfirmed = "clearExceedsConfirmed";
        public const string NoFile = "noFile";
        public const string UploadUnreadable = "uploadUnreadable";
        public const string UploadTooLarge = "uploadTooLarge";
        public const string InvalidMode = "invalidMode";
        public const string InvalidColumns = "invalidColumns";
        public const string ChangePageExpired = "changePageExpired";
    }
}
