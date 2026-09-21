namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Removes the Copilot Adoption closed-period machinery: stored period facts, customer-defined
    /// targets, frozen cohorts, interventions and the scheduled digest send ledger.
    ///
    /// CLASSIFICATION: REMOVAL. No query is being tuned and no table is being rewritten, so the
    /// performance-benchmark rule for performance-motivated schema changes does not apply. There is
    /// nothing to measure: the code that read these tables no longer exists.
    ///
    /// WHY: comparison over time has been taken out of the product. The Copilot Adoption tool now
    /// does two things - show adoption as it is now, and export a complete workbook - and comparison
    /// is done by diffing two exported workbooks rather than by storing history in the database. The
    /// tables below have no remaining reader or writer in the product.
    ///
    /// WHY DROP RATHER THAN ORPHAN: these tables have no remaining reader or writer. Left in place
    /// they would keep being created on every fresh install by <c>Create DB.sql</c>, keep appearing
    /// in the Health page's schema inventory, and in the case of
    /// <c>copilot_adoption_user_period</c> - one row per licensed user per published period - would
    /// grow without bound with nothing reading them. Both <c>copilot_adoption_user_period</c> and
    /// <c>copilot_adoption_cohort_member</c> also carry an <c>ON DELETE CASCADE</c> foreign key to
    /// <c>dbo.users</c>, so leaving them would keep charging every user deletion for a cascade into
    /// a table nothing reads.
    ///
    /// THIS IS DESTRUCTIVE AND IRREVERSIBLE. Any published period history, target, cohort,
    /// intervention or digest send record is deleted. <see cref="Down"/> recreates the tables empty
    /// so a rolled-back build finds the schema it expects, but it cannot restore a single row. Each
    /// drop announces itself with <c>RAISERROR ... WITH NOWAIT</c> and reports the row count
    /// destroyed, so the SQL session log records exactly what was lost.
    ///
    /// WHAT MAY ACTUALLY BE IN THEM: <c>copilot_adoption_period_run</c>,
    /// <c>copilot_adoption_user_period</c>, <c>copilot_adoption_targets</c> and
    /// <c>copilot_adoption_digest_run</c> only ever populated on a deployment that had configured
    /// the Copilot Adoption digest email, because the digest was the sole caller of the publish path
    /// and a target could not be created without a published period. <b>The cohort and intervention
    /// tables are different:</b> the portal's "Start intervention" button built them from the live
    /// analysis and needed no digest and no published period, so those three tables can hold records
    /// on any deployment. Take a backup if that matters.
    ///
    /// The EF model is unchanged: none of these tables ever had an entity or a <c>DbSet</c> - they
    /// were written and read entirely through raw SQL. So the <c>.resx</c> snapshot is a
    /// byte-identical copy of <see cref="IndexPlatformUserActivityLogDate"/>'s, EF sees
    /// <c>model == latest snapshot</c>, and <c>AutomaticDataLossException</c> cannot arise.
    ///
    /// Safety: every drop is guarded by <c>OBJECT_ID(...) IS NOT NULL</c> and so is a no-op on a
    /// database that never had them (the overwhelmingly common case) and on a re-run. Child tables
    /// are dropped before their parents so the foreign keys never block. Runs outside the EF
    /// transaction (<c>suppressTransaction: true</c>) so an interrupted upgrade converges on re-run.
    /// Drops are metadata operations and are fast regardless of row count, so this migration needs
    /// no maintenance window.
    /// </summary>
    public partial class DropCopilotAdoptionPeriodTables : DbMigration
    {
        /// <summary>
        /// Tables dropped, in foreign-key-safe order: children before parents.
        /// </summary>
        /// <remarks>
        /// <c>copilot_adoption_intervention</c> and <c>copilot_adoption_cohort_member</c> both carry
        /// a cascading FK to <c>copilot_adoption_cohort</c>, and <c>copilot_adoption_user_period</c>
        /// and <c>copilot_adoption_cohort_member</c> carry one to <c>dbo.users</c>. Dropping in this
        /// order means no constraint has to be dropped separately.
        /// </remarks>
        public static readonly string[] Tables =
        {
            "copilot_adoption_intervention",
            "copilot_adoption_cohort_member",
            "copilot_adoption_cohort",
            "copilot_adoption_user_period",
            "copilot_adoption_period_run",
            "copilot_adoption_targets",
            "copilot_adoption_digest_run",
        };

        /// <summary>
        /// SQL executed by <see cref="Up"/>. Exposed as a constant so the unit tests and the manual
        /// upgrade script run exactly the same statements.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DropCopilotAdoptionPeriodTables';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @table sysname;
DECLARE @rows bigint;
DECLARE @sql nvarchar(max);

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121)
    + N' UTC. Copilot Adoption no longer stores period history; comparison is done by diffing two '
    + N'exported workbooks. The tables below are dropped and their contents are NOT recoverable.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Children before parents, so the cascading foreign keys to copilot_adoption_cohort and dbo.users
-- never block a drop and no constraint has to be dropped on its own.
DECLARE @drop TABLE (seq int IDENTITY(1,1) NOT NULL, name sysname NOT NULL);
INSERT INTO @drop (name) VALUES
    (N'copilot_adoption_intervention'),
    (N'copilot_adoption_cohort_member'),
    (N'copilot_adoption_cohort'),
    (N'copilot_adoption_user_period'),
    (N'copilot_adoption_period_run'),
    (N'copilot_adoption_targets'),
    (N'copilot_adoption_digest_run');

DECLARE tables CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @drop ORDER BY seq;
OPEN tables;
FETCH NEXT FROM tables INTO @table;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist, nothing to do.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        -- Reported before the drop so the session log records what was destroyed. Read from
        -- sys.partitions rather than COUNT(*) because it is O(1) and this is a log line, not a
        -- decision - nothing branches on the value.
        SET @rows = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions AS p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @table) AND p.index_id IN (0, 1));
        SET @msg = @migration + N': dropping dbo.' + @table + N' (' + CAST(@rows AS nvarchar(20))
            + N' row(s)). This is irreversible.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;

        SET @sql = N'DROP TABLE [dbo].[' + @table + N'];';
        EXEC sp_executesql @sql;
    END

    FETCH NEXT FROM tables INTO @table;
END

CLOSE tables;
DEALLOCATE tables;

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'DropCopilotAdoptionPeriodTables'. Removes the Copilot Adoption closed-period machinery - copilot_adoption_period_run, copilot_adoption_user_period, copilot_adoption_targets, copilot_adoption_cohort, copilot_adoption_cohort_member, copilot_adoption_intervention and copilot_adoption_digest_run. Comparison over time is no longer stored in the database; export the Excel workbook twice and diff the two files instead. THIS IS IRREVERSIBLE: any stored period history, target, cohort, intervention or digest send record is deleted. Drops are metadata operations, so this is fast at any row count and needs no maintenance window. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        /// <summary>
        /// Recreates the seven tables, empty, by replaying the original creating migrations' SQL.
        /// </summary>
        /// <remarks>
        /// <para><b>The rows are not restored and cannot be.</b> What this buys is that rolling the
        /// application back to the previous release does not leave it running against a schema it does
        /// not expect: the previous build's digest phase, target, cohort and intervention code all
        /// issue raw SQL against these tables, and most of those statements are not guarded by an
        /// existence check. A <c>Down()</c> that did nothing would let EF remove this migration's
        /// <c>__MigrationHistory</c> row - reporting the database as being at the predecessor - while
        /// the tables that predecessor requires stayed dropped.</para>
        ///
        /// <para>The SQL is taken from the creating migrations' own <c>Up_Sql</c> constants rather than
        /// copied, so the recreated schema cannot drift from the original definition. Each of those is
        /// already guarded (<c>IF OBJECT_ID(...) IS NULL</c>) and therefore idempotent, and they are
        /// replayed parents-before-children so the foreign keys resolve.</para>
        /// </remarks>
        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'DropCopilotAdoptionPeriodTables'. The seven Copilot Adoption period, target, cohort, intervention and digest tables are recreated EMPTY so a rolled-back application build finds the schema it expects. Their previous contents were destroyed by the Up migration and are NOT restored - recover them from a database backup taken before the upgrade if they are needed.");

            // Parents before children, the mirror of the drop order in Up_Sql: the cohort table has to
            // exist before its member and intervention tables can take a foreign key to it.
            Sql(CopilotAdoptionPeriodFacts.Up_Sql, suppressTransaction: true);
            Sql(CopilotAdoptionTargets.Up_Sql, suppressTransaction: true);
            Sql(CopilotAdoptionCohorts.Up_Sql, suppressTransaction: true);
            Sql(CopilotAdoptionInterventions.Up_Sql, suppressTransaction: true);
            Sql(CopilotAdoptionDigest.Up_Sql, suppressTransaction: true);
        }
    }
}
