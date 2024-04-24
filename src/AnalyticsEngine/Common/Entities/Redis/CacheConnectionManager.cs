using Newtonsoft.Json;
using StackExchange.Redis;
using System;
using System.Threading.Tasks;

namespace Common.Entities.Redis
{
    /// <summary>
    /// Wrapper class for redis connection
    /// </summary>
    public class CacheConnectionManager
    {
        #region Singleton

        readonly ConnectionMultiplexer _muxer = null;
        readonly IDatabase _conn = null;
        private CacheConnectionManager(string connectionString)
        {
            _muxer = ConnectionMultiplexer.Connect(connectionString);
            _conn = _muxer.GetDatabase();
        }

        private static CacheConnectionManager _connectionManager = null;
        public static CacheConnectionManager GetConnectionManager(string connectionString)
        {
            if (_connectionManager == null)
            {
                _connectionManager = new CacheConnectionManager(connectionString);
            }

            return _connectionManager;
        }
        #endregion

        public IDatabase GetDatabase()
        {
            return _conn;
        }
        public ConnectionMultiplexer ConnectionMultiplexer
        {
            get { return _muxer; }
        }

        public async Task<string> GetString(string key)
        {
            var results = await GetDatabase().StringGetAsync(new RedisKey(key));

            if (results.HasValue)
            {
                return results.ToString();
            }
            else
            {
                return null;
            }
        }

        /// <summary>
        /// Get a string that's been temporarily cached & indentified by input params.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task<string> GetStringCache(object input)
        {
            return await GetStringCache(JsonConvert.SerializeObject(input));
        }
        /// <summary>
        /// Get a string that's been temporarily cached & indentified by input params.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task<string> GetStringCache(string inputString)
        {
            var results = await GetDatabase().StringGetAsync(new RedisKey(GetStringCacheKeyName(inputString)));

            if (results.HasValue)
            {
                return results.ToString();
            }
            else
            {
                return null;
            }
        }
        /// <summary>
        /// Set a string value that's been temporarily cached & indentified by input params. Expires in 24 hours.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task CacheStringOneDay(string inputString, string responseToCache)
        {
            var db = GetDatabase();
            var key = new RedisKey(GetStringCacheKeyName(inputString));
            await db.StringSetAsync(key, new RedisValue(responseToCache));

            await db.KeyExpireAsync(key, DateTime.Now.AddDays(1));
        }

        /// <summary>
        /// Set a string value that's been temporarily cached & indentified by input params. Expires in 24 hours.
        /// Usually used for cognitive-services caching to avoid calling API repeatedly for same input data.
        /// </summary>
        public async Task CacheStringOneDay(string inputString)
        {
            var db = GetDatabase();
            var key = new RedisKey(GetStringCacheKeyName(inputString));
            await db.StringSetAsync(key, new RedisValue(inputString));

            await db.KeyExpireAsync(key, DateTime.Now.AddDays(1));
        }



        string GetStringCacheKeyName(string inputString)
        {
            return "string_cache:" + inputString.GetHashCode();
        }

        public async Task SetString(string key, string val)
        {
            await GetDatabase().StringSetAsync(new RedisKey(key), new RedisValue(val));
        }

        public async Task DeleteString(string key)
        {
            await GetDatabase().KeyDeleteAsync(key);
        }

    }
}
