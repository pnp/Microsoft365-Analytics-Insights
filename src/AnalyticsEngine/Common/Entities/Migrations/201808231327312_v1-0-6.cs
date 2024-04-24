namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class v106 : DbMigration
    {
        public override void Up()
        {
            // Run script to create new tables, move data to new tables, delete old
            Sql(Properties.Resources.Audit_Log_Migration, true, null);

            Console.WriteLine("DB SCHEMA: Applied 'audit-log refactor' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
