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


-- Insert chat
insert into [event_copilot_chats] (event_id, app_host, agent_id)
	SELECT 
		imports.event_id
		,app_host
		,copilot_agents.id
		FROM [${STAGING_TABLE_ACTIVITY}] imports
	left join copilot_agents on copilot_agents.[agent_id] = imports.[agent_id]
