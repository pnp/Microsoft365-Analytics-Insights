using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The accounts one <c>/users/delta</c> read reported as no longer active: disabled, deleted - or, when the read
    /// was a complete read of the whole directory, simply not in it.
    /// </summary>
    /// <remarks>
    /// Nobody is added to the users table for them, but a row it already has for one of them must be marked disabled.
    /// Graph reports each change once and the committed delta token then moves past it, so an account dropped from a
    /// read stays <c>account_enabled = 1</c> for ever - and Copilot Adoption can never recommend reclaiming the licence
    /// of someone who has left.
    /// </remarks>
    public sealed class GraphUserDeactivations
    {
        private readonly HashSet<string> _completeReadObjectIds;

        /// <summary>Nothing was reported disabled or deleted, and the read was not a complete read of the directory.</summary>
        public static GraphUserDeactivations None { get; } =
            new GraphUserDeactivations(new GraphUser[0], new string[0], new string[0]);

        /// <param name="completeReadObjectIds">
        /// Every object id in the read, when it was a complete read of the whole directory; otherwise null.
        /// </param>
        public GraphUserDeactivations(IReadOnlyList<GraphUser> disabledUsers, IReadOnlyList<string> disabledObjectIds, IReadOnlyList<string> removedObjectIds,
            IEnumerable<string> completeReadObjectIds = null)
        {
            DisabledUsers = disabledUsers ?? throw new ArgumentNullException(nameof(disabledUsers));
            DisabledObjectIds = disabledObjectIds ?? throw new ArgumentNullException(nameof(disabledObjectIds));
            RemovedObjectIds = removedObjectIds ?? throw new ArgumentNullException(nameof(removedObjectIds));
            _completeReadObjectIds = completeReadObjectIds == null
                ? null
                : new HashSet<string>(completeReadObjectIds, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Users reported with <c>accountEnabled</c> false, complete with their sign-in name. A row the table already
        /// has for one is updated from them like any active user's, <c>account_enabled</c> included.
        /// </summary>
        public IReadOnlyList<GraphUser> DisabledUsers { get; }

        /// <summary>
        /// The object ids of every account reported disabled - those in <see cref="DisabledUsers"/>, and any whose
        /// entries came without a sign-in name - so every row carrying one can be marked disabled.
        /// </summary>
        public IReadOnlyList<string> DisabledObjectIds { get; }

        /// <summary>The object ids of deleted accounts. An <c>@removed</c> entry carries nothing else.</summary>
        public IReadOnlyList<string> RemovedObjectIds { get; }

        /// <summary>
        /// True when the read was a complete read of the whole directory: from no delta token, every page, to its
        /// <c>@odata.deltaLink</c>. It then lists every account that exists - and a full read never reports deletions, so
        /// an account the users table has that it leaves out has been deleted.
        /// </summary>
        public bool IsCompleteDirectoryRead => _completeReadObjectIds != null;

        /// <summary>How many accounts a complete read of the directory listed; 0 for any other read.</summary>
        public int CompleteReadCount => _completeReadObjectIds?.Count ?? 0;

        /// <summary>True when a complete read of the directory listed <paramref name="objectId"/>. False for any other read.</summary>
        public bool CompleteReadIncludes(string objectId)
            => _completeReadObjectIds != null && !string.IsNullOrEmpty(objectId) && _completeReadObjectIds.Contains(objectId);
    }

    /// <summary>One <c>/users/delta</c> read, sorted into what the import does with each entry.</summary>
    internal sealed class UserDeltaRead
    {
        public UserDeltaRead(List<GraphUser> activeUsers, GraphUserDeactivations deactivations)
        {
            ActiveUsers = activeUsers;
            Deactivations = deactivations;
        }

        /// <summary>Enabled users with a sign-in name, one per sign-in name: inserted or updated.</summary>
        public List<GraphUser> ActiveUsers { get; }

        /// <summary>Accounts reported disabled or deleted: never inserted, but their existing rows are marked disabled.</summary>
        public GraphUserDeactivations Deactivations { get; }
    }

    /// <summary>
    /// How the entries of a <c>/users/delta</c> read are sorted. Shared by <c>GraphUserLoader</c> and the test fake, so
    /// a test cannot pass only because the fake hands the import something the real loader never would.
    /// </summary>
    internal static class UserDeltaReadRules
    {
        /// <param name="completeDirectoryRead">
        /// True when <paramref name="entries"/> are a complete read of the whole directory (see
        /// <see cref="GraphUserDeactivations.IsCompleteDirectoryRead"/>), so absence from them means deletion.
        /// </param>
        /// <param name="initialRound">
        /// True when the entries come from a read that started without a delta token. Graph includes relationship
        /// annotations such as <c>manager@delta</c> in every entry of that round, so an entry without one has no manager;
        /// in a later round its absence only means the manager did not change (see <see cref="GraphUser.ManagerReported"/>).
        /// </param>
        /// <remarks>
        /// Graph lists an object once per change, in the order the changes happened, so each object is first resolved to
        /// its final state, and only then sorted:
        /// <list type="bullet">
        /// <item>Its account status is the one its latest entry reports: deleted (<c>@removed</c>), enabled or disabled.
        /// A later entry can leave out what did not change - <c>accountEnabled</c> too, which then changes nothing.</item>
        /// <item>Its profile is the latest entry that carries a sign-in name, written with that final status.</item>
        /// <item>Its manager is the one the latest entry that reported a manager names; when none did, it is "unchanged" -
        /// or, in the initial round, none.</item>
        /// <item>Enabled with a sign-in name: imported. Disabled: every row with its object id is marked disabled, and a
        /// row with its sign-in name is updated from the profile. Deleted: every row with its object id is marked
        /// disabled. No status at all: neither, as before.</item>
        /// <item>Two objects with one sign-in name - one renamed away and one renamed in, while the read was paged: the
        /// later profile has it.</item>
        /// </list>
        /// </remarks>
        public static UserDeltaRead Classify(IReadOnlyList<GraphUser> entries, bool completeDirectoryRead = false, bool initialRound = false)
        {
            var count = entries?.Count ?? 0;
            var byObjectId = new Dictionary<string, ObjectInRead>(count, StringComparer.OrdinalIgnoreCase);
            var objects = new List<ObjectInRead>(count);
            for (var i = 0; i < count; i++)
            {
                var entry = entries[i];
                if (entry == null || (entry.IsRemoved && string.IsNullOrEmpty(entry.Id)))
                {
                    continue;
                }

                ObjectInRead found;
                if (string.IsNullOrEmpty(entry.Id))
                {
                    // Nothing to tie it to another entry: on its own, exactly as before.
                    found = new ObjectInRead(null);
                    objects.Add(found);
                }
                else if (!byObjectId.TryGetValue(entry.Id, out found))
                {
                    found = new ObjectInRead(entry.Id);
                    byObjectId.Add(entry.Id, found);
                    objects.Add(found);
                }
                found.Apply(entry, i);
            }

            var disabledIds = new List<string>();
            var removedIds = new List<string>();
            var presentIds = completeDirectoryRead ? new List<string>(objects.Count) : null;
            var withProfileAt = new ObjectInRead[count];
            foreach (var found in objects)
            {
                if (found.Removed)
                {
                    removedIds.Add(found.ObjectId);
                    continue;
                }

                if (found.ObjectId != null)
                {
                    presentIds?.Add(found.ObjectId);
                    if (found.Enabled == false)
                    {
                        disabledIds.Add(found.ObjectId);
                    }
                }
                if (found.Enabled.HasValue && found.Profile != null)
                {
                    withProfileAt[found.ProfileAt] = found;
                }
            }

            // One profile per sign-in name, the latest - then back into the order of the read.
            var keep = new bool[count];
            var seenUpns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = count - 1; i >= 0; i--)
            {
                if (withProfileAt[i] != null && seenUpns.Add(withProfileAt[i].Profile.UserPrincipalName))
                {
                    keep[i] = true;
                }
            }

            var active = new List<GraphUser>(seenUpns.Count);
            var disabled = new List<GraphUser>();
            for (var i = 0; i < count; i++)
            {
                if (!keep[i])
                {
                    continue;
                }
                var found = withProfileAt[i];
                var user = found.Profile.AccountEnabled == found.Enabled ? found.Profile : found.Profile.WithAccountEnabled(found.Enabled);
                if (found.ManagerSource != null && !ReferenceEquals(found.ManagerSource, found.Profile))
                {
                    user = user.WithManagerFrom(found.ManagerSource);
                }
                else if (found.ManagerSource == null && initialRound)
                {
                    user = user.WithManagerFrom(null);
                }
                if (found.Enabled == true)
                {
                    active.Add(user);
                }
                else
                {
                    disabled.Add(user);
                }
            }

            var deactivations = disabled.Count == 0 && disabledIds.Count == 0 && removedIds.Count == 0 && !completeDirectoryRead
                ? GraphUserDeactivations.None
                : new GraphUserDeactivations(disabled, disabledIds, removedIds, presentIds);
            return new UserDeltaRead(active, deactivations);
        }

        /// <summary>One object's entries in a read, folded into its final state.</summary>
        private sealed class ObjectInRead
        {
            public ObjectInRead(string objectId)
            {
                ObjectId = objectId;
            }

            public string ObjectId { get; }

            /// <summary>Deleted, as of the latest entry.</summary>
            public bool Removed { get; private set; }

            /// <summary>The latest account status reported since the object was last seen deleted, if any.</summary>
            public bool? Enabled { get; private set; }

            /// <summary>The latest entry that carries a sign-in name, and its position in the read.</summary>
            public GraphUser Profile { get; private set; }

            public int ProfileAt { get; private set; }

            /// <summary>The latest entry that reported the user's manager, if any did.</summary>
            public GraphUser ManagerSource { get; private set; }

            public void Apply(GraphUser entry, int position)
            {
                if (entry.IsRemoved)
                {
                    Removed = true;
                    Enabled = null;
                    ManagerSource = null;
                    return;
                }

                Removed = false;
                if (entry.AccountEnabled.HasValue)
                {
                    Enabled = entry.AccountEnabled;
                }
                if (entry.ManagerReported)
                {
                    ManagerSource = entry;
                }
                if (!string.IsNullOrEmpty(entry.UserPrincipalName))
                {
                    Profile = entry;
                    ProfileAt = position;
                }
            }
        }
    }
}
