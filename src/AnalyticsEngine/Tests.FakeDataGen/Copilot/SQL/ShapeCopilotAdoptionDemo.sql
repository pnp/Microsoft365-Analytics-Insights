/* ==========================================================================================
   ShapeCopilotAdoptionDemo.sql
   ------------------------------------------------------------------------------------------
   Shapes the Copilot activity in a FAKE / DEMO analytics database so that every figure on
   the Copilot Adoption page tells one coherent, internally-consistent story - the sort of
   tenant the tool is worth demonstrating against.

   WHY THIS EXISTS
   ---------------
   Tests.FakeDataGen scatters interactions across users at random, which is exactly right for
   importer and volume testing but lands the whole licensed population in one band: the
   funnel, the treemap, the profile radar and every "who should I target" panel show nothing,
   and the tool looks broken when it is working perfectly. CopilotAdoptionScenarioGenerator
   fixes that while generating a fresh database; this script does the same job for a database
   that already has data, and lets you dial the exact headline numbers a demo needs.

   It also removes a common source of confusion: the KPI cards and the funnel are both built
   from the same CopilotAdoptionSummary, so they cannot disagree with each other. If two
   screenshots look inconsistent, they were taken with different values of the "period"
   drop-down, not against different data.

   WHAT IT PRODUCES (at the defaults below, on a ~250-user database)
   -----------------------------------------------------------------
        Copilot licences            150
        Adoption rate               86%     (129 of 150 active in the 28-day period)
        Habitual users              61.3%   (92 users scoring >= 50)
        Reclaimable licences        21      (12 never used, 9 dormant)
        Average engagement          54      (median 61)
        Cowork adoption             10.7%   (16 users)
        Using Copilot unlicensed    18
        Recommended for a licence   ~56

        Funnel: 150 -> 138 -> 129 -> 92 -> 38

   Every six-month trend series rises towards the present, the agent inventory contains all
   four health verdicts, and ten departments span the range from leading to lagging.

   HOW IT WORKS
   ------------
   Nothing is deleted. Existing Copilot interactions are re-dated into each user's own
   history - or pushed past the 365-day history horizon for the cohorts that must look as
   though they never used Copilot - so no child rows are ever orphaned. Fresh in-window
   interactions are then planted to hit each user's target band exactly, tagged with a
   distinctive copilot_log_version so a re-run replaces them instead of stacking a second
   cohort on top of the first.

   Targets are expressed in the three signals CopilotAdoptionScoring actually measures -
   distinct active dates, interactions per active date, and distinct app_host values - so the
   bands are produced by the real scoring code rather than asserted here. Keep the persona
   table in step with CopilotAdoptionOptions if those weights or thresholds ever change.

   USAGE
   -----
       sqlcmd -S <server> -d <demo database> -E -i ShapeCopilotAdoptionDemo.sql -b

   Set @ConfirmDemoDatabase to 1 first. It ships as 0 on purpose: this script rewrites licence
   assignments and audit-event timestamps, and must never be run against a customer database.

   Everything else is derived from whatever the target database contains - departments, user
   counts and agents are read, not assumed - so the same script works on any demo tenant.

   The web API caches each analysis for 10 minutes (CopilotAdoptionAPIController.CacheMinutes),
   so recycle the site or wait before taking screenshots.

   All values are synthetic. Re-runnable: running it twice produces the same shape.
   ========================================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

/* ------------------------------------------------------------------------ Parameters ----- */
DECLARE @ConfirmDemoDatabase bit = 0;   -- <<< set to 1 to run. DEMO DATABASES ONLY.

DECLARE @TargetSeats int = 150;         -- Copilot licences to assign (clamped to the population)
DECLARE @WindowDays  int = 28;          -- must match the period selected in the UI
DECLARE @DemandUsers int = 18;          -- unlicensed people already using Copilot Chat
DECLARE @CoreAgents  int = 60;          -- agents that will look actively maintained

-- Band mix as a share of the licensed population. Established takes the remainder, so these
-- need not total 100. The defaults reproduce 38 / 54 / 26 / 11 / 9 / 12 at 150 seats.
DECLARE @ShareChampion   decimal(5,3) = 0.253;
DECLARE @ShareDeveloping decimal(5,3) = 0.173;
DECLARE @ShareTrialling  decimal(5,3) = 0.073;
DECLARE @ShareDormant    decimal(5,3) = 0.060;
DECLARE @ShareNeverUsed  decimal(5,3) = 0.080;

IF @ConfirmDemoDatabase <> 1
BEGIN
    RAISERROR('Refusing to run: set @ConfirmDemoDatabase = 1 first. This script rewrites licence assignments and audit-event timestamps and is for FAKE / DEMO databases only.', 16, 1);
    SET NOEXEC ON;
END

/* -------------------------------------------------------------------------- Derived ------ */
DECLARE @today date     = CAST(SYSUTCDATETIME() AS date);
DECLARE @from  datetime = DATEADD(day, -(@WindowDays - 1), CAST(@today AS datetime));
DECLARE @snap  datetime = DATEADD(day, -3, CAST(@today AS datetime));   -- UsageReportLagDays = 3
DECLARE @users int      = (SELECT COUNT(*) FROM dbo.users);

-- Always leave an unlicensed population behind: it is what the licence-opportunity half of
-- the tool is about, and a tenant with nobody unlicensed has nothing to recommend.
IF @TargetSeats > @users - 25 SET @TargetSeats = @users - 25;
IF @TargetSeats < 20
BEGIN
    RAISERROR('Not enough rows in dbo.users to shape a meaningful demo (about 45 are needed). Run Tests.FakeDataGen first.', 16, 1);
    SET NOEXEC ON;
END

/* ----------------------------------------------------------- Copilot SKU and operation --- */
DECLARE @sku int = (
    SELECT TOP (1) id FROM dbo.license_types
    WHERE sku_id LIKE 'M365[_]COPILOT%' OR sku_id LIKE 'MICROSOFT[_]365[_]COPILOT%');

IF @sku IS NULL
BEGIN
    INSERT INTO dbo.license_types (sku_id, name) VALUES (N'Microsoft_365_Copilot', N'Microsoft 365 Copilot');
    SET @sku = CAST(SCOPE_IDENTITY() AS int);
    PRINT 'Created a Microsoft 365 Copilot licence type (none was present).';
END

DECLARE @op int = (
    SELECT TOP (1) au.operation_id
    FROM dbo.copilot_chats c
    JOIN dbo.audit_events au ON au.id = c.event_id
    WHERE au.operation_id IS NOT NULL
    GROUP BY au.operation_id
    ORDER BY COUNT(*) DESC);

IF @op IS NULL SET @op = (SELECT TOP (1) id FROM dbo.event_operations WHERE operation_name = 'CopilotInteraction');
IF @op IS NULL
BEGIN
    INSERT INTO dbo.event_operations (operation_name) VALUES ('CopilotInteraction');
    SET @op = CAST(SCOPE_IDENTITY() AS int);
END

PRINT CONCAT('Window: ', CONVERT(char(10), @from, 120), ' .. ', CONVERT(char(10), @today, 120),
             '   seats: ', @TargetSeats, '   SKU id: ', @sku, '   operation id: ', @op);

/* --------------------------------------------------- 0. Undo any previous shaping -------- */
IF OBJECT_ID('tempdb..#oldplant') IS NOT NULL DROP TABLE #oldplant;
SELECT event_id AS id INTO #oldplant FROM dbo.copilot_chats WHERE copilot_log_version = N'1.0-demo';
DELETE c FROM dbo.copilot_chats c JOIN #oldplant o ON o.id = c.event_id;
DELETE a FROM dbo.audit_events  a JOIN #oldplant o ON o.id = a.id;

DECLARE @undone int = (SELECT COUNT(*) FROM #oldplant);
IF @undone > 0 PRINT CONCAT('Removed ', @undone, ' interactions planted by a previous run.');

/* ------------------------------------------------------------------ 1. Department plan ---
   Derived from whatever departments the database actually has, so the script is portable.
   Seats are concentrated in the largest departments so each of them clears
   CopilotAdoptionOptions.MinSeatsPerSegment (5) and appears in the per-department charts,
   which show at most TopSegments (10) segments. Everything else gets a token allocation and
   sits legitimately below the reporting threshold - which is what the threshold is for.
   Maturity is assigned by department rank, so the charts show leaders and laggards rather
   than one flat band.                                                                      */
IF OBJECT_ID('tempdb..#deptplan') IS NOT NULL DROP TABLE #deptplan;
CREATE TABLE #deptplan (dept nvarchar(200) PRIMARY KEY, size int, rnk int, seats int NULL, maturity int);

INSERT INTO #deptplan (dept, size, rnk, maturity)
SELECT dept, size, rnk,
       CASE WHEN rnk <= 2 THEN 4 WHEN rnk <= 5 THEN 3 WHEN rnk <= 8 THEN 2
            WHEN rnk <= 10 THEN 1 ELSE 2 END
FROM (
    SELECT ISNULL(d.name, N'(no department)') AS dept,
           COUNT(*) AS size,
           ROW_NUMBER() OVER (ORDER BY COUNT(*) DESC, ISNULL(d.name, N'(no department)')) AS rnk
    FROM dbo.users u
    LEFT JOIN dbo.user_departments d ON d.id = u.department_id
    GROUP BY ISNULL(d.name, N'(no department)')
) AS s;

DECLARE @deptCount int = (SELECT COUNT(*) FROM #deptplan);
DECLARE @reportable int = CASE WHEN @deptCount < 10 THEN @deptCount ELSE 10 END;
IF @TargetSeats / 8 < @reportable SET @reportable = @TargetSeats / 8;   -- ~8 seats each, minimum
IF @reportable < 1 SET @reportable = 1;

UPDATE #deptplan SET seats = CASE WHEN size < 3 THEN size ELSE 3 END WHERE rnk > @reportable;

DECLARE @tokenSeats int = (SELECT ISNULL(SUM(seats), 0) FROM #deptplan WHERE rnk > @reportable);
DECLARE @mainSize   int = (SELECT SUM(size) FROM #deptplan WHERE rnk <= @reportable);
DECLARE @mainSeats  int = @TargetSeats - @tokenSeats;
IF @mainSeats > @mainSize SET @mainSeats = @mainSize;

-- Largest-remainder allocation: proportional to department size and exact in total.
;WITH cum AS (
    SELECT dept, rnk, SUM(size) OVER (ORDER BY rnk ROWS UNBOUNDED PRECEDING) AS cum_size
    FROM #deptplan WHERE rnk <= @reportable
),
alloc AS (
    SELECT dept,
           CAST(ROUND(@mainSeats * 1.0 * cum_size / @mainSize, 0) AS int)
         - LAG(CAST(ROUND(@mainSeats * 1.0 * cum_size / @mainSize, 0) AS int), 1, 0)
               OVER (ORDER BY rnk) AS seats
    FROM cum
)
UPDATE p SET p.seats = a.seats FROM #deptplan p JOIN alloc a ON a.dept = p.dept;

/* ------------------------------------------------- 2. Who holds a Copilot licence -------- */
IF OBJECT_ID('tempdb..#licensed') IS NOT NULL DROP TABLE #licensed;
CREATE TABLE #licensed (user_id int PRIMARY KEY, dept nvarchar(200), maturity int, enabled bit);

DECLARE @disabledSeats int = (
    SELECT CASE WHEN c < 6 THEN c ELSE 6 END
    FROM (SELECT COUNT(*) AS c FROM dbo.users WHERE ISNULL(account_enabled, 1) = 0) AS d);

;WITH forced AS (                            -- a few disabled accounts deliberately keep a
    SELECT TOP (@disabledSeats) u.id         -- licence: the clearest reclaim of all, and a
    FROM dbo.users u                         -- good demo talking point
    WHERE ISNULL(u.account_enabled, 1) = 0
    ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(u.id AS varchar(20)) + '|forced')) & 2147483647)
),
ranked AS (
    SELECT u.id,
           ISNULL(d.name, N'(no department)') AS dept,
           ISNULL(u.account_enabled, 1) AS enabled,
           ROW_NUMBER() OVER (
               PARTITION BY ISNULL(d.name, N'(no department)')
               ORDER BY CASE WHEN f.id IS NOT NULL THEN -1 ELSE
                        (CHECKSUM(HASHBYTES('MD5', CAST(u.id AS varchar(20)) + '|seat')) & 2147483647) % 100000 END) AS rn
    FROM dbo.users u
    LEFT JOIN dbo.user_departments d ON d.id = u.department_id
    LEFT JOIN forced f ON f.id = u.id
)
INSERT INTO #licensed (user_id, dept, maturity, enabled)
SELECT r.id, r.dept, p.maturity, r.enabled
FROM ranked r
JOIN #deptplan p ON p.dept = r.dept
WHERE r.rn <= p.seats;

/* --------------------------------------------------- 3. Rank users, allocate bands -------
   One ordering decides everything: department maturity plus a deterministic per-user jitter
   wide enough that the bands overlap between departments. Disabled accounts sink to the
   bottom, so they land in "never used" and surface as reclaimable licences.                */
DECLARE @seats        int = (SELECT COUNT(*) FROM #licensed);
DECLARE @nChampion    int = CAST(ROUND(@seats * @ShareChampion,   0) AS int);
DECLARE @nDeveloping  int = CAST(ROUND(@seats * @ShareDeveloping, 0) AS int);
DECLARE @nTrialling   int = CAST(ROUND(@seats * @ShareTrialling,  0) AS int);
DECLARE @nDormant     int = CAST(ROUND(@seats * @ShareDormant,    0) AS int);
DECLARE @nNeverUsed   int = CAST(ROUND(@seats * @ShareNeverUsed,  0) AS int);
DECLARE @nEstablished int = @seats - @nChampion - @nDeveloping - @nTrialling - @nDormant - @nNeverUsed;

IF @nEstablished < 0
BEGIN
    RAISERROR('Band shares total more than 100 percent - reduce them so Established has a remainder.', 16, 1);
    SET NOEXEC ON;
END

IF OBJECT_ID('tempdb..#plan') IS NOT NULL DROP TABLE #plan;
CREATE TABLE #plan (
    user_id      int PRIMARY KEY,
    dept         nvarchar(200),
    band         varchar(20) NOT NULL,
    rank_in_band int NOT NULL,
    days         int NOT NULL DEFAULT 0,   -- distinct active days inside the window
    per_day      int NOT NULL DEFAULT 0,   -- interactions per active day
    apps         int NOT NULL DEFAULT 0,   -- distinct app_host values
    start_age    int NOT NULL DEFAULT 120, -- days ago this user first used Copilot
    last_age     int NOT NULL DEFAULT 29,  -- days ago their history stops
    keep_hist    int NOT NULL DEFAULT 999  -- how many historic interactions stay visible
);

;WITH scored AS (
    SELECT l.user_id, l.dept,
           ROW_NUMBER() OVER (
               ORDER BY CASE WHEN l.enabled = 0 THEN 0 ELSE 1 END DESC,
                        l.maturity * 300
                      + (CHECKSUM(HASHBYTES('MD5', CAST(l.user_id AS varchar(20)) + '|band')) & 2147483647) % 1400 DESC,
                        l.user_id) AS rnk
    FROM #licensed l
)
INSERT INTO #plan (user_id, dept, band, rank_in_band)
SELECT user_id, dept, band, ROW_NUMBER() OVER (PARTITION BY band ORDER BY rnk)
FROM (
    SELECT user_id, dept, rnk,
           CASE WHEN rnk <= @nChampion                                              THEN 'Champion'
                WHEN rnk <= @nChampion + @nEstablished                              THEN 'Established'
                WHEN rnk <= @nChampion + @nEstablished + @nDeveloping                THEN 'Developing'
                WHEN rnk <= @nChampion + @nEstablished + @nDeveloping + @nTrialling  THEN 'Trialling'
                WHEN rnk <= @nChampion + @nEstablished + @nDeveloping + @nTrialling
                          + @nDormant                                               THEN 'Dormant'
                ELSE                                                                     'NeverUsed' END AS band
    FROM scored) AS b;

/* --------------------------------------------------- 4. Engagement personas per band -----
   The scores in the comments are what CopilotAdoptionScoring produces from these three
   signals at WindowDays = 28 with the default weights (frequency 0.5 / depth 0.3 / breadth
   0.2; targets 12 active days, 5 interactions per active day, 3 apps). Several shapes per
   band, so the radar, habit and profile charts show real variety rather than one repeated
   silhouette at every score.                                                               */
IF OBJECT_ID('tempdb..#persona') IS NOT NULL DROP TABLE #persona;
CREATE TABLE #persona (band varchar(20), slot int, days int, per_day int, apps int, PRIMARY KEY (band, slot));

INSERT INTO #persona (band, slot, days, per_day, apps) VALUES
    -- Champion (score >= 75)
    ('Champion',    0, 16, 7, 4),   -- 100.0
    ('Champion',    1, 13, 6, 2),   --  93.3
    ('Champion',    2, 12, 3, 3),   --  88.0
    ('Champion',    3,  9, 5, 3),   --  87.5
    ('Champion',    4, 14, 4, 2),   --  87.3
    ('Champion',    5, 10, 4, 3),   --  85.7
    ('Champion',    6,  8, 6, 3),   --  83.3
    -- Established (50 - 74.9)
    ('Established', 0,  6, 4, 3),   --  69.0
    ('Established', 1, 12, 2, 1),   --  68.7
    ('Established', 2,  8, 3, 2),   --  64.7
    ('Established', 3,  9, 2, 2),   --  62.8
    ('Established', 4, 14, 1, 1),   --  62.7  frequent but shallow
    ('Established', 5,  7, 2, 3),   --  61.2
    ('Established', 6, 10, 1, 2),   --  61.0
    ('Established', 7,  5,12, 1),   --  57.5  deep but narrow
    -- Developing (25 - 49.9)
    ('Developing',  0,  4, 2, 5),   --  48.7  broad but occasional
    ('Developing',  1,  3, 3, 2),   --  43.8
    ('Developing',  2,  7, 1, 1),   --  41.9
    ('Developing',  3,  5, 2, 1),   --  39.5
    ('Developing',  4,  6, 1, 1),   --  37.7
    -- Trialling (> 0 - 24.9)
    ('Trialling',   0,  1, 2, 1),   --  22.9
    ('Trialling',   1,  2, 1, 1),   --  21.0
    ('Trialling',   2,  1, 1, 1);   --  16.9

IF OBJECT_ID('tempdb..#pcount') IS NOT NULL DROP TABLE #pcount;
SELECT band, COUNT(*) AS variants INTO #pcount FROM #persona GROUP BY band;

UPDATE p
SET p.days      = s.days,
    p.per_day   = s.per_day,
    p.apps      = s.apps,
    p.keep_hist = CASE p.band WHEN 'Champion' THEN 999 WHEN 'Established' THEN 999
                              WHEN 'Developing' THEN 12 ELSE 3 END
FROM #plan p
JOIN #pcount c  ON c.band = p.band
JOIN #persona s ON s.band = p.band AND s.slot = (p.rank_in_band - 1) % c.variants
WHERE p.band IN ('Champion', 'Established', 'Developing', 'Trialling');

-- Stagger when each user first picked Copilot up, within their band, so the six-month trend
-- ramps smoothly instead of whole cohorts appearing on the same week.
UPDATE #plan
SET start_age = last_age + 14
              + CASE band WHEN 'Champion' THEN 60 WHEN 'Established' THEN 40
                          WHEN 'Developing' THEN 20 ELSE 5 END
              + (rank_in_band * 37) % CASE band WHEN 'Champion' THEN 70 WHEN 'Established' THEN 60
                                                WHEN 'Developing' THEN 40 ELSE 10 END
WHERE band IN ('Champion', 'Established', 'Developing', 'Trialling');

-- Dormant: used Copilot, then stopped 40-110 days ago. Never used: no trace inside 365 days.
UPDATE #plan
SET last_age  = 40 + (CHECKSUM(HASHBYTES('MD5', CAST(user_id AS varchar(20)) + '|dorm')) & 2147483647) % 70,
    keep_hist = 999
WHERE band = 'Dormant';
UPDATE #plan SET start_age = last_age + 60 WHERE band = 'Dormant';
UPDATE #plan SET start_age = 0, last_age = 0, keep_hist = 0 WHERE band = 'NeverUsed';

/* ------------------------------------------------------------ 5. Apply the licence set --- */
DELETE FROM dbo.user_license_type_lookups WHERE license_type_id = @sku;
INSERT INTO dbo.user_license_type_lookups (user_id, license_type_id)
SELECT user_id, @sku FROM #plan;

PRINT CONCAT('Copilot licences assigned: ', @seats,
             '  (champion ', @nChampion, ', established ', @nEstablished, ', developing ', @nDeveloping,
             ', trialling ', @nTrialling, ', dormant ', @nDormant, ', never used ', @nNeverUsed, ')');

/* --------------------------------------- 6. The unlicensed "proven demand" cohort --------
   People with no Copilot seat who are already using Copilot Chat. This is the headline
   "proven demand" figure and the strongest input to the licence business case. Chosen before
   the history is re-dated, because every OTHER unlicensed user has their Copilot history
   pushed past the horizon - so unlicensed use reads as a trend growing towards today rather
   than one that collapsed at the window edge.                                              */
IF OBJECT_ID('tempdb..#demand') IS NOT NULL DROP TABLE #demand;
CREATE TABLE #demand (user_id int PRIMARY KEY, rn int NOT NULL, days int NOT NULL, per_day int NOT NULL);

;WITH pool AS (
    SELECT u.id,
           ROW_NUMBER() OVER (ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(u.id AS varchar(20)) + '|demand')) & 2147483647)) AS rn
    FROM dbo.users u
    WHERE ISNULL(u.account_enabled, 1) = 1
      AND NOT EXISTS (SELECT 1 FROM #plan p WHERE p.user_id = u.id)
)
INSERT INTO #demand (user_id, rn, days, per_day)
SELECT id, rn,
       CASE WHEN rn <= 10 THEN 8 ELSE 4 + (rn % 4) END,   -- the 10 heaviest tip the business case
       CASE WHEN rn <= 10 THEN 3 ELSE 2 END
FROM pool WHERE rn <= @DemandUsers;

/* --------------------------------------------------- 7. Re-date the existing history -----
   Every existing Copilot interaction is moved OUT of the reporting window and into that
   user's own history, so the in-window picture is exactly what section 9 plants and nothing
   else. Density rises towards the present (a linear ramp in interactions per week, roughly
   3.3x from the oldest week to the newest). Interactions beyond a band's keep_hist
   allowance, and everything belonging to a cohort that must look untouched, are pushed past
   the 365-day history horizon rather than deleted, so no child rows are orphaned.          */
IF OBJECT_ID('tempdb..#hist') IS NOT NULL DROP TABLE #hist;
CREATE TABLE #hist (id uniqueidentifier PRIMARY KEY, new_ts datetime NOT NULL);

;WITH chats AS (
    SELECT au.id, au.user_id,
           ROW_NUMBER() OVER (PARTITION BY au.user_id ORDER BY au.time_stamp DESC, au.id) AS recency,
           COUNT(*)     OVER (PARTITION BY au.user_id)                                    AS cnt
    FROM dbo.audit_events au
    JOIN dbo.copilot_chats c ON c.event_id = au.id
),
shaped AS (
    SELECT ch.id, ch.recency,
           CASE WHEN p.user_id IS NOT NULL THEN p.band
                WHEN d.user_id IS NOT NULL THEN 'Demand'
                ELSE 'NeverUsed' END AS band,
           CASE WHEN p.user_id IS NOT NULL THEN p.keep_hist
                WHEN d.user_id IS NOT NULL THEN 30
                ELSE 0 END AS keep_hist,
           CAST(CASE WHEN p.user_id IS NOT NULL THEN p.last_age  ELSE 29 END AS float) AS last_age,
           CAST(CASE WHEN p.user_id IS NOT NULL THEN p.start_age ELSE 75 END AS float) AS start_age,
           -- q = 0 for this user's newest interaction, 1 for their oldest
           CASE WHEN ch.cnt <= 1 THEN 0.0 ELSE (ch.recency - 1) * 1.0 / (ch.cnt - 1) END AS q
    FROM chats ch
    LEFT JOIN #plan   p ON p.user_id = ch.user_id
    LEFT JOIN #demand d ON d.user_id = ch.user_id
),
aged AS (
    SELECT id, band, keep_hist, recency, last_age, start_age,
           -- inverse CDF of a linearly-decaying density: a smooth ramp, not a uniform smear
           (700.0 - SQRT(490000.0 - 446319.0 * q)) / 491.0 AS x
    FROM shaped
)
INSERT INTO #hist (id, new_ts)
SELECT id,
       DATEADD(minute,
               480 + (CHECKSUM(HASHBYTES('MD5', CAST(id AS varchar(40)))) & 2147483647) % 600,
               CAST(DATEADD(day,
                   -CASE
                        WHEN band = 'NeverUsed' OR recency > keep_hist
                            THEN 400 + (CHECKSUM(HASHBYTES('MD5', CAST(id AS varchar(40)) + '|old')) & 2147483647) % 120
                        ELSE CAST(ROUND(last_age + (start_age - last_age) * x, 0) AS int)
                    END,
                   CAST(@today AS datetime)) AS datetime))
FROM aged;

UPDATE au SET au.time_stamp = h.new_ts
FROM dbo.audit_events au JOIN #hist h ON h.id = au.id;

DECLARE @rehomed int = (SELECT COUNT(*) FROM #hist);
PRINT CONCAT('Historic Copilot interactions re-dated: ', @rehomed);

/* ----------------------------------------- 8. Shared scaffolding for planted activity ---- */
IF OBJECT_ID('tempdb..#days') IS NOT NULL DROP TABLE #days;
CREATE TABLE #days (d date PRIMARY KEY, idx int NOT NULL);

-- Weekdays only: the scorer's frequency target assumes a five-day working week. The weekday
-- test counts from a known Monday so it is independent of SET DATEFIRST.
;WITH n AS (SELECT TOP (@WindowDays) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS i FROM sys.all_objects),
     wd AS (SELECT DATEADD(day, i, CAST(@from AS date)) AS d FROM n
            WHERE DATEDIFF(day, '19000101', DATEADD(day, i, CAST(@from AS date))) % 7 <= 4)
INSERT INTO #days (d, idx) SELECT d, ROW_NUMBER() OVER (ORDER BY d) FROM wd;

IF OBJECT_ID('tempdb..#apps') IS NOT NULL DROP TABLE #apps;
CREATE TABLE #apps (idx int PRIMARY KEY, host nvarchar(100) NOT NULL);
INSERT INTO #apps (idx, host) VALUES
    (1, N'M365Chat'), (2, N'Teams'), (3, N'Word'), (4, N'Outlook'), (5, N'Excel'), (6, N'PowerPoint');

-- Agents that will look actively maintained ("Keep"): used in-window by several people.
-- TOP must be applied in a derived table - a window function in the same SELECT is evaluated
-- over the whole table before TOP, which leaves idx sparse and mostly unmatchable.
IF OBJECT_ID('tempdb..#coreagents') IS NOT NULL DROP TABLE #coreagents;
SELECT id, ROW_NUMBER() OVER (ORDER BY id) AS idx
INTO #coreagents
FROM (
    SELECT TOP (@CoreAgents) id
    FROM dbo.copilot_agents
    ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(id AS varchar(20)) + '|core')) & 2147483647)
) AS picked;

DECLARE @agentCount int = (SELECT COUNT(*) FROM #coreagents);

/* ---------------------------------------------- 9. Plant the in-window interactions ------ */
IF OBJECT_ID('tempdb..#newchats') IS NOT NULL DROP TABLE #newchats;
CREATE TABLE #newchats (
    id uniqueidentifier NOT NULL PRIMARY KEY,
    user_id int NOT NULL,
    ts datetime NOT NULL,
    host nvarchar(100) NOT NULL,
    agent_id int NULL);

;WITH picked AS (      -- the distinct active days each user gets, biased slightly towards
    SELECT p.user_id, p.band, p.per_day, p.apps, d.d,   -- recent days so the current,
           ROW_NUMBER() OVER (                          -- part-finished week is not a cliff
               PARTITION BY p.user_id
               ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(p.user_id AS varchar(20)) + '|'
                                  + CONVERT(char(8), d.d, 112))) & 2147483647) % 1000
                        - d.idx * 12) AS pick
    FROM #plan p CROSS JOIN #days d
    WHERE p.days > 0
),
kept AS (
    SELECT k.user_id, k.band, k.per_day, k.apps, k.d
    FROM picked k JOIN #plan p ON p.user_id = k.user_id
    WHERE k.pick <= p.days
),
expanded AS (
    SELECT k.user_id, k.band, k.apps, k.d, x.n AS slot,
           ROW_NUMBER() OVER (PARTITION BY k.user_id ORDER BY k.d, x.n) AS seq
    FROM kept k
    CROSS APPLY (SELECT TOP (k.per_day) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n
                 FROM sys.all_objects) AS x
)
INSERT INTO #newchats (id, user_id, ts, host, agent_id)
SELECT NEWID(),
       e.user_id,
       DATEADD(minute,
               8 * 60 + (e.slot * 47 + (CHECKSUM(HASHBYTES('MD5', CAST(e.user_id AS varchar(20)) + '|'
                          + CONVERT(char(8), e.d, 112) + '|' + CAST(e.slot AS varchar(10)))) & 2147483647) % 31) % 600,
               CAST(e.d AS datetime)),
       a.host,
       -- An agent on each user's first two interactions of the day, plus ~30% of the rest:
       -- the estate then has a healthy "Keep" tier, and agent reach tracks active users
       -- instead of falling off a cliff at the window boundary.
       CASE WHEN @agentCount = 0 THEN NULL
            WHEN e.slot <= 1 THEN ca.id
            WHEN (CHECKSUM(HASHBYTES('MD5', CAST(e.user_id AS varchar(20)) + '|use|'
                  + CAST(e.seq AS varchar(10)))) & 2147483647) % 10 < 3 THEN ca.id END
FROM expanded e
JOIN #apps a ON a.idx = (e.seq % e.apps) + 1
LEFT JOIN #coreagents ca
       ON @agentCount > 0
      AND ca.idx = ((CHECKSUM(HASHBYTES('MD5', CAST(e.user_id AS varchar(20)) + '|ag|'
                     + CAST(e.seq AS varchar(10)))) & 2147483647) % @agentCount) + 1;

INSERT INTO dbo.audit_events (id, time_stamp, event_data, operation_id, user_id)
SELECT id, ts, NULL, @op, user_id FROM #newchats;

INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total,
                               copilot_credit_estimate_json, thread_id, client_region, copilot_log_version,
                               user_id, time_stamp)
SELECT id, host, agent_id, NULL, NULL,
       LOWER(CONVERT(varchar(36), id)), N'westeurope', N'1.0-demo',
       user_id, ts
FROM #newchats;

DECLARE @planted int = (SELECT COUNT(*) FROM #newchats);
PRINT CONCAT('In-window licensed interactions planted: ', @planted);

/* --------------------------------------------- 10. Give the dormant cohort a past --------
   "Dormant" versus "never used" is the most valuable distinction this tool makes, and it
   rests entirely on there being prior activity. On a database with little or no existing
   Copilot history the re-dating above leaves nothing behind, so top it up here.            */
IF OBJECT_ID('tempdb..#dormtop') IS NOT NULL DROP TABLE #dormtop;
CREATE TABLE #dormtop (id uniqueidentifier PRIMARY KEY, user_id int NOT NULL, ts datetime NOT NULL);

;WITH missing AS (
    SELECT p.user_id, p.last_age
    FROM #plan p
    WHERE p.band = 'Dormant'
      AND NOT EXISTS (
          SELECT 1
          FROM dbo.audit_events au
          JOIN dbo.copilot_chats c ON c.event_id = au.id
          WHERE au.user_id = p.user_id
            AND au.time_stamp >= DATEADD(day, -364, CAST(@today AS datetime))
            AND au.time_stamp <  @from)
),
spread AS (
    SELECT m.user_id, m.last_age, x.n
    FROM missing m
    CROSS APPLY (SELECT TOP (14) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects) AS x
)
INSERT INTO #dormtop (id, user_id, ts)
SELECT NEWID(), user_id,
       DATEADD(minute, 540 + (n * 71) % 420,
               CAST(DATEADD(day, -(last_age + n * 3), CAST(@today AS datetime)) AS datetime))
FROM spread;

INSERT INTO dbo.audit_events (id, time_stamp, event_data, operation_id, user_id)
SELECT id, ts, NULL, @op, user_id FROM #dormtop;

INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total,
                               copilot_credit_estimate_json, thread_id, client_region, copilot_log_version,
                               user_id, time_stamp)
SELECT id, N'M365Chat', NULL, NULL, NULL, LOWER(CONVERT(varchar(36), id)), N'westeurope', N'1.0-demo',
       user_id, ts
FROM #dormtop;

DECLARE @dormTopped int = (SELECT COUNT(DISTINCT user_id) FROM #dormtop);
IF @dormTopped > 0 PRINT CONCAT('Dormant users given a Copilot past: ', @dormTopped);

/* ---------------------------------------------- 11. Microsoft 365 Copilot Cowork ---------
   Cowork only gets its own KPI card when Cowork activity is actually detected, so a demo
   without it silently loses a card. Cowork interactions are added ONLY to Champions whose
   depth and breadth are already capped, and only on days they were already active, so the
   extra volume cannot move anybody's engagement score or band.                             */
DECLARE @coworkAgent int = (
    SELECT TOP (1) id FROM dbo.copilot_agents
    WHERE agent_id LIKE 'Copilot.M365Copilot.Cowork%' OR name LIKE '%Cowork%');

IF @coworkAgent IS NULL AND @agentCount > 0
BEGIN
    -- Re-brand an otherwise unused agent rather than inventing an id, so nothing is orphaned.
    SELECT TOP (1) @coworkAgent = ag.id
    FROM dbo.copilot_agents ag
    WHERE NOT EXISTS (SELECT 1 FROM #coreagents ca WHERE ca.id = ag.id)
    ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(ag.id AS varchar(20)) + '|cowork')) & 2147483647);

    UPDATE dbo.copilot_agents
    SET agent_id = N'Copilot.M365Copilot.CoworkAgent',
        name = N'Microsoft 365 Copilot Cowork',
        is_custom_agent = 0
    WHERE id = @coworkAgent;
END

IF OBJECT_ID('tempdb..#cowork') IS NOT NULL DROP TABLE #cowork;
CREATE TABLE #cowork (id uniqueidentifier PRIMARY KEY, user_id int NOT NULL, ts datetime NOT NULL);

;WITH capped AS (      -- Champions already at 5+ interactions per active day and 3+ surfaces
    SELECT p.user_id FROM #plan p WHERE p.band = 'Champion' AND p.per_day >= 5 AND p.apps >= 3
),
slots AS (
    SELECT n.user_id, n.ts,
           ROW_NUMBER() OVER (PARTITION BY n.user_id ORDER BY n.ts DESC) AS rn
    FROM #newchats n JOIN capped c ON c.user_id = n.user_id
)
INSERT INTO #cowork (id, user_id, ts)
SELECT NEWID(), user_id, DATEADD(minute, 7, ts) FROM slots WHERE rn <= 9;

INSERT INTO dbo.audit_events (id, time_stamp, event_data, operation_id, user_id)
SELECT id, ts, NULL, @op, user_id FROM #cowork;

INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total,
                               copilot_credit_estimate_json, thread_id, client_region, copilot_log_version,
                               user_id, time_stamp)
SELECT id, N'cowork', @coworkAgent, NULL, NULL,
       LOWER(CONVERT(varchar(36), id)), N'westeurope', N'1.0-demo',
       user_id, ts
FROM #cowork;

DECLARE @coworkUsers int = (SELECT COUNT(DISTINCT user_id) FROM #cowork);
DECLARE @coworkInt   int = (SELECT COUNT(*) FROM #cowork);
PRINT CONCAT('Cowork users planted: ', @coworkUsers, ' (', @coworkInt, ' interactions)');

/* ------------------------------------------ 12. Unlicensed Copilot Chat interactions ----- */
IF OBJECT_ID('tempdb..#newunlic') IS NOT NULL DROP TABLE #newunlic;
CREATE TABLE #newunlic (id uniqueidentifier PRIMARY KEY, user_id int NOT NULL, ts datetime NOT NULL, host nvarchar(100) NOT NULL);

;WITH picked AS (
    SELECT d.user_id, d.per_day, dd.d,
           ROW_NUMBER() OVER (
               PARTITION BY d.user_id
               ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(d.user_id AS varchar(20)) + '|u|'
                                  + CONVERT(char(8), dd.d, 112))) & 2147483647) % 1000
                        - dd.idx * 12) AS pick
    FROM #demand d CROSS JOIN #days dd
),
kept AS (
    SELECT k.user_id, k.per_day, k.d
    FROM picked k JOIN #demand d ON d.user_id = k.user_id
    WHERE k.pick <= d.days
),
expanded AS (
    SELECT k.user_id, k.d, x.n AS slot,
           ROW_NUMBER() OVER (PARTITION BY k.user_id ORDER BY k.d, x.n) AS seq
    FROM kept k
    CROSS APPLY (SELECT TOP (k.per_day) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) - 1 AS n FROM sys.all_objects) AS x
)
INSERT INTO #newunlic (id, user_id, ts, host)
SELECT NEWID(), e.user_id,
       DATEADD(minute, 9 * 60 + (e.slot * 53 + (CHECKSUM(HASHBYTES('MD5', CAST(e.user_id AS varchar(20))
                        + CONVERT(char(8), e.d, 112) + CAST(e.slot AS varchar(10)))) & 2147483647) % 37) % 480,
               CAST(e.d AS datetime)),
       -- no seat means no in-app Copilot: these people can only reach Copilot Chat
       CASE WHEN e.seq % 2 = 0 THEN N'bizchat' ELSE N'M365Chat' END
FROM expanded e;

INSERT INTO dbo.audit_events (id, time_stamp, event_data, operation_id, user_id)
SELECT id, ts, NULL, @op, user_id FROM #newunlic;

INSERT INTO dbo.copilot_chats (event_id, app_host, agent_id, copilot_credit_estimate_total,
                               copilot_credit_estimate_json, thread_id, client_region, copilot_log_version,
                               user_id, time_stamp)
SELECT id, host, NULL, NULL, NULL, LOWER(CONVERT(varchar(36), id)), N'westeurope', N'1.0-demo',
       user_id, ts
FROM #newunlic;

DECLARE @unlic      int = (SELECT COUNT(*) FROM #newunlic);
DECLARE @unlicUsers int = (SELECT COUNT(*) FROM #demand);
PRINT CONCAT('Unlicensed Copilot Chat users planted: ', @unlicUsers, ' (', @unlic, ' interactions)');

/* ------------------------------------------- 13. Microsoft 365 usage-report snapshot -----
   The licence-opportunity score reads ONE settled snapshot date, so every user needs a row
   on it or they cannot be considered for a licence at all. Unlicensed users are tiered so
   the recommendation list is a genuine shortlist rather than "everybody".                  */
DELETE FROM dbo.teams_user_activity_log      WHERE [date] = @snap;
DELETE FROM dbo.outlook_user_activity_log    WHERE [date] = @snap;
DELETE FROM dbo.sharepoint_user_activity_log WHERE [date] = @snap;
DELETE FROM dbo.onedrive_user_activity_log   WHERE [date] = @snap;

DECLARE @heavyUnlicensed int = (@users - @seats) * 40 / 100;

IF OBJECT_ID('tempdb..#m365') IS NOT NULL DROP TABLE #m365;
CREATE TABLE #m365 (
    user_id int PRIMARY KEY, tier varchar(10) NOT NULL, jit int NOT NULL,
    msgs int NOT NULL, meets int NOT NULL, sent int NOT NULL, [read] int NOT NULL, files int NOT NULL);

;WITH pool AS (
    SELECT u.id,
           CASE WHEN p.user_id IS NOT NULL          THEN 'licensed'
                WHEN d.user_id IS NOT NULL          THEN 'medium'   -- already proving demand
                WHEN ur.rn <= @heavyUnlicensed      THEN 'heavy'    -- strong business case
                WHEN ur.rn <= @heavyUnlicensed + 25 THEN 'medium'
                ELSE 'light' END AS tier,
           (CHECKSUM(HASHBYTES('MD5', CAST(u.id AS varchar(20)) + '|m365')) & 2147483647) % 100 AS jit
    FROM dbo.users u
    LEFT JOIN #plan   p ON p.user_id = u.id
    LEFT JOIN #demand d ON d.user_id = u.id
    LEFT JOIN (SELECT u2.id,
                      ROW_NUMBER() OVER (ORDER BY (CHECKSUM(HASHBYTES('MD5', CAST(u2.id AS varchar(20)) + '|heavy')) & 2147483647)) AS rn
               FROM dbo.users u2
               WHERE ISNULL(u2.account_enabled, 1) = 1
                 AND NOT EXISTS (SELECT 1 FROM #plan   p2 WHERE p2.user_id = u2.id)
                 AND NOT EXISTS (SELECT 1 FROM #demand d2 WHERE d2.user_id = u2.id)) ur ON ur.id = u.id
)
INSERT INTO #m365 (user_id, tier, jit, msgs, meets, sent, [read], files)
SELECT id, tier, jit,
       CASE tier WHEN 'heavy' THEN 74 + jit % 60  WHEN 'licensed' THEN 45 + jit % 55
                 WHEN 'medium' THEN 22 + jit % 12 ELSE 4 + jit % 8 END,
       CASE tier WHEN 'heavy' THEN 16 + jit % 14  WHEN 'licensed' THEN 9 + jit % 12
                 WHEN 'medium' THEN 6 + jit % 5   ELSE 1 + jit % 3 END,
       CASE tier WHEN 'heavy' THEN 42 + jit % 34  WHEN 'licensed' THEN 24 + jit % 30
                 WHEN 'medium' THEN 14 + jit % 8  ELSE 3 + jit % 6 END,
       CASE tier WHEN 'heavy' THEN 108 + jit % 96 WHEN 'licensed' THEN 62 + jit % 80
                 WHEN 'medium' THEN 30 + jit % 14 ELSE 8 + jit % 12 END,
       CASE tier WHEN 'heavy' THEN 46 + jit % 44  WHEN 'licensed' THEN 22 + jit % 40
                 WHEN 'medium' THEN 13 + jit % 7  ELSE 2 + jit % 5 END
FROM pool;

INSERT INTO dbo.teams_user_activity_log (
    user_id, [date], last_activity_date,
    private_chat_count, team_chat_count, post_messages, reply_messages, urgent_messages,
    calls_count, meetings_count, adhoc_meetings_attended_count, adhoc_meetings_organized_count,
    meetings_attended_count, meetings_organized_count,
    scheduled_onetime_meetings_attended_count, scheduled_onetime_meetings_organized_count,
    scheduled_recurring_meetings_attended_count, scheduled_recurring_meetings_organized_count,
    audio_duration_seconds, video_duration_seconds, screenshare_duration_seconds)
SELECT user_id, @snap, @snap,
       msgs / 2, msgs - msgs / 2 - msgs / 4 - msgs / 8, msgs / 4, msgs / 8, jit % 4,
       meets, meets, meets / 5, meets / 9,
       meets - meets / 4, meets / 4,
       meets / 3, meets / 6, meets / 4, meets / 8,
       meets * 900, meets * 420, meets * 160
FROM #m365;

INSERT INTO dbo.outlook_user_activity_log (
    user_id, [date], last_activity_date,
    email_send_count, email_receive_count, email_read_count, meeting_created_count, meeting_interacted_count)
SELECT user_id, @snap, @snap, sent, [read] + sent, [read], meets / 3, meets
FROM #m365;

INSERT INTO dbo.sharepoint_user_activity_log (
    user_id, [date], last_activity_date, viewed_or_edited, synced, shared_internally, shared_externally)
SELECT user_id, @snap, @snap, files - files / 3, files / 8, files / 5, files / 20
FROM #m365;

INSERT INTO dbo.onedrive_user_activity_log (
    user_id, [date], last_activity_date, viewed_or_edited, synced, shared_internally, shared_externally)
SELECT user_id, @snap, @snap, files / 3, files / 10, files / 6, files / 25
FROM #m365;

DECLARE @m365rows int = (SELECT COUNT(*) FROM #m365);
PRINT CONCAT('Microsoft 365 usage snapshot written for ', @m365rows, ' users on ', CONVERT(char(10), @snap, 120));

/* ------------------------------------------------------------------- 14. What you get ----
   A local re-implementation of CopilotAdoptionScoring, purely so the script can prove what
   it produced without starting the web app. The web page remains the source of truth.     */
;WITH seat AS (SELECT DISTINCT user_id FROM dbo.user_license_type_lookups WHERE license_type_id = @sku),
w AS (
    SELECT au.user_id, au.time_stamp, c.app_host
    FROM dbo.copilot_chats c
    JOIN dbo.audit_events au ON au.id = c.event_id
    JOIN seat s ON s.user_id = au.user_id
    WHERE au.time_stamp >= DATEADD(day, -364, CAST(@today AS datetime))
),
agg AS (
    SELECT s.user_id,
           SUM(CASE WHEN w.time_stamp >= @from THEN 1 ELSE 0 END) AS inter,
           SUM(CASE WHEN w.time_stamp <  @from THEN 1 ELSE 0 END) AS prior,
           (SELECT COUNT(DISTINCT CAST(w2.time_stamp AS date)) FROM w w2
             WHERE w2.user_id = s.user_id AND w2.time_stamp >= @from) AS active_days,
           (SELECT COUNT(DISTINCT w2.app_host) FROM w w2
             WHERE w2.user_id = s.user_id AND w2.time_stamp >= @from AND w2.app_host IS NOT NULL) AS apps
    FROM seat s LEFT JOIN w ON w.user_id = s.user_id
    GROUP BY s.user_id
),
sc AS (
    SELECT user_id, inter, prior, active_days,
           ROUND((CASE WHEN active_days >= 12 THEN 1.0 ELSE active_days / 12.0 END) * 50.0
               + (CASE WHEN active_days = 0 THEN 0
                       WHEN (inter * 1.0 / NULLIF(active_days, 0)) / 5.0 > 1 THEN 1.0
                       ELSE (inter * 1.0 / NULLIF(active_days, 0)) / 5.0 END) * 30.0
               + (CASE WHEN apps >= 3 THEN 1.0 ELSE apps / 3.0 END) * 20.0, 1) AS score
    FROM agg
),
b AS (
    SELECT *, CASE WHEN inter = 0 AND active_days = 0
                        THEN CASE WHEN prior > 0 THEN 'Dormant' ELSE 'NeverUsed' END
                   WHEN score >= 75 THEN 'Champion'
                   WHEN score >= 50 THEN 'Established'
                   WHEN score >= 25 THEN 'Developing'
                   ELSE 'Trialling' END AS band
    FROM sc
)
SELECT COUNT(*)                                                         AS [Licensed],
       SUM(CASE WHEN band <> 'NeverUsed' THEN 1 ELSE 0 END)             AS [Ever used],
       SUM(CASE WHEN band NOT IN ('NeverUsed','Dormant') THEN 1 ELSE 0 END) AS [Active],
       SUM(CASE WHEN band IN ('Established','Champion') THEN 1 ELSE 0 END)  AS [Habitual],
       SUM(CASE WHEN band = 'Champion' THEN 1 ELSE 0 END)               AS [Champions],
       SUM(CASE WHEN band IN ('NeverUsed','Dormant') THEN 1 ELSE 0 END) AS [Reclaimable],
       CAST(AVG(score) AS decimal(5,1))                                 AS [Mean score],
       SUM(inter)                                                       AS [Interactions]
FROM b;

SET NOEXEC OFF;
