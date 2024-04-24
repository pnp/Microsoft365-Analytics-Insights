namespace WebJob.Office365ActivityImporter.Engine.Graph
{
    internal class UserActivityLastImportedRedisSingleDateLoader : RedisSingleDateLoader
    {
        const string UserActivityLastImportedKey = "UserActivityLastImported";
        public UserActivityLastImportedRedisSingleDateLoader(string redisConnectionString) : base(redisConnectionString, UserActivityLastImportedKey)
        {
        }
    }
}
