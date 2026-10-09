-- Manual upgrade: purely additive empty tables, seconds at 1M/10M/100M existing interactions.
-- Prerequisite: 202610071400001_CopilotTurnPairing. Run scripts in migration-id order.
-- No online/offline index rebuild, table rewrite or data backfill.
IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId=N'202610071400001_CopilotTurnPairing')
BEGIN
    RAISERROR('AddPromptCategories: prerequisite 202610071400001_CopilotTurnPairing is not stamped.',16,1);
    RETURN;
END;
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
RAISERROR('AddPromptCategories: adding empty prompt classification tables.', 0, 1) WITH NOWAIT;
IF OBJECT_ID(N'dbo.copilot_prompt_taxonomies', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.copilot_prompt_taxonomies (
        version nvarchar(64) NOT NULL CONSTRAINT PK_copilot_prompt_taxonomies PRIMARY KEY,
        categories_json nvarchar(max) NOT NULL
    );
END;
IF OBJECT_ID(N'dbo.copilot_prompt_classifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.copilot_prompt_classifications (
        interaction_id int NOT NULL CONSTRAINT PK_copilot_prompt_classifications PRIMARY KEY,
        category_id nvarchar(40) NOT NULL,
        taxonomy_version nvarchar(64) NOT NULL,
        human_mode nvarchar(20) NULL,
        CONSTRAINT FK_prompt_classification_interaction FOREIGN KEY (interaction_id)
            REFERENCES dbo.copilot_interactions(id) ON DELETE CASCADE,
        CONSTRAINT FK_prompt_classification_taxonomy FOREIGN KEY (taxonomy_version)
            REFERENCES dbo.copilot_prompt_taxonomies(version),
        CONSTRAINT CK_prompt_classification_mode CHECK (human_mode IS NULL OR human_mode IN (N'directing', N'supervising'))
    );
END;
RAISERROR('AddPromptCategories: schema ready.', 0, 1) WITH NOWAIT;
-- Schema only: never require data to be perfect while an importer can write concurrently.
IF COL_LENGTH(N'dbo.copilot_prompt_classifications',N'human_mode') IS NULL
   OR COL_LENGTH(N'dbo.copilot_prompt_classifications',N'interaction_id') IS NULL
   OR COL_LENGTH(N'dbo.copilot_prompt_classifications',N'category_id') IS NULL
   OR COL_LENGTH(N'dbo.copilot_prompt_classifications',N'taxonomy_version') IS NULL
   OR COL_LENGTH(N'dbo.copilot_prompt_taxonomies',N'categories_json') IS NULL
   OR OBJECT_ID(N'dbo.FK_prompt_classification_interaction',N'F') IS NULL
   OR OBJECT_ID(N'dbo.FK_prompt_classification_taxonomy',N'F') IS NULL
   OR OBJECT_ID(N'dbo.copilot_prompt_taxonomies',N'U') IS NULL
BEGIN
    RAISERROR('AddPromptCategories: NOT stamped - schema incomplete.',16,1);
END
ELSE IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId=N'202610080900001_AddPromptCategories')
BEGIN
    INSERT dbo.__MigrationHistory(MigrationId,ContextKey,Model,ProductVersion)
    SELECT N'202610080900001_AddPromptCategories',ContextKey,Model,ProductVersion
    FROM dbo.__MigrationHistory WHERE MigrationId=N'202610071400001_CopilotTurnPairing';
END;
