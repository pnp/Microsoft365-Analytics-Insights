using System;
using System.Collections.Generic;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>A users row to mark disabled, with the Entra object id it was selected by.</summary>
    internal struct UserRowDeactivation
    {
        public UserRowDeactivation(int userId, string objectId)
        {
            UserId = userId;
            ObjectId = objectId;
        }

        public int UserId { get; }

        /// <summary>
        /// Checked again when the row is written: if the row no longer carries it - a new account has been given the
        /// deleted one's sign-in name, and this run's update has already moved the row to it - the row is left alone.
        /// </summary>
        public string ObjectId { get; }
    }

    /// <summary>
    /// Which <c>dbo.users</c> row stands for an Entra object id.
    /// </summary>
    /// <remarks>
    /// The import identifies people by sign-in name (UPN), so a renamed user gets a second row carrying the same
    /// object id, and the old row stays behind. <c>azure_ad_id</c> is unindexable <c>nvarchar(max)</c>, so these rules
    /// work on the rows the import has already loaded, and never query by it. Zero SQL Server and zero Graph
    /// dependency.
    /// </remarks>
    internal static class UserIdentityRules
    {
        /// <summary>
        /// Adds <paramref name="user"/> to <paramref name="byObjectId"/>, keeping the canonical row when another row
        /// already holds its object id (see <see cref="IsPreferredRow"/>), whatever order the rows arrive in.
        /// </summary>
        /// <param name="currentUpnOf">
        /// The sign-in name Graph reports now for an object id, or null when it is not in this read. Only consulted
        /// for an object id more than one row carries.
        /// </param>
        /// <returns>True when another row already carried the object id.</returns>
        public static bool AddByObjectId(Dictionary<string, Common.Entities.User> byObjectId, Common.Entities.User user, Func<string, string> currentUpnOf)
        {
            if (user == null || string.IsNullOrEmpty(user.AzureAdId))
            {
                return false;
            }

            if (!byObjectId.TryGetValue(user.AzureAdId, out var held))
            {
                byObjectId[user.AzureAdId] = user;
                return false;
            }

            if (!ReferenceEquals(held, user) && IsPreferredRow(user, held, currentUpnOf?.Invoke(user.AzureAdId)))
            {
                byObjectId[user.AzureAdId] = user;
            }
            return true;
        }

        /// <summary>Indexes rows by object id, case-insensitively, choosing the canonical row for a shared one.</summary>
        public static Dictionary<string, Common.Entities.User> IndexByObjectId(IEnumerable<Common.Entities.User> users, Func<string, string> currentUpnOf)
        {
            var byObjectId = new Dictionary<string, Common.Entities.User>(StringComparer.OrdinalIgnoreCase);
            if (users != null)
            {
                foreach (var user in users)
                {
                    AddByObjectId(byObjectId, user, currentUpnOf);
                }
            }
            return byObjectId;
        }

        /// <summary>
        /// True when <paramref name="candidate"/> rather than <paramref name="held"/> should stand for their shared
        /// object id: the row with Graph's current sign-in name, or else the newest row (highest id) - the one a
        /// rename inserted.
        /// </summary>
        public static bool IsPreferredRow(Common.Entities.User candidate, Common.Entities.User held, string currentUpn)
        {
            if (!string.IsNullOrEmpty(currentUpn))
            {
                var candidateIsCurrent = string.Equals(candidate.UserPrincipalName, currentUpn, StringComparison.OrdinalIgnoreCase);
                var heldIsCurrent = string.Equals(held.UserPrincipalName, currentUpn, StringComparison.OrdinalIgnoreCase);
                if (candidateIsCurrent != heldIsCurrent)
                {
                    return candidateIsCurrent;
                }
            }
            return candidate.ID > held.ID;
        }

        /// <summary>
        /// True when <paramref name="row"/>, found by a manager's object id, carries a sign-in name other than the one
        /// Graph reports for them now: a row a rename left behind. Their current row is found by sign-in name instead.
        /// </summary>
        public static bool IsLeftBehindByRename(Common.Entities.User row, string currentUpn)
        {
            return row != null
                && !string.IsNullOrEmpty(currentUpn)
                && !string.Equals(row.UserPrincipalName, currentUpn, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The rows to mark disabled: every saved row, not already disabled, that carries the object id of an account
        /// the read reported disabled or deleted - a rename's left-behind row too - or, after a complete read of the
        /// directory, of an account that read did not list.
        /// </summary>
        /// <remarks>
        /// <c>UserGroupsFilter</c> deliberately plays no part. Deleting a user takes them out of every group, so a
        /// deleted member is always outside the scope by the time the import hears of it, and a disabled one is often
        /// removed from the groups as they leave. Marking their existing row disabled stores nothing new about them; it
        /// only stops the table claiming that someone who has gone can still sign in. Only rows that carry an object
        /// id are considered: that id was read from this directory, by this import or the agent-cost import's directory
        /// lookup. Rows other imports create by sign-in name alone have none, and are never touched.
        /// </remarks>
        public static UserRowsToDeactivate SelectRowsToDeactivate(IEnumerable<Common.Entities.User> dbUsers, GraphUserDeactivations deactivations)
        {
            var rows = new UserRowsToDeactivate();
            if (dbUsers == null || deactivations == null)
            {
                return rows;
            }

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in deactivations.DisabledObjectIds)
            {
                if (!string.IsNullOrEmpty(id)) reported.Add(id);
            }
            foreach (var id in deactivations.RemovedObjectIds)
            {
                if (!string.IsNullOrEmpty(id)) reported.Add(id);
            }
            if (reported.Count == 0 && !deactivations.IsCompleteDirectoryRead)
            {
                return rows;
            }

            foreach (var user in dbUsers)
            {
                if (user == null || user.ID <= 0 || string.IsNullOrEmpty(user.AzureAdId))
                {
                    continue;
                }

                var listed = deactivations.CompleteReadIncludes(user.AzureAdId);
                if (listed)
                {
                    rows.ConfirmedByCompleteRead++;
                }
                if (user.AccountEnabled == false)
                {
                    continue;
                }

                if (reported.Contains(user.AzureAdId))
                {
                    rows.Reported.Add(new UserRowDeactivation(user.ID, user.AzureAdId));
                }
                else if (deactivations.IsCompleteDirectoryRead && !listed)
                {
                    rows.NoLongerInDirectory.Add(new UserRowDeactivation(user.ID, user.AzureAdId));
                }
            }
            return rows;
        }
    }

    /// <summary>What <see cref="UserIdentityRules.SelectRowsToDeactivate"/> chose, kept apart so each can be reported.</summary>
    internal sealed class UserRowsToDeactivate
    {
        /// <summary>Rows of accounts the read reported disabled or deleted.</summary>
        public List<UserRowDeactivation> Reported { get; } = new List<UserRowDeactivation>();

        /// <summary>Rows of accounts a complete read of the directory did not list: deleted, whenever that was.</summary>
        public List<UserRowDeactivation> NoLongerInDirectory { get; } = new List<UserRowDeactivation>();

        /// <summary>Rows with an object id that the complete read did list, disabled or not.</summary>
        public int ConfirmedByCompleteRead { get; set; }
    }

    /// <summary>Whether a complete read of the directory can be trusted to mean "deleted" for the accounts it left out.</summary>
    internal enum DirectoryReadVerdict
    {
        /// <summary>It listed as many users as Graph counts in the directory, within <see cref="DirectoryReadCompleteness.Tolerance"/>.</summary>
        MatchesDirectoryCount,

        /// <summary>Graph's count could not be read, and the read confirmed at least as many of the table's accounts as it left out.</summary>
        ConfirmedByUsersTable,

        /// <summary>It listed clearly fewer users than Graph counts: it may have missed some.</summary>
        ShortOfDirectoryCount,

        /// <summary>It listed clearly more users than Graph counts: one of the two is wrong, so neither proves anything.</summary>
        BeyondDirectoryCount,

        /// <summary>Graph's count could not be read, and the read left out more of the table's accounts than it confirmed.</summary>
        LeftOutMoreThanConfirmed,

        /// <summary>It listed nobody at all.</summary>
        EmptyRead,
    }

    /// <summary>
    /// Decides whether absence from a complete read of the directory is evidence of deletion.
    /// </summary>
    /// <remarks>
    /// A read that ended in its deltaLink but listed only part of the directory - a page that came back without its
    /// <c>value</c>, say - would otherwise disable everyone it missed, and an incremental read never turns them back on.
    /// The read is therefore checked against Graph's own count of the directory's users, which nothing in the users
    /// table can skew: the first complete read after an upgrade can rightly leave out more old leavers than there are
    /// people today. Only when that count cannot be read does it fall back to the users table, refusing when the read
    /// left out more of the table's accounts than it confirmed - safe, but that backlog would keep it refusing.
    /// </remarks>
    internal static class DirectoryReadCompleteness
    {
        /// <summary>
        /// How far the read and the count may differ: 1%, and never less than 10 users. Graph's <c>$count</c> is
        /// eventually consistent, and the directory changes while a large read is paged.
        /// </summary>
        public static int Tolerance(int usersInDirectory) => (int)Math.Max(10L, ((long)usersInDirectory + 99) / 100);

        /// <param name="usersInRead">Accounts the complete read listed.</param>
        /// <param name="usersInDirectory">Graph's count of the directory's users, or null when it could not be read.</param>
        /// <param name="leftOut">Enabled rows with an object id the read did not list.</param>
        /// <param name="confirmed">Rows with an object id the read did list.</param>
        public static DirectoryReadVerdict Judge(int usersInRead, int? usersInDirectory, int leftOut, int confirmed)
        {
            if (usersInRead <= 0)
            {
                return DirectoryReadVerdict.EmptyRead;
            }

            if (usersInDirectory.HasValue)
            {
                var tolerance = Tolerance(usersInDirectory.Value);
                if ((long)usersInRead < (long)usersInDirectory.Value - tolerance)
                {
                    return DirectoryReadVerdict.ShortOfDirectoryCount;
                }
                if ((long)usersInRead > (long)usersInDirectory.Value + tolerance)
                {
                    return DirectoryReadVerdict.BeyondDirectoryCount;
                }
                return DirectoryReadVerdict.MatchesDirectoryCount;
            }

            return leftOut <= confirmed ? DirectoryReadVerdict.ConfirmedByUsersTable : DirectoryReadVerdict.LeftOutMoreThanConfirmed;
        }

        /// <summary>True for the verdicts under which the accounts the read left out are marked disabled.</summary>
        public static bool IsTrusted(DirectoryReadVerdict verdict)
            => verdict == DirectoryReadVerdict.MatchesDirectoryCount || verdict == DirectoryReadVerdict.ConfirmedByUsersTable;
    }
}
