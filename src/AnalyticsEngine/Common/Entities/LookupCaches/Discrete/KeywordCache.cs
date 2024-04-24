using Common.Entities.Entities;
using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    internal class KeywordCache : DBLookupCache<KeyWord>
    {
        public KeywordCache(AnalyticsEntitiesContext context) : base(context) { }
        public override DbSet<KeyWord> EntityStore => DB.KeyWords;


        public async override Task<KeyWord> Load(string searchKey)
        {
            return await DB.KeyWords.SingleOrDefaultAsync(t => t.Name == searchKey);
        }
    }
}
