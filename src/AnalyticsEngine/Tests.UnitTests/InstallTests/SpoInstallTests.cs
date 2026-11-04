using App.ControlPanel.Engine.SPO;
using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Engine.SPO.Rest;
using App.ControlPanel.Engine.SPO.SiteTrackerInstaller;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class SpoInstallTests
    {
        const string ROOT = "https://contoso.sharepoint.com/sites/marketing";
        const string SUB_WEB_1 = ROOT + "/events";
        const string SUB_WEB_2 = ROOT + "/campaigns";
        const string WEB_APP = "https://contoso-analytics.azurewebsites.net";

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

                await installer.InstallWebComponentsToSite(c, "https://localhost");
                await installer.UninstallWebComponentsFromSite(c.DocLibTitle);
            }
        }

        [TestMethod]
        public async Task FakeSiteListInstallerTests()
        {
            var fakeInstaller = new FakeSiteListInstaller(_logger);

            var result = await fakeInstaller.InstallToSites(new string[] { "https://contoso.sharepoint.com", "https://contoso.sharepoint.com/sites/site2" },
                AnyExistingFile(), "1232", "SPOInsights", "https://localhost");

            Assert.IsTrue(result.AllSucceeded);
            Assert.AreEqual(2, result.SucceededSites.Count);
        }

        #region Custom actions on the root web and sub-webs

        // Each web of a tracked site collection carries one classic-page ScriptLink action and one modern-page
        // application customizer action. Removing "the old ones" before adding new ones used to always clear the ROOT
        // web, whichever web it was given: the sub-web loop then deleted the root web's freshly added actions, every
        // sub-web gained another pair on each run, and uninstalling left the sub-webs registered.

        [TestMethod]
        public async Task FirstInstall_RegistersOnePairOfActionsOnTheRootWebAndEverySubWeb()
        {
            var site = new FakeSiteInstallAdaptor(ROOT, SUB_WEB_1, SUB_WEB_2);

            await Install(site);

            AssertOnePairOfTrackerActionsOnEveryWeb(site);
        }

        [TestMethod]
        public async Task Reinstall_ReplacesEachWebsActionsRatherThanAddingAnotherPair()
        {
            var site = new FakeSiteInstallAdaptor(ROOT, SUB_WEB_1, SUB_WEB_2);

            await Install(site);
            await Install(site);
            await Install(site);

            AssertOnePairOfTrackerActionsOnEveryWeb(site);
        }

        [TestMethod]
        public async Task Uninstall_RemovesTheActionsFromTheRootWebAndEverySubWeb()
        {
            var site = new FakeSiteInstallAdaptor(ROOT, SUB_WEB_1, SUB_WEB_2);
            await Install(site);

            Assert.IsTrue(await new SiteAITrackerInstaller<FakeWeb>(site, _logger).UninstallWebComponentsFromSite("SPOInsights"));

            foreach (var web in new[] { ROOT, SUB_WEB_1, SUB_WEB_2 })
            {
                Assert.AreEqual(0, site.ActionsOn(web).Count, $"Uninstall left tracker actions on {web}");
            }
        }

        [TestMethod]
        public async Task SiteWithoutSubWebs_KeepsExactlyOnePairOnItsRootWeb_UntilUninstalled()
        {
            var site = new FakeSiteInstallAdaptor(ROOT);

            await Install(site);
            await Install(site);
            AssertOnePairOfTrackerActionsOnEveryWeb(site);

            await new SiteAITrackerInstaller<FakeWeb>(site, _logger).UninstallWebComponentsFromSite("SPOInsights");
            Assert.AreEqual(0, site.ActionsOn(ROOT).Count);
        }

        /// <summary>
        /// The adaptor's two "remove" methods each delete their own kind of action - they used to be swapped - and
        /// leave anything else on the web alone.
        /// </summary>
        [TestMethod]
        public async Task SpoAdaptor_EachRemoveMethodDeletesItsOwnKindOfAction_AndNothingElse()
        {
            const string CLASSIC_ID = "11111111-1111-1111-1111-111111111111";
            const string MODERN_ID = "22222222-2222-2222-2222-222222222222";
            const string OTHER_ID = "33333333-3333-3333-3333-333333333333";
            var actions = new List<JObject>
            {
                Action(CLASSIC_ID, ClassicPageCustomAction.DESCRIPTION, ClassicPageCustomAction.LOCATION),
                Action(MODERN_ID, ModernAppCustomAction.DESCRIPTION, ModernAppCustomAction.LOCATION),
                Action(OTHER_ID, "Contoso banner", ClassicPageCustomAction.LOCATION)
            };
            var deleted = new List<string>();

            var handler = new FakeHandler(request =>
            {
                var url = Uri.UnescapeDataString(request.RequestUri.ToString());
                if (url.StartsWith(ROOT + "/_api/web?"))
                    return Json("{\"Url\":\"" + ROOT + "\",\"Title\":\"Marketing\",\"ServerRelativeUrl\":\"/sites/marketing\"}");
                if (url.StartsWith(ROOT + "/_api/web/webs?"))
                    return Json("{\"value\":[]}");
                if (url.StartsWith(ROOT + "/_api/web/UserCustomActions?"))
                    return Json(new JObject { ["value"] = new JArray(actions.Select(a => a.DeepClone())) }.ToString());

                var delete = Regex.Match(url, @"^" + Regex.Escape(ROOT) + @"/_api/web/UserCustomActions\(guid'([^']+)'\)$");
                if (delete.Success && request.Headers.TryGetValues("X-HTTP-Method", out var method) && method.Single() == "DELETE")
                {
                    deleted.Add(delete.Groups[1].Value);
                    actions.RemoveAll(a => a["Id"].ToString() == delete.Groups[1].Value);
                    return Json(string.Empty);
                }
                Assert.Fail($"Unexpected request: {request.Method} {url}");
                return null;
            });

            using (var adaptor = new SpoSiteInstallAdaptor(ROOT, new FakeAuthenticator(), _logger, new HttpClient(handler)) { DeleteSettleDelay = TimeSpan.Zero })
            {
                Assert.IsTrue(await adaptor.Init());

                await adaptor.RemoveAITrackerCustomActionFromWeb(adaptor.RootWeb);
                CollectionAssert.AreEqual(new[] { CLASSIC_ID }, deleted, "RemoveAITrackerCustomActionFromWeb should delete the classic-page ScriptLink action");

                await adaptor.RemoveModernUIAITrackerCustomActionFromWeb(adaptor.RootWeb);
                CollectionAssert.AreEqual(new[] { CLASSIC_ID, MODERN_ID }, deleted, "RemoveModernUIAITrackerCustomActionFromWeb should delete the modern-page customizer action");
            }
            Assert.AreEqual(OTHER_ID, actions.Single()["Id"].ToString(), "Another solution's custom action must be left alone");
        }

        async Task Install(FakeSiteInstallAdaptor site)
        {
            var config = new TrackerInstallConfig("InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", new byte[] { 1 });
            Assert.IsTrue(await new SiteAITrackerInstaller<FakeWeb>(site, _logger).InstallWebComponentsToSite(config, WEB_APP));
        }

        static void AssertOnePairOfTrackerActionsOnEveryWeb(FakeSiteInstallAdaptor site)
        {
            foreach (var web in new[] { site.SiteUrl }.Concat(site.SubWebs.Select(w => w.Url)))
            {
                CollectionAssert.AreEquivalent(new[] { ClassicPageCustomAction.LOCATION, ModernAppCustomAction.LOCATION }, site.ActionsOn(web),
                    $"{web} should have exactly one classic-page action and one modern-page action");
            }
        }

        #endregion

        #region One site failing doesn't stop the others

        const string SITE_A = "https://contoso.sharepoint.com/sites/a";
        const string SITE_B = "https://contoso.sharepoint.com/sites/b";
        const string SITE_C = "https://contoso.sharepoint.com/sites/c";
        const string SITE_D = "https://contoso.sharepoint.com/sites/d";

        [TestMethod]
        public async Task InstallToSites_ASiteThatFails_IsReported_AndTheSitesAfterItAreStillInstalled()
        {
            var logger = new CapturingLogger();
            var sites = new FakeSiteListInstaller(logger);
            sites.Site(SITE_B).FailCustomActionChange = () => new SpoRestException(
                $"SharePoint returned 403 Forbidden for '{SITE_B}/_api/web/UserCustomActions'. The signed-in account needs to be a site collection administrator on this site.",
                HttpStatusCode.Forbidden);
            sites.Site(SITE_C).ValidSite = false;

            var result = await sites.InstallToSites(new[] { SITE_A, SITE_B, SITE_C, SITE_D }, AnyExistingFile(), "InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", WEB_APP);

            CollectionAssert.AreEqual(new[] { SITE_A, SITE_D }, result.SucceededSites);
            CollectionAssert.AreEqual(new[] { SITE_B, SITE_C }, result.FailedSites.Select(f => f.Key).ToList());
            StringAssert.Contains(result.FailedSites[0].Value, "403");
            Assert.IsFalse(result.AllSucceeded);
            AssertOnePairOfTrackerActionsOnEveryWeb(sites.Site(SITE_D));

            var errors = logger.Lines.Where(l => l.Level == LogLevel.Error).Select(l => l.Message).ToList();
            Assert.IsTrue(errors.Any(e => e.Contains(SITE_B) && e.Contains("403 Forbidden")), "The failed site and SharePoint's reason should be logged as an error");
            Assert.IsTrue(errors.Any(e => e.Contains("2 of 4") && e.Contains(SITE_B) && e.Contains(SITE_C) && !e.Contains(SITE_A)),
                "The run should end with an error that names every site the tracker was not installed on");
        }

        [TestMethod]
        public async Task InstallToSites_WhenEverySiteSucceeds_ReportsNoErrors()
        {
            var logger = new CapturingLogger();
            var sites = new FakeSiteListInstaller(logger);

            var result = await sites.InstallToSites(new[] { SITE_A, SITE_B }, AnyExistingFile(), "InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", WEB_APP);

            Assert.IsTrue(result.AllSucceeded);
            CollectionAssert.AreEqual(new[] { SITE_A, SITE_B }, result.SucceededSites);
            Assert.IsFalse(logger.Lines.Any(l => l.Level >= LogLevel.Warning), string.Join(Environment.NewLine, logger.Lines.Select(l => l.Message)));
        }

        [TestMethod]
        public async Task InstallToSites_AFailedSignIn_StillEndsTheWholeRun()
        {
            var sites = new FakeSiteListInstaller(new CapturingLogger());
            sites.Site(SITE_A).FailCustomActionChange = () => new SpoAuthenticationException("SharePoint sign-in was cancelled.", null);

            await Assert.ThrowsExceptionAsync<SpoAuthenticationException>(() =>
                sites.InstallToSites(new[] { SITE_A, SITE_B }, AnyExistingFile(), "InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", WEB_APP));

            Assert.AreEqual(0, sites.Site(SITE_B).ActionsOn(SITE_B).Count, "No site should be tried after the sign-in failed");
        }

        [TestMethod]
        public async Task InstallToSites_ACancel_StillEndsTheWholeRun()
        {
            using (var cts = new CancellationTokenSource())
            {
                var sites = new FakeSiteListInstaller(new CapturingLogger()) { CancellationToken = cts.Token };
                sites.Site(SITE_A).FailCustomActionChange = () =>
                {
                    cts.Cancel();
                    return new OperationCanceledException(cts.Token);
                };

                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    sites.InstallToSites(new[] { SITE_A, SITE_B }, AnyExistingFile(), "InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", WEB_APP));

                Assert.AreEqual(0, sites.Site(SITE_B).ActionsOn(SITE_B).Count, "No site should be tried after a cancel");
            }
        }

        [TestMethod]
        public async Task UninstallFromSites_ASiteThatFails_IsReported_AndTheSitesAfterItAreStillCleanedUp()
        {
            var logger = new CapturingLogger();
            var sites = new FakeSiteListInstaller(logger);
            await sites.InstallToSites(new[] { SITE_A, SITE_B }, AnyExistingFile(), "InstrumentationKey=00000000-0000-0000-0000-000000000000", "SPOInsights", WEB_APP);
            sites.Site(SITE_A).FailCustomActionChange = () => new SpoRestException("SharePoint returned 500 Internal Server Error.", HttpStatusCode.InternalServerError);

            var result = await sites.UninstallFromSites(new[] { SITE_A, SITE_B }, "SPOInsights");

            CollectionAssert.AreEqual(new[] { SITE_B }, result.SucceededSites);
            Assert.AreEqual(SITE_A, result.FailedSites.Single().Key);
            Assert.AreEqual(0, sites.Site(SITE_B).ActionsOn(SITE_B).Count);
            Assert.IsTrue(logger.Lines.Any(l => l.Level == LogLevel.Error && l.Message.Contains("1 of 2") && l.Message.Contains(SITE_A)));
        }

        #endregion

        public async Task SPOSiteInstallAdaptorTests()
        {
            const string URL_SP = "https://contoso.sharepoint.com/sites/ProjectFalcon-UXtest";
            const string URL_WEB_APP = "https://localhost:44307";
            using (var authenticator = new InteractiveSpoAuthenticator(clientId: null, tenantId: null, logger: _logger))
            using (var adaptor = new SpoSiteInstallAdaptor(URL_SP, authenticator, _logger))
            {
                var c = new TrackerInstallConfig("1232", "SPOInsights", System.Text.Encoding.UTF8.GetBytes("AITrackerUpload"));
                var installer = new SiteAITrackerInstaller<SpoWeb>(adaptor, _logger);

                await installer.InstallWebComponentsToSite(c, URL_WEB_APP);
                await installer.InstallWebComponentsToSite(c, URL_WEB_APP);   // Overwrite
                await installer.UninstallWebComponentsFromSite(c.DocLibTitle);
            }
        }

        /// <summary>The list installer reads the AITracker file before it starts; the fakes don't care what's in it.</summary>
        static FileInfo AnyExistingFile() => new FileInfo(typeof(SpoInstallTests).Assembly.Location);

        static JObject Action(string id, string description, string location) =>
            new JObject { ["Id"] = id, ["Description"] = description, ["Location"] = location };

        static HttpResponseMessage Json(string body) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

        class FakeWeb { public string Url { get; set; } }

        /// <summary>
        /// A site collection held in memory: its webs, and the custom actions registered on each web (by location, in
        /// the order they were added).
        /// </summary>
        class FakeSiteInstallAdaptor : ISiteInstallAdaptor<FakeWeb>
        {
            readonly List<string> _subWebUrls;
            readonly Dictionary<string, List<string>> _actionsByWeb = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            public FakeSiteInstallAdaptor() : this("http://unittesting", "http://unittesting/sub1", "http://unittesting/sub2")
            {
            }

            public FakeSiteInstallAdaptor(string siteUrl, params string[] subWebUrls)
            {
                SiteUrl = siteUrl;
                _subWebUrls = subWebUrls.ToList();
            }

            /// <summary>False makes the site look like one the signed-in account can't open.</summary>
            public bool ValidSite { get; set; } = true;

            /// <summary>When set, adding or removing a custom action throws what this returns.</summary>
            public Func<Exception> FailCustomActionChange { get; set; }

            public List<string> ActionsOn(string webUrl) => _actionsByWeb.TryGetValue(webUrl, out var actions) ? actions : new List<string>();

            public Task<bool> Init()
            {
                return Task.FromResult(ValidSite);
            }

            public List<FakeWeb> SubWebs => _subWebUrls.Select(url => new FakeWeb { Url = url }).ToList();

            public FakeWeb RootWeb => new FakeWeb { Url = SiteUrl };

            public string SiteUrl { get; }

            public Task AddAITrackerCustomActionToWeb(FakeWeb w, ClassicPageCustomAction classicPageCustomAction)
            {
                Change(w, list => list.Add(classicPageCustomAction.Location));
                return Task.CompletedTask;
            }

            public Task AddModernUIAITrackerCustomActionToWeb(FakeWeb w, ModernAppCustomAction modernAppCustomAction)
            {
                Change(w, list => list.Add(modernAppCustomAction.Location));
                return Task.CompletedTask;
            }

            public Task RemoveAITrackerCustomActionFromWeb(FakeWeb web)
            {
                Change(web, list => list.RemoveAll(a => a == ClassicPageCustomAction.LOCATION));
                return Task.CompletedTask;
            }

            public Task RemoveModernUIAITrackerCustomActionFromWeb(FakeWeb web)
            {
                Change(web, list => list.RemoveAll(a => a == ModernAppCustomAction.LOCATION));
                return Task.CompletedTask;
            }

            void Change(FakeWeb web, Action<List<string>> change)
            {
                var failure = FailCustomActionChange?.Invoke();
                if (failure != null) throw failure;

                if (!_actionsByWeb.TryGetValue(web.Url, out var actions))
                {
                    actions = new List<string>();
                    _actionsByWeb[web.Url] = actions;
                }
                change(actions);
            }

            public Task AddTrackerToLibraryOnRootSite(string listTitle, byte[] aiTrackerContents, bool publish)
            {
                return Task.CompletedTask;
            }

            public Task<ListInfo> ConfirmDocLibOnRootSite(string listTitle)
            {
                return Task.FromResult<ListInfo>(new ListInfo { CreatedNew = true, EnableMinorVersions = false, SiteRelativeUrl = listTitle });
            }

            public string GetUrl(FakeWeb web)
            {
                return web.Url;
            }

            public Task<bool> RemoveTrackerIfExistsOnRootSite(string listTitle)
            {
                return Task.FromResult(false);
            }

            public Task SecureList(string listTitle)
            {
                return Task.CompletedTask;
            }

            public void Dispose() { }

            public Task RemoveDocLibOnRootSite(string listTitle)
            {
                return Task.CompletedTask;
            }
        }

        /// <summary>Hands out one in-memory site per URL, so a site keeps its state from one run to the next.</summary>
        class FakeSiteListInstaller : AbstractSiteListInstaller<FakeWeb>
        {
            readonly Dictionary<string, FakeSiteInstallAdaptor> _sites = new Dictionary<string, FakeSiteInstallAdaptor>(StringComparer.OrdinalIgnoreCase);

            public FakeSiteListInstaller(ILogger logger)
                : base(logger)
            {
            }

            public FakeSiteInstallAdaptor Site(string siteUrl)
            {
                if (!_sites.TryGetValue(siteUrl, out var site))
                {
                    site = new FakeSiteInstallAdaptor(siteUrl);
                    _sites[siteUrl] = site;
                }
                return site;
            }

            public override ISiteInstallAdaptor<FakeWeb> GetAdaptor(string siteUrl)
            {
                return Site(siteUrl);
            }
        }

        class FakeAuthenticator : ISpoAuthenticator
        {
            public Task<string> GetAccessTokenAsync(string siteUrl) => Task.FromResult("fake-token");
            public void Dispose() { }
        }

        class FakeHandler : HttpMessageHandler
        {
            readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_responder(request));
            }
        }
    }

}
