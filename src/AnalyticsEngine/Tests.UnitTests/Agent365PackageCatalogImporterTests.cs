using Common.Entities.Agent365;
using DataUtils;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph;
using WebJob.Office365ActivityImporter.Engine.Graph.Agent365;

namespace Tests.UnitTests
{
    [TestClass]
    public class Agent365PackageCatalogImporterTests
    {
        private const string FirstPage =
            "{\"value\":[{\"id\":\"package-001\",\"agentIdentityId\":\"00000000-0000-0000-0000-000000000000\"," +
            "\"displayName\":\"Synthetic Agent\",\"type\":\"custom\",\"platform\":\"Copilot Studio\"," +
            "\"publisher\":\"Contoso\",\"manifestId\":\"manifest-001\",\"version\":\"1.0\"," +
            "\"isBlocked\":false,\"lastModifiedDateTime\":\"2026-10-01T12:00:00Z\"," +
            "\"lastUsedDateTime\":null,\"activeUsers\":0,\"totalSessions\":0," +
            "\"totalRunTimeInHours\":0.0,\"exceptionRate\":0.0,\"supportedHosts\":[\"Copilot\"]," +
            "\"elementDetails\":[{\"elementType\":\"DeclarativeAgent\",\"elements\":[{" +
            "\"id\":\"element-001\",\"definition\":\"{\\\"private\\\":\\\"not persisted\\\"}\"}]}]}]," +
            "\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/copilot/admin/catalog/packages?$skiptoken=opaque%2Fvalue\"}";

        private const string SecondPage =
            "{\"value\":[{\"id\":\"package-002\",\"displayName\":\"Unknown usage metric\"}]}";

        [TestMethod]
        public async Task ImportAsync_FollowsOpaqueNextLinkAndPersistsOnlyDocumentedElementIdentifiers()
        {
            var handler = new ResponseHandler(
                HttpStatusCode.OK, FirstPage,
                HttpStatusCode.OK, SecondPage);
            var store = new RecordingStore();
            var importer = CreateImporter(handler, store, AppTokenPermissionAccess.Granted);

            var succeeded = await importer.ImportAsync();

            Assert.IsTrue(succeeded, store.Failure);
            CollectionAssert.AreEqual(
                new[]
                {
                    "https://graph.microsoft.com/v1.0/copilot/admin/catalog/packages",
                    "https://graph.microsoft.com/v1.0/copilot/admin/catalog/packages?$skiptoken=opaque%2Fvalue",
                },
                handler.RequestUrls.ToArray());
            Assert.AreEqual(2, store.Packages.Count);
            Assert.AreEqual(1, store.Packages[0].Elements.Count);
            Assert.AreEqual("element-001", store.Packages[0].Elements[0].ElementId);
            Assert.AreEqual("DeclarativeAgent", store.Packages[0].Elements[0].ElementType);
            Assert.IsTrue(store.Packages[0].LastUsedDateTimeProvided);
            Assert.IsNull(store.Packages[0].LastUsedUtc, "An explicit null lastUsedDateTime is a known never-used value.");
            Assert.IsFalse(store.Packages[1].LastUsedDateTimeProvided, "A missing lastUsedDateTime is unknown, not never used.");
            Assert.AreEqual(2, store.CompletedPackageCount);
            Assert.AreEqual(1, store.CompletedElementCount);
            Assert.IsNull(store.Failure);
        }

        [TestMethod]
        public async Task ImportAsync_FailedPageDoesNotPublishPartialSnapshot()
        {
            var handler = new ResponseHandler(
                HttpStatusCode.OK, FirstPage,
                HttpStatusCode.ServiceUnavailable, "{\"error\":{\"message\":\"synthetic\"}}");
            var store = new RecordingStore();

            var succeeded = await CreateImporter(handler, store, AppTokenPermissionAccess.Granted).ImportAsync();

            Assert.IsFalse(succeeded);
            Assert.AreEqual(1, store.Packages.Count, "First-page records remain staged only.");
            Assert.IsFalse(store.Completed);
            StringAssert.Contains(store.Failure, "503");
            Assert.IsFalse(store.Failure.Contains("synthetic"), "Graph response bodies must not be persisted in the failure reason.");
        }

        [TestMethod]
        public async Task ImportAsync_RejectsPaginationLinksOutsideTheGlobalGraphHost()
        {
            const string pageWithUntrustedNextLink =
                "{\"value\":[],\"@odata.nextLink\":\"https://example.invalid/next\"}";
            var handler = new ResponseHandler(HttpStatusCode.OK, pageWithUntrustedNextLink);
            var store = new RecordingStore();

            var succeeded = await CreateImporter(handler, store, AppTokenPermissionAccess.Granted).ImportAsync();

            Assert.IsFalse(succeeded);
            Assert.AreEqual(1, handler.RequestUrls.Count, "The untrusted next link must not be requested.");
            StringAssert.Contains(store.Failure, "invalid Graph pagination link");
            Assert.IsFalse(store.Completed);
        }

        [TestMethod]
        public async Task ImportAsync_RejectsAnEmptyPaginationLinkInsteadOfPublishingPartialData()
        {
            const string pageWithEmptyNextLink = "{\"value\":[],\"@odata.nextLink\":\"\"}";
            var handler = new ResponseHandler(HttpStatusCode.OK, pageWithEmptyNextLink);
            var store = new RecordingStore();

            var succeeded = await CreateImporter(handler, store, AppTokenPermissionAccess.Granted).ImportAsync();

            Assert.IsFalse(succeeded);
            Assert.AreEqual(1, handler.RequestUrls.Count);
            StringAssert.Contains(store.Failure, "invalid Graph pagination link");
            Assert.IsFalse(store.Completed);
        }

        [TestMethod]
        public async Task ImportAsync_RequiresTheRuntimePermissionBeforeCallingGraph()
        {
            var handler = new ResponseHandler();
            var store = new RecordingStore();

            var succeeded = await CreateImporter(handler, store, AppTokenPermissionAccess.NotGranted).ImportAsync();

            Assert.IsFalse(succeeded);
            Assert.AreEqual(0, handler.RequestUrls.Count);
            StringAssert.Contains(store.Failure, "CopilotPackages.Read.All");
        }

        [TestMethod]
        public async Task ImportAsync_ExplainsLicenseAndPermissionOnForbiddenResponseWithoutSavingBody()
        {
            var handler = new ResponseHandler(HttpStatusCode.Forbidden, "{\"error\":{\"message\":\"synthetic private detail\"}}");
            var store = new RecordingStore();

            var succeeded = await CreateImporter(handler, store, AppTokenPermissionAccess.Granted).ImportAsync();

            Assert.IsFalse(succeeded);
            StringAssert.Contains(store.Failure, "Microsoft Agent 365 license");
            StringAssert.Contains(store.Failure, "CopilotPackages.Read.All");
            Assert.IsFalse(store.Failure.Contains("synthetic private detail"));
        }

        private static Agent365PackageCatalogImporter CreateImporter(
            ResponseHandler handler,
            RecordingStore store,
            AppTokenPermissionAccess permission)
        {
            var logger = AnalyticsLogger.ConsoleOnlyTracer();
            var graphClient = new ManualGraphCallClient(handler, logger) { MaxRetries = 0 };
            return new Agent365PackageCatalogImporter(
                null,
                graphClient,
                store,
                logger,
                _ => Task.FromResult(permission));
        }

        private sealed class ResponseHandler : HttpMessageHandler
        {
            private readonly Queue<Tuple<HttpStatusCode, string>> _responses;

            public ResponseHandler(params object[] responsePairs)
            {
                _responses = new Queue<Tuple<HttpStatusCode, string>>();
                for (var i = 0; i < responsePairs.Length; i += 2)
                {
                    _responses.Enqueue(Tuple.Create((HttpStatusCode)responsePairs[i], (string)responsePairs[i + 1]));
                }
            }

            public List<string> RequestUrls { get; } = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestUrls.Add(request.RequestUri.AbsoluteUri);
                if (_responses.Count == 0)
                {
                    throw new AssertFailedException("Unexpected Graph request.");
                }

                var response = _responses.Dequeue();
                return Task.FromResult(new HttpResponseMessage(response.Item1)
                {
                    Content = new StringContent(response.Item2 ?? string.Empty)
                });
            }
        }

        private sealed class RecordingStore : IAgent365PackageCatalogStore
        {
            public List<Agent365Package> Packages { get; } = new List<Agent365Package>();
            public bool Completed { get; private set; }
            public int CompletedPackageCount { get; private set; }
            public int CompletedElementCount { get; private set; }
            public string Failure { get; private set; }

            public Task<Guid> BeginImportAsync(DateTime startedUtc) => Task.FromResult(Guid.NewGuid());

            public Task SavePageAsync(Guid runId, IReadOnlyList<Agent365Package> packages)
            {
                Packages.AddRange(packages);
                return Task.CompletedTask;
            }

            public Task CompleteImportAsync(Guid runId, DateTime completedUtc, int packageCount, int elementCount)
            {
                Completed = true;
                CompletedPackageCount = packageCount;
                CompletedElementCount = elementCount;
                return Task.CompletedTask;
            }

            public Task FailImportAsync(Guid runId, DateTime completedUtc, string error)
            {
                Failure = error;
                return Task.CompletedTask;
            }

            public Task<Agent365CatalogImportHealth> GetImportHealthAsync() =>
                Task.FromResult(new Agent365CatalogImportHealth());

            public Task<Agent365PackageCatalogPage> GetCurrentPageAsync(int offset, int pageSize, bool neverUsedOnly) =>
                Task.FromResult(new Agent365PackageCatalogPage());
        }
    }
}
