namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class PageCommentsAndLikes : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.page_comments",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    comment = c.String(),
                    language_id = c.Int(),
                    sentiment_score = c.Double(),
                    parent_id = c.Int(),
                    user_id = c.Int(nullable: false),
                    url_id = c.Int(nullable: false),
                    created = c.DateTime(nullable: false),
                    sp_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.languages", t => t.language_id)
                .ForeignKey("dbo.page_comments", t => t.parent_id)
                .ForeignKey("dbo.urls", t => t.url_id, cascadeDelete: true)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.language_id)
                .Index(t => t.parent_id)
                .Index(t => t.user_id)
                .Index(t => t.url_id);

            CreateTable(
                "dbo.page_likes",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    user_id = c.Int(nullable: false),
                    url_id = c.Int(nullable: false),
                    created = c.DateTime(nullable: false),
                    sp_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.urls", t => t.url_id, cascadeDelete: true)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id)
                .Index(t => t.url_id);

            Console.WriteLine("DB SCHEMA: Applied 'Page comments and likes (rev 2)' succesfully.");

        }

        public override void Down()
        {
            DropForeignKey("dbo.page_likes", "user_id", "dbo.users");
            DropForeignKey("dbo.page_likes", "url_id", "dbo.urls");
            DropForeignKey("dbo.page_comments", "user_id", "dbo.users");
            DropForeignKey("dbo.page_comments", "url_id", "dbo.urls");
            DropForeignKey("dbo.page_comments", "parent_id", "dbo.page_comments");
            DropForeignKey("dbo.page_comments", "language_id", "dbo.languages");
            DropIndex("dbo.page_likes", new[] { "url_id" });
            DropIndex("dbo.page_likes", new[] { "user_id" });
            DropIndex("dbo.page_comments", new[] { "url_id" });
            DropIndex("dbo.page_comments", new[] { "user_id" });
            DropIndex("dbo.page_comments", new[] { "parent_id" });
            DropIndex("dbo.page_comments", new[] { "language_id" });
            DropTable("dbo.page_likes");
            DropTable("dbo.page_comments");
        }
    }
}
