namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    public partial class CopilotEvents : DbMigration
    {
        public override void Up()
        {
            DropIndex("dbo.audit_events", new[] { "user_ID" });
            DropIndex("dbo.event_meta_sharepoint", new[] { "file_name_id" });
            CreateTable(
                "dbo.event_copilot_chats",
                c => new
                {
                    event_id = c.Guid(nullable: false),
                    app_host = c.String(),
                })
                .PrimaryKey(t => t.event_id)
                .ForeignKey("dbo.audit_events", t => t.event_id)
                .Index(t => t.event_id);

            CreateTable(
                "dbo.event_copilot_files",
                c => new
                {
                    copilot_chat_id = c.Guid(nullable: false),
                    file_extension_id = c.Int(),
                    file_name_id = c.Int(),
                    url_id = c.Int(nullable: false),
                    site_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.copilot_chat_id)
                .ForeignKey("dbo.event_file_ext", t => t.file_extension_id)
                .ForeignKey("dbo.event_file_names", t => t.file_name_id)
                .ForeignKey("dbo.event_copilot_chats", t => t.copilot_chat_id)
                .ForeignKey("dbo.sites", t => t.site_id, cascadeDelete: true)
                .ForeignKey("dbo.urls", t => t.url_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.file_extension_id)
                .Index(t => t.file_name_id)
                .Index(t => t.url_id)
                .Index(t => t.site_id);

            CreateTable(
                "dbo.event_copilot_meetings",
                c => new
                {
                    copilot_chat_id = c.Guid(nullable: false),
                    meeting_id = c.Int(nullable: false),
                })
                .PrimaryKey(t => t.copilot_chat_id)
                .ForeignKey("dbo.online_meetings", t => t.meeting_id, cascadeDelete: true)
                .ForeignKey("dbo.event_copilot_chats", t => t.copilot_chat_id)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.meeting_id);

            CreateTable(
                "dbo.online_meetings",
                c => new
                {
                    id = c.Int(nullable: false, identity: true),
                    created = c.DateTime(nullable: false),
                    meeting_id = c.String(),
                    name = c.String(maxLength: 100),
                })
                .PrimaryKey(t => t.id);

            CreateIndex("dbo.audit_events", "user_id");
            CreateIndex("dbo.event_meta_sharepoint", "file_name_ID");


            Console.WriteLine("DB SCHEMA: Applied 'Copilot audit events' succesfully.");
        }

        public override void Down()
        {
            DropForeignKey("dbo.event_copilot_meetings", "copilot_chat_id", "dbo.event_copilot_chats");
            DropForeignKey("dbo.event_copilot_meetings", "meeting_id", "dbo.online_meetings");
            DropForeignKey("dbo.event_copilot_files", "url_id", "dbo.urls");
            DropForeignKey("dbo.event_copilot_files", "site_id", "dbo.sites");
            DropForeignKey("dbo.event_copilot_files", "copilot_chat_id", "dbo.event_copilot_chats");
            DropForeignKey("dbo.event_copilot_files", "file_name_id", "dbo.event_file_names");
            DropForeignKey("dbo.event_copilot_files", "file_extension_id", "dbo.event_file_ext");
            DropForeignKey("dbo.event_copilot_chats", "event_id", "dbo.audit_events");
            DropIndex("dbo.event_meta_sharepoint", new[] { "file_name_ID" });
            DropIndex("dbo.event_copilot_meetings", new[] { "meeting_id" });
            DropIndex("dbo.event_copilot_meetings", new[] { "copilot_chat_id" });
            DropIndex("dbo.event_copilot_files", new[] { "site_id" });
            DropIndex("dbo.event_copilot_files", new[] { "url_id" });
            DropIndex("dbo.event_copilot_files", new[] { "file_name_id" });
            DropIndex("dbo.event_copilot_files", new[] { "file_extension_id" });
            DropIndex("dbo.event_copilot_files", new[] { "copilot_chat_id" });
            DropIndex("dbo.event_copilot_chats", new[] { "event_id" });
            DropIndex("dbo.audit_events", new[] { "user_id" });
            DropTable("dbo.online_meetings");
            DropTable("dbo.event_copilot_meetings");
            DropTable("dbo.event_copilot_files");
            DropTable("dbo.event_copilot_chats");
            CreateIndex("dbo.event_meta_sharepoint", "file_name_id");
            CreateIndex("dbo.audit_events", "user_ID");
        }
    }
}
