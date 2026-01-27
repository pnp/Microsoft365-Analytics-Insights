using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    public class LanguageCache : DBLookupCacheForEntityWithName<Language>
    {
        public LanguageCache(AnalyticsEntitiesContext context) : base(context) { }
        public override DbSet<Language> EntityStore => DB.Languages;
    }
}
