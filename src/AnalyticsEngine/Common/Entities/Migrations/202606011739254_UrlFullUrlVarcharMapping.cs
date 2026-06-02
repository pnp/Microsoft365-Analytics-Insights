namespace Common.Entities.Migrations
{
    using System.Data.Entity.Migrations;

    /// <summary>
    /// Metadata-only follow-up to <see cref="ShrinkUrlsFullUrlColumn"/> (PR #108).
    ///
    /// This migration's <see cref="Up"/> / <see cref="Down"/> are intentionally empty.
    /// Its purpose is to bring the latest <c>__MigrationHistory</c> model snapshot in sync with
    /// the C# entity model after <see cref="Common.Entities.Url.FullUrl"/> was annotated as
    /// <c>varchar(1700) NOT NULL</c> (so EF parameterises with <c>varchar</c> instead of
    /// <c>nvarchar</c> and the queries on <c>urls.full_url</c> can actually use
    /// <c>IX_urls_full_url</c>). Without this snapshot refresh, every
    /// <c>AnalyticsEntitiesContext</c> construction would throw
    /// <c>AutomaticDataLossException</c> at startup because EF would compute an automatic
    /// migration that drops the old <c>nvarchar(max)</c> column. See issue #109.
    ///
    /// The actual on-disk column-type change and supporting index are performed by
    /// <see cref="ShrinkUrlsFullUrlColumn"/>. Replaying an <c>AlterColumn</c> here would fail
    /// because <c>full_url</c> is now part of <c>IX_urls_full_url</c> (SQL Server blocks
    /// <c>ALTER COLUMN</c> on indexed columns).
    /// </summary>
    public partial class UrlFullUrlVarcharMapping : DbMigration
    {
        public override void Up()
        {
            // Intentionally empty - see class doc. Schema already in target state from
            // ShrinkUrlsFullUrlColumn; this migration only refreshes the model snapshot.
        }

        public override void Down()
        {
            // Intentionally empty - down would just leave the snapshot pointing back at the
            // ShrinkUrlsFullUrlColumn snapshot, no schema change required to undo.
        }
    }
}
