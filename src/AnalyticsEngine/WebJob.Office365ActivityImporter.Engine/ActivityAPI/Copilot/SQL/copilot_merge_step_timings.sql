-- ====================================================================================================
-- Copilot merge per-step timing — diagnostic ENABLE / SUMMARY / DISABLE script.
--
-- The shared Copilot merge (common_upsert_copilot_agents.sql) contains optional per-step timing that is
-- completely inert unless the table below exists. SQL Server's deferred name resolution means the
-- guarded "IF @dbg = 1 INSERT INTO dbo.copilot_merge_step_timings ..." statements never bind (and never
-- error) while the table is absent, so shipping the instrumentation has zero effect until you opt in.
--
-- HOW TO PROFILE A LIVE SYSTEM (run in a maintenance window if you like, but it is safe while importing):
--   1. Run STEP 1 (create the table) to switch instrumentation on.
--   2. Let the importer run for a while so several commit batches flow through the merge.
--   3. Run STEP 2 to see which step dominates (overall and per staging table: chatonly / sp / teams).
--   4. Run STEP 3 (drop the table) to switch instrumentation back off. Do not leave it enabled long-term:
--      it writes ~16 rows per merge invocation and the merge runs thousands of times per import cycle.
-- ====================================================================================================


-- ---- STEP 1: ENABLE ---------------------------------------------------------------------------------
IF OBJECT_ID('dbo.copilot_merge_step_timings', 'U') IS NULL
CREATE TABLE dbo.copilot_merge_step_timings (
    id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_copilot_merge_step_timings PRIMARY KEY,
    captured_at   DATETIME2(3)     NOT NULL CONSTRAINT DF_copilot_merge_step_timings_captured DEFAULT SYSUTCDATETIME(),
    batch_id      UNIQUEIDENTIFIER NOT NULL,     -- one value per merge invocation (a single commit batch)
    staging_table NVARCHAR(128)    NOT NULL,     -- ##..._copilot_chatonly / _sp / _teams
    step_name     VARCHAR(64)      NOT NULL,     -- e.g. insert_junction, insert_site_urls, resolve, TOTAL
    duration_ms   INT              NOT NULL,
    rows_affected INT              NULL          -- @@ROWCOUNT of the step (NULL for the TOTAL row)
);
GO


-- ---- STEP 2: SUMMARISE ------------------------------------------------------------------------------
-- 2a. Which merge step costs the most, overall (excludes the TOTAL bookkeeping row):
SELECT
    step_name,
    COUNT(*)                                              AS batches,
    CAST(SUM(CONVERT(BIGINT, duration_ms)) / 1000.0 AS DECIMAL(14,1)) AS total_s,
    AVG(duration_ms)                                      AS avg_ms,
    MAX(duration_ms)                                      AS max_ms,
    SUM(CONVERT(BIGINT, rows_affected))                   AS total_rows,
    CAST(100.0 * SUM(CONVERT(BIGINT, duration_ms))
         / NULLIF((SELECT SUM(CONVERT(BIGINT, duration_ms))
                   FROM dbo.copilot_merge_step_timings WHERE step_name = 'TOTAL'), 0) AS DECIMAL(5,1)) AS pct_of_total
FROM dbo.copilot_merge_step_timings
WHERE step_name <> 'TOTAL'
GROUP BY step_name
ORDER BY total_s DESC;

-- 2b. Same, split by staging table (chatonly is expected to dominate — same merge, far more rows):
SELECT
    staging_table,
    step_name,
    COUNT(*)                                              AS batches,
    CAST(SUM(CONVERT(BIGINT, duration_ms)) / 1000.0 AS DECIMAL(14,1)) AS total_s,
    AVG(duration_ms)                                      AS avg_ms
FROM dbo.copilot_merge_step_timings
WHERE step_name <> 'TOTAL'
GROUP BY staging_table, step_name
ORDER BY total_s DESC;

-- 2c. Sanity: sum of the measured steps vs the TOTAL row (any large gap = time in unmeasured statements):
SELECT
    CAST(SUM(CASE WHEN step_name <> 'TOTAL' THEN CONVERT(BIGINT, duration_ms) END) / 1000.0 AS DECIMAL(14,1)) AS measured_steps_s,
    CAST(SUM(CASE WHEN step_name =  'TOTAL' THEN CONVERT(BIGINT, duration_ms) END) / 1000.0 AS DECIMAL(14,1)) AS total_s
FROM dbo.copilot_merge_step_timings;


-- ---- STEP 3: DISABLE --------------------------------------------------------------------------------
-- IF OBJECT_ID('dbo.copilot_merge_step_timings', 'U') IS NOT NULL DROP TABLE dbo.copilot_merge_step_timings;
