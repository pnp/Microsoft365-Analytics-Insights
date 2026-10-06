using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
