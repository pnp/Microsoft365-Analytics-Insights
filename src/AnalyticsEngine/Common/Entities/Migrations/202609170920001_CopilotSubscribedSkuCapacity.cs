namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Persists the purchased-seat capacity Graph already returns from subscribedSkus.prepaidUnits on
    /// dbo.license_types, so Copilot Adoption can distinguish assigned seats from purchased-but-never-
    /// assigned seats. The change is purely additive: four NULLable metadata columns on a small lookup
    /// table, with no indexes, no defaults and no backfill. Purchased counts remain NULL until the next
    /// successful user metadata import reads subscribedSkus; NULL is deliberately rendered as unknown,
    /// never zero, because missing Graph permission must not look like no waste.
    ///
    /// CLASSIFICATION: PURELY ADDITIVE. This migration is not performance-motivated and carries no
    /// before/after benchmark: it adds NULLable columns to dbo.license_types only. Runtime is metadata-
    /// only and effectively instant at any tenant size.
    ///
    /// The EF entity model is not changed; importer and report code access the columns through raw SQL.
    /// The .resx snapshot therefore intentionally reuses the predecessor model byte-for-byte.
    /// </summary>
    public partial class CopilotSubscribedSkuCapacity : DbMigration
    {
        public const string UpSql = @"
IF COL_LENGTH(N'dbo.license_types', N'prepaid_enabled_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_enabled_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_enabled_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_enabled_units already present, skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.license_types', N'prepaid_warning_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_warning_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_warning_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_warning_units already present, skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.license_types', N'prepaid_suspended_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_suspended_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_suspended_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_suspended_units already present, skipping.', 0, 1) WITH NOWAIT;

IF COL_LENGTH(N'dbo.license_types', N'subscribed_sku_refreshed_utc') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [subscribed_sku_refreshed_utc] [datetime] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.subscribed_sku_refreshed_utc.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.subscribed_sku_refreshed_utc already present, skipping.', 0, 1) WITH NOWAIT;
";

        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'CopilotSubscribedSkuCapacity'. Adds nullable subscribed SKU capacity columns to license_types so Copilot Adoption can report purchased and unassigned seats. Purely additive, metadata-only, no backfill.");
            Sql(UpSql, suppressTransaction: true);
        }

        public override void Down()
        {
            DropColumn("dbo.license_types", "subscribed_sku_refreshed_utc");
            DropColumn("dbo.license_types", "prepaid_suspended_units");
            DropColumn("dbo.license_types", "prepaid_warning_units");
            DropColumn("dbo.license_types", "prepaid_enabled_units");
        }
    }
}
