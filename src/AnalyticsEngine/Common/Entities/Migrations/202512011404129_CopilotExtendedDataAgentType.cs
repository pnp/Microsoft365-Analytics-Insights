namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class CopilotExtendedDataAgentType : DbMigration
    {
        public override void Up()
        {
            AddColumn("dbo.copilot_agents", "is_custom_agent", c => c.Boolean());

            Console.WriteLine("DB SCHEMA: Applied 'Copilot agent type' succesfully.");
        }
        
        public override void Down()
        {
            DropColumn("dbo.copilot_agents", "is_custom_agent");
        }
    }
}
