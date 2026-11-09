/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202610230900001_AddCopilotCostPreview
   Predecessor: 202610221500003_UniqueOnlineMeetingIds
   =====================================================================================================
   For operators / DBAs who upgrade the Analytics database BY HAND instead of running the installer. It
   applies the migration's SQL verbatim (AddCopilotCostPreview.Up_Sql, then Purge_Sql), then stamps
   __MigrationHistory so EF (DatabaseUpgrader / MigrateDatabaseToLatestVersion) and the web app Health
   page treat it as applied.

   RUN ORDER
     Run every manual script in migration-id order. The prerequisite is
     202610221500003_UniqueOnlineMeetingIds, which must already be stamped in __MigrationHistory. Without
     it this script creates nothing and stops with "NOT stamped".

   WHAT IT DOES
     Creates the empty stores of the default-off Copilot credit history preview (nine dbo.copilot_cost_*
     tables and their indexes) and dbo.copilot_cost_purge_preview, the bounded, dry-run-first purge.

   CLASSIFICATION: PURELY ADDITIVE
     New empty tables, their indexes and one procedure. Nothing existing is altered, rebuilt or dropped,
     so there is no "before" query to measure and the benchmark gate for performance-motivated schema
     changes does not apply.

   UPGRADE TIME
     Seconds, on a tenant of any size and any SQL Server edition: creating empty tables does not depend
     on how much data the database already holds. Stop the importer for a manual upgrade, and deploy the
     matching binaries before restarting it.

   RETENTION
     Preview facts are kept until an administrator purges them explicitly. Purge preview (a dry run;
     synthetic example):
       EXEC dbo.copilot_cost_purge_preview @BeforeDate='20000101', @BatchSize=1000;
     Deleting also requires @DryRun=0, @Confirm=N'PURGE COPILOT COST HISTORY'. It removes observations
     and history only; the private token cache, work and context are independent.

   SAFE TO RE-RUN
     Every step is guarded. Re-running the script against an upgraded database changes nothing and leaves
     the stamp in place.

   THIS IS A CORRECTED COPY (#848)
     The copy attached to earlier stable releases checked the predecessor with a THROW in a batch of its
     own. sqlcmd and SSMS carry on with the next batch after an error, so on a database without the
     predecessor that copy created the preview schema anyway, and its stamp then inserted nothing,
     silently. This copy stops with SET NOEXEC ON, and says "NOT stamped" whenever it does not stamp. If
     the earlier copy left the preview tables on a database without the predecessor, nothing is damaged:
     stamp the predecessor (its manual script, or the installer), then run this script. Every step is
     guarded, so it completes and stamps.
   ===================================================================================================== */

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

/* =====================================================================================================
   PRE-FLIGHT - runs BEFORE anything is created.

   sqlcmd and SSMS do NOT stop at an error: they abandon the failing batch and carry on with the next
   one. So a RAISERROR or THROW alone does not prevent an out-of-order run. SET NOEXEC ON is what makes
   the failure real: every following batch is compiled but not executed, and the SET NOEXEC OFF at the
   very end of the script restores the session.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.__MigrationHistory', N'U') IS NULL
BEGIN
    RAISERROR('AddCopilotCostPreview: NOT stamped - dbo.__MigrationHistory does not exist, so this does not look like an Analytics database. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610221500003_UniqueOnlineMeetingIds')
   AND NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610230900001_AddCopilotCostPreview')
BEGIN
    RAISERROR('AddCopilotCostPreview: NOT stamped - prerequisite migration 202610221500003_UniqueOnlineMeetingIds is not stamped in __MigrationHistory. Run the manual scripts in migration-id order. Nothing has been changed.', 16, 1) WITH NOWAIT;
    SET NOEXEC ON;
END
GO

SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.copilot_cost_control', N'U') IS NULL
 CREATE TABLE dbo.copilot_cost_control(singleton int NOT NULL PRIMARY KEY CHECK(singleton=1),
  generation varchar(32) NOT NULL, blocked bit NOT NULL DEFAULT 0);
IF OBJECT_ID(N'dbo.copilot_cost_runs', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_runs(
  run_id uniqueidentifier NOT NULL PRIMARY KEY,
  generation varchar(32) NOT NULL, inventory_date date NOT NULL,
  inventory_complete bit NOT NULL DEFAULT 0, next_link nvarchar(4000) NULL,
  inventory_pages int NOT NULL DEFAULT 0,
  owner uniqueidentifier NULL, fence bigint NOT NULL DEFAULT 0, owned_until datetimeoffset NULL,
  last_served datetimeoffset NOT NULL, last_attempt datetimeoffset NULL, last_completed datetimeoffset NULL,
  not_before datetimeoffset NULL,
  outcome varchar(40) NOT NULL DEFAULT 'inventory', context_phase int NOT NULL DEFAULT 0,
  context_next nvarchar(4000) NULL,
  CONSTRAINT UQ_copilot_cost_run_day UNIQUE(generation,inventory_date));
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_runs') AND name=N'IX_copilot_cost_run_fair')
 CREATE INDEX IX_copilot_cost_run_fair ON dbo.copilot_cost_runs(generation,last_served);
IF OBJECT_ID(N'dbo.copilot_cost_work', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_work(
  run_id uniqueidentifier NOT NULL, object_id uniqueidentifier NOT NULL,
  user_id int NULL, attempts int NOT NULL DEFAULT 0,
  next_attempt datetimeoffset NOT NULL, completed bit NOT NULL DEFAULT 0,
  observed bit NOT NULL DEFAULT 0, measured bit NOT NULL DEFAULT 0, empty bit NOT NULL DEFAULT 0,
  partial bit NOT NULL DEFAULT 0, unevaluated int NOT NULL DEFAULT 0,
  outcome varchar(40) NOT NULL DEFAULT 'pending',
  CONSTRAINT PK_copilot_cost_work PRIMARY KEY(run_id,object_id),
  CONSTRAINT FK_copilot_cost_work_run FOREIGN KEY(run_id) REFERENCES dbo.copilot_cost_runs(run_id));
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_work') AND name=N'IX_copilot_cost_work_ready')
 CREATE INDEX IX_copilot_cost_work_ready ON dbo.copilot_cost_work(run_id,completed,next_attempt,attempts,object_id) INCLUDE(user_id,outcome);
IF OBJECT_ID(N'dbo.copilot_cost_observations', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_observations(
  observation_date date NOT NULL, object_id uniqueidentifier NOT NULL, user_id int NULL,
  observed_at datetimeoffset NOT NULL, quality tinyint NOT NULL,
  source_month date NULL, period_status varchar(40) NOT NULL DEFAULT 'unverified',
  selection_status nvarchar(max) NOT NULL,
  CONSTRAINT PK_copilot_cost_observations PRIMARY KEY(observation_date,object_id));
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_observations') AND name=N'IX_copilot_cost_observation_user')
 CREATE INDEX IX_copilot_cost_observation_user ON dbo.copilot_cost_observations(object_id,observation_date);
IF OBJECT_ID(N'dbo.copilot_cost_services', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_services(
  observation_date date NOT NULL, object_id uniqueidentifier NOT NULL, category_hash binary(32) NOT NULL,
  category nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  policy_id nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL,
  consumed_quantity float(53) NULL, unit nvarchar(max) NULL,
  allowed bit NULL, decision_reason nvarchar(max) NULL, source_modified datetimeoffset NULL,
  period_limit nvarchar(max) NULL, per_user_limit nvarchar(max) NULL,
  capacity nvarchar(max) NULL, remaining nvarchar(max) NULL, data_status nvarchar(max) NULL,
  CONSTRAINT PK_copilot_cost_services PRIMARY KEY(observation_date,object_id,category_hash),
  CONSTRAINT FK_copilot_cost_services_observation FOREIGN KEY(observation_date,object_id)
   REFERENCES dbo.copilot_cost_observations(observation_date,object_id));
END;
IF OBJECT_ID(N'dbo.copilot_cost_unevaluated', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_unevaluated(
  observation_date date NOT NULL, object_id uniqueidentifier NOT NULL, ordinal int NOT NULL,
  category nvarchar(max) COLLATE Latin1_General_100_BIN2 NOT NULL, reason nvarchar(max) NOT NULL,
  CONSTRAINT PK_copilot_cost_unevaluated PRIMARY KEY(observation_date,object_id,ordinal),
  CONSTRAINT FK_copilot_cost_unevaluated_observation FOREIGN KEY(observation_date,object_id)
   REFERENCES dbo.copilot_cost_observations(observation_date,object_id));
END;
IF OBJECT_ID(N'dbo.copilot_cost_history', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_history(
  history_id bigint IDENTITY NOT NULL PRIMARY KEY, run_id uniqueidentifier NOT NULL,
  publication_id uniqueidentifier NOT NULL,
  observation_date date NOT NULL, object_id uniqueidentifier NOT NULL,
  observed_at datetimeoffset NOT NULL, quality tinyint NOT NULL, bundle nvarchar(max) NOT NULL);
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'IX_copilot_cost_history_scope')
 CREATE INDEX IX_copilot_cost_history_scope ON dbo.copilot_cost_history(observation_date,object_id,history_id);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'IX_copilot_cost_history_user')
 CREATE INDEX IX_copilot_cost_history_user ON dbo.copilot_cost_history(object_id,observation_date,history_id);
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'UX_copilot_cost_history_publication')
 CREATE UNIQUE INDEX UX_copilot_cost_history_publication ON dbo.copilot_cost_history(publication_id);
IF OBJECT_ID(N'dbo.copilot_cost_context', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_context(
  context_id bigint IDENTITY NOT NULL PRIMARY KEY, run_id uniqueidentifier NOT NULL,
  observed_at datetimeoffset NOT NULL, kind varchar(20) NOT NULL, source_url nvarchar(4000) NOT NULL, document nvarchar(max) NOT NULL);
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_context') AND name=N'IX_copilot_cost_context_run')
 CREATE INDEX IX_copilot_cost_context_run ON dbo.copilot_cost_context(run_id,context_id);
IF OBJECT_ID(N'dbo.copilot_cost_pages', N'U') IS NULL
BEGIN
 CREATE TABLE dbo.copilot_cost_pages(
  run_id uniqueidentifier NOT NULL, url_hash binary(32) NOT NULL, kind varchar(20) NOT NULL,
  url nvarchar(4000) NOT NULL, completed bit NOT NULL DEFAULT 0,
  CONSTRAINT PK_copilot_cost_pages PRIMARY KEY(run_id,url_hash));
END;
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_pages') AND name=N'IX_copilot_cost_pages_queue')
 CREATE INDEX IX_copilot_cost_pages_queue ON dbo.copilot_cost_pages(run_id,completed,kind);

GO

CREATE OR ALTER PROCEDURE dbo.copilot_cost_purge_preview
 @BeforeDate date, @ObjectId uniqueidentifier = NULL, @BatchSize int = 1000,
 @DryRun bit = 1, @Confirm nvarchar(40) = NULL
AS
BEGIN
 SET NOCOUNT ON;
 SET XACT_ABORT ON;
 -- NULL is not 0: an unset @DryRun is a dry run, so deleting always needs @DryRun=0 and the confirmation.
 SET @DryRun = ISNULL(@DryRun, 1);
 IF @BeforeDate IS NULL OR @BeforeDate >= CONVERT(date,SYSUTCDATETIME())
  OR @BatchSize NOT BETWEEN 1 AND 10000
  THROW 51000, 'Specify a historical cutoff and a bounded batch.', 1;
 IF @DryRun = 0 AND ISNULL(@Confirm,N'') <> N'PURGE COPILOT COST HISTORY'
  THROW 51000, 'Explicit purge confirmation required.', 1;
 -- Each invocation removes at most BatchSize bundles; rerun explicitly to continue.
 -- User-scoped purge leaves tenant/policy context and directory work untouched.
 SELECT TOP (@BatchSize) history_id INTO #history FROM dbo.copilot_cost_history
 WHERE observation_date < @BeforeDate AND (@ObjectId IS NULL OR object_id=@ObjectId)
 ORDER BY observation_date,object_id,history_id OPTION(RECOMPILE);
 SELECT TOP (@BatchSize) observation_date,object_id INTO #facts FROM dbo.copilot_cost_observations
 WHERE observation_date < @BeforeDate AND (@ObjectId IS NULL OR object_id=@ObjectId)
 ORDER BY observation_date,object_id OPTION(RECOMPILE);
 SELECT (SELECT COUNT(*) FROM #history) AS HistoryBundles,(SELECT COUNT(*) FROM #facts) AS ObservationBundles,@DryRun AS DryRun;
 IF @DryRun = 1 RETURN;
 BEGIN TRANSACTION;
 DELETE s FROM dbo.copilot_cost_services s JOIN #facts f ON f.observation_date=s.observation_date AND f.object_id=s.object_id;
 DELETE s FROM dbo.copilot_cost_unevaluated s JOIN #facts f ON f.observation_date=s.observation_date AND f.object_id=s.object_id;
 DELETE s FROM dbo.copilot_cost_observations s JOIN #facts f ON f.observation_date=s.observation_date AND f.object_id=s.object_id;
 DELETE h FROM dbo.copilot_cost_history h JOIN #history x ON x.history_id=h.history_id;
 COMMIT;
END;

GO

/* =====================================================================================================
   Record the migration in __MigrationHistory.

   The guard checks SCHEMA only: every table, column, index and the purge procedure the migration
   creates must exist, and the predecessor must be stamped. It never checks data. It is reached on every
   path, including a re-run against a database that is already up to date. The model blob is copied from
   the predecessor's row, because this migration does not change the EF model: its .resx snapshot is
   byte-identical to the predecessor's.
   ===================================================================================================== */
IF OBJECT_ID(N'dbo.copilot_cost_work',N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_cost_services',N'U') IS NULL
 OR OBJECT_ID(N'dbo.copilot_cost_control',N'U') IS NULL
 OR OBJECT_ID(N'dbo.copilot_cost_runs',N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_cost_observations',N'U') IS NULL
 OR OBJECT_ID(N'dbo.copilot_cost_unevaluated',N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_cost_history',N'U') IS NULL
 OR OBJECT_ID(N'dbo.copilot_cost_context',N'U') IS NULL OR OBJECT_ID(N'dbo.copilot_cost_pages',N'U') IS NULL
 OR COL_LENGTH(N'dbo.copilot_cost_work',N'unevaluated') IS NULL
 OR COL_LENGTH(N'dbo.copilot_cost_history',N'publication_id') IS NULL
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'UX_copilot_cost_history_publication')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_work') AND name=N'IX_copilot_cost_work_ready')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_runs') AND name=N'IX_copilot_cost_run_fair')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_observations') AND name=N'IX_copilot_cost_observation_user')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'IX_copilot_cost_history_scope')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_history') AND name=N'IX_copilot_cost_history_user')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_context') AND name=N'IX_copilot_cost_context_run')
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.copilot_cost_pages') AND name=N'IX_copilot_cost_pages_queue')
 OR OBJECT_ID(N'dbo.copilot_cost_purge_preview',N'P') IS NULL
BEGIN
    RAISERROR('AddCopilotCostPreview: NOT stamped - Preview schema is incomplete: a dbo.copilot_cost_* table, column or index, or dbo.copilot_cost_purge_preview, is missing. Read the errors above (sqlcmd and SSMS carry on past a failed batch, so the first error may be further up), then re-run this script; every step is guarded.', 16, 1) WITH NOWAIT;
END
ELSE IF EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610230900001_AddCopilotCostPreview')
BEGIN
    RAISERROR('AddCopilotCostPreview: __MigrationHistory is already stamped; nothing to do.', 0, 1) WITH NOWAIT;
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202610221500003_UniqueOnlineMeetingIds')
BEGIN
    RAISERROR('AddCopilotCostPreview: NOT stamped - prerequisite migration 202610221500003_UniqueOnlineMeetingIds is not stamped in __MigrationHistory. Run the manual scripts in migration-id order, then re-run this script.', 16, 1) WITH NOWAIT;
END
ELSE
BEGIN
    INSERT dbo.__MigrationHistory(MigrationId,ContextKey,Model,ProductVersion)
    SELECT '202610230900001_AddCopilotCostPreview',ContextKey,Model,ProductVersion
    FROM dbo.__MigrationHistory WHERE MigrationId='202610221500003_UniqueOnlineMeetingIds';
    RAISERROR('AddCopilotCostPreview: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
END

-- Restore the session. SET NOEXEC OFF still executes under NOEXEC, so this is reached even when the
-- pre-flight above turned NOEXEC on and skipped everything in between.
SET NOEXEC OFF;
