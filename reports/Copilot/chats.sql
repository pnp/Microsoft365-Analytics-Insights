-- Counted turns, not raw audit rows. Maker testing is excluded from this adoption report.
-- Unknown historical estimates stay missing, never silently presented as free usage.
SELECT c.event_id, c.app_host AS [App host], c.agent_id,
       CASE WHEN e.basis = N'NotAssessed'
                 OR (c.agent_id IS NOT NULL AND e.basis IS NULL
                     AND (c.copilot_credit_estimate_total IS NULL OR c.copilot_credit_estimate_total = 0))
            THEN NULL ELSE c.copilot_credit_estimate_total END AS [Audit credit estimate],
       c.copilot_credit_estimate_json,
       CASE WHEN e.basis = N'NotAssessed' THEN N'Not assessed'
            WHEN c.copilot_credit_estimate_total IS NULL THEN N'Historical evidence unavailable'
            WHEN e.basis IS NULL AND c.copilot_credit_estimate_total > 0 THEN N'Legacy audit estimate (not billed)'
            WHEN c.agent_id IS NOT NULL AND e.basis IS NULL THEN N'Historical evidence unavailable'
            WHEN e.basis = N'NoMessages' THEN N'No response evidence'
            ELSE N'Audit estimate (not billed)' END AS [Estimate status]
FROM dbo.copilot_chats AS c
CROSS APPLY (SELECT JSON_VALUE(CASE WHEN ISJSON(c.copilot_credit_estimate_json) = 1
                                   THEN c.copilot_credit_estimate_json END, '$.AgentCreditBasis') AS basis) AS e
WHERE (c.app_host IS NULL OR c.app_host <> N'Copilot Studio')
  AND NOT EXISTS (SELECT 1 FROM dbo.copilot_chat_duplicates AS d
                  WHERE d.time_stamp = c.time_stamp AND d.event_id = c.event_id)
