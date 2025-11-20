--Insert new agents
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


-- Insert chat where there is no existing event_copilot_chats record for the event_id
INSERT INTO dbo.event_copilot_chats (event_id, app_host, agent_id)
SELECT
    i.event_id,
    i.app_host,
    ca.id
FROM dbo.[${STAGING_TABLE_ACTIVITY}]  AS i
LEFT JOIN dbo.copilot_agents AS ca
    ON ca.agent_id = i.agent_id
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.event_copilot_chats AS ec
    WHERE ec.event_id = i.event_id
    )


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
INSERT INTO copilot_event_sensitivity_labels (label_id)
SELECT DISTINCT JSON_VALUE(ar.value, '$.SensitivityLabelId') AS label_id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
WHERE JSON_VALUE(ar.value, '$.SensitivityLabelId') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_sensitivity_labels 
    WHERE label_id = JSON_VALUE(ar.value, '$.SensitivityLabelId')
  );


-- Process AccessedResources: Insert junction table records linking events to accessed resources
INSERT INTO copilot_event_accessed_resources (copilot_chat_id, resource_id_id, resource_name_id, resource_type_id, sensitivity_label_id)
SELECT 
    imports.event_id,
    rid.id,
    rname.id,
    rtype.id,
    slabel.id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.accessed_resources_json) ar
LEFT JOIN copilot_event_accessed_resource_ids rid 
    ON rid.resource_id = JSON_VALUE(ar.value, '$.Id')
LEFT JOIN copilot_event_accessed_resource_names rname 
    ON rname.[name] = JSON_VALUE(ar.value, '$.Name')
LEFT JOIN copilot_event_accessed_resource_types rtype 
    ON rtype.[name] = JSON_VALUE(ar.value, '$.Type')
LEFT JOIN copilot_event_sensitivity_labels slabel 
    ON slabel.label_id = JSON_VALUE(ar.value, '$.SensitivityLabelId')
WHERE imports.accessed_resources_json IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_accessed_resources
    WHERE copilot_chat_id = imports.event_id
      AND (resource_id_id = rid.id OR (resource_id_id IS NULL AND rid.id IS NULL))
      AND (resource_name_id = rname.id OR (resource_name_id IS NULL AND rname.id IS NULL))
      AND (resource_type_id = rtype.id OR (resource_type_id IS NULL AND rtype.id IS NULL))
      AND (sensitivity_label_id = slabel.id OR (sensitivity_label_id IS NULL AND slabel.id IS NULL))
  );

END


-- Process Messages only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_messages', 'U') IS NOT NULL
BEGIN

-- Insert unique message types
INSERT INTO copilot_event_message_types ([name])
SELECT DISTINCT JSON_VALUE(msg.value, '$.Type') AS message_type
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.messages_json) msg
WHERE JSON_VALUE(msg.value, '$.Type') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_message_types 
    WHERE [name] = JSON_VALUE(msg.value, '$.Type')
  );


-- Insert message records (only responses, not prompts)
INSERT INTO copilot_event_messages (copilot_chat_id, message_id, is_prompt, message_type_id)
SELECT 
    imports.event_id,
    ISNULL(JSON_VALUE(msg.value, '$.Id'), NEWID()), -- Generate GUID if no ID provided
    CASE WHEN JSON_VALUE(msg.value, '$.IsPrompt') = 'true' THEN 1 ELSE 0 END, -- Convert JSON boolean string to BIT
    mt.id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.messages_json) msg
LEFT JOIN copilot_event_message_types mt 
    ON mt.[name] = JSON_VALUE(msg.value, '$.Type')
WHERE imports.messages_json IS NOT NULL
  AND (JSON_VALUE(msg.value, '$.IsPrompt') IS NULL OR JSON_VALUE(msg.value, '$.IsPrompt') <> 'true'); -- Only save response messages

END


-- Process Agent Actions only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_agent_actions', 'U') IS NOT NULL
BEGIN

-- Insert unique agent action types
INSERT INTO copilot_event_agent_action_types ([name])
SELECT DISTINCT JSON_VALUE(action.value, '$.Type') AS action_type
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.agent_actions_json) action
WHERE JSON_VALUE(action.value, '$.Type') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_agent_action_types 
    WHERE [name] = JSON_VALUE(action.value, '$.Type')
  );


-- Insert agent action records
INSERT INTO copilot_event_agent_actions (copilot_chat_id, action_id, action_type_id)
SELECT 
    imports.event_id,
    ISNULL(JSON_VALUE(action.value, '$.Id'), NEWID()), -- Generate GUID if no ID provided
    at.id
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.agent_actions_json) action
LEFT JOIN copilot_event_agent_action_types at 
    ON at.[name] = JSON_VALUE(action.value, '$.Type')
WHERE imports.agent_actions_json IS NOT NULL;

END


-- Process AI Tool Usages only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_ai_tool_usages', 'U') IS NOT NULL
BEGIN

-- Insert unique AI tool tiers
INSERT INTO copilot_event_ai_tool_tiers ([name])
SELECT DISTINCT JSON_VALUE(tool.value, '$.Tier') AS tier_name
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.ai_tool_usages_json) tool
WHERE JSON_VALUE(tool.value, '$.Tier') IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_event_ai_tool_tiers 
    WHERE [name] = JSON_VALUE(tool.value, '$.Tier')
  );


-- Insert AI tool usage records
INSERT INTO copilot_event_ai_tool_usages (copilot_chat_id, tool_id, tier_id, response_count)
SELECT 
    imports.event_id,
    ISNULL(JSON_VALUE(tool.value, '$.ToolId'), 'Unknown'),
    tier.id,
    CAST(JSON_VALUE(tool.value, '$.ResponseCount') AS INT)
FROM [${STAGING_TABLE_ACTIVITY}] imports
CROSS APPLY OPENJSON(imports.ai_tool_usages_json) tool
LEFT JOIN copilot_event_ai_tool_tiers tier 
    ON tier.[name] = JSON_VALUE(tool.value, '$.Tier')
WHERE imports.ai_tool_usages_json IS NOT NULL;

END


-- Process Flow Actions only if tables exist (after migration)
IF OBJECT_ID('dbo.copilot_event_flow_actions', 'U') IS NOT NULL
BEGIN

-- Insert flow action records
INSERT INTO copilot_event_flow_actions (copilot_chat_id, action_count)
SELECT 
    imports.event_id,
    CAST(JSON_VALUE(imports.flow_actions_json, '$.ActionCount') AS INT)
FROM [${STAGING_TABLE_ACTIVITY}] imports
WHERE imports.flow_actions_json IS NOT NULL
  AND NOT EXISTS (
    SELECT 1
    FROM copilot_event_flow_actions
    WHERE copilot_chat_id = imports.event_id
  );

END
