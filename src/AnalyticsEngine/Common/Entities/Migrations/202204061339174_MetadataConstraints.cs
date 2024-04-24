namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class MetadataConstraints : DbMigration
    {
        public override void Up()
        {
            DropIndex("dbo.user_license_type_lookups", new[] { "user_id" });
            DropIndex("dbo.user_license_type_lookups", new[] { "license_type_id" });
            CreateIndex("dbo.user_departments", "name", unique: true);
            CreateIndex("dbo.user_job_titles", "name", unique: true);
            CreateIndex("dbo.user_license_type_lookups", new[] { "license_type_id", "user_id" }, unique: true);
            CreateIndex("dbo.license_types", "name", unique: true);
            CreateIndex("dbo.user_office_locations", "name", unique: true);
            Console.WriteLine("DB SCHEMA: Applied 'Usage reports enhancement constraints' succesfully.");
        }

        public override void Down()
        {
            throw new NotSupportedException();
        }
    }
}
