using Common.Entities.Config;
using System.Configuration;
using System.Web;

namespace Tests.UnitTests
{
    public class TestsAppConfig : AppConfig
    {
        public TestsAppConfig()
        {
            this.TestCopilotDocContextIdSpSite = ConfigurationManager.AppSettings.Get("TestCopilotDocContextIdSpSite");
            this.TestCopilotDocContextIdMySites = ConfigurationManager.AppSettings.Get("TestCopilotDocContextIdMySites");
            this.TestCopilotDocContextIdMySites = ConfigurationManager.AppSettings.Get("TestCopilotDocContextIdMySites");
            this.TestCopilotEventUPN = ConfigurationManager.AppSettings.Get("TestCopilotEventUPN");
            this.TestCallThreadId = ConfigurationManager.AppSettings.Get("TestCallThreadId");
            this.TeamSiteFileExtension = ConfigurationManager.AppSettings.Get("TeamSiteFileExtension");
            this.TeamSitesFileName = ConfigurationManager.AppSettings.Get("TeamSitesFileName");
            this.MySitesFileExtension = ConfigurationManager.AppSettings.Get("MySitesFileExtension");
            this.MySitesFileName = ConfigurationManager.AppSettings.Get("MySitesFileName");
            this.MySitesFileUrl = ConfigurationManager.AppSettings.Get("MySitesFileUrl");
            this.TeamSiteFileUrl = ConfigurationManager.AppSettings.Get("TeamSiteFileUrl");

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
