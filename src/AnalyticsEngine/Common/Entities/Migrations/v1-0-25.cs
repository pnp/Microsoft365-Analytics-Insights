namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v1025 : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.hits", "page_load_time", c => c.Double());

            // Add column to view
            Sql(@"
                IF object_id(N'dbo.hits_view', 'V') IS NOT NULL
	                DROP VIEW [dbo].[hits_view]
                GO

                CREATE VIEW [dbo].[hits_view] AS


	                select hits.id, full_url, [users].user_name, hits.session_id, ai_session_id, seconds_on_page, hit_timestamp, page_load_time
		                from hits 
	                inner join urls on hits.url_id = urls.id
	                inner join [sessions] on hits.session_id = [sessions].id
	                inner join [users] on [sessions].user_id = users.id

                GO
                ");
            Console.WriteLine("DB SCHEMA: Applied 'page-load-time' patch.");
        }

        public override void Down()
        {
            DropColumn("dbo.hits", "page_load_time");

            // Restore original view
            Sql(@"
                IF object_id(N'dbo.hits_view', 'V') IS NOT NULL
	                DROP VIEW [dbo].[hits_view]
                GO

                CREATE VIEW [dbo].[hits_view] AS


	                select hits.id, full_url, [users].user_name, hits.session_id, ai_session_id, seconds_on_page, hit_timestamp
		                from hits 
	                inner join urls on hits.url_id = urls.id
	                inner join [sessions] on hits.session_id = [sessions].id
	                inner join [users] on [sessions].user_id = users.id

                GO
                ");
        }
    }
}
