using System.Collections.Generic;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    /// <summary>
    /// The managers referenced by the batch currently being processed, loaded in one query.
    /// </summary>
    /// <remarks>
    /// This is the fix for the manager-resolution N+1 called out in #371. The database-by-UPN
    /// branch of <c>UserDataMapper.UpdateUserManager</c> is reached whenever a manager cannot be
    /// resolved from the in-memory dictionaries. During insert enrichment that means a manager
    /// inserted in a <b>later</b> batch than their report - the dictionary starts from pre-existing
    /// users and each batch adds its own before processing them - and Graph does not order the
    /// delta by reporting line, so on the first import of a large tenant it is a substantial share
    /// of everyone who has a manager. One chunked query per batch replaces those round trips.
    ///
    /// Scope is deliberately one batch. The entities are tracked by the import's context and every
    /// batch ends by detaching them, so a cache kept across batches would hand out detached
    /// entities and EF would try to INSERT the manager. <see cref="LoadForBatchAsync"/> therefore
    /// replaces the contents on every call rather than accumulating.
    ///
    /// With no lookup store it stays permanently empty, which leaves the original per-user query in
    /// place - that is what the <c>UserDataMapper</c> constructor overload without a store gets.
    /// </remarks>
    internal class ManagerPrefetchCache
    {
        private readonly IUserLookupStore _userLookupStore;
        private Dictionary<string, Common.Entities.User> _byUpn = ManagerResolutionRules.IndexByUpn(new Common.Entities.User[0]);

        /// <param name="userLookupStore">May be null, in which case the cache never loads anything.</param>
        public ManagerPrefetchCache(IUserLookupStore userLookupStore)
        {
            _userLookupStore = userLookupStore;
        }

        /// <summary>How many managers the last <see cref="LoadForBatchAsync"/> resolved.</summary>
        public int Count => _byUpn.Count;

        /// <summary>
        /// Replaces the cache with the managers this batch might need to look up by UPN.
        /// </summary>
        public async Task LoadForBatchAsync(IEnumerable<GraphUser> batch, IDictionary<string, GraphUser> graphUsersByAadId)
        {
            _byUpn = ManagerResolutionRules.IndexByUpn(new Common.Entities.User[0]);

            if (_userLookupStore == null)
            {
                return;
            }

            var upns = ManagerResolutionRules.CollectManagerUpnsToPrefetch(batch, graphUsersByAadId);
            if (upns.Count == 0)
            {
                return;
            }

            var managers = await _userLookupStore.GetUsersByUpnAsync(upns);
            _byUpn = ManagerResolutionRules.IndexByUpn(managers);
        }

        /// <summary>Case-insensitive, matching SQL Server's default code-first collation.</summary>
        public bool TryGet(string upn, out Common.Entities.User manager)
        {
            return _byUpn.TryGetValue(upn, out manager);
        }
    }
}
