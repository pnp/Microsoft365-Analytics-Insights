namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Builds the two foreign-key indexes on <c>dbo.copilot_event_accessed_resources</c> for the
    /// <c>action_id</c> and <c>list_item_unique_id_id</c> columns added by
    /// <see cref="CopilotDroppedAuditFields"/>.
    ///
    /// WHY THIS IS ITS OWN MIGRATION
    ///   These indexes were originally the last step of <see cref="CopilotDroppedAuditFields"/>, issued as
    ///   <c>Sql(..., suppressTransaction: true)</c> because on a Copilot-heavy tenant this junction table is
    ///   the largest in the schema and the build must not be held inside the migration transaction.
    ///
    ///   That made the whole migration non-resumable. EF commits every operation preceding a
    ///   transaction-suppressed statement, so an index build that failed - out of disk, log full, cancelled
    ///   session, killed connection - left five tables, nine columns and six foreign keys COMMITTED while
    ///   the migration itself was never stamped in <c>__MigrationHistory</c>. The retry then re-ran the
    ///   unconditional <c>CreateTable</c> operations and failed immediately on objects that already
    ///   existed, so the upgrade could never converge without hand repair.
    ///
    ///   Splitting the two apart fixes that without renumbering anything already applied:
    ///     * CopilotDroppedAuditFields is now a single atomic transaction - it either applies and stamps,
    ///       or rolls back completely.
    ///     * This migration contains ONLY the guarded, idempotent index SQL, so if it is interrupted a
    ///       re-run simply builds whatever is still missing and stamps.
    ///
    /// NOT PERFORMANCE-MOTIVATED, so no before/after benchmark is required by the schema-change policy.
    /// These follow the table's existing convention of one index per foreign-key column; the FK
    /// constraints themselves are created by the predecessor and do not depend on these indexes.
    ///
    /// RUNTIME
    ///   Measured at synthetic scale (offline build, buffer pool dropped before each build, medians of 3
    ///   runs) on a 3,000,000-row junction table: about 2.5 s / 40.7 MB for the action_id index and
    ///   3.1 s / 40.7 MB for list_item_unique_id_id - roughly 5.6 s and 81 MB for the pair. Extrapolating
    ///   as O(n log n) that is about 2 s / 27 MB at 1M rows and a few minutes at 100M. ONLINE is attempted
    ///   on Enterprise / Azure SQL DB / Azure SQL MI; on other editions each build briefly locks the table,
    ///   so run large upgrades in a maintenance window with the importer stopped.
    ///
    /// The EF entity model is unchanged by this migration - indexes on existing columns are physical only -
    /// so its .resx snapshot is a byte-identical copy of its predecessor's
    /// (202608210700003_IndexCopilotInteractionsDedupWindow), per the repo's migration rules. The manual
    /// upgrade script therefore stamps __MigrationHistory by copying the predecessor's row.
    /// </summary>
    public partial class IndexCopilotAccessedResourceFkColumns : DbMigration
    {
        /// <summary>
        /// The index build, verbatim. Exposed as a constant so the manual upgrade script and the unit tests
        /// run exactly the same SQL. Idempotent, guarded and edition-aware: the ONLINE attempt goes through
        /// sp_executesql inside TRY/CATCH, which is what makes the "ONLINE is Enterprise only" error
        /// catchable rather than batch-aborting, with a plain offline build as the fallback.
        /// </summary>
        public const string Up_Sql = CopilotDroppedAuditFields.JunctionIndexes_Sql;

        /// <summary>Drops both indexes if present. Guarded, so it is safe on a database that never had them.</summary>
        public const string Down_Sql = CopilotDroppedAuditFields.JunctionIndexesDown_Sql;

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexCopilotAccessedResourceFkColumns'. Builds the two foreign-key indexes on copilot_event_accessed_resources (action_id, list_item_unique_id_id). On Copilot-heavy tenants this is the largest table in the schema, so the build runs outside the migration transaction - ONLINE where the edition supports it, offline otherwise. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT). Guarded and idempotent: an interrupted run converges on re-run.");

            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexCopilotAccessedResourceFkColumns'.");

            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
