extern alias AnalyticsWeb;
using AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotCosts;
using Azure;
using Azure.Data.Tables;
using Azure.Core.Pipeline;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Owin;
using System.Web.Helpers;
using WebJob.Office365ActivityImporter.Engine.CopilotCosts;

namespace Tests.UnitTests
{
    [TestClass]
    public class CopilotCostConnectionTests
    {
        private const string Tenant = "00000000-0000-0000-0000-000000000001";
        private const string Client = "00000000-0000-0000-0000-000000000002";
        private const string UserId = "00000000-0000-0000-0000-000000000003";
        private static readonly CancellationToken Ct = CancellationToken.None;

        [TestMethod]
        public void CollectionMetadataKeepsCoverageDistinctAndNeverInfersIdleCompletion()
        {
            Func<CostCollectorStatus, JObject> metadata = state => JObject.FromObject(
                AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotCostConnectionAPIController.CollectionMetadata(state));
            var idle = metadata(new CostCollectorStatus());
            Assert.AreEqual("idle", (string)idle["phase"]);
            Assert.IsFalse((bool)idle["inventoryComplete"]);
            Assert.AreEqual(JTokenType.Null, idle["lastOutcome"].Type);
            var partial = metadata(new CostCollectorStatus
            {
                Phase = "users", InventoryComplete = true, DirectoryUsers = 200000,
                Observed = 100000, Measured = 75000, Empty = 20000, Partial = 5000,
                Unevaluated = 5000, Pending = 90000, Retrying = 10000, Denied = 1000,
                LastOutcome = "throttled"
            });
            Assert.AreEqual("users", (string)partial["phase"]);
            Assert.AreEqual(200000L, (long)partial["directoryUsers"]);
            Assert.AreEqual(5000L, (long)partial["partial"]);
            Assert.AreEqual(10000L, (long)partial["retrying"]);
            Assert.AreEqual("throttled", (string)partial["lastOutcome"]);
            Assert.AreEqual(JTokenType.Null, partial["lastCompleted"].Type);
        }

        [TestMethod]
        public void MissingStorageCannotEnableFeatureAndRoleIsTenantApplicationBound()
        {
            Assert.AreEqual("disabled", CostConnectionRuntime.GetAvailability(null, "configured", "https://contoso.example/"));
            Assert.AreEqual("storageRequired", CostConnectionRuntime.GetAvailability("true", null, "https://contoso.example/"));
            Assert.AreEqual("configurationRequired", CostConnectionRuntime.GetAvailability("true", "configured", "http://contoso.example/"));
            var claims = new List<Claim>
            {
                new Claim("tid", Tenant), new Claim("aud", Client), new Claim("oid", UserId),
                new Claim(ClaimTypes.Role, CostConnectionRuntime.OperatorRole)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
            Assert.IsTrue(CostConnectionRuntime.IsOperator(principal, Tenant, Client));
            Assert.IsFalse(CostConnectionRuntime.IsOperator(principal, Client, Client));
            Assert.IsFalse(CostConnectionRuntime.IsOperator(principal, Tenant, Tenant));
            Assert.IsFalse(CostConnectionRuntime.IsOperator(new ClaimsPrincipal(new ClaimsIdentity(claims.Where(c => c.Type != ClaimTypes.Role), "test")), Tenant, Client));
        }

        [TestMethod]
        public async Task CallbackBindsBrowserOperatorStateNonceAndPkceAndCannotReplay()
        {
            var store = new MemoryStore();
            var identity = new Identity();
            var service = Service(store, identity);
            var url = new Uri(await service.StartAsync(UserId, "browser", Ct));
            var query = HttpUtility.ParseQueryString(url.Query);
            var pending = (await service.ReadAsync(Ct)).Pending;
            Assert.AreEqual("form_post", query["response_mode"]);
            Assert.AreEqual("S256", query["code_challenge_method"]);
            Assert.AreEqual(CostConnectionEncoding.Hash(pending.Verifier).TrimEnd('=').Replace('+', '-').Replace('/', '_'), query["code_challenge"]);
            await Fails("invalidCallback", () => service.StageCallbackAsync("code", "wrong", false, "browser", Ct));
            await Fails("invalidCallback", () => service.StageCallbackAsync("code", query["state"], false, "other-browser", Ct));
            await service.StageCallbackAsync("code", query["state"], false, "browser", Ct);
            await Fails("invalidCallback", () => service.StageCallbackAsync("code", query["state"], false, "browser", Ct));
            await Fails("invalidCallback", () => service.CompleteAsync(Client, "browser", Ct));
            await service.CompleteAsync(UserId, "browser", Ct);
            Assert.AreEqual(pending.Verifier, identity.Verifier);
            Assert.AreEqual(pending.Nonce, identity.Nonce);
            Assert.AreEqual("connected", (await service.ReadAsync(Ct)).Status);
            await Fails("invalidCallback", () => service.CompleteAsync(UserId, "browser", Ct));
            Assert.AreEqual(1, identity.Redemptions);
        }

        [TestMethod]
        public async Task ExpiredCallbackAndDifferentReaderCannotReplaceConnection()
        {
            var store = new MemoryStore();
            var identity = new Identity();
            var now = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var service = Service(store, identity, () => now);
            var url = await service.StartAsync(UserId, "browser", Ct);
            now = now.AddMinutes(11);
            await Fails("invalidCallback", () => service.StageCallbackAsync("code", HttpUtility.ParseQueryString(new Uri(url).Query)["state"], false, "browser", Ct));
            await Connect(service);
            identity.Account = "other-reader";
            await Stage(service);
            await Fails("accountMismatch", () => service.CompleteAsync(UserId, "browser", Ct));
            Assert.AreEqual("reader", (await service.ReadAsync(Ct)).AccountId);
        }

        [TestMethod]
        public async Task DisconnectFencesInFlightRefreshAndErasesCacheAndPendingCode()
        {
            var store = new MemoryStore();
            var identity = new Identity();
            var service = Service(store, identity);
            await Connect(service);
            identity.OnRefresh = () => service.DisconnectAsync(Ct);
            await Fails("busy", () => service.RunWithTokenAsync((_, __, ___) => Task.CompletedTask, Ct));
            var state = await service.ReadAsync(Ct);
            Assert.AreEqual("disconnected", state.Status);
            Assert.IsNull(state.Cache);
            Assert.IsNull(state.Pending);
            Assert.IsNull(state.AccountId);
        }

        [TestMethod]
        public async Task RestartUsesOnlyBoundDurableCacheAndInteractionRequiredRemainsVisible()
        {
            var store = new MemoryStore();
            await Connect(Service(store, new Identity()));
            var identity = new Identity { RefreshFailure = "reconnectRequired" };
            var restarted = Service(store, identity);
            await Fails("reconnectRequired", () => restarted.RunWithTokenAsync((_, __, ___) => Task.CompletedTask, Ct));
            Assert.AreEqual("reconnectRequired", (await restarted.ReadAsync(Ct)).Status);
            identity.RefreshFailure = null;
            await restarted.RunWithTokenAsync((_, token, __) => { Assert.AreEqual("access", token); return Task.CompletedTask; }, Ct);
            Assert.AreEqual("reader", identity.RefreshedAccount);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, identity.RefreshedCache);
            Assert.AreEqual("connected", (await restarted.ReadAsync(Ct)).Status);
        }

        [TestMethod]
        public void TableCacheCrossesRealBinaryPropertyBoundariesAndRejectsCorruption()
        {
            foreach (var length in new[] { 0, TableCostDocumentStore.ChunkSize - 1, TableCostDocumentStore.ChunkSize + 1, TableCostDocumentStore.MaximumBytes })
            {
                var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
                var entity = TableCostDocumentStore.Encode("test", "connection", bytes);
                entity.ETag = new ETag("version");
                CollectionAssert.AreEqual(bytes, TableCostDocumentStore.Decode(entity).Content);
                for (var i = 0; i < TableCostDocumentStore.ChunkCount; i++)
                    Assert.IsTrue(entity.GetBinary("Data" + i).Length <= TableCostDocumentStore.ChunkSize);
                entity.Remove("Data0");
                Assert.ThrowsException<CostConnectionException>(() => TableCostDocumentStore.Decode(entity));
            }
            Assert.ThrowsException<CostConnectionException>(() => TableCostDocumentStore.Encode("test", "connection",
                new byte[TableCostDocumentStore.MaximumBytes + 1]));
        }

        [TestMethod]
        public async Task TableSdkPublishesBinaryChunksWithConditionalWholeEntityReplacement()
        {
            JObject stored = null;
            var fail = false;
            var handler = new Handler(async request =>
            {
                if (request.RequestUri.AbsolutePath == "/Tables")
                    return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}") };
                if (request.Method == HttpMethod.Get)
                {
                    var result = (JObject)stored.DeepClone();
                    result["odata.etag"] = "W/\"version\"";
                    return Json(result);
                }
                if (fail) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                if (stored != null)
                {
                    Assert.AreEqual(HttpMethod.Put, request.Method);
                    Assert.AreEqual("W/\"version\"", request.Headers.IfMatch.Single().ToString());
                }
                stored = JObject.Parse(await request.Content.ReadAsStringAsync());
                Assert.AreEqual("Edm.Binary", (string)stored["Data0@odata.type"]);
                var response = new HttpResponseMessage(HttpStatusCode.NoContent);
                response.Headers.TryAddWithoutValidation("ETag", "W/\"version\"");
                return response;
            });
            var options = new TableClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
            options.Retry.MaxRetries = 0;
            var table = WebJob.Office365ActivityImporter.Engine.ActivityAPI.BlobCheckpoint.CheckpointTableClientFactory.CreateAndEnsureTable(
                "DefaultEndpointsProtocol=https;AccountName=contoso;AccountKey=" + Convert.ToBase64String(new byte[32]) + ";EndpointSuffix=core.windows.net",
                "CopilotCostConnections", Tenant, Client, null, null, options);
            var store = new TableCostDocumentStore(table, Tenant, Client);
            var cache = Enumerable.Range(0, TableCostDocumentStore.ChunkSize + 1).Select(i => (byte)(i % 251)).ToArray();
            var version = await store.WriteAsync("connection", cache, null, Ct);
            CollectionAssert.AreEqual(cache, (await store.ReadAsync("connection", Ct)).Content);
            await store.WriteAsync("connection", new byte[0], version, Ct);
            Assert.AreEqual("", (string)stored["Data1"]);
            fail = true;
            await Fails("busy", () => store.WriteAsync("connection", cache, version, Ct));
        }

        [TestMethod]
        public void ParserPreservesFractionalZeroUnlimitedUnknownCaseAndFreshness()
        {
            var json = EmptyUser();
            json["serviceBalances"] = new JArray(
                new JObject { ["serviceId"] = "cowork", ["spendingPolicyId"] = "policy", ["consumedQuantity"] = 1.25,
                    ["unit"] = "copilotCredit", ["remainingQuantity"] = new JObject { ["@odata.type"] = "#microsoft.graph.unlimitedCostManagementQuantity" } },
                new JObject { ["serviceId"] = "Cowork", ["spendingPolicyId"] = "policy", ["consumedQuantity"] = 0,
                    ["remainingQuantity"] = new JObject { ["@odata.type"] = "#microsoft.graph.futureQuantity" } });
            json["unevaluatedServices"] = new JArray(new JObject { ["serviceId"] = "future", ["reason"] = "futureReason" });
            var result = CostGraphSource.ParseUser(json, UserId, DateTimeOffset.UtcNow);
            Assert.AreEqual(1.25, result.Services[0].ConsumedQuantity);
            Assert.AreEqual(0d, result.Services[1].ConsumedQuantity);
            Assert.IsNull(result.Services[0].RemainingQuantity.Value);
            Assert.AreEqual("#microsoft.graph.futureQuantity", result.Services[1].RemainingQuantity.Type);
            Assert.AreEqual("futureReason", (string)result.UnevaluatedServices[0]["reason"]);
            Assert.IsNotNull(result.SelectionDataStatus);
            Assert.AreEqual(0, CostGraphSource.ParseUser(EmptyUser(), UserId, DateTimeOffset.UtcNow).Services.Count);
        }

        [TestMethod]
        public void ParserRejectsMissingParentMalformedQuantitiesAndDuplicateExactKeys()
        {
            var json = EmptyUser();
            json.Remove("selectionDataStatus");
            Assert.ThrowsException<CostConnectionException>(() => CostGraphSource.ParseUser(json, UserId, DateTimeOffset.UtcNow));
            json = EmptyUser();
            var row = new JObject { ["serviceId"] = "cowork", ["spendingPolicyId"] = "policy", ["consumedQuantity"] = double.NaN };
            json["serviceBalances"] = new JArray(row);
            Assert.ThrowsException<CostConnectionException>(() => CostGraphSource.ParseUser(json, UserId, DateTimeOffset.UtcNow));
            row["consumedQuantity"] = 0;
            json["serviceBalances"] = new JArray(row, row.DeepClone());
            Assert.ThrowsException<CostConnectionException>(() => CostGraphSource.ParseUser(json, UserId, DateTimeOffset.UtcNow));
        }

        [TestMethod]
        public async Task TransportRejectsMalformed200AndDoesNotTreat404AsEmpty()
        {
            foreach (var body in new[] { "", "null", "{", "{\"id\":[],\"serviceBalances\":[],\"unevaluatedServices\":[],\"selectionDataStatus\":{}}" })
            {
                var source = Source(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
                await Fails("invalidResponse", () => source.ReadUserAsync(UserId, Ct));
            }
            await Fails("notFound", () => Source(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).ReadUserAsync(UserId, Ct));
            await Fails("permissionDenied", () => Source(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)).ReadUserAsync(UserId, Ct));
            await Fails("sourceUnavailable", () => Source(_ => throw new HttpRequestException("synthetic transport failure")).ReadUserAsync(UserId, Ct));
            await Fails("invalidResponse", () => Source(_ => new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(new byte[] { 0xff }) }).ReadUserAsync(UserId, Ct));
            var calls = 0;
            var throttled = Source(_ =>
            {
                calls++;
                var response = new HttpResponseMessage((HttpStatusCode)429);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
                return response;
            });
            await Fails("throttled", () => throttled.ReadUserAsync(UserId, Ct));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public async Task PagingRejectsRepeatedLinksAndUntrustedDestinations()
        {
            foreach (var url in new[] { "https://contoso.example/v1.0/users", "https://graph.microsoft.com/v1.0/usersBad",
                "https://graph.microsoft.com/v1.0/users/" + UserId, "http://graph.microsoft.com/v1.0/users" })
                Assert.ThrowsException<CostConnectionException>(() => CostGraphSource.ValidateUrl(url, "/v1.0/users"));
            var path = "/beta/copilot/costManagement/userBalances/" + UserId + "/serviceBalances";
            var next = "https://graph.microsoft.com" + path + "?$skiptoken=test";
            var initial = EmptyUser();
            initial["serviceBalances@odata.nextLink"] = next;
            var calls = 0;
            var source = Source(_ =>
            {
                calls++;
                return Json(calls == 1 ? initial : new JObject { ["value"] = new JArray(), ["@odata.nextLink"] = next });
            });
            await Fails("invalidPage", () => source.ReadUserAsync(UserId, Ct));
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public async Task InventoryResumesWithoutReducingDenominatorAnd404CoverageIsExplicit()
        {
            var store = new MemoryStore();
            var state = new CostConnectionState { Probe = new CostProbeProgress { Phase = "inventory" } };
            var directoryCalls = 0;
            var handler = new Handler(request =>
            {
                if (request.RequestUri.AbsolutePath == "/v1.0/users")
                {
                    directoryCalls++;
                    return Json(new JObject { ["value"] = new JArray(new JObject { ["id"] = UserId }) });
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });
            var runner = new CostProbeRunner(store, new HttpClient(handler));
            await runner.StepAsync(state, "delegated", "app", Ct);
            Assert.AreEqual(1, state.Probe.UserCount);
            Assert.AreEqual("users", state.Probe.Phase);
            var resumed = CostConnectionEncoding.Deserialize<CostConnectionState>(CostConnectionEncoding.Serialize(state));
            await runner.StepAsync(resumed, "delegated", "app", Ct);
            Assert.AreEqual("observed", resumed.Probe.Phase);
            Assert.AreEqual(0, resumed.Probe.Empty);
            Assert.AreEqual(1, resumed.Probe.Failed);
            Assert.AreEqual(1, resumed.Probe.Denied);
            Assert.AreEqual(1, directoryCalls);
        }

        [TestMethod]
        public async Task ExpiredOwnerCannotPublishOverANewerOwnerAndLiveOwnersSerializeRefresh()
        {
            var store = new MemoryStore();
            var now = DateTimeOffset.UtcNow;
            var identity = new Identity();
            var service = Service(store, identity, () => now);
            await Connect(service);
            var other = Service(store, new Identity(), () => now);
            identity.OnRefresh = async () =>
            {
                await Fails("busy", () => other.StartAsync(UserId, "second-browser", Ct));
                now = now.AddMinutes(3);
                await other.StartAsync(UserId, "second-browser", Ct);
            };
            await Fails("busy", () => service.RunWithTokenAsync((_, __, ___) =>
                throw new AssertFailedException("A stale owner must not start Graph reads."), Ct));
            var state = await other.ReadAsync(Ct);
            Assert.AreEqual(CostConnectionEncoding.Hash("second-browser"), state.Pending.BrowserHash);
            Assert.IsNull(state.Owner);
        }

        [TestMethod]
        public async Task CorruptCacheAndFailedPersistenceDoNotAcquireTokensOrFallBack()
        {
            var store = new MemoryStore();
            var identity = new Identity();
            var service = Service(store, identity);
            await Connect(service);
            var doc = await store.ReadAsync("connection", Ct);
            var state = CostConnectionEncoding.Deserialize<CostConnectionState>(doc.Content);
            state.Cache = "not base64";
            await store.WriteAsync("connection", CostConnectionEncoding.Serialize(state), doc.Version, Ct);
            await Fails("storageCorrupt", () => service.RunWithTokenAsync((_, __, ___) => Task.CompletedTask, Ct));
            Assert.IsNull(identity.RefreshedAccount);
            store.FailWrite = _ => true;
            await Fails("storageUnavailable", () => service.StartAsync(UserId, "browser", Ct));
            Assert.AreEqual(1, identity.Redemptions);
            Assert.ThrowsException<CostConnectionException>(() => CostConnectionEncoding.Deserialize<CostConnectionState>(Encoding.UTF8.GetBytes("null")));
        }

        [TestMethod]
        public async Task InterruptedLargeInventoryResumesInBoundedSlicesWithoutLosingMembers()
        {
            var store = new MemoryStore();
            var state = new CostConnectionState { Probe = new CostProbeProgress { Phase = "inventory" } };
            var original = CostConnectionEncoding.Serialize(state);
            var rows = new JArray(Enumerable.Range(1, 100).Select(i =>
                new JObject { ["id"] = new Guid(i, 0, 0, new byte[8]).ToString() }));
            var calls = 0;
            var writes = 0;
            store.FailWrite = key => key.StartsWith("member_", StringComparison.Ordinal) && ++writes == 11;
            var runner = new CostProbeRunner(store, new HttpClient(new Handler(_ =>
            {
                calls++;
                return Json(new JObject { ["value"] = rows });
            })));
            await runner.StepAsync(state, "delegated", "app", Ct);
            Assert.AreEqual("storageUnavailable", state.Probe.LastOutcome);
            state = CostConnectionEncoding.Deserialize<CostConnectionState>(original);
            store.FailWrite = null;
            for (var i = 0; i < 4; i++)
            {
                await runner.StepAsync(state, "delegated", "app", Ct);
                if (i < 3)
                {
                    Assert.AreEqual((i + 1) * 25, state.Probe.InventoryOffset);
                    Assert.AreEqual(0, state.Probe.UserCount);
                    Assert.AreEqual("inventory", state.Probe.Phase);
                }
                state = CostConnectionEncoding.Deserialize<CostConnectionState>(CostConnectionEncoding.Serialize(state));
            }
            Assert.AreEqual("users", state.Probe.Phase);
            Assert.AreEqual(100, state.Probe.UserCount);
            Assert.AreEqual(1, calls);
            var page = await store.ReadAsync("page_" + state.Probe.Id + "_0", Ct);
            Assert.AreEqual(100, CostConnectionEncoding.Deserialize<List<string>>(page.Content).Distinct().Count());
        }

        [TestMethod]
        public async Task AuthorizationPostIsBoundedWithoutContentLengthAndRejectsDuplicateFields()
        {
            foreach (var body in new[] { new string('x', 16385), "state=one&state=two&code=test" })
            {
                var request = new OwinContext().Request;
                request.ContentType = "application/x-www-form-urlencoded";
                request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
                await Fails("invalidCallback", () => AnalyticsWeb::Web.AnalyticsWeb.Startup.ReadCostAuthorizationFormAsync(request, Ct));
            }
            var valid = new OwinContext().Request;
            valid.ContentType = "application/x-www-form-urlencoded; charset=UTF-8";
            valid.Body = new MemoryStream(Encoding.UTF8.GetBytes("state=state&code=code%2Bvalue"));
            var form = await AnalyticsWeb::Web.AnalyticsWeb.Startup.ReadCostAuthorizationFormAsync(valid, Ct);
            Assert.AreEqual("code+value", form["code"]);
        }

        [TestMethod]
        public void IdentityTokenRequiresValidSignatureIssuerAudienceLifetimeNonceTenantAndObjectId()
        {
            using (var rsa = RSA.Create())
            using (var other = RSA.Create())
            {
                rsa.KeySize = other.KeySize = 2048;
                var key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test" };
                var keys = new SecurityKey[] { key };
                Func<string, string, string, DateTime, string> issue = (tenant, audience, nonce, expiry) =>
                    new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                        $"https://login.microsoftonline.com/{tenant}/v2.0", audience,
                        new[] { new Claim("nonce", nonce), new Claim("tid", tenant), new Claim("oid", UserId) },
                        DateTime.UtcNow.AddMinutes(-10), expiry, new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
                var token = issue(Tenant, Client, "nonce", DateTime.UtcNow.AddMinutes(5));
                CostMsalIdentity.ValidateIdentityToken(token, "nonce", Tenant, Client, keys);
                Assert.ThrowsException<CostConnectionException>(() => CostMsalIdentity.ValidateIdentityToken(token, "wrong", Tenant, Client, keys));
                Assert.ThrowsException<SecurityTokenInvalidAudienceException>(() => CostMsalIdentity.ValidateIdentityToken(token, "nonce", Tenant, Tenant, keys));
                Assert.ThrowsException<SecurityTokenInvalidIssuerException>(() => CostMsalIdentity.ValidateIdentityToken(token, "nonce", Client, Client, keys));
                Assert.ThrowsException<SecurityTokenExpiredException>(() =>
                    CostMsalIdentity.ValidateIdentityToken(issue(Tenant, Client, "nonce", DateTime.UtcNow.AddMinutes(-5)), "nonce", Tenant, Client, keys));
                Assert.ThrowsException<SecurityTokenSignatureKeyNotFoundException>(() => CostMsalIdentity.ValidateIdentityToken(token,
                    "nonce", Tenant, Client, new[] { new RsaSecurityKey(other) }));
                foreach (var claims in new[]
                {
                    new[] { new Claim("nonce", "nonce"), new Claim("tid", Client), new Claim("oid", UserId) },
                    new[] { new Claim("nonce", "nonce"), new Claim("tid", Tenant) }
                })
                {
                    var invalidClaims = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
                        $"https://login.microsoftonline.com/{Tenant}/v2.0", Client, claims,
                        DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
                        new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
                    Assert.ThrowsException<CostConnectionException>(() => CostMsalIdentity.ValidateIdentityToken(invalidClaims, "nonce", Tenant, Client, keys));
                }
            }
        }

        [TestMethod]
        public void ManagementRequiresExactOriginAndAntiforgeryBoundToPortalSubject()
        {
            var previous = HttpContext.Current;
            var previousClaim = AntiForgeryConfig.UniqueClaimTypeIdentifier;
            try
            {
                AntiForgeryConfig.UniqueClaimTypeIdentifier = ClaimTypes.NameIdentifier;
                var context = new HttpContext(new HttpRequest("", "https://contoso.example/", ""), new HttpResponse(new StringWriter()));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "synthetic-subject") }, "test"));
                HttpContext.Current = context;
                AntiForgery.GetTokens(null, out var cookie, out var token);
                Action<string, string, string> validate = (origin, c, t) =>
                    AnalyticsWeb::Web.AnalyticsWeb.Controllers.CopilotCostConnectionAPIController.ValidateManagementRequest(
                        "https://contoso.example/copilot-cost/callback", origin, c, t);
                validate("https://contoso.example", cookie, token);
                Assert.ThrowsException<CostConnectionException>(() => validate("https://other.example", cookie, token));
                Assert.ThrowsException<System.Web.Mvc.HttpAntiForgeryException>(() => validate("https://contoso.example", cookie, null));
                context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "different-subject") }, "test"));
                Assert.ThrowsException<System.Web.Mvc.HttpAntiForgeryException>(() => validate("https://contoso.example", cookie, token));
            }
            finally
            {
                HttpContext.Current = previous;
                AntiForgeryConfig.UniqueClaimTypeIdentifier = previousClaim;
            }
        }

        private static JObject EmptyUser() => new JObject { ["id"] = UserId, ["serviceBalances"] = new JArray(),
            ["unevaluatedServices"] = new JArray(), ["selectionDataStatus"] = new JObject() };
        private static HttpResponseMessage Json(JObject json) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json.ToString()) };
        private static CostGraphSource Source(Func<HttpRequestMessage, HttpResponseMessage> response) =>
            new CostGraphSource(new HttpClient(new Handler(response)), "test", (_, __) => Task.CompletedTask);
        private static CostConnectionService Service(MemoryStore store, Identity identity, Func<DateTimeOffset> clock = null) =>
            new CostConnectionService(store, identity, Tenant, Client, "https://contoso.example/copilot-cost/callback", clock);
        private static async Task Stage(CostConnectionService service)
        {
            var url = new Uri(await service.StartAsync(UserId, "browser", Ct));
            await service.StageCallbackAsync("code", HttpUtility.ParseQueryString(url.Query)["state"], false, "browser", Ct);
        }
        private static async Task Connect(CostConnectionService service) { await Stage(service); await service.CompleteAsync(UserId, "browser", Ct); }
        private static async Task Fails(string status, Func<Task> action)
        {
            var error = await Assert.ThrowsExceptionAsync<CostConnectionException>(action);
            Assert.AreEqual(status, error.Status);
        }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _response;
            public Handler(Func<HttpRequestMessage, HttpResponseMessage> response) { _response = request => Task.FromResult(response(request)); }
            public Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) { _response = response; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _response(request);
        }
        private sealed class MemoryStore : ICostDocumentStore
        {
            private readonly Dictionary<string, CostDocument> _data = new Dictionary<string, CostDocument>();
            public Func<string, bool> FailWrite;
            public Task<CostDocument> ReadAsync(string key, CancellationToken ct)
            {
                lock (_data) return Task.FromResult(_data.TryGetValue(key, out var doc)
                    ? new CostDocument { Version = doc.Version, Content = doc.Content.ToArray() } : null);
            }
            public Task<string> WriteAsync(string key, byte[] content, string expectedVersion, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (FailWrite?.Invoke(key) == true) throw new CostConnectionException("storageUnavailable");
                lock (_data)
                {
                    _data.TryGetValue(key, out var old);
                    if (old?.Version != expectedVersion) throw new CostConnectionException("busy");
                    var version = Guid.NewGuid().ToString();
                    _data[key] = new CostDocument { Version = version, Content = content.ToArray() };
                    return Task.FromResult(version);
                }
            }
        }
        private sealed class Identity : ICostIdentity
        {
            public string Account = "reader";
            public string Verifier, Nonce, RefreshedAccount, RefreshFailure;
            public byte[] RefreshedCache;
            public int Redemptions;
            public Func<Task> OnRefresh;
            public Task<CostToken> RedeemAsync(string code, string verifier, string nonce, CancellationToken ct)
            { Verifier = verifier; Nonce = nonce; Redemptions++; return Task.FromResult(Token()); }
            public async Task<CostToken> RefreshAsync(string account, byte[] cache, CancellationToken ct)
            {
                RefreshedAccount = account; RefreshedCache = cache;
                if (OnRefresh != null) await OnRefresh();
                if (RefreshFailure != null) throw new CostConnectionException(RefreshFailure);
                return Token();
            }
            private CostToken Token() => new CostToken { AccessToken = "access", AccountId = Account, TenantId = Tenant, Cache = new byte[] { 1, 2, 3 } };
        }
    }
}
