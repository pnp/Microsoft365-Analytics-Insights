namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal class UserActivityLastImportedRedisSingleDateLoader : RedisSingleDateLoader
    {
        const string UserActivityLastImportedKey = "UserActivityLastImported";
        public UserActivityLastImportedRedisSingleDateLoader(string redisConnectionString, string tenantId = null, string clientId = null, string clientSecret = null) : base(redisConnectionString, UserActivityLastImportedKey, tenantId, clientId, clientSecret)
        {
        }
    }
}
