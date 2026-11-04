using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
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
    public class VNetPreservationTests
    {
        private const string GroupId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/Contoso";
        private const string VNetId = GroupId + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet";
        private const string IntegrationName = "contoso-integration";
        private const string EndpointName = "contoso-endpoints";
        private const string IntegrationPrefix = "10.20.30.0/26";
        private const string EndpointPrefix = "10.20.30.64/27";

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        [DataRow(true, true)]
        public async Task ExistingSubnets_AreReadOnlyEvenWithStaleConfigAndCustomNetworkSettings(bool privateSubnet, bool pluralPrefixes)
        {
            using (var handler = new NetworkHandler { PrivateSubnet = privateSubnet, PluralPrefixes = pluralPrefixes })
            using (var http = new HttpClient(handler))
            {
                var logger = new RecordingLogger();
                var task = CreateTask(http, logger, "10.0.2.0/24", "10.0.0.0/16", "10.0.0.0/24");
                var result = await task.ExecuteTaskReturnResult(null);

                Assert.AreEqual(VNetId, result.Id.ToString());
                CollectionAssert.AreEqual(new[] { "GET " + VNetId, "GET " + VNetId + "/subnets/" + IntegrationName },
                    handler.Requests.ToArray(), "Reusing existing subnets must not issue a VNet or subnet write.");
                var warning = logger.Messages.Single(m => m.Item1 == LogLevel.Warning).Item2;
                StringAssert.Contains(warning, IntegrationPrefix);
                StringAssert.Contains(warning, "without changing its address ranges or network settings");
                Assert.IsFalse(warning.Contains("To change, delete"));
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task MatchingExistingPrefix_DoesNotWarnForEitherArmRepresentation(bool pluralPrefixes)
        {
            using (var handler = new NetworkHandler { PluralPrefixes = pluralPrefixes })
            using (var http = new HttpClient(handler))
            {
                var logger = new RecordingLogger();
                await CreateTask(http, logger).ExecuteTaskReturnResult(null);
                Assert.IsFalse(logger.Messages.Any(m => m.Item1 == LogLevel.Warning));
                Assert.IsTrue(handler.Requests.All(r => r.StartsWith("GET ", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public async Task NewVNet_UsesConfiguredIpv4RangesAndDelegatesOnlyIntegrationSubnet()
        {
            var config = new VNetConfig
            {
                Enabled = true, VNetName = "contoso-vnet", AddressPrefix = "10.20.30.0/24",
                SubnetName = EndpointName, SubnetAddressPrefix = EndpointPrefix,
                AppServiceIntegrationSubnetName = IntegrationName,
                AppServiceIntegrationSubnetAddressPrefix = IntegrationPrefix
            };
            Assert.AreEqual(0, config.ValidatInputAndGetErrors().Count);
            using (var handler = new NetworkHandler { MissingVNet = true })
            using (var http = new HttpClient(handler))
            {
                await CreateTask(http, new RecordingLogger()).ExecuteTaskReturnResult(null);
                var properties = handler.CreatedVNet["properties"];
                Assert.AreEqual("10.20.30.0/24", (string)properties["addressSpace"]["addressPrefixes"][0]);
                var subnets = (JArray)properties["subnets"];
                Assert.AreEqual(2, subnets.Count);
                var endpoints = subnets.Single(s => (string)s["name"] == EndpointName)["properties"];
                var integration = subnets.Single(s => (string)s["name"] == IntegrationName)["properties"];
                Assert.AreEqual(EndpointPrefix, (string)endpoints["addressPrefix"]);
                Assert.AreEqual(IntegrationPrefix, (string)integration["addressPrefix"]);
                Assert.AreEqual("Microsoft.Web/serverFarms", (string)integration["delegations"][0]["properties"]["serviceName"]);
                Assert.IsTrue(endpoints["delegations"] == null || !endpoints["delegations"].HasValues);
                CollectionAssert.AreEqual(new[] { "GET " + VNetId, "PUT " + VNetId }, handler.Requests.ToArray());
            }
        }

        private static VNetInstallTask CreateTask(HttpClient http, ILogger logger, string integrationPrefix = IntegrationPrefix,
            string vnetPrefix = "10.20.30.0/24", string endpointPrefix = EndpointPrefix)
        {
            var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0;
            var group = new ArmClient(new SyntheticCredential(), Guid.Empty.ToString(), options)
                .GetResourceGroupResource(new ResourceIdentifier(GroupId));
            var config = TaskConfig.GetConfigForName("contoso-vnet")
                .AddSetting(VNetInstallTask.CONFIG_KEY_ADDRESS_PREFIX, vnetPrefix)
                .AddSetting(VNetInstallTask.CONFIG_KEY_SUBNET_NAME, EndpointName)
                .AddSetting(VNetInstallTask.CONFIG_KEY_SUBNET_ADDRESS_PREFIX, endpointPrefix)
                .AddSetting(VNetInstallTask.CONFIG_KEY_APP_INTEGRATION_SUBNET_NAME, IntegrationName)
                .AddSetting(VNetInstallTask.CONFIG_KEY_APP_INTEGRATION_SUBNET_ADDRESS_PREFIX, integrationPrefix);
            return new VNetInstallTask(config, logger, new AzureLocation("westeurope"), new Dictionary<string, string>())
            { Container = group };
        }

        private sealed class NetworkHandler : HttpMessageHandler
        {
            public bool PrivateSubnet;
            public bool PluralPrefixes;
            public bool MissingVNet;
            public JObject CreatedVNet;
            public readonly List<string> Requests = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri.AbsolutePath;
                Requests.Add(request.Method + " " + path);
                if (MissingVNet && request.Method == HttpMethod.Get && path == VNetId)
                    return Json(HttpStatusCode.NotFound, new JObject
                    { ["error"] = new JObject { ["code"] = "ResourceNotFound", ["message"] = "Synthetic missing VNet" } });
                if (MissingVNet && request.Method == HttpMethod.Put && path == VNetId)
                {
                    CreatedVNet = JObject.Parse(await request.Content.ReadAsStringAsync());
                    var created = (JObject)CreatedVNet.DeepClone();
                    created["id"] = VNetId;
                    created["name"] = "contoso-vnet";
                    created["properties"]["provisioningState"] = "Succeeded";
                    return Json(HttpStatusCode.OK, created);
                }
                Assert.AreEqual(HttpMethod.Get, request.Method, "Existing network resources must never be rewritten.");
                if (path == VNetId)
                    return Json(HttpStatusCode.OK, new JObject
                    {
                        ["id"] = VNetId, ["name"] = "contoso-vnet", ["location"] = "westeurope",
                        ["properties"] = new JObject
                        {
                            ["addressSpace"] = new JObject { ["addressPrefixes"] = new JArray("10.20.30.0/24") },
                            ["dhcpOptions"] = new JObject { ["dnsServers"] = new JArray("10.20.30.100") },
                            ["subnets"] = new JArray(Subnet(EndpointName, EndpointPrefix), Subnet(IntegrationName, IntegrationPrefix))
                        }
                    });
                Assert.AreEqual(VNetId + "/subnets/" + IntegrationName, path);
                return Json(HttpStatusCode.OK, Subnet(IntegrationName, IntegrationPrefix));
            }

            private JObject Subnet(string name, string prefix)
            {
                var properties = new JObject
                {
                    ["defaultOutboundAccess"] = !PrivateSubnet,
                    ["privateEndpointNetworkPolicies"] = "Enabled",
                    ["networkSecurityGroup"] = new JObject { ["id"] = GroupId + "/providers/Microsoft.Network/networkSecurityGroups/contoso-nsg" },
                    ["routeTable"] = new JObject { ["id"] = GroupId + "/providers/Microsoft.Network/routeTables/contoso-routes" },
                    ["natGateway"] = new JObject { ["id"] = GroupId + "/providers/Microsoft.Network/natGateways/contoso-nat" },
                    ["serviceEndpoints"] = new JArray(new JObject { ["service"] = "Microsoft.Storage", ["locations"] = new JArray("westeurope") })
                };
                properties[PluralPrefixes ? "addressPrefixes" : "addressPrefix"] =
                    PluralPrefixes ? (JToken)new JArray(prefix) : new JValue(prefix);
                if (name == IntegrationName)
                    properties["delegations"] = new JArray(new JObject
                    { ["name"] = "contoso-web", ["properties"] = new JObject { ["serviceName"] = "Microsoft.Web/serverFarms" } });
                return new JObject { ["id"] = VNetId + "/subnets/" + name, ["name"] = name, ["properties"] = properties };
            }

            private static HttpResponseMessage Json(HttpStatusCode status, JObject body) => new HttpResponseMessage(status)
            { Content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json") };
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
