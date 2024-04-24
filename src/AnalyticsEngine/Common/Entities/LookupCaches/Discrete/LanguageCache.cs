using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    public class LanguageCache : DBLookupCache<Language>
    {
        public LanguageCache(AnalyticsEntitiesContext context) : base(context) { }
        public override DbSet<Language> EntityStore => DB.Languages;


        public async override Task<Language> Load(string searchKey)
        {
            return await DB.Languages.SingleOrDefaultAsync(t => t.Name == searchKey);
        }
    }
}
