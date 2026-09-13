using Common.Entities.Config;
using System.Configuration;
using System.Web;

namespace Tests.UnitTests
{
    public class TestsAppConfig : AppConfig
    {
        public TestsAppConfig()
        {
            this.TestCopilotDocContextIdSpSite = AnalyticsConfig.AppSettings.Get("TestCopilotDocContextIdSpSite");
            this.TestCopilotDocContextIdMySites = AnalyticsConfig.AppSettings.Get("TestCopilotDocContextIdMySites");
            this.TestCopilotDocContextIdMySites = AnalyticsConfig.AppSettings.Get("TestCopilotDocContextIdMySites");
            this.TestCopilotEventUPN = AnalyticsConfig.AppSettings.Get("TestCopilotEventUPN");
            this.TestCallThreadId = AnalyticsConfig.AppSettings.Get("TestCallThreadId");
            this.TeamSiteFileExtension = AnalyticsConfig.AppSettings.Get("TeamSiteFileExtension");
            this.TeamSitesFileName = AnalyticsConfig.AppSettings.Get("TeamSitesFileName");
            this.MySitesFileExtension = AnalyticsConfig.AppSettings.Get("MySitesFileExtension");
            this.MySitesFileName = AnalyticsConfig.AppSettings.Get("MySitesFileName");
            this.MySitesFileUrl = AnalyticsConfig.AppSettings.Get("MySitesFileUrl");
            this.TeamSiteFileUrl = AnalyticsConfig.AppSettings.Get("TeamSiteFileUrl");

            if (!string.IsNullOrEmpty(this.TestCopilotDocContextIdMySites))
            {
                TestCopilotDocContextIdMySites = HttpUtility.UrlDecode(this.TestCopilotDocContextIdMySites);
            }
            if (!string.IsNullOrEmpty(this.TestCopilotDocContextIdSpSite))
            {
                TestCopilotDocContextIdSpSite = HttpUtility.UrlDecode(this.TestCopilotDocContextIdSpSite);
            }
        }

        public string TestCopilotDocContextIdSpSite { get; set; }
        public string TestCopilotDocContextIdMySites { get; set; }
        public string TestCopilotEventUPN { get; set; }
        public string TestCallThreadId { get; set; }
        public string TeamSiteFileExtension { get; set; }
        public string TeamSitesFileName { get; set; }
        public string MySitesFileExtension { get; set; }
        public string MySitesFileName { get; set; }
        public string MySitesFileUrl { get; set; }

        public string TeamSiteFileUrl { get; set; }
    }
}
