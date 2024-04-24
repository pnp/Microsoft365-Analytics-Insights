namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v107 : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.hits", "sp_request_duration", c => c.Int());

            Console.WriteLine("DB SCHEMA: Applied 'SPRequestDuration' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
