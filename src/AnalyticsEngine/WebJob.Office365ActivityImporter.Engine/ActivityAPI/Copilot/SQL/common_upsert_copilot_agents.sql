-- Insert new agents
INSERT INTO copilot_agents([name], [agent_id], [is_custom_agent])
	SELECT distinct imports.agent_name, imports.agent_id, imports.is_custom_agent
	FROM [${STAGING_TABLE_ACTIVITY}] imports
	left join copilot_agents on copilot_agents.[agent_id] = imports.[agent_id]
	where copilot_agents.[agent_id] is null and imports.[agent_id] is not null;


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


-- Insert chat where there is no existing copilot_chats record for the event_id
-- Uses ROW_NUMBER to deduplicate staging table rows with the same event_id
INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json)
SELECT event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json
FROM (
    SELECT
        i.event_id,
        i.app_host,
        ca.id AS agent_id,
        i.copilot_credit_estimate_total,
        i.copilot_credit_estimate_json,
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


-- Process AccessedResources only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_accessed_resource_ids', 'U') IS NOT NULL
BEGIN

-- Create indexes on lookup tables if they don't already exist (one-time, idempotent)
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_copilot_resource_types_name' AND object_id = OBJECT_ID('dbo.copilot_event_accessed_resource_types'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_copilot_resource_types_name ON copilot_event_accessed_resource_types([name]);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_sensitivity_labels_label_id' AND object_id = OBJECT_ID('dbo.sensitivity_labels'))
    CREATE UNIQUE NONCLUSTERED INDEX IX_sensitivity_labels_label_id ON sensitivity_labels(label_id);

-- Parse JSON once into a temp table to avoid redundant OPENJSON/JSON_VALUE across 6 passes.
-- resource_id / resource_name / site_url are trimmed to 850 chars to match their lookup columns
-- (nvarchar(850) after migration IndexCopilotAccessedResourceLookups, so they can be indexed - the
-- join/de-dup keys below). Trimming here keeps the value consistent between the DISTINCT insert and the
-- resolve join so de-duplication still works, and guarantees no over-width value hits the narrowed column.
SELECT 
    imports.event_id,
    LEFT(JSON_VALUE(ar.value, '$.Id'), 850) AS resource_id,
    LEFT(JSON_VALUE(ar.value, '$.Name'), 850) AS resource_name,
    LEFT(JSON_VALUE(ar.value, '$.SiteUrl'), 850) AS site_url,
    JSON_VALUE(ar.value, '$.Type') AS resource_type,
    JSON_VALUE(ar.value, '$.SensitivityLabelId') AS sensitivity_label_id
INTO #parsed_accessed_resources
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE imports.accessed_resources_json IS NOT NULL;


-- Process AccessedResources: Insert unique resource IDs
INSERT INTO copilot_event_accessed_resource_ids (resource_id)
SELECT DISTINCT par.resource_id
FROM #parsed_accessed_resources par
WHERE par.resource_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_ids ari 
    WHERE ari.resource_id = par.resource_id
  );


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


-- Resolve lookup IDs once into a second temp table for the junction insert
SELECT 
    par.event_id,
    rid.id AS resource_id_id,
    rname.id AS resource_name_id,
    rsiteurl.id AS resource_site_url_id,
    rtype.id AS resource_type_id,
    slabel.id AS sensitivity_label_id
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
    ON slabel.label_id = par.sensitivity_label_id;


-- Process AccessedResources: Insert junction table records linking events to accessed resources.
-- Keyed NOT EXISTS on copilot_chat_id (seeks via IX_copilot_chat_id to just this chat's existing rows)
-- instead of EXCEPT-ing against the WHOLE junction table, which forced a full scan/sort of the entire
-- (millions-of-rows) table every batch. The NULL-safe INTERSECT compares the resolved tuple exactly like
-- EXCEPT did (NULLs equal). DISTINCT de-duplicates repeated resources within the batch.
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id)
SELECT DISTINCT r.event_id, r.resource_id_id, r.resource_name_id, r.resource_site_url_id, r.resource_type_id, r.sensitivity_label_id
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

DROP TABLE #parsed_accessed_resources;
DROP TABLE #resolved_accessed_resources;

END


-- Process Messages only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_messages', 'U') IS NOT NULL
BEGIN

-- Insert message records (only responses, not prompts - prompts are filtered before this SQL runs)
INSERT INTO copilot_event_messages (copilot_chat_id, message_id)
SELECT 
    imports.event_id,
    ISNULL(JSON_VALUE(msg.value, '$.Id'), NEWID()) -- Generate GUID if no ID provided
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.messages_json) msg
WHERE imports.messages_json IS NOT NULL;

END


-- Process AI Model Transparency only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_ai_models', 'U') IS NOT NULL
BEGIN

-- Insert unique AI model names from model_transparency_json
INSERT INTO copilot_ai_models ([name])
SELECT DISTINCT JSON_VALUE(models.value, '$.ModelName') AS model_name
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.model_transparency_json) models
WHERE imports.model_transparency_json IS NOT NULL
  AND JSON_VALUE(models.value, '$.ModelName') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_ai_models 
    WHERE [name] = JSON_VALUE(models.value, '$.ModelName')
  );


-- Link AI models to copilot chats via junction table
INSERT INTO copilot_event_ai_models (copilot_chat_id, model_id)
SELECT DISTINCT 
    imports.event_id,
    cam.id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.model_transparency_json) models
INNER JOIN copilot_ai_models cam 
    ON cam.[name] = JSON_VALUE(models.value, '$.ModelName')
WHERE imports.model_transparency_json IS NOT NULL
  AND JSON_VALUE(models.value, '$.ModelName') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_ai_models ceam 
    WHERE ceam.copilot_chat_id = imports.event_id
      AND ceam.model_id = cam.id
  );

END
