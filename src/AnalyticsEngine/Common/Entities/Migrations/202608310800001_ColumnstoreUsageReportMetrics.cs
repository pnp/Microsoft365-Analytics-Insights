namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds a nonclustered COLUMNSTORE index over the columns the Copilot licence-opportunity query
    /// aggregates on the four per-user Microsoft 365 daily usage-report tables, with a covering B-tree
    /// fallback where columnstore is unavailable.
    ///
    /// <para>
    /// <b>Why.</b> <c>LicenceOpportunities</c> is the slowest step on the Copilot Adoption page (production
    /// p50 90,594 ms against a 90-second command timeout - it times out every time). Its
    /// <c>TeamsUsage</c> / <c>MailUsage</c> / <c>FileUsage</c> CTEs aggregate metric columns
    /// (<c>private_chat_count</c>, <c>email_send_count</c>, <c>viewed_or_edited</c>, ...) that the existing
    /// <c>IX_date</c> - <c>([date], [last_activity_date]) INCLUDE ([user_id])</c> - does not cover. The date
    /// range therefore cannot be served index-only and the optimiser falls back to a full clustered-index
    /// scan of each table, regardless of how narrow the selected window is. On a 200k-user tenant these
    /// tables run to tens of millions of rows each, and there are four of them.
    /// </para>
    ///
    /// <para>
    /// <b>Measured before / after</b> - the real <c>TeamsUsage</c> aggregate from
    /// <c>CopilotAdoptionSql.LicenceOpportunitiesSql</c> over a 28-day window, medians of 3 warm runs with the
    /// cold run discarded, <c>DBCC FREEPROCCACHE</c> before each run, <c>OPTION (RECOMPILE)</c>, on a
    /// synthetic <c>teams_user_activity_log</c> of 13.2M rows / 1,700 MB (200k users x 120 days at ~55% daily
    /// activity) carrying the production <c>IX_date</c> shape and a clustered PK:
    /// </para>
    /// <code>
    ///   variant                          | elapsed   | CPU       | logical reads | index size
    ///   ---------------------------------+-----------+-----------+---------------+-----------
    ///   IX_date only (before)            | 2,619 ms  | 13,032 ms | 179,062       | -
    ///   + covering B-tree NCI            | 2,174 ms  |         - |  27,541       | multi-GB at scale
    ///   + nonclustered COLUMNSTORE       | 1,388 ms  |  2,328 ms |   3,519       | 86 MB
    /// </code>
    /// <para>
    /// 179,062 reads is the whole table, confirming the scan. Columnstore wins on every axis: 51x fewer
    /// logical reads, 5.6x less CPU, 1.9x less elapsed, and 16x compression (86 MB against 1,395 MB of
    /// table). Note the covering B-tree cut reads by 85% but elapsed by only 17% - this step is CPU-bound on
    /// aggregation, not I/O-bound, and a covering B-tree does not fix a CPU-bound aggregate. That is why
    /// columnstore is the primary and the B-tree only the fallback.
    /// </para>
    ///
    /// <para>
    /// <b>Two selectivities - the benefit GROWS with the window.</b> The same <c>TeamsUsage</c> aggregate,
    /// re-run at both a narrow and a wide window on the same 13.2M-row table (medians of 3 warm runs, cold
    /// run discarded, <c>DBCC FREEPROCCACHE</c> per run, <c>OPTION (RECOMPILE)</c>):
    /// </para>
    /// <code>
    ///   window    | IX_date only (before) | + COLUMNSTORE | speed-up
    ///   ----------+-----------------------+---------------+---------
    ///    28 days  |   281 ms              |   128 ms      | 2.2x
    ///   365 days  | 2,652 ms              |   389 ms      | 6.8x
    /// </code>
    /// <para>
    /// There is no regression at either end, and the wide window - the expensive case, and the one an admin
    /// reaching for a year of history actually hits - benefits most, because a scan grows with the range
    /// while the columnstore aggregate stays close to flat. (These absolute numbers are lower than the table
    /// above because that run carried <c>SET STATISTICS IO, TIME ON</c> and a colder buffer pool; the RATIOS
    /// agree - 1.9x there against 2.2x here at 28 days - which is the claim being made.)
    /// </para>
    ///
    /// <para>
    /// <b>Build time and storage - measured</b>, on the same 13.2M-row / 1,395 MB table, so an admin can size
    /// the maintenance window. Both builds are OFFLINE on every edition (this migration does not attempt
    /// <c>ONLINE</c>), so the table is locked for the duration:
    /// </para>
    /// <code>
    ///   index built                | build time | resulting size | per 1M rows
    ///   ---------------------------+------------+----------------+-------------------
    ///   nonclustered COLUMNSTORE   | 40.6 s     |    86 MB       | ~3.1 s,  ~6.5 MB
    ///   covering B-tree (fallback) | 24.3 s     |   883 MB       | ~1.8 s, ~67 MB
    /// </code>
    /// <para>
    /// The columnstore takes ~1.7x longer to build but is <b>10x smaller</b> (86 MB against 883 MB). Repeat
    /// build measured 44.7 s, so ~40-45 s is the stable figure. Scale roughly linearly and multiply by the
    /// four tables. Note the fallback's 883 MB per table is precisely why Express is excluded below: four of
    /// those would consume a third of an Express database's 10 GB ceiling.
    /// </para>
    ///
    /// <para>
    /// <b>Availability, and why this is attempt-and-catch rather than a version check.</b> Nonclustered
    /// columnstore is available in ALL editions only from SQL Server 2016 <b>SP1</b>; 2016 RTM Standard and
    /// Express reject it, as do Azure SQL Database Basic / S0-S2 and elastic pools under 100 eDTU. Detecting
    /// an Azure service tier from T-SQL is unreliable and a hardcoded version/tier matrix rots, so each
    /// index is simply attempted through <c>sp_executesql</c> inside <c>TRY/CATCH</c> and the ladder falls
    /// back on failure - the same idiom as the ONLINE/offline ladder in
    /// <see cref="IndexAuditEventsTimeStamp"/>. The ladder per table is:
    /// columnstore -&gt; covering B-tree -&gt; leave <c>IX_date</c> alone and log.
    /// </para>
    ///
    /// <para>
    /// <b>Express is excluded from the B-tree fallback</b> (<c>EngineEdition = 4</c>). Express caps a database
    /// at 10 GB, and a multi-GB covering index across four tables could push a tenant over that limit - a
    /// far worse outcome than a slow report. Express tenants are also far below the scale at which this
    /// matters.
    /// </para>
    ///
    /// <para>
    /// <b>The two index shapes are mutually exclusive per table.</b> If either already exists the table is
    /// skipped, so a re-run after patching 2016 RTM to SP1 cannot leave a tenant carrying both a multi-GB
    /// B-tree and the columnstore index. Moving a fallback tenant onto columnstore means dropping the B-tree
    /// first, which is a deliberate manual step.
    /// </para>
    ///
    /// <para>
    /// <b>Write path.</b> These tables are written by the importer as per-(date, user) upserts with a
    /// dirty-check that skips unchanged rows, and Graph finalises its daily reports with a lag, so only the
    /// trailing few days are ever rewritten - close to a best case for columnstore. Row-by-row upserts land
    /// in the delta store, so the importer compacts the delta rowgroups after each usage-report cycle (see
    /// <c>AbstractDailyActivityLoader</c>); without that the win decays as the delta store grows.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Purely additive - no existing index, column or row is modified, and no query needs to
    /// change to benefit. Idempotent and guarded per table, <c>suppressTransaction: true</c> so each build
    /// commits independently, and progress is emitted with <c>RAISERROR ... WITH NOWAIT</c>. This migration
    /// does NOT change the EF model, so its <c>.resx</c> snapshot is a verbatim copy of its predecessor's.
    /// </para>
    /// </summary>
    public partial class ColumnstoreUsageReportMetrics : DbMigration
    {
        /// <summary>
        /// Kept as a <c>public const</c> so the shipped
        /// <c>202608310800001_ColumnstoreUsageReportMetrics.manual.sql</c> can embed it verbatim for DBAs who
        /// upgrade the database by hand rather than running the installer.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'ColumnstoreUsageReportMetrics';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @stepStart datetime2(3);
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
-- EngineEdition: 3 = Enterprise, 4 = Express (incl. LocalDB), 5 = Azure SQL DB, 8 = Azure SQL MI.
DECLARE @isExpress bit = CASE WHEN @edition = 4 THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting at ' + CONVERT(nvarchar(30), @start, 121)
         + N' UTC; EngineEdition=' + CAST(@edition AS nvarchar(10))
         + CASE WHEN @isExpress = 1
                THEN N' (Express - the B-tree fallback is disabled to protect the 10 GB database cap).'
                ELSE N'.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets TABLE (
    seq       int IDENTITY(1,1),
    tbl       sysname,
    cs_index  sysname,
    bt_index  sysname,
    cs_cols   nvarchar(1000),   -- columnstore column list
    bt_cols   nvarchar(1000)    -- B-tree fallback INCLUDE list (key is always [date])
);

INSERT @targets (tbl, cs_index, bt_index, cs_cols, bt_cols) VALUES
    (N'teams_user_activity_log',
     N'NCCI_teams_user_activity_log_metrics', N'IX_teams_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [private_chat_count], [team_chat_count], [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]',
     N'[user_id], [last_activity_date], [private_chat_count], [team_chat_count], [post_messages], [reply_messages], [meetings_attended_count], [meetings_organized_count]'),
    (N'outlook_user_activity_log',
     N'NCCI_outlook_user_activity_log_metrics', N'IX_outlook_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [email_send_count], [email_read_count]',
     N'[user_id], [last_activity_date], [email_send_count], [email_read_count]'),
    (N'sharepoint_user_activity_log',
     N'NCCI_sharepoint_user_activity_log_metrics', N'IX_sharepoint_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [viewed_or_edited]',
     N'[user_id], [last_activity_date], [viewed_or_edited]'),
    (N'onedrive_user_activity_log',
     N'NCCI_onedrive_user_activity_log_metrics', N'IX_onedrive_user_activity_log_metrics',
     N'[user_id], [date], [last_activity_date], [viewed_or_edited]',
     N'[user_id], [last_activity_date], [viewed_or_edited]');

DECLARE @seq int = 1;
DECLARE @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @csIndex sysname, @btIndex sysname, @csCols nvarchar(1000), @btCols nvarchar(1000);
DECLARE @rowCount bigint;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @csIndex = cs_index, @btIndex = bt_index, @csCols = cs_cols, @btCols = bt_cols
    FROM @targets WHERE seq = @seq;

    SET @seq += 1;

    IF OBJECT_ID(N'dbo.' + @tbl) IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    -- Mutually exclusive: if EITHER shape is already present, this table is done. Prevents a re-run
    -- after an RTM -> SP1 patch from leaving both a multi-GB B-tree and the columnstore index behind.
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name IN (@csIndex, @btIndex))
    BEGIN
        SET @msg = @migration + N': dbo.' + @tbl + N' already has a usage-metrics index; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
        CONTINUE;
    END

    SET @rowCount = (SELECT ISNULL(SUM(p.rows), 0) FROM sys.partitions p
                     WHERE p.object_id = OBJECT_ID(N'dbo.' + @tbl) AND p.index_id IN (0, 1));
    SET @stepStart = SYSUTCDATETIME();
    SET @msg = @migration + N': dbo.' + @tbl + N' (' + CAST(@rowCount AS nvarchar(20))
             + N' estimated rows) - attempting columnstore...';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- 1. Columnstore. Run through sp_executesql inside TRY/CATCH: an edition/tier rejection aborts the
    --    batch and is only catchable when the statement is executed indirectly.
    BEGIN TRY
        SET @sql = N'CREATE NONCLUSTERED COLUMNSTORE INDEX [' + @csIndex + N'] ON [dbo].[' + @tbl + N'] ('
                 + @csCols + N');';
        EXEC sp_executesql @sql;

        SET @msg = @migration + N': dbo.' + @tbl + N' - columnstore created in '
                 + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END TRY
    BEGIN CATCH
        SET @msg = @migration + N': dbo.' + @tbl + N' - columnstore unavailable (' + ERROR_MESSAGE() + N').';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END CATCH

    -- 2. Covering B-tree fallback, unless this is Express.
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @csIndex)
       AND @isExpress = 0
    BEGIN
        SET @stepStart = SYSUTCDATETIME();
        BEGIN TRY
            SET @msg = @migration + N': dbo.' + @tbl + N' - falling back to a covering B-tree index...';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;

            SET @sql = N'CREATE NONCLUSTERED INDEX [' + @btIndex + N'] ON [dbo].[' + @tbl + N'] ([date]) INCLUDE ('
                     + @btCols + N');';
            EXEC sp_executesql @sql;

            SET @msg = @migration + N': dbo.' + @tbl + N' - covering index created in '
                     + CAST(DATEDIFF(MILLISECOND, @stepStart, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END TRY
        BEGIN CATCH
            -- 3. Neither shape could be created. IX_date is untouched, so the report still works - just
            --    at its current speed. Log loudly and carry on rather than failing the whole upgrade.
            SET @msg = @migration + N': dbo.' + @tbl + N' - covering index ALSO failed ('
                     + ERROR_MESSAGE() + N'). Leaving IX_date in place; the licence-opportunity report will '
                     + N'remain slow on this server.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END CATCH
    END
END

SET @msg = @migration + N': finished in '
         + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Sql(@"
DECLARE @targets TABLE (seq int IDENTITY(1,1), tbl sysname, ix sysname);
INSERT @targets (tbl, ix) VALUES
    (N'teams_user_activity_log',      N'NCCI_teams_user_activity_log_metrics'),
    (N'teams_user_activity_log',      N'IX_teams_user_activity_log_metrics'),
    (N'outlook_user_activity_log',    N'NCCI_outlook_user_activity_log_metrics'),
    (N'outlook_user_activity_log',    N'IX_outlook_user_activity_log_metrics'),
    (N'sharepoint_user_activity_log', N'NCCI_sharepoint_user_activity_log_metrics'),
    (N'sharepoint_user_activity_log', N'IX_sharepoint_user_activity_log_metrics'),
    (N'onedrive_user_activity_log',   N'NCCI_onedrive_user_activity_log_metrics'),
    (N'onedrive_user_activity_log',   N'IX_onedrive_user_activity_log_metrics');

DECLARE @seq int = 1, @maxSeq int = (SELECT MAX(seq) FROM @targets);
DECLARE @tbl sysname, @ix sysname;

WHILE @seq <= @maxSeq
BEGIN
    SELECT @tbl = tbl, @ix = ix FROM @targets WHERE seq = @seq;
    SET @seq += 1;

    IF OBJECT_ID(N'dbo.' + @tbl) IS NOT NULL
       AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.' + @tbl) AND name = @ix)
        EXEC(N'DROP INDEX [' + @ix + N'] ON [dbo].[' + @tbl + N'];');
END
", suppressTransaction: true);
        }
    }
}
