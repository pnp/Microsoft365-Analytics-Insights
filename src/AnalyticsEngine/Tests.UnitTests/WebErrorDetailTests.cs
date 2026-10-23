extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities;
using Common.Entities.AgentCosts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Web.Http;
using System.Xml.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// A failure must not hand a remote caller the exception: its type, message and stack trace name servers, tables
    /// and code paths. The site shipped with <c>customErrors</c> Off and Web API's default error policy, which follows
    /// it - so any anonymous request that could make a controller throw got the lot.
    /// </summary>
    [TestClass]
    public class WebErrorDetailTests
    {
        private const string XdtNamespace = "http://schemas.microsoft.com/XML-Document-Transform";

        [TestMethod]
        public void WebApi_SendsExceptionDetailOnlyToARequestFromThisMachine()
        {
            using (var config = new HttpConfiguration())
            {
                WebApiConfig.Register(config);

                // Default would defer to <customErrors>, so whether a stack trace leaves the server would depend on
                // a web.config transform rather than on this code.
                Assert.AreEqual(IncludeErrorDetailPolicy.LocalOnly, config.IncludeErrorDetailPolicy);
            }
        }

        [TestMethod]
        public void ReleaseBuild_TurnsCustomErrorsOnForRemoteCallers()
        {
            var template = XDocument.Load(WebProjectFile("Web.Template.config"));
            var release = XDocument.Load(WebProjectFile("Web.Release.config"));

            Assert.AreEqual(1, template.Root.Elements("system.web").Elements("customErrors").Count(),
                "The Release transform below edits the template's one <customErrors>; it must still exist.");

            var transform = release.Root.Elements("system.web").Elements("customErrors").SingleOrDefault();
            Assert.IsNotNull(transform, "Web.Release.config must set <customErrors>: the template's is Off.");

            var mode = (string)transform.Attribute("mode");
            CollectionAssert.Contains(new[] { "RemoteOnly", "On" }, mode, "Off sends the exception page to every browser.");

            var how = (string)transform.Attribute(XName.Get("Transform", XdtNamespace)) ?? string.Empty;
            Assert.IsTrue(how.StartsWith("SetAttributes", StringComparison.Ordinal) || how == "Replace",
                $"'{how}' would not change the template's mode.");
        }

        [TestMethod]
        public async Task AgentCosts_UnexpectedFailure_SendsItsCode_ButNotTheExceptionMessage()
        {
            var store = new ThrowingAgentCostStore("synthetic private detail: Server=contoso-sql.database.windows.net");
            using (var host = new PortalTestHost(
                new[] { typeof(AgentCostsAPIController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.NotEnforcing,
                _ => new AgentCostsAPIController(store, () => new ImportTaskSettings())))
            {
                var response = await host.Client.GetAsync("api/AgentCosts/summary");
                var body = await response.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.AreEqual("agentCostsLoadFailed", (string)JObject.Parse(body)["code"]);
                Assert.IsFalse(body.Contains("synthetic private detail"), body);
                Assert.IsFalse(body.Contains("contoso-sql"), body);
            }
        }

        private static string WebProjectFile(string name)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var candidate = Path.Combine(directory.FullName, "Web", name);
                if (File.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            Assert.Fail(name + " was not found.");
            return null;
        }

        private sealed class ThrowingAgentCostStore : IAgentCostReportStore
        {
            private readonly string _message;

            internal ThrowingAgentCostStore(string message)
            {
                _message = message;
            }

            private Task<T> Fail<T>() => Task.FromException<T>(new InvalidOperationException(_message));

            public Task<AgentCostAvailability> GetAvailabilityAsync(bool copilotStudioCreditsEnabled, bool azureCostsEnabled) => Fail<AgentCostAvailability>();
            public Task<AgentCostSummary> GetSummaryAsync(AgentCostQuery query) => Fail<AgentCostSummary>();
            public Task<List<AgentCostDailyPoint>> GetDailyTrendAsync(AgentCostQuery query) => Fail<List<AgentCostDailyPoint>>();
            public Task<List<AgentCostBreakdownRow>> GetBreakdownAsync(AgentCostQuery query, string dimension, int top) => Fail<List<AgentCostBreakdownRow>>();
            public Task<AgentCostDetailPage> GetDetailAsync(AgentCostQuery query) => Fail<AgentCostDetailPage>();
            public Task<List<AzureCostBreakdownRow>> GetAzureBreakdownAsync(AgentCostQuery query, string dimension, int top) => Fail<List<AzureCostBreakdownRow>>();
            public Task<AgentCostFilterOptions> GetFilterOptionsAsync(AgentCostQuery query) => Fail<AgentCostFilterOptions>();
            public Task<List<AgentCostUserRow>> GetTopUsersAsync(AgentCostQuery query, int top) => Fail<List<AgentCostUserRow>>();
        }
    }
}
