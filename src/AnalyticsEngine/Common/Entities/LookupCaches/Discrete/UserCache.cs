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
        /// The user with this UPN, or null. Matching follows the column's collation (case-insensitive on every
        /// supported install) and ignores trailing spaces; duplicate UPNs (possible only where the unique
        /// <c>IX_users</c> is missing) resolve to the lowest id.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The UPN is sent as <c>varchar</c>, which is what <c>users.user_name</c> is (#713). The EF model leaves that
        /// column Unicode, so a plain parameter goes as <c>nvarchar(4000)</c>, and <c>nvarchar</c> wins on data-type
        /// precedence: SQL Server converts the COLUMN to compare. Under a SQL collation - the Azure SQL Database
        /// default - that conversion cannot be turned into a seek, so every call scanned the whole of
        /// <c>IX_users</c>. A Windows collation could still seek through the conversion; it now seeks without one.
        /// This runs once for every UPN a cache has not seen yet: the get-or-create path for users who are not in SQL
        /// yet, and the user import caching each user it has just inserted.
        /// </para>
        /// <para>
        /// Measured with 200,000 synthetic users on LocalDB, 1,000 calls a run, median of 5 runs after a discarded
        /// first, before -&gt; after (reproduce with <c>UpnLookupBenchmarkTests</c>):
        /// <code>
        ///   user_name collation           call   IX_users plan        reads per call   ms per 1,000   SQL CPU ms per 1,000
        ///   SQL_Latin1_General_CP1_CI_AS  hit    Index Scan -> Seek   1,460 -> 6       17,237 -> 380  17,906 -> 16
        ///   SQL_Latin1_General_CP1_CI_AS  miss   Index Scan -> Seek   1,457 -> 3       17,536 -> 292  17,890 -> 31
        ///   Latin1_General_CI_AS          hit    Seek -> Seek             6 -> 6          468 -> 370      63 -> 31
        ///   Latin1_General_CI_AS          miss   Seek -> Seek             3 -> 3          379 -> 325      47 -> 16
        /// </code>
        /// CPU figures under about 50 ms are at the resolution of the server's timer.
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
    }
}
