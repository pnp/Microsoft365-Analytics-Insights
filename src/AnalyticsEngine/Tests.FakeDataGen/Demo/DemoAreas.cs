using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.FakeDataGen.Demo
{
    [Flags]
    internal enum DemoArea
    {
        Directory = 1,
        Copilot = 2,
        CopilotHistory = 4,
        Teams = 8,
        Outlook = 16,
        SentEmail = 32,
        SharePoint = 64,
        Web = 128,
        OneDrive = 256,
        Engage = 512,
        Office = 1024,
        PowerApps = 2048,
        PowerAutomate = 4096,
        PowerBI = 8192,
        CopilotStudio = 16384,
        Dlp = 32768,
        All = 65535
    }

    internal sealed class DemoAreaDescription
    {
        public DemoArea Area { get; }
        public string Key { get; }
        public string Title { get; }

        public DemoAreaDescription(DemoArea area, string key, string title)
        {
            Area = area; Key = key; Title = title;
        }
    }

    internal static class DemoAreas
    {
        public const DemoArea DailyWorkloads = DemoArea.Teams | DemoArea.Outlook | DemoArea.SharePoint
            | DemoArea.OneDrive | DemoArea.Engage | DemoArea.Office;

        public static readonly IReadOnlyList<DemoAreaDescription> Catalogue = new[]
        {
            new DemoAreaDescription(DemoArea.Directory, "directory", "Directory, demographics and current licences"),
            new DemoAreaDescription(DemoArea.Copilot, "copilot", "Copilot audit activity and official usage reports"),
            new DemoAreaDescription(DemoArea.CopilotHistory, "copilot-history", "Copilot interaction history (metadata only)"),
            new DemoAreaDescription(DemoArea.Teams, "teams", "Teams usage, calls and channel activity"),
            new DemoAreaDescription(DemoArea.Outlook, "outlook", "Outlook daily usage"),
            new DemoAreaDescription(DemoArea.SentEmail, "sent-email", "Sent email and synthetic sentiment"),
            new DemoAreaDescription(DemoArea.SharePoint, "sharepoint", "SharePoint audit and daily usage"),
            new DemoAreaDescription(DemoArea.Web, "web", "Web traffic, pages, comments, clicks and search"),
            new DemoAreaDescription(DemoArea.OneDrive, "onedrive", "OneDrive daily usage and storage"),
            new DemoAreaDescription(DemoArea.Engage, "engage", "Viva Engage user/group activity and devices"),
            new DemoAreaDescription(DemoArea.Office, "office", "Office application and device usage"),
            new DemoAreaDescription(DemoArea.PowerApps, "powerapps", "Power Apps usage, sharing and connectors"),
            new DemoAreaDescription(DemoArea.PowerAutomate, "powerautomate", "Power Automate activity, sharing and connectors"),
            new DemoAreaDescription(DemoArea.PowerBI, "powerbi", "Power BI report usage"),
            new DemoAreaDescription(DemoArea.CopilotStudio, "copilot-studio", "Copilot Studio bot activity"),
            new DemoAreaDescription(DemoArea.Dlp, "dlp", "DLP policy matches and Copilot impact")
        };

        public static DemoArea Parse(string value)
        {
            if (string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)) return DemoArea.All;
            var selected = (DemoArea)0;
            foreach (var key in value.Split(','))
            {
                var area = Catalogue.SingleOrDefault(a => a.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase));
                if (area == null || (selected & area.Area) != 0)
                    throw new ArgumentException("Unknown or duplicate activity area: " + key
                        + ". Use all or " + string.Join(",", Catalogue.Select(a => a.Key)) + ".");
                selected |= area.Area;
            }
            return selected;
        }

        public static string Format(DemoArea areas) => areas == DemoArea.All ? "all"
            : string.Join(",", Catalogue.Where(a => (areas & a.Area) != 0).Select(a => a.Key));
    }
}
