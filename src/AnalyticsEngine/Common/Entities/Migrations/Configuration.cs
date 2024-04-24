namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    using System.Linq;

    public sealed class Configuration : DbMigrationsConfiguration<AnalyticsEntitiesContext>
    {
        public Configuration()
        {
            AutomaticMigrationsEnabled = true;
            const int TWO_HOURS = 60 * 60;
            this.CommandTimeout = TWO_HOURS;
        }

        public void OutputCurrentMigration(AnalyticsEntitiesContext context)
        {
            var query = "select top 1 MigrationId from __MigrationHistory order by LEFT(MigrationId, 15) desc";
            var migrationId = context.Database.SqlQuery<string>(query).FirstOrDefault();
            Console.WriteLine($"SQL: Runtime database {context.Database.Connection.Database} is running migration ID \"{migrationId}\".");


        }


        protected override void Seed(AnalyticsEntitiesContext context)
        {
            OutputCurrentMigration(context);
        }
    }
}
