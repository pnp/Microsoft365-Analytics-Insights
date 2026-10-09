-- The shapes measured and REJECTED for the DLP page's governance statements (#648), kept so the comparison in
-- the PR can be reproduced against the same fixture:
--
--   .\Invoke-CopilotGovernanceBenchmark.ps1 -DatabaseName <dev catalog> -CandidatesPath .\CopilotGovernanceRejectedShapes.sql
--
-- Each one returns the same figures as the shipped statement it was measured against.

-- statement: RejectedPerInteractionApply
-- "Aggregate per interaction first, seeking through the copilot_chats time_stamp index": both detail tables
-- reduced to one row per interaction with OUTER APPLY. The optimiser spooled each table and probed the spool once
-- per interaction, which made it by far the slowest shape (tens of millions of worktable reads).
SELECT COUNT_BIG(*) AS Interactions,
       COUNT_BIG(j.Flagged) AS JailbreakReported,
       ISNULL(SUM(CAST(j.Flagged AS bigint)), 0) AS JailbreakFlagged,
       COUNT_BIG(r.XpiaFlagged) AS XpiaReported,
       ISNULL(SUM(CAST(r.XpiaFlagged AS bigint)), 0) AS XpiaFlagged,
       ISNULL(SUM(r.Resources), 0) AS Resources,
       ISNULL(SUM(r.Labelled), 0) AS LabelledResources,
       COUNT_BIG(CASE WHEN r.Resources > 0 THEN 1 END) AS InteractionsWithResources
FROM dbo.copilot_chats AS c
OUTER APPLY (
    SELECT MAX(CAST(m.jailbreak_detected AS int)) AS Flagged
    FROM dbo.copilot_event_messages AS m
    WHERE m.copilot_chat_id = c.event_id
) AS j
OUTER APPLY (
    SELECT COUNT_BIG(*) AS Resources,
           COUNT_BIG(a.sensitivity_label_id) AS Labelled,
           MAX(CAST(a.xpia_detected AS int)) AS XpiaFlagged
    FROM dbo.copilot_event_accessed_resources AS a
    WHERE a.copilot_chat_id = c.event_id
) AS r
WHERE c.time_stamp >= @from AND c.time_stamp <= @to
OPTION (RECOMPILE);

-- statement: RejectedJailbreakGroupedPerInteraction
-- The jailbreak counts from a join grouped per interaction. Half the logical reads of the shipped semi-joins
-- (one scan of copilot_event_messages instead of two), but the hash aggregate spills to tempdb - the optimiser
-- cannot see that the flags sit in recent rows - and it was slower in every interleaved A/B, by 8-63%.
SELECT COUNT_BIG(*) AS JailbreakReported, ISNULL(SUM(CAST(x.Flagged AS bigint)), 0) AS JailbreakFlagged
FROM (
    SELECT m.copilot_chat_id, MAX(CAST(m.jailbreak_detected AS int)) AS Flagged
    FROM dbo.copilot_chats AS c
    INNER JOIN dbo.copilot_event_messages AS m ON m.copilot_chat_id = c.event_id
    WHERE c.time_stamp >= @from AND c.time_stamp <= @to
      AND m.jailbreak_detected IS NOT NULL
    GROUP BY m.copilot_chat_id
) AS x
OPTION (RECOMPILE);

-- statement: RejectedModelsDistinctFirst
-- The model mix with each (interaction, model) pair de-duplicated first and the any-model total from a separate
-- semi-join. 15-23 times fewer logical reads than GROUPING SETS (no spool), but it joins the window twice and was
-- 13-41% slower in an interleaved A/B. A non-interleaved run can show the opposite: measure the two alternately.
SELECT x.Name, CAST(0 AS bit) AS IsTotal, COUNT_BIG(*) AS Interactions
FROM (
    SELECT DISTINCT em.copilot_chat_id, m.[name] AS Name
    FROM dbo.copilot_chats AS c
    INNER JOIN dbo.copilot_event_ai_models AS em ON em.copilot_chat_id = c.event_id
    INNER JOIN dbo.copilot_ai_models AS m ON m.id = em.model_id
    WHERE c.time_stamp >= @from AND c.time_stamp <= @to
) AS x
GROUP BY x.Name
UNION ALL
SELECT NULL, CAST(1 AS bit), COUNT_BIG(*)
FROM dbo.copilot_chats AS c
WHERE c.time_stamp >= @from AND c.time_stamp <= @to
  AND EXISTS (SELECT 1 FROM dbo.copilot_event_ai_models AS em WHERE em.copilot_chat_id = c.event_id)
OPTION (RECOMPILE);
