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

            // Migrations run during installer / DatabaseUpgrader.CheckDbUpgraded, where the
            // operator is watching and is happy to wait. Some upgrades (notably the audit_events
            // FK validation - see AddAuditEventsOperationFK and the "Audit Events FK Upgrade"
            // wiki page) can run for many hours on very large tables (100M+ rows), well beyond
            // any reasonable per-command timeout. Use 0 = infinite so the migration completes
            // rather than aborting mid-way through an FK validation scan.
            this.CommandTimeout = 0;
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
