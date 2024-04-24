namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class OrgUrlsFilterOptions : DbMigration
    {
        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applied 'Org Urls Filter Options' succesfully.");
            AddColumn("dbo.org_urls", "exact_match", c => c.Boolean());
        }

        public override void Down()
        {
            DropColumn("dbo.org_urls", "exact_match");
        }
    }
}
