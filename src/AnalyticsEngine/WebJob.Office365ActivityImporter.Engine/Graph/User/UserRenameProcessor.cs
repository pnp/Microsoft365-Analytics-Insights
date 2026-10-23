using Common.Entities;
using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal static class UserRenameProcessor
    {
        public static async Task<bool> RenameAsync(string connectionString, IEnumerable<GraphUser> graphUsers,
            List<User> dbUsers, ILogger logger, Func<Task> beforeWrite)
        {
            var byObjectId = new Dictionary<string, List<User>>(StringComparer.OrdinalIgnoreCase);
            var byUpn = new Dictionary<string, User>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in dbUsers)
            {
                if (!string.IsNullOrEmpty(user.UserPrincipalName))
                    byUpn[user.UserPrincipalName] = user;
                if (string.IsNullOrEmpty(user.AzureAdId))
                    continue;
                if (!byObjectId.TryGetValue(user.AzureAdId, out var rows))
                    byObjectId[user.AzureAdId] = rows = new List<User>();
                rows.Add(user);
            }

            var renames = new List<(User Row, string NewUpn)>();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var conflicts = 0;
            foreach (var user in graphUsers)
            {
                if (string.IsNullOrEmpty(user.Id) || string.IsNullOrEmpty(user.UserPrincipalName))
                    continue;
                if (seen.TryGetValue(user.Id, out var priorUpn))
                {
                    if (!string.Equals(priorUpn, user.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
                        conflicts++;
                    continue;
                }
                seen.Add(user.Id, user.UserPrincipalName);
                if (!byObjectId.TryGetValue(user.Id, out var rows))
                    continue;
                if (byUpn.TryGetValue(user.UserPrincipalName, out var current))
                {
                    if (!string.Equals(current.AzureAdId, user.Id, StringComparison.OrdinalIgnoreCase))
                        conflicts++;
                    continue;
                }
                if (rows.Count != 1 || user.UserPrincipalName.Length > 250)
                {
                    conflicts++;
                    continue;
                }
                renames.Add((rows[0], user.UserPrincipalName));
            }
            if (conflicts > 0)
            {
                logger.LogError("User import - {Count} ambiguous user rename(s): the current UPN is already stored for another row, " +
                    "multiple historical rows carry the same object id, or Graph returned conflicting names. No identities were merged or overwritten. " +
                    "Directory writes and checkpoint advancement are paused until an administrator resolves the conflicting identities.", conflicts);
                return false;
            }
            if (renames.Count == 0)
                return true;

            using (var connection = AzureSqlTokenAuth.CreateConnection(connectionString))
            {
                await connection.OpenAsync();
                // Four parameters per rename; seek the existing PK and the UPN unique index, never the LOB object-id column.
                const int batchSize = 400;
                for (var start = 0; start < renames.Count; start += batchSize)
                {
                    await beforeWrite();
                    var batch = renames.GetRange(start, Math.Min(batchSize, renames.Count - start));
                    var updated = new HashSet<int>();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandTimeout = 90;
                        var values = new List<string>(batch.Count);
                        for (var i = 0; i < batch.Count; i++)
                        {
                            values.Add($"(@id{i}, @old{i}, @new{i}, @object{i})");
                            command.Parameters.Add($"@id{i}", SqlDbType.Int).Value = batch[i].Row.ID;
                            command.Parameters.Add($"@old{i}", SqlDbType.VarChar, 250).Value = batch[i].Row.UserPrincipalName;
                            command.Parameters.Add($"@new{i}", SqlDbType.VarChar, 250).Value = batch[i].NewUpn;
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
                    foreach (var rename in batch)
                        if (updated.Contains(rename.Row.ID))
                            rename.Row.UserPrincipalName = rename.NewUpn;
                    if (updated.Count != batch.Count)
                    {
                        logger.LogError("User import - a user identity changed while its rename was being saved. Conflicting rows were not " +
                            "overwritten; the directory checkpoint is retained for retry. Earlier successful renames keep their original user ids and history.");
                        return false;
                    }
                }
            }
            logger.LogInformation("User import - renamed {Count} users in place, preserving user ids and all related history.", renames.Count);
            return true;
        }
    }
}
