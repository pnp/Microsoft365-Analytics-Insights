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
INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version)
SELECT event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json, thread_id, client_region, copilot_log_version
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
        ROW_NUMBER() OVER (PARTITION BY i.event_id ORDER BY (SELECT NULL)) AS rn
    FROM dbo.[${STAGING_TABLE_ACTIVITY}] AS i
    LEFT JOIN dbo.copilot_agents AS ca
        ON ca.agent_id = i.agent_id
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
-- The GROUP BY does the batch-level de-duplication that the junction insert used to do with SELECT
-- DISTINCT, and it does it on exactly the ORIGINAL five-column tuple. That keeps two things true at
-- once: the de-dup identity is unchanged (so the composite index from CoverCopilotAccessedResourceDedup
-- still covers the anti-join exactly), and action_id / list_item_unique_id_id ride along as payload
-- picked deterministically with MIN(). Doing it here rather than in the junction INSERT means the hot
-- insert_junction step no longer sorts at all - it just anti-joins an already-unique set.
SELECT 
    par.event_id,
    rid.id AS resource_id_id,
    rname.id AS resource_name_id,
    rsiteurl.id AS resource_site_url_id,
    rtype.id AS resource_type_id,
    slabel.id AS sensitivity_label_id,
    MIN(raction.id) AS action_id,
    MIN(rlistitem.id) AS list_item_unique_id_id
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
GROUP BY par.event_id, rid.id, rname.id, rsiteurl.id, rtype.id, slabel.id;


SET @rows = @@ROWCOUNT;
IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings (batch_id, staging_table, step_name, duration_ms, rows_affected)
    VALUES (@batch_id, N'${STAGING_TABLE_ACTIVITY}', 'resolve', DATEDIFF(MILLISECOND, @t, SYSUTCDATETIME()), @rows);
SET @t = SYSUTCDATETIME();

-- Process AccessedResources: Insert junction table records linking events to accessed resources.
-- Keyed NOT EXISTS on copilot_chat_id (seeks via IX_copilot_chat_id to just this chat's existing rows)
-- instead of EXCEPT-ing against the WHOLE junction table, which forced a full scan/sort of the entire
-- (millions-of-rows) table every batch. The NULL-safe INTERSECT compares the resolved tuple exactly like
-- EXCEPT did (NULLs equal).
--
-- IMPORTANT - the de-dup tuple is deliberately UNCHANGED by the addition of action_id /
-- list_item_unique_id_id. It is the exact tuple covered by the composite key index
-- IX_copilot_event_accessed_resources_dedup (migration CoverCopilotAccessedResourceDedup), which is
-- what turns this existence check from an O(resolved x table) rescan into a seek. Widening the tuple
-- would leave that index only partially covering and regress the known-hot path (and the two extra
-- columns cannot be added to the key anyway: list_item_unique_id_id would push it past the 1700-byte
-- index-key limit for no benefit).
--
-- The two new columns are payload, not identity. #resolved_accessed_resources is already unique per
-- (event, tuple) - the resolve step above GROUPs BY exactly this tuple and MIN()s the payload - so
-- this insert needs no DISTINCT at all and the row count is EXACTLY what it was before. In practice
-- Action is "Read" for every access and listItemUniqueId is a property of the resource itself, so
-- collapsing them onto one row loses nothing real.
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id)
SELECT r.event_id, r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id,
       r.action_id, r.list_item_unique_id_id
FROM #resolved_accessed_resources r
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resources x
    WHERE x.copilot_chat_id = r.event_id
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
-- De-duplicated on (chat, message id), which this insert previously was NOT. One interaction can be
-- staged into TWO staging tables - a Teams chat context stages a chat-only row and a following file
-- context stages a SharePoint row - and each staging table runs this merge, so its messages were
-- inserted twice. The NOT EXISTS seeks the chat's handful of existing rows via IX_copilot_chat_id.
-- Messages with no Id keep the previous behaviour (a generated GUID, and therefore no de-dup).
;WITH parsed_messages AS (
    SELECT DISTINCT
        imports.event_id,
        JSON_VALUE(msg.value, '$.Id') AS message_id,
        TRY_CONVERT(bigint, JSON_VALUE(msg.value, '$.Size')) AS [size],
        CASE JSON_VALUE(msg.value, '$.isPrompt') WHEN 'true' THEN CAST(1 AS bit) WHEN 'false' THEN CAST(0 AS bit) ELSE NULL END AS is_prompt
    FROM [${STAGING_TABLE_ACTIVITY}] imports
    CROSS APPLY OPENJSON(imports.messages_json) msg
    WHERE imports.messages_json IS NOT NULL
)
INSERT INTO copilot_event_messages (copilot_chat_id, message_id, [size], is_prompt)
SELECT
    pm.event_id,
    ISNULL(pm.message_id, NEWID()), -- Generate GUID if no ID provided
    pm.[size],
    pm.is_prompt
FROM parsed_messages pm
WHERE pm.message_id IS NULL
   OR NOT EXISTS (
        SELECT 1
        FROM copilot_event_messages x
        WHERE x.copilot_chat_id = pm.event_id
          AND x.message_id = pm.message_id
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
