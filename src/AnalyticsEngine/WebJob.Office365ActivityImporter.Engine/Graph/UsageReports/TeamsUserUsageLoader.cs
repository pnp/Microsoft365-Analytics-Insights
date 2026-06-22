using Common.Entities;
using Common.Entities.Config;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;
using WebJob.Office365ActivityImporter.Engine.Graph.User;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    public class TeamsUserUsageLoader : AbstractUserDailyActivityLoader<GlobalTeamsUserUsageLog, TeamsUserActivityUserDetail>
    {
        public TeamsUserUsageLoader(ManualGraphCallClient client, UserGroupsCache userGroupsCache, UserGroupsFilterModel userGroupsFilterModel, ILogger telemetry)
            : base(client, userGroupsCache, userGroupsFilterModel, telemetry)
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

            // ISO8601 duration strings. Use TotalSeconds (not .Seconds, which is only the 0-59
            // seconds COMPONENT and silently truncated any call >= 1 minute, e.g. PT1H2M3S -> 3).
            todaysLog.AudioDurationSeconds = (int)System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.AudioDuration).TotalSeconds;
            todaysLog.VideoDurationSeconds = (int)System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.VideoDuration).TotalSeconds;
            todaysLog.ScreenShareDurationSeconds = (int)System.Xml.XmlConvert.ToTimeSpan(userActivityReportPage.ScreenShareDuration).TotalSeconds;
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
