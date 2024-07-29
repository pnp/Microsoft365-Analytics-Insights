using System;
using System.Text.Json;
using DataUtils;

namespace App.ControlPanel.Engine.SPO
{
    public class ModernAppCustomAction
    {

        public const string DESCRIPTION = "SPO Insights ModernUI AITracker App Customizer";
        public const string LOCATION = "ClientSideExtension.ApplicationCustomizer";
        public ModernAppCustomAction(string appInsightsConnectionString, string cacheToken, string insightsWebRootUrl)
        {
            var props = new ModernAppCustomActionProps
            {
                AppInsightsConnectionStringHash = appInsightsConnectionString.Base64Encode(),
                CacheToken = cacheToken,
                InsightsWebRootUrlHash = insightsWebRootUrl.Base64Encode()
            };
            this.ClientSideComponentProperties = JsonSerializer.Serialize(props);
        }

        public string Name { get; } = "AiTrackerModernApplicationCustomizer";
        public string Description { get; } = DESCRIPTION;
        public string Title { get; } = "AiTrackerModernApplicationCustomizer";
        public Guid ClientSideComponentId { get; } = Guid.Parse("a4e24884-9cfd-41ac-87af-747a47055f25");
        public string ClientSideComponentProperties { get; internal set; }
        public string Location { get; } = LOCATION;
    }
    public class ClassicPageCustomAction
    {
        public const string DESCRIPTION = "SPO Insights AITracker";
        public const string LOCATION = "ScriptLink";
        public ClassicPageCustomAction(string sourceFileFQDN, string appInsightsConnectionString, string solutionWebsiteBaseUrl)
        {
            this.ScriptBlock = "var headID = document.getElementsByTagName(\"head\")[0];" +
                "var newScript = document.createElement(\"script\");" +
                "newScript.type = \"text/javascript\";newScript.src=\"" + sourceFileFQDN +
                "\";headID.appendChild(newScript);" +
                $"var appInsightsConnectionStringHash = \"'{appInsightsConnectionString.Base64Encode()}'\";" +
                $"var insightsWebRootUrlHash = \"'{solutionWebsiteBaseUrl.Base64Encode()}'\";";
        }

        public string Name { get; set; }
        public string Description { get; } = DESCRIPTION;
        public string ScriptBlock { get; set; }
        public string Location { get; } = LOCATION;
    }

    public class ListInfo
    {
        public string ServerRelativeUrl { get; set; }
        public bool CreatedNew { get; set; }
        public bool EnableMinorVersions { get; set; }
    }

    public class TrackerInstallConfig
    {
        public TrackerInstallConfig(string appInsightsConnectionString, string docLibTitle, byte[] aiTrackerContents)
        {
            if (string.IsNullOrEmpty(appInsightsConnectionString))
            {
                throw new ArgumentException($"'{nameof(appInsightsConnectionString)}' cannot be null or empty.", nameof(appInsightsConnectionString));
            }

            if (string.IsNullOrEmpty(docLibTitle))
            {
                throw new ArgumentException($"'{nameof(docLibTitle)}' cannot be null or empty.", nameof(docLibTitle));
            }

            AppInsightsConnectionString = appInsightsConnectionString;
            DocLibTitle = docLibTitle;
            AiTrackerContents = aiTrackerContents ?? throw new ArgumentNullException(nameof(aiTrackerContents));
        }

        public string AppInsightsConnectionString { get; set; }
        public string DocLibTitle { get; set; }
        public string AITrackerTitle { get; } = "AITracker.js";

        public byte[] AiTrackerContents { get; set; } = null;
    }

    internal class ModernAppCustomActionProps
    {
        [System.Text.Json.Serialization.JsonPropertyName("appInsightsConnectionStringHash")]
        public string AppInsightsConnectionStringHash { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("cacheToken")]
        public string CacheToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("insightsWebRootUrlHash")]
        public string InsightsWebRootUrlHash { get; set; }
    }

}
