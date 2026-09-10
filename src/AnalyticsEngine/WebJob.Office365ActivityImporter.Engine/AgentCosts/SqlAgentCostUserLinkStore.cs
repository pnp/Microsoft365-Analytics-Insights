using Common.Entities;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.AgentCosts
{
    /// <summary>
    /// Links per-user credit rows to <c>dbo.users</c> in SQL.
    /// </summary>
    /// <remarks>
    /// <para>Matching is on <c>dbo.users.azure_ad_id</c>, which the user import fills from Graph's
    /// <c>user.id</c> - the same value the licensing API reports as <c>userId</c>. No new column and no
    /// backfill are needed; the join was always available.</para>
    ///
    /// <para><b>Cost note.</b> <c>azure_ad_id</c> is <c>nvarchar(max)</c> and therefore cannot be indexed,
    /// so each chunk below is a scan of <c>dbo.users</c>. That is why the filter is pushed into SQL rather
    /// than loading the table and matching in memory: one scan returning a handful of rows beats
    /// transferring 200,000. At the design target this runs once a day over a few chunks, which is
    /// acceptable; making it a seek would mean narrowing the column, and that is a measured change in its
    /// own right rather than something to smuggle in here.</para>
    /// </remarks>
    public class SqlAgentCostUserLinkStore : IAgentCostUserLinkStore
    {
        /// <summary>
        /// Object ids per query. Well inside SQL Server's 2,100-parameter limit, which an EF
        /// <c>Contains</c> translates each element into.
        /// </summary>
        private const int LookupChunkSize = 500;

        private readonly IAnalyticsDbContextFactory _dbContextFactory;
        private readonly ILogger _logger;

        public SqlAgentCostUserLinkStore(IAnalyticsDbContextFactory dbContextFactory, ILogger logger)
        {
            _dbContextFactory = dbContextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
            _logger = logger;
        }

        public async Task<IReadOnlyDictionary<string, int>> GetUserIdsByObjectIdAsync(IReadOnlyCollection<string> objectIds)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (objectIds == null || objectIds.Count == 0) return result;

            var distinct = objectIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            using (var db = _dbContextFactory.Create())
            {
                // GetRange, not Skip/Take: Skip on a List walks the skipped elements every call, which turns
                // chunking into quadratic work on a large tenant.
                for (var i = 0; i < distinct.Count; i += LookupChunkSize)
                {
                    var chunk = distinct.GetRange(i, Math.Min(LookupChunkSize, distinct.Count - i));

                    // No ToLower(): SQL Server's default collation here is already case-insensitive, and
                    // LOWER() on the column would make the predicate non-SARGable for no benefit.
                    var matches = await db.users
                        .Where(u => chunk.Contains(u.AzureAdId))
                        .Select(u => new { u.ID, u.AzureAdId })
                        .ToListAsync();

                    foreach (var match in matches)
                    {
                        if (!string.IsNullOrWhiteSpace(match.AzureAdId)) result[match.AzureAdId] = match.ID;
                    }
                }
            }

            return result;
        }

        public async Task<int> EnsureUserAsync(EntraUserRef user)
        {
            if (user == null) throw new ArgumentNullException(nameof(user));
            if (string.IsNullOrWhiteSpace(user.UserPrincipalName))
            {
                throw new ArgumentException("A user cannot be created without a user principal name.", nameof(user));
            }

            var upn = user.UserPrincipalName.Trim();

            using (var db = _dbContextFactory.Create())
            {
                // UPN first. Users arrive in dbo.users from several places - the audit-activity staging merge
                // inserts them by user name with no object id - so someone can easily already be here with an
                // empty azure_ad_id. Inserting on object id alone would duplicate them.
                var existing = await db.users.FirstOrDefaultAsync(u => u.UserPrincipalName == upn);

                if (existing != null)
                {
                    // Fill in the object id if it was missing, so this user never needs a directory call
                    // again - and so every other feature that wants to match on it can.
                    if (string.IsNullOrWhiteSpace(existing.AzureAdId) && !string.IsNullOrWhiteSpace(user.ObjectId))
                    {
                        existing.AzureAdId = user.ObjectId;
                        await db.SaveChangesAsync();
                    }

                    return existing.ID;
                }

                var created = new User
                {
                    UserPrincipalName = upn,
                    AzureAdId = user.ObjectId ?? string.Empty,
                    Mail = user.Mail ?? string.Empty,
                    AccountEnabled = user.AccountEnabled,
                };

                db.users.Add(created);
                await db.SaveChangesAsync();

                _logger?.LogInformation(
                    "Agent costs - added a user who had billed Copilot Studio credits but was not in the "
                    + "database yet. The next user import will fill in their department, manager and licences.");

                return created.ID;
            }
        }

        public async Task<IReadOnlyList<string>> GetUnresolvedObjectIdsAsync(int max)
        {
            if (max <= 0) return new List<string>();

            using (var db = _dbContextFactory.Create())
            {
                return await db.CopilotStudioCreditUserDaily
                    .Where(r => r.UserId == null && r.EntraObjectId != null && r.EntraObjectId != "")
                    .OrderByDescending(r => r.UsageDate)
                    .Select(r => r.EntraObjectId)
                    .Distinct()
                    .Take(max)
                    .ToListAsync();
            }
        }

        public async Task<int> ApplyUserLinksAsync(IReadOnlyDictionary<string, int> userIdsByObjectId)
        {
            if (userIdsByObjectId == null || userIdsByObjectId.Count == 0) return 0;

            var updated = 0;

            using (var db = _dbContextFactory.Create())
            {
                var objectIds = userIdsByObjectId.Keys.ToList();

                for (var i = 0; i < objectIds.Count; i += LookupChunkSize)
                {
                    var chunk = objectIds.GetRange(i, Math.Min(LookupChunkSize, objectIds.Count - i));

                    var rows = await db.CopilotStudioCreditUserDaily
                        .Where(r => r.UserId == null && chunk.Contains(r.EntraObjectId))
                        .ToListAsync();

                    foreach (var row in rows)
                    {
                        if (row.EntraObjectId != null && userIdsByObjectId.TryGetValue(row.EntraObjectId, out var userId))
                        {
                            row.UserId = userId;
                            updated++;
                        }
                    }
                }

                if (updated > 0) await db.SaveChangesAsync();
            }

            return updated;
        }
    }
}
