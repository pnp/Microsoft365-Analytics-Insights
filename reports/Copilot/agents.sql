-- Inventory origin, not billing kind. Mirrors CopilotAgentClassifier.ResolveStoredOrigin.
SELECT a.id, a.agent_id AS [Internal agent ID], a.name AS [Agent name],
       CASE WHEN k.agent_id LIKE N'Copilot.M365Copilot.%' THEN N'Microsoft'
            WHEN k.agent_id LIKE N'CopilotStudio.%' OR k.agent_id LIKE N'Copilot.Studio.%'
                 OR a.is_custom_agent = 1 THEN N'Customer-built'
            ELSE N'Unknown' END AS [Agent origin]
FROM dbo.copilot_agents AS a
CROSS APPLY (SELECT LTRIM(RTRIM(a.agent_id)) COLLATE Latin1_General_CI_AS AS agent_id) AS k
