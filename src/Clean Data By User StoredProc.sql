
IF OBJECT_ID('dbo.CleanDataByUser', 'P') IS NOT NULL
    DROP PROCEDURE dbo.CleanDataByUser;
GO

CREATE PROCEDURE dbo.CleanDataByUser
    @userId INT
AS
BEGIN
    SET NOCOUNT ON;


    BEGIN TRY
    BEGIN TRAN;





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
    FROM event_copilot_files f
    WHERE f.copilot_chat_id IN (
        SELECT c.event_id FROM event_copilot_chats c
        WHERE c.event_id IN (SELECT Id FROM #UserEvents)
    );

    DELETE m
    FROM event_copilot_meetings m
    WHERE m.copilot_chat_id IN (
        SELECT c.event_id FROM event_copilot_chats c
        WHERE c.event_id IN (SELECT Id FROM #UserEvents)
    );

    DELETE FROM event_copilot_chats
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
    -- Safety: if prior execution on same session failed before cleanup.
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

    IF NOT EXISTS (SELECT 1 FROM #MessagesToDelete)
    BEGIN
        DROP TABLE #MessagesToDelete;
        RETURN;
    END

    BEGIN TRAN;

        -- Delete dependent links first
        DELETE ysl
        FROM dbo.yammer_msg_to_stream ysl
        INNER JOIN #MessagesToDelete d ON d.id = ysl.message_id;

        -- Delete messages
        DELETE ym
        FROM dbo.yammer_messages ym
        INNER JOIN #MessagesToDelete d ON d.id = ym.id;

        DECLARE @Deleted INT = @@ROWCOUNT;

    COMMIT;

    DROP TABLE #MessagesToDelete;


    -------------------------------------------------
    -- Finally the user
    -------------------------------------------------
    DELETE FROM users WHERE id = @UserId;






    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    THROW;
END CATCH


END;

