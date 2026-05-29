namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    public partial class AddSentEmailTables : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.email_addresses",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        address = c.String(nullable: false, maxLength: 450),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => t.address, unique: true);
            
            CreateTable(
                "dbo.sent_email_recipients",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        sent_email_id = c.Int(nullable: false),
                        recipient_address_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.email_addresses", t => t.recipient_address_id)
                .ForeignKey("dbo.sent_emails", t => t.sent_email_id, cascadeDelete: true)
                .Index(t => new { t.sent_email_id, t.recipient_address_id }, unique: true);
            
            CreateTable(
                "dbo.sent_emails",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        subject = c.String(maxLength: 1000),
                        sent_date = c.DateTime(nullable: false),
                        graph_message_id = c.String(nullable: false, maxLength: 450),
                        cognitive_score = c.Double(),
                        from_address_id = c.Int(nullable: false),
                        user_id = c.Int(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.email_addresses", t => t.from_address_id)
                .ForeignKey("dbo.users", t => t.user_id, cascadeDelete: true)
                .Index(t => t.graph_message_id, unique: true)
                .Index(t => t.from_address_id)
                .Index(t => t.user_id);
            
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.sent_email_recipients", "sent_email_id", "dbo.sent_emails");
            DropForeignKey("dbo.sent_emails", "user_id", "dbo.users");
            DropForeignKey("dbo.sent_emails", "from_address_id", "dbo.email_addresses");
            DropForeignKey("dbo.sent_email_recipients", "recipient_address_id", "dbo.email_addresses");
            DropIndex("dbo.sent_emails", new[] { "user_id" });
            DropIndex("dbo.sent_emails", new[] { "from_address_id" });
            DropIndex("dbo.sent_emails", new[] { "graph_message_id" });
            DropIndex("dbo.sent_email_recipients", new[] { "sent_email_id", "recipient_address_id" });
            DropIndex("dbo.email_addresses", new[] { "address" });
            DropTable("dbo.sent_emails");
            DropTable("dbo.sent_email_recipients");
            DropTable("dbo.email_addresses");
        }
    }
}
