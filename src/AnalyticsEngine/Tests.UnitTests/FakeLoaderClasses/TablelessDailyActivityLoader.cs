using Common.Entities;
using Common.Entities.ActivityReports;
using Common.Entities.Config;
using Microsoft.Extensions.Logging;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// A report entity that deliberately declares NO <c>[Table]</c> attribute, so the two loader methods
    /// that resolve a table name can be driven down their missing-attribute paths. See issue #375.
    /// </summary>
    public class TablelessUsageActivityLog : AbstractUsageActivityLog
    {
        public override int AssociatedLookupId { get; set; }
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractDailyActivityLoader{,,,}"/> over
    /// <see cref="TablelessUsageActivityLog"/>, existing only so the storage-inspector wiring can be
    /// exercised without SQL Server or Graph.
    ///
    /// Neither method under test reaches the database: <c>CompactColumnstoreAsync</c> returns before it
    /// touches the inspector, and <c>HasLeadingDateIndexAsync</c> resolves the inspector (the injected fake)
    /// before evaluating the table name, so both can be called with a null context.
    /// </summary>
    public class TablelessDailyActivityLoader
        : AbstractUserDailyActivityLoader<TablelessUsageActivityLog, OutlookUserActivityUserDetail>
    {
        public TablelessDailyActivityLoader(ILogger logger)
            : base(null, null, new UserGroupsFilterModel(null), logger)
        {
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/thisReportDoesNotExist";

        public override DbSet<TablelessUsageActivityLog> GetTable(AnalyticsEntitiesContext context) => null;

        protected override void PopulateReportSpecificMetadata(TablelessUsageActivityLog todaysLog, OutlookUserActivityUserDetail page)
        {
        }

        protected override long CountActivity(OutlookUserActivityUserDetail activityPage) => 0;
    }
}
