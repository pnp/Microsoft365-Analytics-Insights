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

        /// <summary>
        /// The user with this UPN, or null. Users are matched exactly as the unique index <c>IX_users</c> matches them:
        /// by the column's collation (case-insensitive on every supported install), ignoring trailing spaces. Duplicate
        /// UPNs, possible only where that index is missing, resolve to the lowest id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The UPN is sent as <c>varchar</c>, which is what <c>users.user_name</c> is (#713). The EF model leaves that
        /// column Unicode, so a plain parameter goes as <c>nvarchar(4000)</c>, and <c>nvarchar</c> wins on data-type
        /// precedence: SQL Server converts the COLUMN to compare. Under a SQL collation - the Azure SQL Database
        /// default - that conversion cannot be turned into a seek, so every call scanned the whole of
        /// <c>IX_users</c>. A Windows collation could still seek, through a range computed from the converted value
        /// and a residual conversion; it is now a plain seek. This runs once for every UPN a cache has not seen yet:
        /// the get-or-create path for users who are not in SQL yet, and the user import caching each user it has just
        /// inserted.
        /// </para>
        /// <para>
        /// Users are matched exactly as the unique index matches them. Under a SQL collation <c>varchar</c> is compared
        /// with the code page's sort order - what <c>IX_users</c> uses - but the converted column was compared with
        /// Unicode rules, which treat ß as "ss" and æ as "ae". So the old query also matched a stored "straße@..." for
        /// "strasse@...", a row the unique index holds as a different user, and with both stored returned whichever was
        /// older. Under a Windows collation both forms compare by Unicode rules. This is exact for <see cref="Load"/>
        /// itself; <c>GetOrCreateNewResource</c> consults its in-process <c>ObjectByIdCache</c> first, whose
        /// linguistic, case-insensitive comparer also equates "ß" with "ss", so within one cache's lifetime the
        /// spelling seen first still decides the row for both.
        /// </para>
        /// <para>
        /// Measured with 200,000 synthetic users on LocalDB, before -&gt; after, median of 5 runs after a discarded
        /// first: 1,000 calls a run under the SQL collation, 20,000 under the Windows one. Server CPU is the statement's
        /// own <c>sys.dm_exec_query_stats</c> time per execution. Reproduce with <c>UpnLookupBenchmarkTests</c>.
        /// <code>
        ///   user_name collation           call   plan on IX_users     reads per call   server CPU per call   ms per 1,000 calls
        ///   SQL_Latin1_General_CP1_CI_AS  hit    Index Scan -> Seek   1,460 -> 6       17,367 -> 12.6 us     18,146 -> 401
        ///   SQL_Latin1_General_CP1_CI_AS  miss   Index Scan -> Seek   1,457 -> 3       17,374 -> 7.8 us      18,103 -> 303
        ///   Latin1_General_CI_AS          hit    Seek -> Seek             6 -> 6           56.0 -> 11.6 us        505 -> 361
        ///   Latin1_General_CI_AS          miss   Seek -> Seek             3 -> 3           30.0 -> 7.7 us         376 -> 296
        /// </code>
        /// </para>
        /// <para>
        /// A non-ASCII value is now converted to the column's code page by the client, exactly as SQL Server converted
        /// it when the row was inserted, so a value that could not be stored as given (Greek letters, say) finds the
        /// row it was stored as instead of none - and with it, a second import no longer fails on <c>IX_users</c>. Two
        /// values the code page cannot tell apart resolve to the same row, as the unique index already treats them.
        /// UPNs are ASCII by Entra policy, so this is not a tenant case.
        /// </para>
        /// </remarks>
        public async override Task<User> Load(string upn)
        {
            // Use FirstOrDefaultAsync instead of SingleOrDefaultAsync to handle existing duplicate records gracefully
            // Order by ID to ensure consistent results if duplicates exist
            return await EntityStore.Where(t => t.UserPrincipalName == DbFunctions.AsNonUnicode(upn)).OrderBy(t => t.ID).FirstOrDefaultAsync();
        }

        /// <summary>
        /// Resolves up to <see cref="DBLookupCache{T}.MaxKeysPerIdBatch"/> UPNs to user ids in ONE query, for the
        /// daily usage-report save (#705), which used to send <see cref="Load"/> once per user, per report.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Matches as <see cref="Load"/> does for every ASCII UPN, because SQL Server does the matching in both: each
        /// key here is an <c>nvarchar</c> parameter compared with <c>=</c> under the column's collation
        /// (case-insensitive, trailing spaces ignored), and duplicate UPNs resolve to the lowest id (<c>MIN(id)</c>
        /// here, <c>OrderBy(ID).First</c> there). <see cref="Load"/> sends <c>varchar</c> since #713; where that can
        /// make the two differ, for non-ASCII text only, is described below. The result comes back by the key's
        /// POSITION in the request, not by its text, so there is no second, in-memory comparison that could disagree
        /// with the collation.
        /// </para>
        /// <para>
        /// One query per batch. Where <c>user_name</c> is <c>varchar</c> under a SQL collation (the Azure SQL default)
        /// the <c>nvarchar</c> comparison cannot seek <c>IX_users</c>, so this scans it once per 1,000 keys (hash
        /// join); the per-user path it replaces scanned it once per user until #713 moved <see cref="Load"/> to
        /// <c>varchar</c>. Under a Windows collation it is a merge join over one ordered range scan. Measured
        /// before/after: see <c>PreResolveLookupIdsAsync</c> on <c>AbstractDailyActivityLoader</c>.
        /// </para>
        /// <para>
        /// The keys are <c>nvarchar</c> on purpose, although <see cref="Load"/> sends <c>varchar</c> (#713). The two
        /// find the same user whenever both the key and the stored <c>user_name</c> are ASCII, as Entra UPNs are by
        /// policy. They can differ for non-ASCII text. Under a SQL collation <c>varchar</c> is compared with the
        /// non-Unicode sort order, which does not treat the sharp s (U+00DF) or the ae ligature (U+00E6) as "ss" or
        /// "ae", while the <c>nvarchar</c> comparison here does - so an ASCII key can match a stored non-ASCII
        /// <c>user_name</c> here that a <c>varchar</c> <see cref="Load"/>, and the unique index <c>IX_users</c> (which
        /// compares as <c>varchar</c> does), treat as a different user. Under a Windows collation both compare by
        /// Unicode rules and differ only for characters outside the code page.
        /// <c>varchar</c> keys were measured (PR #712; 200,000
        /// synthetic users, 1,000-key batches) and are not better under both collations. Under a SQL collation they
        /// cut SQL CPU from ~59 ms a batch to 1-19 ms. Under a Windows collation, which compares <c>varchar</c> by
        /// Unicode rules, the same merge join over the index took 30-50% more CPU (26-31 ms against 21 ms for keys
        /// spread across the index), and the nested-loop plan chosen for adjacent keys read ~3,200 pages a batch
        /// where the merge join read 15-563 (27x the reads over a 20,000-row save).
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
