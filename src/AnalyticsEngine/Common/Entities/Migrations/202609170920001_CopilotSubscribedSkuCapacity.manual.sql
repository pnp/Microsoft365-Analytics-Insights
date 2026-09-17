/* =====================================================================================================
   MANUAL SQL UPGRADE SCRIPT
   Migration: 202609170920001_CopilotSubscribedSkuCapacity
   =====================================================================================================
   Adds nullable subscribed SKU capacity columns to dbo.license_types so Copilot Adoption can report
   purchased and unassigned Microsoft 365 Copilot seats from Graph subscribedSkus.prepaidUnits.

   RUN ORDER
     The manual scripts form a strict prerequisite chain. This one's predecessor is
     202609170910001_GraphCopilotUsageApiV2 and must already be stamped in __MigrationHistory.

   CLASSIFICATION / RUNTIME
     Purely additive. Four nullable columns on a small lookup table; no index, no default, no backfill.
     This is a metadata-only change and is effectively instant. No performance benchmark is required.

   SAFETY
     Idempotent and guarded. The stamp checks schema only (the columns exist), never row data.
   ===================================================================================================== */

SET NOCOUNT ON;
GO

IF COL_LENGTH(N'dbo.license_types', N'prepaid_enabled_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_enabled_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_enabled_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_enabled_units already present, skipping.', 0, 1) WITH NOWAIT;
GO

IF COL_LENGTH(N'dbo.license_types', N'prepaid_warning_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_warning_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_warning_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_warning_units already present, skipping.', 0, 1) WITH NOWAIT;
GO

IF COL_LENGTH(N'dbo.license_types', N'prepaid_suspended_units') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [prepaid_suspended_units] [int] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.prepaid_suspended_units.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.prepaid_suspended_units already present, skipping.', 0, 1) WITH NOWAIT;
GO

IF COL_LENGTH(N'dbo.license_types', N'subscribed_sku_refreshed_utc') IS NULL
BEGIN
    ALTER TABLE [dbo].[license_types] ADD [subscribed_sku_refreshed_utc] [datetime] NULL;
    RAISERROR('CopilotSubscribedSkuCapacity: added license_types.subscribed_sku_refreshed_utc.', 0, 1) WITH NOWAIT;
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: license_types.subscribed_sku_refreshed_utc already present, skipping.', 0, 1) WITH NOWAIT;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170920001_CopilotSubscribedSkuCapacity')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM dbo.__MigrationHistory WHERE MigrationId = N'202609170910001_GraphCopilotUsageApiV2')
    BEGIN
        RAISERROR('CopilotSubscribedSkuCapacity: NOT stamped - predecessor 202609170910001_GraphCopilotUsageApiV2 is not present. Run scripts in migration-id order.', 16, 1);
    END
    ELSE IF COL_LENGTH(N'dbo.license_types', N'prepaid_enabled_units') IS NULL
        OR COL_LENGTH(N'dbo.license_types', N'prepaid_warning_units') IS NULL
        OR COL_LENGTH(N'dbo.license_types', N'prepaid_suspended_units') IS NULL
        OR COL_LENGTH(N'dbo.license_types', N'subscribed_sku_refreshed_utc') IS NULL
    BEGIN
        RAISERROR('CopilotSubscribedSkuCapacity: NOT stamped - one or more schema columns are missing. Re-run this guarded script and check earlier output.', 16, 1);
    END
    ELSE
    BEGIN
        INSERT INTO dbo.__MigrationHistory (MigrationId, ContextKey, Model, ProductVersion)
        SELECT N'202609170920001_CopilotSubscribedSkuCapacity', ContextKey, Model, ProductVersion
        FROM dbo.__MigrationHistory
        WHERE MigrationId = N'202609170910001_GraphCopilotUsageApiV2';
        RAISERROR('CopilotSubscribedSkuCapacity: stamped __MigrationHistory.', 0, 1) WITH NOWAIT;
    END
END
ELSE
    RAISERROR('CopilotSubscribedSkuCapacity: __MigrationHistory already stamped, skipping.', 0, 1) WITH NOWAIT;
GO
