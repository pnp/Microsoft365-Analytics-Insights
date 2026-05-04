using System.Data.Entity;

namespace Common.Entities.LookupCaches
{

    // User caches
    public class UserDepartmentCache : DBLookupCacheForEntityWithName<UserDepartment>
    {
        public UserDepartmentCache(AnalyticsEntitiesContext context) : base(context) { }

        public override DbSet<UserDepartment> EntityStore => this.DB.UserDepartments;
    }
}
