namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class CopilotExtendedData : DbMigration
    {
        public override void Up()
        {
            RenameTable(name: "dbo.event_copilot_chats", newName: "copilot_chats");
            RenameTable(name: "dbo.event_copilot_files", newName: "copilot_event_files");
            RenameTable(name: "dbo.event_copilot_meetings", newName: "copilot_event_meetings");
            CreateTable(
                "dbo.copilot_event_accessed_resource_ids",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        resource_id = c.String(),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_accessed_resource_names",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_accessed_resource_site_urls",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        site_url = c.String(),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_accessed_resource_types",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_ai_models",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_accessed_resources",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        copilot_chat_id = c.Guid(nullable: false),
                        resource_id_id = c.Int(),
                        resource_name_id = c.Int(),
                        resource_site_url_id = c.Int(),
                        resource_type_id = c.Int(),
                        sensitivity_label_id = c.Int(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .ForeignKey("dbo.copilot_event_accessed_resource_ids", t => t.resource_id_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_names", t => t.resource_name_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_site_urls", t => t.resource_site_url_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_types", t => t.resource_type_id)
                .ForeignKey("dbo.sensitivity_labels", t => t.sensitivity_label_id)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.resource_id_id)
                .Index(t => t.resource_name_id)
                .Index(t => t.resource_site_url_id)
                .Index(t => t.resource_type_id)
                .Index(t => t.sensitivity_label_id);
            
            CreateTable(
                "dbo.sensitivity_labels",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        label_id = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_ai_models",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        copilot_chat_id = c.Guid(nullable: false),
                        model_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_ai_models", t => t.model_id, cascadeDelete: true)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.model_id);
            
            CreateTable(
                "dbo.copilot_event_messages",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        copilot_chat_id = c.Guid(nullable: false),
                        message_id = c.String(maxLength: 500),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id);
            
            AddColumn("dbo.copilot_chats", "copilot_credit_estimate_total", c => c.Int());
            AddColumn("dbo.copilot_chats", "copilot_credit_estimate_json", c => c.String());

            Console.WriteLine("DB SCHEMA: Rolled back all Copilot extended data tables.");
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.copilot_event_messages", "copilot_chat_id", "dbo.copilot_chats");
            DropForeignKey("dbo.copilot_event_ai_models", "copilot_chat_id", "dbo.copilot_chats");
            DropForeignKey("dbo.copilot_event_ai_models", "model_id", "dbo.copilot_ai_models");
            DropForeignKey("dbo.copilot_event_accessed_resources", "sensitivity_label_id", "dbo.sensitivity_labels");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_type_id", "dbo.copilot_event_accessed_resource_types");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_site_url_id", "dbo.copilot_event_accessed_resource_site_urls");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_name_id", "dbo.copilot_event_accessed_resource_names");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_id_id", "dbo.copilot_event_accessed_resource_ids");
            DropForeignKey("dbo.copilot_event_accessed_resources", "copilot_chat_id", "dbo.copilot_chats");
            DropIndex("dbo.copilot_event_messages", new[] { "copilot_chat_id" });
            DropIndex("dbo.copilot_event_ai_models", new[] { "model_id" });
            DropIndex("dbo.copilot_event_ai_models", new[] { "copilot_chat_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "sensitivity_label_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_type_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_site_url_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_name_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_id_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "copilot_chat_id" });
            DropColumn("dbo.copilot_chats", "copilot_credit_estimate_json");
            DropColumn("dbo.copilot_chats", "copilot_credit_estimate_total");
            DropTable("dbo.copilot_event_messages");
            DropTable("dbo.copilot_event_ai_models");
            DropTable("dbo.sensitivity_labels");
            DropTable("dbo.copilot_event_accessed_resources");
            DropTable("dbo.copilot_ai_models");
            DropTable("dbo.copilot_event_accessed_resource_types");
            DropTable("dbo.copilot_event_accessed_resource_site_urls");
            DropTable("dbo.copilot_event_accessed_resource_names");
            DropTable("dbo.copilot_event_accessed_resource_ids");
            RenameTable(name: "dbo.copilot_event_meetings", newName: "event_copilot_meetings");
            RenameTable(name: "dbo.copilot_event_files", newName: "event_copilot_files");
            RenameTable(name: "dbo.copilot_chats", newName: "event_copilot_chats");
        }
    }
}
