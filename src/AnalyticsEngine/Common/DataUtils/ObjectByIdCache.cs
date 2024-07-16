using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace DataUtils
{
    public abstract class ObjectByIdCache<T> where T : class
    {
        private static readonly StringComparer _sqlStringComparer = CultureInfo.GetCultureInfo(1033).CompareInfo
                            .GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth);
        private Dictionary<string, T> ObjectCache { get; set; } = new Dictionary<string, T>(_sqlStringComparer);

        /// <summary>
        /// Load resource either from cache or external load.
        /// </summary>
        /// <param name="loadMethodFoundNoResultCallback">If nothing in cache found or externally, callback with result</param>
        public Task<T> GetResource(string id, Func<Task<T>> loadMethodFoundNoResultCallback)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));
            }

            lock (this)
            {
                if (!ObjectCache.ContainsKey(id))
                {
                    T obj = Load(id).Result;       // Cannot await in lock

                    // No object from load? Invoke callback
                    if (obj == null && loadMethodFoundNoResultCallback != null)
                    {
                        obj = loadMethodFoundNoResultCallback().Result;
                    }

                    if (obj == null)
                    {
                        throw new ArgumentException($"No resources of type {typeof(T).Name} in cache or external load found by ID {id}");
                    }
                    ObjectCache.Add(id, obj);
                }
                return Task.FromResult(ObjectCache[id]);
            }
        }
        public async Task<T> GetResource(string id)
        {
            return await GetResource(id, null);
        }

        public async Task<T> GetResourceOrNullIfNotExists(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException($"'{nameof(id)}' cannot be null or empty.", nameof(id));

            T r = null;
            try
            {
                r = await GetResource(id);
            }
            catch (ArgumentException)
            {
                // This is ok
            }
            return r;
        }

        public abstract Task<T> Load(string id);
    }
}
