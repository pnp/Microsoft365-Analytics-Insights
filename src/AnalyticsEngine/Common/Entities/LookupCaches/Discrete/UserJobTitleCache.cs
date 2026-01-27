using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    public class UserJobTitleCache : DBLookupCacheForEntityWithName<UserJobTitle>
    {
        public UserJobTitleCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserJobTitle> EntityStore => this.DB.UserJobTitles;
    }
}
