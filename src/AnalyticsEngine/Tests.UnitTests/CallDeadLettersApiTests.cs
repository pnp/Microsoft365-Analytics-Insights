extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.Calls;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Calls;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;

namespace Tests.UnitTests
{
    /// <summary>
    /// <c>api/CallDeadLetters</c>, behind the dead-lettered Teams calls card on Administration &gt; Service configuration: the
    /// real controller in an in-memory Web API host with role enforcement on, over an in-memory request store and a fake
    /// queue count. The importer's side is <see cref="CallDeadLetterRequestPollerTests"/>.
    /// </summary>
    [TestClass]
    public class CallDeadLettersApiTests
    {
        private static readonly DateTime Now = CallDeadLetterRequestTests.Now;

        private sealed class Deployment
        {
            public bool CallsImportEnabled = true;
            public bool ServiceBusConfigured = true;
            public bool DurableState = true;
            public IKeyValueStore Values = new InMemoryKeyValueStore();
            public DateTime UtcNow = Now;
            public long DeadLetterCount = 12;
            public bool CountFails;
            public int CountReads;

            public CallDeadLetterRequestStore Store => new CallDeadLetterRequestStore(Values, DurableState);

            public CallDeadLetterService Service() => new CallDeadLetterService(CallsImportEnabled, ServiceBusConfigured, Store,
                ServiceBusConfigured ? ReadDepth : (Func<CancellationToken, Task<CallQueueDepth>>)null, () => UtcNow, new RecordingLogger());

            private Task<CallQueueDepth> ReadDepth(CancellationToken cancellationToken)
            {
                CountReads++;
                if (CountFails) throw new InvalidOperationException("Simulated: the namespace refused the runtime identity.");
                return Task.FromResult(new CallQueueDepth { QueueName = "contoso-calls", ActiveMessageCount = 3, DeadLetterMessageCount = DeadLetterCount });
            }
        }

        private static PortalTestHost Host(Deployment deployment, string role = PortalRoles.Administration, bool crossSiteDefence = false)
        {
            return new PortalTestHost(new[] { typeof(CallDeadLettersAPIController) },
                role == null ? PortalTestHost.SignedIn() : PortalTestHost.SignedIn(role),
                PortalAccessPolicy.Enforcing,
                create: type => new CallDeadLettersAPIController(deployment.Service),
                crossSiteDefence: crossSiteDefence);
        }

        private static async Task<(HttpStatusCode Status, JObject Body)> GetAsync(PortalTestHost host)
        {
            var response = await host.Client.GetAsync("api/CallDeadLetters");
            return (response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));
        }

        private static async Task<(HttpStatusCode Status, JObject Body)> PostAsync(PortalTestHost host, string body, bool fromThePortal = true)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/CallDeadLetters/requests"))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (fromThePortal) request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                var response = await host.Client.SendAsync(request);
                var text = await response.Content.ReadAsStringAsync();
                return (response.StatusCode, string.IsNullOrEmpty(text) ? null : JObject.Parse(text));
            }
        }

        private static async Task SeedAsync(Deployment deployment, CallDeadLetterRequest request)
        {
            Assert.IsTrue(await deployment.Store.TryReplaceAsync(request, (await deployment.Store.ReadAsync()).VersionToken));
        }

        [TestMethod]
        public async Task Status_ForAnAdministrator_GivesTheCountAndTheLatestRequest_AsFactsAndCodes()
        {
            var deployment = new Deployment();
            using (var host = Host(deployment))
            {
                var (status, body) = await GetAsync(host);

                Assert.AreEqual(HttpStatusCode.OK, status);
                Assert.IsTrue((bool)body["available"]);
                Assert.AreEqual(JTokenType.Null, body["unavailableReason"].Type);
                Assert.AreEqual(12, (long)body["deadLetterCount"]);
                Assert.AreEqual(3, (long)body["activeCount"]);
                Assert.IsFalse((bool)body["countUnavailable"]);
                Assert.AreEqual(JTokenType.Null, body["latest"].Type);
                Assert.IsTrue((bool)body["canRequest"]);
                Assert.AreEqual(15, (int)body["pickUpWithinMinutes"]);
                Assert.AreEqual(120, (int)body["finishWithinMinutes"]);
                Assert.AreEqual(15, (int)body["runBudgetMinutes"]);

                var finished = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Reprocess, Now.AddMinutes(-30));
                finished.ClaimedUtc = Now.AddMinutes(-29);
                finished.FinishedUtc = Now.AddMinutes(-20);
                finished.Outcome = CallDeadLetterOutcomes.Partial;
                finished.FailureCode = CallDeadLetterFailureCodes.TimeBudget;
                finished.Result = new CallDeadLetterRunResult { Passes = 3, Replayed = 2000 };
                await SeedAsync(deployment, finished);
                deployment.CountFails = true;

                (status, body) = await GetAsync(host);

                Assert.AreEqual(HttpStatusCode.OK, status, "A count Service Bus won't give is not a failed page.");
                Assert.IsTrue((bool)body["countUnavailable"]);
                Assert.AreEqual(JTokenType.Null, body["deadLetterCount"].Type);
                Assert.AreEqual(CallDeadLetterRequestStates.Finished, (string)body["latest"]["state"]);
                Assert.AreEqual(CallDeadLetterOutcomes.Partial, (string)body["latest"]["outcome"]);
                Assert.AreEqual(CallDeadLetterFailureCodes.TimeBudget, (string)body["latest"]["failureCode"]);
                Assert.AreEqual(2000, (int)body["latest"]["result"]["replayed"]);
                Assert.AreEqual(Now.AddMinutes(-20), ((DateTime)body["latest"]["finishedUtc"]).ToUniversalTime());
                Assert.IsTrue((bool)body["canRequest"]);
                Assert.IsFalse(body.ToString().Contains("Simulated"), "The failure's text stays in the log.");
            }
        }

        [TestMethod]
        public async Task Submit_RecordsTheRequest_ThenRefusesAnotherWhileItIsPending()
        {
            var deployment = new Deployment();
            using (var host = Host(deployment))
            {
                var (status, body) = await PostAsync(host, "{\"action\":\"Reprocess\"}");

                Assert.AreEqual(HttpStatusCode.Accepted, status);
                Assert.AreEqual(CallDeadLetterActions.Reprocess, (string)body["latest"]["action"]);
                Assert.AreEqual(CallDeadLetterRequestStates.Waiting, (string)body["latest"]["state"]);
                Assert.IsFalse((bool)body["canRequest"]);
                var recorded = (await deployment.Store.ReadAsync()).Request;
                Assert.AreEqual(Now, recorded.RequestedUtc);
                Assert.IsNull(recorded.ClaimedUtc);
                Assert.AreEqual(32, recorded.Id.Length);

                (status, body) = await PostAsync(host, "{\"action\":\"clear\"}");

                Assert.AreEqual(HttpStatusCode.Conflict, status);
                Assert.AreEqual(CallDeadLetterErrorCodes.RequestPending, (string)body["code"]);
                Assert.AreEqual(recorded.Id, (await deployment.Store.ReadAsync()).Request.Id, "The waiting request is untouched.");

                var claimed = (await deployment.Store.ReadAsync());
                var running = claimed.Request;
                running.ClaimedUtc = Now.AddMinutes(1);
                Assert.IsTrue(await deployment.Store.TryReplaceAsync(running, claimed.VersionToken));
                deployment.UtcNow = Now.AddMinutes(100);

                (status, body) = await PostAsync(host, "{\"action\":\"clear\"}");

                Assert.AreEqual(HttpStatusCode.Conflict, status, "Claimed 99 minutes ago: still running.");
                Assert.AreEqual(CallDeadLetterErrorCodes.RequestPending, (string)body["code"]);
            }
        }

        /// <summary>A request no importer picked up within 15 minutes, or claimed and not finished within 2 hours, may be replaced.</summary>
        [TestMethod]
        public async Task Submit_ReplacesAStaleRequest()
        {
            var notPickedUp = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Reprocess, Now.AddMinutes(-16));
            var notFinished = CallDeadLetterRequestTests.Waiting(CallDeadLetterActions.Reprocess, Now.AddHours(-3));
            notFinished.ClaimedUtc = Now.AddHours(-2).AddMinutes(-1);

            foreach (var stale in new[] { notPickedUp, notFinished })
            {
                var deployment = new Deployment();
                await SeedAsync(deployment, stale);
                using (var host = Host(deployment))
                {
                    var (_, before) = await GetAsync(host);
                    Assert.AreEqual(CallDeadLetterRequestRules.StateOf(stale, Now), (string)before["latest"]["state"]);
                    Assert.IsTrue((bool)before["canRequest"]);

                    var (status, body) = await PostAsync(host, "{\"action\":\"clear\"}");

                    Assert.AreEqual(HttpStatusCode.Accepted, status);
                    Assert.AreEqual(CallDeadLetterActions.Clear, (string)body["latest"]["action"]);
                    Assert.AreEqual(CallDeadLetterRequestStates.Waiting, (string)body["latest"]["state"]);
                    Assert.AreNotEqual(stale.Id, (string)body["latest"]["id"]);
                }
            }
        }

        [TestMethod]
        public async Task Submit_AnUnknownActionIs400_WithAStableCode()
        {
            var deployment = new Deployment();
            using (var host = Host(deployment))
            {
                foreach (var body in new[] { "{\"action\":\"purge\"}", "{}", "{\"action\":null}" })
                {
                    var (status, error) = await PostAsync(host, body);

                    Assert.AreEqual(HttpStatusCode.BadRequest, status, body);
                    Assert.AreEqual(CallDeadLetterErrorCodes.UnknownAction, (string)error["code"], body);
                }
                Assert.IsNull((await deployment.Store.ReadAsync()).Request);
            }
        }

        /// <summary>
        /// Without the calls import or Service Bus there is no queue; with only the in-memory state store, a request would sit in
        /// the web app's memory where the importer can't see it. The page says why, and a request is refused with that code.
        /// </summary>
        [TestMethod]
        public async Task Unavailable_WithoutTheCallsImport_ServiceBus_OrDurableState()
        {
            var cases = new[]
            {
                (Deployment: new Deployment { CallsImportEnabled = false }, Reason: CallDeadLetterUnavailableReasons.CallsImportDisabled, Counted: false),
                (Deployment: new Deployment { ServiceBusConfigured = false }, Reason: CallDeadLetterUnavailableReasons.ServiceBusNotConfigured, Counted: false),
                (Deployment: new Deployment { DurableState = false }, Reason: CallDeadLetterUnavailableReasons.StateStoreInMemory, Counted: true),
            };
            foreach (var unavailable in cases)
            {
                using (var host = Host(unavailable.Deployment))
                {
                    var (status, body) = await GetAsync(host);

                    Assert.AreEqual(HttpStatusCode.OK, status, unavailable.Reason);
                    Assert.IsFalse((bool)body["available"], unavailable.Reason);
                    Assert.AreEqual(unavailable.Reason, (string)body["unavailableReason"]);
                    Assert.IsFalse((bool)body["canRequest"], unavailable.Reason);
                    Assert.AreEqual(unavailable.Counted, body["deadLetterCount"].Type == JTokenType.Integer, unavailable.Reason);

                    var (refused, error) = await PostAsync(host, "{\"action\":\"reprocess\"}");

                    Assert.AreEqual(HttpStatusCode.Conflict, refused, unavailable.Reason);
                    Assert.AreEqual(unavailable.Reason, (string)error["code"]);
                    Assert.IsNull((await unavailable.Deployment.Store.ReadAsync()).Request, unavailable.Reason);
                }
            }
        }

        [TestMethod]
        public async Task AnUnreachableStateStore_IsSaidPlainly_AndARequestIs503()
        {
            var deployment = new Deployment { Values = new UnreachableKeyValueStore() };
            using (var host = Host(deployment))
            {
                var (status, body) = await GetAsync(host);
                Assert.AreEqual(HttpStatusCode.OK, status);
                Assert.IsTrue((bool)body["latestUnavailable"]);
                Assert.IsFalse((bool)body["canRequest"], "Whether a request is pending is unknown.");
                Assert.AreEqual(12, (long)body["deadLetterCount"]);

                var (refused, error) = await PostAsync(host, "{\"action\":\"reprocess\"}");
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, refused);
                Assert.AreEqual(CallDeadLetterErrorCodes.StateStoreUnavailable, (string)error["code"]);
            }
        }

        /// <summary>Both actions need Administration, and a request must come from the portal's own scripts (#825's central check).</summary>
        [TestMethod]
        public async Task OnlyAnAdministrator_FromThePortalItself_CanAsk()
        {
            var deployment = new Deployment();
            using (var noRole = Host(deployment, role: null))
            using (var piiOnly = Host(deployment, role: PortalRoles.SeePii))
            {
                foreach (var host in new[] { noRole, piiOnly })
                {
                    var response = await host.Client.GetAsync("api/CallDeadLetters");
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                    Assert.AreEqual("administration", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["permission"]);

                    var (refused, error) = await PostAsync(host, "{\"action\":\"clear\"}");
                    Assert.AreEqual(HttpStatusCode.Forbidden, refused);
                    Assert.AreEqual(PortalPermissionDeniedModel.ErrorCode, (string)error["code"]);
                }
                Assert.AreEqual(0, deployment.CountReads, "Refused before the action ran.");
            }
            Assert.IsNull((await deployment.Store.ReadAsync()).Request);

            using (var admin = Host(deployment, crossSiteDefence: true))
            {
                var (crossSite, _) = await PostAsync(admin, "{\"action\":\"clear\"}", fromThePortal: false);
                Assert.AreEqual(HttpStatusCode.Forbidden, crossSite, "A form on another site can't clear the dead-letter queue with an administrator's cookie.");
                Assert.IsNull((await deployment.Store.ReadAsync()).Request);

                var (accepted, _) = await PostAsync(admin, "{\"action\":\"clear\"}");
                Assert.AreEqual(HttpStatusCode.Accepted, accepted);
                Assert.AreEqual(CallDeadLetterActions.Clear, (await deployment.Store.ReadAsync()).Request.Action);
            }
        }
    }
}
