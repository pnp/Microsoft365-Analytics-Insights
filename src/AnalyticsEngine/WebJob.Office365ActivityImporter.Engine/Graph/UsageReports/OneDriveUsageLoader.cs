using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    // https://docs.microsoft.com/en-us/graph/api/reportroot-getonedriveusageaccountdetail?view=graph-rest-beta
    public class OneDriveUsageLoader : AbstractUserDailyActivityLoader<OneDriveUsageLog, OneDriveUsageDetail>
    {
        public OneDriveUsageLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(OneDriveUsageLog todaysLog, OneDriveUsageDetail userActivityReportPage)
        {
            todaysLog.StorageUsedInBytes = userActivityReportPage.StorageUsedInBytes;
            todaysLog.FileCount = userActivityReportPage.FileCount;
            todaysLog.ActiveFileCount = userActivityReportPage.ActiveFileCount;
        }

        protected override long CountActivity(OneDriveUsageDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;

            count += activityPage.StorageUsedInBytes;
            count += activityPage.FileCount;
            count += activityPage.ActiveFileCount;

            return count;
        }
        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getOneDriveUsageAccountDetail";
        public override DbSet<OneDriveUsageLog> GetTable(AnalyticsEntitiesContext context) => context.OneDriveUsageLogs;

    }
}
