namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Lets the Azure cost import record the tag it grouped the Cost Management query by.
    ///
    /// WHY
    ///   Microsoft bills Copilot Cowork, Work IQ API and Copilot Studio through a SINGLE common Copilot
    ///   Credits meter - "Pay As You Go Copilot Credit", under the "Microsoft Copilot Studio" service - and
    ///   documents that there is no separate Azure line item per experience. The only thing that separates
    ///   them on the bill is a tag (observed as serviceName=Cowork / policyName=&lt;spending policy&gt;), which
    ///   is injected onto the usage record by the billing pipeline and is NOT an ARM tag on the resource.
    ///   Without somewhere to put it, "how much of this was Cowork?" is unanswerable from stored data.
    ///
    ///   Note this is also the only route to that figure: Cowork pay-as-you-go consumption does not draw on
    ///   the Copilot Studio MCSMessages entitlement, so the Copilot Studio credit import reports nothing for
    ///   it even when that import is working.
    ///
    /// SCHEMA CHANGES
    ///   New columns (both NULLable, no default, no index, no foreign key)
    ///     * azure_cost_daily.tag_key    nvarchar(256)
    ///     * azure_cost_daily.tag_value  nvarchar(512)
    ///
    ///   nvarchar, not varchar: tag values are tenant-controlled text and may be non-Latin. Both sit under
    ///   the 850-character Unicode indexable width in case an index is ever wanted, though none is added
    ///   here - the report groups by these columns over a date-filtered window, not by seeking them.
    ///
    /// CLASSIFICATION: PURELY ADDITIVE. Not performance-motivated, so it carries no before/after benchmark:
    /// there is no "before" query to measure. Per the repo's schema-change rule additive migrations are
    /// explicitly out of scope for that requirement, and saying so is part of the review rather than an
    /// omission from it.
    ///
    /// COST / RUNTIME
    ///   Both AddColumn operations add a NULLable column with no default, which SQL Server applies as a
    ///   metadata-only change - instant on any table size, no rewrite, no scan, no index build. No
    ///   maintenance window is needed on any edition, and the importer can keep running through it.
    ///
    /// BACKFILL: NONE, AND NONE IS POSSIBLE.
    ///   Existing rows keep NULL. The tag is not recoverable from stored data because the Cost Management
    ///   query API only returns tags when the query GROUPS BY a tag key, so historic rows were fetched
    ///   without it. Re-importing the trailing window with a tag grouping configured repopulates that window
    ///   (the Azure write replaces a scope and window rather than appending); anything older stays NULL.
    ///
    /// OPT-IN, DELIBERATELY.
    ///   Nothing changes until an operator sets the AzureCostGroupBy app setting to include a TagKey entry,
    ///   e.g. "ResourceId;TagKey:serviceName". Cost Management permits only TWO group-by clauses, so a tag
    ///   grouping necessarily costs one of them - the trade is normally Meter for the service split. Leaving
    ///   it off keeps today's behaviour exactly, which is why this migration does not change any default.
    ///
    /// This migration DOES change the EF entity model, so its .resx snapshot is freshly scaffolded rather
    /// than copied from the predecessor, and it is now the head of the chain that EF compares the live model
    /// against at runtime. The manual upgrade script therefore stamps __MigrationHistory with this new model
    /// blob rather than copying the previous row.
    ///
    /// Left as a single atomic EF transaction - no Sql(..., suppressTransaction: true) - because both
    /// operations are instant metadata changes. There is nothing long-running to make resumable.
    /// </summary>
    public partial class AzureCostTagColumns : DbMigration
    {
        public override void Up()
        {
            Console.WriteLine("DB SCHEMA: Applying 'AzureCostTagColumns'. Adds azure_cost_daily.tag_key and .tag_value so the Azure cost import can record the tag it grouped by - the only way to tell Copilot Cowork, Work IQ API and Copilot Studio spend apart, because Microsoft bills all three through one 'Pay As You Go Copilot Credit' meter. Both are NULLable nvarchar columns with no default, which SQL Server applies as a metadata-only change: instant regardless of table size. Existing rows keep NULL until the import is reconfigured to group by a tag.");

            AddColumn("dbo.azure_cost_daily", "tag_key", c => c.String(maxLength: 256));
            AddColumn("dbo.azure_cost_daily", "tag_value", c => c.String(maxLength: 512));
        }

        public override void Down()
        {
            Console.WriteLine("DB SCHEMA: Reverting 'AzureCostTagColumns'.");

            DropColumn("dbo.azure_cost_daily", "tag_value");
            DropColumn("dbo.azure_cost_daily", "tag_key");
        }
    }
}

