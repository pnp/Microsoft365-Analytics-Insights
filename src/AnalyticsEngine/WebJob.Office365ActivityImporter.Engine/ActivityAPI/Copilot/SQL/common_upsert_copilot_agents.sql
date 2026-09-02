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

-- Insert new agents
INSERT INTO copilot_agents([name], [agent_id], [is_custom_agent])
	SELECT distinct imports.agent_name, imports.agent_id, imports.is_custom_agent
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	left join copilot_agents on copilot_agents.[agent_id] = imports.[agent_id]
	where copilot_agents.[agent_id] is null and imports.[agent_id] is not null;


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'insert_agents', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Update agent names to the first value in imports.agent_name for matching agent_id
UPDATE copilot_agents
SET [name] = (
	SELECT TOP 1 imports.agent_name
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE copilot_agents.[agent_id] = imports.[agent_id]
	  AND imports.agent_name IS NOT NULL
	  AND imports.agent_name <> copilot_agents.[name]
	ORDER BY imports.agent_name
),
[is_custom_agent] = (
	SELECT TOP 1 imports.is_custom_agent
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE copilot_agents.[agent_id] = imports.[agent_id]
	  AND imports.is_custom_agent IS NOT NULL
	ORDER BY imports.agent_name
)
WHERE EXISTS (
	SELECT 1
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	WHERE copilot_agents.[agent_id] = imports.[agent_id]
	  AND (
		  (imports.agent_name IS NOT NULL AND imports.agent_name <> copilot_agents.[name])
		  OR (imports.is_custom_agent IS NOT NULL AND (copilot_agents.[is_custom_agent] IS NULL OR imports.is_custom_agent <> copilot_agents.[is_custom_agent]))
	  )
);


SET @rows = @@ROWCOUNT;
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
INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version, user_id, time_stamp)
SELECT event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version, user_id, time_stamp
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
        ROW_NUMBER() OVER (PARTITION BY i.event_id ORDER BY (SELECT NULL)) AS rn
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
    LEFT(JSON_VALUE(ar.value, '$.listItemUniqueId'), 850) AS list_item_unique_id
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
SELECT DISTINCT
    par.event_id,
    rid.id AS resource_id_id,
    rname.id AS resource_name_id,
    rsiteurl.id AS resource_site_url_id,
    rtype.id AS resource_type_id,
    slabel.id AS sensitivity_label_id,
    raction.id AS action_id,
    rlistitem.id AS list_item_unique_id_id
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
    ON rlistitem.resource_id = par.list_item_unique_id;


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
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id)
SELECT r.event_id, r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id,
       r.action_id, r.list_item_unique_id_id
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
;WITH parsed_messages AS (
    SELECT DISTINCT
        imports.event_id,
        JSON_VALUE(msg.value, '$.Id') AS message_id,
        TRY_CONVERT(bigint, JSON_VALUE(msg.value, '$.Size')) AS [size],
        CASE JSON_VALUE(msg.value, '$.isPrompt') WHEN 'true' THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END AS is_prompt
    FROM [${STAGING_TABLE_ACTIVITY}] imports
    CROSS APPLY OPENJSON(imports.messages_json) msg
    WHERE imports.messages_json IS NOT NULL
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
        pm.is_prompt
    FROM parsed_messages pm
)
INSERT INTO copilot_event_messages (copilot_chat_id, message_id, [size], is_prompt)
SELECT
    rm.event_id,
    rm.persisted_message_id,
    rm.[size],
    rm.is_prompt
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
