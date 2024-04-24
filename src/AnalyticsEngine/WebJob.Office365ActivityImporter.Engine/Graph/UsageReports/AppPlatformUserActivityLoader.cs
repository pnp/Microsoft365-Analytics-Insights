using Common.Entities;
using Common.Entities.Entities.Teams;
using Microsoft.Extensions.Logging;
using System;
using System.Data.Entity;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation.UsageReports;

namespace WebJob.Office365ActivityImporter.Engine.Graph.UsageReports
{
    // https://learn.microsoft.com/en-us/graph/api/reportroot-getm365appuserdetail?view=graph-rest-beta
    public class AppPlatformUserActivityLoader : AbstractUserDailyActivityLoader<AppPlatformUserActivityLog, AppPlatformUserActivityDetail>
    {
        public AppPlatformUserActivityLoader(ManualGraphCallClient client, ILogger telemetry)
            : base(client, telemetry)
        {
        }

        public override string ReportGraphURL => "https://graph.microsoft.com/beta/reports/getM365AppUserDetail";

        protected override void PopulateReportSpecificMetadata(AppPlatformUserActivityLog dateRequestedLog, AppPlatformUserActivityDetail reportPage)
        {
            if (reportPage.Details.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one detail record");
            }
            var detail = reportPage.Details[0];
            dateRequestedLog.Windows = detail.Windows.HasValue ? detail.Windows.Value : false;
            dateRequestedLog.Mac = detail.Mac.HasValue ? detail.Mac.Value : false;
            dateRequestedLog.Mobile = detail.Mobile.HasValue ? detail.Mobile.Value : false;
            dateRequestedLog.Web = detail.Web.HasValue ? detail.Web.Value : false;

            dateRequestedLog.Outlook = detail.Outlook.HasValue ? detail.Outlook.Value : false;
            dateRequestedLog.Word = detail.Word.HasValue ? detail.Word.Value : false;
            dateRequestedLog.Excel = detail.Excel.HasValue ? detail.Excel.Value : false;
            dateRequestedLog.PowerPoint = detail.PowerPoint.HasValue ? detail.PowerPoint.Value : false;
            dateRequestedLog.OneNote = detail.OneNote.HasValue ? detail.OneNote.Value : false;
            dateRequestedLog.Teams = detail.Teams.HasValue ? detail.Teams.Value : false;

            dateRequestedLog.OutlookWindows = detail.OutlookWindows.HasValue ? detail.OutlookWindows.Value : false;
            dateRequestedLog.WordWindows = detail.WordWindows.HasValue ? detail.WordWindows.Value : false;
            dateRequestedLog.ExcelWindows = detail.ExcelWindows.HasValue ? detail.ExcelWindows.Value : false;
            dateRequestedLog.PowerPointWindows = detail.PowerPointWindows.HasValue ? detail.PowerPointWindows.Value : false;
            dateRequestedLog.OneNoteWindows = detail.OneNoteWindows.HasValue ? detail.OneNoteWindows.Value : false;
            dateRequestedLog.TeamsWindows = detail.TeamsWindows.HasValue ? detail.TeamsWindows.Value : false;

            dateRequestedLog.OutlookMac = detail.OutlookMac.HasValue ? detail.OutlookMac.Value : false;
            dateRequestedLog.WordMac = detail.WordMac.HasValue ? detail.WordMac.Value : false;
            dateRequestedLog.ExcelMac = detail.ExcelMac.HasValue ? detail.ExcelMac.Value : false;
            dateRequestedLog.PowerPointMac = detail.PowerPointMac.HasValue ? detail.PowerPointMac.Value : false;
            dateRequestedLog.OneNoteMac = detail.OneNoteMac.HasValue ? detail.OneNoteMac.Value : false;
            dateRequestedLog.TeamsMac = detail.TeamsMac.HasValue ? detail.TeamsMac.Value : false;

            dateRequestedLog.OutlookMobile = detail.OutlookMobile.HasValue ? detail.OutlookMobile.Value : false;
            dateRequestedLog.WordMobile = detail.WordMobile.HasValue ? detail.WordMobile.Value : false;
            dateRequestedLog.ExcelMobile = detail.ExcelMobile.HasValue ? detail.ExcelMobile.Value : false;
            dateRequestedLog.PowerPointMobile = detail.PowerPointMobile.HasValue ? detail.PowerPointMobile.Value : false;
            dateRequestedLog.OneNoteMobile = detail.OneNoteMobile.HasValue ? detail.OneNoteMobile.Value : false;
            dateRequestedLog.TeamsMobile = detail.TeamsMobile.HasValue ? detail.TeamsMobile.Value : false;

            dateRequestedLog.OutlookWeb = detail.OutlookWeb.HasValue ? detail.OutlookWeb.Value : false;
            dateRequestedLog.WordWeb = detail.WordWeb.HasValue ? detail.WordWeb.Value : false;
            dateRequestedLog.ExcelWeb = detail.ExcelWeb.HasValue ? detail.ExcelWeb.Value : false;
            dateRequestedLog.PowerPointWeb = detail.PowerPointWeb.HasValue ? detail.PowerPointWeb.Value : false;
            dateRequestedLog.OneNoteWeb = detail.OneNoteWeb.HasValue ? detail.OneNoteWeb.Value : false;
            dateRequestedLog.TeamsWeb = detail.TeamsWeb.HasValue ? detail.TeamsWeb.Value : false;
        }

        protected override long CountActivity(AppPlatformUserActivityDetail activityPage)
        {
            if (activityPage is null)
            {
                throw new ArgumentNullException(nameof(activityPage));
            }

            if (activityPage.Details.Count != 1)
            {
                throw new InvalidOperationException("Expected exactly one detail record");
            }

            var detail = activityPage.Details[0];

            long count = 0;
            if (detail.Windows.HasValue && detail.Windows.Value) count++;
            if (detail.Mac.HasValue && detail.Mac.Value) count++;
            if (detail.Mobile.HasValue && detail.Mobile.Value) count++;
            if (detail.Web.HasValue && detail.Web.Value) count++;

            if (detail.Outlook.HasValue && detail.Outlook.Value) count++;
            if (detail.Word.HasValue && detail.Word.Value) count++;
            if (detail.Excel.HasValue && detail.Excel.Value) count++;
            if (detail.PowerPoint.HasValue && detail.PowerPoint.Value) count++;
            if (detail.OneNote.HasValue && detail.OneNote.Value) count++;
            if (detail.Teams.HasValue && detail.Teams.Value) count++;

            if (detail.OutlookWindows.HasValue && detail.OutlookWindows.Value) count++;
            if (detail.WordWindows.HasValue && detail.WordWindows.Value) count++;
            if (detail.ExcelWindows.HasValue && detail.ExcelWindows.Value) count++;
            if (detail.PowerPointWindows.HasValue && detail.PowerPointWindows.Value) count++;
            if (detail.OneNoteWindows.HasValue && detail.OneNoteWindows.Value) count++;
            if (detail.TeamsWindows.HasValue && detail.TeamsWindows.Value) count++;

            if (detail.OutlookMac.HasValue && detail.OutlookMac.Value) count++;
            if (detail.WordMac.HasValue && detail.WordMac.Value) count++;
            if (detail.ExcelMac.HasValue && detail.ExcelMac.Value) count++;
            if (detail.PowerPointMac.HasValue && detail.PowerPointMac.Value) count++;
            if (detail.OneNoteMac.HasValue && detail.OneNoteMac.Value) count++;
            if (detail.TeamsMac.HasValue && detail.TeamsMac.Value) count++;

            if (detail.OutlookMobile.HasValue && detail.OutlookMobile.Value) count++;
            if (detail.WordMobile.HasValue && detail.WordMobile.Value) count++;
            if (detail.ExcelMobile.HasValue && detail.ExcelMobile.Value) count++;
            if (detail.PowerPointMobile.HasValue && detail.PowerPointMobile.Value) count++;
            if (detail.OneNoteMobile.HasValue && detail.OneNoteMobile.Value) count++;
            if (detail.TeamsMobile.HasValue && detail.TeamsMobile.Value) count++;

            if (detail.OutlookWeb.HasValue && detail.OutlookWeb.Value) count++;
            if (detail.WordWeb.HasValue && detail.WordWeb.Value) count++;
            if (detail.ExcelWeb.HasValue && detail.ExcelWeb.Value) count++;
            if (detail.PowerPointWeb.HasValue && detail.PowerPointWeb.Value) count++;
            if (detail.OneNoteWeb.HasValue && detail.OneNoteWeb.Value) count++;
            if (detail.TeamsWeb.HasValue && detail.TeamsWeb.Value) count++;

            return count;
        }
        public override DbSet<AppPlatformUserActivityLog> GetTable(AnalyticsEntitiesContext context) => context.AppPlatformUserUsageLog;
    }
}
