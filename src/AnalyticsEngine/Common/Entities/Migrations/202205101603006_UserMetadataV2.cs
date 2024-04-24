namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class UserMetadataV2 : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.user_company_name",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            CreateTable(
                "dbo.user_state_or_province",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            CreateTable(
                "dbo.user_country_or_region",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.name, unique: true);

            AddColumn("dbo.users", "postalcode", c => c.String(maxLength: 50));
            AddColumn("dbo.users", "company_name_id", c => c.Int());
            AddColumn("dbo.users", "state_or_province_id", c => c.Int());
            AddColumn("dbo.users", "manager_id", c => c.Int());
            AddColumn("dbo.users", "country_or_region_id", c => c.Int());
            CreateIndex("dbo.users", "company_name_id");
            CreateIndex("dbo.users", "state_or_province_id");
            CreateIndex("dbo.users", "manager_id");
            CreateIndex("dbo.users", "country_or_region_id");
            AddForeignKey("dbo.users", "company_name_id", "dbo.user_company_name", "id");
            AddForeignKey("dbo.users", "manager_id", "dbo.users", "id");
            AddForeignKey("dbo.users", "state_or_province_id", "dbo.user_state_or_province", "id");
            AddForeignKey("dbo.users", "country_or_region_id", "dbo.user_country_or_region", "id");

            // Make sure the next user import deletes delta token
            Sql("UPDATE users set [account_enabled] = null");

            Console.WriteLine("DB SCHEMA: Applied 'User metadata V2' succesfully.");
        }


        public override void Down()
        {
            DropForeignKey("dbo.users", "country_or_region_id", "dbo.user_country_or_region");
            DropForeignKey("dbo.users", "state_or_province_id", "dbo.user_state_or_province");
            DropForeignKey("dbo.users", "manager_id", "dbo.users");
            DropForeignKey("dbo.users", "company_name_id", "dbo.user_company_name");
            DropIndex("dbo.user_country_or_region", new[] { "name" });
            DropIndex("dbo.user_state_or_province", new[] { "name" });
            DropIndex("dbo.user_company_name", new[] { "name" });
            DropIndex("dbo.users", new[] { "country_or_region_id" });
            DropIndex("dbo.users", new[] { "manager_id" });
            DropIndex("dbo.users", new[] { "state_or_province_id" });
            DropIndex("dbo.users", new[] { "company_name_id" });
            DropColumn("dbo.users", "country_or_region_id");
            DropColumn("dbo.users", "manager_id");
            DropColumn("dbo.users", "state_or_province_id");
            DropColumn("dbo.users", "company_name_id");
            DropColumn("dbo.users", "postalcode");
            DropTable("dbo.user_country_or_region");
            DropTable("dbo.user_state_or_province");
            DropTable("dbo.user_company_name");
        }
    }
}
