namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Purely additive, empty classification, immutable taxonomy and run-counter tables. No existing
    /// table is rewritten and no performance-motivated index change is made. Typical upgrade is seconds,
    /// independent of whether the interactions table has 1M, 10M or 100M rows; short metadata locks only.
    /// The EF model is unchanged and the predecessor's snapshot is reused verbatim.
    /// </summary>
    public partial class AddPromptCategories : DbMigration
    {
        public const string Up_Sql = @"SET NOCOUNT ON;
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
IF OBJECT_ID(N'dbo.copilot_prompt_classification_runs', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.copilot_prompt_classification_runs (
        run_id int NOT NULL CONSTRAINT PK_copilot_prompt_classification_runs PRIMARY KEY,
        counters_json nvarchar(max) NOT NULL,
        CONSTRAINT FK_prompt_classification_run FOREIGN KEY (run_id)
            REFERENCES dbo.copilot_interaction_import_log(id) ON DELETE CASCADE
    );
END;
RAISERROR('AddPromptCategories: schema ready.', 0, 1) WITH NOWAIT;";

        public override void Up() => Sql(Up_Sql, suppressTransaction: true);

        public override void Down()
        {
            Sql("DROP TABLE dbo.copilot_prompt_classification_runs; DROP TABLE dbo.copilot_prompt_classifications; DROP TABLE dbo.copilot_prompt_taxonomies;");
        }
    }
}
