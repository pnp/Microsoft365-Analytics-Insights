using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>
    /// The metric groups of the Activity analysis page, in the order the page offers them.
    /// </summary>
    /// <remarks>Stable keys: an API contract with the portal, which translates them. Never rename one.</remarks>
    public static class ActivityAnalysisCategories
    {
        public const string Teams = "teams";
        public const string Outlook = "outlook";
        public const string OneDrive = "onedrive";
        public const string SharePoint = "sharepoint";
        public const string Copilot = "copilot";
        public const string VivaEngage = "vivaEngage";

        public static readonly IReadOnlyList<string> All = new[] { Teams, Outlook, OneDrive, SharePoint, Copilot, VivaEngage };
    }

    /// <summary>What a metric counts. Durations are always reported in raw seconds; the portal shows hours.</summary>
    public static class ActivityAnalysisUnits
    {
        public const string Count = "count";
        public const string Seconds = "seconds";
    }

    /// <summary>One column of <c>profiling.ActivitiesWeeklyColumns</c>, as the Activity analysis page offers it.</summary>
    public sealed class ActivityAnalysisMetric
    {
        internal ActivityAnalysisMetric(int index, string key, string column, string category, string unit, bool core)
        {
            Index = index;
            Key = key;
            Column = column;
            Category = category;
            Unit = unit;
            Core = core;
        }

        /// <summary>The metric's position in <see cref="ActivityAnalysisMetricCatalogue.All"/>, and its slot in every per-metric array.</summary>
        public int Index { get; }

        /// <summary>The stable key the API and the portal use. Never renamed.</summary>
        public string Key { get; }

        /// <summary>The exact column name in <c>profiling.ActivitiesWeeklyColumns</c>.</summary>
        public string Column { get; }

        public string Category { get; }

        public string Unit { get; }

        /// <summary>Offered in the filter panel's activity ranges by default.</summary>
        public bool Core { get; }

        /// <summary>
        /// English fallback only - the portal labels every metric it knows in the reader's language from <see cref="Key"/>.
        /// The column name is what the Power BI report shows, so it is what an administrator will recognise.
        /// </summary>
        public string Label => Column;

        /// <summary>The column as a bracket-quoted SQL identifier. Built from the catalogue's constants, never from input.</summary>
        internal string SqlColumn => "[" + Column.Replace("]", "]]") + "]";
    }

    /// <summary>
    /// Every metric the Activity analysis page can show - the single source of truth the portal mirrors.
    /// </summary>
    /// <remarks>
    /// <para>The keys are an API contract (the portal translates <c>activityAnalysis.metric.&lt;key&gt;</c>), and each
    /// column is the exact name the profiling runbooks write (<c>Profiling-03-CreateSchema.sql</c>). The page reads
    /// <c>profiling.ActivitiesWeeklyColumns</c> only - never the per-metric rows of <c>profiling.ActivitiesWeekly</c>,
    /// which hold the same figures one row per user, week and metric (zeros included).</para>
    /// <para>A column added by a later profiling script version is missing on an older install; the server reads
    /// <c>sys.columns</c> on every load and reports such a metric as unavailable rather than failing the query.</para>
    /// </remarks>
    public static class ActivityAnalysisMetricCatalogue
    {
        public static readonly IReadOnlyList<ActivityAnalysisMetric> All = Build(new[]
        {
            Define("teams.privateChats", "Teams Private Chats", ActivityAnalysisCategories.Teams, true),
            Define("teams.teamChats", "Teams Team Chats", ActivityAnalysisCategories.Teams, true),
            Define("teams.calls", "Teams Calls", ActivityAnalysisCategories.Teams, true),
            Define("teams.meetings", "Teams Meetings", ActivityAnalysisCategories.Teams, true),
            Define("teams.meetingsAttended", "Teams Meetings Attended", ActivityAnalysisCategories.Teams, true),
            Define("teams.meetingsOrganized", "Teams Meetings Organized", ActivityAnalysisCategories.Teams, true),
            Define("teams.adhocMeetingsAttended", "Teams Adhoc Meetings Attended", ActivityAnalysisCategories.Teams, false),
            Define("teams.adhocMeetingsOrganized", "Teams Adhoc Meetings Organized", ActivityAnalysisCategories.Teams, false),
            Define("teams.scheduledOnetimeMeetingsAttended", "Teams Scheduled Onetime Meetings Attended", ActivityAnalysisCategories.Teams, false),
            Define("teams.scheduledOnetimeMeetingsOrganized", "Teams Scheduled Onetime Meetings Organized", ActivityAnalysisCategories.Teams, false),
            Define("teams.scheduledRecurringMeetingsAttended", "Teams Scheduled Recurring Meetings Attended", ActivityAnalysisCategories.Teams, false),
            Define("teams.scheduledRecurringMeetingsOrganized", "Teams Scheduled Recurring Meetings Organized", ActivityAnalysisCategories.Teams, false),
            Define("teams.audioDuration", "Teams Audio Duration Seconds", ActivityAnalysisCategories.Teams, false, ActivityAnalysisUnits.Seconds),
            Define("teams.videoDuration", "Teams Video Duration Seconds", ActivityAnalysisCategories.Teams, false, ActivityAnalysisUnits.Seconds),
            Define("teams.screenshareDuration", "Teams Screenshare Duration Seconds", ActivityAnalysisCategories.Teams, false, ActivityAnalysisUnits.Seconds),
            Define("teams.postMessages", "Teams Post Messages", ActivityAnalysisCategories.Teams, false),
            Define("teams.replyMessages", "Teams Reply Messages", ActivityAnalysisCategories.Teams, false),
            Define("teams.urgentMessages", "Teams Urgent Messages", ActivityAnalysisCategories.Teams, false),
            Define("outlook.emailsSent", "Emails Sent", ActivityAnalysisCategories.Outlook, true),
            Define("outlook.emailsReceived", "Emails Received", ActivityAnalysisCategories.Outlook, true),
            Define("outlook.emailsRead", "Emails Read", ActivityAnalysisCategories.Outlook, true),
            Define("outlook.meetingsCreated", "Outlook Meetings Created", ActivityAnalysisCategories.Outlook, true),
            Define("outlook.meetingsInteracted", "Outlook Meetings Interacted", ActivityAnalysisCategories.Outlook, true),
            Define("onedrive.viewedEdited", "OneDrive Viewed/Edited", ActivityAnalysisCategories.OneDrive, true),
            Define("onedrive.synced", "OneDrive Synced", ActivityAnalysisCategories.OneDrive, true),
            Define("onedrive.sharedInternally", "OneDrive Shared Internally", ActivityAnalysisCategories.OneDrive, true),
            Define("onedrive.sharedExternally", "OneDrive Shared Externally", ActivityAnalysisCategories.OneDrive, true),
            Define("sharepoint.viewedEdited", "SPO Viewed/Edited", ActivityAnalysisCategories.SharePoint, true),
            Define("sharepoint.synced", "SPO Synced", ActivityAnalysisCategories.SharePoint, true),
            Define("sharepoint.sharedInternally", "SPO Shared Internally", ActivityAnalysisCategories.SharePoint, true),
            Define("sharepoint.sharedExternally", "SPO Shared Externally", ActivityAnalysisCategories.SharePoint, true),
            Define("vivaEngage.posted", "Yammer Posted", ActivityAnalysisCategories.VivaEngage, true),
            Define("vivaEngage.read", "Yammer Read", ActivityAnalysisCategories.VivaEngage, true),
            Define("vivaEngage.liked", "Yammer Liked", ActivityAnalysisCategories.VivaEngage, true),
            Define("copilot.chats", "Copilot Chats", ActivityAnalysisCategories.Copilot, true),
            Define("copilot.meetings", "Copilot Meetings", ActivityAnalysisCategories.Copilot, true),
            Define("copilot.files", "Copilot Files", ActivityAnalysisCategories.Copilot, true),
            Define("copilot.app.assist365", "Copilot App Assist365", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.bing", "Copilot App Bing", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.bashTool", "Copilot App BashTool", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.devUi", "Copilot App DevUI", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.excel", "Copilot App Excel", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.loop", "Copilot App Loop", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.m365AdminCenter", "Copilot App M365AdminCenter", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.m365App", "Copilot App M365App", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.office", "Copilot App Office", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.oneNote", "Copilot App OneNote", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.outlook", "Copilot App Outlook", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.planner", "Copilot App Planner", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.powerPoint", "Copilot App PowerPoint", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.sharePoint", "Copilot App SharePoint", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.stream", "Copilot App Stream", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.teams", "Copilot App Teams", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.vivaCopilot", "Copilot App VivaCopilot", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.vivaEngage", "Copilot App VivaEngage", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.vivaGoals", "Copilot App VivaGoals", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.whiteboard", "Copilot App Whiteboard", ActivityAnalysisCategories.Copilot, false),
            Define("copilot.app.word", "Copilot App Word", ActivityAnalysisCategories.Copilot, false),
        });

        /// <summary>What the page selects before the reader chooses: the core Teams metrics.</summary>
        public static readonly IReadOnlyList<string> DefaultSelection = All
            .Where(m => m.Category == ActivityAnalysisCategories.Teams && m.Core)
            .Select(m => m.Key)
            .ToList()
            .AsReadOnly();

        private static readonly Dictionary<string, ActivityAnalysisMetric> ByKey =
            All.ToDictionary(m => m.Key, StringComparer.Ordinal);

        private static readonly Dictionary<string, ActivityAnalysisMetric> ByColumn =
            All.ToDictionary(m => m.Column, StringComparer.OrdinalIgnoreCase);

        public static int Count => All.Count;

        /// <summary>A metric by its key, compared exactly: the keys are a contract, not free text.</summary>
        public static bool TryGet(string key, out ActivityAnalysisMetric metric)
        {
            metric = null;
            return key != null && ByKey.TryGetValue(key, out metric);
        }

        /// <summary>
        /// The metric stored in a column, compared the way SQL Server's default collation compares column names.
        /// </summary>
        public static bool TryGetByColumn(string column, out ActivityAnalysisMetric metric)
        {
            metric = null;
            return column != null && ByColumn.TryGetValue(column, out metric);
        }

        private static ActivityAnalysisMetric[] Build(Definition[] definitions)
        {
            return definitions
                .Select((d, i) => new ActivityAnalysisMetric(i, d.Key, d.Column, d.Category, d.Unit, d.Core))
                .ToArray();
        }

        private static Definition Define(string key, string column, string category, bool core, string unit = ActivityAnalysisUnits.Count)
        {
            return new Definition { Key = key, Column = column, Category = category, Core = core, Unit = unit };
        }

        private sealed class Definition
        {
            public string Key;
            public string Column;
            public string Category;
            public string Unit;
            public bool Core;
        }
    }
}
