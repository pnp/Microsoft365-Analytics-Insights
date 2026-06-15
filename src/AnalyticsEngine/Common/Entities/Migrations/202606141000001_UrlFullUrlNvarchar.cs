namespace Common.Entities.Migrations
{
    using System;
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Converts <c>dbo.urls.full_url</c> to <c>nvarchar(850)</c> on databases that applied the
    /// original, now-superseded, <c>varchar(1700)</c> form of <see cref="ShrinkUrlsFullUrlColumn"/>
    /// and its metadata-only follow-up <see cref="UrlFullUrlVarcharMapping"/>, and refreshes the
    /// EF model snapshot so it matches the corrected <see cref="Common.Entities.Url.FullUrl"/>
    /// <c>nvarchar(850)</c> mapping. See issue #122.
    ///
    /// Background: an earlier release shipped <see cref="ShrinkUrlsFullUrlColumn"/> /
    /// <see cref="UrlFullUrlVarcharMapping"/> which made <c>full_url</c> a single-code-page
    /// <c>varchar(1700)</c> column. That corrupts non-Latin SharePoint URLs (e.g. Greek) to '?'.
    /// <see cref="ShrinkUrlsFullUrlColumn"/> has since been corrected in place to target
    /// <c>nvarchar(850)</c> (1700 bytes = the index-key limit), but customers who already applied
    /// its varchar form will never re-run it (EF6 keys migrations by id, not by SQL content). This
    /// migration is the catch-up: its <see cref="Up"/> simply replays the corrected, idempotent
    /// <see cref="ShrinkUrlsFullUrlColumn.Up_Sql"/> converter, which:
    ///   * is a no-op on databases already at <c>nvarchar(850)</c> (fresh installs and customers
    ///     upgrading from the pre-shrink <c>RemoveDataverseTables</c> release, who picked up the
    ///     corrected <see cref="ShrinkUrlsFullUrlColumn"/> directly); and
    ///   * performs the lossless <c>varchar(1700) -> nvarchar(850)</c> conversion (dropping and
    ///     re-creating <c>IX_urls_full_url</c> around the ALTER) on databases on the old varchar
    ///     form.
    /// Widening <c>varchar -> nvarchar</c> never loses data. The converter still aborts, before
    /// any schema change, if any URL exceeds 850 characters.
    ///
    /// The model-snapshot half of the job is done by this migration's <c>.resx</c> (the latest
    /// snapshot EF compares against at runtime), which encodes <c>full_url</c> as
    /// <c>nvarchar(850)</c>. Without it, <c>AnalyticsEntitiesContext</c> construction would throw
    /// <c>AutomaticDataLossException</c> against the corrected entity model.
    /// </summary>
    public partial class UrlFullUrlNvarchar : DbMigration
    {
        public override void Up()
        {
            // Runs outside the EF migration transaction (suppressTransaction: true) so the
            // schema-modification lock from any ALTER COLUMN is released as soon as it completes
            // and a pre-flight failure leaves the DB untouched. Idempotent: a no-op when full_url
            // is already nvarchar(850). See ShrinkUrlsFullUrlColumn.Up_Sql for the full rationale.
            Console.WriteLine("DB SCHEMA: Applying 'UrlFullUrlNvarchar'. Ensures dbo.urls.full_url is nvarchar(850) (Unicode-safe, e.g. Greek URLs). No-op if already nvarchar(850); converts databases still on the superseded varchar(1700) form. If any URL is longer than 850 chars the migration aborts and lists the offending id + url so you can fix the data and re-run.");
            Sql(ShrinkUrlsFullUrlColumn.Up_Sql, suppressTransaction: true);
        }

        public override void Down()
        {
            // Reuse the ShrinkUrlsFullUrlColumn down script: drop the index and widen full_url back
            // to nvarchar(max). The model snapshot then reverts to UrlFullUrlVarcharMapping's.
            Console.WriteLine("DB SCHEMA: Reverting 'UrlFullUrlNvarchar'.");
            Sql(ShrinkUrlsFullUrlColumn.Down_Sql, suppressTransaction: true);
        }
    }
}
