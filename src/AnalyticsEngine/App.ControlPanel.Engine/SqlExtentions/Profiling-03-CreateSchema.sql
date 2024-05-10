SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

-- ============================================
-- =====                                  =====
-- =====   Create indexes in dbo tables   =====
-- =====                                  =====
-- ============================================
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.teams_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[teams_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.onedrive_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[onedrive_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.outlook_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[outlook_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.sharepoint_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[sharepoint_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.yammer_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[yammer_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_users_account_enabled' AND object_id = OBJECT_ID('dbo.users'))
  CREATE NONCLUSTERED INDEX IX_users_account_enabled
  ON [dbo].[users] (account_enabled) INCLUDE (azure_ad_id);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.teams_user_device_usage_log'))
  CREATE INDEX IX_date ON [dbo].[teams_user_device_usage_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.platform_user_activity_log'))
  CREATE INDEX IX_date ON [dbo].[platform_user_activity_log] (date);
GO
IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.yammer_device_activity_log'))
  CREATE INDEX IX_date ON [dbo].[yammer_device_activity_log] (date);
GO

-- =====================================
-- =====                           =====
-- =====          CLEANUP          =====
-- =====   Remove unused objects   =====
-- =====                           =====
-- =====================================

IF OBJECT_ID(N'usp_GetErrorInfo') IS NOT NULL DROP PROCEDURE usp_GetErrorInfo;
GO
IF OBJECT_ID(N'profiling.usp_CompileDaily') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileDaily];
GO
IF OBJECT_ID(N'profiling.usp_Version') IS NOT NULL DROP PROCEDURE [profiling].[usp_Version];
GO
IF OBJECT_ID(N'profiling.udf_GetLimitDate') IS NOT NULL DROP FUNCTION [profiling].[udf_GetLimitDate];
GO
IF OBJECT_ID(N'profiling.tvf_ActivitiesBetweenDates') IS NOT NULL DROP FUNCTION [profiling].[tvf_ActivitiesBetweenDates];
GO
IF OBJECT_ID(N'profiling.tvf_DuplicatedUsers') IS NOT NULL DROP FUNCTION [profiling].[tvf_DuplicatedUsers];
GO
IF OBJECT_ID(N'profiling.tvf_Version') IS NOT NULL DROP PROCEDURE [profiling].[tvf_Version];
GO
IF OBJECT_ID(N'profiling.Activities') IS NOT NULL DROP TABLE [profiling].[Activities];
GO
IF OBJECT_ID(N'profiling.ActivitiesDaily') IS NOT NULL DROP TABLE [profiling].[ActivitiesDaily];
GO
IF OBJECT_ID(N'profiling.usp_CompileWeek') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeek];
GO
IF OBJECT_ID(N'profiling.usp_CompileWeekColumns') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeekColumns];
GO
IF OBJECT_ID(N'profiling.usp_CompileWeekRows') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeekRows];
GO

-- =================================
-- =====                       =====
-- =====   Create the schema   =====
-- =====                       =====
-- =================================

IF NOT EXISTS (SELECT name FROM sys.schemas WHERE name = N'profiling')
  EXEC('CREATE SCHEMA [profiling] AUTHORIZATION [dbo]');
GO

-- =====================
-- =====           =====
-- =====   VIEWS   =====
-- =====           =====
-- =====================

-- =============================================
-- View to get users worth having in the reports
-- =============================================
IF OBJECT_ID(N'profiling.users') IS NOT NULL DROP VIEW [profiling].[users];
GO

CREATE VIEW [profiling].[users]
AS
  WITH
  cte_license_count AS (
    SELECT user_id, COUNT(id) license_count
    FROM [dbo].[user_license_type_lookups]
    GROUP BY user_id
  ),
  cte_enabled_users AS (
    SELECT id
    FROM dbo.users
    WHERE account_enabled = 1
    AND azure_ad_id IS NOT NULL AND azure_ad_id <> ''
  )
  SELECT eu.id, user_name, azure_ad_id, mail,
    office_location_id, usage_location_id, department_id, job_title_id,
    postalcode, company_name_id, state_or_province_id, manager_id, country_or_region_id,
    license_count
  FROM cte_license_count l
  INNER JOIN cte_enabled_users eu ON eu.id = l.user_id
  INNER JOIN dbo.users u ON u.id = eu.id;
GO

-- ===============================
-- View to get unique postal codes
-- ===============================
IF OBJECT_ID('profiling.user_PostalCodes') IS NOT NULL DROP VIEW [profiling].[user_PostalCodes];
GO

CREATE VIEW [profiling].[user_PostalCodes] AS SELECT DISTINCT [postalcode] FROM [profiling].[users];
GO

-- ======================
-- =====            =====
-- =====   TABLES   =====
-- =====            =====
-- ======================

IF OBJECT_ID(N'profiling.TraceLogs') IS NULL
  CREATE TABLE [profiling].[TraceLogs] (
    [Id] BIGINT NOT NULL IDENTITY(1,1) PRIMARY KEY,
    [Datetime] DATETIME NOT NULL,
    [Message] NVARCHAR(500) NOT NULL
  );
GO

-- ======================================================
-- Weekly aggregated activity data per user. Data in rows
-- ======================================================
IF OBJECT_ID(N'profiling.ActivitiesWeekly') IS NULL
  CREATE TABLE [profiling].[ActivitiesWeekly] (
    [user_id] BIGINT NOT NULL,
    [MetricDate] DATE NOT NULL,
    [Metric] VARCHAR(250) NOT NULL,
    [Sum] INT NOT NULL,
    CONSTRAINT [PK_ActivitiesWeekly] PRIMARY KEY CLUSTERED ([user_id] ASC, [MetricDate] ASC, [Metric] ASC) WITH (
      STATISTICS_NORECOMPUTE = OFF,
      IGNORE_DUP_KEY = OFF,
      OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
    ) ON [PRIMARY]
  );
GO

IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_MetricDate' AND object_id = OBJECT_ID('profiling.ActivitiesWeekly'))
  CREATE INDEX IX_MetricDate ON [profiling].[ActivitiesWeekly] (MetricDate);
GO

-- =========================================================
-- Weekly aggregated activity data per user. Data in columns
-- =========================================================
IF OBJECT_ID(N'profiling.ActivitiesWeeklyColumns') IS NULL
  CREATE TABLE [profiling].[ActivitiesWeeklyColumns] (
    user_id BIGINT,
    [date] DATE,
    [OneDrive Viewed/Edited] BIGINT NOT NULL DEFAULT 0,
    [OneDrive Synced] BIGINT NOT NULL DEFAULT 0,
    [OneDrive Shared Internally] BIGINT NOT NULL DEFAULT 0,
    [OneDrive Shared Externally] BIGINT NOT NULL DEFAULT 0,
    [Emails Sent] BIGINT NOT NULL DEFAULT 0,
    [Emails Received] BIGINT NOT NULL DEFAULT 0,
    [Emails Read] BIGINT NOT NULL DEFAULT 0,
    [Outlook Meetings Created] BIGINT NOT NULL DEFAULT 0,
    [Outlook Meetings Interacted] BIGINT NOT NULL DEFAULT 0,
    [SPO Viewed/Edited] BIGINT NOT NULL DEFAULT 0,
    [SPO Synced] BIGINT NOT NULL DEFAULT 0,
    [SPO Shared Internally] BIGINT NOT NULL DEFAULT 0,
    [SPO Shared Externally] BIGINT NOT NULL DEFAULT 0,
    [Teams Private Chats] BIGINT NOT NULL DEFAULT 0,
    [Teams Team Chats] BIGINT NOT NULL DEFAULT 0,
    [Teams Calls] BIGINT NOT NULL DEFAULT 0,
    [Teams Meetings] BIGINT NOT NULL DEFAULT 0,
    [Teams Meetings Attended] BIGINT NOT NULL DEFAULT 0,
    [Teams Meetings Organized] BIGINT NOT NULL DEFAULT 0,
    [Yammer Posted] BIGINT NOT NULL DEFAULT 0,
    [Yammer Read] BIGINT NOT NULL DEFAULT 0,
    [Yammer Liked] BIGINT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_ActivitiesWeeklyColumns] PRIMARY KEY CLUSTERED ([user_id] ASC, [date] ASC) WITH (
      STATISTICS_NORECOMPUTE = OFF,
      IGNORE_DUP_KEY = OFF,
      OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
    ) ON [PRIMARY]
  );
GO

-- Add possibly missing default contraints
IF NOT EXISTS(SELECT OBJECT_NAME(OBJECT_ID) FROM sys.objects WHERE type_desc LIKE 'DEFAULT_CONSTRAINT' AND SCHEMA_NAME(schema_id) = 'profiling' AND OBJECT_NAME(parent_object_id) = 'ActivitiesWeeklyColumns')
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns]
  ADD
  CONSTRAINT df_onedrive_viewed_edited DEFAULT 0 FOR [OneDrive Viewed/Edited],
  CONSTRAINT df_onedrive_synced DEFAULT 0 FOR [OneDrive Synced],
  CONSTRAINT df_onedrive_shared_internally DEFAULT 0 FOR [OneDrive Shared Internally],
  CONSTRAINT df_onedrive_shared_externally DEFAULT 0 FOR [OneDrive Shared Externally],
  CONSTRAINT df_emails_sent DEFAULT 0 FOR [Emails Sent],
  CONSTRAINT df_emails_received DEFAULT 0 FOR [Emails Received],
  CONSTRAINT df_emails_read DEFAULT 0 FOR [Emails Read],
  CONSTRAINT df_outlook_meetings_created DEFAULT 0 FOR [Outlook Meetings Created],
  CONSTRAINT df_outlook_meetings_interacted DEFAULT 0 FOR [Outlook Meetings Interacted],
  CONSTRAINT df_spo_viewed_edited DEFAULT 0 FOR [SPO Viewed/Edited],
  CONSTRAINT df_spo_synced DEFAULT 0 FOR [SPO Synced],
  CONSTRAINT df_spo_shared_internally DEFAULT 0 FOR [SPO Shared Internally],
  CONSTRAINT df_spo_shared_externally DEFAULT 0 FOR [SPO Shared Externally],
  CONSTRAINT df_teams_private_chats DEFAULT 0 FOR [Teams Private Chats],
  CONSTRAINT df_teams_team_chats DEFAULT 0 FOR [Teams Team Chats],
  CONSTRAINT df_teams_calls DEFAULT 0 FOR [Teams Calls],
  CONSTRAINT df_teams_meetings DEFAULT 0 FOR [Teams Meetings],
  CONSTRAINT df_teams_meetings_attended DEFAULT 0 FOR [Teams Meetings Attended],
  CONSTRAINT df_teams_meetings_organized DEFAULT 0 FOR [Teams Meetings Organized],
  CONSTRAINT df_yammer_posted DEFAULT 0 FOR [Yammer Posted],
  CONSTRAINT df_yammer_read DEFAULT 0 FOR [Yammer Read],
  CONSTRAINT df_yammer_liked DEFAULT 0 FOR [Yammer Liked];
GO

-- Add new metric columns
IF NOT EXISTS(SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'ActivitiesWeeklyColumns' AND TABLE_SCHEMA = 'profiling' AND COLUMN_NAME = 'Teams Adhoc Meetings Attended')
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns]
  ADD
    [Teams Adhoc Meetings Attended] BIGINT NOT NULL DEFAULT 0
    ,[Teams Adhoc Meetings Organized] BIGINT NOT NULL DEFAULT 0
    ,[Teams Scheduled Onetime Meetings Attended] BIGINT NOT NULL DEFAULT 0
    ,[Teams Scheduled Onetime Meetings Organized] BIGINT NOT NULL DEFAULT 0
    ,[Teams Scheduled Recurring Meetings Attended] BIGINT NOT NULL DEFAULT 0
    ,[Teams Scheduled Recurring Meetings Organized] BIGINT NOT NULL DEFAULT 0
    ,[Teams Audio Duration Seconds] BIGINT NOT NULL DEFAULT 0
    ,[Teams Video Duration Seconds] BIGINT NOT NULL DEFAULT 0
    ,[Teams Screenshare Duration Seconds] BIGINT NOT NULL DEFAULT 0
    ,[Teams Post Messages] BIGINT NOT NULL DEFAULT 0
    ,[Teams Reply Messages] BIGINT NOT NULL DEFAULT 0
    ,[Teams Urgent Messages] BIGINT NOT NULL DEFAULT 0;
GO

IF NOT EXISTS (SELECT name FROM sys.indexes WHERE name = N'IX_date' AND object_id = OBJECT_ID('profiling.ActivitiesWeeklyColumns'))
  CREATE INDEX IX_date ON [profiling].[ActivitiesWeeklyColumns] (date);
GO

-- Remove Yammer device columns. First constraints, then the columns
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Platform Count' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name=obj.name FROM sys.objects obj JOIN sys.columns cols ON obj.object_id = cols.default_object_id
  WHERE cols.name = 'Yammer Platform Count' AND cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns');
  EXEC ('ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP CONSTRAINT ' + @name);
END
GO
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Platform Count' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP COLUMN [Yammer Platform Count];
GO

IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Web' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name=obj.name FROM sys.objects obj JOIN sys.columns cols ON obj.object_id = cols.default_object_id
  WHERE cols.name = 'Yammer Used Web' AND cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns');
  EXEC ('ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP CONSTRAINT ' + @name);
END
GO
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Web' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP COLUMN [Yammer Used Web];
GO

IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Mobile' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name=obj.name FROM sys.objects obj JOIN sys.columns cols ON obj.object_id = cols.default_object_id
  WHERE cols.name = 'Yammer Used Mobile' AND cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns');
  EXEC ('ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP CONSTRAINT ' + @name);
END
GO
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Mobile' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP COLUMN [Yammer Used Mobile];
GO
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Others' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name=obj.name FROM sys.objects obj JOIN sys.columns cols ON obj.object_id = cols.default_object_id
  WHERE cols.name = 'Yammer Used Others' AND cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns');
  EXEC ('ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP CONSTRAINT ' + @name);
END
GO
IF EXISTS (select object_id from sys.columns WHERE name = 'Yammer Used Others' AND object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns'))
  ALTER TABLE [profiling].[ActivitiesWeeklyColumns] DROP COLUMN [Yammer Used Others];
GO

-- ======================================================
-- Weekly aggregated usage data per user. Data in columns
-- ======================================================
IF OBJECT_ID(N'profiling.UsageWeekly') IS NULL
  CREATE TABLE [profiling].[UsageWeekly] (
    [user_id] [int] NOT NULL,
    [date] [date] NOT NULL,
    -- Teams devices
    [Teams Used Web] [bit] NOT NULL DEFAULT 0,
    [Teams Used Mac] [bit] NOT NULL DEFAULT 0,
    [Teams Used Windows] [bit] NOT NULL DEFAULT 0,
    [Teams Used Linux] [bit] NOT NULL DEFAULT 0,
    [Teams Used Chrome OS] [bit] NOT NULL DEFAULT 0,
    [Teams Used Mobile] [bit] NOT NULL DEFAULT 0,
    [Teams Used WinPhone] [bit] NOT NULL DEFAULT 0,
    [Teams Used iOS] [bit] NOT NULL DEFAULT 0,
    [Teams Used Android] [bit] NOT NULL DEFAULT 0,
    -- M365 app
    [Office Windows] [bit] NOT NULL DEFAULT 0,
    [Office Mac] [bit] NOT NULL DEFAULT 0,
    [Office Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Web] [bit] NOT NULL DEFAULT 0,
    [Office Outlook] [bit] NOT NULL DEFAULT 0,
    [Office Word] [bit] NOT NULL DEFAULT 0,
    [Office Excel] [bit] NOT NULL DEFAULT 0,
    [Office Powerpoint] [bit] NOT NULL DEFAULT 0,
    [Office Onenote] [bit] NOT NULL DEFAULT 0,
    [Office Teams] [bit] NOT NULL DEFAULT 0,
    [Office Outlook Windows] [bit] NOT NULL DEFAULT 0,
    [Office Word Windows] [bit] NOT NULL DEFAULT 0,
    [Office Excel Windows] [bit] NOT NULL DEFAULT 0,
    [Office Powerpoint Windows] [bit] NOT NULL DEFAULT 0,
    [Office Onenote Windows] [bit] NOT NULL DEFAULT 0,
    [Office Teams Windows] [bit] NOT NULL DEFAULT 0,
    [Office Outlook Mac] [bit] NOT NULL DEFAULT 0,
    [Office Word Mac] [bit] NOT NULL DEFAULT 0,
    [Office Excel Mac] [bit] NOT NULL DEFAULT 0,
    [Office Powerpoint Mac] [bit] NOT NULL DEFAULT 0,
    [Office Onenote Mac] [bit] NOT NULL DEFAULT 0,
    [Office Teams Mac] [bit] NOT NULL DEFAULT 0,
    [Office Outlook Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Word Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Excel Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Powerpoint Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Onenote Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Teams Mobile] [bit] NOT NULL DEFAULT 0,
    [Office Outlook Web] [bit] NOT NULL DEFAULT 0,
    [Office Word Web] [bit] NOT NULL DEFAULT 0,
    [Office Excel Web] [bit] NOT NULL DEFAULT 0,
    [Office Powerpoint Web] [bit] NOT NULL DEFAULT 0,
    [Office Onenote Web] [bit] NOT NULL DEFAULT 0,
    [Office Teams Web] [bit] NOT NULL DEFAULT 0,
    -- Yammer devices
    [Yammer Platform Count] [tinyint] NOT NULL DEFAULT 0,
    [Yammer Used Web] [bit] NOT NULL DEFAULT 0,
    [Yammer Used Mobile] [bit] NOT NULL DEFAULT 0,
    [Yammer Used Others] [bit] NOT NULL DEFAULT 0,
    [Yammer Used WinPhone] [bit] NOT NULL DEFAULT 0,
    [Yammer Used Android] [bit] NOT NULL DEFAULT 0,
    [Yammer Used iPad] [bit] NOT NULL DEFAULT 0,
    [Yammer Used iPhone] [bit] NOT NULL DEFAULT 0,
    CONSTRAINT [PK_profiling.UsageWeekly] PRIMARY KEY CLUSTERED ([user_id], [date] ASC) WITH (
      STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
    ) ON [PRIMARY]
  );
GO

-- =========================
-- =====               =====
-- =====   FUNCTIONS   =====
-- =====               =====
-- =========================

-- ===========================================
-- From a date, return the Monday of that week
-- ===========================================
IF OBJECT_ID(N'profiling.udf_GetMonday') IS NOT NULL
  DROP FUNCTION [profiling].[udf_GetMonday];
GO

CREATE FUNCTION [profiling].[udf_GetMonday] (@CurrentDate DATE)
RETURNS DATE
AS
BEGIN
  DECLARE @Result DATE, @ThisDayWasMonday INT;
  -- Set the first day of the week to Monday
  SET @ThisDayWasMonday = DATEPART(WEEKDAY, '20230102');
  SET @RESULT = @CurrentDate;
  WHILE @ThisDayWasMonday <> DATEPART(WEEKDAY, @Result)
  BEGIN
    SET @Result = CONVERT(DATE, DATEADD(DAY, -1, @Result));
  END
  RETURN @Result;
END
GO

-- ============================================================
-- =====                                                  =====
-- =====      Aggregation Table Types and procedures      =====
-- =====   (They have a specific order to be recreated)   =====
-- =====                                                  =====
-- ============================================================

-- First drop the SPs, then the Table Types
IF OBJECT_ID(N'profiling.usp_UpsertTeams') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertTeams];
GO
IF OBJECT_ID(N'profiling.usp_UpsertOneDrive') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertOneDrive];
GO
IF OBJECT_ID(N'profiling.usp_UpsertSharePoint') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertSharePoint];
GO
IF OBJECT_ID(N'profiling.usp_UpsertOutlook') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertOutlook];
GO
IF OBJECT_ID(N'profiling.usp_UpsertYammer') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertYammer];
GO
IF OBJECT_ID(N'profiling.usp_UpsertTeamsDevices') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertTeamsDevices];
GO
IF OBJECT_ID(N'profiling.usp_UpsertM365Apps') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertM365Apps];
GO
IF OBJECT_ID(N'profiling.usp_UpsertYammerDevices') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertYammerDevices];
GO

-- Add Copilot Upsert DROP PROCEDURE here

-- TODO

IF OBJECT_ID(N'profiling.usp_UpsertCopilot') IS NOT NULL DROP PROCEDURE [profiling].[usp_UpsertCopilot];
GO

-------------

IF TYPE_ID(N'ut_teams_user_activity_log') IS NOT NULL DROP TYPE ut_teams_user_activity_log;
GO
IF TYPE_ID(N'ut_onedrive_user_activity_log') IS NOT NULL DROP TYPE ut_onedrive_user_activity_log;
GO
IF TYPE_ID(N'ut_sharepoint_user_activity_log') IS NOT NULL DROP TYPE ut_sharepoint_user_activity_log;
GO
IF TYPE_ID(N'ut_outlook_user_activity_log') IS NOT NULL DROP TYPE ut_outlook_user_activity_log;
GO
IF TYPE_ID(N'ut_yammer_user_activity_log') IS NOT NULL DROP TYPE ut_yammer_user_activity_log;
GO
IF TYPE_ID(N'ut_teams_user_device_usage_log') IS NOT NULL DROP TYPE ut_teams_user_device_usage_log;
GO
IF TYPE_ID(N'ut_platform_user_activity_log') IS NOT NULL DROP TYPE ut_platform_user_activity_log;
GO
IF TYPE_ID(N'ut_yammer_device_activity_log') IS NOT NULL DROP TYPE ut_yammer_device_activity_log;
GO

--Add Copilot DROP TYPE here

-- TODO

IF TYPE_ID(N'ut_copilot_activity_log') IS NOT NULL DROP TYPE ut_copilot_activity_log;
GO


-- Add Table Types
IF TYPE_ID(N'ut_teams_user_activity_log') IS NULL CREATE TYPE ut_teams_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [private_chat_count] BIGINT NOT NULL,
  [team_chat_count] BIGINT NOT NULL,
  [calls_count] BIGINT NOT NULL,
  [meetings_count] BIGINT NOT NULL,
  [meetings_attended_count] BIGINT NOT NULL,
  [meetings_organized_count] BIGINT NOT NULL,
  [adhoc_meetings_attended_count] BIGINT NOT NULL,
  [adhoc_meetings_organized_count] BIGINT NOT NULL,
  [scheduled_onetime_meetings_attended_count] BIGINT NOT NULL,
  [scheduled_onetime_meetings_organized_count] BIGINT NOT NULL,
  [scheduled_recurring_meetings_attended_count] BIGINT NOT NULL,
  [scheduled_recurring_meetings_organized_count] BIGINT NOT NULL,
  [audio_duration_seconds] INT NOT NULL,
  [video_duration_seconds] INT NOT NULL,
  [screenshare_duration_seconds] INT NOT NULL,
  [post_messages] BIGINT NOT NULL,
  [reply_messages] BIGINT NOT NULL,
  [urgent_messages] BIGINT NOT NULL,

  -- ADD Copilot fields here
  -- TODO

  [copilot_chats_count] BIGINT NOT NULL,
  [copilot_meetings_count] BIGINT NOT NULL,
  [copilot_files_count] BIGINT NOT NULL


);
GO
IF TYPE_ID(N'ut_onedrive_user_activity_log') IS NULL CREATE TYPE ut_onedrive_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [viewed_or_edited] BIGINT NOT NULL,
  [synced] BIGINT NOT NULL,
  [shared_internally] BIGINT NOT NULL,
  [shared_externally] BIGINT NOT NULL
);
GO
IF TYPE_ID(N'ut_sharepoint_user_activity_log') IS NULL CREATE TYPE ut_sharepoint_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [viewed_or_edited] BIGINT NOT NULL,
  [synced] BIGINT NOT NULL,
  [shared_internally] BIGINT NOT NULL,
  [shared_externally] BIGINT NOT NULL
);
GO
IF TYPE_ID(N'ut_outlook_user_activity_log') IS NULL CREATE TYPE ut_outlook_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [email_send_count] BIGINT NOT NULL,
  [email_receive_count] BIGINT NOT NULL,
  [email_read_count] BIGINT NOT NULL,
  [meeting_created_count] BIGINT NOT NULL,
  [meeting_interacted_count] BIGINT NOT NULL
);
GO
IF TYPE_ID(N'ut_yammer_user_activity_log') IS NULL CREATE TYPE ut_yammer_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [posted_count] INT NOT NULL,
  [read_count] INT NOT NULL,
  [liked_count] INT NOT NULL
);
GO
IF TYPE_ID(N'ut_teams_user_device_usage_log') IS NULL CREATE TYPE ut_teams_user_device_usage_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [used_web] [int] NOT NULL DEFAULT 0,
  [used_mac] [int] NOT NULL DEFAULT 0,
  [used_windows] [int] NOT NULL DEFAULT 0,
  [used_linux] [int] NOT NULL DEFAULT 0,
  [used_chrome_os] [int] NOT NULL DEFAULT 0,
  [used_mobile] [int] NOT NULL DEFAULT 0,
  [used_win_phone] [int] NOT NULL DEFAULT 0,
  [used_ios] [int] NOT NULL DEFAULT 0,
  [used_android] [int] NOT NULL DEFAULT 0
);
GO
IF TYPE_ID(N'ut_platform_user_activity_log') IS NULL CREATE TYPE ut_platform_user_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [windows] [int] NOT NULL DEFAULT 0,
  [mac] [int] NOT NULL DEFAULT 0,
  [mobile] [int] NOT NULL DEFAULT 0,
  [web] [int] NOT NULL DEFAULT 0,
  [outlook] [int] NOT NULL DEFAULT 0,
  [word] [int] NOT NULL DEFAULT 0,
  [excel] [int] NOT NULL DEFAULT 0,
  [powerpoint] [int] NOT NULL DEFAULT 0,
  [onenote] [int] NOT NULL DEFAULT 0,
  [teams] [int] NOT NULL DEFAULT 0,
  [outlook_windows] [int] NOT NULL DEFAULT 0,
  [word_windows] [int] NOT NULL DEFAULT 0,
  [excel_windows] [int] NOT NULL DEFAULT 0,
  [powerpoint_windows] [int] NOT NULL DEFAULT 0,
  [onenote_windows] [int] NOT NULL DEFAULT 0,
  [teams_windows] [int] NOT NULL DEFAULT 0,
  [outlook_mac] [int] NOT NULL DEFAULT 0,
  [word_mac] [int] NOT NULL DEFAULT 0,
  [excel_mac] [int] NOT NULL DEFAULT 0,
  [powerpoint_mac] [int] NOT NULL DEFAULT 0,
  [onenote_mac] [int] NOT NULL DEFAULT 0,
  [teams_mac] [int] NOT NULL DEFAULT 0,
  [outlook_mobile] [int] NOT NULL DEFAULT 0,
  [word_mobile] [int] NOT NULL DEFAULT 0,
  [excel_mobile] [int] NOT NULL DEFAULT 0,
  [powerpoint_mobile] [int] NOT NULL DEFAULT 0,
  [onenote_mobile] [int] NOT NULL DEFAULT 0,
  [teams_mobile] [int] NOT NULL DEFAULT 0,
  [outlook_web] [int] NOT NULL DEFAULT 0,
  [word_web] [int] NOT NULL DEFAULT 0,
  [excel_web] [int] NOT NULL DEFAULT 0,
  [powerpoint_web] [int] NOT NULL DEFAULT 0,
  [onenote_web] [int] NOT NULL DEFAULT 0,
  [teams_web] [int] NOT NULL DEFAULT 0
);
GO
IF TYPE_ID(N'ut_yammer_device_activity_log') IS NULL CREATE TYPE ut_yammer_device_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,
  [used_count] [int] NOT NULL,
  [used_web] [int] NOT NULL,
  [used_mobile] [int] NOT NULL,
  [used_others] [int] NOT NULL,
	[used_win_phone] [int] NOT NULL,
	[used_android] [int] NOT NULL,
	[used_ipad] [int] NOT NULL,
	[used_iphone] [int] NOT NULL
);
GO


-- Add Copilot TYPE

-- TODO

IF TYPE_ID(N'ut_copilot_activity_log') IS NULL CREATE TYPE ut_copilot_activity_log AS TABLE (
  [user_id] INT NOT NULL,
  [date] DATETIME NOT NULL,

  [copilot_chats_count] BIGINT NOT NULL,
  [copilot_meetings_count] BIGINT NOT NULL,
  [copilot_files_count] BIGINT NOT NULL
);
GO



-- Add SPs


-- ADD COPILOT PRODECURE here

-- TODO

CREATE PROCEDURE [profiling].[usp_UpsertCopilot] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @copilot AS ut_copilot_activity_log;

  INSERT INTO @copilot (
    [user_id], [date],
    [copilot_chats_count],  [copilot_meetings_count], [copilot_files_count]


  )
  SELECT
    [user_id], @StartDate,
    SUM([copilot_chats_count]), SUM([copilot_meetings_count]), SUM([copilot_files_count])

  FROM dbo.[copilot_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Copilot ChatsCount] = tvp.[copilot_chats_count],
    [Copilot MeetingsCount] = tvp.[copilot_meetings_count],
    [Copilot FilesCount] = tvp.[copilot_files_count]
 

  FROM #ActivitiesStaging AS t
  INNER JOIN @copilot AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]
 
  )
  SELECT
    user_id, date,
    [copilot_chats_count], [copilot_meetings_count], [copilot_files_count]
  FROM @copilot as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

------------------------------------------------


CREATE PROCEDURE [profiling].[usp_UpsertTeams] (
    @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @teams AS ut_teams_user_activity_log;

  INSERT INTO @teams (
    [user_id], [date],
    [private_chat_count], [team_chat_count], [calls_count],
    [meetings_count], [meetings_attended_count], [meetings_organized_count],
    [adhoc_meetings_attended_count], [adhoc_meetings_organized_count],
    [scheduled_onetime_meetings_attended_count], [scheduled_onetime_meetings_organized_count],
    [scheduled_recurring_meetings_attended_count], [scheduled_recurring_meetings_organized_count],
    [audio_duration_seconds], [video_duration_seconds], [screenshare_duration_seconds],
    [post_messages], [reply_messages], [urgent_messages]
  )
  SELECT
    [user_id], @StartDate,
    SUM([private_chat_count]), SUM([team_chat_count]), SUM([calls_count]),
    SUM([meetings_count]), SUM([meetings_attended_count]), SUM([meetings_organized_count]),
    SUM([adhoc_meetings_attended_count]), SUM([adhoc_meetings_organized_count]),
    SUM([scheduled_onetime_meetings_attended_count]), SUM([scheduled_onetime_meetings_organized_count]),
    SUM([scheduled_recurring_meetings_attended_count]), SUM([scheduled_recurring_meetings_organized_count]),
    SUM([audio_duration_seconds]), SUM([video_duration_seconds]), SUM([screenshare_duration_seconds]),
    SUM([post_messages]), SUM([reply_messages]), SUM([urgent_messages])
  FROM dbo.[teams_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Teams Private Chats] = tvp.[private_chat_count],
    [Teams Team Chats] = tvp.[team_chat_count],
    [Teams Calls] = tvp.[calls_count],
    [Teams Meetings] = tvp.[meetings_count],
    [Teams Meetings Attended] = tvp.[meetings_attended_count],
    [Teams Meetings Organized] = tvp.[meetings_organized_count],
    [Teams Adhoc Meetings Attended] = tvp.[adhoc_meetings_attended_count],
    [Teams Adhoc Meetings Organized] = tvp.[adhoc_meetings_organized_count],
    [Teams Scheduled Onetime Meetings Attended] = tvp.[scheduled_onetime_meetings_attended_count],
    [Teams Scheduled Onetime Meetings Organized] = tvp.[scheduled_onetime_meetings_organized_count],
    [Teams Scheduled Recurring Meetings Attended] = tvp.[scheduled_recurring_meetings_attended_count],
    [Teams Scheduled Recurring Meetings Organized] = tvp.[scheduled_recurring_meetings_organized_count],
    [Teams Audio Duration Seconds] = tvp.[audio_duration_seconds],
    [Teams Video Duration Seconds] = tvp.[video_duration_seconds],
    [Teams Screenshare Duration Seconds] = tvp.[screenshare_duration_seconds],
    [Teams Post Messages] = tvp.[post_messages],
    [Teams Reply Messages] = tvp.[reply_messages],
    [Teams Urgent Messages] = tvp.[urgent_messages]
  FROM #ActivitiesStaging AS t
  INNER JOIN @teams AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [Teams Private Chats], [Teams Team Chats], [Teams Calls],
    [Teams Meetings], [Teams Meetings Attended], [Teams Meetings Organized],
    [Teams Adhoc Meetings Attended], [Teams Adhoc Meetings Organized],
    [Teams Scheduled Onetime Meetings Attended], [Teams Scheduled Onetime Meetings Organized],
    [Teams Scheduled Recurring Meetings Attended], [Teams Scheduled Recurring Meetings Organized],
    [Teams Audio Duration Seconds], [Teams Video Duration Seconds], [Teams Screenshare Duration Seconds],
    [Teams Post Messages], [Teams Reply Messages], [Teams Urgent Messages]
  )
  SELECT
    user_id, date,
    [private_chat_count], [team_chat_count], [calls_count],
    [meetings_count], [meetings_attended_count], [meetings_organized_count],
    [adhoc_meetings_attended_count], [adhoc_meetings_organized_count],
    [scheduled_onetime_meetings_attended_count], [scheduled_onetime_meetings_organized_count],
    [scheduled_recurring_meetings_attended_count], [scheduled_recurring_meetings_organized_count],
    [audio_duration_seconds], [video_duration_seconds], [screenshare_duration_seconds],
    [post_messages], [reply_messages], [urgent_messages]
  FROM @teams as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertOneDrive] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @onedrive AS ut_onedrive_user_activity_log;

  INSERT INTO @onedrive (
    [user_id], [date],
    [viewed_or_edited], [synced], [shared_internally], [shared_externally]
  )
  SELECT
    [user_id], @StartDate,
    SUM([viewed_or_edited]), SUM([synced]), SUM([shared_internally]), SUM([shared_externally])
  FROM dbo.[onedrive_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [OneDrive Viewed/Edited] = tvp.[viewed_or_edited],
    [OneDrive Synced] = tvp.[synced],
    [OneDrive Shared Internally] = tvp.[shared_internally],
    [OneDrive Shared Externally] = tvp.[shared_externally]
  FROM #ActivitiesStaging AS t
  INNER JOIN @onedrive AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [OneDrive Viewed/Edited], [OneDrive Synced], [OneDrive Shared Internally], [OneDrive Shared Externally]
  )
  SELECT
    user_id, date,
    [viewed_or_edited], [synced], [shared_internally], [shared_externally]
  FROM @onedrive as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertSharePoint] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @sharepoint AS ut_sharepoint_user_activity_log;

  INSERT INTO @sharepoint (
    [user_id], [date],
    [viewed_or_edited], [synced], [shared_internally], [shared_externally]
  )
  SELECT
    [user_id], @StartDate,
    SUM([viewed_or_edited]), SUM([synced]), SUM([shared_internally]), SUM([shared_externally])
  FROM dbo.[sharepoint_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [SPO Viewed/Edited] = tvp.[viewed_or_edited],
    [SPO Synced] = tvp.[synced],
    [SPO Shared Internally] = tvp.[shared_internally],
    [SPO Shared Externally] = tvp.[shared_externally]
  FROM #ActivitiesStaging AS t
  INNER JOIN @sharepoint AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [SPO Viewed/Edited], [SPO Synced], [SPO Shared Internally], [SPO Shared Externally]
  )
  SELECT
    user_id, date,
    [viewed_or_edited], [synced], [shared_internally], [shared_externally]
  FROM @sharepoint as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertOutlook] (
    @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @outlook AS ut_outlook_user_activity_log;

  INSERT INTO @outlook (
    [user_id], [date],
    [email_send_count], [email_receive_count], [email_read_count],
    [meeting_created_count], [meeting_interacted_count]
  )
  SELECT
    [user_id], @StartDate,
    SUM([email_send_count]), SUM([email_receive_count]), SUM([email_read_count]),
    SUM([meeting_created_count]), SUM([meeting_interacted_count])
  FROM dbo.[outlook_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Emails Sent] = tvp.[email_send_count],
    [Emails Received] = tvp.[email_receive_count],
    [Emails Read] = tvp.[email_read_count],
    [Outlook Meetings Created] = tvp.[meeting_created_count],
    [Outlook Meetings Interacted] = tvp.[meeting_interacted_count]
  FROM #ActivitiesStaging AS t
  INNER JOIN @outlook AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [Emails Sent], [Emails Received], [Emails Read],
    [Outlook Meetings Created], [Outlook Meetings Interacted]
  )
  SELECT
    user_id, date,
    [email_send_count], [email_receive_count], [email_read_count],
    [meeting_created_count], [meeting_interacted_count]
  FROM @outlook as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertYammer] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @yammer AS ut_yammer_user_activity_log;

  INSERT INTO @yammer (
    [user_id], [date],
    [posted_count], [read_count], [liked_count]
  )
  SELECT
    [user_id], @StartDate,
    SUM([posted_count]), SUM([read_count]), SUM([liked_count])
  FROM dbo.[yammer_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Yammer Posted] = tvp.[posted_count],
    [Yammer Read] = tvp.[read_count],
    [Yammer Liked] = tvp.[liked_count]
  FROM #ActivitiesStaging AS t
  INNER JOIN @yammer AS tvp ON t.user_id = tvp.user_id AND t.date = tvp.date;

  INSERT #ActivitiesStaging (
    user_id, date,
    [Yammer Posted], [Yammer Read], [Yammer Liked]
  )
  SELECT
    user_id, date,
    [posted_count], [read_count], [liked_count]
  FROM @yammer as tvp
  WHERE NOT EXISTS (SELECT 1 FROM #ActivitiesStaging as t WHERE t.user_id = tvp.user_id AND t.date = tvp.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertTeamsDevices] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_teams_user_device_usage_log;

  INSERT INTO @ut_staging (
    [user_id], [date],
    [used_web], [used_mac], [used_windows], [used_linux], [used_chrome_os],
    [used_mobile], [used_win_phone], [used_ios], [used_android]
  )
  SELECT
    [user_id], @StartDate,
    -- multiplication here converts the bit column into an int so that aggregation works
    MAX(1*[used_web]), MAX(1*[used_mac]), MAX(1*[used_windows]),
    -- Use COALESCE here to fix #1
    MAX(1*COALESCE([used_linux], 0)), MAX(1*COALESCE([used_chrome_os], 0)),
    MAX(1*[used_win_phone] + 1*[used_ios] + 1*[used_android]), -- used mobile
    MAX(1*[used_win_phone]), MAX(1*[used_ios]), MAX(1*[used_android])
  FROM dbo.[teams_user_device_usage_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Teams Used Web] = staging.[used_web],
    [Teams Used Mac] = staging.[used_mac],
    [Teams Used Windows] = staging.[used_windows],
    [Teams Used Linux] = staging.[used_linux],
    [Teams Used Chrome OS] = staging.[used_chrome_os],
    [Teams Used Mobile] = staging.[used_mobile],
    [Teams Used WinPhone] = staging.[used_win_phone],
    [Teams Used iOS] = staging.[used_ios],
    [Teams Used Android] = staging.[used_android]
  FROM #UsageStaging AS t
  INNER JOIN @ut_staging AS staging ON t.user_id = staging.user_id AND t.date = staging.date;

  INSERT #UsageStaging (
    [user_id], [date],
    [Teams Used Web], [Teams Used Mac], [Teams Used Windows], [Teams Used Linux], [Teams Used Chrome OS],
    [Teams Used Mobile], [Teams Used WinPhone], [Teams Used iOS], [Teams Used Android]
  )
  SELECT
    [user_id], [date],
    [used_web], [used_mac], [used_windows], [used_linux], [used_chrome_os],
    [used_mobile], [used_win_phone], [used_ios], [used_android]
  FROM @ut_staging as staging
  WHERE NOT EXISTS (SELECT 1 FROM #UsageStaging as t WHERE t.user_id = staging.user_id AND t.date = staging.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertM365Apps] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_platform_user_activity_log;

  INSERT INTO @ut_staging (
    [user_id], [date],
    [windows], [mac], [mobile], [web],
    [outlook], [word], [excel], [powerpoint], [onenote], [teams],
    [outlook_windows], [word_windows], [excel_windows], [powerpoint_windows], [onenote_windows], [teams_windows],
    [outlook_mac], [word_mac], [excel_mac], [powerpoint_mac], [onenote_mac], [teams_mac],
    [outlook_mobile], [word_mobile], [excel_mobile], [powerpoint_mobile], [onenote_mobile], [teams_mobile],
    [outlook_web], [word_web], [excel_web], [powerpoint_web], [onenote_web], [teams_web]
  )
  SELECT
    [user_id], @StartDate,
    MAX(1*[windows]), MAX(1*[mac]), MAX(1*[mobile]), MAX(1*[web]),
    MAX(1*[outlook]), MAX(1*[word]), MAX(1*[excel]), MAX(1*[powerpoint]), MAX(1*[onenote]), MAX(1*[teams]),
    MAX(1*[outlook_windows]), MAX(1*[word_windows]), MAX(1*[excel_windows]), MAX(1*[powerpoint_windows]), MAX(1*[onenote_windows]), MAX(1*[teams_windows]),
    MAX(1*[outlook_mac]), MAX(1*[word_mac]), MAX(1*[excel_mac]), MAX(1*[powerpoint_mac]), MAX(1*[onenote_mac]), MAX(1*[teams_mac]),
    MAX(1*[outlook_mobile]), MAX(1*[word_mobile]), MAX(1*[excel_mobile]), MAX(1*[powerpoint_mobile]), MAX(1*[onenote_mobile]), MAX(1*[teams_mobile]),
    MAX(1*[outlook_web]), MAX(1*[word_web]), MAX(1*[excel_web]), MAX(1*[powerpoint_web]), MAX(1*[onenote_web]), MAX(1*[teams_web])
  FROM dbo.[platform_user_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Office Windows] = staging.[windows],
    [Office Mac] = staging.[mac],
    [Office Mobile] = staging.[mobile],
    [Office Web] = staging.[web],
    [Office Outlook] = staging.[outlook],
    [Office Word] = staging.[word],
    [Office Excel] = staging.[excel],
    [Office Powerpoint] = staging.[powerpoint],
    [Office Onenote] = staging.[onenote],
    [Office Teams] = staging.[teams],
    [Office Outlook Windows] = staging.[outlook_windows],
    [Office Word Windows] = staging.[word_windows],
    [Office Excel Windows] = staging.[excel_windows],
    [Office Powerpoint Windows] = staging.[powerpoint_windows],
    [Office Onenote Windows] = staging.[onenote_windows],
    [Office Teams Windows] = staging.[teams_windows],
    [Office Outlook Mac] = staging.[outlook_mac],
    [Office Word Mac] = staging.[word_mac],
    [Office Excel Mac] = staging.[excel_mac],
    [Office Powerpoint Mac] = staging.[powerpoint_mac],
    [Office Onenote Mac] = staging.[onenote_mac],
    [Office Teams Mac] = staging.[teams_mac],
    [Office Outlook Mobile] = staging.[outlook_mobile],
    [Office Word Mobile] = staging.[word_mobile],
    [Office Excel Mobile] = staging.[excel_mobile],
    [Office Powerpoint Mobile] = staging.[powerpoint_mobile],
    [Office Onenote Mobile] = staging.[onenote_mobile],
    [Office Teams Mobile] = staging.[teams_mobile],
    [Office Outlook Web] = staging.[outlook_web],
    [Office Word Web] = staging.[word_web],
    [Office Excel Web] = staging.[excel_web],
    [Office Powerpoint Web] = staging.[powerpoint_web],
    [Office Onenote Web] = staging.[onenote_web],
    [Office Teams Web] = staging.[teams_web]
  FROM #UsageStaging AS t
  INNER JOIN @ut_staging AS staging ON t.user_id = staging.user_id AND t.date = staging.date;

  INSERT #UsageStaging (
    [user_id], [date],
    [Office Windows], [Office Mac], [Office Mobile], [Office Web],
    [Office Outlook], [Office Word], [Office Excel], [Office Powerpoint], [Office Onenote], [Office Teams],
    [Office Outlook Windows], [Office Word Windows], [Office Excel Windows], [Office Powerpoint Windows], [Office Onenote Windows], [Office Teams Windows],
    [Office Outlook Mac], [Office Word Mac], [Office Excel Mac], [Office Powerpoint Mac], [Office Onenote Mac], [Office Teams Mac],
    [Office Outlook Mobile], [Office Word Mobile], [Office Excel Mobile], [Office Powerpoint Mobile], [Office Onenote Mobile], [Office Teams Mobile],
    [Office Outlook Web], [Office Word Web], [Office Excel Web], [Office Powerpoint Web], [Office Onenote Web], [Office Teams Web]
  )
  SELECT
    [user_id], [date],
    [windows], [mac], [mobile], [web],
    [outlook], [word], [excel], [powerpoint], [onenote], [teams],
    [outlook_windows], [word_windows], [excel_windows], [powerpoint_windows], [onenote_windows], [teams_windows],
    [outlook_mac], [word_mac], [excel_mac], [powerpoint_mac], [onenote_mac], [teams_mac],
    [outlook_mobile], [word_mobile], [excel_mobile], [powerpoint_mobile], [onenote_mobile], [teams_mobile],
    [outlook_web], [word_web], [excel_web], [powerpoint_web], [onenote_web], [teams_web]
  FROM @ut_staging as staging
  WHERE NOT EXISTS (SELECT 1 FROM #UsageStaging as t WHERE t.user_id = staging.user_id AND t.date = staging.date);
END
GO

CREATE PROCEDURE [profiling].[usp_UpsertYammerDevices] (
  @StartDate DATE, @EndDate DATE
) AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_yammer_device_activity_log;

  INSERT INTO @ut_staging (
    [user_id], [date],
    [used_count], [used_web], [used_mobile], [used_others],
    [used_win_phone], [used_android], [used_ipad], [used_iphone]
  )
  SELECT
    [user_id], @StartDate,
    MAX(1 * [used_web] + 1 * [used_others] + 1 * [used_win_phone] + 1 * [used_android]
      + 1 * [used_ipad] + 1 * [used_iphone] + 1 * [used_others]),
    MAX(1 * [used_web]),
    MAX(1 * [used_win_phone] + 1 * [used_android] + 1 * [used_ipad] + 1 * [used_iphone]),
    MAX(1 * [used_others]),
    MAX(1 * [used_win_phone]),
    MAX(1 * [used_android]),
    MAX(1 * [used_ipad]),
    MAX(1 * [used_iphone])
  FROM [dbo].[yammer_device_activity_log]
  WHERE @StartDate <= [date] AND [date] <= @EndDate
  GROUP BY [user_id];

  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    [Yammer Platform Count] = staging.[used_count],
    [Yammer Used Web] = staging.[used_web],
    [Yammer Used Mobile] = staging.[used_mobile],
    [Yammer Used Others] = staging.[used_others],
    [Yammer Used WinPhone] = staging.[used_win_phone],
    [Yammer Used Android] = staging.[used_android],
    [Yammer Used iPad] = staging.[used_ipad],
    [Yammer Used iPhone] = staging.[used_iphone]
  FROM #UsageStaging AS t
  INNER JOIN @ut_staging AS staging ON t.user_id = staging.user_id AND t.date = staging.date;

  INSERT #UsageStaging (
    user_id, date,
    [Yammer Platform Count], [Yammer Used Web], [Yammer Used Mobile], [Yammer Used Others],
    [Yammer Used WinPhone], [Yammer Used Android], [Yammer Used iPad], [Yammer Used iPhone]
  )
  SELECT
    user_id, date,
    [used_count], [used_web], [used_mobile], [used_others],
    [used_win_phone], [used_android], [used_ipad], [used_iphone]
  FROM @ut_staging as staging
  WHERE NOT EXISTS (SELECT 1 FROM #UsageStaging as t WHERE t.user_id = staging.user_id AND t.date = staging.date);
END
GO

-- ===========================================
-- Aggregates a week of activity. Data in rows
-- ===========================================
IF OBJECT_ID(N'profiling.usp_CompileWeekActivityRows') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeekActivityRows];
GO

CREATE PROCEDURE [profiling].[usp_CompileWeekActivityRows] (
  @Monday DATE -- Start day of the week to aggregate
) AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileWeekActivityRows] Starting: ' + CAST(@Monday AS NVARCHAR);
    INSERT INTO [profiling].[ActivitiesWeekly]
    SELECT
      user_id,
      @Monday as MetricDate,
      Metric,
      Sum(Value) as Sum
    FROM #ActivitiesStaging AS Pivoted
    UNPIVOT (
      -- Convert columns into rows
      Value FOR Metric IN (
        [OneDrive Viewed/Edited], [OneDrive Synced],
        [OneDrive Shared Internally], [OneDrive Shared Externally],
        [Emails Sent], [Emails Received], [Emails Read],
        [Outlook Meetings Created], [Outlook Meetings Interacted],
        [SPO Viewed/Edited], [SPO Synced],
        [SPO Shared Internally], [SPO Shared Externally],
        [Teams Private Chats], [Teams Team Chats], [Teams Calls],
        [Teams Meetings], [Teams Meetings Attended], [Teams Meetings Organized],
        [Teams Adhoc Meetings Attended], [Teams Adhoc Meetings Organized],
        [Teams Scheduled Onetime Meetings Attended], [Teams Scheduled Onetime Meetings Organized],
        [Teams Scheduled Recurring Meetings Attended], [Teams Scheduled Recurring Meetings Organized],
        [Teams Audio Duration Seconds], [Teams Video Duration Seconds], [Teams Screenshare Duration Seconds],
        [Teams Post Messages], [Teams Reply Messages], [Teams Urgent Messages],
        [Yammer Posted], [Yammer Read], [Yammer Liked],

        -- Add here Copilot fields

        -- TODO

        [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]

      )
    ) AS Unpivoted
    GROUP BY user_id, Metric;
  END TRY
  BEGIN CATCH
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileWeekActivityRows] Catch: ' + ERROR_MESSAGE();
  END CATCH
END
GO

-- ==============================================
-- Aggregates a week of activity. Data in columns
-- ==============================================
IF OBJECT_ID(N'profiling.usp_CompileWeekActivityColumns') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeekActivityColumns];
GO

CREATE PROCEDURE [profiling].[usp_CompileWeekActivityColumns] (
  @Monday DATE -- Start day of the week to aggregate
) AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileWeekActivityColumns] Starting: ' + CAST(@Monday AS NVARCHAR);
    INSERT INTO [profiling].[ActivitiesWeeklyColumns] (
      user_id, date,
      [OneDrive Viewed/Edited], [OneDrive Synced],
      [OneDrive Shared Internally], [OneDrive Shared Externally],

      [Emails Sent], [Emails Received], [Emails Read],
      [Outlook Meetings Created], [Outlook Meetings Interacted],

      [SPO Viewed/Edited], [SPO Synced],
      [SPO Shared Internally], [SPO Shared Externally],

      [Teams Private Chats], [Teams Team Chats], [Teams Calls],
      [Teams Meetings], [Teams Meetings Attended], [Teams Meetings Organized],
      [Teams Adhoc Meetings Attended], [Teams Adhoc Meetings Organized],
      [Teams Scheduled Onetime Meetings Attended], [Teams Scheduled Onetime Meetings Organized],
      [Teams Scheduled Recurring Meetings Attended], [Teams Scheduled Recurring Meetings Organized],
      [Teams Audio Duration Seconds], [Teams Video Duration Seconds], [Teams Screenshare Duration Seconds],
      [Teams Post Messages], [Teams Reply Messages], [Teams Urgent Messages],

      [Yammer Posted], [Yammer Read], [Yammer Liked],

      -- Add here Copilot fields

      -- TODO
      [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]


    )
    SELECT
      user_id,
      @Monday as date,
      [OneDrive Viewed/Edited], [OneDrive Synced],
      [OneDrive Shared Internally], [OneDrive Shared Externally],

      [Emails Sent], [Emails Received], [Emails Read],
      [Outlook Meetings Created], [Outlook Meetings Interacted],

      [SPO Viewed/Edited], [SPO Synced],
      [SPO Shared Internally], [SPO Shared Externally],

      [Teams Private Chats], [Teams Team Chats], [Teams Calls],
      [Teams Meetings], [Teams Meetings Attended], [Teams Meetings Organized],
      [Teams Adhoc Meetings Attended], [Teams Adhoc Meetings Organized],
      [Teams Scheduled Onetime Meetings Attended], [Teams Scheduled Onetime Meetings Organized],
      [Teams Scheduled Recurring Meetings Attended], [Teams Scheduled Recurring Meetings Organized],
      [Teams Audio Duration Seconds], [Teams Video Duration Seconds], [Teams Screenshare Duration Seconds],
      [Teams Post Messages], [Teams Reply Messages], [Teams Urgent Messages],

      [Yammer Posted], [Yammer Read], [Yammer Liked],

      --Add here Copilot fields

      -TODO
      [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]


    FROM #ActivitiesStaging;
  END TRY
  BEGIN CATCH
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileWeekActivityColumns] Catch: ' + ERROR_MESSAGE();
  END CATCH
END
GO

-- ===========================================
-- Aggregates a week of usage. Data in columns
-- ===========================================
IF OBJECT_ID(N'profiling.usp_CompileUsageWeek') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileUsageWeek];
GO

CREATE PROCEDURE [profiling].[usp_CompileUsageWeek] (
  @Monday DATE -- Start day of the week to aggregate
) AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    -- SET NOCOUNT ON added to prevent extra result sets from interfering with SELECT statements.
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileUsageWeek] Starting: ' + CAST(@Monday AS NVARCHAR);

    DECLARE
      @Sunday DATE = DATEADD(DAY, 6, @Monday),
      @ColumnsDone INT = 0;

    -- Check if the data has already been aggregated
    SELECT @ColumnsDone=COUNT([date])
    FROM [profiling].[UsageWeekly]
    WHERE [date] = @Monday;

    -- If the data has not been aggregated, do it
    IF @ColumnsDone = 0
    BEGIN
      CREATE TABLE #UsageStaging (
        [user_id] [int] NOT NULL,
        [date] [datetime] NOT NULL,
        -- Teams devices
        [Teams Used Web] [int] NOT NULL DEFAULT 0,
        [Teams Used Mac] [int] NOT NULL DEFAULT 0,
        [Teams Used Windows] [int] NOT NULL DEFAULT 0,
        [Teams Used Linux] [int] NOT NULL DEFAULT 0,
        [Teams Used Chrome OS] [int] NOT NULL DEFAULT 0,
        [Teams Used Mobile] [int] NOT NULL DEFAULT 0,
        [Teams Used WinPhone] [int] NOT NULL DEFAULT 0,
        [Teams Used iOS] [int] NOT NULL DEFAULT 0,
        [Teams Used Android] [int] NOT NULL DEFAULT 0,
        -- M365 app
        [Office Windows] [int] NOT NULL DEFAULT 0,
        [Office Mac] [int] NOT NULL DEFAULT 0,
        [Office Mobile] [int] NOT NULL DEFAULT 0,
        [Office Web] [int] NOT NULL DEFAULT 0,
        [Office Outlook] [int] NOT NULL DEFAULT 0,
        [Office Word] [int] NOT NULL DEFAULT 0,
        [Office Excel] [int] NOT NULL DEFAULT 0,
        [Office Powerpoint] [int] NOT NULL DEFAULT 0,
        [Office Onenote] [int] NOT NULL DEFAULT 0,
        [Office Teams] [int] NOT NULL DEFAULT 0,
        [Office Outlook Windows] [int] NOT NULL DEFAULT 0,
        [Office Word Windows] [int] NOT NULL DEFAULT 0,
        [Office Excel Windows] [int] NOT NULL DEFAULT 0,
        [Office Powerpoint Windows] [int] NOT NULL DEFAULT 0,
        [Office Onenote Windows] [int] NOT NULL DEFAULT 0,
        [Office Teams Windows] [int] NOT NULL DEFAULT 0,
        [Office Outlook Mac] [int] NOT NULL DEFAULT 0,
        [Office Word Mac] [int] NOT NULL DEFAULT 0,
        [Office Excel Mac] [int] NOT NULL DEFAULT 0,
        [Office Powerpoint Mac] [int] NOT NULL DEFAULT 0,
        [Office Onenote Mac] [int] NOT NULL DEFAULT 0,
        [Office Teams Mac] [int] NOT NULL DEFAULT 0,
        [Office Outlook Mobile] [int] NOT NULL DEFAULT 0,
        [Office Word Mobile] [int] NOT NULL DEFAULT 0,
        [Office Excel Mobile] [int] NOT NULL DEFAULT 0,
        [Office Powerpoint Mobile] [int] NOT NULL DEFAULT 0,
        [Office Onenote Mobile] [int] NOT NULL DEFAULT 0,
        [Office Teams Mobile] [int] NOT NULL DEFAULT 0,
        [Office Outlook Web] [int] NOT NULL DEFAULT 0,
        [Office Word Web] [int] NOT NULL DEFAULT 0,
        [Office Excel Web] [int] NOT NULL DEFAULT 0,
        [Office Powerpoint Web] [int] NOT NULL DEFAULT 0,
        [Office Onenote Web] [int] NOT NULL DEFAULT 0,
        [Office Teams Web] [int] NOT NULL DEFAULT 0,
        -- Yammer devices
        [Yammer Platform Count] [int] NOT NULL DEFAULT 0,
        [Yammer Used Web] [int] NOT NULL DEFAULT 0,
        [Yammer Used Mobile] [int] NOT NULL DEFAULT 0,
        [Yammer Used Others] [int] NOT NULL DEFAULT 0,
        [Yammer Used WinPhone] [bit] NOT NULL DEFAULT 0,
        [Yammer Used Android] [bit] NOT NULL DEFAULT 0,
        [Yammer Used iPad] [bit] NOT NULL DEFAULT 0,
        [Yammer Used iPhone] [bit] NOT NULL DEFAULT 0,

        -- Add COPILOT fields here

        -- TODO
        [Copilot ChatsCount] [bit] NOT NULL DEFAULT 0, 
        [Copilot MeetingsCount] [bit] NOT NULL DEFAULT 0,
        [Copilot FilesCount] [bit] NOT NULL DEFAULT 0



      );

      EXECUTE [profiling].[usp_UpsertTeamsDevices] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertM365Apps] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertYammerDevices] @Monday, @Sunday;

      INSERT INTO [profiling].[UsageWeekly] (
        user_id, date,
        [Teams Used Web], [Teams Used Mac], [Teams Used Windows], [Teams Used Linux], [Teams Used Chrome OS],
        [Teams Used Mobile], [Teams Used WinPhone], [Teams Used iOS], [Teams Used Android], 
        [Office Windows], [Office Mac], [Office Mobile], [Office Web],
        [Office Outlook], [Office Word], [Office Excel], [Office Powerpoint], [Office Onenote], [Office Teams],
        [Office Outlook Windows], [Office Word Windows], [Office Excel Windows], [Office Powerpoint Windows], [Office Onenote Windows], [Office Teams Windows],
        [Office Outlook Mac], [Office Word Mac], [Office Excel Mac], [Office Powerpoint Mac], [Office Onenote Mac], [Office Teams Mac],
        [Office Outlook Mobile], [Office Word Mobile], [Office Excel Mobile], [Office Powerpoint Mobile], [Office Onenote Mobile], [Office Teams Mobile],
        [Office Outlook Web], [Office Word Web], [Office Excel Web], [Office Powerpoint Web], [Office Onenote Web], [Office Teams Web],
        [Yammer Platform Count],
        [Yammer Used Web], [Yammer Used Mobile], [Yammer Used Others],
        [Yammer Used WinPhone], [Yammer Used Android], [Yammer Used iPad], [Yammer Used iPhone],

        -- Add Copilot fields here

        -- TODO
        [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]

      )
      SELECT
        user_id,
        @Monday as date,
        [Teams Used Web], [Teams Used Mac], [Teams Used Windows], [Teams Used Linux], [Teams Used Chrome OS],
        [Teams Used Mobile], [Teams Used WinPhone], [Teams Used iOS], [Teams Used Android], 
        [Office Windows], [Office Mac], [Office Mobile], [Office Web],
        [Office Outlook], [Office Word], [Office Excel], [Office Powerpoint], [Office Onenote], [Office Teams],
        [Office Outlook Windows], [Office Word Windows], [Office Excel Windows], [Office Powerpoint Windows], [Office Onenote Windows], [Office Teams Windows],
        [Office Outlook Mac], [Office Word Mac], [Office Excel Mac], [Office Powerpoint Mac], [Office Onenote Mac], [Office Teams Mac],
        [Office Outlook Mobile], [Office Word Mobile], [Office Excel Mobile], [Office Powerpoint Mobile], [Office Onenote Mobile], [Office Teams Mobile],
        [Office Outlook Web], [Office Word Web], [Office Excel Web], [Office Powerpoint Web], [Office Onenote Web], [Office Teams Web],
        [Yammer Platform Count],
        [Yammer Used Web], [Yammer Used Mobile], [Yammer Used Others],
        [Yammer Used WinPhone], [Yammer Used Android], [Yammer Used iPad], [Yammer Used iPhone]

        -- Add Copilot fields here
        -- TODO
        [Copilot ChatsCount], [Copilot MeetingsCount], [Copilot FilesCount]

      FROM #UsageStaging;

      DROP TABLE #UsageStaging;
    END
  END TRY
  BEGIN CATCH
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'Catch [usp_CompileUsageWeek]: ' + ERROR_MESSAGE();

    IF OBJECT_ID('tempdb..#UsageStaging') IS NOT NULL
      DROP TABLE #UsageStaging;
  END CATCH
END
GO

-- ===================================
-- Aggregates a week of analytics data
-- ===================================
IF OBJECT_ID(N'profiling.usp_CompileActivityWeek') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileActivityWeek];
GO

CREATE PROCEDURE [profiling].[usp_CompileActivityWeek] (
  @Monday DATE -- Start day of the week to aggregate
) AS
BEGIN
  -- SET NOCOUNT ON added to prevent extra result sets from interfering with SELECT statements.
  SET NOCOUNT ON;
  BEGIN TRY
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileActivityWeek] Starting: ' + CAST(@Monday AS NVARCHAR);
    DECLARE
      @Sunday DATE = DATEADD(DAY, 6, @Monday),
      @RowsDone INT = 0, -- Unpivoted data
      @ColumnsDone INT = 0

    -- Check if the data has already been aggregated
    SELECT @ColumnsDone=COUNT([date])
    FROM [profiling].[ActivitiesWeeklyColumns]
    WHERE [date] = @Monday

    SELECT @RowsDone=COUNT([MetricDate])
    FROM [profiling].[ActivitiesWeekly]
    WHERE [MetricDate] = @Monday

    -- If the data has not been aggregated, do it
    IF @RowsDone = 0 OR @ColumnsDone = 0
    BEGIN
      CREATE TABLE #ActivitiesStaging (
        [user_id] BIGINT,
        [date] DATE,

        [OneDrive Viewed/Edited] BIGINT DEFAULT 0,
        [OneDrive Synced] BIGINT DEFAULT 0,
        [OneDrive Shared Internally] BIGINT DEFAULT 0,
        [OneDrive Shared Externally] BIGINT DEFAULT 0,

        [SPO Viewed/Edited] BIGINT DEFAULT 0,
        [SPO Synced] BIGINT DEFAULT 0,
        [SPO Shared Internally] BIGINT DEFAULT 0,
        [SPO Shared Externally] BIGINT DEFAULT 0,

        [Emails Sent] BIGINT DEFAULT 0,
        [Emails Received] BIGINT DEFAULT 0,
        [Emails Read] BIGINT DEFAULT 0,
        [Outlook Meetings Created] BIGINT DEFAULT 0,
        [Outlook Meetings Interacted] BIGINT DEFAULT 0,

        [Teams Private Chats] BIGINT DEFAULT 0,
        [Teams Team Chats] BIGINT DEFAULT 0,
        [Teams Calls] BIGINT DEFAULT 0,
        [Teams Meetings] BIGINT DEFAULT 0,
        [Teams Meetings Attended] BIGINT DEFAULT 0,
        [Teams Meetings Organized] BIGINT DEFAULT 0,
        [Teams Adhoc Meetings Attended] BIGINT DEFAULT 0,
        [Teams Adhoc Meetings Organized] BIGINT DEFAULT 0,
        [Teams Scheduled Onetime Meetings Attended] BIGINT DEFAULT 0,
        [Teams Scheduled Onetime Meetings Organized] BIGINT DEFAULT 0,
        [Teams Scheduled Recurring Meetings Attended] BIGINT DEFAULT 0,
        [Teams Scheduled Recurring Meetings Organized] BIGINT DEFAULT 0,
        [Teams Audio Duration Seconds] BIGINT DEFAULT 0,
        [Teams Video Duration Seconds] BIGINT DEFAULT 0,
        [Teams Screenshare Duration Seconds] BIGINT DEFAULT 0,
        [Teams Post Messages] BIGINT DEFAULT 0,
        [Teams Reply Messages] BIGINT DEFAULT 0,
        [Teams Urgent Messages] BIGINT DEFAULT 0,

        [Yammer Posted] BIGINT DEFAULT 0,
        [Yammer Read] BIGINT DEFAULT 0,
        [Yammer Liked] BIGINT DEFAULT 0,


        -- Add Copilot fields here
        -- TODO
        [Copilot ChatsCount] BIGINT DEFAULT 0,
        [Copilot MeetingsCount] BIGINT DEFAULT 0,
        [Copilot FilesCount] BIGINT DEFAULT 0
      );

      -- Insert only the activities between the dates
      EXECUTE [profiling].[usp_UpsertTeams] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertOneDrive] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertSharePoint] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertOutlook] @Monday, @Sunday;
      EXECUTE [profiling].[usp_UpsertYammer] @Monday, @Sunday;
     
     -- ADD COPILOT upsertCopilot NEW
     -- TODO
      EXECUTE [profiling].[usp_UpsertCopilot] @Monday, @Sunday;



      IF @ColumnsDone = 0
        EXECUTE [profiling].[usp_CompileWeekActivityColumns] @Monday;
      IF @RowsDone = 0
        EXECUTE [profiling].[usp_CompileWeekActivityRows] @Monday;
      
      DROP TABLE #ActivitiesStaging;
    END
  END TRY
  BEGIN CATCH
    INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
    SELECT GETDATE(), N'[usp_CompileActivityWeek] Catch: ' + ERROR_MESSAGE();

    IF OBJECT_ID('tempdb..#ActivitiesStaging') IS NOT NULL
      DROP TABLE #ActivitiesStaging;
  END CATCH
END
GO

-- =============================================
-- Aggregates all possible weekly analytics data
-- =============================================
IF OBJECT_ID(N'profiling.usp_CompileWeekly') IS NOT NULL DROP PROCEDURE [profiling].[usp_CompileWeekly];
GO

CREATE PROCEDURE [profiling].[usp_CompileWeekly] (
  @WeeksToKeep INT,
  @All INT = 0
)
AS
BEGIN
  SET NOCOUNT ON;
  -- Today is the first day that there should be data, which usually is Today-4 days
  DECLARE @Today DATE = DATEADD(DAY, -4, GETDATE());
  -- Day when aggregation should stop
  DECLARE @ThisWeeksMonday DATE = [profiling].[udf_GetMonday](@Today);
  -- When data aggregation starts
  DECLARE @RetentionDate DATE = DATEADD(WEEK, -1 * @WeeksToKeep, @ThisWeeksMonday);
  -- Get last aggregated date in the table, it will be a Monday
  DECLARE
    @LastDateInTables DATE
    ,@MaxActitiesWeekly DATE
    ,@MaxActitiesWeeklyColumns DATE
    ,@MaxUsageWeekly DATE;
  SELECT @MaxActitiesWeekly = MAX(MetricDate) FROM [profiling].[ActivitiesWeekly];
  SELECT @MaxActitiesWeeklyColumns = MAX([date]) FROM [profiling].[ActivitiesWeeklyColumns];
  SELECT @MaxUsageWeekly = MAX([date]) FROM [profiling].[UsageWeekly];
  SELECT @LastDateInTables = MIN([date]) FROM (VALUES (@MaxActitiesWeekly), (@MaxActitiesWeeklyColumns), (@MaxUsageWeekly)) AS x([date])

  IF @LastDateInTables IS NULL OR @All = 1
      -- Start from the retention date
      SET @LastDateInTables = @RetentionDate;

  -- Week by week, aggregate the data
  DECLARE @Monday DATE = DATEADD(DAY, 7, @LastDateInTables);
  INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
  SELECT GETDATE(), N'Weekly aggregation requested, from ' + CAST(@Monday as NVARCHAR) + ' to possibly ' + CAST(@ThisWeeksMonday as NVARCHAR);
  WHILE @ThisWeeksMonday > @Monday
  BEGIN
      DECLARE @Sunday DATE = DATEADD(DAY, 6, @Monday);
      INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
      SELECT GETDATE(), N'Week from ' + CAST(@Monday AS NVARCHAR) + ' to ' + CAST(@Sunday AS NVARCHAR);
      EXECUTE [profiling].[usp_CompileActivityWeek] @Monday;
      EXECUTE [profiling].[usp_CompileUsageWeek] @Monday;
      SET @Monday = DATEADD(DAY, 7, @Monday);
  END

  -- Cleanup. Remove data in the tables before the retention date
  INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
  SELECT GETDATE(), N'Starting cleanup. Retention date: ' + CAST(@RetentionDate AS NVARCHAR);
  DELETE FROM [profiling].[ActivitiesWeekly] WHERE [MetricDate] < @RetentionDate;
  DELETE FROM [profiling].[ActivitiesWeeklyColumns] WHERE [date] < @RetentionDate;
  DELETE FROM [profiling].[UsageWeekly] WHERE [date] < @RetentionDate;
  INSERT INTO [profiling].[TraceLogs] ([Datetime], [Message])
  SELECT GETDATE(), N'Aggregation finished';
END
GO
