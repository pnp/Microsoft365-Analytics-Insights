namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;
    
    /// <summary>
    /// Adds the agent-cost tables: billed Copilot Studio credit consumption, the tenant Copilot Credits
    /// entitlement snapshot, daily Azure spend from Microsoft Cost Management, and the shared import log.
    ///
    /// <para><b>Purely additive.</b> Four new, initially empty tables and their two unique indexes. Nothing
    /// existing is altered, dropped or rewritten, and no existing query changes plan - so there is no "before"
    /// measurement to take and this migration is out of scope for the prove-it performance rule. It is safe to
    /// apply online: the only locks are on tables no query touches yet.</para>
    ///
    /// <para>Upgrade time is independent of tenant size - creating empty tables is O(1), so this is seconds on
    /// a 200k-user tenant just as it is on a small one.</para>
    ///
    /// <para>The unique indexes on <c>(usage_date, dimension_hash)</c> and <c>(usage_date, row_hash)</c> are
    /// not an optimisation: they are the upsert keys. Both sources restate history (Azure re-estimates an open
    /// billing period several times a day; Copilot Studio recalculates as usage days settle), so both
    /// importers re-read a trailing window, and without these a re-read would duplicate every restated day.
    /// The usage date leads so a date-range report still seeks.</para>
    /// </summary>
    public partial class AgentCostTables : DbMigration
    {
        public override void Up()
        {
            CreateTable(
                "dbo.agent_cost_import_log",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        import_name = c.String(maxLength: 100),
                        imported_utc = c.DateTime(nullable: false),
                        window_from = c.DateTime(),
                        window_to = c.DateTime(),
                        rows_read = c.Int(nullable: false),
                        rows_saved = c.Int(nullable: false),
                        error = c.String(maxLength: 1000),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.azure_cost_daily",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        usage_date = c.DateTime(nullable: false),
                        scope = c.String(maxLength: 400),
                        subscription_id = c.String(maxLength: 100),
                        resource_id = c.String(maxLength: 850),
                        resource_group = c.String(maxLength: 255),
                        service_name = c.String(maxLength: 255),
                        meter_category = c.String(maxLength: 255),
                        meter_sub_category = c.String(maxLength: 255),
                        meter_name = c.String(maxLength: 255),
                        cost = c.Decimal(nullable: false, precision: 18, scale: 6),
                        currency = c.String(maxLength: 10),
                        quantity = c.Decimal(precision: 18, scale: 6),
                        is_estimated = c.Boolean(nullable: false),
                        row_hash = c.String(nullable: false, maxLength: 64),
                        imported_utc = c.DateTime(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => new { t.usage_date, t.row_hash }, unique: true);
            
            CreateTable(
                "dbo.copilot_studio_credit_capacity",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        snapshot_utc = c.DateTime(nullable: false),
                        consumption_as_of = c.DateTime(),
                        entitled = c.Decimal(precision: 18, scale: 6),
                        consumed = c.Decimal(precision: 18, scale: 6),
                        consumption_type = c.String(maxLength: 50),
                        allocated = c.Decimal(precision: 18, scale: 6),
                        available = c.Decimal(precision: 18, scale: 6),
                        pay_as_you_go_consumed = c.Decimal(precision: 18, scale: 6),
                        status = c.String(maxLength: 50),
                    })
                .PrimaryKey(t => t.id);
            
            CreateTable(
                "dbo.copilot_studio_credit_daily",
                c => new
                    {
                        id = c.Int(nullable: false, identity: true),
                        usage_date = c.DateTime(nullable: false),
                        environment_id = c.String(maxLength: 200),
                        environment_name = c.String(maxLength: 255),
                        agent_id = c.String(maxLength: 200),
                        agent_name = c.String(maxLength: 255),
                        harness = c.String(maxLength: 50),
                        feature_name = c.String(maxLength: 200),
                        channel_id = c.String(maxLength: 200),
                        llm_model = c.String(maxLength: 200),
                        tool_invoked = c.String(maxLength: 400),
                        knowledge_sources = c.String(maxLength: 400),
                        billed_credits = c.Decimal(nullable: false, precision: 18, scale: 6),
                        non_billed_credits = c.Decimal(precision: 18, scale: 6),
                        distinct_users = c.Int(),
                        last_refreshed_utc = c.DateTime(),
                        dimension_hash = c.String(nullable: false, maxLength: 64),
                        imported_utc = c.DateTime(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .Index(t => new { t.usage_date, t.dimension_hash }, unique: true);
            
        }
        
        public override void Down()
        {
            DropIndex("dbo.copilot_studio_credit_daily", new[] { "usage_date", "dimension_hash" });
            DropIndex("dbo.azure_cost_daily", new[] { "usage_date", "row_hash" });
            DropTable("dbo.copilot_studio_credit_daily");
            DropTable("dbo.copilot_studio_credit_capacity");
            DropTable("dbo.azure_cost_daily");
            DropTable("dbo.agent_cost_import_log");
        }
    }
}
