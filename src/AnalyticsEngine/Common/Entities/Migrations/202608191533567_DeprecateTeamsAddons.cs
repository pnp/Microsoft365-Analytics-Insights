namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Deprecates Teams add-on / app-install tracking (issue #278).
    ///
    /// The per-user app-install log (<c>teams_addons_user_installed_log</c>) was written one row per
    /// user, per installed app, per metadata cycle. At this solution's 200k-user performance baseline
    /// with ~20 apps per user that is ~4M rows/day - over a billion rows a year - for a dataset that
    /// barely changes day to day and that fed a single "Teams app installs" lookup category. Across our
    /// reporting deployments it became the largest table in the product by both row count and storage,
    /// larger than <c>audit_events</c>, which backs most of the actual reporting. The import (and the
    /// per-user Graph call it required) has therefore been removed entirely, along with the EF entities
    /// <c>TeamAddOnDefinition</c> / <c>TeamAddOnLog</c> / <c>UserAppsLog</c>.
    ///
    /// WHAT THIS MIGRATION DOES TO THE DATABASE
    /// It drops <c>teams_addons</c>, <c>teams_addons_log</c> and <c>teams_addons_user_installed_log</c>
    /// ONLY when all three are empty - i.e. on fresh installs, and on tenants that never enabled the
    /// import. If ANY of them contains a row, all three are left completely untouched and the migration
    /// logs (loudly) that the tables were retained. Deleting a customer's historic fact table is their
    /// decision, not ours, and an unbounded DELETE/DROP of a multi-hundred-GB table inside an upgrade
    /// window is exactly the kind of thing that turns a 5 minute upgrade into an outage.
    ///
    /// Because the tables may survive, the migration is deliberately conservative elsewhere too:
    ///  * <c>teams_tabs.teams_addon_id</c> is NOT dropped. It is nullable and simply no longer mapped by
    ///    EF, so leaving it avoids rewriting a table we are keeping. Only its foreign key to
    ///    <c>teams_addons</c> is dropped, and only when <c>teams_addons</c> itself is being dropped.
    ///  * The reporting views that read the add-on tables (<c>vwTeamsAddOns_Log</c>, and the AddOns CTE
    ///    inside <c>vwTeamsStats</c>) are only rewritten when the tables actually go, so a customer who
    ///    keeps their data keeps their views working. <c>vwTeamsStats</c> retains its
    ///    "Add-Ons - Active Only" column (hard-coded 0) so downstream reports selecting that column do
    ///    not break.
    ///
    /// Admins who want the storage back can simply drop the tables by hand after upgrading - this
    /// migration is re-runnable and is a no-op once they are gone.
    ///
    /// RUNTIME: the emptiness test is an EXISTS (not COUNT(*)), so it is O(1)-ish even on a billion-row
    /// table, and the drop path only ever runs against empty tables. Either way this migration is
    /// effectively instant. No online/offline index build is involved, so no maintenance window is needed.
    /// </summary>
    public partial class DeprecateTeamsAddons : DbMigration
    {
        public const string Up_Sql = @"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'DeprecateTeamsAddons';
DECLARE @msg nvarchar(2000);
DECLARE @sql nvarchar(max);
DECLARE @hasRows bit;
DECLARE @anyRows bit = 0;
DECLARE @present int = 0;
DECLARE @estimate bigint;

DECLARE @defs sysname = N'teams_addons';
DECLARE @teamLog sysname = N'teams_addons_log';
DECLARE @userLog sysname = N'teams_addons_user_installed_log';

DECLARE @tables table (sequence int NOT NULL PRIMARY KEY, table_name sysname NOT NULL);
INSERT INTO @tables (sequence, table_name)
VALUES (1, N'teams_addons_user_installed_log'), (2, N'teams_addons_log'), (3, N'teams_addons');

DECLARE @i int = 1;
DECLARE @table sysname;

WHILE @i <= 3
BEGIN
    SELECT @table = table_name FROM @tables WHERE sequence = @i;

    IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
    BEGIN
        SET @present += 1;

        -- EXISTS rather than COUNT(*): teams_addons_user_installed_log can hold a billion rows and we
        -- only need to know whether it holds any. sys.partitions gives a cheap estimate for the log line.
        SET @hasRows = 0;
        SET @sql = N'SELECT @out = CASE WHEN EXISTS (SELECT 1 FROM [dbo].[' + @table + N']) THEN 1 ELSE 0 END;';
        EXEC sp_executesql @sql, N'@out bit OUTPUT', @out = @hasRows OUTPUT;

        SELECT @estimate = ISNULL(SUM(rows), 0)
        FROM sys.partitions
        WHERE object_id = OBJECT_ID(N'dbo.' + @table) AND index_id IN (0, 1);

        IF @hasRows = 1
        BEGIN
            SET @anyRows = 1;
            SET @msg = @migration + N': dbo.' + @table + N' holds data (about '
                + CAST(@estimate AS nvarchar(20)) + N' rows).';
        END
        ELSE
            SET @msg = @migration + N': dbo.' + @table + N' is empty.';

        RAISERROR(@msg, 0, 1) WITH NOWAIT;
    END

    SET @i += 1;
END

IF @present = 0
BEGIN
    SET @msg = @migration + N': no Teams add-on tables present; nothing to do.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE IF @anyRows = 1
BEGIN
    SET @msg = @migration + N': ***** Teams add-on tracking is DEPRECATED and the import has been removed. *****';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': your teams_addons / teams_addons_log / teams_addons_user_installed_log tables still contain data, so they have been LEFT IN PLACE and are now read-only historic data - nothing writes to them any more.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    SET @msg = @migration + N': teams_addons_user_installed_log is typically the largest table in the database. To reclaim that storage, drop the tables by hand once you no longer need the history (drop the teams_tabs foreign key first), then re-run this migration script - it will confirm they are gone.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    SET @msg = @migration + N': all Teams add-on tables are empty; removing them.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;

    -- 1) Reporting views that read the add-on tables. vwTeamsAddOns_Log exists only to report add-ons,
    --    so it goes. vwTeamsStats keeps its shape (including the Add-Ons column, now a constant 0) so
    --    any saved report or query selecting those columns keeps working.
    IF OBJECT_ID(N'dbo.vwTeamsAddOns_Log', N'V') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'DROP VIEW [dbo].[vwTeamsAddOns_Log];';
        RAISERROR('DeprecateTeamsAddons: dropped view vwTeamsAddOns_Log.', 0, 1) WITH NOWAIT;
    END

    IF OBJECT_ID(N'dbo.vwTeamsStats', N'V') IS NOT NULL
    BEGIN
        EXEC sp_executesql N'
ALTER VIEW [dbo].[vwTeamsStats] AS
    WITH
    Members AS (
        SELECT teams.name, COUNT(DISTINCT team_membership_log.user_id) AS [Member Count]
        FROM teams
        LEFT JOIN team_membership_log
            ON teams.id = team_membership_log.team_id
        GROUP BY teams.id, teams.name
    ),
    Channels AS (
        SELECT teams.name, COUNT(teams_channels.name) AS [Channel Count]
        FROM teams
        INNER JOIN teams_channels
            ON teams.id = teams_channels.team_id
        GROUP BY teams.name
    ),
    Tabs AS (
        SELECT teams.name, COUNT(DISTINCT teams_tabs.name) AS [Active Tab Count]
        FROM teams
        INNER JOIN teams_channels
            ON teams.id = teams_channels.team_id
        INNER JOIN teams_channel_tabs_log
            ON teams_channels.id = teams_channel_tabs_log.channel_id
        INNER JOIN teams_tabs
            ON teams_channel_tabs_log.tab_id = teams_tabs.id
        GROUP BY teams.name
    )
    SELECT Members.name AS [Team],
        COALESCE(Members.[Member Count], 0) AS [Members],
        COALESCE(Channels.[Channel Count], 0) AS [Channels],
        CAST(0 AS int) AS [Add-Ons - Active Only],
        COALESCE(Tabs.[Active Tab Count], 0) AS [Tabs - Active Only]
    FROM Members
    LEFT JOIN Channels
        ON Members.name = Channels.name
    LEFT JOIN Tabs
        ON Members.name = Tabs.name;';
        RAISERROR('DeprecateTeamsAddons: rewrote view vwTeamsStats without the add-on counts.', 0, 1) WITH NOWAIT;
    END

    -- 2) Foreign keys pointing AT the add-on tables from tables we are keeping (in practice just
    --    teams_tabs.teams_addon_id). Keys defined ON the dropped tables go with DROP TABLE.
    DECLARE @fkName sysname, @fkTable nvarchar(300);
    DECLARE fk_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT fk.name, QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.tables AS t ON t.object_id = fk.parent_object_id
        WHERE fk.referenced_object_id IN (
                OBJECT_ID(N'dbo.teams_addons'),
                OBJECT_ID(N'dbo.teams_addons_log'),
                OBJECT_ID(N'dbo.teams_addons_user_installed_log'))
          AND fk.parent_object_id NOT IN (
                OBJECT_ID(N'dbo.teams_addons'),
                OBJECT_ID(N'dbo.teams_addons_log'),
                OBJECT_ID(N'dbo.teams_addons_user_installed_log'));

    OPEN fk_cursor;
    FETCH NEXT FROM fk_cursor INTO @fkName, @fkTable;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @sql = N'ALTER TABLE ' + @fkTable + N' DROP CONSTRAINT ' + QUOTENAME(@fkName) + N';';
        EXEC sp_executesql @sql;
        SET @msg = @migration + N': dropped foreign key ' + @fkName + N' on ' + @fkTable + N'.';
        RAISERROR(@msg, 0, 1) WITH NOWAIT;

        FETCH NEXT FROM fk_cursor INTO @fkName, @fkTable;
    END
    CLOSE fk_cursor;
    DEALLOCATE fk_cursor;

    -- 3) The tables themselves, children first.
    SET @i = 1;
    WHILE @i <= 3
    BEGIN
        SELECT @table = table_name FROM @tables WHERE sequence = @i;

        IF OBJECT_ID(N'dbo.' + @table, N'U') IS NOT NULL
        BEGIN
            SET @sql = N'DROP TABLE [dbo].[' + @table + N'];';
            EXEC sp_executesql @sql;
            SET @msg = @migration + N': dropped table dbo.' + @table + N'.';
            RAISERROR(@msg, 0, 1) WITH NOWAIT;
        END

        SET @i += 1;
    END

    SET @msg = @migration + N': Teams add-on tracking removed. Note dbo.teams_tabs.teams_addon_id is left in place (nullable, unused) so the teams_tabs table does not need rewriting.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
END
";

        public const string Down_Sql = @"
SET NOCOUNT ON;

-- Recreates the deprecated add-on schema (empty). Data is not recoverable: if the tables still held
-- rows the Up did not drop them, so this is only reachable for a database where they were empty.
IF OBJECT_ID(N'dbo.teams_addons', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[teams_addons]
    (
        [id] int NOT NULL IDENTITY,
        [addon_type] int NOT NULL,
        [published_state] nvarchar(50),
        [graph_id] nvarchar(100) NOT NULL,
        [name] nvarchar(100),
        CONSTRAINT [PK_dbo.teams_addons] PRIMARY KEY ([id])
    );
    CREATE UNIQUE INDEX [IX_graph_id] ON [dbo].[teams_addons] ([graph_id]);
END

IF OBJECT_ID(N'dbo.teams_addons_log', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[teams_addons_log]
    (
        [id] int NOT NULL IDENTITY,
        [addon_id] int NOT NULL,
        [date] datetime NOT NULL,
        [team_id] int NOT NULL,
        CONSTRAINT [PK_dbo.teams_addons_log] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_addon_id] ON [dbo].[teams_addons_log] ([addon_id]);
    CREATE INDEX [IX_team_id] ON [dbo].[teams_addons_log] ([team_id]);
    ALTER TABLE [dbo].[teams_addons_log]
        ADD CONSTRAINT [FK_dbo.teams_addons_log_dbo.teams_addons_addon_id]
        FOREIGN KEY ([addon_id]) REFERENCES [dbo].[teams_addons] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[teams_addons_log]
        ADD CONSTRAINT [FK_dbo.teams_addons_log_dbo.teams_team_id]
        FOREIGN KEY ([team_id]) REFERENCES [dbo].[teams] ([id]) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.teams_addons_user_installed_log', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[teams_addons_user_installed_log]
    (
        [id] int NOT NULL IDENTITY,
        [date] datetime NOT NULL,
        [addon_id] int NOT NULL,
        [user_id] int NOT NULL,
        CONSTRAINT [PK_dbo.teams_addons_user_installed_log] PRIMARY KEY ([id])
    );
    CREATE INDEX [IX_addon_id] ON [dbo].[teams_addons_user_installed_log] ([addon_id]);
    CREATE INDEX [IX_user_id] ON [dbo].[teams_addons_user_installed_log] ([user_id]);
    ALTER TABLE [dbo].[teams_addons_user_installed_log]
        ADD CONSTRAINT [FK_dbo.teams_addons_user_installed_log_dbo.teams_addons_addon_id]
        FOREIGN KEY ([addon_id]) REFERENCES [dbo].[teams_addons] ([id]) ON DELETE CASCADE;
    ALTER TABLE [dbo].[teams_addons_user_installed_log]
        ADD CONSTRAINT [FK_dbo.teams_addons_user_installed_log_dbo.users_user_id]
        FOREIGN KEY ([user_id]) REFERENCES [dbo].[users] ([id]) ON DELETE CASCADE;
END

IF OBJECT_ID(N'dbo.teams_tabs', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.teams_tabs') AND name = N'teams_addon_id')
BEGIN
    ALTER TABLE [dbo].[teams_tabs] ADD [teams_addon_id] int NULL;
END

IF OBJECT_ID(N'dbo.teams_tabs', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.teams_tabs') AND name = N'teams_addon_id')
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'dbo.teams_tabs') AND name = N'FK_dbo.teams_tabs_dbo.teams_addons_teams_addon_id')
BEGIN
    ALTER TABLE [dbo].[teams_tabs]
        ADD CONSTRAINT [FK_dbo.teams_tabs_dbo.teams_addons_teams_addon_id]
        FOREIGN KEY ([teams_addon_id]) REFERENCES [dbo].[teams_addons] ([id]);
END
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'DeprecateTeamsAddons'. Teams add-on / app-install tracking is deprecated and its import has been removed. The teams_addons, teams_addons_log and teams_addons_user_installed_log tables are dropped ONLY if they are empty; if they hold data they are left untouched as read-only history for you to archive or drop yourself.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'DeprecateTeamsAddons'. Recreates the (empty) Teams add-on tables.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
