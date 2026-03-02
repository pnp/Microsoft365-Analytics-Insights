using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    // User caches
    public class UserDepartmentCache : DBLookupCacheForEntityWithName<UserDepartment>
    {
        public UserDepartmentCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserDepartment> EntityStore => this.DB.UserDepartments;
    }
}
