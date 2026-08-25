using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph.Copilot.InteractionHistory
{
    /// <summary>
    /// The outcome of resolving <c>UserGroupsFilter</c> to a set of pilot member UPNs.
    /// </summary>
    /// <remarks>
    /// Carries a completeness flag as well as the members because "no members" and "we gave up looking"
    /// are very different things and used to be indistinguishable (issue #297). An empty set from a
    /// mistyped group name is a configuration problem the admin should fix; an empty set because group
    /// discovery hit its safety cap is the importer failing to do its job, and silently reporting the
    /// second as the first meant a pilot group sitting past the cap looked like an idle, healthy import.
    /// </remarks>
    public class PilotGroupResolution
    {
        public PilotGroupResolution(HashSet<string> memberUpns, string incompleteReason = null)
        {
            MemberUpns = memberUpns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IncompleteReason = incompleteReason;
        }

        /// <summary>Member UPNs of every matched group. Empty when nothing matched.</summary>
        public HashSet<string> MemberUpns { get; }

        /// <summary>
        /// Why the resolved scope may be missing members, or null when it is known to be complete.
        /// </summary>
        public string IncompleteReason { get; }

        /// <summary>
        /// True when a cap or an error stopped resolution early, so <see cref="MemberUpns"/> may be a
        /// subset of the configured scope. The import should report this rather than treat the result
        /// as authoritative.
        /// </summary>
        public bool IsIncomplete => !string.IsNullOrEmpty(IncompleteReason);
    }
}
