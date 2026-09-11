/*
Synthetic daily usage-report save benchmark harness for issue #495.

Run only against an empty throwaway SQL Server database. This script creates synthetic tables with the
same relevant production column types and current query shapes used by AbstractDailyActivityLoader:
  - dbo.users.user_name is varchar(250), matching production Entra UPN storage.
  - dbo.outlook_user_activity_log uses int identity id, datetime date/last_activity_date, int user_id,
    bigint report counters, FK to users and the current IX_date shape from IndexUsageReportSnapshots.
  - lookup path: users WHERE user_name = @Upn ORDER BY id.
  - existing-row load path: outlook_user_activity_log WHERE [date] = @ReportDate, then client-side key by user_id.

Do not paste production names, row counts, plans or data into this file or into PR/release text. Vary the
parameters below for small/intermediate/200k-user synthetic runs, narrow/wide reporting windows and cold/warm
cache runs, then record medians externally with SET STATISTICS IO/TIME and actual execution plans enabled.
*/
SET NOCOUNT ON;

IF CONVERT(sysname, DATABASEPROPERTYEX(DB_NAME(), 'Collation')) <> N'Latin1_General_CI_AS'
BEGIN
    RAISERROR('Create the throwaway benchmark database with Latin1_General_CI_AS to match the production EF database collation.', 16, 1);
    RETURN;
END

DECLARE @SyntheticUsers int = 200000;
DECLARE @SyntheticDays int = 28;
DECLARE @ReportDate date = DATEADD(day, -1, CONVERT(date, SYSUTCDATETIME()));

IF OBJECT_ID(N'dbo.outlook_user_activity_log', N'U') IS NOT NULL DROP TABLE dbo.outlook_user_activity_log;
IF OBJECT_ID(N'dbo.users', N'U') IS NOT NULL DROP TABLE dbo.users;

CREATE TABLE dbo.users
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_users PRIMARY KEY CLUSTERED,
    user_name varchar(250) NOT NULL,
    mail varchar(max) NULL,
    last_updated datetime NULL,
    azure_ad_id varchar(max) NULL,
    account_enabled bit NULL,
    postalcode nvarchar(50) NULL,
    org_id int NULL,
    company_name_id int NULL,
    state_or_province_id int NULL,
    manager_id int NULL,
    country_or_region_id int NULL,
    office_location_id int NULL,
    usage_location_id int NULL,
    department_id int NULL,
    job_title_id int NULL
);

CREATE UNIQUE NONCLUSTERED INDEX IX_users_user_name ON dbo.users(user_name);

CREATE TABLE dbo.outlook_user_activity_log
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_outlook_user_activity_log PRIMARY KEY CLUSTERED,
    [date] datetime NOT NULL,
    last_activity_date datetime NULL,
    user_id int NOT NULL,
    email_send_count bigint NOT NULL CONSTRAINT DF_outlook_send DEFAULT (0),
    email_receive_count bigint NOT NULL CONSTRAINT DF_outlook_receive DEFAULT (0),
    email_read_count bigint NOT NULL CONSTRAINT DF_outlook_read DEFAULT (0),
    meeting_created_count bigint NOT NULL CONSTRAINT DF_outlook_meeting_created DEFAULT (0),
    meeting_interacted_count bigint NOT NULL CONSTRAINT DF_outlook_meeting_interacted DEFAULT (0),
    CONSTRAINT FK_outlook_user_activity_log_users FOREIGN KEY(user_id) REFERENCES dbo.users(id)
);

CREATE NONCLUSTERED INDEX IX_date ON dbo.outlook_user_activity_log([date], last_activity_date) INCLUDE(user_id);
CREATE NONCLUSTERED INDEX IX_outlook_user_activity_log_user_date ON dbo.outlook_user_activity_log(user_id, [date]);

;WITH n AS
(
    SELECT TOP (@SyntheticUsers) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.users(user_name, mail, azure_ad_id, account_enabled, postalcode)
SELECT CONCAT('user', rn, '@contoso.com'), CONCAT('user', rn, '@contoso.com'),
       '00000000-0000-0000-0000-000000000000', 1, N'00000'
FROM n
ORDER BY rn;

;WITH d AS
(
    SELECT TOP (@SyntheticDays) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS day_offset
    FROM sys.all_objects
)
INSERT dbo.outlook_user_activity_log([date], last_activity_date, user_id, email_send_count, email_receive_count,
                                     email_read_count, meeting_created_count, meeting_interacted_count)
SELECT DATEADD(day, -d.day_offset, @ReportDate), DATEADD(day, -d.day_offset, @ReportDate), u.id,
       u.id % 17, u.id % 19, u.id % 23, u.id % 3, u.id % 5
FROM dbo.users AS u
CROSS JOIN d;

CHECKPOINT;

-- Existing-row materialisation query used once per report date by SqlUsageReportStore.GetRowsForDateAsync.
DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO, TIME ON;
SELECT id, [date], last_activity_date, user_id, email_send_count, email_receive_count,
       email_read_count, meeting_created_count, meeting_interacted_count
FROM dbo.outlook_user_activity_log
WHERE [date] = CONVERT(datetime, @ReportDate)
OPTION (RECOMPILE);
SET STATISTICS IO, TIME OFF;

-- Cold lookup path used by UserCache.Load before an uncached row can be inserted or linked.
DECLARE @LookupUpn varchar(250) = 'user1000@contoso.com';
DBCC FREEPROCCACHE WITH NO_INFOMSGS;
SET STATISTICS IO, TIME ON;
SELECT TOP (1) id, user_name, mail, last_updated, azure_ad_id, account_enabled, postalcode,
       org_id, company_name_id, state_or_province_id, manager_id, country_or_region_id,
       office_location_id, usage_location_id, department_id, job_title_id
FROM dbo.users
WHERE user_name = @LookupUpn
ORDER BY id
OPTION (RECOMPILE);
SET STATISTICS IO, TIME OFF;

-- Dirty-check/update shape for a changed existing row. Execute with a small and large changed-row set.
DECLARE @ChangedRows int = 1000;
;WITH c AS
(
    SELECT TOP (@ChangedRows) id
    FROM dbo.outlook_user_activity_log
    WHERE [date] = CONVERT(datetime, @ReportDate)
    ORDER BY id
)
UPDATE l
SET email_read_count = email_read_count + 1
FROM dbo.outlook_user_activity_log AS l
JOIN c ON c.id = l.id;
