using App.ControlPanel.Engine.SPO;
using App.ControlPanel.Engine.SPO.SiteTrackerInstaller;
using Microsoft.Extensions.Logging;
using Microsoft.SharePoint.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class SpoInstallTests
    {
        ILogger _logger;
        public SpoInstallTests()
        {
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());

            _logger = loggerFactory.CreateLogger("");
        }

        [TestMethod]
        public async Task FakeSiteInstallAdaptorTests()
        {
            using (var adaptor = new FakeSiteInstallAdaptor())
            {
                Assert.ThrowsException<ArgumentException>(() => new TrackerInstallConfig("", "", null));
                var c = new TrackerInstallConfig("1232", "SPOInsights", new byte[] { byte.MaxValue });
                var installer = new SiteAITrackerInstaller<FakeWeb>(adaptor, _logger);

                await installer.InstallWebComponentsToSite(c, null);
                await installer.UninstallWebComponentsFromSite(c.DocLibTitle);
            }
        }

        [TestMethod]
        public async Task FakeSiteListInstallerTests()
        {
            var tempFile = new FileInfo(System.IO.Path.GetTempFileName());
            System.IO.File.WriteAllText(tempFile.FullName, "AITracker contents");
            var fakeInstaller = new FakeSiteListInstaller(_logger);

            await fakeInstaller.InstallToSites(new string[] { "https://contoso.sharepoint.com", "https://contoso.sharepoint.com/sites/site2" }, tempFile, "1232", "SPOInsights", null);

            try
            {
                System.IO.File.Delete(tempFile.FullName);
            }
            catch (IOException)
            {
                // Whatevs
            }
        }

#if DEBUG
        [TestMethod]
#endif
        public async Task SPOSiteInstallAdaptorTests()
        {
            const string URL_SP = "https://moderncomms933270.sharepoint.com/sites/ProjectFalcon-UXtest";
            const string URL_WEB_APP = "https://localhost:44307";
            using (var adaptor = new SpoSiteInstallAdaptor(URL_SP, _logger))
            {
                var c = new TrackerInstallConfig("1232", "SPOInsights", System.Text.Encoding.UTF8.GetBytes("AITrackerUpload"));
                var installer = new SiteAITrackerInstaller<Web>(adaptor, _logger);

                await installer.InstallWebComponentsToSite(c, URL_WEB_APP);
                await installer.InstallWebComponentsToSite(c, URL_WEB_APP);   // Overwrite
                await installer.UninstallWebComponentsFromSite(c.DocLibTitle);
            }
        }

        class FakeWeb { public string Url { get; set; } }

        class FakeSiteInstallAdaptor : ISiteInstallAdaptor<FakeWeb>
        {
            public Task<bool> Init()
            {
                return Task.FromResult(true);
            }

            public List<FakeWeb> SubWebs => new List<FakeWeb>() { new FakeWeb { Url = "http://unittesting/sub1" }, new FakeWeb { Url = "http://unittesting/sub2" } };

            public FakeWeb RootWeb => new FakeWeb { Url = SiteUrl };

            public string SiteUrl => "http://unittesting";

            public Task AddAITrackerCustomActionToWeb(FakeWeb w, ClassicPageCustomAction classicPageCustomAction)
            {
                Console.WriteLine($"Adding custom action to fake web {w.Url}");
                return Task.CompletedTask;
            }

            public Task AddModernUIAITrackerCustomActionToWeb(FakeWeb w, ModernAppCustomAction modernAppCustomAction)
            {
                Console.WriteLine($"Adding modern UI custom action to fake web {w.Url}");
                return Task.CompletedTask;
            }

            public Task AddTrackerToLibraryOnRootSite(string listTitle, byte[] aiTrackerContents, bool publish)
            {
                Console.WriteLine($"Adding AITracker to fake library {listTitle}");
                return Task.CompletedTask;
            }

            public Task<ListInfo> ConfirmDocLibOnRootSite(string listTitle)
            {
                Console.WriteLine($"Confirmed fake library {listTitle} exists");
                return Task.FromResult<ListInfo>(new ListInfo { CreatedNew = true, EnableMinorVersions = false, ServerRelativeUrl = listTitle });
            }

            public string GetUrl(FakeWeb web)
            {
                return web.Url;
            }

            public Task<bool> RemoveTrackerIfExistsOnRootSite(string listTitle)
            {
                Console.WriteLine($"Removed AITracker to fake library {listTitle}");
                return Task.FromResult(false);
            }

            public Task SecureList(string listTitle)
            {
                Console.WriteLine($"Secured AITracker");
                return Task.CompletedTask;
            }
            public void Dispose() { }

            public Task RemoveDocLibOnRootSite(string listTitle)
            {
                return Task.CompletedTask;
            }

            public Task RemoveAITrackerCustomActionFromWeb(FakeWeb web)
            {
                return Task.CompletedTask;
            }

            public Task RemoveModernUIAITrackerCustomActionFromWeb(FakeWeb web)
            {
                return Task.CompletedTask;
            }
        }

        class FakeSiteListInstaller : AbstractSiteListInstaller<FakeWeb>
        {
            public FakeSiteListInstaller(ILogger logger)
                : base(logger)
            {
            }

            public override ISiteInstallAdaptor<FakeWeb> GetAdaptor(string siteUrl)
            {
                return new FakeSiteInstallAdaptor();
            }
        }
    }

}
