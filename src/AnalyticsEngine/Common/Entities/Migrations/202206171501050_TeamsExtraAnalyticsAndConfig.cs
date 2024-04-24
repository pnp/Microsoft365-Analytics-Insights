namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class TeamsExtraAnalyticsAndConfig : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.group_crawl_config",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    rule_graph_id = c.String(),
                    include = c.Boolean(nullable: false),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.teams_reaction_types",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateTable(
                "dbo.teams_user_channel_reactions",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    reaction_id = c.Int(),
                    user_id = c.Int(nullable: false),
                    channel_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_channels", t => t.channel_id, cascadeDelete: true)
                .ForeignKey("dbo.teams_reaction_types", t => t.reaction_id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.reaction_id)
                .Index(t => t.user_id)
                .Index(t => t.channel_id);

            CreateTable(
                "dbo.teams_addons_user_installed_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    date = c.DateTime(nullable: false),
                    addon_id = c.Int(nullable: false),
                    user_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.teams_addons", t => t.addon_id, cascadeDelete: true)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.addon_id)
                .Index(t => t.user_id);

            Console.WriteLine("DB SCHEMA: Applied 'Teams extra analytics & config' succesfully.");
        }

        public override void Down()
        {
            DropForeignKey("dbo.teams_addons_user_installed_log", "user_id", "dbo.users");
            DropForeignKey("dbo.teams_addons_user_installed_log", "addon_id", "dbo.teams_addons");
            DropForeignKey("dbo.teams_user_channel_reactions", "user_id", "dbo.users");
            DropForeignKey("dbo.teams_user_channel_reactions", "reaction_id", "dbo.teams_reaction_types");
            DropForeignKey("dbo.teams_user_channel_reactions", "channel_id", "dbo.teams_channels");
            DropIndex("dbo.teams_addons_user_installed_log", new[] { "user_id" });
            DropIndex("dbo.teams_addons_user_installed_log", new[] { "addon_id" });
            DropIndex("dbo.teams_user_channel_reactions", new[] { "channel_id" });
            DropIndex("dbo.teams_user_channel_reactions", new[] { "user_id" });
            DropIndex("dbo.teams_user_channel_reactions", new[] { "reaction_id" });
            DropTable("dbo.teams_addons_user_installed_log");
            DropTable("dbo.teams_user_channel_reactions");
            DropTable("dbo.teams_reaction_types");
            DropTable("dbo.group_crawl_config");
        }
    }
}
