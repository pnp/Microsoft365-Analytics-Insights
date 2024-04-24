using Common.Entities.Config;
using System;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.StatsUploader
{
    public class RedisStatsDatesLoader : RedisSingleDateLoader, IStatsDatesLoader
    {
        const string REDIS_KEY = "statsLastUploaded";

        public RedisStatsDatesLoader(AppConfig config) : base(config.ConnectionStrings.RedisConnectionString, REDIS_KEY)
        {
        }

        // IStatsDatesLoader implementations
        public async Task<DateTime?> GetLastUploadDt()
        {
            return await base.GetLastDT();
        }

        public async Task RegisterLastUploadDt()
        {
            await base.SaveDT();
        }
        public async Task RegisterLastUploadDt(DateTime lastUpload)
        {
            await base.SaveDT(lastUpload);
        }
    }
}
