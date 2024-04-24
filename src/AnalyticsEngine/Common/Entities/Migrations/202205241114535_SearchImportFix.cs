namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class SearchImportFix : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.searches", "date_time", c => c.DateTime());

            Console.WriteLine("DB SCHEMA: Applied 'User metadata V2' succesfully.");
        }

        public override void Down()
        {
            DropColumn("dbo.searches", "date_time");
        }
    }
}
