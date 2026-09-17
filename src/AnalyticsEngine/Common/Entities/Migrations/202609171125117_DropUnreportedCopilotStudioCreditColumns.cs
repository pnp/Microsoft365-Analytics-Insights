namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Removes four Copilot Studio credit columns that Microsoft's billing API does not populate.
    ///
    /// WHY
    ///   A live capture of the per-agent credit response - taken from the Power Platform admin centre,
    ///   which is the only caller able to reach that route on a real tenant - carried a metadata object of
    ///   exactly <c>ResourceName</c>, <c>NonBillableQuantity</c> and <c>Users</c>. There was no LLM model,
    ///   no tool invoked, no knowledge source and no channel. Those four appear in no Microsoft REST
    ///   reference either, so the columns had no obtainable source and every row would have stayed NULL.
    ///
    ///   They were speculative when added. Keeping them meant the report offered four pivots, four grid
    ///   columns and four filters that could only ever answer "Not reported" - which an admin reads as
    ///   "this tenant did none of that" rather than "this was never reported".
    ///
    /// SCHEMA CHANGES
    ///   Dropped from dbo.copilot_studio_credit_daily
    ///     * channel_id        nvarchar(200) NULL
    ///     * llm_model         nvarchar(200) NULL
    ///     * tool_invoked      nvarchar(400) NULL
    ///     * knowledge_sources nvarchar(400) NULL
    ///
    /// CLASSIFICATION: REMOVAL. Not performance-motivated, so no before/after benchmark is owed - there is
    /// no query whose plan this changes. It is called out explicitly rather than left implicit, because an
    /// unclassified migration reads as an unmeasured one.
    ///
    /// DATA LOSS: NONE IN PRACTICE, BUT THIS IS A DROP.
    ///   The columns are unreachable from the source API, so on every deployment observed they are NULL.
    ///   The table first shipped in build 1830 (12 Sep 2026), so it does exist in customer databases - this
    ///   is deliberately not treated as a brand-new table. A drop is still the right call: the values
    ///   cannot be produced, and a column that can only be NULL is a permanent invitation to misread the
    ///   report. Nothing about money, credits or attribution is lost; these were descriptive dimensions.
    ///
    /// UPSERT KEY CHANGE
    ///   The four fields were part of <c>dimension_hash</c>. Dropping them shortens the hashed tuple, so a
    ///   database that already holds rows will INSERT a new row for a slice rather than UPDATE the old one
    ///   on the first run after upgrade, then settle from the following run onwards. That is a one-off
    ///   duplicate per slice per day, not ongoing drift - and it cannot inflate spend for anyone whose
    ///   credit import has never succeeded, which is every deployment seen so far.
    ///
    /// COST / RUNTIME
    ///   Four metadata-only DROP COLUMN operations. SQL Server does not rewrite or scan the table, so this
    ///   is instant at any size and needs no maintenance window on any edition. No index references these
    ///   columns, so none has to be rebuilt.
    ///
    /// This migration DOES change the EF entity model, so its .resx snapshot is freshly scaffolded and is
    /// now the head of the chain. The manual upgrade script therefore stamps __MigrationHistory with this
    /// new model blob rather than copying the predecessor's row.
    /// </summary>
    public partial class DropUnreportedCopilotStudioCreditColumns : DbMigration
    {
        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'DropUnreportedCopilotStudioCreditColumns'. Drops copilot_studio_credit_daily.channel_id, .llm_model, .tool_invoked and .knowledge_sources - four columns Microsoft's credit API does not populate, so they could only ever be NULL and made the report offer pivots that can never answer. Metadata-only drops: instant at any table size, no maintenance window.");

            DropColumn("dbo.copilot_studio_credit_daily", "channel_id");
            DropColumn("dbo.copilot_studio_credit_daily", "llm_model");
            DropColumn("dbo.copilot_studio_credit_daily", "tool_invoked");
            DropColumn("dbo.copilot_studio_credit_daily", "knowledge_sources");
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'DropUnreportedCopilotStudioCreditColumns'. The columns come back empty - their values were never obtainable.");

            AddColumn("dbo.copilot_studio_credit_daily", "knowledge_sources", c => c.String(maxLength: 400));
            AddColumn("dbo.copilot_studio_credit_daily", "tool_invoked", c => c.String(maxLength: 400));
            AddColumn("dbo.copilot_studio_credit_daily", "llm_model", c => c.String(maxLength: 200));
            AddColumn("dbo.copilot_studio_credit_daily", "channel_id", c => c.String(maxLength: 200));
        }
    }
}

