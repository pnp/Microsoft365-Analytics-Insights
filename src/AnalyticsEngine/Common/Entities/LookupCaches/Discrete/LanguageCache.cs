using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading;

namespace Common.Entities.LookupCaches
{

    public class LanguageCache : DBLookupCacheForEntityWithName<Language>
    {
        public LanguageCache(AnalyticsEntitiesContext context) : base(context) { }
        public LanguageCache(AnalyticsEntitiesContext context, CancellationToken cancellationToken) : base(context, cancellationToken) { }
        public override DbSet<Language> EntityStore => DB.Languages;
    }
}
