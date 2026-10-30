using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.InstallerTasks;
using App.ControlPanel.Engine.Prerequisites;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Models;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class AzureResourceProviderPreflightTests
    {
        [TestMethod]
        public void RequiredProviders_FollowProvisioningOptionsAndDeduplicateSharedAiProvider()
        {
            var config = MinimalConfig();
            CollectionAssert.AreEquivalent(new[] { "Microsoft.Web", "Microsoft.Sql", "Microsoft.Storage",
                "Microsoft.KeyVault", "Microsoft.Insights", "Microsoft.OperationalInsights" },
                AzureResourceProviderPreflight.RequiredProviders(config).ToArray());

            var options = new Dictionary<string, Action<bool>>
            {
                ["Microsoft.ServiceBus"] = enabled => config.ServiceBusEnabled = enabled,
                ["Microsoft.CognitiveServices"] = enabled => config.CognitiveServicesEnabled = enabled,
                ["Microsoft.Automation"] = enabled => config.SolutionConfig.ImportTaskSettings.GraphUsageReports = enabled,
                ["Microsoft.Network"] = enabled => config.NetworkConfig.Enabled = enabled
            };
            foreach (var option in options)
            {
                option.Value(true);
                var required = AzureResourceProviderPreflight.RequiredProviders(config);
                Assert.AreEqual(7, required.Count);
                Assert.IsTrue(required.Contains(option.Key));
                StringAssert.Contains(DeploymentPrerequisites.Build(config).Requirements
                    .Single(r => r.Id == "check-provider-registration").AdminAction, option.Key);
                option.Value(false);
            }
            config.FoundryPromptEnabled = true;
            Assert.IsTrue(AzureResourceProviderPreflight.RequiredProviders(config).Contains("Microsoft.CognitiveServices"));
            config.CognitiveServicesEnabled = true;
            Assert.AreEqual(1, AzureResourceProviderPreflight.RequiredProviders(config).Count(p => p == "Microsoft.CognitiveServices"));
            config.SolutionConfig.ImportTaskSettings.GraphCopilotUsageReports = true;
            Assert.IsFalse(AzureResourceProviderPreflight.RequiredProviders(config).Contains("Microsoft.Automation"),
                "Copilot usage reports do not provision an Automation account.");
            config.SolutionConfig.ImportTaskSettings.Calls = true;
            Assert.IsTrue(AzureResourceProviderPreflight.RequiredProviders(config).Contains("Microsoft.ServiceBus"),
                "Calls require Service Bus even when the configuration still needs its provisioning toggle enabled.");
        }

        [DataTestMethod]
        [DataRow("Registered", true, LogLevel.Information)]
        [DataRow("registered", true, LogLevel.Information)]
        [DataRow("Registering", true, LogLevel.Warning)]
        [DataRow("NotRegistered", false, LogLevel.Error)]
        [DataRow("Unregistered", false, LogLevel.Error)]
        [DataRow("Unregistering", false, LogLevel.Error)]
        [DataRow(null, true, LogLevel.Warning)]
        [DataRow("Unknown", true, LogLevel.Warning)]
        public async Task SdkChecks_AreReadOnlyAndDistinguishMissingPendingAndUnknownStates(string state, bool canDeploy, LogLevel level)
        {
            using (var fixture = new Fixture())
            {
                fixture.Handler.States["Microsoft.Sql"] = state;
                Assert.AreEqual(canDeploy, await fixture.Check());
                CollectionAssert.AreEquivalent(AzureResourceProviderPreflight.RequiredProviders(fixture.Config).ToArray(),
                    fixture.Handler.Providers.ToArray());
                Assert.IsTrue(fixture.Logger.Messages.Any(m => m.Item1 == level && m.Item2.Contains("'Microsoft.Sql'")));
                if (!canDeploy)
                {
                    var message = fixture.Logger.Messages.Single(m => m.Item1 == LogLevel.Error).Item2;
                    StringAssert.Contains(message, "az provider register --namespace Microsoft.Sql --subscription " + Guid.Empty);
                    StringAssert.Contains(message, "Microsoft.Sql/register/action");
                }
                if (state == "Registering")
                    StringAssert.Contains(string.Join("\n", fixture.Logger.Messages.Select(m => m.Item2)), "deployment region");
            }
        }

        [TestMethod]
        public async Task MissingProviders_StopBackendBeforeResourceDiscoveryOrMutationAndReportEveryFailure()
        {
            using (var fixture = new Fixture())
            {
                fixture.Handler.States["Microsoft.Sql"] = "NotRegistered";
                fixture.Handler.States["Microsoft.Storage"] = "NotRegistered";
                var config = fixture.Config;
                config.Subscription = new AzureSubscription(Guid.Empty.ToString(), "Contoso");
                config.InstallerAccount = new AppRegistrationCredentials(Guid.Empty.ToString(), "synthetic-secret", Guid.Empty.ToString());
                config.ResourceGroupName = "contoso";
                config.AzureLocationName = "westeurope";
                config.AppServiceWebAppName = "contoso-web";
                config.SQLServerName = "contoso-sql";
                config.SQLServerDatabaseName = "contoso-analytics";
                config.KeyVaultName = "contoso-vault";
                config.StorageAccountName = "contosoanalytics";
                config.AppInsightsName = "contoso-insights";
                config.AppInsightsWorkspaceName = "contoso-workspace";
                var job = new AzurePaaSInstallJob(fixture.Logger, config, fixture.Subscription);

                var exception = await Assert.ThrowsExceptionAsync<InstallException>(() => job.Install());
                StringAssert.Contains(exception.Message, "before changing Azure resources");
                Assert.AreEqual(2, fixture.Logger.Messages.Count(m => m.Item1 == LogLevel.Error));
                Assert.AreEqual(6, fixture.Handler.Providers.Count,
                    "Every missing provider must be reported in one pass, without starting backend resource calls.");
            }
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.Forbidden)]
        [DataRow(HttpStatusCode.ServiceUnavailable)]
        public async Task FailedReads_WarnRatherThanClaimingRegistrationIsMissing(HttpStatusCode status)
        {
            using (var fixture = new Fixture())
            {
                fixture.Handler.FailedStatus = status;
                Assert.IsTrue(await fixture.Check());
                Assert.IsFalse(fixture.Logger.Messages.Any(m => m.Item1 == LogLevel.Error));
                Assert.IsTrue(fixture.Logger.Messages.Any(m => m.Item1 == LogLevel.Warning
                    && m.Item2.Contains("unverified, not missing")));
            }
        }

        [TestMethod]
        public async Task Cancellation_InterruptsTheReadAndPreventsFurtherProviderCalls()
        {
            using (var fixture = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.Handler.WaitForCancellation = true;
                var check = fixture.Check(cancellation.Token);
                cancellation.Cancel();
                try { await check; Assert.Fail("Expected cancellation."); }
                catch (OperationCanceledException) { }
                Assert.AreEqual(1, fixture.Handler.Providers.Count);
            }
        }

        private static SolutionInstallConfig MinimalConfig() => new SolutionInstallConfig
        {
            ServiceBusEnabled = false, CognitiveServicesEnabled = false, FoundryPromptEnabled = false,
            HealthMonitoring = new HealthMonitoringConfig { Enabled = false }
        };

        private sealed class Fixture : IDisposable
        {
            public readonly ProviderHandler Handler = new ProviderHandler();
            public readonly RecordingLogger Logger = new RecordingLogger();
            public readonly SolutionInstallConfig Config = MinimalConfig();
            public readonly SubscriptionResource Subscription;
            private readonly HttpClient _http;

            public Fixture()
            {
                _http = new HttpClient(Handler);
                var options = new ArmClientOptions { Transport = new HttpClientTransport(_http) };
                options.Retry.MaxRetries = 0;
                Subscription = new ArmClient(new SyntheticCredential(), Guid.Empty.ToString(), options)
                    .GetSubscriptionResource(new ResourceIdentifier("/subscriptions/" + Guid.Empty));
            }
            public Task<bool> Check(CancellationToken token = default(CancellationToken)) =>
                AzureResourceProviderPreflight.CheckAsync(Subscription, Config, Logger, token);
            public void Dispose() => _http.Dispose();
        }

        private sealed class ProviderHandler : HttpMessageHandler
        {
            public readonly Dictionary<string, string> States = new Dictionary<string, string>();
            public readonly List<string> Providers = new List<string>();
            public HttpStatusCode? FailedStatus;
            public bool WaitForCancellation;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Assert.AreEqual(HttpMethod.Get, request.Method, "Provider preflight must never mutate Azure.");
                var provider = request.RequestUri.Segments.Last();
                Assert.AreEqual("/subscriptions/" + Guid.Empty + "/providers/" + provider, request.RequestUri.AbsolutePath,
                    "No backend resource discovery or provisioning may precede successful preflight.");
                Providers.Add(provider);
                if (WaitForCancellation) await Task.Delay(Timeout.Infinite, cancellationToken);
                var payload = FailedStatus.HasValue
                    ? new JObject { ["error"] = new JObject { ["code"] = "SyntheticFailure", ["message"] = "Synthetic provider read failure" } }
                    : new JObject { ["id"] = request.RequestUri.AbsolutePath, ["namespace"] = provider,
                        ["registrationState"] = States.TryGetValue(provider, out var state) ? state : "Registered" };
                return new HttpResponseMessage(FailedStatus ?? HttpStatusCode.OK)
                {
                    Content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class RecordingLogger : ILogger
        {
            public readonly List<Tuple<LogLevel, string>> Messages = new List<Tuple<LogLevel, string>>();
            public IDisposable BeginScope<TState>(TState state) => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
                Messages.Add(Tuple.Create(logLevel, formatter(state, exception)));
        }
    }
}
