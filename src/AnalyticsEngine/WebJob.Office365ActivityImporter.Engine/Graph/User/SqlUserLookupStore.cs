using Common.Entities;
using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// Entity Framework implementation of <see cref="IUserLookupStore"/>, reading through the same
    /// context the user import is writing with so the entities it returns are tracked.
    /// </summary>
    internal class SqlUserLookupStore : IUserLookupStore
    {
        /// <summary>
        /// How many UPNs go into one <c>IN (...)</c> list. SQL Server allows 2,100 parameters per
        /// command, and EF6 sends each element of a <c>Contains</c> list as its own parameter, so
        /// this has to stay comfortably below that. 1,000 is the size the rest of the user pipeline
        /// already uses for the same reason (see the reload loop in <c>UserMetadataUpdater</c>).
        /// </summary>
        public const int UpnChunkSize = 1000;

        private readonly AnalyticsEntitiesContext _db;
        private readonly int _chunkSize;

        public SqlUserLookupStore(AnalyticsEntitiesContext db) : this(db, UpnChunkSize) { }

        public SqlUserLookupStore(AnalyticsEntitiesContext db, int chunkSize)
        {
            if (chunkSize < 1) throw new ArgumentOutOfRangeException(nameof(chunkSize));
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _chunkSize = chunkSize;
        }

        public async Task<IReadOnlyList<Common.Entities.User>> GetUsersByUpnAsync(IReadOnlyCollection<string> upns)
        {
            var results = new List<Common.Entities.User>(upns.Count);
            if (upns.Count == 0)
            {
                return results;
            }

            // GetRange rather than Skip().Take() - Skip() on a List walks past every prior element,
            // so chunking N items in slices of K costs O(N^2/K).
            var all = upns as List<string> ?? upns.ToList();
            for (var i = 0; i < all.Count; i += _chunkSize)
            {
                var chunk = all.GetRange(i, Math.Min(_chunkSize, all.Count - i));

                // Tracked on purpose: callers assign the result to a navigation property.
                //
                // LicenseLookups is included deliberately, and it is load-bearing rather than
                // decoration. In the per-user licence path (no Organization.Read.All) the batch's
                // own users are AsNoTracking snapshots loaded WITH their licences, and they are
                // attached one at a time as the batch is processed. If this query tracks one of
                // them first - a manager who is also a subject of the same batch -
                // GetOrAttachUser returns this instance instead, and EF will not populate a
                // navigation property it was never asked to load: proxies are disabled and
                // User.LicenseLookups is a plain non-virtual List initialised empty, so there is
                // no lazy load to rescue it. ProcessUserLicenses would then delete none of the
                // user's existing licence rows before re-adding them, and SaveChanges would fail
                // on the unique (license_type_id, user_id) index. Loading the licences here keeps
                // this instance at least as complete as the snapshot it can shadow.
                //
                // No LOWER() on the column - the default code-first collation is already
                // case-insensitive, and lowering it would make the predicate non-SARGable and
                // scan the whole table instead of seeking the user_name index.
                var loaded = await _db.users
                    .Where(u => chunk.Contains(u.UserPrincipalName))
                    .Include(u => u.LicenseLookups)
                    .ToListAsync();

                results.AddRange(loaded);
            }

            return results;
        }
    }
}
