using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The difference between the licence assignments a database currently holds and the ones the
    /// tenant's SKUs say it should hold. Pure logic - no database, no Graph - so the reconciliation
    /// rules can be unit tested directly (issue #392).
    /// </summary>
    public class UserLicenseAssignmentDelta
    {
        private UserLicenseAssignmentDelta(
            List<UserLicenseAssignment> toAdd,
            List<UserLicenseAssignment> toRemove,
            int unchangedCount)
        {
            ToAdd = toAdd;
            ToRemove = toRemove;
            UnchangedCount = unchangedCount;
        }

        /// <summary>Assignments Graph reports that the database does not have yet.</summary>
        public IReadOnlyList<UserLicenseAssignment> ToAdd { get; }

        /// <summary>Assignments the database holds that Graph no longer reports.</summary>
        public IReadOnlyList<UserLicenseAssignment> ToRemove { get; }

        /// <summary>Assignments that are already correct and must not be touched.</summary>
        public int UnchangedCount { get; }

        public bool IsEmpty => ToAdd.Count == 0 && ToRemove.Count == 0;

        /// <summary>
        /// Builds the reconciliation plan. <paramref name="current"/> must already be scoped to the
        /// users this refresh owns, so nothing outside that scope can ever be removed.
        /// </summary>
        public static UserLicenseAssignmentDelta Between(
            ISet<UserLicenseAssignment> current,
            ISet<UserLicenseAssignment> desired)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (desired == null) throw new ArgumentNullException(nameof(desired));

            var toAdd = new List<UserLicenseAssignment>();
            var unchanged = 0;
            foreach (var wanted in desired)
            {
                if (current.Contains(wanted))
                {
                    unchanged++;
                }
                else
                {
                    toAdd.Add(wanted);
                }
            }

            var toRemove = new List<UserLicenseAssignment>();
            foreach (var existing in current)
            {
                if (!desired.Contains(existing))
                {
                    toRemove.Add(existing);
                }
            }

            return new UserLicenseAssignmentDelta(toAdd, toRemove, unchanged);
        }
    }
}
