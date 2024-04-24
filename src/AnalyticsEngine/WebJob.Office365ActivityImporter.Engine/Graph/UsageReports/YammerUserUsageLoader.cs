using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammeractivityuserdetail?view=graph-rest-beta
    /// </summary>
    public class YammerUserUsageLoader : AbstractUserDailyActivityLoader<YammerUserActivityLog, YammerUserActivityUserDetail>
    {
        public YammerUserUsageLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(YammerUserActivityLog todaysLog, YammerUserActivityUserDetail userActivityReportPage)
        {
            // Convert serialised object to DB object
            todaysLog.ReadCount = GetOptionalInt(userActivityReportPage.ReadCount);
            todaysLog.LikedCount = GetOptionalInt(userActivityReportPage.LikedCount);
            todaysLog.PostedCount = GetOptionalInt(userActivityReportPage.PostedCount);
        }

        protected override long CountActivity(YammerUserActivityUserDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;
            count += GetOptionalInt(activityPage.ReadCount);
            count += GetOptionalInt(activityPage.LikedCount);
            count += GetOptionalInt(activityPage.PostedCount);

            return count;
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getYammerActivityUserDetail";
        public override DbSet<YammerUserActivityLog> GetTable(AnalyticsEntitiesContext context) => context.YammerUserActivityLogs;

    }
}
