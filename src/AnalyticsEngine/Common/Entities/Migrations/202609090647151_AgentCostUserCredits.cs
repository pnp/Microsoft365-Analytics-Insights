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
    /// <para><c>user_id</c> is a real foreign key to <c>dbo.users</c>, resolved by the importer from the
    /// Entra object id the licensing API returns and <c>dbo.users.azure_ad_id</c>, which the user import
    /// already populates from Graph's <c>user.id</c>. The raw identifier is kept alongside it in
    /// <c>entra_object_id</c>.</para>
    ///
    /// <para>The key is <b>nullable and does not cascade</b>. Nullable because a billing row must survive a
    /// user who cannot be resolved - deleted from the directory, or created since the last user import - and
    /// is re-resolved on later cycles. Non-cascading because deleting a user must not silently erase spend
    /// history and change historical totals; production never deletes users, so this only ever surfaces as a
    /// loud failure rather than quiet data loss.</para>
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
                        entra_object_id = c.String(maxLength: 200),
                        user_id = c.Int(),
                        environment_id = c.String(maxLength: 200),
                        environment_name = c.String(maxLength: 255),
                        agent_id = c.String(maxLength: 200),
                        billed_credits = c.Decimal(nullable: false, precision: 18, scale: 6),
                        unit = c.String(maxLength: 50),
                        dimension_hash = c.String(nullable: false, maxLength: 64),
                        imported_utc = c.DateTime(nullable: false),
                    })
                .PrimaryKey(t => t.id)
                .ForeignKey("dbo.users", t => t.user_id)
                .Index(t => new { t.usage_date, t.dimension_hash }, unique: true)
                .Index(t => t.user_id)
                .Index(t => t.entra_object_id);
            
        }
        
        public override void Down()
        {
            DropForeignKey("dbo.copilot_studio_credit_user_daily", "user_id", "dbo.users");
            DropIndex("dbo.copilot_studio_credit_user_daily", new[] { "entra_object_id" });
            DropIndex("dbo.copilot_studio_credit_user_daily", new[] { "user_id" });
            DropIndex("dbo.copilot_studio_credit_user_daily", new[] { "usage_date", "dimension_hash" });
            DropTable("dbo.copilot_studio_credit_user_daily");
        }
    }
}
