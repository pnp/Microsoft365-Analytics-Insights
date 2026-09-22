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
        /// Updates an org type's name, attribute and enabled flag.
        /// </summary>
        /// <exception cref="UserOrgValidationException">The name is already taken, or the configuration is invalid.</exception>
        Task UpdateAsync(UserOrgType type, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Deletes an org type and everything hanging off it - assignments, values, import jobs and any
        /// staged rows - in one transaction.
        /// </summary>
        /// <remarks>
        /// Done explicitly rather than by cascade: <c>user_org_assignments</c> already cascades from
        /// <c>dbo.users</c>, and SQL Server refuses a second cascade path into the same table.
        /// </remarks>
        Task DeleteAsync(int id, CancellationToken cancellationToken = default(CancellationToken));
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

        /// <summary>One job, or <c>null</c> when it does not exist.</summary>
        Task<UserOrgImportJob> GetJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Moves a job from <see cref="UserOrgImportStatus.Pending"/> to
        /// <see cref="UserOrgImportStatus.Running"/>, returning <c>false</c> if somebody else already
        /// claimed it. Atomic, so two web instances cannot run the same import twice.
        /// </summary>
        Task<bool> TryClaimJobAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Records that the worker is still alive.</summary>
        Task HeartbeatAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Applies a claimed job's staged rows to the assignments, honouring its
        /// <see cref="UserOrgImportMode"/>, and returns the row counts.
        /// </summary>
        Task<UserOrgImportJob> ApplyAsync(int jobId, CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>Marks a job finished and discards its staged rows.</summary>
        Task CompleteJobAsync(
            int jobId,
            UserOrgImportStatus status,
            string errorMessage,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Whether this org type already has a job waiting or running, so the portal can refuse to queue
        /// a second one.
        /// </summary>
        Task<UserOrgImportJob> GetActiveJobForTypeAsync(
            int orgTypeId,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
