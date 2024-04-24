namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v108 : DbMigration
    {
        public override void Up()
        {
            // Dimension tables
            Sql(Properties.Resources.Table_DimDate);
            Sql(Properties.Resources.Table_DimTime);

            // Drop & create Views
            Sql(Properties.Resources.View_Content);
            Sql(Properties.Resources.View_Usage);
            Sql(Properties.Resources.View_vwEvents);
            Sql(Properties.Resources.View_VWHits);
            Sql(Properties.Resources.View_vwSearches);
            Sql(Properties.Resources.View_vwSessions);

            Console.WriteLine("DB SCHEMA: Applied 'Report views v1' succesfully.");

        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
