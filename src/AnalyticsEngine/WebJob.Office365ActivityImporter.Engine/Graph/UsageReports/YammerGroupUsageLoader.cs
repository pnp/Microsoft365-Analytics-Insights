using Common.Entities;
using Common.Entities.Entities;
using Common.Entities.Entities.Teams;
using Common.Entities.LookupCaches;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammergroupsactivitydetail?view=graph-rest-beta
    /// </summary>
    public class YammerGroupUsageLoader : AbstractDailyActivityLoader<YammerGroupActivityLog, YammerGroupActivityDetail, YammerGroup, YammerGroupCache>
    {
        public YammerGroupUsageLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(YammerGroupActivityLog todaysLog, YammerGroupActivityDetail userActivityReportPage)
        {
            // Convert serialised object to DB object
            todaysLog.ReadCount = GetOptionalInt(userActivityReportPage.ReadCount);
            todaysLog.MemberCount = GetOptionalInt(userActivityReportPage.MemberCount);
            todaysLog.PostedCount = GetOptionalInt(userActivityReportPage.PostedCount);
            todaysLog.LikedCount = GetOptionalInt(userActivityReportPage.LikedCount);
        }

        protected override long CountActivity(YammerGroupActivityDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;
            count += GetOptionalInt(activityPage.ReadCount);
            count += GetOptionalInt(activityPage.MemberCount);
            count += GetOptionalInt(activityPage.PostedCount);
            count += GetOptionalInt(activityPage.LikedCount);

            return count;
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getYammerGroupsActivityDetail";


        public override DbSet<YammerGroupActivityLog> GetTable(AnalyticsEntitiesContext context) => context.YammerGroupActivityLogs;
    }
}
