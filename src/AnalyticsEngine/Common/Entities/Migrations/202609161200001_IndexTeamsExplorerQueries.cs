namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds the date indexes the Teams Explorer's tabs need, and widens the existing
    /// <c>IX_date</c> on <c>teams_user_device_usage_log</c> to cover the device-mix aggregate.
    ///
    /// <para>
    /// <b>Why.</b> Four of the tables the Teams Explorer reads had no seekable access path for a date
    /// range. <c>teams_channel_stats_log</c>, <c>teams_user_channel_reactions</c> and
    /// <c>team_membership_log</c> carry only their foreign-key indexes, so every window - however
    /// narrow - scanned the whole table. <c>teams_user_device_usage_log</c> does have an
    /// <c>IX_date</c>, created by the installer's profiling schema script, but it is key-only, so the
    /// device-mix query seeked the date and then paid a clustered-index lookup per row to read eight
    /// <c>bit</c> columns.
    /// </para>
    ///
    /// <para>
    /// <b>Measured before / after.</b> The real queries from
    /// <see cref="Common.Entities.TeamsExplorer.TeamsExplorerSql"/>, run by
    /// <c>Benchmarks/Invoke-TeamsExplorerIndexBenchmark.ps1</c> against the synthetic fixture in
    /// <c>Benchmarks/TeamsExplorerIndexBenchmark.sql</c> (10,000 users over 400 days: 4.0M device-usage
    /// rows, 800k channel-stat rows, 267k reactions, 400k calls). Medians of four warm runs, cold run
    /// discarded, <c>DBCC FREEPROCCACHE</c> before each run, <c>OPTION (RECOMPILE)</c>:
    /// </para>
    /// <code>
    ///   query / table                          window | logical reads    | elapsed        | plan
    ///   ---------------------------------------------+------------------+----------------+---------------
    ///   device mix (teams_user_device_usage_log) 28d  | 17,383 ->    807 | 1,346 ->   680 | scan -> seek
    ///   device mix (teams_user_device_usage_log) 365d | 17,383 -> 10,833 | 7,702 -> 7,235 | scan -> seek
    ///   channel stats (teams_channel_stats_log)  28d  | 11,061 ->    699 |   572 ->   104 | scan -> seek
    ///   channel stats (teams_channel_stats_log)  365d | 11,061 ->  9,219 | 1,581 -> 1,262 | scan -> seek
    ///   reactions (teams_user_channel_reactions) 28d  |  1,094 ->     71 |    62 ->    10 | scan -> seek
    ///   reactions (teams_user_channel_reactions) 365d |  1,094 ->    908 |   195 ->   147 | scan -> seek
    ///   membership (team_membership_log)         28d  |    271 ->     35 |    32 ->    20 | scan -> seek
    ///   membership (team_membership_log)         365d |    271 ->    244 |   226 ->   177 | scan -> seek
    /// </code>
    /// <para>
    /// Every index improves BOTH logical reads and elapsed time at BOTH selectivities, and the plan
    /// operator changes from a scan to a seek in every case - which is why it got faster, not just
    /// that it did on the day. The narrow window - the one the UI opens on - improves dramatically
    /// because it becomes a genuine range seek instead of a full scan. The wide window gains less, as
    /// expected: a 365-day range over 400 days of history is most of the table, so there is little
    /// left to skip.
    /// </para>
    ///
    /// <para>
    /// The 365-day reactions figure is worth a note on method. A first pass at four repeats showed it
    /// as a 9% wall-clock REGRESSION (192 ms -&gt; 210 ms) against a 17% drop in reads. Rather than
    /// wave an 18 ms move away, it was re-measured at nine repeats, where it became a 25% improvement
    /// (195 ms -&gt; 147 ms). The first reading was run-to-run variance on a loaded machine. Logical
    /// reads - which do not move with machine load - said the same thing in both passes, which is
    /// exactly why they are the primary signal.
    /// </para>
    ///
    /// <para>
    /// <b>Two candidates were measured and REJECTED.</b> Recording them matters as much as the ones
    /// that shipped:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b><c>call_sessions (call_record_id) INCLUDE (attendee_user_id, [start], [end])</c> - harmful.</b>
    /// On the call-KPI query it cut reads 8% over a 28-day window but <b>raised them 90% over a
    /// 365-day window</b> (8,164 -&gt; 15,525), and made the 28-day case 26% slower in wall-clock
    /// (756 ms -&gt; 955 ms). Once the narrower index exists the optimiser switches to seeking it,
    /// which is the wrong shape for a join that already reads most of the table. Its 365-day
    /// wall-clock did improve, which is precisely the trap: a near-doubling of logical reads is the
    /// early warning that the gain evaporates under concurrent report load, and reads are the signal
    /// that does not move with machine load. This is the "plausible-looking index that makes the hot
    /// path worse" the repository's benchmarking rule exists to catch, and it would have shipped on
    /// reasoning alone.
    /// </item>
    /// <item>
    /// <b><c>call_session_call_modalities (call_session_id) INCLUDE (call_modality_id)</c> - neutral.</b>
    /// Reads moved -0.2% at 28 days and -8.6% at 365 days, and the plan kept scanning that index
    /// either way - so it never became a new access path. It costs ~35 MB per 2M rows and an offline
    /// build for no measured change in kind, so it is not shipped. (The shape itself was the right
    /// one to try - the extra column is RETURNED, not MATCHED, which is the case where
    /// <c>INCLUDE</c> beats a wider key - but being the right shape is not the same as being worth
    /// building.)
    /// </item>
    /// </list>
    ///
    /// <para>
    /// <b>Build time and storage - measured</b> on the fixture above, so an admin can size the
    /// maintenance window. Scale roughly linearly with row count:
    /// </para>
    /// <code>
    ///   index                                          rows | build | size
    ///   ----------------------------------------------------+-------+--------
    ///   IX_date (teams_user_device_usage_log, widened)  4.0M | 11-15s|  93 MB
    ///   IX_teams_channel_stats_log_date                 800k |  2-3s |  26 MB
    ///   IX_teams_user_channel_reactions_date            267k |  &lt;1s  |   8 MB
    ///   IX_team_membership_log_date                      75k |  &lt;1s  |   2 MB
    /// </code>
    /// <para>
    /// At a 200k-user tenant the device-usage table is the one that decides the window: roughly
    /// 3-4 seconds and 23 MB per million rows.
    /// </para>
    ///
    /// <para>
    /// <b>Why <c>IX_date</c> is widened rather than joined by a second index.</b> The installer's
    /// profiling schema script already creates <c>IX_date</c> on <c>teams_user_device_usage_log</c>.
    /// A second index with the same leading column would leave two overlapping indexes to maintain on
    /// a table that takes a large daily write load from the usage importer. This is the same choice
    /// <see cref="IndexUsageReportSnapshots"/> made for the five per-user activity tables.
    /// </para>
    ///
    /// <para>
    /// <b>Safety.</b> Purely additive - no existing index is narrowed, no column or row is modified,
    /// and no query has to change to benefit. Idempotent and guarded per table (a table or column that
    /// does not exist is skipped, and an index that already has the required shape is left alone),
    /// <c>suppressTransaction: true</c> so each build commits independently and a partial apply
    /// converges on re-run, <c>ONLINE</c> attempted through <c>sp_executesql</c> inside
    /// <c>TRY/CATCH</c> with an offline fallback, and progress emitted with
    /// <c>RAISERROR ... WITH NOWAIT</c>. This migration does NOT change the EF model, so its
    /// <c>.resx</c> snapshot is a verbatim copy of its predecessor's.
    /// </para>
    /// </summary>
    public partial class IndexTeamsExplorerQueries : DbMigration
    {
        /// <summary>
        /// Kept as a <c>public const</c> so the shipped
        /// <c>202609161200001_IndexTeamsExplorerQueries.manual.sql</c> can embed it verbatim for DBAs
        /// who upgrade the database by hand rather than running the installer.
        /// </summary>
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'IndexTeamsExplorerQueries';
DECLARE @start datetime2(3) = SYSUTCDATETIME();
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);

DECLARE @edition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
-- ONLINE index operations exist only on Enterprise (3), Azure SQL DB (5) and Azure SQL MI (8).
-- Express / Standard / LocalDB reject them; the attempt runs through sp_executesql inside TRY/CATCH
-- so that rejection is catchable and we can fall back to an offline build.
DECLARE @canOnline bit = CASE WHEN @edition IN (3, 5, 8) THEN 1 ELSE 0 END;

SET @msg = @migration + N': starting; EngineEdition=' + CAST(@edition AS nvarchar(10))
    + CASE WHEN @canOnline = 1
        THEN N'; online index builds will be attempted.'
        ELSE N'; online index builds are unavailable, so each table is locked during its build. Stop the importer and use a maintenance window.' END;
RAISERROR(@msg, 0, 1) WITH NOWAIT;

DECLARE @targets table
(
    sequence    int NOT NULL PRIMARY KEY,
    table_name  sysname NOT NULL,
    index_name  sysname NOT NULL,
    include_sql nvarchar(1000) NOT NULL
);

INSERT INTO @targets (sequence, table_name, index_name, include_sql) VALUES
    -- Widened, not added: the installer's profiling schema script already creates IX_date here.
    (1, N'teams_user_device_usage_log', N'IX_date',
        N' INCLUDE ([user_id], [used_web], [used_win_phone], [used_linux], [used_chrome_os], [used_ios], [used_android], [used_mac], [used_windows])'),
    (2, N'teams_channel_stats_log', N'IX_teams_channel_stats_log_date',
        N' INCLUDE ([channel_id], [chats_count], [sentiment_score])'),
    (3, N'teams_user_channel_reactions', N'IX_teams_user_channel_reactions_date',
        N' INCLUDE ([channel_id], [user_id], [reaction_id])'),
    (4, N'team_membership_log', N'IX_team_membership_log_date',
        N' INCLUDE ([team_id], [user_id])');

DECLARE @i int = 1;
DECLARE @maxSeq int = (SELECT MAX(sequence) FROM @targets);
DECLARE @table sysname, @index sysname, @includeSql nvarchar(1000);
DECLARE @objectId int, @indexId int, @rowCount bigint, @onlineDone bit, @hasDate bit;

WHILE @i <= @maxSeq
BEGIN
    SELECT @table = table_name, @index = index_name, @includeSql = include_sql
    FROM @targets WHERE sequence = @i;

    SET @objectId = OBJECT_ID(N'dbo.' + @table, N'U');

    IF @objectId IS NULL
    BEGIN
        SET @msg = @migration + N': dbo.' + @table + N' does not exist; skipping.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END
    ELSE
    BEGIN
        SET @hasDate = CASE WHEN EXISTS (
            SELECT 1 FROM sys.columns WHERE object_id = @objectId AND name = N'date')
            THEN 1 ELSE 0 END;

        IF @hasDate = 0
        BEGIN
            SET @msg = @migration + N': dbo.' + @table + N' has no [date] column; skipping.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            SELECT @indexId = index_id FROM sys.indexes
            WHERE object_id = @objectId AND name = @index;

            -- ""Already correct"" means: keyed on [date] and carrying at least as many included
            -- columns as we are about to add. Counting rather than naming each column keeps this
            -- guard short; a partially-built index has fewer, so it is rebuilt.
            IF @indexId IS NOT NULL
               AND EXISTS (
                    SELECT 1 FROM sys.index_columns AS ic
                    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE ic.object_id = @objectId AND ic.index_id = @indexId
                      AND ic.key_ordinal = 1 AND c.name = N'date')
               AND (
                    SELECT COUNT(*) FROM sys.index_columns AS ic
                    WHERE ic.object_id = @objectId AND ic.index_id = @indexId
                      AND ic.is_included_column = 1
                   ) >= (LEN(@includeSql) - LEN(REPLACE(@includeSql, N',', N''))) + 1
            BEGIN
                SET @msg = @migration + N': [' + @index + N'] on dbo.' + @table
                    + N' already has the required shape; skipping.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
            ELSE
            BEGIN
                SELECT @rowCount = ISNULL(SUM(rows), 0) FROM sys.partitions
                WHERE object_id = @objectId AND index_id IN (0, 1);

                SET @msg = @migration
                    + CASE WHEN @indexId IS NULL THEN N': creating [' ELSE N': rebuilding [' END
                    + @index + N'] on dbo.' + @table + N' ('
                    + CAST(@rowCount AS nvarchar(20)) + N' estimated rows).';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;

                SET @onlineDone = 0;

                IF @canOnline = 1
                BEGIN
                    BEGIN TRY
                        SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                            + @table + N'] ([date])' + @includeSql
                            + CASE WHEN @indexId IS NULL
                                THEN N' WITH (ONLINE = ON);'
                                ELSE N' WITH (DROP_EXISTING = ON, ONLINE = ON);' END;
                        EXEC sp_executesql @sql;
                        SET @onlineDone = 1;
                    END TRY
                    BEGIN CATCH
                        SET @msg = @migration + N': online build of [' + @index + N'] failed ('
                            + ERROR_MESSAGE() + N'); retrying offline.';
                        RAISERROR(@msg, 0, 1) WITH NOWAIT;
                    END CATCH
                END

                IF @onlineDone = 0
                BEGIN
                    SET @sql = N'CREATE NONCLUSTERED INDEX [' + @index + N'] ON [dbo].['
                        + @table + N'] ([date])' + @includeSql
                        + CASE WHEN @indexId IS NULL
                            THEN N';'
                            ELSE N' WITH (DROP_EXISTING = ON);' END;
                    EXEC sp_executesql @sql;
                END

                SET @msg = @migration + N': [' + @index + N'] is ready.';
                RAISERROR(@msg, 0, 1) WITH NOWAIT;
            END
        END
    END

    SET @indexId = NULL;
    SET @i += 1;
END

SET @msg = @migration + N': finished in '
    + CAST(DATEDIFF(MILLISECOND, @start, SYSUTCDATETIME()) AS nvarchar(20)) + N'ms.';
RAISERROR(@msg, 0, 1) WITH NOWAIT;
";

        /// <summary>
        /// SQL executed by <see cref="Down"/>. Drops the three new indexes and returns
        /// <c>IX_date</c> on <c>teams_user_device_usage_log</c> to the key-only shape the profiling
        /// schema script creates. Idempotent and guarded.
        /// </summary>
        public const string Down_Sql = @"
SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.teams_user_device_usage_log', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.teams_user_device_usage_log') AND name = N'IX_date')
    CREATE NONCLUSTERED INDEX [IX_date] ON [dbo].[teams_user_device_usage_log] ([date])
    WITH (DROP_EXISTING = ON);

IF OBJECT_ID(N'dbo.teams_channel_stats_log', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.teams_channel_stats_log') AND name = N'IX_teams_channel_stats_log_date')
    DROP INDEX [IX_teams_channel_stats_log_date] ON [dbo].[teams_channel_stats_log];

IF OBJECT_ID(N'dbo.teams_user_channel_reactions', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.teams_user_channel_reactions') AND name = N'IX_teams_user_channel_reactions_date')
    DROP INDEX [IX_teams_user_channel_reactions_date] ON [dbo].[teams_user_channel_reactions];

IF OBJECT_ID(N'dbo.team_membership_log', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.team_membership_log') AND name = N'IX_team_membership_log_date')
    DROP INDEX [IX_team_membership_log_date] ON [dbo].[team_membership_log];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'IndexTeamsExplorerQueries'. Adds date indexes to the Teams channel-statistics, channel-reaction and team-membership tables, and widens IX_date on teams_user_device_usage_log to cover the device-mix aggregate, so the Teams Explorer's tabs seek a date range instead of scanning the table. Purely additive. Where ONLINE index builds are unavailable each build briefly locks its table, so run this with the importer stopped. Check the SQL session for live progress (RAISERROR ... WITH NOWAIT).");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'IndexTeamsExplorerQueries'.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
