namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class ExtendedUsageReports : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.platform_user_activity_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    windows = c.Boolean(nullable: false),
                    mac = c.Boolean(nullable: false),
                    mobile = c.Boolean(nullable: false),
                    web = c.Boolean(nullable: false),
                    outlook = c.Boolean(nullable: false),
                    word = c.Boolean(nullable: false),
                    excel = c.Boolean(nullable: false),
                    powerpoint = c.Boolean(nullable: false),
                    onenote = c.Boolean(nullable: false),
                    teams = c.Boolean(nullable: false),
                    outlook_windows = c.Boolean(nullable: false),
                    word_windows = c.Boolean(nullable: false),
                    excel_windows = c.Boolean(nullable: false),
                    powerpoint_windows = c.Boolean(nullable: false),
                    onenote_windows = c.Boolean(nullable: false),
                    teams_windows = c.Boolean(nullable: false),
                    outlook_mac = c.Boolean(nullable: false),
                    word_mac = c.Boolean(nullable: false),
                    excel_mac = c.Boolean(nullable: false),
                    powerpoint_mac = c.Boolean(nullable: false),
                    onenote_mac = c.Boolean(nullable: false),
                    teams_mac = c.Boolean(nullable: false),
                    outlook_mobile = c.Boolean(nullable: false),
                    word_mobile = c.Boolean(nullable: false),
                    excel_mobile = c.Boolean(nullable: false),
                    powerpoint_mobile = c.Boolean(nullable: false),
                    onenote_mobile = c.Boolean(nullable: false),
                    teams_mobile = c.Boolean(nullable: false),
                    outlook_web = c.Boolean(nullable: false),
                    word_web = c.Boolean(nullable: false),
                    excel_web = c.Boolean(nullable: false),
                    powerpoint_web = c.Boolean(nullable: false),
                    onenote_web = c.Boolean(nullable: false),
                    teams_web = c.Boolean(nullable: false),
                    user_id = c.Int(nullable: false),
                    date = c.DateTime(nullable: false),
                    last_activity_date = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.user_id);

            CreateTable(
                "dbo.sharepoint_sites_file_stats_log",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    site_id = c.Int(nullable: false),
                    external_sharing = c.Boolean(nullable: false),
                    file_count = c.Int(nullable: false),
                    active_file_count = c.Int(nullable: false),
                    page_view_count = c.Int(nullable: false),
                    visited_page_count = c.Int(nullable: false),
                    storage_used_bytes = c.Int(nullable: false),
                    storage_allocated_bytes = c.Long(nullable: false),
                    anonymous_link_count = c.Int(nullable: false),
                    company_link_count = c.Int(nullable: false),
                    secure_link_for_guest_count = c.Int(nullable: false),
                    secure_Link_for_member_count = c.Int(nullable: false),
                    week_ending = c.DateTime(),
                })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.sites", t => t.site_id, cascadeDelete: true)
                .Index(t => t.site_id);

            AddColumn("dbo.users", "mail", c => c.String());
            AddColumn("dbo.teams_user_device_usage_log", "used_linux", c => c.Boolean());
            AddColumn("dbo.teams_user_device_usage_log", "used_chrome_os", c => c.Boolean());
            AddColumn("dbo.teams_user_activity_log", "post_messages", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "reply_messages", c => c.Long(nullable: false));
            AddColumn("dbo.teams_user_activity_log", "urgent_messages", c => c.Long(nullable: false));


            Console.WriteLine("DB SCHEMA: Applied 'Extended Usage Reports' succesfully.");
        }

        public override void Down()
        {
            DropForeignKey("dbo.sharepoint_sites_file_stats_log", "site_id", "dbo.sites");
            DropForeignKey("dbo.platform_user_activity_log", "user_id", "dbo.users");
            DropIndex("dbo.sharepoint_sites_file_stats_log", new[] { "site_id" });
            DropIndex("dbo.platform_user_activity_log", new[] { "user_id" });
            DropColumn("dbo.teams_user_activity_log", "urgent_messages");
            DropColumn("dbo.teams_user_activity_log", "reply_messages");
            DropColumn("dbo.teams_user_activity_log", "post_messages");
            DropColumn("dbo.teams_user_device_usage_log", "used_chrome_os");
            DropColumn("dbo.teams_user_device_usage_log", "used_linux");
            DropColumn("dbo.users", "mail");
            DropTable("dbo.sharepoint_sites_file_stats_log");
            DropTable("dbo.platform_user_activity_log");
        }
    }
}
