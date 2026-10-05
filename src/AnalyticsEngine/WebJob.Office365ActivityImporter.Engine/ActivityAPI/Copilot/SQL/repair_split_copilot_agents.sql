-- Self-healing repair of Copilot agents that were imported under more than one dbo.copilot_agents row (#699).
--
-- WHAT IT FIXES
-- Older importers could give one agent several rows, splitting its users and interactions between them:
--   * A Copilot Studio agent was keyed on its Entra Agent ID (a bare GUID) by Copilot Studio's runtime records,
--     and on "T_{titleId}.{agentId}" by Microsoft 365 Copilot's records. That made two rows, and the first one
--     was never named, so reports showed it as an unnamed agent.
--   * The agents upsert inserted one row per distinct name a NEW agent arrived with in a batch (a NULL name
--     included), so a single agent_id could get several rows.
--   * SharePoint agents arrived both as "SharePointAgents.Declarative.SPO_..." and as bare "SPO_...". The
--     importer has normalised that since July 2026, but rows created before then were never merged.
-- The importer now resolves every one of these to a single id (CopilotAuditLogContent.NormalizeAgentId), and the
-- upsert can no longer create duplicates, but neither touches rows that already exist. This does. For each set
-- of rows that now resolve to the same id it keeps ONE row: the oldest already keyed on that id, or failing that
-- the oldest. That row gets the canonical id, the best name and flag in the set, and every interaction. The
-- other rows are then deleted.
--
-- THE NAME IT KEEPS
-- A display name logged by Microsoft 365 Copilot (on a "T_..." row) wins. The bare-GUID row of a split Copilot
-- Studio agent was only ever named from its schema name, if at all. Failing that, the kept row's own name; then
-- any other. If an agent has since been renamed, the importer's next Microsoft 365 Copilot record corrects it.
-- is_custom_agent: true wins, as in the upsert.
--
-- WHEN IT RUNS
-- From the web-job top level, once per import cycle, straight after repair_denormalised_copilot_columns.sql and
-- for the same reasons (see that script). It has to heal databases whose import is failing, idle or switched
-- off, and it catches rows an older importer build writes while an upgrade is in progress.
-- It is also safe to run by hand, as often as you like: it takes no parameters, is idempotent, and returns one
-- row of counts.
--
-- COST
-- Nothing to do (the normal case): two scans of copilot_agents, which holds one row per agent, with no sorts.
-- Otherwise each merged row's interactions are moved through IX_agent_id in statements of at
-- most 50,000, smallest rows first, so one heavily used agent can't hold every other merge back. A run moves at
-- most 1,000,000 interactions; anything left is finished by the next run, and a row is only deleted once nothing
-- points at it. A run that stops part-way through (timeout, failover) leaves a consistent state, and the next
-- run picks the same rows to keep.
-- Measured on synthetic data in LocalDB, with production's indexes and foreign key: 51,000 agents and 2.3 million
-- interactions, with 1,000 rows to merge, one of which held 1.2 million interactions. The first run merged 999
-- rows and moved 1,000,000 interactions (18 s CPU, 131-145 s elapsed); the second moved the last 300,000 (6 s
-- CPU, 45 s). With nothing to do, a run took 54 ms and 1,395 logical reads. See PR #700.

SET NOCOUNT ON;

IF OBJECT_ID('dbo.copilot_agents', 'U') IS NULL OR OBJECT_ID('dbo.copilot_chats', 'U') IS NULL
BEGIN
	SELECT CAST(0 AS int) AS MergedAgentRows, CAST(0 AS int) AS RekeyedAgentRows,
	       CAST(0 AS bigint) AS MovedInteractions, CAST(0 AS int) AS PendingAgentRows;
	RETURN;
END

DECLARE @batch int = 50000;
DECLARE @maxMoved bigint = 1000000;
DECLARE @take int;
DECLARE @rows int;
DECLARE @moved bigint = 0;
DECLARE @merged int = 0;
DECLARE @rekeyed int = 0;
DECLARE @pending int = 0;
DECLARE @loserId int;
DECLARE @survivorId int;

-- "T_{titleId}.{agentId}": exactly the shape NormalizeAgentId rewrites to its agentId, and nothing looser.
DECLARE @hex nvarchar(20) = N'[0-9a-fA-F]';
DECLARE @guid nvarchar(1000) = REPLICATE(@hex, 8) + N'-' + REPLICATE(@hex, 4) + N'-' + REPLICATE(@hex, 4)
	+ N'-' + REPLICATE(@hex, 4) + N'-' + REPLICATE(@hex, 12);
DECLARE @titleScopedId nvarchar(2000) = N'T[_]' + @guid + N'.' + @guid;
DECLARE @spoWrapper nvarchar(50) = N'SharePointAgents.Declarative.';

-- Is there anything to repair? A row whose id is not canonical, or an id with more than one row. If no row has a
-- non-canonical id, every id already is its canonical id, so the second test can group on agent_id itself. This
-- check is all a run costs once the database has been repaired: two scans of copilot_agents, with no sorts.
IF NOT EXISTS (SELECT 1 FROM dbo.copilot_agents
               WHERE agent_id LIKE @titleScopedId OR agent_id LIKE @spoWrapper + N'SPO[_]%')
   AND NOT EXISTS (SELECT 1 FROM dbo.copilot_agents
                   WHERE agent_id IS NOT NULL AND LEN(agent_id) <= 450
                   GROUP BY CAST(agent_id AS nvarchar(450))
                   HAVING COUNT(*) > 1)
BEGIN
	SELECT CAST(0 AS int) AS MergedAgentRows, CAST(0 AS int) AS RekeyedAgentRows,
	       CAST(0 AS bigint) AS MovedInteractions, CAST(0 AS int) AS PendingAgentRows;
	RETURN;
END

DROP TABLE IF EXISTS #repair_agents;

-- Every agent row, with the id NormalizeAgentId resolves it to today. The id is held as nvarchar(450) so the
-- rows can be grouped cheaply (agent_id itself is nvarchar(max), and can't be). A row whose id doesn't fit is
-- left alone; no id the importer produces comes anywhere near that length.
WITH keyed AS (
	SELECT ca.id, ca.agent_id, ca.[name], ca.is_custom_agent,
	       CAST(k.canonical_id AS nvarchar(450)) AS canonical_id,
	       CASE WHEN ca.agent_id LIKE @titleScopedId THEN 1 ELSE 0 END AS is_title_scoped,
	       CASE WHEN ca.agent_id = k.canonical_id THEN 1 ELSE 0 END AS is_canonical
	FROM dbo.copilot_agents AS ca
	CROSS APPLY (SELECT CASE
			WHEN ca.agent_id LIKE @titleScopedId THEN RIGHT(ca.agent_id, 36)
			WHEN ca.agent_id LIKE @spoWrapper + N'SPO[_]%' THEN STUFF(ca.agent_id, 1, LEN(@spoWrapper), N'')
			ELSE ca.agent_id
		END AS canonical_id) AS k
	WHERE ca.agent_id IS NOT NULL AND LEN(k.canonical_id) <= 450
),
grouped AS (
	SELECT keyed.id, keyed.agent_id, keyed.[name], keyed.is_title_scoped, keyed.is_canonical, keyed.canonical_id,
	       COUNT(*) OVER (PARTITION BY keyed.canonical_id) AS group_rows,
	       MIN(keyed.is_canonical) OVER (PARTITION BY keyed.canonical_id) AS group_all_canonical,
	       FIRST_VALUE(keyed.id) OVER (PARTITION BY keyed.canonical_id ORDER BY keyed.is_canonical DESC, keyed.id) AS survivor_id,
	       -- true wins, then false, else NULL. Counted rather than MAX()ed so a run by hand prints no
	       -- "Null value is eliminated by an aggregate" warning.
	       CASE WHEN SUM(CASE WHEN keyed.is_custom_agent = 1 THEN 1 ELSE 0 END) OVER (PARTITION BY keyed.canonical_id) > 0 THEN CAST(1 AS bit)
	            WHEN SUM(CASE WHEN keyed.is_custom_agent = 0 THEN 1 ELSE 0 END) OVER (PARTITION BY keyed.canonical_id) > 0 THEN CAST(0 AS bit)
	       END AS group_flag
	FROM keyed
)
SELECT g.id, g.agent_id, g.canonical_id, g.is_canonical, g.survivor_id, g.group_flag,
       FIRST_VALUE(g.[name]) OVER (PARTITION BY g.canonical_id ORDER BY
           CASE WHEN g.[name] IS NULL THEN 1 ELSE 0 END,
           CASE WHEN g.is_title_scoped = 1 THEN 0 WHEN g.id = g.survivor_id THEN 1 ELSE 2 END,
           CASE WHEN g.is_title_scoped = 1 THEN -g.id ELSE g.id END) AS best_name
INTO #repair_agents
FROM grouped AS g
WHERE g.group_rows > 1 OR g.group_all_canonical = 0;	-- only ids that need repairing

IF NOT EXISTS (SELECT 1 FROM #repair_agents)
BEGIN
	DROP TABLE #repair_agents;
	SELECT CAST(0 AS int) AS MergedAgentRows, CAST(0 AS int) AS RekeyedAgentRows,
	       CAST(0 AS bigint) AS MovedInteractions, CAST(0 AS int) AS PendingAgentRows;
	RETURN;
END

CREATE CLUSTERED INDEX IX_repair_agents_id ON #repair_agents (id);

-- 1. The kept row takes the canonical id, the best name and the group's flag.
SET @rekeyed = (SELECT COUNT(*) FROM #repair_agents WHERE id = survivor_id AND is_canonical = 0);

UPDATE ca
SET agent_id = r.canonical_id,
	[name] = COALESCE(r.best_name, ca.[name]),
	is_custom_agent = COALESCE(r.group_flag, ca.is_custom_agent)
FROM dbo.copilot_agents AS ca
INNER JOIN #repair_agents AS r
	ON r.id = ca.id AND r.id = r.survivor_id;

-- 2. Move the other rows' interactions onto it: one row at a time, the rows with the fewest interactions first,
--    in statements of at most @batch, until @maxMoved have moved in this run.
DECLARE losers CURSOR LOCAL FAST_FORWARD FOR
	SELECT r.id, r.survivor_id
	FROM #repair_agents AS r
	CROSS APPLY (SELECT COUNT_BIG(*) AS interactions FROM dbo.copilot_chats AS c WHERE c.agent_id = r.id) AS n
	WHERE r.id <> r.survivor_id
	ORDER BY n.interactions, r.id;

OPEN losers;
FETCH NEXT FROM losers INTO @loserId, @survivorId;
WHILE @@FETCH_STATUS = 0 AND @moved < @maxMoved
BEGIN
	SET @rows = 1;
	WHILE @rows > 0 AND @moved < @maxMoved
	BEGIN
		SET @take = CASE WHEN @maxMoved - @moved < @batch THEN @maxMoved - @moved ELSE @batch END;

		UPDATE TOP (@take) dbo.copilot_chats
		SET agent_id = @survivorId
		WHERE agent_id = @loserId;

		SET @rows = @@ROWCOUNT;
		SET @moved += @rows;
	END

	FETCH NEXT FROM losers INTO @loserId, @survivorId;
END
CLOSE losers;
DEALLOCATE losers;

-- 3. Delete the other rows once nothing points at them. One that is still referenced (this run's limit was
--    reached, or an older importer is still writing to it) is left for the next run.
DELETE ca
FROM dbo.copilot_agents AS ca
INNER JOIN #repair_agents AS r
	ON r.id = ca.id AND r.id <> r.survivor_id
WHERE NOT EXISTS (SELECT 1 FROM dbo.copilot_chats AS c WHERE c.agent_id = ca.id);

SET @merged = @@ROWCOUNT;

SET @pending = (SELECT COUNT(*)
                FROM #repair_agents AS r
                INNER JOIN dbo.copilot_agents AS ca ON ca.id = r.id
                WHERE r.id <> r.survivor_id);

DROP TABLE #repair_agents;

SELECT @merged AS MergedAgentRows, @rekeyed AS RekeyedAgentRows, @moved AS MovedInteractions, @pending AS PendingAgentRows;
