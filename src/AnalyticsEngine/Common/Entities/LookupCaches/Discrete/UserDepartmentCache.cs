using System.Data.Entity;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{

    // User caches
    public class UserDepartmentCache : DBLookupCache<UserDepartment>
    {
        public UserDepartmentCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserDepartment> EntityStore => this.DB.UserDepartments;

        public async override Task<UserDepartment> Load(string searchName)
        {
            return await EntityStore.SingleOrDefaultAsync(t => t.Name == searchName);
        }
    }
}
