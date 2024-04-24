namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class TeamsExtraViews2 : DbMigration
    {
        public override void Up()
        {
            // Pete's report missed these views. Alter/create
            Sql(Properties.Resources.TeamsViewsV2);
            Console.WriteLine("DB SCHEMA: Applied 'Teams Device Views' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
