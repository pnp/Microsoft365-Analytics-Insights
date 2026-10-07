-- ================================================================================================
-- Optional per-step timing. ZERO overhead unless a diagnostics table dbo.copilot_merge_step_timings
-- exists (see copilot_merge_step_timings.sql). Create it to profile this shared merge on a live
-- system, let a few import cycles run, query the summary in that script, then DROP the table to turn
-- instrumentation back off. Safe to ship: with the table absent, @dbg is 0 and nothing is written.
-- ================================================================================================
DECLARE @dbg BIT = CASE WHEN OBJECT_ID('dbo.copilot_merge_step_timings','U') IS NOT NULL THEN 1 ELSE 0 END;
DECLARE @batch_id UNIQUEIDENTIFIER = NEWID();
DECLARE @t DATETIME2(7) = SYSUTCDATETIME();
DECLARE @t0 DATETIME2(7) = @t;
DECLARE @rows INT = 0;

-- ================================================================================================
-- Agents (dbo.copilot_agents): one row per agent_id, kept up to date from each batch. Issue #699.
--
-- 1. The batch is collapsed to ONE row per agent_id before copilot_agents is touched. Inserting
--    straight from the staging rows (SELECT DISTINCT name, agent_id, flag) gave a new agent one row
--    per distinct name in the batch, a NULL name included, and agent_id is nvarchar(max), so no unique
--    index can stop it. A Copilot Studio agent would hit that every time: its runtime record (no name)
--    and its Microsoft 365 Copilot record (display name) resolve to the same agent_id.
-- 2. Name. A display name (agent_name, from AgentName / TargetAgentName) names an unnamed agent, and
--    replaces a different stored name when every record in the batch agrees on it. A batch whose
--    records disagree leaves the stored name alone, so two spellings of one agent can't flip it back and
--    forth on every batch. agent_fallback_name (a Copilot Studio schema name parsed from AppIdentity,
--    for records that carry no name) only ever fills a NULL name, so it never replaces a display name.
--    Nothing here sets a name to NULL. Comparisons are NULL-safe: "agent_name <> name" is UNKNOWN for a
--    NULL stored name, which is why an agent first seen without a name used to stay unnamed for good.
-- 3. is_custom_agent. A non-NULL flag replaces a different stored one (true wins within a batch). Name
--    and flag are updated independently: each used to be set from a subquery that returned NULL
--    whenever only the OTHER column had changed, wiping it.
-- 4. Names are trimmed to the column's 100 characters, so an over-long name can't fail the batch.
-- ================================================================================================
SELECT
	imports.agent_id,
	MIN(LEFT(NULLIF(imports.agent_name, N''), 100)) AS name_min,
	MAX(LEFT(NULLIF(imports.agent_name, N''), 100)) AS name_max,
	MIN(LEFT(NULLIF(imports.agent_fallback_name, N''), 100)) AS fallback_name,
	CAST(MAX(CAST(imports.is_custom_agent AS tinyint)) AS bit) AS is_custom_agent
INTO #batch_agents
FROM [${STAGING_TABLE_ACTIVITY}] imports
WHERE imports.agent_id IS NOT NULL
GROUP BY imports.agent_id;

INSERT INTO dbo.copilot_agents ([name], [agent_id], [is_custom_agent])
SELECT COALESCE(b.name_min, b.fallback_name), b.agent_id, b.is_custom_agent
FROM #batch_agents AS b
WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_agents AS ca WHERE ca.[agent_id] = b.agent_id);


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_agents', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

UPDATE ca
SET [name] = n.new_name,
	[is_custom_agent] = COALESCE(b.is_custom_agent, ca.[is_custom_agent])
FROM dbo.copilot_agents AS ca
INNER JOIN #batch_agents AS b
	ON b.agent_id = ca.[agent_id]
CROSS APPLY (
	SELECT CASE
		WHEN b.name_min IS NULL THEN COALESCE(ca.[name], b.fallback_name)	-- no display name in the batch
		WHEN ca.[name] IS NULL THEN b.name_min								-- the first display name
		WHEN b.name_min = b.name_max THEN b.name_min						-- the batch agrees: take it
		ELSE ca.[name]														-- the batch disagrees: keep it
	END AS new_name
) AS n
WHERE (n.new_name IS NOT NULL AND (ca.[name] IS NULL OR n.new_name <> ca.[name]))
	OR (b.is_custom_agent IS NOT NULL AND (ca.[is_custom_agent] IS NULL OR b.is_custom_agent <> ca.[is_custom_agent]));


SET @rows = @@ROWCOUNT;
DROP TABLE #batch_agents;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'update_agents', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Insert chat where there is no existing copilot_chats record for the event_id
-- Uses ROW_NUMBER to deduplicate staging table rows with the same event_id
-- thread_id / client_region / copilot_log_version are LEFT()-trimmed to their target column widths.
-- The staging columns are nvarchar(max) on purpose (see StagingClasses.cs): a bounded staging column
-- makes InsertBatch drop the entire row, and losing a whole interaction because a thread id was long
-- is worse than truncating the id.
--
-- user_id / time_stamp are DENORMALISED copies of the parent audit event's columns. A Copilot
-- interaction has no date of its own, so without them every Copilot report has to join
-- copilot_chats -> audit_events, the largest table in the product and clustered on a random GUID.
-- See migration DenormaliseCopilotChatUserAndTime for the measurements.
--
-- Sourced from dbo.audit_events rather than from the staging table because the staging table does not
-- carry the user or the timestamp (see StagingClasses.cs), and because audit_events is the single
-- source of truth - reading it here makes drift between the two copies impossible by construction.
-- The audit event is always already present: copilot_chats.event_id has a FOREIGN KEY to
-- audit_events.id, so a missing parent could not be inserted at all.
--
-- LEFT (not INNER) JOIN on purpose: an INNER JOIN would silently DROP a chat whose audit event was
-- missing, turning a loud foreign-key violation into invisible data loss. With LEFT JOIN the row is
-- still offered to the insert and the existing FK behaviour is preserved exactly.
--
-- The ROW_NUMBER orders by the agent row's id so that an agent_id which already has more than one
-- copilot_agents row (from before #699 stopped new ones) always resolves to its oldest row, rather than
-- to whichever one the plan happened to return first.
--
-- conversation_id is LEFT()-trimmed to its column width like thread_id. It is what pairs the two audit
-- records of one Microsoft 365 Copilot turn with a Copilot Studio agent - see "Turns counted once" below.
INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version, user_id, time_stamp, conversation_id)
SELECT event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version, user_id, time_stamp, conversation_id
FROM (
    SELECT
        i.event_id,
        i.app_host,
        ca.id AS agent_id,
        i.copilot_credit_estimate_total,
        i.copilot_credit_estimate_json,
        LEFT(i.thread_id, 450) AS thread_id,
        LEFT(i.client_region, 50) AS client_region,
        LEFT(i.copilot_log_version, 50) AS copilot_log_version,
        ae.user_id AS user_id,
        ae.time_stamp AS time_stamp,
        LEFT(i.conversation_id, 450) AS conversation_id,
        ROW_NUMBER() OVER (PARTITION BY i.event_id ORDER BY ca.id) AS rn
    FROM dbo.[${STAGING_TABLE_ACTIVITY}] AS i
    LEFT JOIN dbo.copilot_agents AS ca
        ON ca.agent_id = i.agent_id
    LEFT JOIN dbo.audit_events AS ae
        ON ae.id = i.event_id
    WHERE NOT EXISTS (
        SELECT 1
        FROM dbo.copilot_chats AS ec
        WHERE ec.event_id = i.event_id
    )
) AS deduped
WHERE rn = 1


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_chats', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Update existing chat records with Copilot Credit estimation data if not already present
UPDATE dbo.copilot_chats
SET 
    copilot_credit_estimate_total = i.copilot_credit_estimate_total,
    copilot_credit_estimate_json = i.copilot_credit_estimate_json
FROM dbo.copilot_chats AS ec
INNER JOIN dbo.[${STAGING_TABLE_ACTIVITY}] AS i
    ON ec.event_id = i.event_id
WHERE ec.copilot_credit_estimate_total IS NULL 
    AND i.copilot_credit_estimate_total IS NOT NULL;


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'update_chats', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- The conversation id of an agent interaction stored before it was kept (#699). The importer re-reads a
-- rolling look-back window, so an event an older build saved comes back through here and can still be
-- paired below. Agent interactions only: nothing else is paired, and it keeps this seek to agent rows.
UPDATE ec
SET ec.conversation_id = LEFT(i.conversation_id, 450)
FROM dbo.copilot_chats AS ec
INNER JOIN [${STAGING_TABLE_ACTIVITY}] AS i
    ON ec.event_id = i.event_id
WHERE ec.conversation_id IS NULL
  AND i.conversation_id IS NOT NULL
  AND i.agent_id IS NOT NULL;

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'update_conversations', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- ================================================================================================
-- Turns counted once (issue #699, phase 2).
--
-- A Copilot Studio agent's turn can be audited more than once, and every agent figure used to count
-- each record as an interaction:
--
--   * Microsoft 365 Copilot. The Copilot Studio runtime logs a record (app_host 'm365copilot', no
--     messages) and Microsoft 365 Copilot logs another one 3-8 s later (app_host 'Office' in every turn
--     seen, with the agent's name, the messages and the credit estimate). Same agent after
--     NormalizeAgentId, same user, same conversation_id, different thread ids. They alternate within a
--     conversation: runtime, client, runtime, client.
--   * Teams. Only the runtime logs (app_host 'Microsoft Teams'), but a turn has been seen to log an
--     extra runtime record a few seconds after the first. Its cause is unknown.
--
-- Both records are KEPT. The extra one gets a row in dbo.copilot_chat_duplicates naming the record its
-- turn is counted on, and every report that counts interactions leaves those rows out:
--
--   reason 1 - the runtime twin of a Microsoft 365 Copilot record. The client record is the one counted:
--              it carries the name, the messages and the credit estimate, and its app_host is the one
--              every other Microsoft 365 Copilot turn has. The runtime twin stays the evidence for its own
--              AccessedResources and DLP detail, which reports de-duplicate per turn.
--   reason 2 - an extra runtime record: logged within @echoSeconds of a runtime record of the same agent,
--              user, conversation and app_host. Its turn is counted on that record, or on the client
--              record that record is paired with. A real second turn within @echoSeconds would also be
--              folded in; that needs the first answer, and the user's next prompt, inside 10 seconds.
--
-- Rules:
--   * Pairs are one-to-one, never many-to-one. Each unpaired client record proposes to the nearest
--     unpaired runtime record logged up to @pairWindowSeconds before it (or up to @pairLeadSeconds after
--     it, for clock skew, only when none came before); each runtime record accepts the earliest proposal.
--     Rounds repeat until nothing new pairs, so rapid prompts (runtime, runtime, client, client) still
--     pair two-for-two. 120 s is fifteen times the longest gap seen (8 s), so a slow answer still pairs,
--     and the nearest-first rule keeps a short alternating conversation from pairing across turns.
--   * Only runtime records are ever marked. A client record, and a runtime record with no twin (Teams,
--     the Copilot Studio test pane, or a Microsoft 365 Copilot turn whose client record has not arrived
--     yet), always counts.
--   * An extra record (reason 2) can still be claimed as a pair twin later, which turns it into reason 1.
--     That is how a quick runtime record whose client record arrives in a later import cycle ends up
--     paired with the right twin. Nothing is ever un-marked, so the result only ever converges.
--   * Order and cycles don't matter: the records can arrive in one batch or in different import cycles,
--     in either order, and again on re-import. Whichever arrives second does the pairing, and a record
--     already marked, or a client record already paired, is never considered again.
--
-- Finding a turn's other records without a new index on copilot_chats: the staged agent interactions
-- (seeds) are turned into time ranges of +/- @turnReach seconds, overlapping ranges are merged, and each
-- merged range is ONE seek on IX_copilot_chats_time_stamp_user_id (time_stamp, user_id) INCLUDE
-- (app_host, agent_id), filtered on the seeds' (user_id, agent_id) inside the index. Only the rows that
-- survive need a key lookup for conversation_id. Restricted to agents keyed on a bare GUID (the Entra
-- Agent ID every Copilot Studio runtime record carries), so a tenant with no Copilot Studio agents
-- never runs the range seeks at all. Measured cost: see the pull request for #699.
--
-- Pairing is derived data, so a failure here must never fail the import that saved the interactions.
-- It is caught and logged to the optional step profiler, and the next import cycle that re-reads the
-- records pairs them.
-- ================================================================================================
IF OBJECT_ID('dbo.copilot_chat_duplicates', 'U') IS NOT NULL
BEGIN
BEGIN TRY
    DECLARE @pairWindowSeconds int = 120;
    DECLARE @pairLeadSeconds int = 5;
    DECLARE @echoSeconds int = 10;
    DECLARE @turnReach int = @pairWindowSeconds + @pairLeadSeconds + @echoSeconds;
    DECLARE @turnRound int = 0;

    -- The staged agent interactions that may have another record of the same turn.
    SELECT DISTINCT c.event_id, c.user_id, c.agent_id, c.conversation_id, c.time_stamp
    INTO #turn_seed
    FROM [${STAGING_TABLE_ACTIVITY}] AS i
    INNER JOIN dbo.copilot_chats AS c ON c.event_id = i.event_id
    INNER JOIN dbo.copilot_agents AS ag ON ag.id = c.agent_id
    WHERE i.agent_id IS NOT NULL
      AND i.conversation_id IS NOT NULL
      AND c.user_id IS NOT NULL
      AND c.conversation_id IS NOT NULL
      -- Keeps DATEADD below clear of the datetime range on a nonsense timestamp.
      AND c.time_stamp >= '20000101' AND c.time_stamp < '99990101'
      AND LEN(ag.agent_id) = 36
      AND TRY_CONVERT(uniqueidentifier, ag.agent_id) IS NOT NULL;

    SET @rows = 0;

    IF EXISTS (SELECT 1 FROM #turn_seed)
    BEGIN
        -- Merged time ranges around the seeds (gaps and islands), one index seek each.
        ;WITH bounds AS (
            SELECT DATEADD(SECOND, -@turnReach, time_stamp) AS lo, DATEADD(SECOND, @turnReach, time_stamp) AS hi
            FROM #turn_seed
        ), ordered AS (
            SELECT lo, hi, MAX(hi) OVER (ORDER BY lo, hi ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS prev_hi
            FROM bounds
        ), islands AS (
            SELECT lo, hi, SUM(CASE WHEN prev_hi >= lo THEN 0 ELSE 1 END) OVER (ORDER BY lo, hi ROWS UNBOUNDED PRECEDING) AS island
            FROM ordered
        )
        SELECT MIN(lo) AS lo, MAX(hi) AS hi
        INTO #turn_ranges
        FROM islands
        GROUP BY island;

        -- Every interaction of the seeds' users with the seeds' agents inside those ranges, read from
        -- IX_copilot_chats_time_stamp_user_id alone. host_kind: 1 the Microsoft 365 Copilot runtime record,
        -- 2 the Teams runtime record, 3 the Copilot Studio test pane, 0 a record from the Copilot client.
        SELECT c.event_id, c.user_id, c.agent_id, c.time_stamp,
               CAST(CASE WHEN c.app_host = N'm365copilot' THEN 1
                         WHEN c.app_host = N'Microsoft Teams' THEN 2
                         WHEN c.app_host = N'Copilot Studio' THEN 3
                         ELSE 0 END AS tinyint) AS host_kind
        INTO #turn_near
        FROM #turn_ranges AS r
        INNER JOIN dbo.copilot_chats AS c
            ON c.time_stamp >= r.lo AND c.time_stamp <= r.hi
        WHERE EXISTS (SELECT 1 FROM #turn_seed AS s WHERE s.user_id = c.user_id AND s.agent_id = c.agent_id)
        OPTION (RECOMPILE);

        -- ...narrowed to the seeds' conversations. The only key lookups in the pairing.
        SELECT DISTINCT n.event_id, n.user_id, n.agent_id, n.time_stamp, n.host_kind, c.conversation_id
        INTO #turn_rows
        FROM #turn_near AS n
        INNER JOIN dbo.copilot_chats AS c ON c.event_id = n.event_id
        WHERE EXISTS (SELECT 1 FROM #turn_seed AS s
                      WHERE s.user_id = n.user_id AND s.agent_id = n.agent_id AND s.conversation_id = c.conversation_id)
        OPTION (RECOMPILE);

        -- Runtime records not yet a pair twin. An extra record (reason 2) can still be claimed.
        SELECT t.event_id, t.user_id, t.agent_id, t.conversation_id, t.time_stamp
        INTO #turn_runtime
        FROM #turn_rows AS t
        WHERE t.host_kind = 1
          AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.event_id = t.event_id AND d.reason = 1);

        -- Client records not yet paired.
        SELECT t.event_id, t.user_id, t.agent_id, t.conversation_id, t.time_stamp
        INTO #turn_client
        FROM #turn_rows AS t
        WHERE t.host_kind = 0
          AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.counted_event_id = t.event_id AND d.reason = 1)
          AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.event_id = t.event_id);

        CREATE TABLE #turn_pairs (runtime_event_id uniqueidentifier NOT NULL PRIMARY KEY, client_event_id uniqueidentifier NOT NULL UNIQUE);

        WHILE @turnRound < 50 AND EXISTS (SELECT 1 FROM #turn_runtime) AND EXISTS (SELECT 1 FROM #turn_client)
        BEGIN
            SET @turnRound += 1;

            ;WITH proposals AS (
                SELECT cl.event_id AS client_event_id,
                       rt.event_id AS runtime_event_id,
                       cl.time_stamp AS client_time,
                       ROW_NUMBER() OVER (
                           PARTITION BY cl.event_id
                           ORDER BY CASE WHEN rt.time_stamp <= cl.time_stamp THEN 0 ELSE 1 END,
                                    ABS(DATEDIFF_BIG(MILLISECOND, rt.time_stamp, cl.time_stamp)),
                                    rt.event_id) AS preference
                FROM #turn_client AS cl
                INNER JOIN #turn_runtime AS rt
                    ON rt.user_id = cl.user_id
                   AND rt.agent_id = cl.agent_id
                   AND rt.conversation_id = cl.conversation_id
                   AND rt.time_stamp >= DATEADD(SECOND, -@pairWindowSeconds, cl.time_stamp)
                   AND rt.time_stamp <= DATEADD(SECOND, @pairLeadSeconds, cl.time_stamp)
                WHERE NOT EXISTS (SELECT 1 FROM #turn_pairs AS p WHERE p.client_event_id = cl.event_id)
                  AND NOT EXISTS (SELECT 1 FROM #turn_pairs AS p WHERE p.runtime_event_id = rt.event_id)
            ), accepted AS (
                SELECT client_event_id, runtime_event_id,
                       ROW_NUMBER() OVER (PARTITION BY runtime_event_id ORDER BY client_time, client_event_id) AS acceptance
                FROM proposals
                WHERE preference = 1
            )
            INSERT INTO #turn_pairs (runtime_event_id, client_event_id)
            SELECT runtime_event_id, client_event_id
            FROM accepted
            WHERE acceptance = 1;

            IF @@ROWCOUNT = 0 BREAK;
        END

        -- Record the pairs. A runtime record already marked as an extra record becomes a pair twin.
        UPDATE d
        SET d.counted_event_id = p.client_event_id, d.reason = 1
        FROM dbo.copilot_chat_duplicates AS d
        INNER JOIN #turn_pairs AS p ON p.runtime_event_id = d.event_id;

        INSERT INTO dbo.copilot_chat_duplicates (event_id, counted_event_id, reason)
        SELECT p.runtime_event_id, p.client_event_id, 1
        FROM #turn_pairs AS p
        WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.event_id = p.runtime_event_id);

        SET @rows = (SELECT COUNT(*) FROM #turn_pairs);

        -- An extra record whose turn was counted on a runtime record that is now a pair twin: its turn is
        -- now counted on that record's client twin.
        UPDATE d
        SET d.counted_event_id = p.client_event_id
        FROM dbo.copilot_chat_duplicates AS d
        INNER JOIN #turn_pairs AS p ON p.runtime_event_id = d.counted_event_id
        WHERE d.reason = 2;

        -- Extra runtime records. A burst is a run of runtime records of one agent, user, conversation and
        -- app_host, each within @echoSeconds of the one before. A record in a burst that already holds a
        -- pair twin belongs to the nearest twin's turn; otherwise every record after the burst's first
        -- belongs to the first one's turn. Only records that still count are marked.
        ;WITH runtime AS (
            SELECT t.event_id, t.user_id, t.agent_id, t.conversation_id, t.host_kind, t.time_stamp,
                   d.reason, d.counted_event_id,
                   LAG(t.time_stamp) OVER (PARTITION BY t.user_id, t.agent_id, t.conversation_id, t.host_kind
                                           ORDER BY t.time_stamp, t.event_id) AS previous_time
            FROM #turn_rows AS t
            LEFT JOIN dbo.copilot_chat_duplicates AS d ON d.event_id = t.event_id
            WHERE t.host_kind IN (1, 2, 3)
        ), runs AS (
            SELECT *,
                   SUM(CASE WHEN previous_time IS NULL
                              OR DATEDIFF_BIG(MILLISECOND, previous_time, time_stamp) > @echoSeconds * 1000
                            THEN 1 ELSE 0 END)
                       OVER (PARTITION BY user_id, agent_id, conversation_id, host_kind
                             ORDER BY time_stamp, event_id ROWS UNBOUNDED PRECEDING) AS burst,
                   ROW_NUMBER() OVER (PARTITION BY user_id, agent_id, conversation_id, host_kind
                                      ORDER BY time_stamp, event_id) AS position
            FROM runtime
        ), bursts AS (
            SELECT *,
                   MIN(position) OVER (PARTITION BY user_id, agent_id, conversation_id, host_kind, burst) AS first_position
            FROM runs
        )
        SELECT x.event_id,
               COALESCE(twin.counted_event_id,
                        CASE WHEN first_record.reason = 2 THEN first_record.counted_event_id ELSE first_record.event_id END) AS counted_event_id
        INTO #turn_extra
        FROM bursts AS x
        OUTER APPLY (
            SELECT TOP (1) b.counted_event_id
            FROM bursts AS b
            WHERE b.user_id = x.user_id AND b.agent_id = x.agent_id AND b.conversation_id = x.conversation_id
              AND b.host_kind = x.host_kind AND b.burst = x.burst
              AND b.reason = 1
            ORDER BY ABS(DATEDIFF_BIG(MILLISECOND, b.time_stamp, x.time_stamp)), b.event_id
        ) AS twin
        OUTER APPLY (
            SELECT TOP (1) b.event_id, b.reason, b.counted_event_id
            FROM bursts AS b
            WHERE b.user_id = x.user_id AND b.agent_id = x.agent_id AND b.conversation_id = x.conversation_id
              AND b.host_kind = x.host_kind AND b.burst = x.burst
              AND b.position = x.first_position
        ) AS first_record
        WHERE x.reason IS NULL
          AND (twin.counted_event_id IS NOT NULL OR x.position > x.first_position);

        INSERT INTO dbo.copilot_chat_duplicates (event_id, counted_event_id, reason)
        SELECT e.event_id, e.counted_event_id, 2
        FROM #turn_extra AS e
        WHERE e.counted_event_id IS NOT NULL
          AND e.counted_event_id <> e.event_id
          AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d WHERE d.event_id = e.event_id);

        SET @rows = @rows + @@ROWCOUNT;

        -- A Copilot DLP match both records of a pair carry is the same block: keep it on the counted record.
        -- Only reaches rows stored before the pair was found (records saved by an older build, or a pairing
        -- that failed and was retried); insert_copilot_dlp_events_from_staging_table.sql stops a pair's later
        -- record adding the copy in the first place.
        IF OBJECT_ID('dbo.copilot_dlp_events', 'U') IS NOT NULL
        BEGIN
            DELETE x
            FROM dbo.copilot_dlp_events AS x
            INNER JOIN #turn_pairs AS p ON p.runtime_event_id = x.copilot_chat_id
            WHERE EXISTS (
                SELECT 1
                FROM dbo.copilot_dlp_events AS y
                WHERE y.copilot_chat_id = p.client_event_id
                  AND EXISTS (
                      SELECT y.dlp_policy_id, y.dlp_rule_id, y.dlp_action_id, y.resource_name_id, y.resource_type_id, y.sensitivity_label_id, y.is_blocked
                      INTERSECT
                      SELECT x.dlp_policy_id, x.dlp_rule_id, x.dlp_action_id, x.resource_name_id, x.resource_type_id, x.sensitivity_label_id, x.is_blocked
                  )
            );
        END

        DROP TABLE #turn_extra;
        DROP TABLE #turn_pairs;
        DROP TABLE #turn_client;
        DROP TABLE #turn_runtime;
        DROP TABLE #turn_rows;
        DROP TABLE #turn_near;
        DROP TABLE #turn_ranges;
    END

    DROP TABLE #turn_seed;

    IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
        VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'pair_turns', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
END TRY
BEGIN CATCH
    IF OBJECT_ID('tempdb..#turn_extra') IS NOT NULL DROP TABLE #turn_extra;
    IF OBJECT_ID('tempdb..#turn_pairs') IS NOT NULL DROP TABLE #turn_pairs;
    IF OBJECT_ID('tempdb..#turn_client') IS NOT NULL DROP TABLE #turn_client;
    IF OBJECT_ID('tempdb..#turn_runtime') IS NOT NULL DROP TABLE #turn_runtime;
    IF OBJECT_ID('tempdb..#turn_rows') IS NOT NULL DROP TABLE #turn_rows;
    IF OBJECT_ID('tempdb..#turn_near') IS NOT NULL DROP TABLE #turn_near;
    IF OBJECT_ID('tempdb..#turn_ranges') IS NOT NULL DROP TABLE #turn_ranges;
    IF OBJECT_ID('tempdb..#turn_seed') IS NOT NULL DROP TABLE #turn_seed;

    -- rows_affected carries the SQL error number, so a failing pairing shows up in the step profiler.
    IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
        VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'pair_turns_failed', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), ERROR_NUMBER());
END CATCH
END

SET @t = SYSUTCDATETIME();

-- Process AccessedResources only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_accessed_resource_ids', 'U') IS NOT NULL
BEGIN

-- Create indexes on lookup tables if they don't already exist (one-time, idempotent)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_copilot_resource_types_name' AND object_id = OBJECT_ID('dbo.copilot_event_accessed_resource_types'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_copilot_resource_types_name ON copilot_event_accessed_resource_types([name]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_sensitivity_labels_label_id' AND object_id = OBJECT_ID('dbo.sensitivity_labels'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_sensitivity_labels_label_id ON sensitivity_labels(label_id);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_copilot_resource_actions_name' AND object_id = OBJECT_ID('dbo.copilot_event_accessed_resource_actions'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_copilot_resource_actions_name ON copilot_event_accessed_resource_actions([name]);

-- Parse JSON once into a temp table to avoid redundant OPENJSON/JSON_VALUE across 6 passes.
-- resource_id / resource_name / site_url are trimmed to 850 chars to match their lookup columns
-- (nvarchar(850) after migration IndexCopilotAccessedResourceLookups, so they can be indexed - the
-- join/de-dup keys below). Trimming here keeps the value consistent between the DISTINCT insert and the
-- resolve join so de-duplication still works, and guarantees no over-width value hits the narrowed column.
-- site_url is additionally NORMALISED to its path (everything before the first '?' or '#' is kept, the
-- query string / #fragment dropped) BEFORE de-dup: the Copilot audit SiteUrl carries a volatile per-access
-- token (e.g. xsdata), so without this the same site is a near-unique string every access and the
-- site_urls dimension balloons to millions of rows (one per access) instead of one per site. Mirrors
-- StringUtils.RemoveXsDataParam / EnsureUrlWithinLength's "reduce to path" step. See issue #122.
SELECT 
    imports.event_id,
    LEFT(JSON_VALUE(ar.value, '$.Id'), 850) AS resource_id,
    LEFT(JSON_VALUE(ar.value, '$.Name'), 850) AS resource_name,
    LEFT(np.site_url_path, 850) AS site_url,
    JSON_VALUE(ar.value, '$.Type') AS resource_type,
    JSON_VALUE(ar.value, '$.SensitivityLabelId') AS sensitivity_label_id,
    -- Action is a tiny value set ("Read", ...) so it is dimensioned like resource_type rather than
    -- stored inline on the (largest Copilot) junction table.
    LEFT(JSON_VALUE(ar.value, '$.Action'), 100) AS resource_action,
    -- listItemUniqueId is an opaque resource identifier from the same value domain as Id - the audit
    -- payload frequently repeats Id verbatim here - so it is resolved against the SAME
    -- copilot_event_accessed_resource_ids dimension and trimmed to the same 850 chars.
    LEFT(JSON_VALUE(ar.value, '$.listItemUniqueId'), 850) AS list_item_unique_id,
    -- XPIADetected: was a Cross-Prompt Injection Attack detected from this resource. Arrives as the JSON
    -- literal 'true'/'false', which CONVERT(bit, ...) cannot parse, hence the explicit CASE. Kept as
    -- tinyint here so it can be MAX()'d during resolve (SQL Server has no MAX over bit).
    CASE JSON_VALUE(ar.value, '$.XPIADetected') WHEN 'true' THEN CAST(1 AS tinyint) WHEN 'false' THEN CAST(0 AS tinyint) ELSE NULL END AS xpia_detected
INTO #parsed_accessed_resources
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
CROSS APPLY (SELECT JSON_VALUE(ar.value, '$.SiteUrl') AS raw_site_url) rs
CROSS APPLY (
    -- Keep just the path: strip from the first '?' or '#' onward. NULLIF turns CHARINDEX's "not found"
    -- (0) into NULL so MIN ignores it; when neither is present the whole value is kept.
    SELECT CASE WHEN rs.raw_site_url IS NULL THEN NULL
                ELSE LEFT(rs.raw_site_url,
                          ISNULL((SELECT MIN(pos) FROM (VALUES
                                     (NULLIF(CHARINDEX('?', rs.raw_site_url), 0)),
                                     (NULLIF(CHARINDEX('#', rs.raw_site_url), 0))) q(pos)) - 1,
                                  LEN(rs.raw_site_url)))
           END AS site_url_path
) np
WHERE imports.accessed_resources_json IS NOT NULL;


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'parse_resources', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique resource IDs.
-- Unchanged from before: the Id column on its own.
INSERT INTO copilot_event_accessed_resource_ids (resource_id)
SELECT DISTINCT par.resource_id
FROM #parsed_accessed_resources par
WHERE par.resource_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_ids ari 
    WHERE ari.resource_id = par.resource_id
  );

SET @rows = @@ROWCOUNT;

-- listItemUniqueId is the same kind of opaque resource identifier as Id and lands in the SAME
-- dimension, so the two de-duplicate against each other instead of storing the string twice on the
-- largest Copilot table. Kept as a SEPARATE statement rather than UNIONed into the one above: the
-- payload usually repeats Id verbatim as listItemUniqueId, so the "<> resource_id" filter leaves this
-- statement with nothing to do in the common case (measured ~15 ms), whereas folding both columns
-- into one derived table changed the plan of the original statement and cost ~8x more.
INSERT INTO copilot_event_accessed_resource_ids (resource_id)
SELECT DISTINCT par.list_item_unique_id
FROM #parsed_accessed_resources par
WHERE par.list_item_unique_id IS NOT NULL
  AND (par.resource_id IS NULL OR par.list_item_unique_id <> par.resource_id)
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resource_ids ari
    WHERE ari.resource_id = par.list_item_unique_id
  );

-- Sum of both statements above (the second is normally a no-op), so the profiler reports the whole step.
SET @rows = @rows + @@ROWCOUNT;

IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_ids', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique resource names
INSERT INTO copilot_event_accessed_resource_names ([name])
SELECT DISTINCT par.resource_name
FROM #parsed_accessed_resources par
WHERE par.resource_name IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_names arn 
    WHERE arn.[name] = par.resource_name
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_names', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique resource site URLs
INSERT INTO copilot_event_accessed_resource_site_urls (site_url)
SELECT DISTINCT par.site_url
FROM #parsed_accessed_resources par
WHERE par.site_url IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_site_urls arsu 
    WHERE arsu.site_url = par.site_url
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_site_urls', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique resource types
INSERT INTO copilot_event_accessed_resource_types ([name])
SELECT DISTINCT par.resource_type
FROM #parsed_accessed_resources par
WHERE par.resource_type IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_types art 
    WHERE art.[name] = par.resource_type
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_types', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique sensitivity labels
INSERT INTO sensitivity_labels (label_id)
SELECT DISTINCT par.sensitivity_label_id
FROM #parsed_accessed_resources par
WHERE par.sensitivity_label_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM sensitivity_labels sl 
    WHERE sl.label_id = par.sensitivity_label_id
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_labels', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert unique resource actions (e.g. "Read").
INSERT INTO copilot_event_accessed_resource_actions ([name])
SELECT DISTINCT par.resource_action
FROM #parsed_accessed_resources par
WHERE par.resource_action IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resource_actions ara
    WHERE ara.[name] = par.resource_action
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_actions', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Resolve lookup IDs once into a second temp table for the junction insert.
--
-- DISTINCT over the WHOLE resolved tuple - including action_id and list_item_unique_id_id. This used to
-- GROUP BY only the five resource columns and pick the two extra columns with independent MIN()s, which
-- was wrong twice over (issue #287):
--
--   * It DROPPED actions. The same resource accessed twice in one interaction with different actions
--     (Read then Write) collapsed to a single row keeping the lower id, discarding the other - which is
--     exactly what persisting Action was added to stop (#262).
--   * It FABRICATED pairings. MIN(raction.id) and MIN(rlistitem.id) are evaluated independently over the
--     group, so the surviving row could pair an action taken from one source row with a list-item id
--     taken from a different one - a combination that never occurred in the payload.
--
-- Taking the whole tuple as the identity means every surviving row is a combination that genuinely
-- appeared, and distinct actions each get their own row. The de-dup identity below is widened to match,
-- and IX_copilot_event_accessed_resources_dedup was widened with it (migration
-- WidenCopilotAccessedResourceDedupIndex) so the existence check still seeks the exact tuple. Both extra
-- columns are ints, so this adds 8 bytes to the index key - the earlier claim that list_item_unique_id_id
-- would breach the 1700-byte index-key limit was simply wrong (it is an int FK, not a URL).
--
-- xpia_detected is PAYLOAD, not identity: it is aggregated with MAX over the eight-column tuple rather
-- than joining it. DISTINCT became GROUP BY purely to allow that - the grouping columns are exactly the
-- columns the DISTINCT covered, so the resolved row set is unchanged and the dedup index needs no change.
--
-- This is NOT a repeat of the issue #287 mistake. That bug was two INDEPENDENT MIN()s (action and
-- list-item) over a group, which could pair an action from one source row with a list-item id from
-- another and fabricate a combination that never occurred. Fabrication requires two or more values being
-- aggregated independently and then read as a pair. Here exactly ONE value is aggregated and it is read
-- on its own, so there is nothing to mis-pair: MAX answers "was XPIA flagged on any occurrence of this
-- resource tuple in this batch", which is the question worth asking of a security signal. NULL (field
-- absent) is preserved because MAX ignores NULLs, so unknown stays distinguishable from an explicit false.
SELECT
    par.event_id,
    rid.id AS resource_id_id,
    rname.id AS resource_name_id,
    rsiteurl.id AS resource_site_url_id,
    rtype.id AS resource_type_id,
    slabel.id AS sensitivity_label_id,
    raction.id AS action_id,
    rlistitem.id AS list_item_unique_id_id,
    CONVERT(bit, MAX(par.xpia_detected)) AS xpia_detected
INTO #resolved_accessed_resources
FROM #parsed_accessed_resources par
LEFT JOIN copilot_event_accessed_resource_ids rid 
    ON rid.resource_id = par.resource_id
LEFT JOIN copilot_event_accessed_resource_names rname 
    ON rname.[name] = par.resource_name
LEFT JOIN copilot_event_accessed_resource_site_urls rsiteurl 
    ON rsiteurl.site_url = par.site_url
LEFT JOIN copilot_event_accessed_resource_types rtype 
    ON rtype.[name] = par.resource_type
LEFT JOIN sensitivity_labels slabel 
    ON slabel.label_id = par.sensitivity_label_id
LEFT JOIN copilot_event_accessed_resource_actions raction
    ON raction.[name] = par.resource_action
LEFT JOIN copilot_event_accessed_resource_ids rlistitem
    ON rlistitem.resource_id = par.list_item_unique_id
GROUP BY
    par.event_id,
    rid.id,
    rname.id,
    rsiteurl.id,
    rtype.id,
    slabel.id,
    raction.id,
    rlistitem.id;


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'resolve', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert junction table records linking events to accessed resources.
-- Keyed NOT EXISTS on copilot_chat_id (seeks via the composite dedup index to just this chat's existing
-- rows) instead of EXCEPT-ing against the WHOLE junction table, which forced a full scan/sort of the
-- entire (millions-of-rows) table every batch. The NULL-safe INTERSECT compares the resolved tuple
-- exactly like EXCEPT did (NULLs equal).
--
-- The de-dup tuple is the FULL seven-column resolved tuple, action_id and list_item_unique_id_id
-- included. They are part of the row's identity, not payload: the same document Read and then Written in
-- one interaction is two distinct facts, and collapsing them threw one away (issue #287). Matching on
-- the full tuple is also what stops a re-imported batch inserting a second copy of a row that only
-- differs in the columns the old tuple ignored - in steady state. Across the upgrade that added those
-- two columns it is NOT sufficient on its own; see the note on the second NOT EXISTS below.
--
-- IX_copilot_event_accessed_resources_dedup carries all seven as KEY columns (widened from five by
-- migration WidenCopilotAccessedResourceDedupIndex), so this remains an exact (chat_id, tuple) seek
-- rather than the O(resolved x table) rescan it was before CoverCopilotAccessedResourceDedup. Keep the
-- column list here and the index key in step.
--
-- A 6-key + INCLUDE(action_id, list_item_unique_id_id) index was measured as the alternative and
-- REJECTED. It ties the composite key on a small commit batch, and has FEWER logical reads on a large
-- one (10,904 against 63,883 at 20,000 resolved rows) - but it is 5.5x SLOWER in wall-clock (521 ms
-- against 94 ms), because the extra columns are only residual predicates so the optimiser abandons the
-- seek and hash-joins a full index scan instead. See the migration's doc comment for the full table.
-- The SECOND NOT EXISTS below handles the upgrade boundary, and is not redundant. Migration
-- CopilotDroppedAuditFields adds action_id and list_item_unique_id_id as NULLable with no backfill, so
-- every row written before that upgrade has NULL in both. A re-staged pre-upgrade event now resolves a
-- non-NULL action_id (the payload's Action, present on most accessed resources), and NULL-safe INTERSECT
-- correctly reports (.., NULL, NULL) as different from (.., <action>, ..) - so the full-tuple check alone
-- would insert a SECOND copy of a row that is already there. Re-staging is routine, not exotic: the
-- importer re-reads a rolling look-back window and the blob checkpoint is in-memory by default, so every
-- event imported in the days before the upgrade comes back through this merge afterwards. Those
-- duplicates would be permanent and would silently inflate every COUNT over this table.
--
-- So a stored row with NULL in both new columns is treated as matching on the five original columns.
-- That deliberately declines to add a second action for an event imported pre-upgrade: under the old
-- five-column tuple such an event only ever stored one row anyway, so this preserves what was recorded
-- rather than half-revising it. Both branches are keyed on copilot_chat_id, so both seek.
--
-- xpia_detected rides along as payload and is absent from BOTH existence checks, exactly like the row's
-- other non-identity content. The consequence is the same "first write wins" behaviour the two columns
-- above already have: a resource tuple already stored (from an earlier batch, or from before this
-- column existed) keeps its existing xpia_detected - NULL included - rather than being revised. New
-- interactions carry the value from their first insert. Historic rows can be backfilled out of
-- audit_events.event_data, which retains the raw payload; that is deliberately not done here.
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id, xpia_detected)
SELECT r.event_id, r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id,
       r.action_id, r.list_item_unique_id_id, r.xpia_detected
FROM #resolved_accessed_resources r
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resources x
    WHERE x.copilot_chat_id = r.event_id
      AND EXISTS (
          SELECT x.resource_id_id, x.resource_name_id, x.resource_site_url_id, x.resource_type_id, x.sensitivity_label_id, x.action_id, x.list_item_unique_id_id
          INTERSECT
          SELECT r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id, r.action_id, r.list_item_unique_id_id
      )
)
AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resources x
    WHERE x.copilot_chat_id = r.event_id
      AND x.action_id IS NULL
      AND x.list_item_unique_id_id IS NULL
      AND EXISTS (
          SELECT x.resource_id_id, x.resource_name_id, x.resource_site_url_id, x.resource_type_id, x.sensitivity_label_id
          INTERSECT
          SELECT r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id
      )
);

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_junction', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

DROP TABLE #parsed_accessed_resources;
DROP TABLE #resolved_accessed_resources;

END


-- Process Messages only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_messages', 'U') IS NOT NULL
BEGIN

-- Insert message records. Both prompts and responses are staged now (see SerializeMessages): Size is
-- only obtainable from the prompt row, and is_prompt would be a constant if prompts were dropped.
-- JSON_VALUE paths are case-sensitive and must match the JsonProperty names on the Message class.
-- isPrompt arrives as the JSON literal 'true'/'false', which CONVERT(bit, ...) cannot parse, hence the
-- explicit CASE; Size is Edm.Int64 so TRY_CONVERT protects against a non-numeric value.
--
-- De-duplicated on (chat, persisted message id), which this insert previously was NOT. One interaction can be
-- staged into TWO staging tables - a Teams chat context stages a chat-only row and a following file
-- context stages a SharePoint row - and each staging table runs this merge, so its messages were
-- inserted twice. The NOT EXISTS seeks the chat's handful of existing rows via IX_copilot_chat_id.
-- Messages with no Id use a deterministic fallback from the event id + prompt/response flag + size.
-- parsed_messages is already DISTINCT on exactly that tuple, so this preserves the one-attempt row set
-- while making a retry of the same audit event idempotent.
--
-- JailbreakDetected is AGGREGATED (MAX) over that same tuple rather than added to it. Adding it to the
-- DISTINCT would break the invariant the fallback id depends on: two otherwise-identical id-less messages
-- differing only in the jailbreak flag would become two rows sharing one generated persisted_message_id.
-- MAX keeps the row set byte-identical to before and answers "was a jailbreak flagged for this message",
-- with NULL preserved when the payload omits the field entirely (MAX ignores NULLs; all-NULL stays NULL).
--
-- KNOWN ONE-TIME UPGRADE EFFECT, accepted deliberately. Message.IsPrompt became bool? in the same change
-- that added jailbreak_detected. Before it, a payload omitting isPrompt deserialised to false and was
-- re-serialised into messages_json as "isPrompt": false, so this CASE produced 0 and the fallback id ended
-- ':0:'. It now serialises as null, so the CASE produces NULL and the fallback id ends ':u:'. For a message
-- that has BOTH no Id AND no isPrompt, a row stored before the upgrade therefore no longer matches the id
-- computed after it, and the NOT EXISTS below inserts a second row for the same logical message when the
-- importer re-stages that event inside its rolling look-back window. It is bounded (only that window, only
-- that intersection, once) and no report reads this table, so it is not worth a data-state guard here - and
-- the two rows are not equivalent anyway: the pre-upgrade row asserts is_prompt = 0, which is the very
-- mis-statement the nullability change exists to stop. Recorded so it is not rediscovered as a new defect.
;WITH parsed_messages AS (
    SELECT
        imports.event_id,
        JSON_VALUE(msg.value, '$.Id') AS message_id,
        TRY_CONVERT(bigint, JSON_VALUE(msg.value, '$.Size')) AS [size],
        CASE JSON_VALUE(msg.value, '$.isPrompt') WHEN 'true' THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END AS is_prompt,
        CONVERT(bit, MAX(CASE JSON_VALUE(msg.value, '$.JailbreakDetected') WHEN 'true' THEN 1 WHEN 'false' THEN 0 ELSE NULL END)) AS jailbreak_detected
    FROM [${STAGING_TABLE_ACTIVITY}] imports
    CROSS APPLY OPENJSON(imports.messages_json) msg
    WHERE imports.messages_json IS NOT NULL
    GROUP BY
        imports.event_id,
        JSON_VALUE(msg.value, '$.Id'),
        TRY_CONVERT(bigint, JSON_VALUE(msg.value, '$.Size')),
        CASE JSON_VALUE(msg.value, '$.isPrompt') WHEN 'true' THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END
),
resolved_messages AS (
    SELECT
        pm.event_id,
        COALESCE(
            pm.message_id,
            N'missing:' + CONVERT(nvarchar(36), pm.event_id)
                + N':' + COALESCE(CONVERT(nvarchar(1), pm.is_prompt), N'u')
                + N':' + COALESCE(CONVERT(nvarchar(20), pm.[size]), N'u')
        ) AS persisted_message_id,
        pm.[size],
        pm.is_prompt,
        pm.jailbreak_detected
    FROM parsed_messages pm
)
INSERT INTO copilot_event_messages (copilot_chat_id, message_id, [size], is_prompt, jailbreak_detected)
SELECT
    rm.event_id,
    rm.persisted_message_id,
    rm.[size],
    rm.is_prompt,
    rm.jailbreak_detected
FROM resolved_messages rm
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_event_messages x
    WHERE x.copilot_chat_id = rm.event_id
      AND x.message_id = rm.persisted_message_id
);

END


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_messages', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AI Model Transparency only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_ai_models', 'U') IS NOT NULL
BEGIN

-- Parse the model transparency JSON once instead of running OPENJSON twice (insert + link) over the
-- staging table. The dimension key is the whole (name, provider, version) tuple - the model version is
-- part of a model's identity for AI-transparency reporting, and a tuple key keeps the dimension
-- additive so no UPDATE pass over it is needed. Rows imported before this change keep NULL
-- provider/version and are matched by payloads that omit them (the INTERSECT below treats NULLs as
-- equal, so they are reused rather than duplicated).
SELECT DISTINCT
    imports.event_id,
    JSON_VALUE(models.value, '$.ModelName') AS model_name,
    LEFT(JSON_VALUE(models.value, '$.ModelProviderName'), 100) AS provider_name,
    LEFT(JSON_VALUE(models.value, '$.ModelVersion'), 100) AS model_version
INTO #parsed_ai_models
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.model_transparency_json) models
WHERE imports.model_transparency_json IS NOT NULL
  AND JSON_VALUE(models.value, '$.ModelName') IS NOT NULL;

-- Insert unique AI models from model_transparency_json
INSERT INTO copilot_ai_models ([name], provider_name, [version])
SELECT DISTINCT pm.model_name, pm.provider_name, pm.model_version
FROM #parsed_ai_models pm
WHERE NOT EXISTS (
    SELECT 1 
    FROM copilot_ai_models cam
    WHERE cam.[name] = pm.model_name
      AND EXISTS (
          SELECT cam.provider_name, cam.[version]
          INTERSECT
          SELECT pm.provider_name, pm.model_version
      )
  );


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_ai_models', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Link AI models to copilot chats via junction table
INSERT INTO copilot_event_ai_models (copilot_chat_id, model_id)
SELECT DISTINCT 
    pm.event_id,
    cam.id
FROM #parsed_ai_models pm
INNER JOIN copilot_ai_models cam 
    ON cam.[name] = pm.model_name
   AND EXISTS (
       SELECT cam.provider_name, cam.[version]
       INTERSECT
       SELECT pm.provider_name, pm.model_version
   )
WHERE NOT EXISTS (
    SELECT 1 
    FROM copilot_event_ai_models ceam 
    WHERE ceam.copilot_chat_id = pm.event_id
      AND ceam.model_id = cam.id
  );

-- Captured before DROP TABLE, which would otherwise reset @@ROWCOUNT to 0.
SET @rows = @@ROWCOUNT;

DROP TABLE #parsed_ai_models;

END

IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'link_ai_models', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- ================================================================================================
-- Interaction contexts (schema collection "Contexts"), only if the tables exist (after migration).
-- The file/meeting resolution in the importer only ever resolves the FIRST file or meeting context,
-- so everything else in this unordered collection used to be discarded. Here ALL of them are kept.
-- Rows are roughly interaction-sized (0-2 contexts per event), so this is a much smaller pass than
-- the accessed-resource one above.
-- ================================================================================================
IF OBJECT_ID('dbo.copilot_event_contexts', 'U') IS NOT NULL
BEGIN

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_copilot_context_types_name' AND object_id = OBJECT_ID('dbo.copilot_event_context_types'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_copilot_context_types_name ON copilot_event_context_types([name]);

-- Parse once. context_ref / container_id are LEFT()-trimmed to their column widths so an unusually
-- long value truncates instead of failing the whole batch insert.
SELECT
    imports.event_id,
    LEFT(JSON_VALUE(ctx.value, '$.Id'), 850) AS context_ref,
    LEFT(JSON_VALUE(ctx.value, '$.Type'), 100) AS context_type,
    LEFT(JSON_VALUE(ctx.value, '$.ContainerId'), 450) AS container_id
INTO #parsed_contexts
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.contexts_json) ctx
WHERE imports.contexts_json IS NOT NULL;

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'parse_contexts', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Context types are a tiny value set ("docx", "TeamsMeeting", ...), so they are dimensioned.
INSERT INTO copilot_event_context_types ([name])
SELECT DISTINCT pc.context_type
FROM #parsed_contexts pc
WHERE pc.context_type IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_context_types cct
    WHERE cct.[name] = pc.context_type
  );

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_context_types', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- De-dup exactly like the accessed-resource junction: seek the chat's existing contexts via the EF
-- foreign-key index on copilot_chat_id, then compare the tuple with a NULL-safe INTERSECT. No extra
-- composite index is needed here (unlike the accessed-resource junction) because an interaction has a
-- handful of contexts at most, so the seek returns a tiny row set and the tuple compare is a residual
-- on those few rows - and context_ref is too wide to be an index key alongside the chat id anyway.
INSERT INTO copilot_event_contexts (copilot_chat_id, context_ref, context_type_id, container_id)
SELECT DISTINCT pc.event_id, pc.context_ref, cct.id, pc.container_id
FROM #parsed_contexts pc
LEFT JOIN copilot_event_context_types cct
    ON cct.[name] = pc.context_type
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_event_contexts x
    WHERE x.copilot_chat_id = pc.event_id
      AND EXISTS (
          SELECT x.context_ref, x.context_type_id, x.container_id
          INTERSECT
          SELECT pc.context_ref, cct.id, pc.container_id
      )
);

-- Captured before DROP TABLE, which would otherwise reset @@ROWCOUNT to 0 and log this step as
-- having inserted nothing.
SET @rows = @@ROWCOUNT;

DROP TABLE #parsed_contexts;

END


IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_contexts', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- ================================================================================================
-- AI system plugins (schema collection "AISystemPlugin") - which plugins/connectors grounded the
-- answer. Lookup + junction, mirroring copilot_ai_models / copilot_event_ai_models.
-- ================================================================================================
IF OBJECT_ID('dbo.copilot_ai_system_plugins', 'U') IS NOT NULL
BEGIN

SELECT DISTINCT
    imports.event_id,
    LEFT(JSON_VALUE(plg.value, '$.Id'), 255) AS plugin_id,
    LEFT(JSON_VALUE(plg.value, '$.Name'), 255) AS plugin_name,
    LEFT(JSON_VALUE(plg.value, '$.Version'), 50) AS plugin_version
INTO #parsed_ai_system_plugins
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.ai_system_plugins_json) plg
WHERE imports.ai_system_plugins_json IS NOT NULL
  AND (JSON_VALUE(plg.value, '$.Id') IS NOT NULL OR JSON_VALUE(plg.value, '$.Name') IS NOT NULL);

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'parse_ai_system_plugins', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Keyed on the whole (plugin id, name, version) tuple with a NULL-safe INTERSECT, so a plugin version
-- bump adds a row rather than rewriting history, and payloads without a version reuse the existing row.
INSERT INTO copilot_ai_system_plugins (plugin_id, [name], [version])
SELECT DISTINCT pp.plugin_id, pp.plugin_name, pp.plugin_version
FROM #parsed_ai_system_plugins pp
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_ai_system_plugins asp
    WHERE EXISTS (
        SELECT asp.plugin_id, asp.[name], asp.[version]
        INTERSECT
        SELECT pp.plugin_id, pp.plugin_name, pp.plugin_version
    )
);

SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_ai_system_plugins', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

INSERT INTO copilot_event_ai_system_plugins (copilot_chat_id, ai_system_plugin_id)
SELECT DISTINCT pp.event_id, asp.id
FROM #parsed_ai_system_plugins pp
INNER JOIN copilot_ai_system_plugins asp
    ON EXISTS (
        SELECT asp.plugin_id, asp.[name], asp.[version]
        INTERSECT
        SELECT pp.plugin_id, pp.plugin_name, pp.plugin_version
    )
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_event_ai_system_plugins x
    WHERE x.copilot_chat_id = pp.event_id
      AND x.ai_system_plugin_id = asp.id
);

-- Captured before DROP TABLE, which would otherwise reset @@ROWCOUNT to 0.
SET @rows = @@ROWCOUNT;

DROP TABLE #parsed_ai_system_plugins;

END


IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'link_ai_system_plugins', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'TOTAL', DATEDIFF(MILLISECOND, @t0, SYSUTCDATETIME()), NULL);
