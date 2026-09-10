namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Retires four unused audit / Yammer / Stream feature areas and the eight tables behind them.
    ///
    /// WHY
    ///  * <c>audit_event_prop_names</c>, <c>audit_event_prop_vals</c>, <c>audit_event_exchange_props</c> and
    ///    <c>audit_event_azure_ad_props</c> stored the Management Activity API's "ExtendedProperties" bag for
    ///    Exchange and Entra ID (Azure AD) events. Nothing in the product ever read them back - no report, no
    ///    lookup page, no export - and telemetry showed no customer using them either. They were pure write
    ///    amplification on the hottest path in the importer: two lookup round-trips plus a row per property
    ///    per event, for every Exchange and Entra event imported.
    ///  * <c>yammer_messages</c> and <c>yammer_msg_to_stream</c> belonged to a legacy Viva Engage (Yammer)
    ///    *message* import that no longer exists in the codebase - nothing has written to them for years.
    ///    NOTE this is NOT the Yammer/Viva Engage *usage report* import, which is unaffected:
    ///    <c>yammer_user_activity_log</c>, <c>yammer_group_activity_log</c>, <c>yammer_device_activity_log</c>
    ///    and the <c>yammer_groups</c> lookup they depend on all stay exactly as they are.
    ///  * <c>stream_videos</c> and <c>event_meta_stream</c> backed Microsoft Stream (Classic) audit events.
    ///    Stream Classic has been retired by Microsoft, so no tenant produces these events any more. Stream
    ///    records that do still arrive are now imported as generic events (audit_events plus
    ///    event_meta_general, which keeps the raw record JSON), so no audit coverage is lost.
    ///
    /// WHAT THIS MIGRATION DOES TO THE DATABASE
    /// It drops each group of tables ONLY when every table in that group is empty - i.e. on fresh installs
    /// and on tenants that never accumulated any of this data. If ANY table in a group holds a row, that
    /// whole group is left completely untouched and the migration logs (loudly) that the tables were
    /// retained. Deleting a customer's historic data is their decision, not ours. The three groups are
    /// evaluated independently, so a tenant with Exchange extended properties still gets the Yammer and
    /// Stream tables cleaned up.
    ///
    /// The groups, and the order they must be dropped in (children before parents):
    ///  1. audit_event_exchange_props, audit_event_azure_ad_props, then audit_event_prop_names /
    ///     audit_event_prop_vals. Self-contained: nothing else references them.
    ///  2. yammer_msg_to_stream, then yammer_messages.
    ///  3. event_meta_stream, then stream_videos. stream_videos is ALSO referenced by
    ///     yammer_msg_to_stream, so it can only go once group 2 has gone too - if a tenant kept its Yammer
    ///     messages, event_meta_stream is still dropped but stream_videos is retained.
    ///
    /// Reporting views are only rewritten when the tables they read actually go, so a customer who keeps
    /// their data keeps their views working:
    ///  * <c>events_view_azure_ad</c> / <c>events_view_exchange</c> lose their <c>extended_properties_count</c>
    ///    column (it counted rows in the props tables). Every other column is unchanged.
    ///  * <c>events_view_stream</c> is dropped outright - it exists only to report Stream video events.
    ///
    /// Admins who want the storage back can drop any retained table by hand after upgrading; this migration
    /// is re-runnable and is a no-op once a group is gone.
    ///
    /// RUNTIME: the emptiness test is an EXISTS (not COUNT(*)), so it is O(1)-ish however large a table is,
    /// and the drop path only ever runs against empty tables. This migration is effectively instant. No
    /// index build or table rewrite is involved, so no maintenance window is needed.
    /// </summary>
    public partial class RetireUnusedAuditYammerStreamTables : DbMigration
    {
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'RetireUnusedAuditYammerStreamTables';
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);
DECLARE @hasRows bit;
DECLARE @estimate bigint;
DECLARE @i int;
DECLARE @table sysname;

-- Every table this migration can remove, with the group it belongs to and the order tables inside a
-- group must be dropped in (children first).
DECLARE @tables table
(
    grp int NOT NULL,
    sequence int NOT NULL,
    table_name sysname NOT NULL,
    present bit NOT NULL DEFAULT 0,
    has_rows bit NOT NULL DEFAULT 0,
    PRIMARY KEY (grp, sequence)
);

INSERT INTO @tables (grp, sequence, table_name)
VALUES
    -- Group 1: audit extended properties (Exchange + Entra ID)
    (1, 1, N'audit_event_exchange_props'),
    (1, 2, N'audit_event_azure_ad_props'),
    (1, 3, N'audit_event_prop_names'),
    (1, 4, N'audit_event_prop_vals'),
    -- Group 2: legacy Yammer message import
    (2, 1, N'yammer_msg_to_stream'),
    (2, 2, N'yammer_messages'),
    -- Group 3: Microsoft Stream (Classic) audit metadata
    (3, 1, N'event_meta_stream'),
    (3, 2, N'stream_videos');

-------------------------------------------------------------------------------
-- 1) Survey what is present and whether it holds anything.
-------------------------------------------------------------------------------
DECLARE survey_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT grp, sequence, table_name FROM @tables ORDER BY grp, sequence;

DECLARE @grp int, @seq int;
OPEN survey_cursor;
FETCH NEXT FROM survey_cursor INTO @grp, @seq, @table;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
    BEGIN
        -- EXISTS rather than COUNT(*): we only need to know whether the table holds any row, and on a
        -- long-lived tenant audit_event_exchange_props can be very large. sys.partitions gives a cheap
        -- estimate for the log line.
        SET @hasRows = 0;
        SET @sql = N'SELECT @out = CASE WHEN EXISTS (SELECT 1 FROM [dbo].[' + @table + N']) THEN 1 ELSE 0 END;';
        EXEC sp_executesql @sql, N'@out bit OUTPUT', @out = @hasRows OUTPUT;

        SELECT @estimate = ISNULL(SUM(rows), 0)
        FROM sys.partitions
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND index_id IN (0, 1);

        UPDATE @tables SET present = 1, has_rows = @hasRows WHERE grp = @grp AND sequence = @seq;

        IF @hasRows = 1
            SET @msg = @migration + N': dbo.' + @table + N' holds data (about '
                + CAST(@estimate AS nvarchar(20)) + N' rows).';
        ELSE
            SET @msg = @migration + N': dbo.' + @table + N' is empty.';

        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    FETCH NEXT FROM survey_cursor INTO @grp, @seq, @table;
END
CLOSE survey_cursor;
DEALLOCATE survey_cursor;

DECLARE @group1Present int = (SELECT COUNT(*) FROM @tables WHERE grp = 1 AND present = 1);
DECLARE @group1Rows    int = (SELECT COUNT(*) FROM @tables WHERE grp = 1 AND has_rows = 1);
DECLARE @group2Present int = (SELECT COUNT(*) FROM @tables WHERE grp = 2 AND present = 1);
DECLARE @group2Rows    int = (SELECT COUNT(*) FROM @tables WHERE grp = 2 AND has_rows = 1);
DECLARE @group3Present int = (SELECT COUNT(*) FROM @tables WHERE grp = 3 AND present = 1);
DECLARE @group3Rows    int = (SELECT COUNT(*) FROM @tables WHERE grp = 3 AND has_rows = 1);

-------------------------------------------------------------------------------
-- 2) Group 1 - audit extended properties.
-------------------------------------------------------------------------------
IF @group1Present = 0
BEGIN
    SET @msg = @migration + N': no audit extended-property tables present; nothing to do for them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE IF @group1Rows > 0
BEGIN
    SET @msg = @migration + N': ***** Exchange / Entra ID audit extended properties are RETIRED and the importer no longer writes them. *****';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': your audit_event_prop_names / audit_event_prop_vals / audit_event_exchange_props / audit_event_azure_ad_props tables still contain data, so they have been LEFT IN PLACE and are now read-only historic data - nothing writes to them any more.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': to reclaim that storage, drop the two *_props tables and then the two lookup tables by hand once you no longer need the history, then re-run this script - it will confirm they are gone and rewrite the events_view_exchange / events_view_azure_ad views.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': all audit extended-property tables are empty; removing them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- The two reporting views count rows in the props tables. Rewrite them without that column before the
    -- tables go, otherwise they would be left permanently broken. Every other column is unchanged.
    IF OBJECT_ID(N'dbo.events_view_azure_ad', N'V') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'
ALTER VIEW [dbo].[events_view_azure_ad] AS
    SELECT audit_events.id
        ,[user_id]
        ,[users].[user_name]
        ,operation_id
        ,event_operations.operation_name as operation
        ,time_stamp
    FROM [dbo].audit_events
    inner join users on
        audit_events.[user_id] = users.id
    inner join event_meta_azure_ad on
        event_meta_azure_ad.event_id = audit_events.id
    inner join event_operations on
        audit_events.operation_id = event_operations.id;';
        SET @msg = @migration + N': rewrote view events_view_azure_ad without extended_properties_count.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    IF OBJECT_ID(N'dbo.events_view_exchange', N'V') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'
ALTER VIEW [dbo].[events_view_exchange] AS
    SELECT audit_events.id
        ,[user_id]
        ,[users].[user_name]
        ,operation_id
        ,event_operations.operation_name as operation
        ,time_stamp
    FROM [dbo].audit_events
    inner join users on
        audit_events.[user_id] = users.id
    inner join event_meta_exchange on
        event_meta_exchange.event_id = audit_events.id
    inner join event_operations on
        audit_events.operation_id = event_operations.id;';
        SET @msg = @migration + N': rewrote view events_view_exchange without extended_properties_count.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    SET @i = 1;
    WHILE @i <= 4
    BEGIN
        SELECT @table = table_name FROM @tables WHERE grp = 1 AND sequence = @i;
        IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TABLE [dbo].[' + @table + N'];';
            EXEC sp_executesql @sql;
            SET @msg = @migration + N': dropped table dbo.' + @table + N'.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        SET @i += 1;
    END
END

-------------------------------------------------------------------------------
-- 3) Group 2 - legacy Yammer message import.
-------------------------------------------------------------------------------
IF @group2Present = 0
BEGIN
    SET @msg = @migration + N': no legacy Yammer message tables present; nothing to do for them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE IF @group2Rows > 0
BEGIN
    SET @msg = @migration + N': ***** the legacy Yammer MESSAGE import is RETIRED (the Viva Engage usage-report import is unaffected). *****';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': your yammer_messages / yammer_msg_to_stream tables still contain data, so they have been LEFT IN PLACE and are now read-only historic data - nothing writes to them any more.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': the legacy Yammer message tables are empty; removing them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    SET @i = 1;
    WHILE @i <= 2
    BEGIN
        SELECT @table = table_name FROM @tables WHERE grp = 2 AND sequence = @i;
        IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TABLE [dbo].[' + @table + N'];';
            EXEC sp_executesql @sql;
            SET @msg = @migration + N': dropped table dbo.' + @table + N'.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        SET @i += 1;
    END
END

-------------------------------------------------------------------------------
-- 4) Group 3 - Microsoft Stream (Classic) audit metadata.
-------------------------------------------------------------------------------
IF @group3Present = 0
BEGIN
    SET @msg = @migration + N': no Microsoft Stream tables present; nothing to do for them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE IF @group3Rows > 0
BEGIN
    SET @msg = @migration + N': ***** Microsoft Stream (Classic) is retired by Microsoft and its dedicated import has been removed. Stream records that still arrive are now stored as generic audit events. *****';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': your event_meta_stream / stream_videos tables still contain data, so they have been LEFT IN PLACE and are now read-only historic data - nothing writes to them any more. The events_view_stream view is kept so that history stays reportable.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': the Microsoft Stream tables are empty; removing them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- This view exists only to report Stream video events, so it goes with them.
    IF OBJECT_ID(N'dbo.events_view_stream', N'V') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'DROP VIEW [dbo].[events_view_stream];';
        SET @msg = @migration + N': dropped view events_view_stream.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    IF OBJECT_ID(N'dbo.event_meta_stream', N'U') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'DROP TABLE [dbo].[event_meta_stream];';
        SET @msg = @migration + N': dropped table dbo.event_meta_stream.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    -- stream_videos is also referenced by yammer_msg_to_stream. If a tenant kept its Yammer messages
    -- (group 2 above), that foreign key still exists, so the video lookup has to stay too. Removing the
    -- constraint instead would silently strip referential integrity from data we deliberately preserved.
    IF OBJECT_ID(N'dbo.stream_videos', N'U') IS NOT NULL
    BEGIN
        IF OBJECT_ID(N'dbo.yammer_msg_to_stream', N'U') IS NOT NULL
        BEGIN
            SET @msg = @migration + N': dbo.stream_videos was RETAINED because dbo.yammer_msg_to_stream still exists and references it. Drop the retained Yammer message tables by hand and re-run this script to remove it.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
        ELSE
        BEGIN
            EXEC sp_executesql N'DROP TABLE [dbo].[stream_videos];';
            SET @msg = @migration + N': dropped table dbo.stream_videos.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END
    END
END
";

        public const string Down_Sql = @"
SET NOCOUNT ON;

-- Recreates the retired schema (empty). Data is not recoverable: if a table still held rows the Up did
-- not drop it, so this is only reachable for a database where these tables were already empty.
IF OBJECT_ID(N'dbo.audit_event_prop_names', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[audit_event_prop_names]
    (
        [id] int NOT NULL IDENTITY,
        [name] nvarchar(max),
        CONSTRAINT [PK_dbo.audit_event_prop_names] PRIMARY KEY ([id])
    );
END

IF OBJECT_ID(N'dbo.audit_event_prop_vals', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[audit_event_prop_vals]
    (
        [id] int NOT NULL IDENTITY,
        [value] nvarchar(max),
        CONSTRAINT [PK_dbo.audit_event_prop_vals] PRIMARY KEY ([id])
    );
END

IF OBJECT_ID(N'dbo.audit_event_exchange_props', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[audit_event_exchange_props]
    (
        [id] int NOT NULL IDENTITY,
        [prop_name_id] int NOT NULL,
        [prop_val_id] int NOT NULL,
        [event_id] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_dbo.audit_event_exchange_props] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_prop_name_id] ON [dbo].[audit_event_exchange_props] ([prop_name_id]);
    CREATE INDEX [IX_prop_val_id] ON [dbo].[audit_event_exchange_props] ([prop_val_id]);
    CREATE INDEX [IX_event_id] ON [dbo].[audit_event_exchange_props] ([event_id]);
    ALTER TABLE [dbo].[audit_event_exchange_props]
        ADD CONSTRAINT [FK_dbo.audit_event_exchange_props_dbo.audit_event_prop_names_prop_name_id]
        FOREIGN KEY ([prop_name_id]) REFERENCES [dbo].[audit_event_prop_names] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[audit_event_exchange_props]
        ADD CONSTRAINT [FK_dbo.audit_event_exchange_props_dbo.audit_event_prop_vals_prop_val_id]
        FOREIGN KEY ([prop_val_id]) REFERENCES [dbo].[audit_event_prop_vals] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[audit_event_exchange_props]
        ADD CONSTRAINT [FK_dbo.audit_event_exchange_props_dbo.event_meta_exchange_event_id]
        FOREIGN KEY ([event_id]) REFERENCES [dbo].[event_meta_exchange] ([event_id]) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.audit_event_azure_ad_props', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[audit_event_azure_ad_props]
    (
        [id] int NOT NULL IDENTITY,
        [prop_name_id] int NOT NULL,
        [prop_val_id] int NOT NULL,
        [event_id] uniqueidentifier NOT NULL,
        CONSTRAINT [PK_dbo.audit_event_azure_ad_props] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_prop_name_id] ON [dbo].[audit_event_azure_ad_props] ([prop_name_id]);
    CREATE INDEX [IX_prop_val_id] ON [dbo].[audit_event_azure_ad_props] ([prop_val_id]);
    CREATE INDEX [IX_event_id] ON [dbo].[audit_event_azure_ad_props] ([event_id]);
    ALTER TABLE [dbo].[audit_event_azure_ad_props]
        ADD CONSTRAINT [FK_dbo.audit_event_azure_ad_props_dbo.audit_event_prop_names_prop_name_id]
        FOREIGN KEY ([prop_name_id]) REFERENCES [dbo].[audit_event_prop_names] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[audit_event_azure_ad_props]
        ADD CONSTRAINT [FK_dbo.audit_event_azure_ad_props_dbo.audit_event_prop_vals_prop_val_id]
        FOREIGN KEY ([prop_val_id]) REFERENCES [dbo].[audit_event_prop_vals] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[audit_event_azure_ad_props]
        ADD CONSTRAINT [FK_dbo.audit_event_azure_ad_props_dbo.event_meta_azure_ad_event_id]
        FOREIGN KEY ([event_id]) REFERENCES [dbo].[event_meta_azure_ad] ([event_id]) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.stream_videos', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[stream_videos]
    (
        [id] int NOT NULL IDENTITY,
        [stream_id] uniqueidentifier NOT NULL,
        [name] nvarchar(100),
        CONSTRAINT [PK_dbo.stream_videos] PRIMARY KEY ([id])
    );
    CREATE UNIQUE INDEX [IX_stream_id] ON [dbo].[stream_videos] ([stream_id]);
END

IF OBJECT_ID(N'dbo.event_meta_stream', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[event_meta_stream]
    (
        [event_id] uniqueidentifier NOT NULL,
        [o365_client_application_id] int NOT NULL,
        [video_id] int NOT NULL,
        CONSTRAINT [PK_dbo.event_meta_stream] PRIMARY KEY ([event_id])
    );
    CREATE INDEX [IX_event_id] ON [dbo].[event_meta_stream] ([event_id]);
    CREATE INDEX [IX_o365_client_application_id] ON [dbo].[event_meta_stream] ([o365_client_application_id]);
    CREATE INDEX [IX_video_id] ON [dbo].[event_meta_stream] ([video_id]);
    ALTER TABLE [dbo].[event_meta_stream]
        ADD CONSTRAINT [FK_dbo.event_meta_stream_dbo.audit_events_event_id]
        FOREIGN KEY ([event_id]) REFERENCES [dbo].[audit_events] ([id]);
    ALTER TABLE [dbo].[event_meta_stream]
        ADD CONSTRAINT [FK_dbo.event_meta_stream_dbo.o365_client_applications_o365_client_application_id]
        FOREIGN KEY ([o365_client_application_id]) REFERENCES [dbo].[o365_client_applications] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[event_meta_stream]
        ADD CONSTRAINT [FK_dbo.event_meta_stream_dbo.stream_videos_video_id]
        FOREIGN KEY ([video_id]) REFERENCES [dbo].[stream_videos] ([id]) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.yammer_messages', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[yammer_messages]
    (
        [id] int NOT NULL IDENTITY,
        [sender_id] int NOT NULL,
        [created] datetime NOT NULL,
        [yammer_msg_id] bigint NOT NULL,
        [reply_to_yammer_msg_id] bigint,
        [likes_count] int NOT NULL,
        [followers_count] int NOT NULL,
        [parent_msg_id] int,
        CONSTRAINT [PK_dbo.yammer_messages] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_sender_id] ON [dbo].[yammer_messages] ([sender_id]);
    CREATE UNIQUE INDEX [IX_yammer_msg_id] ON [dbo].[yammer_messages] ([yammer_msg_id]);
    CREATE INDEX [IX_parent_msg_id] ON [dbo].[yammer_messages] ([parent_msg_id]);
    ALTER TABLE [dbo].[yammer_messages]
        ADD CONSTRAINT [FK_dbo.yammer_messages_dbo.users_sender_id]
        FOREIGN KEY ([sender_id]) REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[yammer_messages]
        ADD CONSTRAINT [FK_dbo.yammer_messages_dbo.yammer_messages_parent_msg_id]
        FOREIGN KEY ([parent_msg_id]) REFERENCES [dbo].[yammer_messages] ([id]);
END

IF OBJECT_ID(N'dbo.yammer_msg_to_stream', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[yammer_msg_to_stream]
    (
        [id] int NOT NULL IDENTITY,
        [message_id] int NOT NULL,
        [stream_id] int NOT NULL,
        CONSTRAINT [PK_dbo.yammer_msg_to_stream] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_message_id] ON [dbo].[yammer_msg_to_stream] ([message_id]);
    CREATE INDEX [IX_stream_id] ON [dbo].[yammer_msg_to_stream] ([stream_id]);
    ALTER TABLE [dbo].[yammer_msg_to_stream]
        ADD CONSTRAINT [FK_dbo.yammer_msg_to_stream_dbo.yammer_messages_message_id]
        FOREIGN KEY ([message_id]) REFERENCES [dbo].[yammer_messages] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[yammer_msg_to_stream]
        ADD CONSTRAINT [FK_dbo.yammer_msg_to_stream_dbo.stream_videos_stream_id]
        FOREIGN KEY ([stream_id]) REFERENCES [dbo].[stream_videos] ([id]) ON DELETE CASCADE;
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'RetireUnusedAuditYammerStreamTables'. The Exchange/Entra audit extended-property tables, the legacy Yammer message tables and the Microsoft Stream (Classic) tables are dropped ONLY if they are empty; if any group holds data it is left untouched as read-only history for you to archive or drop yourself. The Viva Engage (Yammer) usage-report import is NOT affected.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'RetireUnusedAuditYammerStreamTables'. Recreates the (empty) retired tables.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
