-- Insert new agents
INSERT INTO copilot_agents([name], [agent_id])
	SELECT distinct imports.agent_name, imports.agent_id
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
)
WHERE EXISTS (
    SELECT 1
    FROM [${STAGING_TABLE_ACTIVITY}] imports
    WHERE copilot_agents.[agent_id] = imports.[agent_id]
      AND imports.agent_name IS NOT NULL
      AND imports.agent_name <> copilot_agents.[name]
);


-- Insert chat where there is no existing copilot_chats record for the event_id
INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json)
SELECT
    i.event_id,
    i.app_host,
    ca.id,
    i.copilot_credit_estimate_total,
    i.copilot_credit_estimate_json
FROM dbo.[${STAGING_TABLE_ACTIVITY}]  AS i
LEFT JOIN dbo.copilot_agents AS ca
    ON ca.agent_id = i.agent_id
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.copilot_chats AS ec
    WHERE ec.event_id = i.event_id
    )


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

-- Process AccessedResources: Insert unique resource IDs
INSERT INTO copilot_event_accessed_resource_ids (resource_id)
SELECT DISTINCT JSON_VALUE(ar.value, '$.Id') AS resource_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.Id') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_ids 
    WHERE resource_id = JSON_VALUE(ar.value, '$.Id')
  );


-- Process AccessedResources: Insert unique resource names
INSERT INTO copilot_event_accessed_resource_names ([name])
SELECT DISTINCT JSON_VALUE(ar.value, '$.Name') AS resource_name
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.Name') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_names 
    WHERE [name] = JSON_VALUE(ar.value, '$.Name')
  );


-- Process AccessedResources: Insert unique resource site URLs
INSERT INTO copilot_event_accessed_resource_site_urls (site_url)
SELECT DISTINCT JSON_VALUE(ar.value, '$.SiteUrl') AS site_url
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.SiteUrl') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_site_urls 
    WHERE site_url = JSON_VALUE(ar.value, '$.SiteUrl')
  );


-- Process AccessedResources: Insert unique resource types
INSERT INTO copilot_event_accessed_resource_types ([name])
SELECT DISTINCT JSON_VALUE(ar.value, '$.Type') AS resource_type
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.Type') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_accessed_resource_types 
    WHERE [name] = JSON_VALUE(ar.value, '$.Type')
  );


-- Process AccessedResources: Insert unique sensitivity labels
INSERT INTO sensitivity_labels (label_id)
SELECT DISTINCT JSON_VALUE(ar.value, '$.SensitivityLabelId') AS label_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.SensitivityLabelId') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM sensitivity_labels 
    WHERE label_id = JSON_VALUE(ar.value, '$.SensitivityLabelId')
  );


-- Process AccessedResources: Insert junction table records linking events to accessed resources
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id)
SELECT 
    imports.event_id,
    rid.id,
    rname.id,
    rsiteurl.id,
    rtype.id,
    slabel.id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
LEFT JOIN copilot_event_accessed_resource_ids rid 
    ON rid.resource_id = JSON_VALUE(ar.value, '$.Id')
LEFT JOIN copilot_event_accessed_resource_names rname 
    ON rname.[name] = JSON_VALUE(ar.value, '$.Name')
LEFT JOIN copilot_event_accessed_resource_site_urls rsiteurl 
    ON rsiteurl.site_url = JSON_VALUE(ar.value, '$.SiteUrl')
LEFT JOIN copilot_event_accessed_resource_types rtype 
    ON rtype.[name] = JSON_VALUE(ar.value, '$.Type')
LEFT JOIN sensitivity_labels slabel 
    ON slabel.label_id = JSON_VALUE(ar.value, '$.SensitivityLabelId')
WHERE imports.accessed_resources_json IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resources
    WHERE copilot_chat_id = imports.event_id
      AND (resource_id_id = rid.id OR (resource_id_id IS NULL AND rid.id IS NULL))
      AND (resource_name_id = rname.id OR (resource_name_id IS NULL AND rname.id IS NULL))
      AND (resource_site_url_id = rsiteurl.id OR (resource_site_url_id IS NULL AND rsiteurl.id IS NULL))
      AND (resource_type_id = rtype.id OR (resource_type_id IS NULL AND rtype.id IS NULL))
      AND (sensitivity_label_id = slabel.id OR (sensitivity_label_id IS NULL AND slabel.id IS NULL))
  );

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
