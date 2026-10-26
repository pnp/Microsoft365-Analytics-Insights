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
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

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
        [TestMethod]
        public void CoreHandlerReplacesTheFrameworkExceptionHandler()
        {
            Assert.IsTrue(typeof(IExceptionHandler).IsAssignableFrom(typeof(AnalyticsWebExceptionHandler)));
        }

        [TestMethod]
        public void ProductionHostUsesTheGenericErrorRouteWithoutXmlTransforms()
        {
            var source = File.ReadAllText(WebProjectFile("Program.cs"));
            StringAssert.Contains(source, "if (!app.Environment.IsDevelopment())");
            StringAssert.Contains(source, "app.UseExceptionHandler(\"/Home/Error\")");
            Assert.IsFalse(source.Contains("UseDeveloperExceptionPage"));
            Assert.IsFalse(File.Exists(Path.Combine(Path.GetDirectoryName(WebProjectFile("Program.cs")), "Web.Template.config")));
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

        [TestMethod]
        public void CoreHostRegistersTheHandlerThatSendsAnErrorReference()
        {
            var services = new ServiceCollection();
            services.AddExceptionHandler<AnalyticsWebExceptionHandler>();
            using var provider = services.BuildServiceProvider();
            Assert.IsInstanceOfType(provider.GetServices<IExceptionHandler>().Single(), typeof(AnalyticsWebExceptionHandler));
            StringAssert.Contains(File.ReadAllText(WebProjectFile("Program.cs")), "AddExceptionHandler<AnalyticsWebExceptionHandler>");
        }

        [TestMethod]
        public async Task UnhandledException_SendsACodeAndAnErrorId_ButNotTheExceptionMessage()
        {
            var failure = new InvalidOperationException("synthetic private detail: Server=contoso-sql.database.windows.net");
            using (var host = ThrowingHost(failure))
            {
                var response = await host.Client.GetAsync("api/S10Throwing");
                var body = await response.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
                var json = JObject.Parse(body);
                Assert.AreEqual("unexpected", (string)json["code"]);
                Assert.AreEqual(WebErrorReference.For(failure), (string)json["errorId"]);
                Assert.IsTrue(Guid.TryParse((string)json["errorId"], out _), body);
                Assert.AreEqual(2, json.Count, body);
                Assert.IsFalse(body.Contains("synthetic private detail"), body);
                Assert.IsFalse(body.Contains("contoso-sql"), body);
            }
        }

        [TestMethod]
        public async Task ACachedFailure_RethrownToALaterRequest_CarriesTheOriginalErrorId()
        {
            var cached = new InvalidOperationException("synthetic cached failure");
            using (var host = ThrowingHost(cached))
            {
                var first = JObject.Parse(await (await host.Client.GetAsync("api/S10Throwing")).Content.ReadAsStringAsync());
                var second = JObject.Parse(await (await host.Client.GetAsync("api/S10Throwing")).Content.ReadAsStringAsync());

                Assert.AreEqual((string)first["errorId"], (string)second["errorId"]);
            }

            Assert.AreNotEqual(WebErrorReference.For(cached), WebErrorReference.For(new InvalidOperationException("synthetic other failure")));
        }

        [TestMethod]
        public void ReportedException_IsStampedWithTheSameErrorId_OnTheExceptionAndItsTrace()
        {
            var channel = new RecordingTelemetryChannel();
            var configuration = new Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration
            {
                TelemetryChannel = channel,
                ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            };
            var logger = new DataUtils.AnalyticsLogger(new Microsoft.ApplicationInsights.TelemetryClient(configuration), "WebApiTest");
            var failure = new InvalidOperationException("synthetic failure");

            WebExceptionTelemetry.Report(failure, "WebApi /api/example", _ => logger);

            var errorId = WebErrorReference.For(failure);
            var exception = channel.Sent.OfType<Microsoft.ApplicationInsights.DataContracts.ExceptionTelemetry>().Single();
            Assert.AreEqual(errorId, exception.Properties["ErrorId"]);
            Assert.AreEqual(errorId, exception.Context.Operation.Id, "No other operation owns the failure, so the error id is the operation id.");

            var trace = channel.Sent.OfType<Microsoft.ApplicationInsights.DataContracts.TraceTelemetry>()
                .Single(t => t.Message.Contains("Unhandled web request error"));
            StringAssert.Contains(trace.Message, "ErrorId " + errorId);
            Assert.AreEqual(errorId, trace.Context.Operation.Id);
        }

        private static PortalTestHost ThrowingHost(Exception failure) =>
            new PortalTestHost(
                new[] { typeof(S10ThrowingController) },
                PortalTestHost.SignedIn(),
                PortalAccessPolicy.NotEnforcing,
                _ => new S10ThrowingController(failure),
                productionExceptionHandler: true);

        public sealed class S10ThrowingController : ControllerBase
        {
            private readonly Exception _failure;

            public S10ThrowingController(Exception failure)
            {
                _failure = failure;
            }

            [HttpGet]
            [Route("api/S10Throwing")]
            public IActionResult Get() => throw _failure;
        }

        private sealed class RecordingTelemetryChannel : Microsoft.ApplicationInsights.Channel.ITelemetryChannel
        {
            internal List<Microsoft.ApplicationInsights.Channel.ITelemetry> Sent { get; } = new List<Microsoft.ApplicationInsights.Channel.ITelemetry>();

            public bool? DeveloperMode { get; set; }
            public string EndpointAddress { get; set; }

            public void Send(Microsoft.ApplicationInsights.Channel.ITelemetry item) => Sent.Add(item);
            public void Flush() { }
            public void Dispose() { }
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
