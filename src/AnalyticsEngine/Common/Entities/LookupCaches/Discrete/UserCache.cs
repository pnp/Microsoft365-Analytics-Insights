using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class UserCache : DBLookupCache<User>
    {
        public UserCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<User> EntityStore => this.DB.users;

        public async Task<User> GetOrCreateUser(string username, bool v)
        {
            return await GetOrCreateNewResource(username, new User { UserPrincipalName = username }, v);
        }

        public async override Task<User> Load(string upn)
        {
            // Use FirstOrDefaultAsync instead of SingleOrDefaultAsync to handle existing duplicate records gracefully
            // Order by ID to ensure consistent results if duplicates exist
            return await EntityStore.Where(t => t.UserPrincipalName == upn).OrderBy(t => t.ID).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Resolves up to <see cref="DBLookupCache{T}.MaxKeysPerIdBatch"/> UPNs to user ids in ONE query, for the
        /// daily usage-report save (#705), which used to send <see cref="Load"/> once per user, per report.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Matches exactly as <see cref="Load"/> does, because SQL Server does the matching in both: each key is an
        /// <c>nvarchar</c> parameter, as EF sends <see cref="Load"/>'s, compared with <c>=</c> under the column's
        /// collation (case-insensitive, trailing spaces ignored), and duplicate UPNs resolve to the lowest id
        /// (<c>MIN(id)</c> here, <c>OrderBy(ID).First</c> there). The result comes back by the key's POSITION in
        /// the request, not by its text, so there is no second, in-memory comparison that could disagree with
        /// the collation.
        /// </para>
        /// <para>
        /// Each query is one hash join of the requested keys against <c>users</c>. Where <c>user_name</c> is
        /// <c>varchar</c> under a SQL collation (the Azure SQL default) the <c>nvarchar</c> comparison cannot seek
        /// <c>IX_users</c> - which is also true of <see cref="Load"/>, so the old path scanned that index once per
        /// user; this scans it once per 1,000. Measured before/after: see <c>PreResolveLookupIdsAsync</c> on
        /// <c>AbstractDailyActivityLoader</c>.
        /// </para>
        /// </remarks>
        public override async Task<IReadOnlyDictionary<string, int>> LoadExistingIdsAsync(IReadOnlyList<string> keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            if (keys.Count > MaxKeysPerIdBatch)
            {
                throw new ArgumentOutOfRangeException(nameof(keys), $"At most {MaxKeysPerIdBatch} keys per call; got {keys.Count}.");
            }

            // No context (unit tests build the cache without one): nothing to query, so resolve key by key.
            if (DB == null)
            {
                return null;
            }

            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            if (keys.Count == 0)
            {
                return found;
            }

            var sql = new StringBuilder("SELECT k.i AS KeyIndex, MIN(u.id) AS Id FROM (VALUES ");
            var parameters = new object[keys.Count];
            using (var parameterFactory = DB.Database.Connection.CreateCommand())
            {
                for (var i = 0; i < keys.Count; i++)
                {
                    if (i > 0)
                    {
                        sql.Append(',');
                    }
                    sql.Append('(').Append(i).Append(", @k").Append(i).Append(')');

                    var parameter = parameterFactory.CreateParameter();
                    parameter.ParameterName = "@k" + i;
                    parameter.DbType = DbType.String;
                    parameter.Size = keys[i] != null && keys[i].Length > 4000 ? -1 : 4000;
                    parameter.Value = (object)keys[i] ?? DBNull.Value;
                    parameters[i] = parameter;
                }
            }
            sql.Append(") AS k(i, v) INNER JOIN dbo.users AS u ON u.user_name = k.v GROUP BY k.i");

            var rows = await DB.Database.SqlQuery<IdForKey>(sql.ToString(), parameters).ToListAsync();
            foreach (var row in rows)
            {
                found[keys[row.KeyIndex]] = row.Id;
            }
            return found;
        }

        /// <summary>One row of <see cref="LoadExistingIdsAsync"/>'s result. Public only so EF can materialise it.</summary>
        public sealed class IdForKey
        {
            public int KeyIndex { get; set; }
            public int Id { get; set; }
        }
    }
}
