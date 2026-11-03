using Common.Entities;
using DataUtils.Sql;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>Why a user's identity could not be matched safely to a users row.</summary>
    internal enum UserIdentityConflictKind
    {
        /// <summary>The user's new sign-in name is on a row that carries another account's object id.</summary>
        DestinationOtherObjectId,

        /// <summary>Graph listed the object id under two sign-in names, or two object ids under one sign-in name.</summary>
        ConflictingGraphNames,

        /// <summary>The new sign-in name is longer than <c>users.user_name</c> holds.</summary>
        UpnTooLong,

        /// <summary>The row, or the new sign-in name, changed between reading the users table and writing the rename.</summary>
        ChangedDuringRename,
    }

    /// <summary>One user the import could not match safely. Row ids and the object id only: never a sign-in name.</summary>
    internal sealed class UserIdentityConflict
    {
        public UserIdentityConflict(string objectId, UserIdentityConflictKind kind, IReadOnlyList<int> sourceRowIds, int? destinationRowId)
        {
            ObjectId = objectId;
            Kind = kind;
            SourceRowIds = sourceRowIds ?? new int[0];
            DestinationRowId = destinationRowId;
        }

        public string ObjectId { get; }
        public UserIdentityConflictKind Kind { get; }

        /// <summary>The rows already carrying the user's object id.</summary>
        public IReadOnlyList<int> SourceRowIds { get; }

        /// <summary>The row holding the user's new sign-in name, when there is one.</summary>
        public int? DestinationRowId { get; }

        public override string ToString()
            => $"{ObjectId} {Kind} source row(s) [{string.Join(",", SourceRowIds)}] destination row {(DestinationRowId.HasValue ? DestinationRowId.Value.ToString() : "none")}";
    }

    /// <summary>One users row to rename in place.</summary>
    internal sealed class PlannedUserRename
    {
        public PlannedUserRename(string objectId, User row, string newUpn, int round, IReadOnlyList<int> rowsWithObjectId)
        {
            ObjectId = objectId;
            Row = row;
            NewUpn = newUpn;
            Round = round;
            RowsWithObjectId = rowsWithObjectId;
        }

        public string ObjectId { get; }
        public User Row { get; }
        public string NewUpn { get; }

        /// <summary>
        /// 0 when the new name is free; otherwise one more than the round of the rename that frees it (a chain:
        /// A takes B's old name once B has moved on). Rounds are written in order.
        /// </summary>
        public int Round { get; }

        public IReadOnlyList<int> RowsWithObjectId { get; }
    }

    /// <summary>What <see cref="UserRenameProcessor.Plan"/> decided, before anything is written.</summary>
    internal sealed class UserRenamePlan
    {
        private readonly Dictionary<string, UserIdentityConflict> _conflicts = new Dictionary<string, UserIdentityConflict>(StringComparer.OrdinalIgnoreCase);

        public List<PlannedUserRename> Renames { get; } = new List<PlannedUserRename>();

        /// <summary>
        /// Users whose new sign-in name is on a row with no object id - a row another import created from activity it
        /// saw under that name. That row is matched by name and given the object id by the metadata update, as before
        /// in-place renames existed; the user's earlier rows keep their history.
        /// </summary>
        public int PlaceholdersAdopted { get; set; }

        /// <summary>By object id.</summary>
        public IReadOnlyDictionary<string, UserIdentityConflict> Conflicts => _conflicts;

        internal void AddConflict(UserIdentityConflict conflict)
        {
            if (!_conflicts.ContainsKey(conflict.ObjectId))
            {
                _conflicts.Add(conflict.ObjectId, conflict);
            }
        }

        internal bool IsConflicted(string objectId) => _conflicts.ContainsKey(objectId);
    }

    /// <summary>What reconciling this read's identities with the users table did.</summary>
    internal sealed class UserIdentityReconciliation
    {
        public int RenamesApplied { get; set; }

        public int ObsoleteNamesReleased { get; set; }

        /// <summary>Renames applied to the newest of several rows carrying the object id.</summary>
        public int RenamedFromSeveralRows { get; set; }

        /// <summary>Renames applied after another rename in the same run freed their new name.</summary>
        public int ChainedRenames { get; set; }

        public int PlaceholdersAdopted { get; set; }

        public long ElapsedMs { get; set; }

        /// <summary>The users quarantined this cycle, by object id.</summary>
        public Dictionary<string, UserIdentityConflict> Conflicts { get; } = new Dictionary<string, UserIdentityConflict>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Keeps a renamed user on the users row they already have, instead of starting a second one under the new name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A user whose object id already has a row, and whose new sign-in name has none, has that row renamed in place, so
    /// every user id and all history stay as they were. When several rows carry the object id (renames by earlier
    /// versions, or a placeholder adopted below), the newest is renamed - the row <see cref="UserIdentityRules.IsPreferredRow(User, User, string)"/>
    /// treats as theirs everywhere else - and the older ones stay as history.
    /// </para>
    /// <para>
    /// When the new name is on a row with no object id, that row is a placeholder: the audit, Copilot, Power Platform,
    /// web-traffic, calls and usage-report imports all add a row by sign-in name alone the first time they see someone.
    /// It is left for the metadata update to adopt, which gives it the object id, exactly as before in-place renames
    /// existed. Merging it into the original row would mean repointing every user-keyed reference and resolving per-user,
    /// per-day unique keys, which is deferred.
    /// </para>
    /// <para>
    /// A reused name can be released only after fresh, bounded directory verification establishes the current owner
    /// and the previous object's deletion/absence. The previous row is disabled and renamed to a reserved historical
    /// name, keeping its object id, primary key and all references. Assigning the current owner's existing row, or
    /// creating their separate row, is in the same guarded transaction. No distinct object ids are ever merged.
    /// </para>
    /// <para>
    /// Anything else stays ambiguous - the name is held by a row with another object id (an unverified reused name,
    /// or two users swapping names), Graph named the user twice, or the row changed while it was being written - and
    /// only those users are quarantined: nothing that depends on their identity is written, everyone else is imported,
    /// and the caller keeps their latest Graph profile so they are re-checked on every cycle. A chain of renames in one
    /// read (A takes B's old name, B moves on) is not ambiguous: B is renamed first.
    /// </para>
    /// </remarks>
    internal static class UserRenameProcessor
    {
        /// <summary><c>users.user_name</c> is <c>varchar(250)</c>.</summary>
        internal const int MaxUpnLength = 250;

        /// <summary>The first conflicts written to the log, by id.</summary>
        internal const int ConflictsLogged = 20;

        internal const int MaxReusedNamesVerifiedPerCycle = 50;
        private static int _verificationRound;

        /// <summary>Decides what to rename and what to quarantine, from rows already loaded. No SQL, no Graph.</summary>
        public static UserRenamePlan Plan(IEnumerable<GraphUser> graphUsers, IEnumerable<User> dbUsers)
        {
            var byObjectId = new Dictionary<string, List<User>>(StringComparer.OrdinalIgnoreCase);
            var byUpn = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in dbUsers ?? Enumerable.Empty<User>())
            {
                if (user == null)
                    continue;
                if (!string.IsNullOrEmpty(user.UserPrincipalName))
                    byUpn[user.UserPrincipalName] = user;
                if (string.IsNullOrEmpty(user.AzureAdId))
                    continue;
                if (!byObjectId.TryGetValue(user.AzureAdId, out var rows))
                    byObjectId[user.AzureAdId] = rows = new List<User>();
                rows.Add(user);
            }

            var plan = new UserRenamePlan();
            var nameOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ownerOfName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<KeyValuePair<GraphUser, List<User>>>();
            foreach (var user in graphUsers ?? Enumerable.Empty<GraphUser>())
            {
                if (user == null || string.IsNullOrEmpty(user.Id) || string.IsNullOrEmpty(user.UserPrincipalName))
                    continue;
                if (nameOf.TryGetValue(user.Id, out var priorUpn))
                {
                    if (!string.Equals(priorUpn, user.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
                        plan.AddConflict(new UserIdentityConflict(user.Id, UserIdentityConflictKind.ConflictingGraphNames, RowIds(byObjectId, user.Id), null));
                    continue;
                }
                nameOf.Add(user.Id, user.UserPrincipalName);

                if (ownerOfName.TryGetValue(user.UserPrincipalName, out var otherObjectId))
                {
                    byUpn.TryGetValue(user.UserPrincipalName, out var holder);
                    plan.AddConflict(new UserIdentityConflict(user.Id, UserIdentityConflictKind.ConflictingGraphNames, RowIds(byObjectId, user.Id), holder?.ID));
                    plan.AddConflict(new UserIdentityConflict(otherObjectId, UserIdentityConflictKind.ConflictingGraphNames, RowIds(byObjectId, otherObjectId), holder?.ID));
                    continue;
                }
                ownerOfName.Add(user.UserPrincipalName, user.Id);

                byObjectId.TryGetValue(user.Id, out var rowsWithObjectId);
                candidates.Add(new KeyValuePair<GraphUser, List<User>>(user, rowsWithObjectId ?? new List<User>()));
            }

            var renamingRow = new Dictionary<int, PlannedUserRename>();
            var blocked = new List<Tuple<GraphUser, List<User>, User>>();
            foreach (var candidate in candidates)
            {
                var user = candidate.Key;
                if (plan.IsConflicted(user.Id))
                    continue;

                if (byUpn.TryGetValue(user.UserPrincipalName, out var destination))
                {
                    if (string.Equals(destination.AzureAdId, user.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.IsNullOrEmpty(destination.AzureAdId))
                    {
                        plan.PlaceholdersAdopted++;
                        continue;
                    }
                    blocked.Add(Tuple.Create(user, candidate.Value, destination));
                    continue;
                }

                if (user.UserPrincipalName.Length > MaxUpnLength)
                {
                    plan.AddConflict(new UserIdentityConflict(user.Id, UserIdentityConflictKind.UpnTooLong, Ids(candidate.Value), null));
                    continue;
                }

                if (candidate.Value.Count > 0)
                    AddRename(plan, renamingRow, user, candidate.Value, round: 0);
            }

            // A chain: the new name is held by a row this run renames away first. Later rounds only ever depend on
            // earlier ones, so a cycle (two users swapping names) never resolves and stays a conflict.
            for (var round = 1; blocked.Count > 0; round++)
            {
                var stillBlocked = new List<Tuple<GraphUser, List<User>, User>>();
                foreach (var waiting in blocked)
                {
                    if (waiting.Item2.Count > 0 && renamingRow.TryGetValue(waiting.Item3.ID, out var freeing) && freeing.Round < round)
                        AddRename(plan, renamingRow, waiting.Item1, waiting.Item2, round);
                    else
                        stillBlocked.Add(waiting);
                }
                if (stillBlocked.Count == blocked.Count)
                    break;
                blocked = stillBlocked;
            }

            foreach (var waiting in blocked)
            {
                plan.AddConflict(new UserIdentityConflict(waiting.Item1.Id, UserIdentityConflictKind.DestinationOtherObjectId, Ids(waiting.Item2), waiting.Item3.ID));
            }
            return plan;
        }

        private static void AddRename(UserRenamePlan plan, Dictionary<int, PlannedUserRename> renamingRow, GraphUser user, List<User> rows, int round)
        {
            var source = rows[0];
            for (var i = 1; i < rows.Count; i++)
            {
                if (UserIdentityRules.IsPreferredRow(rows[i], source, user.UserPrincipalName))
                    source = rows[i];
            }
            var rename = new PlannedUserRename(user.Id, source, user.UserPrincipalName, round, Ids(rows));
            plan.Renames.Add(rename);
            renamingRow[source.ID] = rename;
        }

        private static IReadOnlyList<int> RowIds(Dictionary<string, List<User>> byObjectId, string objectId)
            => byObjectId.TryGetValue(objectId, out var rows) ? Ids(rows) : new int[0];

        private static IReadOnlyList<int> Ids(List<User> rows)
        {
            var ids = new int[rows.Count];
            for (var i = 0; i < rows.Count; i++)
                ids[i] = rows[i].ID;
            Array.Sort(ids);
            return ids;
        }

        /// <summary>
        /// Plans and writes the renames, and reports which users must be quarantined. Never fails the import over an
        /// identity it cannot match; a SQL failure or a lost writer lock still throws.
        /// </summary>
        public static async Task<UserIdentityReconciliation> ReconcileAsync(string connectionString, IEnumerable<GraphUser> graphUsers,
            List<User> dbUsers, ILogger logger, Func<Task> beforeWrite,
            Func<GraphUser, User, Task<bool>> verifyObsoleteOwner = null)
        {
            var timer = Stopwatch.StartNew();
            var profiles = graphUsers.ToList();
            var plan = Plan(profiles, dbUsers);
            var result = new UserIdentityReconciliation { PlaceholdersAdopted = plan.PlaceholdersAdopted };
            foreach (var conflict in plan.Conflicts.Values)
                result.Conflicts[conflict.ObjectId] = conflict;

            if (plan.Renames.Count > 0)
                await ApplyAsync(connectionString, plan, result, logger, beforeWrite);

            // A newcomer can take a name freed by an ordinary rename, but only if that rename actually succeeded.
            // There is no source row to rename for this account; the normal insert phase will create it.
            var rows = plan.Conflicts.Values.Any(c => c.Kind == UserIdentityConflictKind.DestinationOtherObjectId)
                ? dbUsers.ToDictionary(u => u.ID) : null;
            foreach (var conflict in plan.Conflicts.Values.Where(c => c.Kind == UserIdentityConflictKind.DestinationOtherObjectId && c.SourceRowIds.Count == 0))
            {
                var owner = profiles.First(u => string.Equals(u.Id, conflict.ObjectId, StringComparison.OrdinalIgnoreCase));
                if (!string.Equals(rows[conflict.DestinationRowId.Value].UserPrincipalName, owner.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
                    result.Conflicts.Remove(conflict.ObjectId);
            }

            if (verifyObsoleteOwner != null && result.Conflicts.Values.Any(c => c.Kind == UserIdentityConflictKind.DestinationOtherObjectId))
            {
                var namedObjectIds = new HashSet<string>(profiles.Select(u => u.Id), StringComparer.OrdinalIgnoreCase);
                var blocked = result.Conflicts.Values.Where(c => c.Kind == UserIdentityConflictKind.DestinationOtherObjectId)
                    .OrderBy(c => c.ObjectId, StringComparer.OrdinalIgnoreCase).ToList();
                // Rotate a bounded window so persistent ambiguous conflicts cannot starve the rest.
                var offset = blocked.Count == 0 ? 0
                    : (int)((long)(Interlocked.Increment(ref _verificationRound) & int.MaxValue) * MaxReusedNamesVerifiedPerCycle % blocked.Count);
                for (var index = 0; index < Math.Min(blocked.Count, MaxReusedNamesVerifiedPerCycle); index++)
                {
                    var conflict = blocked[(offset + index) % blocked.Count];
                    var destination = rows[conflict.DestinationRowId.Value];
                    // A chain or swap, including a disabled live owner, is never a retirement.
                    if (namedObjectIds.Contains(destination.AzureAdId))
                        continue;
                    var owner = profiles.First(u => string.Equals(u.Id, conflict.ObjectId, StringComparison.OrdinalIgnoreCase));
                    if (owner.AccountEnabled != true || !Guid.TryParse(destination.AzureAdId, out var oldId)
                        || !await verifyObsoleteOwner(owner, destination))
                        continue;
                    var source = conflict.SourceRowIds.Select(id => rows[id])
                        .OrderByDescending(u => u.ID).FirstOrDefault();
                    await beforeWrite();
                    var assignedId = await ReleaseAndAssignAsync(connectionString, source, destination, owner, HistoricalName(destination.ID, oldId));
                    if (!assignedId.HasValue)
                    {
                        result.Conflicts[owner.Id] = new UserIdentityConflict(owner.Id, UserIdentityConflictKind.ChangedDuringRename,
                            conflict.SourceRowIds, destination.ID);
                        continue;
                    }
                    destination.UserPrincipalName = HistoricalName(destination.ID, oldId);
                    destination.AccountEnabled = false;
                    if (source == null)
                    {
                        source = new User { ID = assignedId.Value, AzureAdId = owner.Id };
                        dbUsers.Add(source);
                    }
                    source.UserPrincipalName = owner.UserPrincipalName;
                    result.Conflicts.Remove(owner.Id);
                    result.ObsoleteNamesReleased++;
                }
                if (blocked.Count > MaxReusedNamesVerifiedPerCycle)
                    logger.LogWarning("User import - reused UPN verification budget: at most {Limit} conflicts verified per cycle; deferred identities stay quarantined and the verification window rotates.",
                        MaxReusedNamesVerifiedPerCycle);
            }

            LogConflicts(result, logger);
            result.ElapsedMs = timer.ElapsedMilliseconds;
            return result;
        }

        // ':' is forbidden in Entra UPNs; '@...invalid' keeps generic domain/identity readers out of hash paths.
        internal static string HistoricalName(int rowId, Guid objectId)
            => $"retired:entra:{objectId:N}:row:{rowId.ToString(System.Globalization.CultureInfo.InvariantCulture)}@history.invalid";

        /// <summary>
        /// The name is never free outside this transaction. Both rows are guarded against the snapshot read before
        /// Graph verification; a concurrent refresh, rename or placeholder writer leaves the pair unchanged.
        /// </summary>
        internal static async Task<int?> ReleaseAndAssignAsync(string connectionString, User source, User destination,
            GraphUser owner, string historicalName)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandTimeout = 90;
                    command.Parameters.Add("@destination", SqlDbType.Int).Value = destination.ID;
                    command.Parameters.Add("@previousObject", SqlDbType.NVarChar, -1).Value = destination.AzureAdId;
                    command.Parameters.Add("@desired", SqlDbType.VarChar, MaxUpnLength).Value = owner.UserPrincipalName;
                    command.Parameters.Add("@historical", SqlDbType.VarChar, MaxUpnLength).Value = historicalName;
                    command.Parameters.Add("@destinationUpdated", SqlDbType.DateTime).Value = (object)destination.LastUpdated ?? DBNull.Value;
                    command.Parameters.Add("@destinationEnabled", SqlDbType.Bit).Value = (object)destination.AccountEnabled ?? DBNull.Value;
                    command.Parameters.Add("@source", SqlDbType.Int).Value = (object)source?.ID ?? DBNull.Value;
                    command.Parameters.Add("@old", SqlDbType.VarChar, MaxUpnLength).Value = (object)source?.UserPrincipalName ?? DBNull.Value;
                    command.Parameters.Add("@object", SqlDbType.NVarChar, -1).Value = owner.Id;
                    command.Parameters.Add("@created", SqlDbType.DateTime2).Value = (object)owner.CreatedDateTime ?? DBNull.Value;
                    command.Parameters.Add("@sourceUpdated", SqlDbType.DateTime).Value = (object)source?.LastUpdated ?? DBNull.Value;
                    command.Parameters.Add("@sourceEnabled", SqlDbType.Bit).Value = (object)source?.AccountEnabled ?? DBNull.Value;
                    command.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
    WHERE id = @destination AND user_name = @desired AND azure_ad_id = @previousObject
      AND (last_updated = @destinationUpdated OR (last_updated IS NULL AND @destinationUpdated IS NULL))
      AND (account_enabled = @destinationEnabled OR (account_enabled IS NULL AND @destinationEnabled IS NULL)))
    RETURN;
IF EXISTS (SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK) WHERE user_name = @historical)
    RETURN;
IF @source IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.users WITH (UPDLOCK, HOLDLOCK)
    WHERE id = @source AND user_name = @old AND azure_ad_id = @object
      AND (last_updated = @sourceUpdated OR (last_updated IS NULL AND @sourceUpdated IS NULL))
      AND (account_enabled = @sourceEnabled OR (account_enabled IS NULL AND @sourceEnabled IS NULL)))
    RETURN;
UPDATE dbo.users SET user_name = @historical, account_enabled = 0, last_updated = GETUTCDATE()
WHERE id = @destination;
IF @source IS NULL
BEGIN
    INSERT dbo.users (user_name, azure_ad_id, account_enabled, created_utc) VALUES (@desired, @object, 1, @created);
    SELECT CAST(SCOPE_IDENTITY() AS int);
END
ELSE
BEGIN
    UPDATE dbo.users SET user_name = @desired WHERE id = @source;
    SELECT @source;
END";
                    var assigned = await command.ExecuteScalarAsync();
                    if (assigned == null || assigned == DBNull.Value)
                    {
                        transaction.Rollback();
                        return null;
                    }
                    transaction.Commit();
                    return (int)assigned;
                }
            }
        }

        private static async Task ApplyAsync(string connectionString, UserRenamePlan plan, UserIdentityReconciliation result, ILogger logger, Func<Task> beforeWrite)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var round in plan.Renames.GroupBy(r => r.Round).OrderBy(g => g.Key))
                {
                    var renames = round.ToList();

                    // Four parameters per rename; seek the existing PK and the UPN unique index, never the LOB object-id column.
                    const int batchSize = 400;
                    for (var start = 0; start < renames.Count; start += batchSize)
                    {
                        await beforeWrite();
                        var batch = renames.GetRange(start, Math.Min(batchSize, renames.Count - start));
                        var updated = await RenameBatchAsync(connection, batch);

                        var notRenamed = new List<int>();
                        foreach (var rename in batch)
                        {
                            if (updated.Contains(rename.Row.ID))
                            {
                                rename.Row.UserPrincipalName = rename.NewUpn;
                                result.RenamesApplied++;
                                if (rename.RowsWithObjectId.Count > 1)
                                    result.RenamedFromSeveralRows++;
                                if (rename.Round > 0)
                                    result.ChainedRenames++;
                            }
                            else
                            {
                                notRenamed.Add(rename.Row.ID);
                                if (!result.Conflicts.ContainsKey(rename.ObjectId))
                                    result.Conflicts[rename.ObjectId] = new UserIdentityConflict(rename.ObjectId, UserIdentityConflictKind.ChangedDuringRename,
                                        rename.RowsWithObjectId, null);
                            }
                        }

                        if (notRenamed.Count > 0)
                        {
                            logger.LogWarning("User import - rename batch partly applied: attempted {Attempted}, updated {Updated}. Row ids not renamed: {RowIds}{More}. " +
                                "Each of those rows, or its new sign-in name, changed after the users table was read - usually another import adding a row under the new name. " +
                                "Their users are quarantined and re-checked on the next cycle; nothing was overwritten.",
                                batch.Count, updated.Count, string.Join(",", notRenamed.Take(ConflictsLogged)),
                                notRenamed.Count > ConflictsLogged ? $" and {notRenamed.Count - ConflictsLogged} more" : string.Empty);
                        }
                    }
                }
            }
        }

        private static async Task<HashSet<int>> RenameBatchAsync(Microsoft.Data.SqlClient.SqlConnection connection, List<PlannedUserRename> batch)
        {
            var updated = new HashSet<int>();
            using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = 90;
                var values = new List<string>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    values.Add($"(@id{i}, @old{i}, @new{i}, @object{i})");
                    command.Parameters.Add($"@id{i}", SqlDbType.Int).Value = batch[i].Row.ID;
                    command.Parameters.Add($"@old{i}", SqlDbType.VarChar, MaxUpnLength).Value = batch[i].Row.UserPrincipalName;
                    command.Parameters.Add($"@new{i}", SqlDbType.VarChar, MaxUpnLength).Value = batch[i].NewUpn;
                    command.Parameters.Add($"@object{i}", SqlDbType.NVarChar, -1).Value = batch[i].Row.AzureAdId;
                }
                command.CommandText = @"
UPDATE u
SET user_name = names.new_upn
OUTPUT inserted.id
FROM dbo.users u WITH (UPDLOCK, HOLDLOCK)
JOIN (VALUES " + string.Join(",", values) + @") names(id, old_upn, new_upn, object_id) ON names.id = u.id
WHERE u.user_name = names.old_upn AND u.azure_ad_id = names.object_id
  AND NOT EXISTS (SELECT 1 FROM dbo.users target WITH (UPDLOCK, HOLDLOCK)
                  WHERE target.user_name = names.new_upn AND target.id <> u.id);";
                using (var reader = await command.ExecuteReaderAsync())
                    while (await reader.ReadAsync())
                        updated.Add(reader.GetInt32(0));
            }
            return updated;
        }

        /// <summary>
        /// One line naming every kind of conflict and the first <see cref="ConflictsLogged"/> by id, so an operator can
        /// find the rows involved. Never sign-in names.
        /// </summary>
        private static void LogConflicts(UserIdentityReconciliation result, ILogger logger)
        {
            if (result.Conflicts.Count == 0)
                return;

            int Count(UserIdentityConflictKind kind) => result.Conflicts.Values.Count(c => c.Kind == kind);
            var first = result.Conflicts.Values
                .OrderBy(c => c.Kind)
                .ThenBy(c => c.ObjectId, StringComparer.OrdinalIgnoreCase)
                .Take(ConflictsLogged)
                .Select(c => c.ToString());
            logger.LogWarning("User import - identity conflicts: {Total} user(s) quarantined (destinationOtherObjectId={DestinationOtherObjectId}, " +
                "conflictingGraphNames={ConflictingGraphNames}, upnTooLong={UpnTooLong}, changedDuringRename={ChangedDuringRename}). " +
                "Nothing that depends on their identity is written for them; everyone else is imported, and they are re-checked every cycle. First {Shown}: {Conflicts}",
                result.Conflicts.Count,
                Count(UserIdentityConflictKind.DestinationOtherObjectId),
                Count(UserIdentityConflictKind.ConflictingGraphNames),
                Count(UserIdentityConflictKind.UpnTooLong),
                Count(UserIdentityConflictKind.ChangedDuringRename),
                Math.Min(ConflictsLogged, result.Conflicts.Count),
                string.Join("; ", first));
        }
    }
}
