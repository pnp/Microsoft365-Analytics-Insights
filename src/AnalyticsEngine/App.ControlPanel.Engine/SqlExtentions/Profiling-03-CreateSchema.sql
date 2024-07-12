/* tsqllint-disable warning set-transaction-isolation-level */

SET ANSI_NULLS ON;
GO

SET QUOTED_IDENTIFIER ON;
GO

-- ============================================
-- =====                                  =====
-- =====   Create indexes in dbo tables   =====
-- =====                                  =====
-- ============================================

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.teams_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.teams_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.onedrive_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.onedrive_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.outlook_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.outlook_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.sharepoint_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.sharepoint_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.yammer_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.yammer_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_users_account_enabled' AND object_id = OBJECT_ID('dbo.users')
)
BEGIN
  CREATE NONCLUSTERED INDEX IX_users_account_enabled ON dbo.users (account_enabled)
  INCLUDE (azure_ad_id);
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.teams_user_device_usage_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.teams_user_device_usage_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.platform_user_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.platform_user_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_date' AND object_id = OBJECT_ID('dbo.yammer_device_activity_log')
)
BEGIN
  CREATE INDEX IX_date ON dbo.yammer_device_activity_log ("date");
END
GO

IF NOT EXISTS (
  SELECT name FROM sys.indexes
  WHERE name = N'IX_user_id' AND object_id = OBJECT_ID('dbo.user_license_type_lookups')
)
BEGIN
  CREATE NONCLUSTERED INDEX IX_user_id ON dbo.user_license_type_lookups ("user_id");
END
GO

-- =====================================
-- =====                           =====
-- =====          CLEANUP          =====
-- =====   Remove unused objects   =====
-- =====                           =====
-- =====================================

IF OBJECT_ID(N'usp_GetErrorInfo') IS NOT NULL
BEGIN
  DROP PROCEDURE usp_GetErrorInfo;
END

GO
IF OBJECT_ID(N'profiling.usp_CompileDaily') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileDaily;
END
GO

IF OBJECT_ID(N'profiling.usp_Version') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_Version;
END
GO

IF OBJECT_ID(N'profiling.udf_GetLimitDate') IS NOT NULL
BEGIN
  DROP FUNCTION profiling.udf_GetLimitDate;
END
GO

IF OBJECT_ID(N'profiling.tvf_ActivitiesBetweenDates') IS NOT NULL
BEGIN
  DROP FUNCTION profiling.tvf_ActivitiesBetweenDates;
END
GO

IF OBJECT_ID(N'profiling.tvf_DuplicatedUsers') IS NOT NULL
BEGIN
  DROP FUNCTION profiling.tvf_DuplicatedUsers;
END
GO

IF OBJECT_ID(N'profiling.tvf_Version') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.tvf_Version;
END
GO

IF OBJECT_ID(N'profiling.Activities') IS NOT NULL
BEGIN
  DROP TABLE profiling.Activities;
END
GO

IF OBJECT_ID(N'profiling.ActivitiesDaily') IS NOT NULL
BEGIN
  DROP TABLE profiling.ActivitiesDaily;
END
GO

IF OBJECT_ID(N'profiling.usp_CompileWeek') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeek;
END
GO

IF OBJECT_ID(N'profiling.usp_CompileWeekColumns') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeekColumns;
END
GO

IF OBJECT_ID(N'profiling.usp_CompileWeekRows') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeekRows;
END
GO

-- =================================
-- =====                       =====
-- =====   Create the schema   =====
-- =====                       =====
-- =================================

IF NOT EXISTS (
  SELECT name
  FROM sys.schemas
  WHERE name = N'profiling'
)
BEGIN
  EXEC ('CREATE SCHEMA profiling AUTHORIZATION dbo;');
END
GO

-- =====================
-- =====           =====
-- =====   VIEWS   =====
-- =====           =====
-- =====================

-- =============================================
-- View to get users worth having in the reports
-- =============================================

IF OBJECT_ID(N'profiling.users') IS NOT NULL
BEGIN
  DROP VIEW profiling.users;
END
GO

CREATE VIEW profiling.users
AS
  WITH
  cte_license_count AS (
    SELECT
      "user_id",
      COUNT(id) AS license_count
    FROM dbo.user_license_type_lookups
    GROUP BY "user_id"
  ),
  cte_enabled_users AS (
    SELECT id
    FROM dbo.users
    WHERE account_enabled = 1 AND azure_ad_id IS NOT NULL AND azure_ad_id <> ''
  )
  SELECT
    eu.id,
    user_name,
    azure_ad_id,
    mail,
    office_location_id,
    usage_location_id,
    department_id,
    job_title_id,
    postalcode,
    company_name_id,
    state_or_province_id,
    manager_id,
    country_or_region_id,
    license_count
  FROM
    cte_license_count AS l
    INNER JOIN cte_enabled_users AS eu ON eu.id = l."user_id"
    INNER JOIN dbo.users AS u ON u.id = eu.id;
GO

-- ===============================
-- View to get unique postal codes
-- ===============================

IF OBJECT_ID('profiling.user_PostalCodes') IS NOT NULL
BEGIN
  DROP VIEW profiling.user_PostalCodes;
END
GO

CREATE VIEW profiling.user_PostalCodes
AS
  SELECT DISTINCT postalcode
  FROM profiling.users;
GO

-- ======================
-- =====            =====
-- =====   TABLES   =====
-- =====            =====
-- ======================

IF OBJECT_ID(N'profiling.TraceLogs') IS NULL
BEGIN
  CREATE TABLE profiling.TraceLogs
  (
    Id BIGINT NOT NULL IDENTITY(1, 1) PRIMARY KEY,
    "Datetime" DATETIME NOT NULL,
    Message NVARCHAR(500) NOT NULL
  );
END
GO

-- =============================================
-- Aggregates all possible weekly analytics data
-- =============================================

IF OBJECT_ID(N'profiling.usp_Trace') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_Trace;
END
GO

CREATE PROCEDURE profiling.usp_Trace
(
  @Message NVARCHAR(500),
  @p1 NVARCHAR(50) = NULL,
  @p2 NVARCHAR(50) = NULL,
  @p3 NVARCHAR(50) = NULL,
  @p4 NVARCHAR(50) = NULL,
  @p5 NVARCHAR(50) = NULL
)
AS
BEGIN
  DECLARE @FormattedMessage NVARCHAR(MAX);
  SELECT @FormattedMessage = FORMATMESSAGE(@Message, CAST(@p1 AS NVARCHAR(50)), CAST(@p2 AS NVARCHAR(50)), CAST(@p3 AS NVARCHAR(50)), CAST(@p4 AS NVARCHAR(50)), CAST(@p5 AS NVARCHAR(50)));

  INSERT INTO profiling.TraceLogs ("Datetime", Message)
  SELECT GETDATE(), @FormattedMessage;
END;
GO

-- ======================================================
-- Weekly aggregated activity data per user. Data in rows
-- ======================================================

IF OBJECT_ID(N'profiling.ActivitiesWeekly') IS NULL
BEGIN
  CREATE TABLE profiling.ActivitiesWeekly
  (
    "user_id" BIGINT NOT NULL,
    MetricDate DATE NOT NULL,
    Metric VARCHAR(250) NOT NULL,
    Sum INT NOT NULL,
    CONSTRAINT PK_ActivitiesWeekly
      PRIMARY KEY CLUSTERED ("user_id" ASC, MetricDate ASC, Metric ASC)
      WITH (
        STATISTICS_NORECOMPUTE = OFF
        ,IGNORE_DUP_KEY = OFF
        -- ,OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
      )
  );
END;
GO

IF NOT EXISTS (
  SELECT name
  FROM sys.indexes
  WHERE object_id = OBJECT_ID('profiling.ActivitiesWeekly')
    AND name = N'IX_MetricDate'
)
BEGIN
  CREATE INDEX IX_MetricDate ON profiling.ActivitiesWeekly (MetricDate);
END
GO

-- =========================================================
-- Weekly aggregated activity data per user. Data in columns
-- =========================================================

IF OBJECT_ID(N'profiling.ActivitiesWeeklyColumns') IS NULL
BEGIN
  CREATE TABLE profiling.ActivitiesWeeklyColumns
  (
    "user_id" BIGINT,
    "date" DATE,
    "OneDrive Viewed/Edited" BIGINT NOT NULL DEFAULT 0,
    "OneDrive Synced" BIGINT NOT NULL DEFAULT 0,
    "OneDrive Shared Internally" BIGINT NOT NULL DEFAULT 0,
    "OneDrive Shared Externally" BIGINT NOT NULL DEFAULT 0,
    "Emails Sent" BIGINT NOT NULL DEFAULT 0,
    "Emails Received" BIGINT NOT NULL DEFAULT 0,
    "Emails Read" BIGINT NOT NULL DEFAULT 0,
    "Outlook Meetings Created" BIGINT NOT NULL DEFAULT 0,
    "Outlook Meetings Interacted" BIGINT NOT NULL DEFAULT 0,
    "SPO Viewed/Edited" BIGINT NOT NULL DEFAULT 0,
    "SPO Synced" BIGINT NOT NULL DEFAULT 0,
    "SPO Shared Internally" BIGINT NOT NULL DEFAULT 0,
    "SPO Shared Externally" BIGINT NOT NULL DEFAULT 0,
    "Teams Private Chats" BIGINT NOT NULL DEFAULT 0,
    "Teams Team Chats" BIGINT NOT NULL DEFAULT 0,
    "Teams Calls" BIGINT NOT NULL DEFAULT 0,
    "Teams Meetings" BIGINT NOT NULL DEFAULT 0,
    "Teams Meetings Attended" BIGINT NOT NULL DEFAULT 0,
    "Teams Meetings Organized" BIGINT NOT NULL DEFAULT 0,
    "Yammer Posted" BIGINT NOT NULL DEFAULT 0,
    "Yammer Read" BIGINT NOT NULL DEFAULT 0,
    "Yammer Liked" BIGINT NOT NULL DEFAULT 0,
    "Copilot Chats" BIGINT NOT NULL DEFAULT 0,
    "Copilot Meetings" BIGINT NOT NULL DEFAULT 0,
    "Copilot Files" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Assist365" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Bing" BIGINT NOT NULL DEFAULT 0,
    "Copilot App BashTool" BIGINT NOT NULL DEFAULT 0,
    "Copilot App DevUI" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Excel" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Loop" BIGINT NOT NULL DEFAULT 0,
    "Copilot App M365AdminCenter" BIGINT NOT NULL DEFAULT 0,
    "Copilot App M365App" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Office" BIGINT NOT NULL DEFAULT 0,
    "Copilot App OneNote" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Outlook" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Planner" BIGINT NOT NULL DEFAULT 0,
    "Copilot App PowerPoint" BIGINT NOT NULL DEFAULT 0,
    "Copilot App SharePoint" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Stream" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Teams" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaCopilot" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaEngage" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaGoals" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Whiteboard" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Word" BIGINT NOT NULL DEFAULT 0,

    CONSTRAINT PK_ActivitiesWeeklyColumns
    PRIMARY KEY CLUSTERED ("user_id" ASC, "date" ASC)
    WITH (
        STATISTICS_NORECOMPUTE = OFF
        ,IGNORE_DUP_KEY = OFF
        -- ,OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
      )
  );
END
GO

-- Add possibly missing default contraints
IF NOT EXISTS (
  SELECT OBJECT_NAME(OBJECT_ID)
  FROM sys.objects
  WHERE
    type_desc LIKE 'DEFAULT_CONSTRAINT'
    AND SCHEMA_NAME(SCHEMA_ID) = 'profiling'
    AND OBJECT_NAME(parent_object_id) = 'ActivitiesWeeklyColumns'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns
  ADD
    CONSTRAINT df_onedrive_viewed_edited DEFAULT 0 FOR "OneDrive Viewed/Edited",
    CONSTRAINT df_onedrive_synced DEFAULT 0 FOR "OneDrive Synced",
    CONSTRAINT df_onedrive_shared_internally DEFAULT 0 FOR "OneDrive Shared Internally",
    CONSTRAINT df_onedrive_shared_externally DEFAULT 0 FOR "OneDrive Shared Externally",
    CONSTRAINT df_emails_sent DEFAULT 0 FOR "Emails Sent",
    CONSTRAINT df_emails_received DEFAULT 0 FOR "Emails Received",
    CONSTRAINT df_emails_read DEFAULT 0 FOR "Emails Read",
    CONSTRAINT df_outlook_meetings_created DEFAULT 0 FOR "Outlook Meetings Created",
    CONSTRAINT df_outlook_meetings_interacted DEFAULT 0 FOR "Outlook Meetings Interacted",
    CONSTRAINT df_spo_viewed_edited DEFAULT 0 FOR "SPO Viewed/Edited",
    CONSTRAINT df_spo_synced DEFAULT 0 FOR "SPO Synced",
    CONSTRAINT df_spo_shared_internally DEFAULT 0 FOR "SPO Shared Internally",
    CONSTRAINT df_spo_shared_externally DEFAULT 0 FOR "SPO Shared Externally",
    CONSTRAINT df_teams_private_chats DEFAULT 0 FOR "Teams Private Chats",
    CONSTRAINT df_teams_team_chats DEFAULT 0 FOR "Teams Team Chats",
    CONSTRAINT df_teams_calls DEFAULT 0 FOR "Teams Calls",
    CONSTRAINT df_teams_meetings DEFAULT 0 FOR "Teams Meetings",
    CONSTRAINT df_teams_meetings_attended DEFAULT 0 FOR "Teams Meetings Attended",
    CONSTRAINT df_teams_meetings_organized DEFAULT 0 FOR "Teams Meetings Organized",
    CONSTRAINT df_yammer_posted DEFAULT 0 FOR "Yammer Posted",
    CONSTRAINT df_yammer_read DEFAULT 0 FOR "Yammer Read",
    CONSTRAINT df_yammer_liked DEFAULT 0 FOR "Yammer Liked";
END
GO

-- Add new Teams columns
IF NOT EXISTS (
  SELECT 1
  FROM sys.columns
  WHERE object_id = OBJECT_ID('profiling.ActivitiesWeeklyColumns')
    AND name = 'Teams Adhoc Meetings Attended'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns
  ADD
    "Teams Adhoc Meetings Attended" BIGINT NOT NULL DEFAULT 0,
    "Teams Adhoc Meetings Organized" BIGINT NOT NULL DEFAULT 0,
    "Teams Scheduled Onetime Meetings Attended" BIGINT NOT NULL DEFAULT 0,
    "Teams Scheduled Onetime Meetings Organized" BIGINT NOT NULL DEFAULT 0,
    "Teams Scheduled Recurring Meetings Attended" BIGINT NOT NULL DEFAULT 0,
    "Teams Scheduled Recurring Meetings Organized" BIGINT NOT NULL DEFAULT 0,
    "Teams Audio Duration Seconds" BIGINT NOT NULL DEFAULT 0,
    "Teams Video Duration Seconds" BIGINT NOT NULL DEFAULT 0,
    "Teams Screenshare Duration Seconds" BIGINT NOT NULL DEFAULT 0,
    "Teams Post Messages" BIGINT NOT NULL DEFAULT 0,
    "Teams Reply Messages" BIGINT NOT NULL DEFAULT 0,
    "Teams Urgent Messages" BIGINT NOT NULL DEFAULT 0;
END
GO

-- Add new Copilot columns
IF NOT EXISTS (
  SELECT 1
  FROM sys.columns
  WHERE object_id = OBJECT_ID('profiling.ActivitiesWeeklyColumns')
    AND name = 'Copilot Chats'
)
BEGIN
  ALTER TABLE
    profiling.ActivitiesWeeklyColumns
  ADD
    "Copilot Chats" BIGINT NOT NULL DEFAULT 0,
    "Copilot Meetings" BIGINT NOT NULL DEFAULT 0,
    "Copilot Files" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Assist365" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Bing" BIGINT NOT NULL DEFAULT 0,
    "Copilot App BashTool" BIGINT NOT NULL DEFAULT 0,
    "Copilot App DevUI" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Excel" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Loop" BIGINT NOT NULL DEFAULT 0,
    "Copilot App M365AdminCenter" BIGINT NOT NULL DEFAULT 0,
    "Copilot App M365App" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Office" BIGINT NOT NULL DEFAULT 0,
    "Copilot App OneNote" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Outlook" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Planner" BIGINT NOT NULL DEFAULT 0,
    "Copilot App PowerPoint" BIGINT NOT NULL DEFAULT 0,
    "Copilot App SharePoint" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Stream" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Teams" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaCopilot" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaEngage" BIGINT NOT NULL DEFAULT 0,
    "Copilot App VivaGoals" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Whiteboard" BIGINT NOT NULL DEFAULT 0,
    "Copilot App Word" BIGINT NOT NULL DEFAULT 0;
END
GO

IF NOT EXISTS (
  SELECT name
  FROM sys.indexes
  WHERE object_id = OBJECT_ID('profiling.ActivitiesWeeklyColumns')
    AND name = N'IX_date'
)
BEGIN
  CREATE INDEX IX_date ON profiling.ActivitiesWeeklyColumns ("date");
END
GO

-- Remove Yammer device columns. First constraints, then the columns
IF EXISTS (
  SELECT 1
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Platform Count'
)
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name = obj.name
  FROM sys.objects AS obj
  JOIN sys.columns AS cols ON obj.object_id = cols.default_object_id
  WHERE cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND cols.name = 'Yammer Platform Count';
  EXEC ('ALTER TABLE profiling.ActivitiesWeeklyColumns DROP CONSTRAINT ' + @name);
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Platform Count'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns DROP COLUMN "Yammer Platform Count";
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Web'
)
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name = obj.name
  FROM sys.objects AS obj
    JOIN sys.columns AS cols ON obj.object_id = cols.default_object_id
  WHERE cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND cols.name = 'Yammer Used Web';
  EXEC ('ALTER TABLE profiling.ActivitiesWeeklyColumns DROP CONSTRAINT ' + @name);
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Web'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns DROP COLUMN "Yammer Used Web";
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Mobile'
)
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name = obj.name
  FROM sys.objects AS obj
    JOIN sys.columns AS cols ON obj.object_id = cols.default_object_id
  WHERE cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND cols.name = 'Yammer Used Mobile';
  EXEC ('ALTER TABLE profiling.ActivitiesWeeklyColumns DROP CONSTRAINT ' + @name);
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Mobile'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns DROP COLUMN "Yammer Used Mobile";
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Others'
)
BEGIN
  DECLARE @name NVARCHAR(200);
  SELECT @name = obj.name
  FROM sys.objects AS obj
    JOIN sys.columns AS cols ON obj.object_id = cols.default_object_id
  WHERE cols.object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND cols.name = 'Yammer Used Others';
  EXEC ('ALTER TABLE profiling.ActivitiesWeeklyColumns DROP CONSTRAINT ' + @name);
END
GO

IF EXISTS (
  SELECT object_id
  FROM sys.columns
  WHERE object_id = OBJECT_ID(N'profiling.ActivitiesWeeklyColumns')
    AND name = 'Yammer Used Others'
)
BEGIN
  ALTER TABLE profiling.ActivitiesWeeklyColumns DROP COLUMN "Yammer Used Others";
END
GO

-- ======================================================
-- Weekly aggregated usage data per user. Data in columns
-- ======================================================
IF OBJECT_ID(N'profiling.UsageWeekly') IS NULL
BEGIN
  CREATE TABLE profiling.UsageWeekly
  (
    "user_id" INT NOT NULL,
    "date" DATE NOT NULL,
    "Teams Used Web" BIT NOT NULL DEFAULT 0,
    "Teams Used Mac" BIT NOT NULL DEFAULT 0,
    "Teams Used Windows" BIT NOT NULL DEFAULT 0,
    "Teams Used Linux" BIT NOT NULL DEFAULT 0,
    "Teams Used Chrome OS" BIT NOT NULL DEFAULT 0,
    "Teams Used Mobile" BIT NOT NULL DEFAULT 0,
    "Teams Used WinPhone" BIT NOT NULL DEFAULT 0,
    "Teams Used iOS" BIT NOT NULL DEFAULT 0,
    "Teams Used Android" BIT NOT NULL DEFAULT 0,
    "Office Windows" BIT NOT NULL DEFAULT 0,
    "Office Mac" BIT NOT NULL DEFAULT 0,
    "Office Mobile" BIT NOT NULL DEFAULT 0,
    "Office Web" BIT NOT NULL DEFAULT 0,
    "Office Outlook" BIT NOT NULL DEFAULT 0,
    "Office Word" BIT NOT NULL DEFAULT 0,
    "Office Excel" BIT NOT NULL DEFAULT 0,
    "Office Powerpoint" BIT NOT NULL DEFAULT 0,
    "Office Onenote" BIT NOT NULL DEFAULT 0,
    "Office Teams" BIT NOT NULL DEFAULT 0,
    "Office Outlook Windows" BIT NOT NULL DEFAULT 0,
    "Office Word Windows" BIT NOT NULL DEFAULT 0,
    "Office Excel Windows" BIT NOT NULL DEFAULT 0,
    "Office Powerpoint Windows" BIT NOT NULL DEFAULT 0,
    "Office Onenote Windows" BIT NOT NULL DEFAULT 0,
    "Office Teams Windows" BIT NOT NULL DEFAULT 0,
    "Office Outlook Mac" BIT NOT NULL DEFAULT 0,
    "Office Word Mac" BIT NOT NULL DEFAULT 0,
    "Office Excel Mac" BIT NOT NULL DEFAULT 0,
    "Office Powerpoint Mac" BIT NOT NULL DEFAULT 0,
    "Office Onenote Mac" BIT NOT NULL DEFAULT 0,
    "Office Teams Mac" BIT NOT NULL DEFAULT 0,
    "Office Outlook Mobile" BIT NOT NULL DEFAULT 0,
    "Office Word Mobile" BIT NOT NULL DEFAULT 0,
    "Office Excel Mobile" BIT NOT NULL DEFAULT 0,
    "Office Powerpoint Mobile" BIT NOT NULL DEFAULT 0,
    "Office Onenote Mobile" BIT NOT NULL DEFAULT 0,
    "Office Teams Mobile" BIT NOT NULL DEFAULT 0,
    "Office Outlook Web" BIT NOT NULL DEFAULT 0,
    "Office Word Web" BIT NOT NULL DEFAULT 0,
    "Office Excel Web" BIT NOT NULL DEFAULT 0,
    "Office Powerpoint Web" BIT NOT NULL DEFAULT 0,
    "Office Onenote Web" BIT NOT NULL DEFAULT 0,
    "Office Teams Web" BIT NOT NULL DEFAULT 0,
    "Yammer Platform Count" TINYINT NOT NULL DEFAULT 0,
    "Yammer Used Web" BIT NOT NULL DEFAULT 0,
    "Yammer Used Mobile" BIT NOT NULL DEFAULT 0,
    "Yammer Used Others" BIT NOT NULL DEFAULT 0,
    "Yammer Used WinPhone" BIT NOT NULL DEFAULT 0,
    "Yammer Used Android" BIT NOT NULL DEFAULT 0,
    "Yammer Used iPad" BIT NOT NULL DEFAULT 0,
    "Yammer Used iPhone" BIT NOT NULL DEFAULT 0,
    CONSTRAINT [PK_profiling.UsageWeekly]
    PRIMARY KEY CLUSTERED ("user_id", "date" ASC)
    WITH (
        STATISTICS_NORECOMPUTE = OFF
        ,IGNORE_DUP_KEY = OFF
        -- ,OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF
      )
  );
END
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
BEGIN
  DROP FUNCTION profiling.udf_GetMonday;
END
GO

CREATE FUNCTION profiling.udf_GetMonday (@CurrentDate DATE)
RETURNS DATE
AS
BEGIN
  DECLARE
    @Result DATE,
    @ThisDayWasMonday INT;

  -- Set the first day of the week to Monday
  SELECT
    @ThisDayWasMonday = DATEPART(WEEKDAY, '20230102'),
    @Result = @CurrentDate;
  WHILE @ThisDayWasMonday <> DATEPART(WEEKDAY, @Result)
  BEGIN
    SELECT @Result = CONVERT(DATE, DATEADD(DAY, -1, @Result));
  END
  RETURN @Result;
END;
GO

-- ============================================================
-- =====                                                  =====
-- =====      Aggregation Table Types and procedures      =====
-- =====   (They have a specific order to be recreated)   =====
-- =====                                                  =====
-- ============================================================

-- First drop the SPs, then the Table Types
IF OBJECT_ID(N'profiling.usp_UpsertTeams') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertTeams;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertOneDrive') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertOneDrive;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertSharePoint') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertSharePoint;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertOutlook') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertOutlook;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertYammer') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertYammer;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertTeamsDevices') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertTeamsDevices;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertM365Apps') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertM365Apps;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertYammerDevices') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertYammerDevices;
END
GO

IF OBJECT_ID(N'profiling.usp_UpsertCopilot') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_UpsertCopilot;
END
GO

IF TYPE_ID(N'ut_teams_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_teams_user_activity_log;
END
GO

IF TYPE_ID(N'ut_onedrive_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_onedrive_user_activity_log;
END
GO

IF TYPE_ID(N'ut_sharepoint_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_sharepoint_user_activity_log;
END
GO

IF TYPE_ID(N'ut_outlook_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_outlook_user_activity_log;
END
GO

IF TYPE_ID(N'ut_yammer_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_yammer_user_activity_log;
END
GO

IF TYPE_ID(N'ut_teams_user_device_usage_log') IS NOT NULL
BEGIN
  DROP TYPE ut_teams_user_device_usage_log;
END
GO

IF TYPE_ID(N'ut_platform_user_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_platform_user_activity_log;
END
GO

IF TYPE_ID(N'ut_yammer_device_activity_log') IS NOT NULL
BEGIN
  DROP TYPE ut_yammer_device_activity_log;
END
GO

IF TYPE_ID(N'ut_copilot_activities') IS NOT NULL
BEGIN
  DROP TYPE ut_copilot_activities;
END
GO

-- Add Table Types

IF TYPE_ID(N'ut_teams_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_teams_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    private_chat_count BIGINT NOT NULL,
    team_chat_count BIGINT NOT NULL,
    calls_count BIGINT NOT NULL,
    meetings_count BIGINT NOT NULL,
    meetings_attended_count BIGINT NOT NULL,
    meetings_organized_count BIGINT NOT NULL,
    adhoc_meetings_attended_count BIGINT NOT NULL,
    adhoc_meetings_organized_count BIGINT NOT NULL,
    scheduled_onetime_meetings_attended_count BIGINT NOT NULL,
    scheduled_onetime_meetings_organized_count BIGINT NOT NULL,
    scheduled_recurring_meetings_attended_count BIGINT NOT NULL,
    scheduled_recurring_meetings_organized_count BIGINT NOT NULL,
    audio_duration_seconds INT NOT NULL,
    video_duration_seconds INT NOT NULL,
    screenshare_duration_seconds INT NOT NULL,
    post_messages BIGINT NOT NULL,
    reply_messages BIGINT NOT NULL,
    urgent_messages BIGINT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_onedrive_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_onedrive_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    viewed_or_edited BIGINT NOT NULL,
    synced BIGINT NOT NULL,
    shared_internally BIGINT NOT NULL,
    shared_externally BIGINT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_sharepoint_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_sharepoint_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    viewed_or_edited BIGINT NOT NULL,
    synced BIGINT NOT NULL,
    shared_internally BIGINT NOT NULL,
    shared_externally BIGINT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_outlook_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_outlook_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    email_send_count BIGINT NOT NULL,
    email_receive_count BIGINT NOT NULL,
    email_read_count BIGINT NOT NULL,
    meeting_created_count BIGINT NOT NULL,
    meeting_interacted_count BIGINT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_yammer_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_yammer_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    posted_count INT NOT NULL,
    read_count INT NOT NULL,
    liked_count INT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_teams_user_device_usage_log') IS NULL
BEGIN
  CREATE TYPE ut_teams_user_device_usage_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    used_web INT NOT NULL DEFAULT 0,
    used_mac INT NOT NULL DEFAULT 0,
    used_windows INT NOT NULL DEFAULT 0,
    used_linux INT NOT NULL DEFAULT 0,
    used_chrome_os INT NOT NULL DEFAULT 0,
    used_mobile INT NOT NULL DEFAULT 0,
    used_win_phone INT NOT NULL DEFAULT 0,
    used_ios INT NOT NULL DEFAULT 0,
    used_android INT NOT NULL DEFAULT 0
    );
END
GO

IF TYPE_ID(N'ut_platform_user_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_platform_user_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    windows INT NOT NULL DEFAULT 0,
    mac INT NOT NULL DEFAULT 0,
    mobile INT NOT NULL DEFAULT 0,
    web INT NOT NULL DEFAULT 0,
    outlook INT NOT NULL DEFAULT 0,
    word INT NOT NULL DEFAULT 0,
    excel INT NOT NULL DEFAULT 0,
    powerpoint INT NOT NULL DEFAULT 0,
    onenote INT NOT NULL DEFAULT 0,
    teams INT NOT NULL DEFAULT 0,
    outlook_windows INT NOT NULL DEFAULT 0,
    word_windows INT NOT NULL DEFAULT 0,
    excel_windows INT NOT NULL DEFAULT 0,
    powerpoint_windows INT NOT NULL DEFAULT 0,
    onenote_windows INT NOT NULL DEFAULT 0,
    teams_windows INT NOT NULL DEFAULT 0,
    outlook_mac INT NOT NULL DEFAULT 0,
    word_mac INT NOT NULL DEFAULT 0,
    excel_mac INT NOT NULL DEFAULT 0,
    powerpoint_mac INT NOT NULL DEFAULT 0,
    onenote_mac INT NOT NULL DEFAULT 0,
    teams_mac INT NOT NULL DEFAULT 0,
    outlook_mobile INT NOT NULL DEFAULT 0,
    word_mobile INT NOT NULL DEFAULT 0,
    excel_mobile INT NOT NULL DEFAULT 0,
    powerpoint_mobile INT NOT NULL DEFAULT 0,
    onenote_mobile INT NOT NULL DEFAULT 0,
    teams_mobile INT NOT NULL DEFAULT 0,
    outlook_web INT NOT NULL DEFAULT 0,
    word_web INT NOT NULL DEFAULT 0,
    excel_web INT NOT NULL DEFAULT 0,
    powerpoint_web INT NOT NULL DEFAULT 0,
    onenote_web INT NOT NULL DEFAULT 0,
    teams_web INT NOT NULL DEFAULT 0
    );
END
GO

IF TYPE_ID(N'ut_yammer_device_activity_log') IS NULL
BEGIN
  CREATE TYPE ut_yammer_device_activity_log AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    used_count INT NOT NULL,
    used_web INT NOT NULL,
    used_mobile INT NOT NULL,
    used_others INT NOT NULL,
    used_win_phone INT NOT NULL,
    used_android INT NOT NULL,
    used_ipad INT NOT NULL,
    used_iphone INT NOT NULL
    );
END
GO

IF TYPE_ID(N'ut_copilot_activities') IS NULL
BEGIN
  CREATE TYPE ut_copilot_activities AS TABLE (
    "user_id" INT NOT NULL,
    "date" DATETIME NOT NULL,
    copilot_chats BIGINT NOT NULL DEFAULT 0,
    copilot_meetings BIGINT NOT NULL DEFAULT 0,
    copilot_files BIGINT NOT NULL DEFAULT 0,
    copilot_assist365 BIGINT NOT NULL DEFAULT 0,
    copilot_bing BIGINT NOT NULL DEFAULT 0,
    copilot_bashtool BIGINT NOT NULL DEFAULT 0,
    copilot_devui BIGINT NOT NULL DEFAULT 0,
    copilot_excel BIGINT NOT NULL DEFAULT 0,
    copilot_loop BIGINT NOT NULL DEFAULT 0,
    copilot_m365admincenter BIGINT NOT NULL DEFAULT 0,
    copilot_m365app BIGINT NOT NULL DEFAULT 0,
    copilot_office BIGINT NOT NULL DEFAULT 0,
    copilot_onenote BIGINT NOT NULL DEFAULT 0,
    copilot_outlook BIGINT NOT NULL DEFAULT 0,
    copilot_planner BIGINT NOT NULL DEFAULT 0,
    copilot_powerpoint BIGINT NOT NULL DEFAULT 0,
    copilot_sharepoint BIGINT NOT NULL DEFAULT 0,
    copilot_stream BIGINT NOT NULL DEFAULT 0,
    copilot_teams BIGINT NOT NULL DEFAULT 0,
    copilot_vivacopilot BIGINT NOT NULL DEFAULT 0,
    copilot_vivaengage BIGINT NOT NULL DEFAULT 0,
    copilot_vivagoals BIGINT NOT NULL DEFAULT 0,
    copilot_whiteboard BIGINT NOT NULL DEFAULT 0,
    copilot_word BIGINT NOT NULL DEFAULT 0
    );
END
GO

-- Add SPs

/*
Upsert procedures should get the raw data from the database and then process it
and push it to the staging temp table.
There are two staging tables, depending on the type of data:
- Activity count data (user sent X emails that day) goes into #ActivitiesStaging
- Boolean data (Teams mobile was used or not that day) goes into #UsageStaging
*/
CREATE PROCEDURE profiling.usp_UpsertTeams
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @teams AS ut_teams_user_activity_log;
  INSERT INTO @teams
  (
    "user_id",
    "date",
    private_chat_count,
    team_chat_count,
    calls_count,
    meetings_count,
    meetings_attended_count,
    meetings_organized_count,
    adhoc_meetings_attended_count,
    adhoc_meetings_organized_count,
    scheduled_onetime_meetings_attended_count,
    scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count,
    scheduled_recurring_meetings_organized_count,
    audio_duration_seconds,
    video_duration_seconds,
    screenshare_duration_seconds,
    post_messages,
    reply_messages,
    urgent_messages
  )
  SELECT
    "user_id",
    @StartDate,
    SUM(private_chat_count),
    SUM(team_chat_count),
    SUM(calls_count),
    SUM(meetings_count),
    SUM(meetings_attended_count),
    SUM(meetings_organized_count),
    SUM(adhoc_meetings_attended_count),
    SUM(adhoc_meetings_organized_count),
    SUM(scheduled_onetime_meetings_attended_count),
    SUM(scheduled_onetime_meetings_organized_count),
    SUM(scheduled_recurring_meetings_attended_count),
    SUM(scheduled_recurring_meetings_organized_count),
    SUM(audio_duration_seconds),
    SUM(video_duration_seconds),
    SUM(screenshare_duration_seconds),
    SUM(post_messages),
    SUM(reply_messages),
    SUM(urgent_messages)
  FROM dbo.teams_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Teams Private Chats" = tvp.private_chat_count,
    "Teams Team Chats" = tvp.team_chat_count,
    "Teams Calls" = tvp.calls_count,
    "Teams Meetings" = tvp.meetings_count,
    "Teams Meetings Attended" = tvp.meetings_attended_count,
    "Teams Meetings Organized" = tvp.meetings_organized_count,
    "Teams Adhoc Meetings Attended" = tvp.adhoc_meetings_attended_count,
    "Teams Adhoc Meetings Organized" = tvp.adhoc_meetings_organized_count,
    "Teams Scheduled Onetime Meetings Attended" = tvp.scheduled_onetime_meetings_attended_count,
    "Teams Scheduled Onetime Meetings Organized" = tvp.scheduled_onetime_meetings_organized_count,
    "Teams Scheduled Recurring Meetings Attended" = tvp.scheduled_recurring_meetings_attended_count,
    "Teams Scheduled Recurring Meetings Organized" = tvp.scheduled_recurring_meetings_organized_count,
    "Teams Audio Duration Seconds" = tvp.audio_duration_seconds,
    "Teams Video Duration Seconds" = tvp.video_duration_seconds,
    "Teams Screenshare Duration Seconds" = tvp.screenshare_duration_seconds,
    "Teams Post Messages" = tvp.post_messages,
    "Teams Reply Messages" = tvp.reply_messages,
    "Teams Urgent Messages" = tvp.urgent_messages
  FROM #ActivitiesStaging AS t
    INNER JOIN @teams AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "Teams Private Chats",
    "Teams Team Chats",
    "Teams Calls",
    "Teams Meetings",
    "Teams Meetings Attended",
    "Teams Meetings Organized",
    "Teams Adhoc Meetings Attended",
    "Teams Adhoc Meetings Organized",
    "Teams Scheduled Onetime Meetings Attended",
    "Teams Scheduled Onetime Meetings Organized",
    "Teams Scheduled Recurring Meetings Attended",
    "Teams Scheduled Recurring Meetings Organized",
    "Teams Audio Duration Seconds",
    "Teams Video Duration Seconds",
    "Teams Screenshare Duration Seconds",
    "Teams Post Messages",
    "Teams Reply Messages",
    "Teams Urgent Messages"
  )
  SELECT
    "user_id",
    "date",
    private_chat_count,
    team_chat_count,
    calls_count,
    meetings_count,
    meetings_attended_count,
    meetings_organized_count,
    adhoc_meetings_attended_count,
    adhoc_meetings_organized_count,
    scheduled_onetime_meetings_attended_count,
    scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count,
    scheduled_recurring_meetings_organized_count,
    audio_duration_seconds,
    video_duration_seconds,
    screenshare_duration_seconds,
    post_messages,
    reply_messages,
    urgent_messages
  FROM @teams AS tvp
  WHERE NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertOneDrive
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @onedrive AS ut_onedrive_user_activity_log;
  INSERT INTO @onedrive
  (
    "user_id",
    "date",
    viewed_or_edited,
    synced,
    shared_internally,
    shared_externally
  )
  SELECT
    "user_id",
    @StartDate,
    SUM(viewed_or_edited),
    SUM(synced),
    SUM(shared_internally),
    SUM(shared_externally)
  FROM dbo.onedrive_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "OneDrive Viewed/Edited" = tvp.viewed_or_edited,
    "OneDrive Synced" = tvp.synced,
    "OneDrive Shared Internally" = tvp.shared_internally,
    "OneDrive Shared Externally" = tvp.shared_externally
  FROM #ActivitiesStaging AS t
    INNER JOIN @onedrive AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "OneDrive Viewed/Edited",
    "OneDrive Synced",
    "OneDrive Shared Internally",
    "OneDrive Shared Externally"
  )
  SELECT
    "user_id",
    "date",
    viewed_or_edited,
    synced,
    shared_internally,
    shared_externally
  FROM @onedrive AS tvp
  WHERE NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertSharePoint
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @sharepoint AS ut_sharepoint_user_activity_log;
  INSERT INTO @sharepoint
  (
    "user_id",
    "date",
    viewed_or_edited,
    synced,
    shared_internally,
    shared_externally
  )
  SELECT
    "user_id",
    @StartDate,
    SUM(viewed_or_edited),
    SUM(synced),
    SUM(shared_internally),
    SUM(shared_externally)
  FROM dbo.sharepoint_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "SPO Viewed/Edited" = tvp.viewed_or_edited,
    "SPO Synced" = tvp.synced,
    "SPO Shared Internally" = tvp.shared_internally,
    "SPO Shared Externally" = tvp.shared_externally
  FROM #ActivitiesStaging AS t
    INNER JOIN @sharepoint AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "SPO Viewed/Edited",
    "SPO Synced",
    "SPO Shared Internally",
    "SPO Shared Externally"
  )
  SELECT
    "user_id",
    "date",
    viewed_or_edited,
    synced,
    shared_internally,
    shared_externally
  FROM @sharepoint AS tvp
  WHERE NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertOutlook
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @outlook AS ut_outlook_user_activity_log;
  INSERT INTO @outlook
  (
    "user_id",
    "date",
    email_send_count,
    email_receive_count,
    email_read_count,
    meeting_created_count,
    meeting_interacted_count
  )
  SELECT
    "user_id",
    @StartDate,
    SUM(email_send_count),
    SUM(email_receive_count),
    SUM(email_read_count),
    SUM(meeting_created_count),
    SUM(meeting_interacted_count)
  FROM dbo.outlook_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Emails Sent" = tvp.email_send_count,
    "Emails Received" = tvp.email_receive_count,
    "Emails Read" = tvp.email_read_count,
    "Outlook Meetings Created" = tvp.meeting_created_count,
    "Outlook Meetings Interacted" = tvp.meeting_interacted_count
  FROM #ActivitiesStaging AS t
    INNER JOIN @outlook AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "Emails Sent",
    "Emails Received",
    "Emails Read",
    "Outlook Meetings Created",
    "Outlook Meetings Interacted"
  )
  SELECT
    "user_id",
    "date",
    email_send_count,
    email_receive_count,
    email_read_count,
    meeting_created_count,
    meeting_interacted_count
  FROM @outlook AS tvp
  WHERE NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertYammer
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @yammer AS ut_yammer_user_activity_log;
  INSERT INTO @yammer
  (
    "user_id",
    "date",
    posted_count,
    read_count,
    liked_count
  )
  SELECT
    "user_id",
    @StartDate,
    SUM(posted_count),
    SUM(read_count),
    SUM(liked_count)
  FROM dbo.yammer_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Yammer Posted" = tvp.posted_count,
    "Yammer Read" = tvp.read_count,
    "Yammer Liked" = tvp.liked_count
  FROM #ActivitiesStaging AS t
    INNER JOIN @yammer AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "Yammer Posted",
    "Yammer Read",
    "Yammer Liked"
  )
  SELECT
    "user_id",
    "date",
    posted_count,
    read_count,
    liked_count
  FROM @yammer AS tvp
  WHERE
  NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertTeamsDevices
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_teams_user_device_usage_log;
  INSERT INTO @ut_staging
  (
    "user_id",
    "date",
    used_web,
    used_mac,
    used_windows,
    used_linux,
    used_chrome_os,
    used_mobile,
    used_win_phone,
    used_ios,
    used_android
  )
  SELECT
    "user_id",
    @StartDate,
    -- multiplication here converts the BIT column into an INT so the aggregation works
    MAX(1 * used_web),
    MAX(1 * used_mac),
    MAX(1 * used_windows),
    -- Use COALESCE here to fix #1
    MAX(1 * COALESCE(used_linux, 0)),
    MAX(1 * COALESCE(used_chrome_os, 0)),
    MAX(1 * used_win_phone + 1 * used_ios + 1 * used_android),
    -- used mobile
    MAX(1 * used_win_phone),
    MAX(1 * used_ios),
    MAX(1 * used_android)
  FROM dbo.teams_user_device_usage_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Teams Used Web" = staging.used_web,
    "Teams Used Mac" = staging.used_mac,
    "Teams Used Windows" = staging.used_windows,
    "Teams Used Linux" = staging.used_linux,
    "Teams Used Chrome OS" = staging.used_chrome_os,
    "Teams Used Mobile" = staging.used_mobile,
    "Teams Used WinPhone" = staging.used_win_phone,
    "Teams Used iOS" = staging.used_ios,
    "Teams Used Android" = staging.used_android
  FROM #UsageStaging AS t
    INNER JOIN @ut_staging AS staging
      ON t."user_id" = staging."user_id" AND t."date" = staging."date";
  /* tsqllint-enable warning update-where */

  INSERT #UsageStaging
  (
    "user_id",
    "date",
    "Teams Used Web",
    "Teams Used Mac",
    "Teams Used Windows",
    "Teams Used Linux",
    "Teams Used Chrome OS",
    "Teams Used Mobile",
    "Teams Used WinPhone",
    "Teams Used iOS",
    "Teams Used Android"
  )
  SELECT
    "user_id",
    "date",
    used_web,
    used_mac,
    used_windows,
    used_linux,
    used_chrome_os,
    used_mobile,
    used_win_phone,
    used_ios,
    used_android
  FROM @ut_staging AS staging
  WHERE NOT EXISTS (
    SELECT 1
    FROM #UsageStaging AS t
    WHERE t."user_id" = staging."user_id" AND t."date" = staging."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertM365Apps
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_platform_user_activity_log;
  INSERT INTO @ut_staging
  (
    "user_id",
    "date",
    windows,
    mac,
    mobile,
    web,
    outlook,
    word,
    excel,
    powerpoint,
    onenote,
    teams,
    outlook_windows,
    word_windows,
    excel_windows,
    powerpoint_windows,
    onenote_windows,
    teams_windows,
    outlook_mac,
    word_mac,
    excel_mac,
    powerpoint_mac,
    onenote_mac,
    teams_mac,
    outlook_mobile,
    word_mobile,
    excel_mobile,
    powerpoint_mobile,
    onenote_mobile,
    teams_mobile,
    outlook_web,
    word_web,
    excel_web,
    powerpoint_web,
    onenote_web,
    teams_web
  )
  SELECT
    "user_id",
    @StartDate,
    MAX(1 * windows),
    MAX(1 * mac),
    MAX(1 * mobile),
    MAX(1 * web),
    MAX(1 * outlook),
    MAX(1 * word),
    MAX(1 * excel),
    MAX(1 * powerpoint),
    MAX(1 * onenote),
    MAX(1 * teams),
    MAX(1 * outlook_windows),
    MAX(1 * word_windows),
    MAX(1 * excel_windows),
    MAX(1 * powerpoint_windows),
    MAX(1 * onenote_windows),
    MAX(1 * teams_windows),
    MAX(1 * outlook_mac),
    MAX(1 * word_mac),
    MAX(1 * excel_mac),
    MAX(1 * powerpoint_mac),
    MAX(1 * onenote_mac),
    MAX(1 * teams_mac),
    MAX(1 * outlook_mobile),
    MAX(1 * word_mobile),
    MAX(1 * excel_mobile),
    MAX(1 * powerpoint_mobile),
    MAX(1 * onenote_mobile),
    MAX(1 * teams_mobile),
    MAX(1 * outlook_web),
    MAX(1 * word_web),
    MAX(1 * excel_web),
    MAX(1 * powerpoint_web),
    MAX(1 * onenote_web),
    MAX(1 * teams_web)
  FROM dbo.platform_user_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Office Windows" = staging.windows,
    "Office Mac" = staging.mac,
    "Office Mobile" = staging.mobile,
    "Office Web" = staging.web,
    "Office Outlook" = staging.outlook,
    "Office Word" = staging.word,
    "Office Excel" = staging.excel,
    "Office Powerpoint" = staging.powerpoint,
    "Office Onenote" = staging.onenote,
    "Office Teams" = staging.teams,
    "Office Outlook Windows" = staging.outlook_windows,
    "Office Word Windows" = staging.word_windows,
    "Office Excel Windows" = staging.excel_windows,
    "Office Powerpoint Windows" = staging.powerpoint_windows,
    "Office Onenote Windows" = staging.onenote_windows,
    "Office Teams Windows" = staging.teams_windows,
    "Office Outlook Mac" = staging.outlook_mac,
    "Office Word Mac" = staging.word_mac,
    "Office Excel Mac" = staging.excel_mac,
    "Office Powerpoint Mac" = staging.powerpoint_mac,
    "Office Onenote Mac" = staging.onenote_mac,
    "Office Teams Mac" = staging.teams_mac,
    "Office Outlook Mobile" = staging.outlook_mobile,
    "Office Word Mobile" = staging.word_mobile,
    "Office Excel Mobile" = staging.excel_mobile,
    "Office Powerpoint Mobile" = staging.powerpoint_mobile,
    "Office Onenote Mobile" = staging.onenote_mobile,
    "Office Teams Mobile" = staging.teams_mobile,
    "Office Outlook Web" = staging.outlook_web,
    "Office Word Web" = staging.word_web,
    "Office Excel Web" = staging.excel_web,
    "Office Powerpoint Web" = staging.powerpoint_web,
    "Office Onenote Web" = staging.onenote_web,
    "Office Teams Web" = staging.teams_web
  FROM #UsageStaging AS t
    INNER JOIN @ut_staging AS staging ON t."user_id" = staging."user_id"
      AND t."date" = staging."date";
  /* tsqllint-enable warning update-where */

  INSERT #UsageStaging
  (
    "user_id",
    "date",
    "Office Windows",
    "Office Mac",
    "Office Mobile",
    "Office Web",
    "Office Outlook",
    "Office Word",
    "Office Excel",
    "Office Powerpoint",
    "Office Onenote",
    "Office Teams",
    "Office Outlook Windows",
    "Office Word Windows",
    "Office Excel Windows",
    "Office Powerpoint Windows",
    "Office Onenote Windows",
    "Office Teams Windows",
    "Office Outlook Mac",
    "Office Word Mac",
    "Office Excel Mac",
    "Office Powerpoint Mac",
    "Office Onenote Mac",
    "Office Teams Mac",
    "Office Outlook Mobile",
    "Office Word Mobile",
    "Office Excel Mobile",
    "Office Powerpoint Mobile",
    "Office Onenote Mobile",
    "Office Teams Mobile",
    "Office Outlook Web",
    "Office Word Web",
    "Office Excel Web",
    "Office Powerpoint Web",
    "Office Onenote Web",
    "Office Teams Web"
  )
  SELECT
    "user_id",
    "date",
    windows,
    mac,
    mobile,
    web,
    outlook,
    word,
    excel,
    powerpoint,
    onenote,
    teams,
    outlook_windows,
    word_windows,
    excel_windows,
    powerpoint_windows,
    onenote_windows,
    teams_windows,
    outlook_mac,
    word_mac,
    excel_mac,
    powerpoint_mac,
    onenote_mac,
    teams_mac,
    outlook_mobile,
    word_mobile,
    excel_mobile,
    powerpoint_mobile,
    onenote_mobile,
    teams_mobile,
    outlook_web,
    word_web,
    excel_web,
    powerpoint_web,
    onenote_web,
    teams_web
  FROM @ut_staging AS staging
  WHERE NOT EXISTS (
    SELECT 1
    FROM #UsageStaging AS t
    WHERE t."user_id" = staging."user_id" AND t."date" = staging."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertYammerDevices
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @ut_staging AS ut_yammer_device_activity_log;
  INSERT INTO @ut_staging
  (
    "user_id",
    "date",
    used_count,
    used_web,
    used_mobile,
    used_others,
    used_win_phone,
    used_android,
    used_ipad,
    used_iphone
  )
  SELECT
    "user_id",
    @StartDate,
    MAX(1 * used_web + 1 * used_others + 1 * used_win_phone + 1 * used_android
      + 1 * used_ipad + 1 * used_iphone + 1 * used_others),
    MAX(1 * used_web),
    MAX(1 * used_win_phone + 1 * used_android + 1 * used_ipad + 1 * used_iphone),
    MAX(1 * used_others),
    MAX(1 * used_win_phone),
    MAX(1 * used_android),
    MAX(1 * used_ipad),
    MAX(1 * used_iphone)
  FROM dbo.yammer_device_activity_log
  WHERE @StartDate <= "date" AND "date" <= @EndDate
  GROUP BY "user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Yammer Platform Count" = staging.used_count,
    "Yammer Used Web" = staging.used_web,
    "Yammer Used Mobile" = staging.used_mobile,
    "Yammer Used Others" = staging.used_others,
    "Yammer Used WinPhone" = staging.used_win_phone,
    "Yammer Used Android" = staging.used_android,
    "Yammer Used iPad" = staging.used_ipad,
    "Yammer Used iPhone" = staging.used_iphone
  FROM #UsageStaging AS t
    INNER JOIN @ut_staging AS staging
      ON t."user_id" = staging."user_id" AND t."date" = staging."date";
  /* tsqllint-enable warning update-where */

  INSERT #UsageStaging
  (
    "user_id",
    "date",
    "Yammer Platform Count",
    "Yammer Used Web",
    "Yammer Used Mobile",
    "Yammer Used Others",
    "Yammer Used WinPhone",
    "Yammer Used Android",
    "Yammer Used iPad",
    "Yammer Used iPhone"
  )
  SELECT
    "user_id",
    "date",
    used_count,
    used_web,
    used_mobile,
    used_others,
    used_win_phone,
    used_android,
    used_ipad,
    used_iphone
  FROM @ut_staging AS staging
  WHERE NOT EXISTS (
    SELECT 1
    FROM #UsageStaging AS t
    WHERE t."user_id" = staging."user_id" AND t."date" = staging."date"
  );
END;
GO

CREATE PROCEDURE profiling.usp_UpsertCopilot
(
  @StartDate DATE,
  @EndDate DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  DECLARE @copilot AS ut_copilot_activities;
  WITH
    hosts_pivoted AS (
      SELECT
        "user_id",
        "date",
        bizchat,
        appchat,
        Assist365,
        Bing,
        BashTool,
        DevUI,
        Excel,
        Loop,
        M365AdminCenter,
        M365App,
        Office,
        OneNote,
        Outlook,
        Planner,
        PowerPoint,
        SharePoint,
        Stream,
        Teams,
        VivaCopilot,
        VivaEngage,
        VivaGoals,
        Whiteboard,
        Word
      FROM (
        SELECT
          app_host,
          @StartDate AS "date",
          "user_id",
          event_id
        FROM dbo.event_copilot_chats AS c
          JOIN dbo.audit_events AS au ON c.event_id = au.id
        WHERE @StartDate <= au.time_stamp AND au.time_stamp <= @EndDate
      ) t
      PIVOT (
        COUNT(event_id)
        FOR app_host IN (
          -- AS more hosts appear, they need to be added here AS they are in the JSON
          -- bizchat is M365 Chat experience on Bing and Teams
          -- appchat refers to the copilot experiences on apps like Excel, PowerPoint, Teams, and more
          bizchat,
          appchat,
          Assist365,
          Bing,
          BashTool,
          DevUI,
          Excel,
          Loop,
          M365AdminCenter,
          M365App,
          Office,
          OneNote,
          Outlook,
          Planner,
          PowerPoint,
          SharePoint,
          Stream,
          Teams,
          VivaCopilot,
          VivaEngage,
          VivaGoals,
          Whiteboard,
          Word
        )
      ) AS pivoted
    ),
    host_activities AS (
      SELECT
        "user_id",
        "date",
        Assist365 AS copilot_assist365,
        Bing AS copilot_bing,
        BashTool AS copilot_bashtool,
        DevUI AS copilot_devui,
        Excel AS copilot_excel,
        Loop AS copilot_loop,
        M365AdminCenter AS copilot_m365admincenter,
        (bizchat + M365App) AS copilot_m365app,
        (appchat + Office) AS copilot_office,
        OneNote AS copilot_onenote,
        Outlook AS copilot_outlook,
        Planner AS copilot_planner,
        PowerPoint AS copilot_powerpoint,
        SharePoint AS copilot_sharepoint,
        Stream AS copilot_stream,
        Teams AS copilot_teams,
        VivaCopilot AS copilot_vivacopilot,
        VivaEngage AS copilot_vivaengage,
        VivaGoals AS copilot_vivagoals,
        Whiteboard AS copilot_whiteboard,
        Word AS copilot_word
      FROM hosts_pivoted
    ),
    events AS (
      SELECT
        "user_id",
        @StartDate AS "date",
        c.event_id AS chat_id,
        f.copilot_chat_id AS has_file,
        m.copilot_chat_id AS has_meeting
      FROM dbo.event_copilot_chats AS c
        JOIN dbo.audit_events AS au ON c.event_id = au.id
        LEFT JOIN dbo.event_copilot_files AS f ON c.event_id = f.copilot_chat_id
        LEFT JOIN dbo.event_copilot_meetings AS m ON c.event_id = m.copilot_chat_id
      WHERE @StartDate <= au.time_stamp AND au.time_stamp <= @EndDate
    ),
    event_counts AS (
      SELECT
        "user_id",
        "date",
        COUNT(chat_id) AS chat_count,
        COUNT(has_file) AS file_count,
        COUNT(has_meeting) AS meeting_count
      FROM events
      GROUP BY "user_id", "date"
    )
  INSERT INTO @copilot
  (
    "user_id",
    "date",
    copilot_chats,
    copilot_meetings,
    copilot_files,
    copilot_assist365,
    copilot_bing,
    copilot_bashtool,
    copilot_devui,
    copilot_excel,
    copilot_loop,
    copilot_m365admincenter,
    copilot_m365app,
    copilot_office,
    copilot_onenote,
    copilot_outlook,
    copilot_planner,
    copilot_powerpoint,
    copilot_sharepoint,
    copilot_stream,
    copilot_teams,
    copilot_vivacopilot,
    copilot_vivaengage,
    copilot_vivagoals,
    copilot_whiteboard,
    copilot_word
  )
  SELECT
    a."user_id",
    @StartDate,
    SUM(c.chat_count),
    SUM(c.meeting_count),
    SUM(c.file_count),
    SUM(copilot_assist365),
    SUM(copilot_bing),
    SUM(copilot_bashtool),
    SUM(copilot_devui),
    SUM(copilot_excel),
    SUM(copilot_loop),
    SUM(copilot_m365admincenter),
    SUM(copilot_m365app),
    SUM(copilot_office),
    SUM(copilot_onenote),
    SUM(copilot_outlook),
    SUM(copilot_planner),
    SUM(copilot_powerpoint),
    SUM(copilot_sharepoint),
    SUM(copilot_stream),
    SUM(copilot_teams),
    SUM(copilot_vivacopilot),
    SUM(copilot_vivaengage),
    SUM(copilot_vivagoals),
    SUM(copilot_whiteboard),
    SUM(copilot_word)
  FROM host_activities AS a
    JOIN event_counts AS c ON a."user_id" = c."user_id"
  GROUP BY a."user_id";

  /* tsqllint-disable warning update-where */
  UPDATE t WITH (UPDLOCK, SERIALIZABLE)
  SET
    "Copilot Chats" = tvp.copilot_chats,
    "Copilot Meetings" = tvp.copilot_meetings,
    "Copilot Files" = tvp.copilot_files,
    "Copilot App Assist365" = tvp.copilot_assist365,
    "Copilot App Bing" = tvp.copilot_bing,
    "Copilot App BashTool" = tvp.copilot_bashtool,
    "Copilot App DevUI" = tvp.copilot_devui,
    "Copilot App Excel" = tvp.copilot_excel,
    "Copilot App Loop" = tvp.copilot_loop,
    "Copilot App M365AdminCenter" = tvp.copilot_m365admincenter,
    "Copilot App M365App" = tvp.copilot_m365app,
    "Copilot App Office" = tvp.copilot_office,
    "Copilot App OneNote" = tvp.copilot_onenote,
    "Copilot App Outlook" = tvp.copilot_outlook,
    "Copilot App Planner" = tvp.copilot_planner,
    "Copilot App PowerPoint" = tvp.copilot_powerpoint,
    "Copilot App SharePoint" = tvp.copilot_sharepoint,
    "Copilot App Stream" = tvp.copilot_stream,
    "Copilot App Teams" = tvp.copilot_teams,
    "Copilot App VivaCopilot" = tvp.copilot_vivacopilot,
    "Copilot App VivaEngage" = tvp.copilot_vivaengage,
    "Copilot App VivaGoals" = tvp.copilot_vivagoals,
    "Copilot App Whiteboard" = tvp.copilot_whiteboard,
    "Copilot App Word" = tvp.copilot_word
  FROM #ActivitiesStaging AS t
    INNER JOIN @copilot AS tvp
      ON t."user_id" = tvp."user_id" AND t."date" = tvp."date";
  /* tsqllint-enable warning update-where */

  INSERT #ActivitiesStaging
  (
    "user_id",
    "date",
    "Copilot Chats",
    "Copilot Meetings",
    "Copilot Files",
    "Copilot App Assist365",
    "Copilot App Bing",
    "Copilot App BashTool",
    "Copilot App DevUI",
    "Copilot App Excel",
    "Copilot App Loop",
    "Copilot App M365AdminCenter",
    "Copilot App M365App",
    "Copilot App Office",
    "Copilot App OneNote",
    "Copilot App Outlook",
    "Copilot App Planner",
    "Copilot App PowerPoint",
    "Copilot App SharePoint",
    "Copilot App Stream",
    "Copilot App Teams",
    "Copilot App VivaCopilot",
    "Copilot App VivaEngage",
    "Copilot App VivaGoals",
    "Copilot App Whiteboard",
    "Copilot App Word"
  )
  SELECT
    "user_id",
    "date",
    copilot_chats,
    copilot_meetings,
    copilot_files,
    copilot_assist365,
    copilot_bing,
    copilot_bashtool,
    copilot_devui,
    copilot_excel,
    copilot_loop,
    copilot_m365admincenter,
    copilot_m365app,
    copilot_office,
    copilot_onenote,
    copilot_outlook,
    copilot_planner,
    copilot_powerpoint,
    copilot_sharepoint,
    copilot_stream,
    copilot_teams,
    copilot_vivacopilot,
    copilot_vivaengage,
    copilot_vivagoals,
    copilot_whiteboard,
    copilot_word
  FROM @copilot AS tvp
  WHERE NOT EXISTS (
    SELECT 1
    FROM #ActivitiesStaging AS t
    WHERE t."user_id" = tvp."user_id" AND t."date" = tvp."date"
  );
END;
GO

-- ===========================================
-- Aggregates a week of activity. Data in rows
-- ===========================================

IF OBJECT_ID(N'profiling.usp_CompileWeekActivityRows') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeekActivityRows;
END
GO

CREATE PROCEDURE profiling.usp_CompileWeekActivityRows
(
  -- Start day of the week to aggregate
  @Monday DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    EXEC profiling.usp_Trace '[usp_CompileWeekActivityRows] Starting: %s', @Monday;

    INSERT INTO profiling.ActivitiesWeekly
    SELECT
      "user_id",
      @Monday AS MetricDate,
      Metric,
      SUM(VALUE) AS Sum
    FROM #ActivitiesStaging AS Pivoted
    UNPIVOT (
      -- Convert columns into rows
      VALUE FOR Metric IN (
        "OneDrive Viewed/Edited",
        "OneDrive Synced",
        "OneDrive Shared Internally",
        "OneDrive Shared Externally",
        "Emails Sent",
        "Emails Received",
        "Emails Read",
        "Outlook Meetings Created",
        "Outlook Meetings Interacted",
        "SPO Viewed/Edited",
        "SPO Synced",
        "SPO Shared Internally",
        "SPO Shared Externally",
        "Teams Private Chats",
        "Teams Team Chats",
        "Teams Calls",
        "Teams Meetings",
        "Teams Meetings Attended",
        "Teams Meetings Organized",
        "Teams Adhoc Meetings Attended",
        "Teams Adhoc Meetings Organized",
        "Teams Scheduled Onetime Meetings Attended",
        "Teams Scheduled Onetime Meetings Organized",
        "Teams Scheduled Recurring Meetings Attended",
        "Teams Scheduled Recurring Meetings Organized",
        "Teams Audio Duration Seconds",
        "Teams Video Duration Seconds",
        "Teams Screenshare Duration Seconds",
        "Teams Post Messages",
        "Teams Reply Messages",
        "Teams Urgent Messages",
        "Yammer Posted",
        "Yammer Read",
        "Yammer Liked",
        "Copilot Chats",
        "Copilot Meetings",
        "Copilot Files",
        "Copilot App Assist365",
        "Copilot App Bing",
        "Copilot App BashTool",
        "Copilot App DevUI",
        "Copilot App Excel",
        "Copilot App Loop",
        "Copilot App M365AdminCenter",
        "Copilot App M365App",
        "Copilot App Office",
        "Copilot App OneNote",
        "Copilot App Outlook",
        "Copilot App Planner",
        "Copilot App PowerPoint",
        "Copilot App SharePoint",
        "Copilot App Stream",
        "Copilot App Teams",
        "Copilot App VivaCopilot",
        "Copilot App VivaEngage",
        "Copilot App VivaGoals",
        "Copilot App Whiteboard",
        "Copilot App Word"
      )
    ) AS Unpivoted
    GROUP BY "user_id", Metric;
  END TRY
  BEGIN CATCH
    DECLARE @ErrorMessage NVARCHAR(4000);
    SELECT @ErrorMessage = ERROR_MESSAGE();
    EXEC profiling.usp_Trace '[usp_CompileWeekActivityRows] Catch: %s', @ErrorMessage;
  END CATCH;
END;
GO

-- ==============================================
-- Aggregates a week of activity. Data in columns
-- ==============================================

IF OBJECT_ID(N'profiling.usp_CompileWeekActivityColumns') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeekActivityColumns;
END
GO

CREATE PROCEDURE profiling.usp_CompileWeekActivityColumns
(
  -- Start day of the week to aggregate
  @Monday DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    EXEC profiling.usp_Trace '[usp_CompileWeekActivityColumns] Starting: %s', @Monday;

    INSERT INTO profiling.ActivitiesWeeklyColumns
    (
      "user_id",
      "date",
      "OneDrive Viewed/Edited",
      "OneDrive Synced",
      "OneDrive Shared Internally",
      "OneDrive Shared Externally",
      "Emails Sent",
      "Emails Received",
      "Emails Read",
      "Outlook Meetings Created",
      "Outlook Meetings Interacted",
      "SPO Viewed/Edited",
      "SPO Synced",
      "SPO Shared Internally",
      "SPO Shared Externally",
      "Teams Private Chats",
      "Teams Team Chats",
      "Teams Calls",
      "Teams Meetings",
      "Teams Meetings Attended",
      "Teams Meetings Organized",
      "Teams Adhoc Meetings Attended",
      "Teams Adhoc Meetings Organized",
      "Teams Scheduled Onetime Meetings Attended",
      "Teams Scheduled Onetime Meetings Organized",
      "Teams Scheduled Recurring Meetings Attended",
      "Teams Scheduled Recurring Meetings Organized",
      "Teams Audio Duration Seconds",
      "Teams Video Duration Seconds",
      "Teams Screenshare Duration Seconds",
      "Teams Post Messages",
      "Teams Reply Messages",
      "Teams Urgent Messages",
      "Yammer Posted",
      "Yammer Read",
      "Yammer Liked",
      "Copilot Chats",
      "Copilot Meetings",
      "Copilot Files",
      "Copilot App Assist365",
      "Copilot App Bing",
      "Copilot App BashTool",
      "Copilot App DevUI",
      "Copilot App Excel",
      "Copilot App Loop",
      "Copilot App M365AdminCenter",
      "Copilot App M365App",
      "Copilot App Office",
      "Copilot App OneNote",
      "Copilot App Outlook",
      "Copilot App Planner",
      "Copilot App PowerPoint",
      "Copilot App SharePoint",
      "Copilot App Stream",
      "Copilot App Teams",
      "Copilot App VivaCopilot",
      "Copilot App VivaEngage",
      "Copilot App VivaGoals",
      "Copilot App Whiteboard",
      "Copilot App Word"
    )
    SELECT
      "user_id",
      @Monday AS "date",
      "OneDrive Viewed/Edited",
      "OneDrive Synced",
      "OneDrive Shared Internally",
      "OneDrive Shared Externally",
      "Emails Sent",
      "Emails Received",
      "Emails Read",
      "Outlook Meetings Created",
      "Outlook Meetings Interacted",
      "SPO Viewed/Edited",
      "SPO Synced",
      "SPO Shared Internally",
      "SPO Shared Externally",
      "Teams Private Chats",
      "Teams Team Chats",
      "Teams Calls",
      "Teams Meetings",
      "Teams Meetings Attended",
      "Teams Meetings Organized",
      "Teams Adhoc Meetings Attended",
      "Teams Adhoc Meetings Organized",
      "Teams Scheduled Onetime Meetings Attended",
      "Teams Scheduled Onetime Meetings Organized",
      "Teams Scheduled Recurring Meetings Attended",
      "Teams Scheduled Recurring Meetings Organized",
      "Teams Audio Duration Seconds",
      "Teams Video Duration Seconds",
      "Teams Screenshare Duration Seconds",
      "Teams Post Messages",
      "Teams Reply Messages",
      "Teams Urgent Messages",
      "Yammer Posted",
      "Yammer Read",
      "Yammer Liked",
      "Copilot Chats",
      "Copilot Meetings",
      "Copilot Files",
      "Copilot App Assist365",
      "Copilot App Bing",
      "Copilot App BashTool",
      "Copilot App DevUI",
      "Copilot App Excel",
      "Copilot App Loop",
      "Copilot App M365AdminCenter",
      "Copilot App M365App",
      "Copilot App Office",
      "Copilot App OneNote",
      "Copilot App Outlook",
      "Copilot App Planner",
      "Copilot App PowerPoint",
      "Copilot App SharePoint",
      "Copilot App Stream",
      "Copilot App Teams",
      "Copilot App VivaCopilot",
      "Copilot App VivaEngage",
      "Copilot App VivaGoals",
      "Copilot App Whiteboard",
      "Copilot App Word"
    FROM #ActivitiesStaging;
  END TRY
  BEGIN CATCH
    DECLARE @ErrorMessage NVARCHAR(4000);
    SELECT @ErrorMessage = ERROR_MESSAGE();
    EXEC profiling.usp_Trace '[usp_CompileWeekActivityColumns] Catch: %s', @ErrorMessage;
  END CATCH;
END;
GO

-- ===========================================
-- Aggregates a week of usage. Data in columns
-- ===========================================

IF OBJECT_ID(N'profiling.usp_CompileUsageWeek') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileUsageWeek;
END
GO

CREATE PROCEDURE profiling.usp_CompileUsageWeek
(
  -- Start day of the week to aggregate
  @Monday DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    EXEC profiling.usp_Trace '[usp_CompileUsageWeek] Starting: %s', @Monday;

    DECLARE
      @Sunday DATE = DATEADD(DAY, 6, @Monday),
      @ColumnsDone INT = 0;
    
    -- Check if the data has already been aggregated
    SELECT @ColumnsDone = COUNT(DATE)
    FROM profiling.UsageWeekly
    WHERE "date" = @Monday;
    
    -- If the data has not been aggregated, do it
    IF @ColumnsDone = 0
    BEGIN
      CREATE TABLE #UsageStaging
      (
        "user_id" INT NOT NULL,
        "date" DATETIME NOT NULL,
        "Teams Used Web" INT NOT NULL DEFAULT 0,
        "Teams Used Mac" INT NOT NULL DEFAULT 0,
        "Teams Used Windows" INT NOT NULL DEFAULT 0,
        "Teams Used Linux" INT NOT NULL DEFAULT 0,
        "Teams Used Chrome OS" INT NOT NULL DEFAULT 0,
        "Teams Used Mobile" INT NOT NULL DEFAULT 0,
        "Teams Used WinPhone" INT NOT NULL DEFAULT 0,
        "Teams Used iOS" INT NOT NULL DEFAULT 0,
        "Teams Used Android" INT NOT NULL DEFAULT 0,
        "Office Windows" INT NOT NULL DEFAULT 0,
        "Office Mac" INT NOT NULL DEFAULT 0,
        "Office Mobile" INT NOT NULL DEFAULT 0,
        "Office Web" INT NOT NULL DEFAULT 0,
        "Office Outlook" INT NOT NULL DEFAULT 0,
        "Office Word" INT NOT NULL DEFAULT 0,
        "Office Excel" INT NOT NULL DEFAULT 0,
        "Office Powerpoint" INT NOT NULL DEFAULT 0,
        "Office Onenote" INT NOT NULL DEFAULT 0,
        "Office Teams" INT NOT NULL DEFAULT 0,
        "Office Outlook Windows" INT NOT NULL DEFAULT 0,
        "Office Word Windows" INT NOT NULL DEFAULT 0,
        "Office Excel Windows" INT NOT NULL DEFAULT 0,
        "Office Powerpoint Windows" INT NOT NULL DEFAULT 0,
        "Office Onenote Windows" INT NOT NULL DEFAULT 0,
        "Office Teams Windows" INT NOT NULL DEFAULT 0,
        "Office Outlook Mac" INT NOT NULL DEFAULT 0,
        "Office Word Mac" INT NOT NULL DEFAULT 0,
        "Office Excel Mac" INT NOT NULL DEFAULT 0,
        "Office Powerpoint Mac" INT NOT NULL DEFAULT 0,
        "Office Onenote Mac" INT NOT NULL DEFAULT 0,
        "Office Teams Mac" INT NOT NULL DEFAULT 0,
        "Office Outlook Mobile" INT NOT NULL DEFAULT 0,
        "Office Word Mobile" INT NOT NULL DEFAULT 0,
        "Office Excel Mobile" INT NOT NULL DEFAULT 0,
        "Office Powerpoint Mobile" INT NOT NULL DEFAULT 0,
        "Office Onenote Mobile" INT NOT NULL DEFAULT 0,
        "Office Teams Mobile" INT NOT NULL DEFAULT 0,
        "Office Outlook Web" INT NOT NULL DEFAULT 0,
        "Office Word Web" INT NOT NULL DEFAULT 0,
        "Office Excel Web" INT NOT NULL DEFAULT 0,
        "Office Powerpoint Web" INT NOT NULL DEFAULT 0,
        "Office Onenote Web" INT NOT NULL DEFAULT 0,
        "Office Teams Web" INT NOT NULL DEFAULT 0,
        "Yammer Platform Count" INT NOT NULL DEFAULT 0,
        "Yammer Used Web" INT NOT NULL DEFAULT 0,
        "Yammer Used Mobile" INT NOT NULL DEFAULT 0,
        "Yammer Used Others" INT NOT NULL DEFAULT 0,
        "Yammer Used WinPhone" BIT NOT NULL DEFAULT 0,
        "Yammer Used Android" BIT NOT NULL DEFAULT 0,
        "Yammer Used iPad" BIT NOT NULL DEFAULT 0,
        "Yammer Used iPhone" BIT NOT NULL DEFAULT 0
      );
      EXECUTE profiling.usp_UpsertTeamsDevices @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertM365Apps @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertYammerDevices @Monday, @Sunday;

      INSERT INTO profiling.UsageWeekly
      (
        "user_id",
        "date",
        "Teams Used Web",
        "Teams Used Mac",
        "Teams Used Windows",
        "Teams Used Linux",
        "Teams Used Chrome OS",
        "Teams Used Mobile",
        "Teams Used WinPhone",
        "Teams Used iOS",
        "Teams Used Android",
        "Office Windows",
        "Office Mac",
        "Office Mobile",
        "Office Web",
        "Office Outlook",
        "Office Word",
        "Office Excel",
        "Office Powerpoint",
        "Office Onenote",
        "Office Teams",
        "Office Outlook Windows",
        "Office Word Windows",
        "Office Excel Windows",
        "Office Powerpoint Windows",
        "Office Onenote Windows",
        "Office Teams Windows",
        "Office Outlook Mac",
        "Office Word Mac",
        "Office Excel Mac",
        "Office Powerpoint Mac",
        "Office Onenote Mac",
        "Office Teams Mac",
        "Office Outlook Mobile",
        "Office Word Mobile",
        "Office Excel Mobile",
        "Office Powerpoint Mobile",
        "Office Onenote Mobile",
        "Office Teams Mobile",
        "Office Outlook Web",
        "Office Word Web",
        "Office Excel Web",
        "Office Powerpoint Web",
        "Office Onenote Web",
        "Office Teams Web",
        "Yammer Platform Count",
        "Yammer Used Web",
        "Yammer Used Mobile",
        "Yammer Used Others",
        "Yammer Used WinPhone",
        "Yammer Used Android",
        "Yammer Used iPad",
        "Yammer Used iPhone"
      )
      SELECT
        "user_id",
        @Monday AS "date",
        "Teams Used Web",
        "Teams Used Mac",
        "Teams Used Windows",
        "Teams Used Linux",
        "Teams Used Chrome OS",
        "Teams Used Mobile",
        "Teams Used WinPhone",
        "Teams Used iOS",
        "Teams Used Android",
        "Office Windows",
        "Office Mac",
        "Office Mobile",
        "Office Web",
        "Office Outlook",
        "Office Word",
        "Office Excel",
        "Office Powerpoint",
        "Office Onenote",
        "Office Teams",
        "Office Outlook Windows",
        "Office Word Windows",
        "Office Excel Windows",
        "Office Powerpoint Windows",
        "Office Onenote Windows",
        "Office Teams Windows",
        "Office Outlook Mac",
        "Office Word Mac",
        "Office Excel Mac",
        "Office Powerpoint Mac",
        "Office Onenote Mac",
        "Office Teams Mac",
        "Office Outlook Mobile",
        "Office Word Mobile",
        "Office Excel Mobile",
        "Office Powerpoint Mobile",
        "Office Onenote Mobile",
        "Office Teams Mobile",
        "Office Outlook Web",
        "Office Word Web",
        "Office Excel Web",
        "Office Powerpoint Web",
        "Office Onenote Web",
        "Office Teams Web",
        "Yammer Platform Count",
        "Yammer Used Web",
        "Yammer Used Mobile",
        "Yammer Used Others",
        "Yammer Used WinPhone",
        "Yammer Used Android",
        "Yammer Used iPad",
        "Yammer Used iPhone"
      FROM #UsageStaging;

      DROP TABLE #UsageStaging;
    END
  END TRY
  BEGIN CATCH
    DECLARE @ErrorMessage NVARCHAR(4000);
    SELECT @ErrorMessage = ERROR_MESSAGE();
    EXEC profiling.usp_Trace 'Catch [usp_CompileUsageWeek]: %s', @ErrorMessage;
    IF OBJECT_ID('tempdb..#UsageStaging') IS NOT NULL
    BEGIN
      DROP TABLE #UsageStaging;
    END
  END CATCH;
END;
GO

-- ===================================
-- Aggregates a week of analytics data
-- ===================================

IF OBJECT_ID(N'profiling.usp_CompileActivityWeek') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileActivityWeek;
END
GO

CREATE PROCEDURE profiling.usp_CompileActivityWeek
(
  -- Start day of the week to aggregate
  @Monday DATE
)
AS
BEGIN
  SET NOCOUNT ON;
  BEGIN TRY
    EXEC profiling.usp_Trace '[usp_CompileActivityWeek] Starting: %s', @Monday;

    DECLARE
      @Sunday DATE = DATEADD(DAY, 6, @Monday),
      @RowsDone INT = 0,
      @ColumnsDone INT = 0;

    -- Check if the data has already been aggregated
    SELECT @ColumnsDone = COUNT("date")
    FROM profiling.ActivitiesWeeklyColumns
    WHERE "date" = @Monday;

    SELECT @RowsDone = COUNT(MetricDate)
    FROM profiling.ActivitiesWeekly
    WHERE MetricDate = @Monday;

    IF @RowsDone = 0 OR @ColumnsDone = 0
    BEGIN
      CREATE TABLE #ActivitiesStaging
      (
        "user_id" BIGINT,
        "date" DATE,
        "OneDrive Viewed/Edited" BIGINT DEFAULT 0,
        "OneDrive Synced" BIGINT DEFAULT 0,
        "OneDrive Shared Internally" BIGINT DEFAULT 0,
        "OneDrive Shared Externally" BIGINT DEFAULT 0,
        "SPO Viewed/Edited" BIGINT DEFAULT 0,
        "SPO Synced" BIGINT DEFAULT 0,
        "SPO Shared Internally" BIGINT DEFAULT 0,
        "SPO Shared Externally" BIGINT DEFAULT 0,
        "Emails Sent" BIGINT DEFAULT 0,
        "Emails Received" BIGINT DEFAULT 0,
        "Emails Read" BIGINT DEFAULT 0,
        "Outlook Meetings Created" BIGINT DEFAULT 0,
        "Outlook Meetings Interacted" BIGINT DEFAULT 0,
        "Teams Private Chats" BIGINT DEFAULT 0,
        "Teams Team Chats" BIGINT DEFAULT 0,
        "Teams Calls" BIGINT DEFAULT 0,
        "Teams Meetings" BIGINT DEFAULT 0,
        "Teams Meetings Attended" BIGINT DEFAULT 0,
        "Teams Meetings Organized" BIGINT DEFAULT 0,
        "Teams Adhoc Meetings Attended" BIGINT DEFAULT 0,
        "Teams Adhoc Meetings Organized" BIGINT DEFAULT 0,
        "Teams Scheduled Onetime Meetings Attended" BIGINT DEFAULT 0,
        "Teams Scheduled Onetime Meetings Organized" BIGINT DEFAULT 0,
        "Teams Scheduled Recurring Meetings Attended" BIGINT DEFAULT 0,
        "Teams Scheduled Recurring Meetings Organized" BIGINT DEFAULT 0,
        "Teams Audio Duration Seconds" BIGINT DEFAULT 0,
        "Teams Video Duration Seconds" BIGINT DEFAULT 0,
        "Teams Screenshare Duration Seconds" BIGINT DEFAULT 0,
        "Teams Post Messages" BIGINT DEFAULT 0,
        "Teams Reply Messages" BIGINT DEFAULT 0,
        "Teams Urgent Messages" BIGINT DEFAULT 0,
        "Yammer Posted" BIGINT DEFAULT 0,
        "Yammer Read" BIGINT DEFAULT 0,
        "Yammer Liked" BIGINT DEFAULT 0,
        "Copilot Chats" BIGINT DEFAULT 0,
        "Copilot Meetings" BIGINT DEFAULT 0,
        "Copilot Files" BIGINT DEFAULT 0,
        "Copilot App Assist365" BIGINT DEFAULT 0,
        "Copilot App Bing" BIGINT DEFAULT 0,
        "Copilot App BashTool" BIGINT DEFAULT 0,
        "Copilot App DevUI" BIGINT DEFAULT 0,
        "Copilot App Excel" BIGINT DEFAULT 0,
        "Copilot App Loop" BIGINT DEFAULT 0,
        "Copilot App M365AdminCenter" BIGINT DEFAULT 0,
        "Copilot App M365App" BIGINT DEFAULT 0,
        "Copilot App Office" BIGINT DEFAULT 0,
        "Copilot App OneNote" BIGINT DEFAULT 0,
        "Copilot App Outlook" BIGINT DEFAULT 0,
        "Copilot App Planner" BIGINT DEFAULT 0,
        "Copilot App PowerPoint" BIGINT DEFAULT 0,
        "Copilot App SharePoint" BIGINT DEFAULT 0,
        "Copilot App Stream" BIGINT DEFAULT 0,
        "Copilot App Teams" BIGINT DEFAULT 0,
        "Copilot App VivaCopilot" BIGINT DEFAULT 0,
        "Copilot App VivaEngage" BIGINT DEFAULT 0,
        "Copilot App VivaGoals" BIGINT DEFAULT 0,
        "Copilot App Whiteboard" BIGINT DEFAULT 0,
        "Copilot App Word" BIGINT DEFAULT 0
      );

      -- Insert only the activities between the dates
      EXECUTE profiling.usp_UpsertTeams @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertOneDrive @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertSharePoint @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertOutlook @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertYammer @Monday, @Sunday;
      EXECUTE profiling.usp_UpsertCopilot @Monday, @Sunday;

      IF @ColumnsDone = 0
      BEGIN
        EXECUTE profiling.usp_CompileWeekActivityColumns @Monday;
      END

      IF @RowsDone = 0
      BEGIN
        EXECUTE profiling.usp_CompileWeekActivityRows @Monday;
      END

      DROP TABLE #ActivitiesStaging;
    END
  END TRY
  BEGIN CATCH
    DECLARE @ErrorMessage NVARCHAR(4000);
    SELECT @ErrorMessage = ERROR_MESSAGE();
    EXEC profiling.usp_Trace '[usp_CompileActivityWeek] Catch: %s', @ErrorMessage;
    IF OBJECT_ID('tempdb..#ActivitiesStaging') IS NOT NULL
    BEGIN
      DROP TABLE #ActivitiesStaging;
    END
  END CATCH;
END;
GO

-- =============================================
-- Aggregates all possible weekly analytics data
-- =============================================

IF OBJECT_ID(N'profiling.usp_CompileWeekly') IS NOT NULL
BEGIN
  DROP PROCEDURE profiling.usp_CompileWeekly;
END
GO

CREATE PROCEDURE profiling.usp_CompileWeekly
(
  @WeeksToKeep INT,
  @All INT = 0
)
AS
BEGIN
  SET NOCOUNT ON;
  -- Today is the first day that there should be data, which usually is Today-4 days
  DECLARE @Today DATE = DATEADD(DAY, -4, GETDATE());
  -- Day when aggregation should stop
  DECLARE @ThisWeeksMonday DATE = profiling.udf_GetMonday (@Today);
  -- When data aggregation starts
  DECLARE @RetentionDate DATE = DATEADD(WEEK, -1 * @WeeksToKeep, @ThisWeeksMonday);
  -- Get last aggregated date in the table, it will be a Monday
  DECLARE @LastDateInTables DATE;

  -- Dates in the aggregated weekly tables
  WITH
    dates AS (
      SELECT MAX(MetricDate) AS "date" FROM profiling.ActivitiesWeekly
      UNION
      SELECT MAX("date") FROM profiling.ActivitiesWeeklyColumns
      UNION
      SELECT MAX("date") FROM profiling.UsageWeekly
    )
  SELECT @LastDateInTables = MIN("date")
  FROM dates;

  IF @LastDateInTables IS NULL OR @All = 1
  BEGIN
    -- Start from the retention date
    SELECT @LastDateInTables = @RetentionDate;
  END

  -- Week by week, aggregate the data
  DECLARE @Monday DATE = DATEADD(DAY, 7, @LastDateInTables);

  EXEC profiling.usp_Trace
    N'Weekly aggregation requested. First: %s. Last: %s',
    @Monday,
    @ThisWeeksMonday;

  WHILE @ThisWeeksMonday > @Monday
  BEGIN
    DECLARE @Sunday DATE = DATEADD(DAY, 6, @Monday);

    EXEC profiling.usp_Trace 'Week from %s to %s', @Monday, @Sunday;

    EXECUTE profiling.usp_CompileActivityWeek @Monday;
    EXECUTE profiling.usp_CompileUsageWeek @Monday;

    SELECT @Monday = DATEADD(DAY, 7, @Monday);
  END

  -- Cleanup. Remove data in the tables before the retention date
  EXEC profiling.usp_Trace 'Starting cleanup. Retention date: %s', @RetentionDate;

  DELETE FROM profiling.ActivitiesWeekly
  WHERE MetricDate < @RetentionDate;

  DELETE FROM profiling.ActivitiesWeeklyColumns
  WHERE "date" < @RetentionDate;

  DELETE FROM profiling.UsageWeekly
  WHERE "date" < @RetentionDate;

  EXEC profiling.usp_Trace 'Aggregation finished';
END;
GO
