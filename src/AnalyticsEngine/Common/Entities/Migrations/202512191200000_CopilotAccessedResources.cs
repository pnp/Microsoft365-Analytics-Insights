namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class CopilotAccessedResources : DbMigration
    {
        public override void Up()
        {
            // ===== Accessed Resources Tables =====
            CreateTable(
                "dbo.copilot_event_accessed_resource_ids",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        resource_id = c.String(maxLength: 5000),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_event_accessed_resource_names",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        name = c.String(maxLength: 5000),
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
                "dbo.copilot_event_sensitivity_labels",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        label_id = c.String(maxLength: 100),
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
                        resource_type_id = c.Int(),
                        sensitivity_label_id = c.Int(),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.event_copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .ForeignKey("dbo.copilot_event_accessed_resource_ids", t => t.resource_id_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_names", t => t.resource_name_id)
                .ForeignKey("dbo.copilot_event_accessed_resource_types", t => t.resource_type_id)
                .ForeignKey("dbo.copilot_event_sensitivity_labels", t => t.sensitivity_label_id)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.resource_id_id)
                .Index(t => t.resource_name_id)
                .Index(t => t.resource_type_id)
                .Index(t => t.sensitivity_label_id);
            

            // ===== Messages Tables =====
            CreateTable(
                "dbo.copilot_event_messages",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        copilot_chat_id = c.Guid(nullable: false),
                        message_id = c.String(maxLength: 500),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.event_copilot_chats", t => t.copilot_chat_id, cascadeDelete: true)
                .Index(t => t.copilot_chat_id)
                .Index(t => t.message_id);
            
            Console.WriteLine("DB SCHEMA: All Copilot extended data tables created successfully.");
        }
        
        public override void Down()
        {
            // ===== Drop Messages =====
            DropForeignKey("dbo.copilot_event_messages", "copilot_chat_id", "dbo.event_copilot_chats");
            DropIndex("dbo.copilot_event_messages", new[] { "message_id" });
            DropIndex("dbo.copilot_event_messages", new[] { "copilot_chat_id" });
            DropTable("dbo.copilot_event_messages");
            
            // ===== Drop Accessed Resources =====
            DropForeignKey("dbo.copilot_event_accessed_resources", "sensitivity_label_id", "dbo.copilot_event_sensitivity_labels");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_type_id", "dbo.copilot_event_accessed_resource_types");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_name_id", "dbo.copilot_event_accessed_resource_names");
            DropForeignKey("dbo.copilot_event_accessed_resources", "resource_id_id", "dbo.copilot_event_accessed_resource_ids");
            DropForeignKey("dbo.copilot_event_accessed_resources", "copilot_chat_id", "dbo.event_copilot_chats");
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "sensitivity_label_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_type_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_name_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "resource_id_id" });
            DropIndex("dbo.copilot_event_accessed_resources", new[] { "copilot_chat_id" });
            DropTable("dbo.copilot_event_accessed_resources");
            DropTable("dbo.copilot_event_sensitivity_labels");
            DropTable("dbo.copilot_event_accessed_resource_types");
            DropTable("dbo.copilot_event_accessed_resource_names");
            DropTable("dbo.copilot_event_accessed_resource_ids");
            
            Console.WriteLine("DB SCHEMA: Rolled back all Copilot extended data tables.");
        }
    }
}
