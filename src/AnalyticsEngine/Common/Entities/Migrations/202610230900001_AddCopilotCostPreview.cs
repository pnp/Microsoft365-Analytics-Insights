namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Additive preview stores only: creates empty tables, without changing the EF model or existing
    /// tenant tables. No before/after performance improvement is claimed. Upgrade cost is empty-table
    /// DDL, independent of existing audit volume; no existing fact-table rewrite or offline index build.
    /// Retention is indefinite until an administrator explicitly runs the bounded dry-run-first purge.
    /// Synthetic LocalDB proof (200k work/parent rows and 1M approximately 1-KiB history bundles;
    /// five warm runs, OPTION RECOMPILE, cold run discarded): ready paging sought in 1 ms / 3 reads
    /// at both dense and sparse eligibility; 1/90-day history sought in 1/11 ms / 14/930 reads;
    /// coverage took 110 ms / 1,912 reads; bounded tenant/user purge previews 6/3 ms / 35/8 reads.
    /// A 999-ID nullable link plus bounded historical relink scans existing users:
    /// 238 ms / 7,213 reads, approximately 48 seconds
    /// for 201 inventory pages at 200k users, excluding Graph latency. No existing-user index change.
    /// The fixture reserved approximately 1.24 GiB for work, parent and history stores including indexes
    /// (service/context rows excluded). Its 115-second data generation is NOT a schema-upgrade estimate
    /// or a claim of Graph throughput. These are additive-store measurements, not before/after gains.
    /// </summary>
    public sealed partial class AddCopilotCostPreview : DbMigration
    {
        public const string Up_Sql = @"
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
";

        public const string Purge_Sql = @"
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
";
        public override void Up()
        {
            Sql(Up_Sql, suppressTransaction: true);
            Sql(Purge_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            // Preview facts must never be destroyed by an automatic rollback.
            throw new System.NotSupportedException("Use the explicit bounded preview purge.");
        }
    }
}
