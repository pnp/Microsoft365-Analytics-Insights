using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
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
        /// Delegates to <see cref="ExistingUserIds"/> so all batch callers use the actual column collation.
        /// Matches return by input position; dictionary keys retain their original spelling without imposing
        /// an in-memory case/accent comparison on SQL's answer. Duplicate database names resolve to the lowest id.
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

            var ids = await ExistingUserIds.FindAsync(DB, keys);
            for (var i = 0; i < keys.Count; i++)
                if (ids[i].HasValue) found[keys[i]] = ids[i].Value;
            return found;
        }
    }
}
