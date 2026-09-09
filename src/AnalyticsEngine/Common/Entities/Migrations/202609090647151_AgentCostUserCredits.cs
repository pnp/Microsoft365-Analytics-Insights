namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    /// <summary>
    /// Adds <c>copilot_studio_credit_user_daily</c>: billed Copilot Studio credit consumption attributed to
    /// an individual user, per day.
    ///
    /// <para>Separate from <c>AgentCostTables</c> because it comes from a different source. Microsoft added
    /// the per-user entitlement routes (<c>/licensing/entitlements/{id}/users</c>) in July 2026; before
    /// those existed, Copilot Studio consumption could only be read per agent and per environment.</para>
    ///
    /// <para><b>Purely additive.</b> One new, initially empty table and its unique upsert index. Nothing
    /// existing is altered, dropped or rewritten and no existing query changes plan, so there is no "before"
    /// measurement to take and it is out of scope for the prove-it performance rule. Upgrade time is
    /// independent of tenant size and no maintenance window is needed.</para>
    ///
    /// <para><c>user_id</c> is a plain column with <b>no foreign key</b> to <c>dbo.users</c>: Microsoft
    /// documents it only as a string, so joining it would rest on an assumption about its format rather than
    /// a fact.</para>
    /// </summary>
    public partial class AgentCostUserCredits : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.copilot_studio_credit_user_daily",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        usage_date = c.DateTime(nullable: false),
                        user_id = c.String(maxLength: 200),
                        environment_id = c.String(maxLength: 200),
                        environment_name = c.String(maxLength: 255),
                        agent_id = c.String(maxLength: 200),
                        billed_credits = c.Decimal(nullable: false, precision: 18, scale: 6),
                        unit = c.String(maxLength: 50),
                        dimension_hash = c.String(nullable: false, maxLength: 64),
                        imported_utc = c.DateTime(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => new { t.usage_date, t.dimension_hash }, unique: true);
            
        }
        
        public override void Down()
        {
            DropIndex("dbo.copilot_studio_credit_user_daily", new[] { "usage_date", "dimension_hash" });
            DropTable("dbo.copilot_studio_credit_user_daily");
        }
    }
}
