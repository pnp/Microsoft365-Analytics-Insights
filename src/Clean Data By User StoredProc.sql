/*======================================================================
  Procedure:   [dbo].[CleanDataByUser]
  Purpose:     Safely clean/delete user-scoped data across related tables,
               with transactional safety.
  Author:      sambetts
  Created:     2025-09-26
  Notes:       - Idempotent deployment (CREATE OR ALTER)
               - XACT_ABORT, TRY/CATCH with THROW
               - Respects outer transactions
  Returns:     Return code (0=OK), @ReturnMessage OUTPUT
======================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[CleanDataByUser]
      @UserId          INT                      -- TODO: adjust type (e.g., UNIQUEIDENTIFIER)
    , @ReturnMessage   NVARCHAR(4000) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -------------------------------------------------------------------
    -- 0) DECLARATIONS
    -------------------------------------------------------------------
    DECLARE @proc_name SYSNAME = OBJECT_SCHEMA_NAME(@@PROCID) + N'.' + OBJECT_NAME(@@PROCID);
    DECLARE @start_time DATETIME2(3) = SYSUTCDATETIME();
    DECLARE @had_outer_tx BIT = CASE WHEN @@TRANCOUNT > 0 THEN 1 ELSE 0 END;
    DECLARE @rc INT = 0;
    DECLARE @total BIGINT = 0;
    SET @ReturnMessage = N'';

    -------------------------------------------------------------------
    -- 1) VALIDATION
    -------------------------------------------------------------------
    IF @UserId IS NULL
    BEGIN
        SET @rc = 51001;
        SET @ReturnMessage = N'@UserId is required.';
        ;THROW 51001, '@UserId is required.', 1;
    END

    -------------------------------------------------------------------
    -- 2) OPTIONAL: ENFORCE ISOLATION (leave commented unless needed)
    -------------------------------------------------------------------
    -- SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
    -- SET TRANSACTION ISOLATION LEVEL SNAPSHOT;

    BEGIN TRY
        

    -------------------------------------------------
    -- Temp tables for reuse
    -------------------------------------------------
    IF OBJECT_ID('tempdb..#UserEvents') IS NOT NULL DROP TABLE #UserEvents;
    CREATE TABLE #UserEvents (Id UNIQUEIDENTIFIER PRIMARY KEY);
    INSERT INTO #UserEvents(Id)
    SELECT id FROM audit_events WHERE user_id = @UserId;

    IF OBJECT_ID('tempdb..#UserSessions') IS NOT NULL DROP TABLE #UserSessions;
    CREATE TABLE #UserSessions (Id INT PRIMARY KEY);
    INSERT INTO #UserSessions(Id)
    SELECT id FROM sessions WHERE user_id = @UserId;

    IF OBJECT_ID('tempdb..#UserSearches') IS NOT NULL DROP TABLE #UserSearches;
    CREATE TABLE #UserSearches (Id INT PRIMARY KEY);
    INSERT INTO #UserSearches(Id)
    SELECT s.id
    FROM searches s
    INNER JOIN #UserSessions us ON us.Id = s.session_id;

    -- Call records (organiser or attendee)
    IF OBJECT_ID('tempdb..#UserCallRecords') IS NOT NULL DROP TABLE #UserCallRecords;
    CREATE TABLE #UserCallRecords (Id INT PRIMARY KEY);
    INSERT INTO #UserCallRecords(Id)
    SELECT DISTINCT cr.id
    FROM call_records cr
    LEFT JOIN call_sessions cs ON cs.call_record_id = cr.id
    WHERE cr.organizer_id = @UserId OR cs.attendee_user_id = @UserId;

    IF OBJECT_ID('tempdb..#UserCallSessions') IS NOT NULL DROP TABLE #UserCallSessions;
    CREATE TABLE #UserCallSessions (Id INT PRIMARY KEY);
    INSERT INTO #UserCallSessions(Id)
    SELECT id FROM call_sessions WHERE call_record_id IN (SELECT Id FROM #UserCallRecords) OR attendee_user_id = @UserId;

    -------------------------------------------------
    -- Delete dependent rows (leaf -> parent order)
    -------------------------------------------------

    -- Page likes & comments
    DELETE FROM page_likes    WHERE user_id = @UserId;
    DELETE FROM page_comments WHERE user_id = @UserId;

    -- Teams / user activity logs
    DELETE FROM teams_addons_user_installed_log WHERE user_id = @UserId;
    DELETE FROM teams_user_channel_reactions    WHERE user_id = @UserId;
    DELETE FROM team_membership_log             WHERE user_id = @UserId;
    DELETE FROM team_owners                     WHERE owner_id = @UserId;
    DELETE FROM user_license_type_lookups       WHERE user_id = @UserId;

    DELETE FROM teams_user_activity_log         WHERE user_id = @UserId;
    DELETE FROM teams_user_device_usage_log     WHERE user_id = @UserId;
    DELETE FROM platform_user_activity_log      WHERE user_id = @UserId;

    DELETE FROM sharepoint_user_activity_log    WHERE user_id = @UserId;
    DELETE FROM onedrive_user_activity_log      WHERE user_id = @UserId;
    DELETE FROM yammer_user_activity_log        WHERE user_id = @UserId;
    DELETE FROM outlook_user_activity_log       WHERE user_id = @UserId;

    -- Searches & sessions
    DELETE s
    FROM searches s
    INNER JOIN #UserSearches us ON us.Id = s.id;

    DELETE FROM sessions WHERE id IN (SELECT Id FROM #UserSessions);

    -- Call related (modalities -> sessions -> feedback/failures -> records)
    DELETE csm
    FROM call_session_call_modalities csm
    WHERE csm.call_session_id IN (SELECT Id FROM #UserCallSessions);

    DELETE FROM call_feedback
    WHERE call_id IN (SELECT Id FROM #UserCallRecords)
       OR user_id = @UserId;

    DELETE FROM call_failures
    WHERE call_id IN (SELECT Id FROM #UserCallRecords);

    DELETE FROM call_sessions
    WHERE id IN (SELECT Id FROM #UserCallSessions);

    DELETE FROM call_records
    WHERE id IN (SELECT Id FROM #UserCallRecords);

    -------------------------------------------------
    -- Copilot (files/meetings before chats)
    -------------------------------------------------
    DELETE f
    FROM copilot_event_files f
    WHERE f.copilot_chat_id IN (
        SELECT c.event_id FROM copilot_chats c
        WHERE c.event_id IN (SELECT Id FROM #UserEvents)
    );

    DELETE m
    FROM copilot_event_meetings m
    WHERE m.copilot_chat_id IN (
        SELECT c.event_id FROM copilot_chats c
        WHERE c.event_id IN (SELECT Id FROM #UserEvents)
    );

    DELETE FROM copilot_chats
    WHERE event_id IN (SELECT Id FROM #UserEvents);

    -------------------------------------------------
    -- Other event metadata (add more if needed)
    -------------------------------------------------
    DELETE FROM event_meta_sharepoint WHERE event_id IN (SELECT Id FROM #UserEvents);
    DELETE FROM event_meta_stream     WHERE event_id IN (SELECT Id FROM #UserEvents);
    DELETE FROM event_meta_general    WHERE event_id IN (SELECT Id FROM #UserEvents);
    DELETE FROM event_meta_exchange   WHERE event_id IN (SELECT Id FROM #UserEvents);

    -------------------------------------------------
    -- Audit events (base)
    -------------------------------------------------
    DELETE FROM audit_events WHERE user_id = @UserId;



    -- Yammer messages
        IF OBJECT_ID('tempdb..#MessagesToDelete') IS NOT NULL
            DROP TABLE #MessagesToDelete;

        ;WITH RecursiveMessages AS
        (
            -- Seed: messages authored by the user
            SELECT ym.id, ym.yammer_msg_id
            FROM dbo.yammer_messages ym
            WHERE ym.sender_id = @UserId
            UNION ALL
            -- Recurse: any message replying (directly/indirectly) to those
            SELECT child.id, child.yammer_msg_id
            FROM dbo.yammer_messages child
            INNER JOIN RecursiveMessages parent
                ON child.reply_to_yammer_msg_id = parent.yammer_msg_id
        )
        SELECT DISTINCT rm.id
        INTO #MessagesToDelete
        FROM RecursiveMessages rm
        OPTION (MAXRECURSION 0);

        IF EXISTS (SELECT 1 FROM #MessagesToDelete)
        BEGIN
            -- Delete dependent links first
            DELETE ysl
            FROM dbo.yammer_msg_to_stream ysl
            INNER JOIN #MessagesToDelete d ON d.id = ysl.message_id;

            -- Delete messages
            DELETE ym
            FROM dbo.yammer_messages ym
            INNER JOIN #MessagesToDelete d ON d.id = ym.id;

            -- Optional: capture @Deleted if you need it
            DECLARE @Deleted INT = @@ROWCOUNT;
        END

        -- Always drop the temp table if it exists
        IF OBJECT_ID('tempdb..#MessagesToDelete') IS NOT NULL
            DROP TABLE #MessagesToDelete;


        -- Unset any users with this user as their manager
        Update users set manager_id = null 
            WHERE manager_id = @UserId;



        -------------------------------------------------
        -- Finally the user
        -------------------------------------------------
        DELETE FROM users WHERE id = @UserId;


        RETURN @rc; -- 0
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 AND @had_outer_tx = 0
            ROLLBACK;

        DECLARE 
              @err_number   INT         = ERROR_NUMBER()
            , @err_severity INT         = ERROR_SEVERITY()
            , @err_state    INT         = ERROR_STATE()
            , @err_line     INT         = ERROR_LINE()
            , @err_proc     NVARCHAR(256) = ERROR_PROCEDURE()
            , @err_msg      NVARCHAR(4000) = ERROR_MESSAGE();

        SET @rc = ISNULL(@err_number, 50000);
        SET @ReturnMessage = CONCAT(
            N'ERR: ', ISNULL(@err_proc, @proc_name), N' (line ', @err_line, N'): ',
            @err_msg, N' [state=', @err_state, N', severity=', @err_severity, N']'
        );

        ;THROW; -- preserve original error metadata
    END CATCH
END
GO

-- Optional permissions
-- GRANT EXECUTE ON [dbo].[CleanDataByUser] TO [app_executor];
