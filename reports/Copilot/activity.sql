SELECT CONVERT(varchar(36), cc.event_id) AS InteractionId,
       ae.time_stamp AS InteractionTimestampUtc,
       CAST(ae.time_stamp AS date) AS InteractionDate,
       COALESCE(NULLIF(cc.app_host, ''), 'Unknown') AS Application,
       COALESCE(op.operation_name, 'Unknown') AS Operation,
       ae.user_id AS UserId, ag.name AS AgentName,
       CASE WHEN cc.agent_id IS NULL THEN N'No agent'
            WHEN k.agent_id LIKE N'Copilot.M365Copilot.%' THEN N'Microsoft'
            WHEN k.agent_id LIKE N'CopilotStudio.%' OR k.agent_id LIKE N'Copilot.Studio.%'
                 OR ag.is_custom_agent = 1 THEN N'Customer-built'
            ELSE N'Unknown' END AS AgentType
FROM dbo.copilot_chats AS cc
INNER JOIN dbo.audit_events AS ae ON ae.id = cc.event_id
LEFT JOIN dbo.event_operations AS op ON op.id = ae.operation_id
LEFT JOIN dbo.copilot_agents AS ag ON ag.id = cc.agent_id
CROSS APPLY (SELECT LTRIM(RTRIM(ag.agent_id)) COLLATE Latin1_General_CI_AS AS agent_id) AS k
WHERE (cc.app_host IS NULL OR cc.app_host <> N'Copilot Studio')
  AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d
                  WHERE d.time_stamp = cc.time_stamp AND d.event_id = cc.event_id)
