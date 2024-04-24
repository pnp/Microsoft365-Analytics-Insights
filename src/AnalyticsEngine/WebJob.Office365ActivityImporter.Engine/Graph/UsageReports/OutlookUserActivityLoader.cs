using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public class OutlookUserActivityLoader : AbstractUserDailyActivityLoader<OutlookUsageActivityLog, OutlookUserActivityUserDetail>
    {
        public OutlookUserActivityLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(OutlookUsageActivityLog todaysLog, OutlookUserActivityUserDetail userActivityReportPage)
        {
            todaysLog.MeetingCreated = userActivityReportPage.MeetingCreated;
            todaysLog.ReadCount = userActivityReportPage.ReadCount;
            todaysLog.ReceiveCount = userActivityReportPage.ReceiveCount;
            todaysLog.SendCount = userActivityReportPage.SendCount;
            todaysLog.MeetingInteracted = userActivityReportPage.MeetingInteracted;

        }

        protected override long CountActivity(OutlookUserActivityUserDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;
            count += activityPage.ReadCount;
            count += activityPage.ReceiveCount;
            count += activityPage.SendCount;
            count += activityPage.MeetingCreated;
            count += activityPage.MeetingInteracted;

            return count;
        }
        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getEmailActivityUserDetail";
        public override DbSet<OutlookUsageActivityLog> GetTable(AnalyticsEntitiesContext context) => context.OutlookUsageActivityLogs;

    }
}
