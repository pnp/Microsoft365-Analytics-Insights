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

        /// <summary>
        /// Inserts the supplied assignments, ignoring any that already exist. Returns rows written.
        /// </summary>
        Task<int> AddAssignments(IReadOnlyList<UserLicenseAssignment> assignments);

        /// <summary>
        /// Deletes exactly the supplied assignments. Returns rows deleted.
        /// </summary>
        Task<int> RemoveAssignments(IReadOnlyList<UserLicenseAssignment> assignments);
    }
}
