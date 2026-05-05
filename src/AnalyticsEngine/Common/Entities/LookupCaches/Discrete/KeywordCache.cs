using Common.Entities.Entities;
using System.Data.Entity;

namespace Common.Entities.LookupCaches
{
    internal class KeywordCache : DBLookupCacheForEntityWithName<KeyWord>
    {
        public KeywordCache(AnalyticsEntitiesContext context) : base(context) { }
        public override DbSet<KeyWord> EntityStore => DB.KeyWords;
    }
}
