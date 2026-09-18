/*
Teams Explorer index benchmark harness.

Purpose
  Measure, before and after each candidate index, the queries the Teams Explorer actually runs, so
  that only indexes with a PROVEN positive impact are shipped. The repository rule is explicit: no
  performance-motivated schema change goes to a stable release without before/after logical reads
  AND elapsed time, the plan operator either side, and more than one selectivity.

Scope
  Deliberately limited to the query shapes whose supporting index is MISSING today:

    1. call_sessions        - only FK indexes exist, so a per-call attendee lookup pays a key lookup
                              for start/end/attendee_user_id on every row.
    2. call_session_call_modalities
                            - its only index is UNIQUE (call_modality_id, call_session_id), which
                              leads on the wrong column for a per-session lookup. This is the
                              textbook "the extra column is RETURNED, not MATCHED" case, so the
                              candidate is a key on call_session_id with an INCLUDE - the opposite
                              shape to the composite key that the Copilot de-dup needed.
    3. teams_user_device_usage_log
                            - IX_date is key-only, so the device-mix aggregate does a lookup per row
                              to read eight bit columns.
    4. teams_channel_stats_log, teams_user_channel_reactions, team_membership_log
                            - no date index at all.

  teams_user_activity_log is deliberately NOT covered here. Its candidate change is a DROP_EXISTING
  rebuild of the existing nonclustered COLUMNSTORE index, which must be measured against the Copilot
  Adoption licence-opportunity query as well as against this page, at a table size (13M+ rows) that
  belongs in its own harness rather than bolted onto this one.

Safety
  Run ONLY against an empty throwaway database. Every table here is a synthetic replica: it carries
  the production column types and the production index shapes that exist today, and nothing else.
  Never point this at a customer database, and never paste real names, row counts or plans into this
  file, a PR or a release note.

SQLCMD variables
  SyntheticUsers   - distinct synthetic users (for example 5000 or 200000)
  SyntheticCalls   - call records to seed across the period (for example 200000)
  SyntheticDays    - days of history to spread the data across (for example 400)
*/
SET NOCOUNT ON;

IF CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) <> N'Latin1_General_CI_AS'
BEGIN
    RAISERROR('Create the throwaway benchmark database with Latin1_General_CI_AS to match the production EF database collation.', 16, 1);
    RETURN;
END

DECLARE @SyntheticUsers int = $(SyntheticUsers);
DECLARE @SyntheticCalls int = $(SyntheticCalls);
DECLARE @SyntheticDays int = $(SyntheticDays);

PRINT CONCAT('Teams Explorer index benchmark fixture: users=', @SyntheticUsers,
             ', calls=', @SyntheticCalls,
             ', days=', @SyntheticDays,
             ', database=', DB_NAME(),
             ', source=synthetic LocalDB/SQL Server fixture');

-- ---------------------------------------------------------------------------------------------
-- Schema. Types and existing indexes mirror the migrated production schema.
-- ---------------------------------------------------------------------------------------------

IF OBJECT_ID(N'dbo.call_session_call_modalities', N'U') IS NOT NULL DROP TABLE dbo.call_session_call_modalities;
IF OBJECT_ID(N'dbo.call_sessions', N'U') IS NOT NULL DROP TABLE dbo.call_sessions;
IF OBJECT_ID(N'dbo.call_records', N'U') IS NOT NULL DROP TABLE dbo.call_records;
IF OBJECT_ID(N'dbo.call_modalities', N'U') IS NOT NULL DROP TABLE dbo.call_modalities;
IF OBJECT_ID(N'dbo.call_types', N'U') IS NOT NULL DROP TABLE dbo.call_types;
IF OBJECT_ID(N'dbo.teams_user_device_usage_log', N'U') IS NOT NULL DROP TABLE dbo.teams_user_device_usage_log;
IF OBJECT_ID(N'dbo.teams_user_channel_reactions', N'U') IS NOT NULL DROP TABLE dbo.teams_user_channel_reactions;
IF OBJECT_ID(N'dbo.teams_channel_stats_log', N'U') IS NOT NULL DROP TABLE dbo.teams_channel_stats_log;
IF OBJECT_ID(N'dbo.team_membership_log', N'U') IS NOT NULL DROP TABLE dbo.team_membership_log;
IF OBJECT_ID(N'dbo.teams_channels', N'U') IS NOT NULL DROP TABLE dbo.teams_channels;
IF OBJECT_ID(N'dbo.teams', N'U') IS NOT NULL DROP TABLE dbo.teams;
IF OBJECT_ID(N'dbo.users', N'U') IS NOT NULL DROP TABLE dbo.users;

CREATE TABLE dbo.users
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    -- varchar, not nvarchar: Entra restricts a UPN to ASCII, and production matches that.
    user_name varchar(250) NOT NULL,
    account_enabled bit NULL,
    department_id int NULL
);

CREATE TABLE dbo.call_types
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_call_types PRIMARY KEY CLUSTERED,
    name nvarchar(100) NOT NULL
);

CREATE TABLE dbo.call_modalities
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_call_modalities PRIMARY KEY CLUSTERED,
    name nvarchar(100) NULL
);
CREATE UNIQUE NONCLUSTERED INDEX IX_name ON dbo.call_modalities(name);

CREATE TABLE dbo.call_records
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_call_records PRIMARY KEY CLUSTERED,
    organizer_id int NOT NULL,
    call_type_id int NOT NULL,
    graph_id nvarchar(100) NULL,
    [start] datetime NOT NULL,
    [end] datetime NOT NULL
);
-- The shape IndexReportDateQueries ships today.
CREATE NONCLUSTERED INDEX IX_call_records_start ON dbo.call_records([start]) INCLUDE([end]);

CREATE TABLE dbo.call_sessions
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_call_sessions PRIMARY KEY CLUSTERED,
    attendee_user_id int NOT NULL,
    [start] datetime NOT NULL,
    [end] datetime NOT NULL,
    call_record_id int NOT NULL
);
-- Exactly what the v1 migration creates: two bare FK indexes, neither covering.
CREATE NONCLUSTERED INDEX IX_attendee_user_id ON dbo.call_sessions(attendee_user_id);
CREATE NONCLUSTERED INDEX IX_call_record_id ON dbo.call_sessions(call_record_id);

CREATE TABLE dbo.call_session_call_modalities
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_call_session_call_modalities PRIMARY KEY CLUSTERED,
    call_modality_id int NOT NULL,
    call_session_id int NOT NULL
);
-- Leads on call_modality_id, which is why a per-session lookup cannot seek it.
CREATE UNIQUE NONCLUSTERED INDEX IX_call_modality_id_call_session_id
    ON dbo.call_session_call_modalities(call_modality_id, call_session_id);

CREATE TABLE dbo.teams_user_device_usage_log
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_teams_user_device_usage_log PRIMARY KEY CLUSTERED,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL,
    user_id int NOT NULL,
    used_web bit NULL,
    used_win_phone bit NULL,
    used_linux bit NULL,
    used_chrome_os bit NULL,
    used_ios bit NULL,
    used_android bit NULL,
    used_mac bit NULL,
    used_windows bit NULL
);
-- Key-only, exactly as the installer's profiling schema script creates it.
CREATE NONCLUSTERED INDEX IX_date ON dbo.teams_user_device_usage_log([date]);
CREATE NONCLUSTERED INDEX IX_user_id ON dbo.teams_user_device_usage_log(user_id);

CREATE TABLE dbo.teams
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_teams PRIMARY KEY CLUSTERED,
    name nvarchar(100) NOT NULL,
    graph_id nvarchar(100) NOT NULL,
    discovered datetime NOT NULL,
    has_refresh_token bit NOT NULL,
    last_refresh datetime NULL
);

CREATE TABLE dbo.teams_channels
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_teams_channels PRIMARY KEY CLUSTERED,
    graph_id nvarchar(100) NOT NULL,
    name nvarchar(100) NULL,
    team_id int NOT NULL
);
CREATE NONCLUSTERED INDEX IX_team_id ON dbo.teams_channels(team_id);

CREATE TABLE dbo.teams_channel_stats_log
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_teams_channel_stats_log PRIMARY KEY CLUSTERED,
    chats_count int NULL,
    sentiment_score float NULL,
    [date] datetime NOT NULL,
    channel_id int NOT NULL
);
-- The only index the migration creates: channel_id. No date index at all.
CREATE NONCLUSTERED INDEX IX_channel_id ON dbo.teams_channel_stats_log(channel_id);

CREATE TABLE dbo.teams_user_channel_reactions
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_teams_user_channel_reactions PRIMARY KEY CLUSTERED,
    reaction_id int NULL,
    user_id int NOT NULL,
    channel_id int NOT NULL,
    [date] datetime NOT NULL
);
CREATE NONCLUSTERED INDEX IX_reaction_id ON dbo.teams_user_channel_reactions(reaction_id);
CREATE NONCLUSTERED INDEX IX_user_id_reactions ON dbo.teams_user_channel_reactions(user_id);
CREATE NONCLUSTERED INDEX IX_channel_id_reactions ON dbo.teams_user_channel_reactions(channel_id);

CREATE TABLE dbo.team_membership_log
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_team_membership_log PRIMARY KEY CLUSTERED,
    [date] datetime NOT NULL,
    team_id int NOT NULL,
    user_id int NOT NULL
);
CREATE NONCLUSTERED INDEX IX_team_id_membership ON dbo.team_membership_log(team_id);
CREATE NONCLUSTERED INDEX IX_user_id_membership ON dbo.team_membership_log(user_id);

-- ---------------------------------------------------------------------------------------------
-- Seed
-- ---------------------------------------------------------------------------------------------

DECLARE @Anchor datetime = DATEADD(DAY, -1, CONVERT(date, SYSUTCDATETIME()));

;WITH n AS
(
    SELECT TOP (@SyntheticUsers) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.users (user_name, account_enabled, department_id)
SELECT CONCAT('synthetic', i, '@contoso.example'), 1, 1 + i % 25
FROM n;

INSERT dbo.call_types (name) VALUES (N'peerToPeer'), (N'groupCall');
INSERT dbo.call_modalities (name) VALUES (N'audio'), (N'video'), (N'videoBasedScreenSharing'), (N'screenSharing');

PRINT CONCAT('Seeding ', @SyntheticCalls, ' call records...');

;WITH n AS
(
    SELECT TOP (@SyntheticCalls) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c
)
INSERT dbo.call_records (organizer_id, call_type_id, graph_id, [start], [end])
SELECT
    1 + i % @SyntheticUsers,
    1 + i % 2,
    CONVERT(nvarchar(100), NEWID()),
    DATEADD(MINUTE, (i * 7) % 1440, DATEADD(DAY, -(i % @SyntheticDays), @Anchor)),
    DATEADD(MINUTE, ((i * 7) % 1440) + 10 + i % 50, DATEADD(DAY, -(i % @SyntheticDays), @Anchor))
FROM n;

PRINT 'Seeding call sessions (1-4 attendees per call)...';

INSERT dbo.call_sessions (attendee_user_id, [start], [end], call_record_id)
SELECT
    1 + (c.id * s.seat) % @SyntheticUsers,
    DATEADD(MINUTE, s.seat, c.[start]),
    c.[end],
    c.id
FROM dbo.call_records AS c
CROSS APPLY (SELECT 1 AS seat UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4) AS s
WHERE s.seat <= 1 + c.id % 4;

PRINT 'Seeding call session modalities...';

INSERT dbo.call_session_call_modalities (call_modality_id, call_session_id)
SELECT DISTINCT 1 + (s.id + m.n) % 4, s.id
FROM dbo.call_sessions AS s
CROSS APPLY (SELECT 0 AS n UNION ALL SELECT 1) AS m;

PRINT 'Seeding device usage (one row per user per day, as Graph reports it)...';

;WITH d AS
(
    SELECT TOP (@SyntheticDays) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS offset
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.teams_user_device_usage_log
    ([date], last_activity_date, user_id, used_web, used_win_phone, used_linux,
     used_chrome_os, used_ios, used_android, used_mac, used_windows)
SELECT
    DATEADD(DAY, -d.offset, @Anchor),
    DATEADD(DAY, -d.offset, @Anchor),
    u.id,
    CASE WHEN (u.id + d.offset) % 5 = 0 THEN 1 ELSE 0 END,
    0, 0, 0,
    CASE WHEN (u.id + d.offset) % 4 = 0 THEN 1 ELSE 0 END,
    CASE WHEN (u.id + d.offset) % 7 = 0 THEN 1 ELSE 0 END,
    CASE WHEN (u.id + d.offset) % 9 = 0 THEN 1 ELSE 0 END,
    CASE WHEN (u.id + d.offset) % 3 <> 0 THEN 1 ELSE 0 END
FROM dbo.users AS u CROSS JOIN d;

PRINT 'Seeding teams, channels and their daily statistics...';

;WITH t AS
(
    SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
    FROM sys.all_objects a
)
INSERT dbo.teams (name, graph_id, discovered, has_refresh_token, last_refresh)
SELECT CONCAT(N'Synthetic team ', i), CONVERT(nvarchar(100), NEWID()),
       DATEADD(DAY, -@SyntheticDays, @Anchor), 1, @Anchor
FROM t;

INSERT dbo.teams_channels (graph_id, name, team_id)
SELECT CONVERT(nvarchar(100), NEWID()), CONCAT(N'Channel ', c.n), t.id
FROM dbo.teams AS t
CROSS APPLY (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4) AS c;

;WITH d AS
(
    SELECT TOP (@SyntheticDays) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS offset
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.teams_channel_stats_log (chats_count, sentiment_score, [date], channel_id)
SELECT
    (ch.id + d.offset) % 40,
    CASE WHEN (ch.id + d.offset) % 5 = 0 THEN NULL ELSE 0.15 + ((ch.id + d.offset) % 8) * 0.1 END,
    DATEADD(DAY, -d.offset, @Anchor),
    ch.id
FROM dbo.teams_channels AS ch CROSS JOIN d;

PRINT 'Seeding reactions and membership history...';

;WITH d AS
(
    SELECT TOP (@SyntheticDays) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS offset
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.teams_user_channel_reactions (reaction_id, user_id, channel_id, [date])
SELECT 1 + (ch.id + d.offset) % 4, 1 + (ch.id * (d.offset + 1)) % @SyntheticUsers, ch.id,
       DATEADD(DAY, -d.offset, @Anchor)
FROM dbo.teams_channels AS ch CROSS JOIN d
WHERE (ch.id + d.offset) % 3 = 0;

;WITH d AS
(
    SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS offset
    FROM sys.all_objects a
)
INSERT dbo.team_membership_log ([date], team_id, user_id)
SELECT DATEADD(DAY, -(d.offset * 7), @Anchor), t.id, 1 + (t.id * (d.offset + 1) + m.n) % @SyntheticUsers
FROM dbo.teams AS t
CROSS JOIN d
CROSS APPLY (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5) AS m;

UPDATE STATISTICS dbo.call_records WITH FULLSCAN;
UPDATE STATISTICS dbo.call_sessions WITH FULLSCAN;
UPDATE STATISTICS dbo.call_session_call_modalities WITH FULLSCAN;
UPDATE STATISTICS dbo.teams_user_device_usage_log WITH FULLSCAN;
UPDATE STATISTICS dbo.teams_channel_stats_log WITH FULLSCAN;
UPDATE STATISTICS dbo.teams_user_channel_reactions WITH FULLSCAN;
UPDATE STATISTICS dbo.team_membership_log WITH FULLSCAN;

SELECT
    (SELECT COUNT_BIG(*) FROM dbo.users)                          AS users,
    (SELECT COUNT_BIG(*) FROM dbo.call_records)                   AS call_records,
    (SELECT COUNT_BIG(*) FROM dbo.call_sessions)                  AS call_sessions,
    (SELECT COUNT_BIG(*) FROM dbo.call_session_call_modalities)   AS call_session_modalities,
    (SELECT COUNT_BIG(*) FROM dbo.teams_user_device_usage_log)    AS device_usage,
    (SELECT COUNT_BIG(*) FROM dbo.teams_channel_stats_log)        AS channel_stats,
    (SELECT COUNT_BIG(*) FROM dbo.teams_user_channel_reactions)   AS reactions,
    (SELECT COUNT_BIG(*) FROM dbo.team_membership_log)            AS membership;

PRINT 'Fixture ready. Run Invoke-TeamsExplorerIndexBenchmark.ps1 to measure before/after.';
