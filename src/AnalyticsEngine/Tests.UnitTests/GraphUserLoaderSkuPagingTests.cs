using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Common.Entities.Config;
using DataUtils;
using System.Data.Entity;
using Tests.UnitTests.FakeLoaderClasses;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Issue #707: the licence refresh listed each SKU's holders with no <c>$top</c>, so Graph paged them 100 at a
    /// time - one sequential request per 100 holders of a SKU, every cycle. These tests drive the real
    /// <see cref="GraphUserLoader.LoadUsersBySku"/> through the Graph SDK over a scripted transport and pin the
    /// page size, that paging still follows <c>@odata.nextLink</c>, and that the issue #392 "no answer is not an
    /// empty answer" guard still holds.
    /// </summary>
    [TestClass]
    public class GraphUserLoaderSkuPagingTests
    {
        private static readonly Guid SkuId = Guid.Empty;

        private const string NextLink =
            "https://graph.microsoft.com/v1.0/users?$filter=assignedLicenses%2Fany(u%3Au%2FskuId+eq+00000000-0000-0000-0000-000000000000)" +
            "&$select=userPrincipalName&$top=999&$skiptoken=synthetic-page-2";

        private const string CurrentId = "00000000-0000-0000-0000-000000000001";
        private const string PreviousId = "00000000-0000-0000-0000-000000000002";
        private const string CurrentJson = "{\"id\":\"" + CurrentId + "\",\"userPrincipalName\":\"reused@contoso.com\",\"accountEnabled\":true}";
        private const string NotFoundJson = "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"synthetic missing object\"}}";

        [TestMethod]
        public async Task ReusedUpn_FreshOwnerAndCorroboratedAbsence_UsesOnlyTheConflictingPair()
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, CurrentJson)
                .Then(HttpStatusCode.NotFound, NotFoundJson)
                .Then(HttpStatusCode.OK, "{\"value\":[" + CurrentJson + "]}");
            Assert.IsTrue(await NewLoader(handler).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
            Assert.AreEqual(3, handler.Requests.Count);
            Assert.AreEqual("/v1.0/users/" + CurrentId, handler.Requests[0].AbsolutePath);
            Assert.AreEqual("/v1.0/users/" + PreviousId, handler.Requests[1].AbsolutePath);
            var query = Uri.UnescapeDataString(handler.Requests[2].Query.Replace("+", "%20"));
            StringAssert.Contains(query, "id eq '" + CurrentId + "'");
            StringAssert.Contains(query, "id eq '" + PreviousId + "'");
            StringAssert.Contains(query, "userPrincipalName eq 'reused@contoso.com'");
            StringAssert.Contains(query, "$select=id,userPrincipalName,accountEnabled");
        }

        [DataTestMethod]
        [DataRow("{\"value\":[]}")]
        [DataRow("{}")]
        [DataRow("{\"value\":[" + CurrentJson + "],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/users?$skiptoken=synthetic\"}")]
        [DataRow("{\"value\":[" + CurrentJson + ",{\"id\":\"" + PreviousId + "\",\"accountEnabled\":false}]}")]
        [DataRow("{\"value\":[" + CurrentJson + ",{\"id\":\"00000000-0000-0000-0000-000000000003\",\"userPrincipalName\":\"reused@contoso.com\"}]}")]
        public async Task ReusedUpn_User404Alone_PartialOrAmbiguousDirectoryAnswer_KeepsQuarantine(string directoryJson)
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, CurrentJson)
                .Then(HttpStatusCode.NotFound, NotFoundJson)
                .Then(HttpStatusCode.OK, directoryJson);
            Assert.IsFalse(await NewLoader(handler).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(503)]
        [DataRow(404)]
        public async Task ReusedUpn_CurrentOwnerReadFails_KeepsQuarantine(int status)
        {
            var handler = new ScriptedGraphHandler().Then((HttpStatusCode)status, NotFoundJson);
            Assert.IsFalse(await NewLoader(handler).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
            Assert.AreEqual(1, handler.Requests.Count);
        }

        [DataTestMethod]
        [DataRow(403)]
        [DataRow(503)]
        [DataRow(200)]
        public async Task ReusedUpn_OldOwnerUnreadableOrStillLiveEvenDisabled_KeepsQuarantine(int status)
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, CurrentJson)
                .Then((HttpStatusCode)status, status == 200 ? "{\"id\":\"" + PreviousId + "\",\"accountEnabled\":false}" : NotFoundJson);
            Assert.IsFalse(await NewLoader(handler).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
            Assert.AreEqual(2, handler.Requests.Count);
        }

        [TestMethod]
        public async Task ReusedUpn_RequiresBothFreshObjectReadsAndSuccessfulDirectoryCorroboration()
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, CurrentJson)
                .Then(HttpStatusCode.NotFound, NotFoundJson)
                .Then(HttpStatusCode.Forbidden, NotFoundJson);
            Assert.IsFalse(await NewLoader(handler).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
            Assert.AreEqual(3, handler.Requests.Count);
            var changed = new ScriptedGraphHandler().Then(HttpStatusCode.OK, CurrentJson.Replace("reused@", "changed@"));
            Assert.IsFalse(await NewLoader(changed).VerifyReusedUpnAsync(CurrentOwner(), PreviousId));
        }

        private static GraphUser CurrentOwner() => new GraphUser { Id = CurrentId, UserPrincipalName = "reused@contoso.com", AccountEnabled = true };

        [TestMethod]
        public async Task ReusedUpn_PriorDeletionButOldObjectRestoredUnderAnotherName_DeclinesAndPreservesRows()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var owner = CurrentOwner();
            owner.UserPrincipalName = "restored-" + suffix + "@contoso.com";
            var sourceName = "restored-" + suffix + "@contoso.onmicrosoft.com";
            int sourceId, previousId;
            using (var db = new Common.Entities.AnalyticsEntitiesContext())
            {
                var source = new Common.Entities.User { UserPrincipalName = sourceName, AzureAdId = CurrentId, AccountEnabled = true };
                var previous = new Common.Entities.User { UserPrincipalName = owner.UserPrincipalName, AzureAdId = PreviousId, AccountEnabled = false };
                db.users.AddRange(new[] { source, previous });
                await db.SaveChangesAsync();
                sourceId = source.ID; previousId = previous.ID;
            }
            var handler = new RestoredOwnerHandler(owner.UserPrincipalName);
            var graph = new GraphServiceClient(new HttpClient(handler), new BearerTokenAuthenticationProvider("test-token"));
            var verifier = new GraphUserLoader(null, null, NullLogger.Instance, graph);
            var loader = new FakeUserMetadataLoader(new List<GraphUser>(), new List<Microsoft.Graph.Models.SubscribedSku>());
            await loader.DeltaValueProvider.SetDeltaToken("synthetic-checkpoint");
            loader.DeltaUsersOverride = new List<GraphUser>
            {
                new GraphUser { Id = PreviousId, Removed = JObject.Parse("{\"reason\":\"deleted\"}") }, owner,
            };
            loader.ReusedUpnVerification = (current, previous) =>
            {
                Assert.IsTrue(loader.LastLoadDeactivations.RemovedObjectIds.Contains(previous),
                    "The prior deletion event must not bypass a fresh old-object read.");
                return verifier.VerifyReusedUpnAsync(current, previous);
            };
            Assert.IsTrue(await new UserMetadataUpdater(AnalyticsLogger.ConsoleOnlyTracer(), new AppConfig(), loader).InsertAndUpdateDatabaseFromExternalUsers());
            Assert.IsTrue(handler.OldObjectRead);
            Assert.IsFalse(handler.CollectionRead, "A live old object refuses retirement before the lagging collection can mislead it.");
            using (var db = new Common.Entities.AnalyticsEntitiesContext())
            {
                Assert.AreEqual(sourceName, (await db.users.SingleAsync(u => u.ID == sourceId)).UserPrincipalName);
                var previous = await db.users.SingleAsync(u => u.ID == previousId);
                Assert.AreEqual(owner.UserPrincipalName, previous.UserPrincipalName);
                Assert.AreEqual(PreviousId, previous.AzureAdId);
            }
        }

        private sealed class RestoredOwnerHandler : HttpMessageHandler
        {
            private readonly string _currentName;
            public RestoredOwnerHandler(string currentName) { _currentName = currentName; }
            public bool OldObjectRead { get; private set; }
            public bool CollectionRead { get; private set; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var current = CurrentJson.Replace("reused@contoso.com", _currentName);
                string body;
                if (request.RequestUri.AbsolutePath == "/v1.0/users/" + CurrentId)
                    body = current;
                else if (request.RequestUri.AbsolutePath == "/v1.0/users/" + PreviousId)
                {
                    OldObjectRead = true;
                    body = "{\"id\":\"" + PreviousId + "\",\"userPrincipalName\":\"restored-old@contoso.com\",\"accountEnabled\":false}";
                }
                else if (request.RequestUri.AbsolutePath == "/v1.0/users")
                {
                    CollectionRead = true;
                    body = "{\"value\":[" + current + "]}";
                }
                else
                    throw new InvalidOperationException("Unexpected synthetic Graph request.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }
        }

        [TestMethod]
        public async Task LoadUsersBySku_RequestsTheMaximumPageSize_AndFollowsNextLinkToTheEnd()
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, Page(NextLink, "alice@contoso.com", "bob@contoso.com"))
                .Then(HttpStatusCode.OK, Page(null, "carol@contoso.com"));

            var users = await NewLoader(handler).LoadUsersBySku(SkuId);

            CollectionAssert.AreEqual(new[] { "alice@contoso.com", "bob@contoso.com", "carol@contoso.com" },
                users.Select(u => u.UserPrincipalName).ToArray(), "Every page's users, in order.");

            Assert.AreEqual(2, handler.Requests.Count, "One request per page: the first, then the nextLink.");

            var first = handler.Requests[0];
            Assert.AreEqual("/v1.0/users", first.AbsolutePath);
            var firstQuery = Uri.UnescapeDataString(first.Query.Replace("+", "%20"));
            StringAssert.Contains(firstQuery, "$top=999", "999 is the documented maximum page size for GET /users.");
            StringAssert.Contains(firstQuery, "$select=userPrincipalName");
            StringAssert.Contains(firstQuery, $"$filter=assignedLicenses/any(u:u/skuId eq {SkuId})");

            Assert.AreEqual(Uri.UnescapeDataString(new Uri(NextLink).AbsoluteUri), Uri.UnescapeDataString(handler.Requests[1].AbsoluteUri),
                "Page 2 must be Graph's nextLink, followed as-is.");
        }

        [TestMethod]
        public async Task LoadUsersBySku_ASkuNobodyHolds_IsOneRequestAndAnEmptyList()
        {
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.OK, Page(null));

            var users = await NewLoader(handler).LoadUsersBySku(SkuId);

            Assert.AreEqual(0, users.Count);
            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task LoadUsersBySku_NoResponseBody_StillThrowsRatherThanReportingNobody()
        {
            // Issue #392: a missing answer must never be read as "nobody holds this SKU" - the licence refresh
            // would then delete every assignment for it. The page-size change must not weaken that.
            var handler = new ScriptedGraphHandler().Then(HttpStatusCode.NoContent, null);

            var ex = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => NewLoader(handler).LoadUsersBySku(SkuId));

            StringAssert.Contains(ex.Message, SkuId.ToString());
            Assert.AreEqual(1, handler.Requests.Count);
        }

        [TestMethod]
        public async Task LoadUsersBySku_AFailedPageTwo_Throws_InsteadOfReturningTheFirstPage()
        {
            var handler = new ScriptedGraphHandler()
                .Then(HttpStatusCode.OK, Page(NextLink, "alice@contoso.com"))
                .Then(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"generalException\",\"message\":\"synthetic\"}}");

            Exception thrown = null;
            try
            {
                await NewLoader(handler).LoadUsersBySku(SkuId);
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            Assert.IsNotNull(thrown, "A partial list of holders must fail the import, never be reconciled against.");
            Assert.AreEqual(2, handler.Requests.Count);
        }

        private static GraphUserLoader NewLoader(ScriptedGraphHandler handler)
        {
            // The plain HttpClient has no retry middleware, so every scripted response is seen exactly once.
            var graph = new GraphServiceClient(new HttpClient(handler), new BearerTokenAuthenticationProvider("test-token"));
            return new GraphUserLoader(null, null, NullLogger.Instance, graph);
        }

        private static string Page(string nextLink, params string[] upns)
        {
            var value = string.Join(",", upns.Select(upn => "{\"userPrincipalName\":\"" + upn + "\"}"));
            var next = nextLink == null ? string.Empty : "\"@odata.nextLink\":\"" + nextLink + "\",";
            return "{" + next + "\"value\":[" + value + "]}";
        }

        /// <summary>
        /// Plays back scripted responses in order and records every URI requested. An unscripted request fails the
        /// test rather than being answered, so "exactly N requests" can't pass by accident.
        /// </summary>
        private sealed class ScriptedGraphHandler : HttpMessageHandler
        {
            private readonly object _gate = new object();
            private readonly Queue<Tuple<HttpStatusCode, string>> _responses = new Queue<Tuple<HttpStatusCode, string>>();
            private readonly List<Uri> _requests = new List<Uri>();

            public IReadOnlyList<Uri> Requests
            {
                get { lock (_gate) { return _requests.ToList(); } }
            }

            public ScriptedGraphHandler Then(HttpStatusCode status, string jsonBody)
            {
                _responses.Enqueue(Tuple.Create(status, jsonBody));
                return this;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Tuple<HttpStatusCode, string> next;
                lock (_gate)
                {
                    _requests.Add(request.RequestUri);
                    next = _responses.Count > 0 ? _responses.Dequeue() : null;
                }

                if (next == null)
                {
                    return Task.FromException<HttpResponseMessage>(new InvalidOperationException($"Unscripted Graph request: {request.RequestUri}"));
                }

                var response = new HttpResponseMessage(next.Item1)
                {
                    RequestMessage = request,
                    Content = new StringContent(next.Item2 ?? string.Empty, Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(response);
            }
        }
    }
}
