/*======================================================================
  Procedure:   [dbo].[CleanDataByUser]
  Purpose:     Permanently deletes what the analytics database stores about one person, in a single
               transaction. It removes and anonymises the same data as the portal's User scope purge
               (Common/Entities/UserScope/Purge/UserScopePurgePlan.cs), for one user instead of everyone
               outside a scope; keep the two in step.
  Author:      sambetts
  Created:     2025-09-26
  Notes:       - Idempotent deployment (CREATE OR ALTER)
               - All or nothing: run on its own, it commits only once every step has worked, and rolls
                 everything back if any step fails. Called inside your own transaction, it neither
                 commits nor rolls back itself; after an error (XACT_ABORT ON) roll yours back.
               - XACT_ABORT, TRY/CATCH with THROW
               - Deletes the person's own data. What is someone else's but names them is anonymised
                 instead: on Teams calls they share with other people they become the anonymous
                 "Unknown User", replies to their page comments and Viva Engage messages lose the link to
                 them, shares made to them lose their recipient, and the people they managed lose their
                 manager. Search text, prompt phrases and email addresses that only their rows used are
                 deleted; ones that anything else still uses stay.
               - Not touched: tenant-wide data, the anonymous "Unknown User" (refused), anything kept
                 outside this database, and the user-organisation import history.
               - The importers can keep running. If one adds data about the person while this runs, the
                 delete it conflicts with fails and everything is rolled back: run it again.
  Returns:     Return code (0=OK), @ReturnMessage OUTPUT
======================================================================*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

CREATE OR ALTER PROCEDURE [dbo].[CleanDataByUser]
      @UserId          INT
    , @ReturnMessage   NVARCHAR(4000) = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -------------------------------------------------------------------
    -- 0) DECLARATIONS
    -------------------------------------------------------------------
    DECLARE @proc_name SYSNAME = OBJECT_SCHEMA_NAME(@@PROCID) + N'.' + OBJECT_NAME(@@PROCID);
    DECLARE @had_outer_tx BIT = CASE WHEN @@TRANCOUNT > 0 THEN 1 ELSE 0 END;
    DECLARE @rc INT = 0;
    DECLARE @azure_ad_id NVARCHAR(400);
    DECLARE @object_id UNIQUEIDENTIFIER;
    DECLARE @unknown_user_id INT;
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

    -- The anonymous Unknown User stands in for everyone the imports could not identify or were told to leave out.
    -- It is not a person, and purging it would delete or rewrite other people's calls.
    IF EXISTS (SELECT 1 FROM dbo.users WHERE id = @UserId AND user_name = 'Unknown User')
    BEGIN
        SET @rc = 51002;
        SET @ReturnMessage = N'The anonymous Unknown User stands in for many people and is never purged.';
        ;THROW 51002, 'The anonymous Unknown User stands in for many people and is never purged.', 1;
    END

    BEGIN TRY
        IF @had_outer_tx = 0
            BEGIN TRANSACTION;

        SELECT @azure_ad_id = azure_ad_id FROM dbo.users WHERE id = @UserId;
        SET @object_id = TRY_CONVERT(UNIQUEIDENTIFIER, @azure_ad_id);

        -------------------------------------------------
        -- Temp tables for reuse
        -------------------------------------------------
        IF OBJECT_ID('tempdb..#UserEvents') IS NOT NULL DROP TABLE #UserEvents;
        CREATE TABLE #UserEvents (Id UNIQUEIDENTIFIER PRIMARY KEY);
        INSERT INTO #UserEvents (Id)
        SELECT id FROM dbo.audit_events WHERE user_id = @UserId;

        IF OBJECT_ID('tempdb..#UserSessions') IS NOT NULL DROP TABLE #UserSessions;
        CREATE TABLE #UserSessions (Id INT PRIMARY KEY);
        INSERT INTO #UserSessions (Id)
        SELECT id FROM dbo.sessions WHERE user_id = @UserId;

        -- Lookup rows the person's rows point at: deleted at the end if nothing refers to them any more.
        IF OBJECT_ID('tempdb..#ReleasedSearchTerms') IS NOT NULL DROP TABLE #ReleasedSearchTerms;
        CREATE TABLE #ReleasedSearchTerms (Id INT PRIMARY KEY);
        IF OBJECT_ID('tempdb..#ReleasedAddresses') IS NOT NULL DROP TABLE #ReleasedAddresses;
        CREATE TABLE #ReleasedAddresses (Id INT PRIMARY KEY);
        IF OBJECT_ID('tempdb..#ReleasedKeywords') IS NOT NULL DROP TABLE #ReleasedKeywords;
        CREATE TABLE #ReleasedKeywords (Id INT PRIMARY KEY);

        -------------------------------------------------
        -- Audit events and everything hanging off them (leaf -> parent). Copilot details are keyed by the audit
        -- event they describe, so the event's user decides.
        -------------------------------------------------
        DELETE x FROM dbo.copilot_event_files x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_event_meetings x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_tool_executions x
        WHERE x.event_id IN (SELECT Id FROM #UserEvents) OR x.copilot_chat_id IN (SELECT Id FROM #UserEvents);
        DELETE x FROM dbo.copilot_event_accessed_resources x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_event_messages x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_event_contexts x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_event_ai_models x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_event_ai_system_plugins x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_dlp_events x INNER JOIN #UserEvents e ON e.Id = x.copilot_chat_id;
        DELETE x FROM dbo.copilot_chat_duplicates x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.copilot_chats x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.dlp_rule_matches x INNER JOIN #UserEvents e ON e.Id = x.event_id;

        -- What the person shared goes with their event. What others shared with them is anonymised further down.
        DELETE x FROM dbo.event_meta_power_app_share x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_power_automate_flow_share x INNER JOIN #UserEvents e ON e.Id = x.event_id;

        -- Exchange and Entra ID extended properties. The RetireUnusedAuditYammerStreamTables migration keeps these
        -- tables only where they held data, and their original foreign keys to the event metadata do not cascade.
        -- Guarded and run through sp_executesql so this procedure still runs where the tables have gone.
        IF OBJECT_ID('dbo.audit_event_azure_ad_props', 'U') IS NOT NULL
            EXEC sp_executesql N'DELETE x FROM dbo.audit_event_azure_ad_props x INNER JOIN #UserEvents e ON e.Id = x.event_id;';
        IF OBJECT_ID('dbo.audit_event_exchange_props', 'U') IS NOT NULL
            EXEC sp_executesql N'DELETE x FROM dbo.audit_event_exchange_props x INNER JOIN #UserEvents e ON e.Id = x.event_id;';

        DELETE x FROM dbo.event_meta_azure_ad x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_copilot_studio x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_exchange x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_general x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_power_app x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_power_automate_flow x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_power_bi x INNER JOIN #UserEvents e ON e.Id = x.event_id;
        DELETE x FROM dbo.event_meta_sharepoint x INNER JOIN #UserEvents e ON e.Id = x.event_id;

        -- Microsoft Stream (Classic) audit metadata, kept by the same migration only where it held data.
        IF OBJECT_ID('dbo.event_meta_stream', 'U') IS NOT NULL
            EXEC sp_executesql N'DELETE x FROM dbo.event_meta_stream x INNER JOIN #UserEvents e ON e.Id = x.event_id;';

        -- audit_events.user_id has no foreign key, so nothing else would stop these being orphaned. By user rather than
        -- the list above, so an event that arrived since goes too - or, if metadata arrived with it, fails the delete
        -- and rolls everything back rather than orphan it.
        DELETE FROM dbo.audit_events WHERE user_id = @UserId;

        -------------------------------------------------
        -- Web traffic: clicked elements hang off hits, hits and searches off sessions (none of it cascades).
        -------------------------------------------------
        DELETE c
        FROM dbo.hits_clicked_elements c
        INNER JOIN dbo.hits h ON h.id = c.hit_id
        INNER JOIN #UserSessions s ON s.Id = h.session_id;

        DELETE h FROM dbo.hits h INNER JOIN #UserSessions s ON s.Id = h.session_id;

        INSERT INTO #ReleasedSearchTerms (Id)
        SELECT DISTINCT x.search_term_id
        FROM dbo.searches x INNER JOIN #UserSessions s ON s.Id = x.session_id
        WHERE x.search_term_id IS NOT NULL;

        DELETE x FROM dbo.searches x INNER JOIN #UserSessions s ON s.Id = x.session_id;
        DELETE FROM dbo.sessions WHERE user_id = @UserId;

        -------------------------------------------------
        -- Teams calls: other people's records of a call stay. The person becomes the anonymous Unknown User on a
        -- call they share with someone else; a call with nobody else identifiable left on it is deleted with
        -- everything under it, which is what the import stores for such a call. Their own feedback goes.
        -------------------------------------------------
        IF OBJECT_ID('tempdb..#UserCalls') IS NOT NULL DROP TABLE #UserCalls;
        CREATE TABLE #UserCalls (Id INT PRIMARY KEY);
        INSERT INTO #UserCalls (Id)
        SELECT r.id FROM dbo.call_records r WHERE r.organizer_id = @UserId
        UNION SELECT s.call_record_id FROM dbo.call_sessions s WHERE s.attendee_user_id = @UserId AND s.call_record_id IS NOT NULL
        UNION SELECT f.call_id FROM dbo.call_feedback f WHERE f.user_id = @UserId AND f.call_id IS NOT NULL;

        IF EXISTS (SELECT 1 FROM #UserCalls)
        BEGIN
            SELECT TOP (1) @unknown_user_id = id FROM dbo.users WHERE user_name = 'Unknown User' ORDER BY id;
            IF @unknown_user_id IS NULL
            BEGIN
                INSERT INTO dbo.users (user_name) VALUES ('Unknown User');
                SET @unknown_user_id = SCOPE_IDENTITY();
            END

            IF OBJECT_ID('tempdb..#DoomedCalls') IS NOT NULL DROP TABLE #DoomedCalls;
            CREATE TABLE #DoomedCalls (Id INT PRIMARY KEY);
            INSERT INTO #DoomedCalls (Id)
            SELECT k.Id
            FROM #UserCalls k
            INNER JOIN dbo.call_records r ON r.id = k.Id
            WHERE (r.organizer_id = @unknown_user_id OR r.organizer_id = @UserId)
              AND NOT EXISTS (SELECT 1 FROM dbo.call_sessions s
                              WHERE s.call_record_id = r.id
                                AND s.attendee_user_id <> @unknown_user_id
                                AND s.attendee_user_id <> @UserId);

            DELETE m
            FROM dbo.call_session_call_modalities m
            INNER JOIN dbo.call_sessions s ON s.id = m.call_session_id
            INNER JOIN #DoomedCalls d ON d.Id = s.call_record_id;
            DELETE s FROM dbo.call_sessions s INNER JOIN #DoomedCalls d ON d.Id = s.call_record_id;
            DELETE f FROM dbo.call_failures f INNER JOIN #DoomedCalls d ON d.Id = f.call_id;
            DELETE f FROM dbo.call_feedback f INNER JOIN #DoomedCalls d ON d.Id = f.call_id;
            DELETE r FROM dbo.call_records r INNER JOIN #DoomedCalls d ON d.Id = r.id;

            UPDATE dbo.call_records SET organizer_id = @unknown_user_id WHERE organizer_id = @UserId;
            UPDATE dbo.call_sessions SET attendee_user_id = @unknown_user_id WHERE attendee_user_id = @UserId;
        END

        DELETE FROM dbo.call_feedback WHERE user_id = @UserId;

        -------------------------------------------------
        -- Page comments and likes: replies by other people stay, no longer linked to the removed comment.
        -------------------------------------------------
        UPDATE r SET parent_id = NULL
        FROM dbo.page_comments r
        INNER JOIN dbo.page_comments p ON p.id = r.parent_id
        WHERE p.user_id = @UserId;

        DELETE FROM dbo.page_comments WHERE user_id = @UserId;
        DELETE FROM dbo.page_likes    WHERE user_id = @UserId;

        -------------------------------------------------
        -- Sent email: the recipients hang off the email.
        -------------------------------------------------
        INSERT INTO #ReleasedAddresses (Id)
        SELECT x.recipient_address_id
        FROM dbo.sent_email_recipients x INNER JOIN dbo.sent_emails m ON m.id = x.sent_email_id
        WHERE m.user_id = @UserId AND x.recipient_address_id IS NOT NULL
        UNION
        SELECT m.from_address_id FROM dbo.sent_emails m WHERE m.user_id = @UserId AND m.from_address_id IS NOT NULL;

        DELETE x
        FROM dbo.sent_email_recipients x INNER JOIN dbo.sent_emails m ON m.id = x.sent_email_id
        WHERE m.user_id = @UserId;
        DELETE FROM dbo.sent_emails WHERE user_id = @UserId;

        -------------------------------------------------
        -- Teams ownership, membership and reactions
        -------------------------------------------------
        DELETE FROM dbo.team_owners                  WHERE owner_id = @UserId;
        DELETE FROM dbo.team_membership_log          WHERE user_id = @UserId;
        DELETE FROM dbo.teams_user_channel_reactions WHERE user_id = @UserId;

        -------------------------------------------------
        -- Usage reports
        -------------------------------------------------
        DELETE FROM dbo.teams_user_activity_log      WHERE user_id = @UserId;
        DELETE FROM dbo.teams_user_device_usage_log  WHERE user_id = @UserId;
        DELETE FROM dbo.onedrive_usage_activity_log  WHERE user_id = @UserId;
        DELETE FROM dbo.onedrive_user_activity_log   WHERE user_id = @UserId;
        DELETE FROM dbo.outlook_user_activity_log    WHERE user_id = @UserId;
        DELETE FROM dbo.sharepoint_user_activity_log WHERE user_id = @UserId;
        DELETE FROM dbo.platform_user_activity_log   WHERE user_id = @UserId;
        DELETE FROM dbo.yammer_user_activity_log     WHERE user_id = @UserId;
        DELETE FROM dbo.yammer_device_activity_log   WHERE user_id = @UserId;

        -- Copilot usage reports (optional import, issue #286): Microsoft's own per-user Copilot figures.
        -- copilot_user_count_log is deliberately NOT touched - it is a tenant-level count per app and date.
        IF OBJECT_ID('dbo.copilot_usage_user_activity_log', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_usage_user_activity_log WHERE user_id = @UserId;
        IF OBJECT_ID('dbo.cowork_usage_user_activity_log', 'U') IS NOT NULL
            DELETE FROM dbo.cowork_usage_user_activity_log WHERE user_id = @UserId;

        -------------------------------------------------
        -- Copilot AI interaction history (optional import). Deleted explicitly rather than left to cascade:
        -- copilot_interactions.user_id is intentionally a non-cascading FK. Leaf -> parent order.
        -------------------------------------------------
        IF OBJECT_ID('dbo.copilot_interaction_keywords', 'U') IS NOT NULL
        BEGIN
            INSERT INTO #ReleasedKeywords (Id)
            SELECT DISTINCT k.keyword_id
            FROM dbo.copilot_interaction_keywords k INNER JOIN dbo.copilot_interactions i ON i.id = k.interaction_id
            WHERE i.user_id = @UserId AND k.keyword_id IS NOT NULL;

            DELETE k
            FROM dbo.copilot_interaction_keywords k INNER JOIN dbo.copilot_interactions i ON i.id = k.interaction_id
            WHERE i.user_id = @UserId;
        END

        IF OBJECT_ID('dbo.copilot_prompt_classifications', 'U') IS NOT NULL
            DELETE p
            FROM dbo.copilot_prompt_classifications p INNER JOIN dbo.copilot_interactions i ON i.id = p.interaction_id
            WHERE i.user_id = @UserId;

        IF OBJECT_ID('dbo.copilot_interactions', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_interactions WHERE user_id = @UserId;

        IF OBJECT_ID('dbo.copilot_interaction_sessions', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_interaction_sessions WHERE user_id = @UserId;

        IF OBJECT_ID('dbo.copilot_interaction_user_watermarks', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_interaction_user_watermarks WHERE user_id = @UserId;

        -------------------------------------------------
        -- Licences and credits
        -------------------------------------------------
        DELETE FROM dbo.user_license_history      WHERE user_id = @UserId;
        DELETE FROM dbo.user_license_type_lookups WHERE user_id = @UserId;

        -- Copilot Studio per-user credits, including rows the import could not link to a user row yet: those carry
        -- only the person's Microsoft Entra object id.
        IF OBJECT_ID('dbo.copilot_studio_credit_user_daily', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_studio_credit_user_daily
            WHERE user_id = @UserId
               OR (user_id IS NULL AND @azure_ad_id IS NOT NULL AND entra_object_id = @azure_ad_id);

        IF OBJECT_ID('dbo.copilot_adoption_reclaim_exclusions', 'U') IS NOT NULL
            DELETE FROM dbo.copilot_adoption_reclaim_exclusions WHERE user_id = @UserId;

        -------------------------------------------------
        -- Copilot credit history (preview). Keyed by Microsoft Entra object id: its user_id is a link the collector
        -- fills in, never a foreign key, so deleting the user would orphan these rather than fail. A service and an
        -- unevaluated service hang off their day's observation.
        -------------------------------------------------
        IF @object_id IS NOT NULL AND OBJECT_ID('dbo.copilot_cost_observations', 'U') IS NOT NULL
        BEGIN
            DELETE s
            FROM dbo.copilot_cost_services s
            INNER JOIN dbo.copilot_cost_observations o ON o.observation_date = s.observation_date AND o.object_id = s.object_id
            WHERE o.object_id = @object_id;

            DELETE s
            FROM dbo.copilot_cost_unevaluated s
            INNER JOIN dbo.copilot_cost_observations o ON o.observation_date = s.observation_date AND o.object_id = s.object_id
            WHERE o.object_id = @object_id;

            DELETE FROM dbo.copilot_cost_observations WHERE object_id = @object_id;
            DELETE FROM dbo.copilot_cost_history      WHERE object_id = @object_id;

            -- One work row per run: sought run by run on the (run_id, object_id) key.
            DELETE w
            FROM dbo.copilot_cost_runs r
            INNER JOIN dbo.copilot_cost_work w ON w.run_id = r.run_id AND w.object_id = @object_id;
        END

        -------------------------------------------------
        -- Retired features whose tables an upgrade kept because they still held data
        -------------------------------------------------
        -- Teams add-on installs (DeprecateTeamsAddons).
        IF OBJECT_ID('dbo.teams_addons_user_installed_log', 'U') IS NOT NULL
            EXEC sp_executesql N'DELETE FROM dbo.teams_addons_user_installed_log WHERE user_id = @UserId;', N'@UserId INT', @UserId = @UserId;

        -- Viva Engage (Yammer) messages from the long-retired message import (RetireUnusedAuditYammerStreamTables;
        -- the Viva Engage usage reports are a different import). Replies by other people stay, no longer linked to
        -- the removed message. The link table can have been dropped on its own.
        IF OBJECT_ID('dbo.yammer_messages', 'U') IS NOT NULL
            EXEC sp_executesql N'
                UPDATE r SET parent_msg_id = NULL
                FROM dbo.yammer_messages r
                INNER JOIN dbo.yammer_messages p ON p.id = r.parent_msg_id
                WHERE p.sender_id = @UserId;

                IF OBJECT_ID(''dbo.yammer_msg_to_stream'', ''U'') IS NOT NULL
                    DELETE l
                    FROM dbo.yammer_msg_to_stream l
                    INNER JOIN dbo.yammer_messages m ON m.id = l.message_id
                    WHERE m.sender_id = @UserId;

                DELETE FROM dbo.yammer_messages WHERE sender_id = @UserId;', N'@UserId INT', @UserId = @UserId;

        -------------------------------------------------
        -- Someone else's records that name the person
        -------------------------------------------------
        -- Shares made to them stay with whoever shared, without a recipient.
        UPDATE dbo.event_meta_power_app_share           SET shared_with_user_id = NULL WHERE shared_with_user_id = @UserId;
        UPDATE dbo.event_meta_power_automate_flow_share SET shared_with_user_id = NULL WHERE shared_with_user_id = @UserId;

        -- The people they managed keep their record, without a manager.
        UPDATE dbo.users SET manager_id = NULL WHERE manager_id = @UserId;

        -- Their user-organisation values.
        IF OBJECT_ID('dbo.user_org_assignments', 'U') IS NOT NULL
            DELETE FROM dbo.user_org_assignments WHERE user_id = @UserId;

        -------------------------------------------------
        -- Shared vocabulary only the person used: search text, email addresses and Copilot prompt phrases (a phrase
        -- can be a whole short prompt). Only rows nothing refers to any more go. If an importer starts using one
        -- while this runs, the foreign key refuses that delete and the row stays; the rest of the erasure stands.
        -------------------------------------------------
        SET XACT_ABORT OFF;
        BEGIN TRY
            DELETE l
            FROM dbo.search_terms l INNER JOIN #ReleasedSearchTerms r ON r.Id = l.id
            WHERE NOT EXISTS (SELECT 1 FROM dbo.searches x WHERE x.search_term_id = l.id);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() <> 547 OR XACT_STATE() <> 1
            BEGIN
                ;THROW;
            END
        END CATCH

        BEGIN TRY
            DELETE l
            FROM dbo.email_addresses l INNER JOIN #ReleasedAddresses r ON r.Id = l.id
            WHERE NOT EXISTS (SELECT 1 FROM dbo.sent_emails x WHERE x.from_address_id = l.id)
              AND NOT EXISTS (SELECT 1 FROM dbo.sent_email_recipients x WHERE x.recipient_address_id = l.id);
        END TRY
        BEGIN CATCH
            IF ERROR_NUMBER() <> 547 OR XACT_STATE() <> 1
            BEGIN
                ;THROW;
            END
        END CATCH

        IF OBJECT_ID('dbo.copilot_interaction_keywords', 'U') IS NOT NULL
        BEGIN
            BEGIN TRY
                DELETE l
                FROM dbo.keywords l INNER JOIN #ReleasedKeywords r ON r.Id = l.id
                WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_interaction_keywords x WHERE x.keyword_id = l.id)
                  AND NOT EXISTS (SELECT 1 FROM dbo.teams_channel_stats_log_keywords x WHERE x.keyword_id = l.id);
            END TRY
            BEGIN CATCH
                IF ERROR_NUMBER() <> 547 OR XACT_STATE() <> 1
                BEGIN
                    ;THROW;
                END
            END CATCH
        END
        SET XACT_ABORT ON;

        -------------------------------------------------
        -- Finally the user
        -------------------------------------------------
        DELETE FROM dbo.users WHERE id = @UserId;

        IF @had_outer_tx = 0
            COMMIT TRANSACTION;

        RETURN @rc; -- 0
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 AND @had_outer_tx = 0
            ROLLBACK TRANSACTION;

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
