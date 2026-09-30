extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Models.UserImport;
using Common.Entities;
using Common.Entities.State;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Claims;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Controllers;
using System.Web.Http.Dispatcher;
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;

namespace Tests.UnitTests
{
    /// <summary>
    /// The web portal's Administration &gt; User import page (issue #664): it reports whether the Graph user import
    /// has a stored checkpoint, and clears it so the next run reads every user again - the in-product version of
    /// deleting the stored token by hand.
    ///
    /// Clearing is the portal's first action that changes state, so these tests pin its safety as well as its
    /// behaviour: it only runs for a signed-in admin, only from the portal's own scripts (a signed-in admin's
    /// cookie alone, sent by a form on another site, must not be enough), and never exposes the token itself.
    /// </summary>
    [TestClass]
    public class UserImportCheckpointApiTests
    {
        private static readonly Guid TenantId = Guid.Empty;
        private const string TokenValue = "synthetic-delta-token-value-0001";
        private const string StampValue = "2026-09-01T10:00:00.0000000Z";

        private static string CheckpointKey => UserImportCheckpointKeys.DeltaToken(TenantId);

        #region The key names are the importer's

        [TestMethod]
        public void CheckpointKey_KeepsTheFormatExistingDeploymentsAlreadyStore()
        {
            // Changing it would orphan every stored token, so every deployment would read its whole directory again
            // on upgrade - and the key documented in the wiki would stop matching.
            Assert.AreEqual("UserDeltaCode-00000000-0000-0000-0000-000000000000-v2", UserImportCheckpointKeys.DeltaToken(Guid.Empty));
        }

        [TestMethod]
        public void LastCompletedKey_IsTheImportersCadenceKey()
        {
            Assert.AreEqual("GraphUsersMetadataLastImported", UserImportCheckpointKeys.LastCompleted);
            Assert.AreEqual(ProductionGraphImportSectionFactory.GraphUsersMetadataLastImportedKey, UserImportCheckpointKeys.LastCompleted,
                "Clearing a key the cadence gate does not read would not make the import run on the next cycle.");
        }

        #endregion

        #region What the page is told

        [TestMethod]
        public async Task Status_WithoutStorage_SaysSo_AndNeverTouchesAStore()
        {
            var service = NewService(store: null, importSettings: new ImportTaskSettings { GraphUsersMetadata = true }, intervalHours: 24);

            var status = await service.GetStatusAsync();

            Assert.IsFalse(status.StorageConfigured);
            Assert.IsFalse(status.CheckpointStored);
            Assert.IsNull(status.LastCompletedUtc);
            Assert.AreEqual(CheckpointKey, status.CheckpointKey);
            Assert.AreEqual(true, status.UserImportEnabled);
            Assert.AreEqual(24, status.IntervalHours);
        }

        [TestMethod]
        public async Task Status_ReportsAStoredCheckpoint_WithoutEverReadingIt()
        {
            var store = new InMemoryCheckpointStore();
            store.Values[CheckpointKey] = TokenValue;
            store.Values[UserImportCheckpointKeys.LastCompleted] = StampValue;

            var status = await NewService(store).GetStatusAsync();

            Assert.IsTrue(status.CheckpointStored);
            Assert.AreEqual(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), status.LastCompletedUtc);
            Assert.AreEqual(DateTimeKind.Utc, status.LastCompletedUtc.Value.Kind);
            CollectionAssert.DoesNotContain(store.StringReads, CheckpointKey, "The token's value is never loaded - only tested for existence.");
            Assert.IsFalse(JsonConvert.SerializeObject(status).Contains(TokenValue), "The token must never reach the browser.");
        }

        [TestMethod]
        public async Task Status_NothingStored()
        {
            var status = await NewService(new InMemoryCheckpointStore(), importSettings: null).GetStatusAsync();

            Assert.IsTrue(status.StorageConfigured);
            Assert.IsFalse(status.CheckpointStored);
            Assert.IsNull(status.LastCompletedUtc);
            Assert.IsNull(status.UserImportEnabled, "Unreadable import settings are unknown, not 'off'.");
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("not a date")]
        public void LastCompleted_ThatCannotBeRead_IsNotRecorded(string raw)
        {
            Assert.IsNull(UserImportCheckpointService.ParseLastCompleted(raw));
        }

        [TestMethod]
        public async Task Status_NamesTheTableAndPartitionThatHoldTheCheckpoint()
        {
            var status = await NewService(new InMemoryCheckpointStore()).GetStatusAsync();

            Assert.AreEqual(StateStore.TableName, status.CheckpointTable);
            Assert.AreEqual(StatePartitions.UserImport, status.CheckpointPartition);
            Assert.AreEqual("AnalyticsState", status.CheckpointTable, "Documented in the wiki - operators look for it by name.");
            Assert.AreEqual("UserImport", status.CheckpointPartition, "Documented in the wiki - operators look for it by name.");
        }

        [TestMethod]
        public async Task StateTableStore_ReadsAndClearsEachKeyInThePartitionTheImporterWritesItTo()
        {
            // The importer keeps the delta token in the UserImport partition and its cadence stamp in ImportSchedule
            // (Program.cs / UserMetadataUpdater). Reading either from the wrong partition would report "nothing stored"
            // and clear nothing.
            var checkpoints = new InMemoryKeyValueStore();
            var schedule = new InMemoryKeyValueStore();
            await checkpoints.SetStringAsync(CheckpointKey, TokenValue);
            await schedule.SetStringAsync(UserImportCheckpointKeys.LastCompleted, StampValue);

            var service = new UserImportCheckpointService(TenantId,
                () => new StateTableUserImportCheckpointStore(checkpoints, schedule),
                new ImportTaskSettings { GraphUsersMetadata = true }, 24, null);

            var status = await service.GetStatusAsync();
            Assert.IsTrue(status.CheckpointStored);
            Assert.AreEqual(new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), status.LastCompletedUtc);

            var cleared = await service.ClearAsync(runOnNextCycle: true);
            Assert.IsTrue(cleared.CheckpointCleared);
            Assert.IsTrue(cleared.LastCompletedCleared);
            Assert.AreEqual(0, checkpoints.Count, "The delta token row is gone.");
            Assert.AreEqual(0, schedule.Count, "The last-completed row is gone.");
        }

        #endregion

        #region Clearing

        [TestMethod]
        public async Task Clear_DeletesTheCheckpoint_AndTheLastCompletedStampOnlyWhenAsked()
        {
            var store = StoreWithCheckpointAndStamp();

            var result = await NewService(store).ClearAsync(runOnNextCycle: false);

            Assert.IsTrue(result.CheckpointCleared);
            Assert.IsFalse(result.LastCompletedCleared);
            Assert.IsFalse(store.Values.ContainsKey(CheckpointKey));
            Assert.IsTrue(store.Values.ContainsKey(UserImportCheckpointKeys.LastCompleted), "The import then runs when its interval next allows.");

            store = StoreWithCheckpointAndStamp();
            result = await NewService(store).ClearAsync(runOnNextCycle: true);

            Assert.IsTrue(result.CheckpointCleared);
            Assert.IsTrue(result.LastCompletedCleared);
            Assert.AreEqual(0, store.Values.Count, "With the stamp gone the cadence gate lets the import run on the next cycle.");
        }

        [TestMethod]
        public async Task Clear_WhenNothingIsStored_SaysSoRatherThanClaimingItClearedSomething()
        {
            var result = await NewService(new InMemoryCheckpointStore()).ClearAsync(runOnNextCycle: true);

            Assert.IsFalse(result.CheckpointCleared);
            Assert.IsFalse(result.LastCompletedCleared);
        }

        [TestMethod]
        public async Task Clear_IsLogged_WithoutTheTokenOrTheTenant()
        {
            var logger = new CapturingLogger();

            await NewService(StoreWithCheckpointAndStamp(), logger: logger).ClearAsync(runOnNextCycle: true);

            var warning = logger.Messages(LogLevel.Warning).Single();
            StringAssert.Contains(warning, "cleared from the web portal");
            StringAssert.Contains(warning, "runs on the next import cycle");
            Assert.IsFalse(warning.Contains(TokenValue));
            Assert.IsFalse(warning.Contains(CheckpointKey), "The key carries the tenant id, and logs get pasted into public issues.");
        }

        [TestMethod]
        public async Task Clear_WithoutStorage_IsRefused()
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => NewService(store: null).ClearAsync(runOnNextCycle: true));
        }

        [TestMethod]
        public async Task UnreachableStorage_IsReportedAsUnavailable_ForBothReadingAndClearing()
        {
            var broken = new InMemoryCheckpointStore { Throw = true };
            await Assert.ThrowsExceptionAsync<UserImportCheckpointUnavailableException>(() => NewService(broken).GetStatusAsync());
            await Assert.ThrowsExceptionAsync<UserImportCheckpointUnavailableException>(() => NewService(broken).ClearAsync(runOnNextCycle: false));

            // Failing to connect at all surfaces from the store factory rather than from a call.
            var unreachable = new UserImportCheckpointService(TenantId,
                () => throw new InvalidOperationException("synthetic storage connection failure"), null, 24, null);
            await Assert.ThrowsExceptionAsync<UserImportCheckpointUnavailableException>(() => unreachable.GetStatusAsync());
        }

        #endregion

        #region Through the Web API pipeline

        [TestMethod]
        public async Task Get_ReturnsCamelCaseFacts_AndNeverTheToken()
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var response = await host.Client.GetAsync("api/UserImportCheckpoint"))
            {
                var body = await response.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                var json = JObject.Parse(body);
                Assert.AreEqual(true, (bool)json["storageConfigured"]);
                Assert.AreEqual(true, (bool)json["checkpointStored"]);
                Assert.AreEqual(CheckpointKey, (string)json["checkpointKey"]);
                Assert.AreEqual("AnalyticsState", (string)json["checkpointTable"]);
                Assert.AreEqual("UserImport", (string)json["checkpointPartition"]);
                StringAssert.Contains(body, "\"lastCompletedUtc\":\"2026-09-01T10:00:00Z\"", "The SPA parses an ISO UTC timestamp.");
                Assert.IsFalse(body.Contains(TokenValue));
            }
        }

        [TestMethod]
        public async Task Clear_FromThePortal_DeletesTheCheckpoint()
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var request = PortalClear(runOnNextCycle: true))
            using (var response = await host.Client.SendAsync(request))
            {
                var body = await response.Content.ReadAsStringAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
                var json = JObject.Parse(body);
                Assert.AreEqual(true, (bool)json["checkpointCleared"]);
                Assert.AreEqual(true, (bool)json["lastCompletedCleared"]);
                Assert.AreEqual(0, store.Values.Count);
            }
        }

        /// <summary>
        /// Cross-site request forgery: a page on another site submits a form to the API, and the browser attaches the
        /// admin's auth cookie. The cookie alone must never be enough.
        /// </summary>
        [TestMethod]
        public async Task Clear_WithoutThePortalsHeader_IsRefused_AndDeletesNothing()
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "api/UserImportCheckpoint/clear")
            {
                Content = new StringContent("runOnNextCycle=true", Encoding.UTF8, "application/x-www-form-urlencoded"),
            })
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.AreEqual(2, store.Values.Count, "Nothing may be deleted.");
            }
        }

        [DataTestMethod]
        [DataRow("cross-site")]
        [DataRow("same-site")]
        [DataRow("none")]
        public async Task Clear_ThatTheBrowserSaysCameFromElsewhere_IsRefused(string fetchSite)
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var request = PortalClear(runOnNextCycle: true))
            {
                request.Headers.Add("Sec-Fetch-Site", fetchSite);

                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode,
                        $"Sec-Fetch-Site '{fetchSite}': only the portal's own origin may clear the checkpoint.");
                    Assert.AreEqual(2, store.Values.Count);
                }
            }
        }

        [TestMethod]
        public async Task Clear_ThatTheBrowserSaysIsSameOrigin_IsAccepted()
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var request = PortalClear(runOnNextCycle: false))
            {
                request.Headers.Add("Sec-Fetch-Site", "same-origin");

                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    Assert.IsFalse(store.Values.ContainsKey(CheckpointKey));
                }
            }
        }

        [TestMethod]
        public async Task Clear_ByAGetRequest_IsImpossible()
        {
            // A GET can be triggered by an <img> tag on any page, so a state change must never answer one.
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, "api/UserImportCheckpoint/clear"))
            {
                request.Headers.Add("X-Requested-With", "XMLHttpRequest");
                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.IsFalse(response.IsSuccessStatusCode, $"Got {(int)response.StatusCode}.");
                    Assert.AreEqual(2, store.Values.Count);
                }
            }
        }

        [TestMethod]
        public async Task Clear_WhenNotSignedIn_IsRefused_AndDeletesNothing()
        {
            var store = StoreWithCheckpointAndStamp();
            using (var host = new CheckpointHost(NewService(store), () => new ClaimsPrincipal(new ClaimsIdentity())))
            using (var request = PortalClear(runOnNextCycle: true))
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual(2, store.Values.Count);
            }
        }

        [TestMethod]
        public async Task Clear_WithoutStorage_AnswersWithAStableCode()
        {
            using (var host = new CheckpointHost(NewService(store: null)))
            using (var request = PortalClear(runOnNextCycle: true))
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
                Assert.AreEqual("storageNotConfigured", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
            }
        }

        [TestMethod]
        public async Task UnreachableStorage_AnswersWithAStableCode_AndNoServerText()
        {
            using (var host = new CheckpointHost(NewService(new InMemoryCheckpointStore { Throw = true })))
            {
                using (var response = await host.Client.GetAsync("api/UserImportCheckpoint"))
                {
                    Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync());
                    Assert.AreEqual("storageUnavailable", (string)json["code"]);
                    Assert.AreEqual(1, json.Properties().Count(), "The page writes the sentence; the server sends only the code.");
                }

                using (var request = PortalClear(runOnNextCycle: true))
                using (var response = await host.Client.SendAsync(request))
                {
                    Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                    Assert.AreEqual("storageUnavailable", (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"]);
                }
            }
        }

        #endregion

        #region The request check on its own

        [TestMethod]
        public void SameOriginCheck_NeedsTheHeader_AndAcceptsItInAnyCase()
        {
            using (var withHeader = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/x"))
            using (var lowerCase = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/x"))
            using (var withoutHeader = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/x"))
            using (var wrongValue = new HttpRequestMessage(HttpMethod.Post, "http://localhost/api/x"))
            {
                withHeader.Headers.Add("X-Requested-With", "XMLHttpRequest");
                lowerCase.Headers.Add("x-requested-with", "xmlhttprequest");
                wrongValue.Headers.Add("X-Requested-With", "fetch");

                Assert.IsTrue(RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(withHeader));
                Assert.IsTrue(RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(lowerCase));
                Assert.IsFalse(RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(withoutHeader));
                Assert.IsFalse(RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(wrongValue));
                Assert.IsFalse(RequireSameOriginXhrAttribute.IsSameOriginScriptRequest(null));
            }
        }

        [TestMethod]
        public void TheClearAction_IsAPostThatCarriesTheSameOriginCheck()
        {
            var clear = typeof(UserImportCheckpointAPIController).GetMethod(nameof(UserImportCheckpointAPIController.Clear));

            Assert.IsNotNull(clear.GetCustomAttribute<HttpPostAttribute>());
            Assert.IsNotNull(clear.GetCustomAttribute<RequireSameOriginXhrAttribute>(),
                "Removing the check would let any page the admin visits clear the checkpoint with their session.");
            Assert.IsNotNull(typeof(UserImportCheckpointAPIController).GetCustomAttribute<AuthorizeAttribute>());
        }

        #endregion

        #region Wire contract

        [TestMethod]
        public void EveryModelPropertyDeclaresACamelCaseWireName()
        {
            // This web app has no camelCase contract resolver: a property without [JsonProperty] serialises in
            // PascalCase and the SPA reads undefined (see DlpApiContractTests for the incident behind this rule).
            foreach (var type in new[] { typeof(UserImportCheckpointStatus), typeof(UserImportCheckpointClearRequest), typeof(UserImportCheckpointClearResult), typeof(UserImportCheckpointError) })
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var attribute = property.GetCustomAttribute<JsonPropertyAttribute>();
                    Assert.IsNotNull(attribute, $"{type.Name}.{property.Name} has no [JsonProperty].");
                    Assert.IsTrue(char.IsLower(attribute.PropertyName[0]), $"{type.Name}.{property.Name} serialises as '{attribute.PropertyName}', which is not camelCase.");
                }
            }
        }

        #endregion

        #region Helpers

        private static UserImportCheckpointService NewService(InMemoryCheckpointStore store, ImportTaskSettings importSettings = null, int intervalHours = 24, ILogger logger = null)
        {
            return new UserImportCheckpointService(TenantId,
                store == null ? (Func<IUserImportCheckpointStore>)null : () => store,
                importSettings, intervalHours, logger);
        }

        private static InMemoryCheckpointStore StoreWithCheckpointAndStamp()
        {
            var store = new InMemoryCheckpointStore();
            store.Values[CheckpointKey] = TokenValue;
            store.Values[UserImportCheckpointKeys.LastCompleted] = StampValue;
            return store;
        }

        /// <summary>The request the portal's apiFetch makes: JSON body, and the X-Requested-With header.</summary>
        private static HttpRequestMessage PortalClear(bool runOnNextCycle)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "api/UserImportCheckpoint/clear")
            {
                Content = new StringContent("{\"runOnNextCycle\":" + (runOnNextCycle ? "true" : "false") + "}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            return request;
        }

        private sealed class InMemoryCheckpointStore : IUserImportCheckpointStore
        {
            public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
            public List<string> StringReads { get; } = new List<string>();
            public bool Throw { get; set; }

            public Task<bool> KeyExistsAsync(string key)
            {
                ThrowIfBroken();
                return Task.FromResult(Values.ContainsKey(key));
            }

            public Task<string> GetStringAsync(string key)
            {
                ThrowIfBroken();
                StringReads.Add(key);
                return Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
            }

            public Task<bool> DeleteKeyAsync(string key)
            {
                ThrowIfBroken();
                return Task.FromResult(Values.Remove(key));
            }

            private void ThrowIfBroken()
            {
                if (Throw) throw new InvalidOperationException("synthetic storage outage");
            }
        }

        /// <summary>The controller behind the real Web API pipeline - routing, [Authorize] and filters - in memory.</summary>
        private sealed class CheckpointHost : IDisposable
        {
            private readonly HttpConfiguration _configuration;
            private readonly HttpServer _server;

            public CheckpointHost(UserImportCheckpointService service, Func<IPrincipal> principal = null)
            {
                _configuration = new HttpConfiguration();
                _configuration.Services.Replace(typeof(IHttpControllerTypeResolver), new ControllerTypes());
                _configuration.Services.Replace(typeof(IHttpControllerActivator), new ControllerActivator(() => new UserImportCheckpointAPIController(() => service)));
                _configuration.MessageHandlers.Add(new PrincipalHandler(principal ?? Administrator));
                _configuration.MapHttpAttributeRoutes();
                _server = new HttpServer(_configuration);
                Client = new HttpClient(_server) { BaseAddress = new Uri("http://localhost/") };
            }

            public HttpClient Client { get; }

            private static IPrincipal Administrator()
            {
                var identity = new ClaimsIdentity("synthetic-admin");
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "synthetic-administrator"));
                return new ClaimsPrincipal(identity);
            }

            public void Dispose()
            {
                Client.Dispose();
                _server.Dispose();
                _configuration.Dispose();
            }

            private sealed class ControllerTypes : IHttpControllerTypeResolver
            {
                public ICollection<Type> GetControllerTypes(IAssembliesResolver assembliesResolver) =>
                    new[] { typeof(UserImportCheckpointAPIController) };
            }

            private sealed class ControllerActivator : IHttpControllerActivator
            {
                private readonly Func<IHttpController> _create;
                internal ControllerActivator(Func<IHttpController> create) { _create = create; }
                public IHttpController Create(HttpRequestMessage request, HttpControllerDescriptor controllerDescriptor, Type controllerType) => _create();
            }

            private sealed class PrincipalHandler : DelegatingHandler
            {
                private readonly Func<IPrincipal> _principal;
                internal PrincipalHandler(Func<IPrincipal> principal) { _principal = principal; }

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    request.GetRequestContext().Principal = _principal();
                    return base.SendAsync(request, cancellationToken);
                }
            }
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<Tuple<LogLevel, string>> _entries = new List<Tuple<LogLevel, string>>();

            public List<string> Messages(LogLevel level) => _entries.Where(e => e.Item1 == level).Select(e => e.Item2).ToList();

            public IDisposable BeginScope<TState>(TState state) => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                _entries.Add(Tuple.Create(logLevel, formatter(state, exception)));
            }
        }

        #endregion
    }
}
