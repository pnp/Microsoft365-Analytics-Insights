using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public class TeamsUserUsageLoader : AbstractUserDailyActivityLoader<GlobalTeamsUserUsageLog, TeamsUserActivityUserDetail>
    {
        public TeamsUserUsageLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }
        protected override void PopulateReportSpecificMetadata(GlobalTeamsUserUsageLog todaysLog, TeamsUserActivityUserDetail userActivityReportPage)
        {
            // Convert serialised object to DB object
            todaysLog.CallCount = userActivityReportPage.CallCount;
            todaysLog.MeetingCount = userActivityReportPage.MeetingCount;
            todaysLog.PrivateChatMessageCount = userActivityReportPage.PrivateChatMessageCount;
            todaysLog.TeamChatMessageCount = userActivityReportPage.TeamChatMessageCount;

            todaysLog.AdHocMeetingsAttendedCount = userActivityReportPage.AdHocMeetingsAttendedCount;
            todaysLog.AdHocMeetingsOrganizedCount = userActivityReportPage.AdHocMeetingsOrganizedCount;
            todaysLog.MeetingsAttendedCount = userActivityReportPage.MeetingsAttendedCount;
            todaysLog.MeetingsOrganizedCount = userActivityReportPage.MeetingsOrganizedCount;
            todaysLog.ScheduledOneTimeMeetingsAttendedCount = userActivityReportPage.ScheduledOneTimeMeetingsAttendedCount;
            todaysLog.ScheduledOneTimeMeetingsOrganizedCount = userActivityReportPage.ScheduledOneTimeMeetingsOrganizedCount;
            todaysLog.ScheduledRecurringMeetingsAttendedCount = userActivityReportPage.ScheduledRecurringMeetingsAttendedCount;
            todaysLog.ScheduledRecurringMeetingsOrganizedCount = userActivityReportPage.ScheduledRecurringMeetingsOrganizedCount;

            todaysLog.UrgentMessages = userActivityReportPage.UrgentMessages;
            todaysLog.PostMessages = userActivityReportPage.PostMessages;
            todaysLog.ReplyMessages = userActivityReportPage.ReplyMessages;

            // ISO8601 duration strings.
            todaysLog.AudioDurationSeconds = System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.AudioDuration).Seconds;
            todaysLog.VideoDurationSeconds = System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.VideoDuration).Seconds;
            todaysLog.ScreenShareDurationSeconds = System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.ScreenShareDuration).Seconds;
        }

        protected override long CountActivity(TeamsUserActivityUserDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            long count = 0;
            count += activityPage.AdHocMeetingsAttendedCount;
            count += activityPage.AdHocMeetingsOrganizedCount;
            count += activityPage.CallCount;
            count += activityPage.MeetingCount;
            count += activityPage.MeetingsAttendedCount;
            count += activityPage.MeetingsOrganizedCount;
            count += activityPage.PrivateChatMessageCount;
            count += activityPage.ScheduledOneTimeMeetingsAttendedCount;
            count += activityPage.ScheduledOneTimeMeetingsOrganizedCount;
            count += activityPage.ScheduledRecurringMeetingsAttendedCount;
            count += activityPage.ScheduledRecurringMeetingsOrganizedCount;
            count += activityPage.TeamChatMessageCount;
            count += activityPage.UrgentMessages;
            count += activityPage.PostMessages;
            count += activityPage.ReplyMessages;

            return count;
        }

        public override DbSet<GlobalTeamsUserUsageLog> GetTable(AnalyticsEntitiesContext context) => context.TeamUserActivityLogs;

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getTeamsUserActivityUserDetail";
    }
}
