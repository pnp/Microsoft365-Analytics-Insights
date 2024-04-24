using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class UserJobTitleCache : DBLookupCache<UserJobTitle>
    {
        public UserJobTitleCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserJobTitle> EntityStore => this.DB.UserJobTitles;

        public async override Task<UserJobTitle> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
