using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// One row of <c>dbo.user_license_type_lookups</c> - "this user holds this licence type" -
    /// as a value type, so a licence refresh can be expressed as a set difference between what
    /// the database holds and what Graph says should be there.
    /// </summary>
    public struct UserLicenseAssignment : IEquatable<UserLicenseAssignment>
    {
        public UserLicenseAssignment(int userId, int licenseTypeId)
        {
            UserId = userId;
            LicenseTypeId = licenseTypeId;
        }

        public int UserId { get; }

        public int LicenseTypeId { get; }

        public bool Equals(UserLicenseAssignment other)
        {
            return UserId == other.UserId && LicenseTypeId == other.LicenseTypeId;
        }

        public override bool Equals(object obj)
        {
            return obj is UserLicenseAssignment other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (UserId * 397) ^ LicenseTypeId;
            }
        }
        public override string ToString() => $"user {UserId} -> licence type {LicenseTypeId}";
    }
    /// <summary>
    /// Context for one completed licence refresh. It is created before writes so assignment history
    /// can carry the refresh timestamp and the preceding completed refresh as the uncertainty bound;
    /// it is only persisted to dbo.license_refresh_runs once all Graph reads and SQL writes succeed.
    /// </summary>
    public sealed class LicenseRefreshRunInfo
    {
        public LicenseRefreshRunInfo(DateTime completedUtc, DateTime? previousCompletedUtc, bool historyTablesAvailable)
        {
            CompletedUtc = completedUtc;
            PreviousCompletedUtc = previousCompletedUtc;
            HistoryTablesAvailable = historyTablesAvailable;
        }
        public DateTime CompletedUtc { get; }
        public DateTime? PreviousCompletedUtc { get; }
        public bool HistoryTablesAvailable { get; }
        public bool IsFirstHistoryRefresh => HistoryTablesAvailable && PreviousCompletedUtc == null;
    }
    /// <summary>Seat-count observation for one licence type in a completed refresh.</summary>
    public sealed class LicenseSeatCountSnapshot
    {
        public int LicenseTypeId { get; set; }
        public int? ConsumedUnits { get; set; }
        public int? PrepaidEnabledUnits { get; set; }
        public int? PrepaidWarningUnits { get; set; }
        public int? PrepaidSuspendedUnits { get; set; }
    }
    /// <summary>
    /// Read/write port for <c>dbo.user_license_type_lookups</c>, so the licence-refresh rules in
    /// <c>UserLicenseProcessor</c> can be exercised without a database. See issue #392.
    /// </summary>
    /// <remarks>
    /// Deliberately expresses the refresh as "load what is there, add what is missing, remove what
    /// is gone" rather than "delete everything then re-insert". The old delete-then-refill left the
    /// table partially populated for the several minutes the refill took, so every report joining it
    /// saw a tenant missing most or all of its licences.
    /// </remarks>
    public interface IUserLicenseStore
    {
        /// <summary>
        /// Licence assignments currently stored for the supplied users. Users outside this set are
        /// not returned, so the caller can never delete a row it does not own.
        /// </summary>
        Task<HashSet<UserLicenseAssignment>> LoadAssignmentsFor(ICollection<int> userIds);
        /// <summary>Creates the in-memory context for one licence refresh, probing whether history tables exist.</summary>
        Task<LicenseRefreshRunInfo> StartRefresh(DateTime completedUtc);
        /// <summary>Carries current lookup and open-history rows across licence-type rows that represent the same SKU after a display-name rename.</summary>
        Task<int> CarryAssignmentsAcrossRenamedLicenceTypes(IReadOnlyList<int> currentLicenseTypeIds, LicenseRefreshRunInfo refresh);
        /// <summary>
        /// Inserts the supplied assignments, ignoring any that already exist. Returns rows written.
        /// </summary>
        Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh);
        /// <summary>
        /// Deletes exactly the supplied assignments. Returns rows deleted.
        /// </summary>
        Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments, LicenseRefreshRunInfo refresh);

        /// <summary>Repairs open history rows to match the current lookup table after a completed refresh.</summary>
        Task<LicenseHistoryReconcileResult> ReconcileHistoryWithCurrentLookups(LicenseRefreshRunInfo refresh);

        /// <summary>Records the completed refresh and its seat-count observations after every write has succeeded.</summary>
        Task<int?> CompleteRefresh(LicenseRefreshRunInfo refresh, IReadOnlyList<LicenseSeatCountSnapshot> seatCounts);
    }

    /// <summary>Counts from the set-based history/current-state reconcile.</summary>
    public sealed class LicenseHistoryReconcileResult
    {
        public int ClosedOpenRowsWithoutLookup { get; set; }
        public int OpenedLookupRowsWithoutHistory { get; set; }
    }
}
