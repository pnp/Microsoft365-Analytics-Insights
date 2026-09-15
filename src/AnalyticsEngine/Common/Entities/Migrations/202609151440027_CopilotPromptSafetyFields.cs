namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Persists the two Copilot prompt-safety flags the importer was throwing away: a jailbreak attempt
    /// on a prompt message, and a Cross-Prompt Injection Attack (XPIA) detected from an accessed
    /// resource. Both come from the Copilot interaction audit record the importer already downloads -
    /// no new API, permission or import-cycle cost.
    ///
    /// Issue #570. These are documented on the Purview audit schema page
    /// (https://learn.microsoft.com/en-us/purview/audit-copilot) but absent from the older OData
    /// Management API schema the models were originally written against, which is how both were missed.
    /// They failed differently and it is worth keeping the distinction:
    ///
    ///   * XPIADetected was already a property on the deserialisation model and already reached the
    ///     staging JSON - the merge simply never extracted it.
    ///   * JailbreakDetected was not modelled at all, so Newtonsoft discarded it during typed
    ///     deserialisation and it never reached staging.
    ///
    /// Neither was ever LOST, which is why this is a normalisation gap rather than data loss: the
    /// importer stores the whole raw CopilotEventData payload in audit_events.event_data, and that
    /// serialisation runs off a `dynamic` parse rather than the typed model, so it keeps fields we never
    /// declared. The flags have therefore always been present in that blob - just not queryable without
    /// shredding JSON. See BACKFILL below.
    ///
    /// SCHEMA CHANGES
    ///   New columns (both NULLable bit, no default, no index, no foreign key)
    ///     * copilot_event_messages.jailbreak_detected
    ///     * copilot_event_accessed_resources.xpia_detected
    ///
    /// CLASSIFICATION: PURELY ADDITIVE. This migration is not performance-motivated and carries no
    /// before/after benchmark, because there is no "before" query to measure - it adds two NULLable
    /// columns and nothing else. Per the repo's schema-change rule, additive migrations are explicitly
    /// out of scope for the measurement requirement; saying so is part of the review rather than an
    /// omission from it.
    ///
    /// COST / RUNTIME
    ///   Both AddColumn operations add a NULLable column with no default, which SQL Server applies as a
    ///   metadata-only change: instant regardless of table size, including a 100M-row
    ///   copilot_event_accessed_resources. No table rewrite, no scan, no index build, and so no
    ///   maintenance window is required on any edition - which is the significant difference from
    ///   <see cref="CopilotDroppedAuditFields"/>, whose foreign keys DID force a Sch-M-locked scan.
    ///
    /// DE-DUPLICATION IMPACT: NONE, DELIBERATELY.
    ///   xpia_detected is payload, not identity. IX_copilot_event_accessed_resources_dedup is left
    ///   exactly as <see cref="WidenCopilotAccessedResourceDedupIndex"/> built it, and the merge's
    ///   accessed-resource existence check still matches on the same seven resolved columns. The merge
    ///   aggregates the flag per resolved tuple (MAX) instead of joining on it, so no index had to be
    ///   widened and no measurement is owed. Had it been added to the tuple this would have become a
    ///   performance-motivated index rebuild on the largest table in the schema, needing the full
    ///   before/after evidence - a much more expensive change for a signal that is not part of a row's
    ///   identity.
    ///
    ///   The consequence, consistent with how action_id already behaves, is first-write-wins: a resource
    ///   tuple already stored keeps its existing xpia_detected rather than being revised by a re-staged
    ///   copy of the same interaction. See the note in common_upsert_copilot_agents.sql.
    ///
    /// BACKFILL: NOT DONE HERE, BUT POSSIBLE.
    ///   Existing rows keep NULL. Unlike <see cref="CopilotDroppedAuditFields"/> - which genuinely could
    ///   not be backfilled, because Management Activity API content is only retrievable for 7 days - the
    ///   values for historic rows are already on disk in audit_events.event_data and could be recovered
    ///   with JSON_VALUE. That is deliberately left out of this migration: it would be an unbounded
    ///   UPDATE over the two largest Copilot tables, which is exactly the kind of long-running data
    ///   movement the repo's migration rules say to keep out of the upgrade path. If it is ever wanted it
    ///   belongs in a separate, batched, resumable migration with its own measurement.
    ///
    /// This migration DOES change the EF entity model, so its .resx snapshot is freshly scaffolded (it is
    /// not a copy of the predecessor's). The manual upgrade script therefore has to stamp
    /// __MigrationHistory with this new model blob rather than copying the previous row.
    ///
    /// Because this is now the HEAD of the migration chain, its snapshot is also the one EF compares the
    /// live model against at runtime - so it necessarily carries every model change made since the
    /// predecessor's own (deliberately reused) snapshot was taken. Concretely that is the switch of the EF
    /// provider from <c>System.Data.SqlClient</c> to <c>Microsoft.Data.SqlClient</c>, which came with
    /// SPOInsightsDBConfiguration deriving from MicrosoftSqlDbConfiguration and is baked into the SSDL.
    /// That is correct and intentional: a raw-SQL migration may reuse a stale snapshot, but the head of
    /// the chain may not, or EF would see the live model as divergent and try to auto-migrate the
    /// difference.
    ///
    /// Note that <c>dbo.users.created_utc</c>, added by <see cref="CopilotReclaimEligibilityInputs"/>, is
    /// deliberately NOT in this snapshot: it has no EF property at all and is written only by the bulk-SQL
    /// user-update path. Having no property is what let that migration reuse its predecessor's snapshot,
    /// and it is why the column does not appear here either.
    ///
    /// Left as a single atomic EF transaction - no Sql(..., suppressTransaction: true) - because both
    /// operations are instant metadata changes. There is nothing long-running to make resumable, so the
    /// migration either applies and stamps or rolls back entirely, and a retry starts clean either way.
    /// </summary>
    public partial class CopilotPromptSafetyFields : DbMigration
    {
        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotPromptSafetyFields'. Adds the two Copilot prompt-safety flags the importer parsed but never stored: copilot_event_messages.jailbreak_detected and copilot_event_accessed_resources.xpia_detected. Both are NULLable columns with no default, which SQL Server applies as a metadata-only change - instant even on very large tables. No backfill: existing rows keep NULL.");

            AddColumn("dbo.copilot_event_accessed_resources", "xpia_detected", c => c.Boolean());
            AddColumn("dbo.copilot_event_messages", "jailbreak_detected", c => c.Boolean());
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'CopilotPromptSafetyFields'.");

            DropColumn("dbo.copilot_event_messages", "jailbreak_detected");
            DropColumn("dbo.copilot_event_accessed_resources", "xpia_detected");
        }
    }
}
