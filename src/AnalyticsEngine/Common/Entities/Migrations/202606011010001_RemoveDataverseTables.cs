namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Defensive cleanup for an early dev version of <see cref="PowerPlatformAuditLogging"/>
    /// that created <c>dataverse_entities</c> and <c>event_meta_dataverse</c> tables. We
    /// decided not to ship those tables (Dataverse adoption events are not in scope - see the
    /// Power Platform import: agents, apps, flows and reports only), so this migration drops
    /// them on any database that already has them.
    ///
    /// Safe to run on fresh installs (no-op when the tables are absent) and on databases that
    /// only have the post-rework PowerPlatformAuditLogging migration applied.
    /// </summary>
    public partial class RemoveDataverseTables : DbMigration
    {
        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'RemoveDataverseTables' (defensive cleanup of obsolete Dataverse tables, no-op on fresh installs).");
            Sql(@"
SET NOCOUNT ON;

DECLARE @migration nvarchar(100) = N'RemoveDataverseTables';
DECLARE @msg nvarchar(2000);

IF OBJECT_ID(N'dbo.event_meta_dataverse', N'U') IS NOT NULL
BEGIN
    SET @msg = @migration + N': dropping dbo.event_meta_dataverse.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    DROP TABLE [dbo].[event_meta_dataverse];
END

IF OBJECT_ID(N'dbo.dataverse_entities', N'U') IS NOT NULL
BEGIN
    SET @msg = @migration + N': dropping dbo.dataverse_entities.';
    RAISERROR(@msg, 0, 1) WITH NOWAIT;
    DROP TABLE [dbo].[dataverse_entities];
END
");
        }

        public override void Down()
        {
            // One-way cleanup: we do not recreate the dropped tables. Re-introducing
            // Dataverse support would be a fresh feature design, not a Down() of this migration.
        }
    }
}
