using System.Data.Entity;

namespace Tests.UnitTests
{
    public class FakeOfficeServicesDB : DbContext
    {
        public FakeOfficeServicesDB() : base("UnitTestingOffice365Services")
        {
        }

        public DbSet<TestingSubscriptions> subscriptions { get; set; }
    }
}
