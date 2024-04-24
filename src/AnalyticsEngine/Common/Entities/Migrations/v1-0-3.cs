namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Added web & site tables. Hits now linked to webs.
    /// </summary>
    public partial class v103 : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.webs",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    url_base = c.String(maxLength: 500),
                    title = c.String(),
                    site_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.sites", t => t.site_id, cascadeDelete: true)
                .Index(t => t.url_base, unique: true)
                .Index(t => t.site_id);

            CreateTable(
                "dbo.sites",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    url_base = c.String(maxLength: 500),
                })
                .PrimaryKey(t => t.id)
                .Index(t => t.url_base, unique: true);

            AddColumn("dbo.hits", "web_id", c => c.Int());
            CreateIndex("dbo.hits", "web_id");
            AddForeignKey("dbo.hits", "web_id", "dbo.webs", "id");

            Console.WriteLine("DB SCHEMA: Applied 'Hits SPSite & SPWeb' patch.");
        }

        public override void Down()
        {
            DropForeignKey("dbo.hits", "web_id", "dbo.webs");
            DropForeignKey("dbo.webs", "site_id", "dbo.sites");
            DropIndex("dbo.sites", new[] { "url_base" });
            DropIndex("dbo.webs", new[] { "site_id" });
            DropIndex("dbo.webs", new[] { "url_base" });
            DropIndex("dbo.hits", new[] { "web_id" });
            DropColumn("dbo.hits", "web_id");
            DropTable("dbo.sites");
            DropTable("dbo.webs");
        }
    }
}
