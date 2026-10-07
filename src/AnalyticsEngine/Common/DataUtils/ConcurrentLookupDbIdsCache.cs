using System;
using System.Collections.Concurrent;

namespace DataUtils
{
    /// <summary>
    /// Cache IDs for a 1 or more database record types. Threadsafe.
    /// It's a 3d cache basically. 
    /// </summary>
    public class ConcurrentLookupDbIdsCache
    {
        private ConcurrentDictionary<string, ConcurrentDictionary<string, int>> typeCache = null;
        public ConcurrentLookupDbIdsCache()
        {
            typeCache = new ConcurrentDictionary<string, ConcurrentDictionary<string, int>>();
        }
        public int? GetCachedIdForName<T>(string name) where T : class
        {
            lock (this)
            {
                // GetOrAdd with a factory: the per-name dictionary is only allocated the first time a type is seen,
                // not on every call - this is called once per usage-report row.
                var cache = typeCache.GetOrAdd(typeof(T).FullName, _ => new ConcurrentDictionary<string, int>());

                if (cache.TryGetValue(name, out var id))
                {
                    return id;
                }
                else return null;
            }
        }

        public void AddOrUpdateForName<T>(string name, int id)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentNullException(nameof(name));
            }

            lock (this)
            {
                var cache = typeCache.GetOrAdd(typeof(T).FullName, _ => new ConcurrentDictionary<string, int>());

                cache.AddOrUpdate(name, id, (index, val) => val);
            }
        }
    }
}
