namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class ExtendedUsageReportsSpSiteUsageLogDbFixes : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.sites", "site_id", c => c.String(maxLength: 100));
            AlterColumn("dbo.sharepoint_sites_file_stats_log", "storage_used_bytes", c => c.Long(nullable: false));

            Console.WriteLine("DB SCHEMA: Applied 'Extended Usage Reports SP Site Usage DB fix' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException("This migration cannot be reversed.");
        }
    }
}
