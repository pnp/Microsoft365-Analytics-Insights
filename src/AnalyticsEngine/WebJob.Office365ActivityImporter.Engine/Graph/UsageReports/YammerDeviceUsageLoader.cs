using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    /// <summary>
    /// https://docs.microsoft.com/en-us/graph/api/reportroot-getyammerdeviceusageuserdetail?view=graph-rest-beta
    /// </summary>
    public class YammerDeviceUsageLoader : AbstractUserDailyActivityLoader<YammerDeviceActivityLog, YammerDeviceActivityDetail>
    {
        public YammerDeviceUsageLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(YammerDeviceActivityLog todaysLog, YammerDeviceActivityDetail userActivityReportPage)
        {
            // Convert serialised object to DB object
            todaysLog.UsedWeb = userActivityReportPage.UsedWeb;
            todaysLog.UsedIpad = userActivityReportPage.UsedIpad;
            todaysLog.UsedIphone = userActivityReportPage.UsedIphone;
            todaysLog.UsedAndroidPhone = userActivityReportPage.UsedAndroidPhone;
            todaysLog.UsedWindowsPhone = userActivityReportPage.UsedWindowsPhone;
            todaysLog.UsedOthers = userActivityReportPage.UsedOthers;
        }

        protected override long CountActivity(YammerDeviceActivityDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;
            if (activityPage.UsedAndroidPhone.HasValue && activityPage.UsedAndroidPhone.Value) count++;
            if (activityPage.UsedWeb.HasValue && activityPage.UsedWeb.Value) count++;
            if (activityPage.UsedWindowsPhone.HasValue && activityPage.UsedWindowsPhone.Value) count++;
            if (activityPage.UsedIpad.HasValue && activityPage.UsedIpad.Value) count++;
            if (activityPage.UsedIphone.HasValue && activityPage.UsedIphone.Value) count++;
            if (activityPage.UsedOthers.HasValue && activityPage.UsedOthers.Value) count++;

            return count;
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getYammerDeviceUsageUserDetail";
        public override DbSet<YammerDeviceActivityLog> GetTable(AnalyticsEntitiesContext context) => context.YammerDeviceActivityLogs;

    }
}
