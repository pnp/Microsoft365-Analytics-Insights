namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class CopilotAgents : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.copilot_agents",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        agent_id = c.String(),
                        name = c.String(maxLength: 100),
                    })
                .PrimaryKey(t => t.id);
            
            AddColumn("dbo.event_copilot_chats", "agent_id", c => c.Int());
            CreateIndex("dbo.event_copilot_chats", "agent_id");
            AddForeignKey("dbo.event_copilot_chats", "agent_id", "dbo.copilot_agents", "id");
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.event_copilot_chats", "agent_id", "dbo.copilot_agents");
            DropIndex("dbo.event_copilot_chats", new[] { "agent_id" });
            DropColumn("dbo.event_copilot_chats", "agent_id");
            DropTable("dbo.copilot_agents");
        }
    }
}
