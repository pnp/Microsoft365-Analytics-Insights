namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class TeamsExtraViews1 : DbMigration
    {
        public override void Up()
        {
            // Pete's report missed these views. Alter/create
            Sql(Properties.Resources.TeamsExtraViews1);
            Console.WriteLine("DB SCHEMA: Applied 'Teams Extra Views' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
