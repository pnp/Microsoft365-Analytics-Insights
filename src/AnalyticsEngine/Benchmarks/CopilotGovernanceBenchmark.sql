/*
Copilot governance signals benchmark fixture (#648).

Purpose
  Seed a synthetic, tenant-shaped copy of the Copilot interaction tables the DLP page's governance
  section reads, so its queries can be measured at the scale of a ~200,000-user tenant before they
  ship. The measured statements are NOT in this file: Invoke-CopilotGovernanceBenchmark.ps1 reads them
  out of Web/Controllers/DlpAPIController.cs, so the benchmark always measures the shipped query.

  There is no schema change to measure. The question is whether the shipped QUERY SHAPE is cheap
  enough on the indexes that exist today, at a narrow (28-day) and a wide (90-day) window.

Where the tables live
  In their own schema, [govbench], so the fixture can share a database with other work without
  touching dbo. The harness rewrites "dbo." to "govbench." in the statements it measures. Every
  table is dropped again at the end of a run unless the harness is told to keep it.

Shape (per SQLCMD variables; the defaults in the harness are in brackets)
  SyntheticUsers         [200000]  people in the tenant. 30% of them generate 70% of the interactions.
  SyntheticInteractions  [4000000] copilot_chats rows, spread over SyntheticDays.
  SyntheticDays          [180]     days of history, so a 28-day window reads a small slice of every
                                   table and a 90-day window half of it.
  FlagDays               [60]      how many recent days carry the jailbreak / XPIA flags. Rows imported
                                   before #570 shipped keep NULL, exactly as a real upgraded tenant has.

  Per interaction, roughly:
    copilot_event_messages             2.1  (one prompt, one or two responses)
    copilot_event_accessed_resources   1.75 (none on half the interactions, 1-6 on the rest)
    copilot_event_ai_models            0.12 (Microsoft names no model on most Microsoft 365 Copilot interactions)
    copilot_event_ai_system_plugins    0.16 (web search on about one in seven)

  Flags, inside FlagDays only:
    jailbreak_detected  reported on 95% of prompt rows, never on a response row; TRUE on about 5 in 10,000
    xpia_detected       reported on 92% of resource rows; TRUE on about 2 in 10,000
  sensitivity_label_id on about 22% of resource rows, at every age.

Fidelity
  Column types and the indexes that can serve these queries mirror the migrated production schema
  (scripted from a migrated database, not retyped from memory):
    copilot_chats                     PK(event_id) clustered, IX_event_id, IX_agent_id,
                                      IX_copilot_chats_time_stamp_user_id (time_stamp, user_id) INCLUDE (app_host, agent_id)
    copilot_chat_duplicates           PK(event_id), IX_time_stamp, IX_counted_event_id INCLUDE (reason);
                                      empty by default, so every generated record is a canonical turn
    copilot_event_messages            PK(id) clustered, IX_copilot_chat_id
    copilot_event_accessed_resources  PK(id) clustered, IX_copilot_chat_id, IX_sensitivity_label_id,
                                      IX_copilot_event_accessed_resources_dedup (8 key columns)
    copilot_event_ai_models           PK(id) clustered, IX_copilot_chat_id, IX_model_id
    copilot_event_ai_system_plugins   PK(id) clustered, IX_copilot_chat_id, IX_ai_system_plugin_id
  Left out, because no index on them can serve a query keyed on copilot_chat_id: the single-column
  foreign-key indexes on resource_id_id, resource_name_id, resource_site_url_id, resource_type_id,
  action_id and list_item_unique_id_id. Detail rows get identity values in time order, as an
  importer that runs every cycle assigns them.

  Known differences from production, none of which a measured statement depends on:
    - copilot_chats is clustered after loading, so its pages are full; a production table clustered
      on a random GUID is fragmented. The measured statements read copilot_chats only through
      IX_copilot_chats_time_stamp_user_id.
    - No foreign keys, and no audit_events / users / copilot_agents rows behind the ids.

Safety
  Synthetic values only. Never point this at a customer database, and never paste real names, row
  counts or plans into this file, a PR or a release note.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @SyntheticUsers int = $(SyntheticUsers);
DECLARE @SyntheticInteractions int = $(SyntheticInteractions);
DECLARE @SyntheticDays int = $(SyntheticDays);
DECLARE @FlagDays int = $(FlagDays);
DECLARE @now datetime = DATEADD(DAY, 1, CAST(CAST(SYSUTCDATETIME() AS date) AS datetime));
DECLARE @flagStart datetime = DATEADD(DAY, -@FlagDays, @now);
DECLARE @t datetime2 = SYSUTCDATETIME();
DECLARE @msg nvarchar(400);

SET @msg = CONCAT(N'Copilot governance fixture: users=', @SyntheticUsers, N', interactions=', @SyntheticInteractions,
                  N', days=', @SyntheticDays, N', flagDays=', @FlagDays, N', database=', DB_NAME());
RAISERROR(@msg, 0, 1) WITH NOWAIT;

IF SCHEMA_ID(N'govbench') IS NULL EXEC (N'CREATE SCHEMA govbench AUTHORIZATION dbo;');

IF OBJECT_ID(N'govbench.copilot_event_ai_system_plugins', N'U') IS NOT NULL DROP TABLE govbench.copilot_event_ai_system_plugins;
IF OBJECT_ID(N'govbench.copilot_ai_system_plugins', N'U') IS NOT NULL DROP TABLE govbench.copilot_ai_system_plugins;
IF OBJECT_ID(N'govbench.copilot_event_ai_models', N'U') IS NOT NULL DROP TABLE govbench.copilot_event_ai_models;
IF OBJECT_ID(N'govbench.copilot_ai_models', N'U') IS NOT NULL DROP TABLE govbench.copilot_ai_models;
IF OBJECT_ID(N'govbench.copilot_event_accessed_resources', N'U') IS NOT NULL DROP TABLE govbench.copilot_event_accessed_resources;
IF OBJECT_ID(N'govbench.copilot_event_messages', N'U') IS NOT NULL DROP TABLE govbench.copilot_event_messages;
IF OBJECT_ID(N'govbench.copilot_chat_duplicates', N'U') IS NOT NULL DROP TABLE govbench.copilot_chat_duplicates;
IF OBJECT_ID(N'govbench.copilot_chats', N'U') IS NOT NULL DROP TABLE govbench.copilot_chats;
IF OBJECT_ID(N'govbench.sensitivity_labels', N'U') IS NOT NULL DROP TABLE govbench.sensitivity_labels;

CREATE TABLE govbench.copilot_chats
(
    event_id uniqueidentifier NOT NULL,
    app_host nvarchar(max) NULL,
    agent_id int NULL,
    copilot_credit_estimate_total int NULL,
    copilot_credit_estimate_json nvarchar(max) NULL,
    thread_id nvarchar(450) NULL,
    client_region nvarchar(50) NULL,
    copilot_log_version nvarchar(50) NULL,
    user_id int NULL,
    time_stamp datetime NULL
);

CREATE TABLE govbench.copilot_chat_duplicates
(
    event_id uniqueidentifier NOT NULL,
    time_stamp datetime NOT NULL,
    counted_event_id uniqueidentifier NOT NULL,
    reason tinyint NOT NULL,
    CONSTRAINT [PK_govbench.copilot_chat_duplicates] PRIMARY KEY CLUSTERED (event_id)
);
CREATE NONCLUSTERED INDEX IX_copilot_chat_duplicates_time_stamp
    ON govbench.copilot_chat_duplicates (time_stamp);
CREATE NONCLUSTERED INDEX IX_copilot_chat_duplicates_counted_event_id
    ON govbench.copilot_chat_duplicates (counted_event_id) INCLUDE (reason);

CREATE TABLE govbench.copilot_event_messages
(
    id int IDENTITY(1,1) NOT NULL,
    copilot_chat_id uniqueidentifier NOT NULL,
    message_id nvarchar(500) NULL,
    [size] bigint NULL,
    is_prompt bit NULL,
    jailbreak_detected bit NULL,
    CONSTRAINT [PK_govbench.copilot_event_messages] PRIMARY KEY CLUSTERED (id)
);

CREATE TABLE govbench.copilot_event_accessed_resources
(
    id int IDENTITY(1,1) NOT NULL,
    copilot_chat_id uniqueidentifier NOT NULL,
    resource_id_id int NULL,
    resource_name_id int NULL,
    resource_site_url_id int NULL,
    resource_type_id int NULL,
    sensitivity_label_id int NULL,
    action_id int NULL,
    list_item_unique_id_id int NULL,
    xpia_detected bit NULL,
    CONSTRAINT [PK_govbench.copilot_event_accessed_resources] PRIMARY KEY CLUSTERED (id)
);

CREATE TABLE govbench.sensitivity_labels
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_govbench.sensitivity_labels] PRIMARY KEY CLUSTERED,
    label_id nvarchar(100) NULL
);

CREATE TABLE govbench.copilot_ai_models
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_govbench.copilot_ai_models] PRIMARY KEY CLUSTERED,
    [name] nvarchar(100) NULL,
    provider_name nvarchar(100) NULL,
    [version] nvarchar(100) NULL
);

CREATE TABLE govbench.copilot_event_ai_models
(
    id int IDENTITY(1,1) NOT NULL,
    copilot_chat_id uniqueidentifier NOT NULL,
    model_id int NOT NULL,
    CONSTRAINT [PK_govbench.copilot_event_ai_models] PRIMARY KEY CLUSTERED (id)
);

CREATE TABLE govbench.copilot_ai_system_plugins
(
    id int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_govbench.copilot_ai_system_plugins] PRIMARY KEY CLUSTERED,
    plugin_id nvarchar(255) NULL,
    [name] nvarchar(255) NULL,
    [version] nvarchar(50) NULL
);

CREATE TABLE govbench.copilot_event_ai_system_plugins
(
    id int IDENTITY(1,1) NOT NULL,
    copilot_chat_id uniqueidentifier NOT NULL,
    ai_system_plugin_id int NOT NULL,
    CONSTRAINT [PK_govbench.copilot_event_ai_system_plugins] PRIMARY KEY CLUSTERED (id)
);

-- Dimensions. Synthetic names only.
INSERT INTO govbench.sensitivity_labels (label_id)
SELECT CONCAT(N'00000000-0000-0000-0000-0000000000', RIGHT(CONCAT(N'0', n), 2))
FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) AS v(n);

INSERT INTO govbench.copilot_ai_models ([name], provider_name, [version])
VALUES (N'DEEP_LEO', N'Microsoft', NULL), (N'DEEP_LEO', N'Microsoft', N'2026-06-01'),
       (N'contoso-model-large', N'Contoso AI', N'2026-01-01'), (N'contoso-model-small', N'Contoso AI', N'2026-01-01'),
       (N'contoso-model-reasoning', N'Contoso AI', N'2026-03-01');

INSERT INTO govbench.copilot_ai_system_plugins (plugin_id, [name], [version])
VALUES (N'BingWebSearch', N'BuiltIn', NULL), (N'Contoso.Tickets', N'Contoso Tickets', N'1.0'),
       (N'Contoso.Expenses', N'Contoso Expenses', N'2.1');

-- ---------------------------------------------------------------------------------------------
-- copilot_chats. Loaded as a heap and clustered afterwards (see "Known differences").
-- ---------------------------------------------------------------------------------------------
;WITH e1(n) AS (SELECT 1 FROM (VALUES (1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) AS v(n)),
      e3(n) AS (SELECT 1 FROM e1 a CROSS JOIN e1 b CROSS JOIN e1 c),
      nums(n) AS (SELECT TOP (@SyntheticInteractions) ROW_NUMBER() OVER (ORDER BY (SELECT NULL))
                  FROM e3 a CROSS JOIN e3 b CROSS JOIN e3 c),
      r(n, r1, r2, r3, r4) AS (
          SELECT n, (CHECKSUM(NEWID()) & 2147483647), (CHECKSUM(NEWID()) & 2147483647), (CHECKSUM(NEWID()) & 2147483647), (CHECKSUM(NEWID()) & 2147483647)
          FROM nums)
INSERT INTO govbench.copilot_chats WITH (TABLOCK)
    (event_id, app_host, agent_id, copilot_credit_estimate_total, copilot_credit_estimate_json,
     thread_id, client_region, copilot_log_version, user_id, time_stamp)
SELECT NEWID(),
       CHOOSE(r1 % 8 + 1, N'BizChat', N'Word', N'Outlook', N'Teams', N'Excel', N'PowerPoint', N'Edge', N'Office'),
       CASE WHEN r2 % 10 < 3 THEN 1 + r3 % 500 END,
       2 + r4 % 20,
       CASE WHEN r4 % 10 < 7 THEN N'{"GenerativeAnswers":1,"TenantGraphGrounding":1,"AgentActions":0,"DeepReasoning":0,"UnclassifiedResources":0,"Basis":"TenantResource"}' END,
       CONVERT(nvarchar(36), NEWID()),
       N'GBR',
       N'1.0.0.0',
       CASE WHEN r2 % 10 < 7 THEN 1 + r3 % (@SyntheticUsers * 3 / 10) ELSE 1 + r3 % @SyntheticUsers END,
       DATEADD(SECOND, -(r1 % (@SyntheticDays * 86400)), @now)
FROM r;

SET @msg = CONCAT(N'  copilot_chats loaded in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

ALTER TABLE govbench.copilot_chats ADD CONSTRAINT [PK_govbench.copilot_chats] PRIMARY KEY CLUSTERED (event_id);
CREATE NONCLUSTERED INDEX IX_event_id ON govbench.copilot_chats (event_id);
CREATE NONCLUSTERED INDEX IX_agent_id ON govbench.copilot_chats (agent_id);
CREATE NONCLUSTERED INDEX IX_copilot_chats_time_stamp_user_id ON govbench.copilot_chats (time_stamp, user_id) INCLUDE (app_host, agent_id);

SET @msg = CONCAT(N'  copilot_chats indexed in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------
-- copilot_event_messages: a prompt and one response, plus a second response on one interaction in ten.
-- Inserted in interaction time order, so identity values follow import order as they do in production.
-- ---------------------------------------------------------------------------------------------
INSERT INTO govbench.copilot_event_messages WITH (TABLOCK) (copilot_chat_id, message_id, [size], is_prompt, jailbreak_detected)
SELECT c.event_id,
       CONCAT(N'17', RIGHT(CONCAT(N'00000000000', (CHECKSUM(c.event_id) & 2147483647) % 100000000000 + v.n), 11)),
       NULL,
       CASE WHEN v.n = 1 THEN 1 ELSE 0 END,
       CASE WHEN v.n = 1 AND c.time_stamp >= @flagStart AND (CHECKSUM(NEWID()) & 2147483647) % 100 < 95
            THEN CASE WHEN (CHECKSUM(NEWID()) & 2147483647) % 10000 < 5 THEN 1 ELSE 0 END
       END
FROM govbench.copilot_chats AS c
CROSS APPLY (VALUES (1), (2), (3)) AS v(n)
WHERE v.n <= 2 OR (CHECKSUM(c.event_id) & 2147483647) % 10 = 0
ORDER BY c.time_stamp, c.event_id, v.n;

SET @msg = CONCAT(N'  copilot_event_messages loaded in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

CREATE NONCLUSTERED INDEX IX_copilot_chat_id ON govbench.copilot_event_messages (copilot_chat_id);

SET @msg = CONCAT(N'  copilot_event_messages indexed in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------
-- copilot_event_accessed_resources: none on half the interactions, 1-6 resources on the rest.
-- ---------------------------------------------------------------------------------------------
INSERT INTO govbench.copilot_event_accessed_resources WITH (TABLOCK)
    (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id,
     sensitivity_label_id, action_id, list_item_unique_id_id, xpia_detected)
SELECT c.event_id,
       k.resource_id,
       1 + k.resource_id % 1500000,
       1 + k.resource_id % 50000,
       1 + k.resource_id % 20,
       CASE WHEN (CHECKSUM(NEWID()) & 2147483647) % 100 < 22 THEN 1 + k.resource_id % 12 END,
       1 + k.resource_id % 3,
       CASE WHEN k.resource_id % 10 < 6 THEN k.resource_id END,
       CASE WHEN c.time_stamp >= @flagStart AND (CHECKSUM(NEWID()) & 2147483647) % 100 < 92
            THEN CASE WHEN (CHECKSUM(NEWID()) & 2147483647) % 10000 < 2 THEN 1 ELSE 0 END
       END
FROM govbench.copilot_chats AS c
CROSS APPLY (VALUES (1), (2), (3), (4), (5), (6)) AS v(n)
CROSS APPLY (SELECT CAST(1 + (CAST(CHECKSUM(c.event_id) & 2147483647 AS bigint) + v.n * 7919) % 2000000 AS int) AS resource_id) AS k
WHERE (CHECKSUM(c.event_id) & 2147483647) % 2 = 0
  AND v.n <= 1 + ((CHECKSUM(c.event_id) & 2147483647) / 2) % 6
ORDER BY c.time_stamp, c.event_id, v.n;

SET @msg = CONCAT(N'  copilot_event_accessed_resources loaded in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

CREATE NONCLUSTERED INDEX IX_copilot_chat_id ON govbench.copilot_event_accessed_resources (copilot_chat_id);
CREATE NONCLUSTERED INDEX IX_sensitivity_label_id ON govbench.copilot_event_accessed_resources (sensitivity_label_id);
CREATE NONCLUSTERED INDEX IX_copilot_event_accessed_resources_dedup ON govbench.copilot_event_accessed_resources
    (copilot_chat_id, resource_id_id, resource_name_id, resource_site_url_id, resource_type_id, sensitivity_label_id, action_id, list_item_unique_id_id);

SET @msg = CONCAT(N'  copilot_event_accessed_resources indexed in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;
SET @t = SYSUTCDATETIME();

-- ---------------------------------------------------------------------------------------------
-- AI models and system plugins: sparse, as Microsoft names a model on few Microsoft 365 Copilot interactions.
-- ---------------------------------------------------------------------------------------------
INSERT INTO govbench.copilot_event_ai_models WITH (TABLOCK) (copilot_chat_id, model_id)
SELECT c.event_id, m.model_id
FROM govbench.copilot_chats AS c
CROSS APPLY (SELECT (CHECKSUM(c.event_id) & 2147483647) % 1000 AS bucket) AS b
CROSS APPLY (
    SELECT CASE WHEN b.bucket < 20 THEN 1 WHEN b.bucket < 30 THEN 2 END AS model_id
    UNION ALL
    SELECT CASE WHEN b.bucket BETWEEN 25 AND 84 THEN 3 WHEN b.bucket BETWEEN 85 AND 109 THEN 4 WHEN b.bucket BETWEEN 110 AND 119 THEN 5 END
) AS m
WHERE m.model_id IS NOT NULL
ORDER BY c.time_stamp, c.event_id;

INSERT INTO govbench.copilot_event_ai_system_plugins WITH (TABLOCK) (copilot_chat_id, ai_system_plugin_id)
SELECT c.event_id, p.plugin_id
FROM govbench.copilot_chats AS c
CROSS APPLY (SELECT (CHECKSUM(c.event_id) & 2147483647) % 997 AS bucket) AS b
CROSS APPLY (
    SELECT CASE WHEN b.bucket < 140 THEN 1 END AS plugin_id
    UNION ALL
    SELECT CASE WHEN b.bucket BETWEEN 130 AND 144 THEN 2 WHEN b.bucket BETWEEN 145 AND 159 THEN 3 END
) AS p
WHERE p.plugin_id IS NOT NULL
ORDER BY c.time_stamp, c.event_id;

CREATE NONCLUSTERED INDEX IX_copilot_chat_id ON govbench.copilot_event_ai_models (copilot_chat_id);
CREATE NONCLUSTERED INDEX IX_model_id ON govbench.copilot_event_ai_models (model_id);
CREATE NONCLUSTERED INDEX IX_copilot_chat_id ON govbench.copilot_event_ai_system_plugins (copilot_chat_id);
CREATE NONCLUSTERED INDEX IX_ai_system_plugin_id ON govbench.copilot_event_ai_system_plugins (ai_system_plugin_id);

SET @msg = CONCAT(N'  models and plugins loaded and indexed in ', DATEDIFF(SECOND, @t, SYSUTCDATETIME()), N's'); RAISERROR(@msg, 0, 1) WITH NOWAIT;

-- Fresh statistics, as an auto-update would have produced after the import that wrote these rows.
UPDATE STATISTICS govbench.copilot_chats WITH FULLSCAN;
UPDATE STATISTICS govbench.copilot_event_messages WITH FULLSCAN;
UPDATE STATISTICS govbench.copilot_event_accessed_resources WITH FULLSCAN;
UPDATE STATISTICS govbench.copilot_event_ai_models WITH FULLSCAN;
UPDATE STATISTICS govbench.copilot_event_ai_system_plugins WITH FULLSCAN;

-- The shape actually produced, for the report. Sizes, not data.
SELECT t.name AS [table],
       SUM(CASE WHEN i.index_id IN (0, 1) AND a.type = 1 THEN p.rows ELSE 0 END) AS [rows],
       CAST(SUM(CASE WHEN i.index_id IN (0, 1) THEN a.used_pages ELSE 0 END) * 8.0 / 1024.0 AS decimal(10,1)) AS table_mb,
       CAST(SUM(CASE WHEN i.index_id > 1 THEN a.used_pages ELSE 0 END) * 8.0 / 1024.0 AS decimal(10,1)) AS indexes_mb
FROM sys.tables AS t
JOIN sys.indexes AS i ON i.object_id = t.object_id
JOIN sys.partitions AS p ON p.object_id = i.object_id AND p.index_id = i.index_id
JOIN sys.allocation_units AS a ON a.container_id = p.partition_id
WHERE t.schema_id = SCHEMA_ID(N'govbench')
GROUP BY t.name
ORDER BY t.name;
