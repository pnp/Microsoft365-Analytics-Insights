-- Bounded, restart-safe repairs supported by persisted facts (#639 / #699).
-- Safe to run manually or once per importer cycle; no parameters and no schema changes.
-- Origin is recoverable only for recognised stored identities. A bare GUID, SPO_ id, display
-- name or a legacy false flag cannot prove Microsoft origin. Leave those states untouched.
-- No credit totals are computed here: old zero-estimate JSON omitted response counts, and
-- Messages may survive in audit_events.event_data (CopilotEventData only); the original top-level
-- AppIdentity / AgentPlatform do not. CopilotHistoricalRepair can replay the retained subset in
-- explicit bounded UTC windows, but only prices agents whose kind is still provable.
SET NOCOUNT ON;
DECLARE @classified int = 0, @restored int = 0;

IF OBJECT_ID('dbo.copilot_agents', 'U') IS NOT NULL
BEGIN
    UPDATE TOP (1000) a
    SET is_custom_agent = CASE WHEN k.agent_id LIKE N'Copilot.M365Copilot.%' THEN 0 ELSE 1 END
    FROM dbo.copilot_agents AS a
    CROSS APPLY (SELECT LTRIM(RTRIM(a.agent_id)) COLLATE Latin1_General_CI_AS AS agent_id) AS k
    WHERE a.is_custom_agent IS NULL
      AND (k.agent_id LIKE N'CopilotStudio.%'
           OR k.agent_id LIKE N'Copilot.Studio.%'
           OR k.agent_id LIKE N'Copilot.M365Copilot.%');
    SET @classified = @@ROWCOUNT;
END

IF OBJECT_ID('dbo.copilot_chat_duplicates', 'U') IS NOT NULL
BEGIN
    -- A same-host record within ten seconds is not proof of an extra record of one turn.
    -- Restore its count, without moving/deleting chats or resources. Already-deleted DLP copies
    -- cannot be recovered from these mappings; replay source records if still retained.
    DELETE TOP (50000) FROM dbo.copilot_chat_duplicates WHERE reason = 2;
    SET @restored = @@ROWCOUNT;
END

SELECT @classified AS ClassifiedAgents, @restored AS RestoredTurns;
