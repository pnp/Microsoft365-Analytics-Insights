namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Adds <c>dbo.portal_global_filters</c>: the administrator's global report filter - the conditions
    /// every insights report in the portal applies on top of a reader's own filters, and which only a
    /// portal administrator can change.
    ///
    /// <para>
    /// <b>Classification: purely additive.</b> One new, empty table; no existing table is rewritten, no
    /// existing column is altered and no existing query is being tuned. The benchmark gate that applies to
    /// <i>performance-motivated</i> schema changes therefore does not apply - there is no "before" query to
    /// measure. The table holds at most one row, read once a minute per web process.
    /// </para>
    ///
    /// <para>
    /// <b>No EF model change.</b> The table is read and written with raw SQL
    /// (<c>Common.Entities.UserFilters.SqlGlobalFilterStore</c>), exactly as the user organisation tables
    /// are. Because the entity model is untouched, the <c>.resx</c> snapshot is a byte-identical copy of the
    /// predecessor migration's, so EF sees <c>model == latest snapshot</c>, never auto-migrates, and cannot
    /// throw <c>AutomaticDataLossException</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Before the upgrade.</b> The web app treats a missing table as "no global filter", so a portal
    /// deployed ahead of its database keeps working; only saving a filter needs the table.
    /// </para>
    /// </summary>
    public partial class PortalGlobalFilter : DbMigration
    {
        /// <summary>
        /// Additive, guarded and re-runnable. Exposed so the manual DBA script can embed exactly the SQL
        /// that EF runs.
        /// </summary>
        public const string Up_Sql = @"SET NOCOUNT ON;

RAISERROR('PortalGlobalFilter: adding the portal global report filter table. Purely additive schema, so no performance benchmark is required.', 0, 1) WITH NOWAIT;

-- ---------------------------------------------------------------------------
-- portal_global_filters - the administrator's global report filter.
--   One row at most (id = 1). filter_json is the filter's wire form: an array of
--   conditions, each optionally naming an attribute of the person viewing the
--   report (see GlobalFilterCodec). An empty string means no filter.
--   nvarchar, never varchar: the values are department names, cost centres and
--   other text from a customer tenant, routinely in non-Latin scripts.
--   revision moves on every save, so a save from a page opened before somebody
--   else's change is refused rather than quietly putting back what they changed.
-- ---------------------------------------------------------------------------
IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[portal_global_filters]
    (
        [id] int NOT NULL,
        [filter_json] nvarchar(max) NOT NULL CONSTRAINT [DF_portal_global_filters_filter_json] DEFAULT (N''),
        [revision] int NOT NULL CONSTRAINT [DF_portal_global_filters_revision] DEFAULT (1),
        [modified_utc] datetime2(7) NOT NULL CONSTRAINT [DF_portal_global_filters_modified_utc] DEFAULT SYSUTCDATETIME(),
        [modified_by] nvarchar(256) NULL,
        CONSTRAINT [PK_portal_global_filters] PRIMARY KEY CLUSTERED ([id] ASC),
        CONSTRAINT [CK_portal_global_filters_single_row] CHECK ([id] = 1)
    );

    RAISERROR('PortalGlobalFilter: created dbo.portal_global_filters.', 0, 1) WITH NOWAIT;
END
ELSE
BEGIN
    RAISERROR('PortalGlobalFilter: dbo.portal_global_filters already exists.', 0, 1) WITH NOWAIT;
END

RAISERROR('PortalGlobalFilter: finished.', 0, 1) WITH NOWAIT;
";

        public const string Down_Sql = @"SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.portal_global_filters', N'U') IS NOT NULL
    DROP TABLE [dbo].[portal_global_filters];
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'PortalGlobalFilter'. Adds the portal's global report filter table. Purely additive; no performance benchmark required.");
            Sql(Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'PortalGlobalFilter'. Drops the portal global report filter table.");
            Sql(Down_Sql, suppressTransaction: true);
        }
    }
}
