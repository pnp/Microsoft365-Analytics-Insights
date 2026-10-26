extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.Config;
using Common.Entities.PromptCategories;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    public class PromptCategoryTestDriveTests
    {
        private static AppConfig Settings() => new AppConfig
        {
            FoundryPromptCategorisationEndpoint = "https://contoso.openai.azure.com/",
            FoundryPromptCategorisationDeployment = "synthetic-model",
            FoundryPromptCategorisationKey = "synthetic-key"
        };

        private sealed class Backend : IPromptCategoryBackend
        {
            public int Calls;
            public string Prompt;
            public PromptCategoryConfiguration Taxonomy;
            public Func<CancellationToken, Task<PromptCategoryBatchResult>> Respond = _ =>
                Task.FromResult(new PromptCategoryBatchResult { Categories = new[] { "analysis" }, InputTokens = 100, OutputTokens = 10 });
            public Task<PromptCategoryBatchResult> ClassifyAsync(PromptCategoryConfiguration taxonomy,
                IReadOnlyList<string> prompts, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                Taxonomy = taxonomy;
                Assert.AreEqual(1, prompts.Count);
                Prompt = prompts[0];
                return Respond(cancellationToken);
            }
        }

        private static PortalTestHost Host(PromptCategoryConfigurationStore store, Backend backend, AppConfig settings = null)
        {
            var host = new PortalTestHost(new[] { typeof(PromptCategoriesAPIController) },
                PortalTestHost.SignedIn(PortalRoles.Administration), PortalAccessPolicy.Enforcing,
                _ => new PromptCategoriesAPIController(store, settings: settings ?? Settings(), backendFactory: () => backend));
            host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
            host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            return host;
        }

        private static Task<HttpResponseMessage> Post(PortalTestHost host, string prompt, string version) =>
            host.Client.PostAsync("api/PromptCategories/test", new StringContent(
                JsonConvert.SerializeObject(new { prompt, version }), Encoding.UTF8, "application/json"));

        private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            Assert.AreEqual(status, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(code, (string)JObject.Parse(body)["code"]);
            Assert.IsFalse(body.Contains("synthetic-key") || body.Contains("contoso") || body.Contains("Καλημέρα"));
        }

        [TestMethod]
        public async Task TestDrive_UsesSavedTaxonomyEvenWhenDisabled_AndDoesNotPersistSampleOrCounters()
        {
            var kv = new InMemoryKeyValueStore();
            var store = new PromptCategoryConfigurationStore(kv);
            var config = PromptCategoryConfiguration.Defaults();
            config.Categories[4].Name = "Contoso analysis";
            await store.SaveAsync(config, (await store.GetAsync()).Revision);
            var before = await kv.GetStringAsync("current");
            var backend = new Backend();
            using (var host = Host(store, backend))
            {
                var response = await Post(host, "Καλημέρα: compare synthetic options.", config.Version);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl.NoStore);
                var body = await response.Content.ReadAsStringAsync();
                var result = JObject.Parse(body);
                Assert.AreEqual("analysis", (string)result["category"]["id"]);
                Assert.AreEqual("Contoso analysis", (string)result["category"]["name"]);
                Assert.AreEqual("directing", (string)result["category"]["humanMode"]);
                Assert.AreEqual(config.Version, (string)result["version"]);
                Assert.AreEqual(100, (int)result["inputTokens"]);
                Assert.AreEqual(10, (int)result["outputTokens"]);
                Assert.AreEqual("Καλημέρα: compare synthetic options.", backend.Prompt);
                Assert.IsFalse(backend.Taxonomy.Enabled);
                Assert.IsFalse(body.Contains("Καλημέρα") || body.Contains("synthetic-key") || body.Contains("openai.azure.com"));
                Assert.AreEqual(before, await kv.GetStringAsync("current"));
                Assert.IsNull(await kv.GetStringAsync("runs"));
            }
        }

        [TestMethod]
        public async Task TestDrive_RequiresAdminAndSameOriginBeforeSendingAnything()
        {
            var store = new PromptCategoryConfigurationStore(new InMemoryKeyValueStore());
            var config = await store.GetAsync();
            var backend = new Backend();
            using (var host = Host(store, backend))
            {
                host.Principal = PortalTestHost.SignedIn();
                Assert.AreEqual(HttpStatusCode.Forbidden, (await Post(host, "Synthetic prompt", config.Version)).StatusCode);
                host.Principal = PortalTestHost.SignedIn(PortalRoles.Administration);
                host.Client.DefaultRequestHeaders.Remove("X-Requested-With");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await Post(host, "Synthetic prompt", config.Version)).StatusCode);
                host.Client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
                host.Client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
                host.Client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "cross-site");
                Assert.AreEqual(HttpStatusCode.Forbidden, (await Post(host, "Synthetic prompt", config.Version)).StatusCode);
                Assert.AreEqual(0, backend.Calls);
            }
        }

        [TestMethod]
        public async Task TestDrive_RejectsInvalidInputAndStaleTaxonomyBeforeCallingModel()
        {
            var store = new PromptCategoryConfigurationStore(new InMemoryKeyValueStore());
            var config = await store.GetAsync();
            var backend = new Backend();
            using (var host = Host(store, backend))
            {
                foreach (var prompt in new[] { null, "", " \t\r\n", new string('x', 5001) })
                    await AssertError(await Post(host, prompt, config.Version), HttpStatusCode.BadRequest, "invalidTestPrompt");
                await AssertError(await Post(host, "Synthetic prompt", "old-version"), HttpStatusCode.Conflict, "taxonomyChanged");
                Assert.AreEqual(0, backend.Calls);
                Assert.AreEqual(HttpStatusCode.OK, (await Post(host, new string('x', 5000), config.Version)).StatusCode);
                Assert.AreEqual(5000, backend.Prompt.Length);
            }
            using (var host = Host(store, backend, new AppConfig { FoundryPromptCategorisationEndpoint = "", FoundryPromptCategorisationDeployment = "" }))
                await AssertError(await Post(host, "Synthetic prompt", config.Version), HttpStatusCode.Conflict, "backendNotConfigured");
            Assert.AreEqual(1, backend.Calls);
        }

        [DataTestMethod]
        [DataRow("throttled", "testThrottled", 429)]
        [DataRow("access-denied", "testAccessDenied", 503)]
        [DataRow("timeout", "testTimeout", 503)]
        [DataRow("refused", "testRefused", 503)]
        [DataRow("invalid-response", "testInvalidResponse", 503)]
        [DataRow("service-failure", "testServiceFailure", 503)]
        public async Task TestDrive_ReportsModelFailuresWithoutSuccessShapedFallback(string failure, string code, int status)
        {
            var store = new PromptCategoryConfigurationStore(new InMemoryKeyValueStore());
            var config = await store.GetAsync();
            var backend = new Backend { Respond = _ => Task.FromResult(new PromptCategoryBatchResult { Failure = failure }) };
            using (var host = Host(store, backend))
                await AssertError(await Post(host, "Synthetic prompt", config.Version), (HttpStatusCode)status, code);
        }

        [TestMethod]
        public async Task TestDrive_RejectsInvalidResults_ButOtherIsAValidCategory()
        {
            var store = new PromptCategoryConfigurationStore(new InMemoryKeyValueStore());
            var config = await store.GetAsync();
            var backend = new Backend();
            using (var host = Host(store, backend))
            {
                foreach (var result in new[] {
                    null, new PromptCategoryBatchResult(),
                    new PromptCategoryBatchResult { Categories = new[] { "unknown" } },
                    new PromptCategoryBatchResult { Categories = new[] { "analysis", "other" } },
                    new PromptCategoryBatchResult { Categories = new[] { "other" }, ResponseWasInvalid = true } })
                {
                    backend.Respond = _ => Task.FromResult(result);
                    await AssertError(await Post(host, "Synthetic prompt", config.Version), HttpStatusCode.BadGateway, "testInvalidResponse");
                }
                backend.Respond = _ => Task.FromResult(new PromptCategoryBatchResult { Categories = new[] { "other" } });
                Assert.AreEqual(HttpStatusCode.OK, (await Post(host, "Synthetic prompt", config.Version)).StatusCode);
                backend.Respond = _ => throw new InvalidOperationException("Do not echo Καλημέρα or synthetic-key");
                await AssertError(await Post(host, "Synthetic prompt", config.Version), HttpStatusCode.ServiceUnavailable, "testServiceFailure");
            }
        }

        [TestMethod]
        public async Task TestDrive_BoundsConcurrentModelCalls_AndReleasesSlots()
        {
            var store = new PromptCategoryConfigurationStore(new InMemoryKeyValueStore());
            var config = await store.GetAsync();
            var complete = new TaskCompletionSource<PromptCategoryBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var backend = new Backend { Respond = _ => complete.Task };
            using (var host = Host(store, backend))
            {
                var first = Post(host, "Synthetic first prompt", config.Version);
                var second = Post(host, "Synthetic second prompt", config.Version);
                try
                {
                    var deadline = DateTime.UtcNow.AddSeconds(5);
                    while (Volatile.Read(ref backend.Calls) < 2 && DateTime.UtcNow < deadline) await Task.Delay(10);
                    Assert.AreEqual(2, backend.Calls);
                    await AssertError(await Post(host, "Synthetic third prompt", config.Version), (HttpStatusCode)429, "testBusy");
                }
                finally { complete.TrySetResult(new PromptCategoryBatchResult { Categories = new[] { "analysis" } }); }
                Assert.AreEqual(HttpStatusCode.OK, (await first).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await second).StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, (await Post(host, "Synthetic final prompt", config.Version)).StatusCode);
            }
        }
    }
}
