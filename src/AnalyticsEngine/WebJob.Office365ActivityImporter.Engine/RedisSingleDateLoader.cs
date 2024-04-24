using Common.Entities.Redis;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// For loading and saving a single date value in Redis
    /// </summary>
    public class RedisSingleDateLoader
    {
        private readonly string _key;
        private CacheConnectionManager _cacheConnectionManager = null;

        public RedisSingleDateLoader(string redisConnectionString, string key)
        {
            _cacheConnectionManager = CacheConnectionManager.GetConnectionManager(redisConnectionString);
            _key = key;
        }
        public async Task<DateTime?> GetLastDT()
        {
            var lastVal = await _cacheConnectionManager.GetString(_key);
            if (!string.IsNullOrEmpty(lastVal))
            {
                var dt = DateTime.Now;
                if (DateTime.TryParse(lastVal, DateTimeFormatInfo.CurrentInfo, DateTimeStyles.RoundtripKind, out dt))
                {
                    return dt;
                }
            }

            return null;
        }

        public async Task SaveDT()
        {
            await SaveDT(DateTime.Now);
        }
        public async Task SaveDT(DateTime dt)
        {
            await _cacheConnectionManager.SetString(_key, dt.ToString("o", CultureInfo.InvariantCulture));
        }


        public async Task DeleteDt()
        {
            await _cacheConnectionManager.DeleteString(_key);
        }
    }
}
