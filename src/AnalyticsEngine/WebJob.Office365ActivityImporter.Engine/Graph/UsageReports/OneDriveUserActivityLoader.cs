using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    // https://docs.microsoft.com/en-us/graph/api/reportroot-getonedriveactivityuserdetail?view=graph-rest-beta
    public class OneDriveUserActivityLoader : AbstractUserDailyActivityLoader<OneDriveUserActivityLog, OneDriveUserActivityDetail>
    {
        public OneDriveUserActivityLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(OneDriveUserActivityLog todaysLog, OneDriveUserActivityDetail userActivityReportPage)
        {
            todaysLog.SharedInternally = userActivityReportPage.SharedInternally;
            todaysLog.SharedExternally = userActivityReportPage.SharedExternally;
            todaysLog.Synced = userActivityReportPage.Synced;
            todaysLog.ViewedOrEdited = userActivityReportPage.ViewedOrEdited;
            todaysLog.LastActivityDate = userActivityReportPage.LastActivityDate;
        }

        protected override long CountActivity(OneDriveUserActivityDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;

            count += activityPage.SharedInternally;
            count += activityPage.SharedExternally;
            count += activityPage.Synced;
            count += activityPage.ViewedOrEdited;

            return count;
        }
        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getOneDriveActivityUserDetail";

        public override DbSet<OneDriveUserActivityLog> GetTable(AnalyticsEntitiesContext context) => context.OneDriveUserActivityLogs;

    }
}
