-- Process Copilot tool executions (AIExecuteTool events)
-- Links tool executions back to copilot chats via message_id matching copilot_event_messages

-- Insert unique tool names
IF OBJECT_ID('dbo.copilot_tool_names', 'U') IS NOT NULL
BEGIN

INSERT INTO copilot_tool_names ([name])
SELECT DISTINCT imports.tool_name
FROM [${STAGING_TABLE_ACTIVITY}] imports
WHERE imports.tool_name IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 
    FROM copilot_tool_names 
    WHERE [name] = imports.tool_name
  );


-- Insert tool execution records, resolving copilot_chat_id via message_id -> copilot_event_messages
INSERT INTO copilot_tool_executions (event_id, copilot_chat_id, message_id, tool_name_id, app_host)
SELECT 
    imports.event_id,
    msg.copilot_chat_id,
    imports.message_id,
    tn.id,
    imports.app_host
FROM [${STAGING_TABLE_ACTIVITY}] imports
LEFT JOIN copilot_tool_names tn 
    ON tn.[name] = imports.tool_name
LEFT JOIN copilot_event_messages msg 
    ON msg.message_id = imports.message_id
WHERE NOT EXISTS (
    SELECT 1
    FROM copilot_tool_executions te
    WHERE te.event_id = imports.event_id
      AND (te.tool_name_id = tn.id OR (te.tool_name_id IS NULL AND tn.id IS NULL))
      AND (te.message_id = imports.message_id OR (te.message_id IS NULL AND imports.message_id IS NULL))
);

END
