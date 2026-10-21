using App.ControlPanel.Engine.InstallerTasks;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using CloudInstallEngine;
using CloudInstallEngine.Azure;
using CloudInstallEngine.Azure.InstallTasks;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class PrivateNetworkTeardownTests
    {
        private const string ResourceGroupId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/Contoso";
        private const string SqlId = ResourceGroupId + "/providers/Microsoft.Sql/servers/contoso-sql";
        private const string EndpointId = ResourceGroupId + "/providers/Microsoft.Network/privateEndpoints/pe-contoso-sql-sql";
        private const string ZoneId = ResourceGroupId + "/providers/Microsoft.Network/privateDnsZones/privatelink.database.windows.net";
        private const string SubnetId = ResourceGroupId + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet/subnets/default";

        [TestMethod]
        public async Task FreshPublicAndHybridDoNoDestructiveWork()
        {
            var fixture = new Fixture(false);
            await fixture.Run();
            Assert.AreEqual(0, fixture.Client.Mutations.Count);
            Assert.AreEqual(0, fixture.Saved.Count);
            fixture.Client.ThrowOnRead = true;
            await fixture.Run(true);
            Assert.AreEqual(0, fixture.Client.Mutations.Count);
        }

        [TestMethod]
        public async Task OwnedTransitionRemovesOnlyOwnedGroupsEndpointsAndEmptyLinkAndReruns()
        {
            var fixture = new Fixture();
            await fixture.Run();
            CollectionAssert.AreEqual(new[] { "Public:" + SqlId, "Delete:ZoneGroup", "Delete:Endpoint", "Delete:Link" },
                fixture.Client.Mutations);
            Assert.AreEqual("Public", fixture.Manifest.AppliedTopology);
            Assert.IsTrue(fixture.Manifest.Artifacts.Where(a => a.Kind != PrivateNetworkProvenance.Zone).All(a => a.Removed));
            var count = fixture.Client.Mutations.Count;
            await fixture.Run();
            Assert.AreEqual(count, fixture.Client.Mutations.Count);
        }

        [TestMethod]
        public async Task SharedDnsSurvivesAndRequiresExplicitDnsReview()
        {
            var fixture = new Fixture();
            fixture.Client.SharedZone = true;
            await fixture.Run();
            Assert.IsTrue(fixture.Client.Live.ContainsKey(ZoneId + "/virtualNetworkLinks/contoso-link"));
            Assert.IsFalse(fixture.Client.Mutations.Contains("Delete:Link"));
            Assert.AreEqual("PublicSharedDnsReviewRequired", fixture.Manifest.AppliedTopology);
        }

        [TestMethod]
        public async Task AdoptedDnsLinkRetainsReviewAfterCheckpointedGroupAndEndpointRetry()
        {
            var fixture = new Fixture();
            var link = fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.Link);
            link.CreatedByInstaller = false;
            fixture.Client.FailEndpointOnce = true;
            await Assert.ThrowsExceptionAsync<RequestFailedException>(() => fixture.Run());
            Assert.AreEqual("PublicTransitionPending", fixture.Manifest.AppliedTopology);
            Assert.IsTrue(fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup).Removed);

            await fixture.Run();
            Assert.AreEqual("PublicSharedDnsReviewRequired", fixture.Manifest.AppliedTopology);
            Assert.IsTrue(fixture.Client.Live.ContainsKey(link.Id));
            Assert.IsFalse(fixture.Client.Mutations.Contains("Delete:Link"));
            var mutations = fixture.Client.Mutations.Count;
            await fixture.Run();
            Assert.AreEqual("PublicSharedDnsReviewRequired", fixture.Manifest.AppliedTopology);
            Assert.AreEqual(mutations, fixture.Client.Mutations.Count);
            Assert.AreEqual("PublicSharedDnsReviewRequired",
                JsonConvert.DeserializeObject<PrivateNetworkManifest>(fixture.Saved.Last()).AppliedTopology);
        }

        [DataTestMethod]
        [DataRow("PublicTransitionPending", false)]
        [DataRow("Public", false)]
        [DataRow("PublicSharedDnsReviewRequired", false)]
        [DataRow("PublicTransitionPending", true)]
        public async Task CompletedTransitionUsesDurableDnsHistoryOnNoOp(string topology, bool linkRemoved)
        {
            var fixture = new Fixture();
            foreach (var artifact in fixture.Manifest.Artifacts)
            {
                if (artifact.Kind == PrivateNetworkProvenance.Link)
                {
                    artifact.CreatedByInstaller = false;
                    artifact.Removed = linkRemoved;
                    if (linkRemoved) fixture.Client.Live.Remove(artifact.Id);
                }
                else
                {
                    artifact.Removed = true;
                    fixture.Client.Live.Remove(artifact.Id);
                }
            }
            fixture.Client.RecordPresent = false;
            fixture.Manifest.AppliedTopology = topology;
            await fixture.Run();
            Assert.AreEqual(linkRemoved ? "Public" : "PublicSharedDnsReviewRequired", fixture.Manifest.AppliedTopology);
            Assert.AreEqual(0, fixture.Client.Mutations.Count);
        }

        [TestMethod]
        public async Task ForeignSameNamedEndpointBlocksBeforeAnyPublicEnableOrDelete()
        {
            var fixture = new Fixture();
            fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.Endpoint).CreatedByInstaller = false;
            var ex = await Assert.ThrowsExceptionAsync<InstallException>(() => fixture.Run());
            StringAssert.Contains(ex.Message, "BLOCKED");
            StringAssert.Contains(ex.Message, "manually");
            StringAssert.Contains(ex.Message, "--review-networking");
            Assert.AreEqual(0, fixture.Client.Mutations.Count);
        }

        [TestMethod]
        public async Task ForeignChildGroupOrChangedRelationshipsBlockBeforeDestructiveWork()
        {
            foreach (var change in new Action<Fixture>[]
            {
                f => f.Client.Live[EndpointId].Children[0].Id += "-foreign",
                f => f.Client.Live[EndpointId].Artifact.SubnetId += "-foreign",
                f => f.Client.Live[EndpointId].Tags[PrivateNetworkProvenance.OwnerTag] = "foreign",
                f => f.Client.Live[EndpointId + "/privateDnsZoneGroups/contoso-group"].Artifact.Records[0].Addresses.Add("10.0.0.9"),
                f => f.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup).Records[0].CreatedExclusively = false
            })
            {
                var fixture = new Fixture();
                change(fixture);
                await Assert.ThrowsExceptionAsync<InstallException>(() => fixture.Run());
                Assert.AreEqual(0, fixture.Client.Mutations.Count);
            }
        }

        [TestMethod]
        public async Task PartialFailurePersistsPendingAndRetriesFromConfirmedMissingGroup()
        {
            var fixture = new Fixture();
            fixture.Client.FailEndpointOnce = true;
            await Assert.ThrowsExceptionAsync<RequestFailedException>(() => fixture.Run());
            Assert.AreEqual("PublicTransitionPending", fixture.Manifest.AppliedTopology);
            Assert.AreEqual("PublicTransitionPending", JsonConvert.DeserializeObject<PrivateNetworkManifest>(fixture.Saved.Last()).AppliedTopology);
            Assert.IsTrue(fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup).Removed);
            Assert.IsFalse(fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.Endpoint).Removed);
            await fixture.Run();
            Assert.AreEqual("Public", fixture.Manifest.AppliedTopology);
            Assert.AreEqual(1, fixture.Client.Mutations.Count(m => m == "Delete:ZoneGroup"));
        }

        [TestMethod]
        public async Task StaleDnsAfterGroupDeletionIsNotMarkedRemovedOrPublic()
        {
            var fixture = new Fixture();
            fixture.Client.LeaveRecords = true;
            await Assert.ThrowsExceptionAsync<InstallException>(() => fixture.Run());
            Assert.AreEqual("PublicTransitionPending", fixture.Manifest.AppliedTopology);
            Assert.IsFalse(fixture.Manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup).Removed);
            Assert.IsTrue(fixture.Client.Live.ContainsKey(EndpointId));
            await Assert.ThrowsExceptionAsync<InstallException>(() => fixture.Run());
            fixture.Client.RecordPresent = false;
            await fixture.Run();
            Assert.AreEqual("Public", fixture.Manifest.AppliedTopology);
        }

        [TestMethod]
        public async Task CheckpointFailurePreventsPublicEnableOrDeletion()
        {
            var fixture = new Fixture();
            fixture.FailSave = true;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => fixture.Run());
            Assert.AreEqual(0, fixture.Client.Mutations.Count);
        }

        [TestMethod]
        public async Task EndpointSdkCreationRecordsIntentBeforePutAndNeverClaimsExistingEndpoint()
        {
            using (var handler = new EndpointHandler())
            using (var http = new HttpClient(handler))
            {
                var saved = new List<string>();
                var manifest = new PrivateNetworkManifest();
                var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId,
                    () => saved.Add(JsonConvert.SerializeObject(manifest)));
                handler.BeforePut = () =>
                {
                    Assert.AreEqual(1, saved.Count);
                    Assert.IsFalse(JsonConvert.DeserializeObject<PrivateNetworkManifest>(saved[0]).Artifacts.Single().CreationConfirmed);
                };
                var task = EndpointTask(Group(http), provenance);
                await task.ExecuteTaskReturnResult(null);
                Assert.IsTrue(provenance.IsOwned(EndpointId));
                Assert.AreEqual(manifest.DeploymentToken, (string)handler.Stored["tags"][PrivateNetworkProvenance.OwnerTag]);
                Assert.AreEqual(1, handler.Puts);
                await task.ExecuteTaskReturnResult(null);
                Assert.AreEqual(1, handler.Puts);

                var otherManifest = new PrivateNetworkManifest();
                var observer = new PrivateNetworkProvenance(otherManifest, ResourceGroupId, () => { });
                await EndpointTask(Group(http), observer).ExecuteTaskReturnResult(null);
                Assert.IsFalse(observer.IsOwned(EndpointId), "Even a matching marker on a reused endpoint does not create provenance.");
                Assert.IsFalse(otherManifest.Artifacts.Single().CreatedByInstaller);
            }
        }

        [TestMethod]
        public async Task SdkDisconnectedExistingEndpointIsReportedAndNeverRecreatedOrClaimed()
        {
            using (var handler = new NetworkingHandler("synthetic-foreign"))
            using (var http = new HttpClient(handler))
            {
                handler.Stored[EndpointId]["properties"]["privateLinkServiceConnections"][0]["properties"]["privateLinkServiceConnectionState"]["status"] = "Disconnected";
                var manifest = new PrivateNetworkManifest();
                var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId, () => { });
                var ex = await Assert.ThrowsExceptionAsync<InstallException>(() =>
                    EndpointTask(Group(http), provenance).ExecuteTaskReturnResult(null));
                StringAssert.Contains(ex.Message, "disconnected");
                Assert.IsFalse(provenance.IsOwned(EndpointId));
                Assert.AreEqual(0, handler.Puts);
                Assert.AreEqual(0, handler.Deletes.Count);
            }
        }

        [TestMethod]
        public async Task InterruptedCreationCannotBeSilentlyAdoptedAndMissingIntentCanRetry()
        {
            using (var handler = new EndpointHandler { FailPutOnce = true })
            using (var http = new HttpClient(handler))
            {
                var manifest = new PrivateNetworkManifest();
                var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId, () => { });
                var task = EndpointTask(Group(http), provenance);
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() => task.ExecuteTaskReturnResult(null));
                Assert.IsFalse(manifest.Artifacts.Single().CreationConfirmed);
                await Assert.ThrowsExceptionAsync<InstallException>(() => task.ExecuteTaskReturnResult(null));
                handler.Stored = null;
                await task.ExecuteTaskReturnResult(null);
                Assert.IsTrue(provenance.IsOwned(EndpointId));
            }
        }

        [TestMethod]
        public async Task SdkAdapterPreservesUnrelatedEndpointAndPropagatesFailedDiscovery()
        {
            using (var handler = new EndpointHandler { ListMode = true })
            using (var http = new HttpClient(handler))
            {
                handler.Stored = JObject.Parse(EndpointHandler.EndpointJson);
                handler.Stored["properties"]["privateLinkServiceConnections"][0]["properties"]["privateLinkServiceId"] = SqlId + "-foreign";
                handler.Stored["name"] = "foreign-endpoint";
                handler.Stored["id"] = EndpointId + "-foreign";
                var adapter = new PrivateNetworkCleanupClient(Group(http), new PrivateNetworkManifest(),
                    new[] { SqlId }, new[] { "pe-contoso-sql-sql" });
                Assert.AreEqual(0, (await adapter.DiscoverEndpoints(CancellationToken.None)).Count);
                Assert.AreEqual(0, handler.Puts);
                handler.FailReads = true;
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() => adapter.DiscoverEndpoints(CancellationToken.None));
            }
        }

        [DataTestMethod]
        [DataRow("Microsoft.Sql/servers")]
        [DataRow("Microsoft.Storage/storageAccounts")]
        [DataRow("Microsoft.KeyVault/vaults")]
        [DataRow("Microsoft.ServiceBus/namespaces")]
        [DataRow("Microsoft.Web/sites")]
        [DataRow("Microsoft.CognitiveServices/accounts")]
        [DataRow("Microsoft.Automation/automationAccounts")]
        public async Task SdkPublicTransitionChangesOnlyConfirmedTargetAndVerifiesReadback(string resourceType)
        {
            var target = ResourceGroupId + "/providers/" + resourceType + "/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId };
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint, TargetId = target,
                CreatedByInstaller = true, CreationConfirmed = true
            });
            using (var handler = new ResourceHandler(target, resourceType))
            using (var http = new HttpClient(handler))
            {
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await adapter.ValidatePublicTarget(target, CancellationToken.None);
                Assert.AreEqual(0, handler.Mutations);
                await adapter.EnablePublicTarget(target, CancellationToken.None);
                Assert.AreEqual(1, handler.Mutations);
                Assert.IsTrue(handler.Reads >= 3, "Preflight, update and independent public-access readback must run.");
                Assert.AreEqual("preserve", (string)handler.Stored["tags"]["Contoso"]);
                Assert.IsTrue((bool)handler.Stored["properties"]["disableLocalAuth"], "Unrelated security settings remain intact.");
                manifest.Artifacts.Clear();
                await Assert.ThrowsExceptionAsync<InstallException>(() => adapter.EnablePublicTarget(target, CancellationToken.None));
                Assert.AreEqual(1, handler.Mutations);
            }
        }

        [DataTestMethod]
        [DataRow("Microsoft.Sql/servers")]
        [DataRow("Microsoft.Storage/storageAccounts")]
        [DataRow("Microsoft.KeyVault/vaults")]
        [DataRow("Microsoft.ServiceBus/namespaces")]
        [DataRow("Microsoft.Web/sites")]
        [DataRow("Microsoft.CognitiveServices/accounts")]
        [DataRow("Microsoft.Automation/automationAccounts")]
        public async Task SdkEverySupportedProviderDistinguishesMissingTargetsAndPolicyDenial(string resourceType)
        {
            var target = ResourceGroupId + "/providers/" + resourceType + "/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId, AppliedTopology = "PublicTransitionPending" };
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint, TargetId = target,
                CreatedByInstaller = true, CreationConfirmed = true
            });
            using (var handler = new ResourceHandler(target, resourceType) { ReadErrorCode = "ResourceNotFound" })
            using (var http = new HttpClient(handler))
            {
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await adapter.ValidatePublicTarget(target, CancellationToken.None);
                await adapter.EnablePublicTarget(target, CancellationToken.None);
                Assert.AreEqual(0, handler.Mutations, "Genuinely absent resources are not recreated by teardown.");
                handler.ReadErrorCode = null;
                handler.FailPatchOnce = true;
                handler.FailureStatus = HttpStatusCode.Forbidden;
                var ex = await Assert.ThrowsExceptionAsync<RequestFailedException>(() => adapter.EnablePublicTarget(target, CancellationToken.None));
                Assert.AreEqual(403, ex.Status, "Policy/authorization failures must reach the caller.");
                Assert.IsFalse(manifest.Artifacts.Single().Removed);
                Assert.AreEqual("PublicTransitionPending", manifest.AppliedTopology);
                handler.IgnoreChanges = true;
                await Assert.ThrowsExceptionAsync<InstallException>(() => adapter.EnablePublicTarget(target, CancellationToken.None));
                Assert.IsFalse(manifest.Artifacts.Single().Removed, "Unconfirmed public readback cannot retire private state.");
            }
        }

        [TestMethod]
        public async Task SdkPublicTransitionRejectsUnconfirmedReadbackAndPreservesStorageFirewall()
        {
            var target = ResourceGroupId + "/providers/Microsoft.Storage/storageAccounts/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId };
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint, TargetId = target,
                CreatedByInstaller = true, CreationConfirmed = true
            });
            using (var handler = new ResourceHandler(target, "Microsoft.Storage/storageAccounts") { IgnoreChanges = true })
            using (var http = new HttpClient(handler))
            {
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await Assert.ThrowsExceptionAsync<InstallException>(() => adapter.EnablePublicTarget(target, CancellationToken.None));
                handler.Stored["properties"]["networkAcls"]["defaultAction"] = "Deny";
                await Assert.ThrowsExceptionAsync<InstallException>(() => adapter.ValidatePublicTarget(target, CancellationToken.None));
                Assert.AreEqual(1, handler.Mutations, "Operator firewall policy must not be opened implicitly.");
            }
        }

        [TestMethod]
        public async Task SdkPublicUpdateNotFoundDoesNotMasqueradeAsAnAbsentTarget()
        {
            var target = ResourceGroupId + "/providers/Microsoft.Web/sites/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId };
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = target, Kind = PrivateNetworkProvenance.Integration, TargetId = target,
                SubnetId = SubnetId, CreatedByInstaller = true, CreationConfirmed = true
            });
            using (var handler = new ResourceHandler(target, "Microsoft.Web/sites")
            { FailPatchOnce = true, FailureStatus = HttpStatusCode.NotFound })
            using (var http = new HttpClient(handler))
            {
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() => adapter.EnablePublicTarget(target, CancellationToken.None));
                Assert.AreEqual("Disabled", (string)handler.Stored["properties"]["publicNetworkAccess"]);
                handler.ReadErrorCode = "InvalidResourceType";
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() => adapter.Read(manifest.Artifacts.Single(), CancellationToken.None));
                handler.ReadErrorCode = "ResourceNotFound";
                Assert.IsNull(await adapter.Read(manifest.Artifacts.Single(), CancellationToken.None));
                await adapter.EnablePublicTarget(target, CancellationToken.None);
                Assert.AreEqual(1, handler.Mutations, "Only an initial genuine absence is an idempotent no-op.");
            }
        }

        [TestMethod]
        public async Task SdkOwnedVaultRuleRemovalPreservesForeignRulesAndIpPolicy()
        {
            var target = ResourceGroupId + "/providers/Microsoft.KeyVault/vaults/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId };
            var rule = new PrivateNetworkArtifact
            {
                Id = target + "/virtualNetworkRules/contoso-owned-rule", Kind = PrivateNetworkProvenance.VirtualNetworkRule,
                TargetId = target, SubnetId = SubnetId, CreatedByInstaller = true, CreationConfirmed = true
            };
            manifest.Artifacts.Add(rule);
            using (var handler = new ResourceHandler(target, "Microsoft.KeyVault/vaults"))
            using (var http = new HttpClient(handler))
            {
                var foreignSubnet = SubnetId + "-foreign";
                handler.Stored["properties"]["networkAcls"]["virtualNetworkRules"] = new JArray(
                    new JObject { ["id"] = SubnetId }, new JObject { ["id"] = foreignSubnet });
                handler.Stored["properties"]["networkAcls"]["defaultAction"] = "Deny";
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await adapter.Delete(rule, CancellationToken.None);
                Assert.AreEqual(1, handler.Mutations);
                var remaining = (JArray)handler.Stored["properties"]["networkAcls"]["virtualNetworkRules"];
                Assert.AreEqual(1, remaining.Count);
                Assert.AreEqual(foreignSubnet, (string)remaining.Single()["id"]);
                Assert.AreEqual("Deny", (string)handler.Stored["properties"]["networkAcls"]["defaultAction"]);
                await adapter.Delete(rule, CancellationToken.None);
                Assert.AreEqual(1, ((JArray)handler.Stored["properties"]["networkAcls"]["virtualNetworkRules"]).Count);
            }
        }

        [TestMethod]
        public async Task SdkOwnedAppIntegrationRetriesAfterDetachAndRestoresPreviousRouteAll()
        {
            var target = ResourceGroupId + "/providers/Microsoft.Web/sites/contoso";
            var manifest = new PrivateNetworkManifest { ResourceGroupId = ResourceGroupId };
            var integration = new PrivateNetworkArtifact
            {
                Id = target, Kind = PrivateNetworkProvenance.Integration, TargetId = target,
                SubnetId = SubnetId, CreatedByInstaller = true, CreationConfirmed = true
            };
            manifest.Artifacts.Add(integration);
            using (var handler = new ResourceHandler(target, "Microsoft.Web/sites") { FailPatchOnce = true })
            using (var http = new HttpClient(handler))
            {
                handler.Stored["properties"]["virtualNetworkSubnetId"] = SubnetId;
                handler.Stored["properties"]["siteConfig"] = new JObject { ["vnetRouteAllEnabled"] = true };
                var adapter = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() => adapter.Delete(integration, CancellationToken.None));
                Assert.AreEqual(1, handler.IntegrationDeletes);
                var live = await adapter.Read(integration, CancellationToken.None);
                Assert.IsTrue(PrivateNetworkProvenance.Matches(integration, live.Artifact), "Already-detached state must still allow the pending route-all reset.");
                await adapter.Delete(integration, CancellationToken.None);
                Assert.AreEqual(1, handler.IntegrationDeletes);
                Assert.IsFalse((bool)handler.Stored["properties"]["siteConfig"]["vnetRouteAllEnabled"]);
                handler.Stored["properties"]["virtualNetworkSubnetId"] = SubnetId + "-foreign";
                await Assert.ThrowsExceptionAsync<InstallException>(() => adapter.Delete(integration, CancellationToken.None));
                Assert.AreEqual(1, handler.IntegrationDeletes);
            }
        }

        [TestMethod]
        public void DurableManifestCheckpointPreservesOperatorInputsAndUnicodeAndDoesNotClaimSuccess()
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "network-provenance-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var config = new App.ControlPanel.Engine.SolutionInstallConfig();
                config.NetworkConfig.VNetName = "contoso-vnet";
                config.NetworkConfig.CustomEndpointNames.SqlServer = "contoso-custom-sql";
                config.Tags.Add(new Common.Entities.Installer.AzTag("Purpose", "Καλημέρα κόσμε"));
                var path = Path.Combine(directory, "contoso.json");
                config.Save(path, "synthetic-password");
                var provenance = new PrivateNetworkProvenance(config.PrivateNetworking, ResourceGroupId,
                    () => config.CheckpointPrivateNetworking("synthetic-password"));
                provenance.BeginCreate(new PrivateNetworkArtifact
                {
                    Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint,
                    TargetId = SqlId, GroupId = "sqlServer", SubnetId = SubnetId
                });
                var saved = JObject.Parse(File.ReadAllText(path));
                Assert.AreEqual("Pending", (string)saved["PrivateNetworking"]["AppliedTopology"]);
                Assert.IsFalse((bool)saved["PrivateNetworking"]["Artifacts"][0]["CreationConfirmed"]);
                Assert.AreEqual("contoso-vnet", (string)saved["NetworkConfig"]["VNetName"]);
                Assert.AreEqual("contoso-custom-sql", (string)saved["NetworkConfig"]["CustomEndpointNames"]["SqlServer"]);
                Assert.AreEqual("Καλημέρα κόσμε", (string)saved["Tags"][0]["Value"]);
                Assert.IsNull(JObject.Parse(config.ToJson("synthetic-password"))["PrivateNetworking"],
                    "Runtime/SQL configuration snapshots must not become an ownership authority.");
                provenance.ObserveMissingResourceGroup();
                var restored = App.ControlPanel.Engine.SolutionInstallConfig.LoadFromFile(path, "synthetic-password").Config;
                Assert.AreEqual("Public", restored.PrivateNetworking.AppliedTopology);
                Assert.IsTrue(restored.PrivateNetworking.Artifacts.Single().Removed);
                Assert.AreEqual("contoso-vnet", restored.NetworkConfig.VNetName);
                File.Delete(path);
                Assert.ThrowsException<FileNotFoundException>(() => provenance.ConfirmCreate(EndpointId));
                Assert.IsFalse(File.Exists(path + ".network-checkpoint-new"));
            }
            finally { Directory.Delete(directory, true); }
        }

        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(true, false)]
        [DataRow(false, true)]
        public async Task DnsSdkProvisioningAndTeardownRoundTripPreservesZoneAndUsesConditionalLinkDelete(bool adoptedDns, bool sharedRecords)
        {
            var manifest = new PrivateNetworkManifest();
            var saved = new List<string>();
            var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId,
                () => saved.Add(JsonConvert.SerializeObject(manifest)));
            provenance.BeginCreate(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint,
                TargetId = SqlId, GroupId = "sqlServer", SubnetId = SubnetId
            });
            provenance.ConfirmCreate(EndpointId);
            using (var handler = new NetworkingHandler(manifest.DeploymentToken))
            using (var http = new HttpClient(handler))
            {
                var linkId = ZoneId + "/virtualNetworkLinks/privatelink.database.windows.net-vnet-link";
                if (adoptedDns) handler.SeedForeignDns();
                var group = Group(http);
                var dns = new PrivateDnsZoneInstallTask(TaskConfig.GetConfigForName("privatelink.database.windows.net")
                    .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, new ResourceIdentifier(SubnetId).Parent.ToString())
                    .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, "pe-contoso-sql-sql"),
                    NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>())
                { Container = group, Networking = provenance };
                await dns.ExecuteTaskReturnResult(null);
                var ownedGroup = manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup);
                Assert.IsTrue(ownedGroup.CreatedByInstaller && ownedGroup.CreationConfirmed);
                Assert.AreEqual(1, ownedGroup.Records.Count);
                Assert.IsTrue(ownedGroup.Records.Single().CreatedExclusively);
                var putCount = handler.Puts;
                handler.ForeignRecordPresent = sharedRecords;
                await dns.ExecuteTaskReturnResult(null);
                Assert.AreEqual(putCount, handler.Puts);
                var cleanup = new PrivateNetworkTeardown(provenance, new PrivateNetworkCleanupClient(group, manifest,
                    new[] { SqlId }, new[] { "pe-contoso-sql-sql" }), NullLogger.Instance);
                await cleanup.Run(false, CancellationToken.None);
                Assert.AreEqual(adoptedDns || sharedRecords ? "PublicSharedDnsReviewRequired" : "Public", manifest.AppliedTopology);
                Assert.IsFalse(handler.RecordPresent);
                Assert.IsTrue(handler.Stored.ContainsKey(ZoneId), "Never delete the whole DNS zone.");
                Assert.IsTrue(handler.Deletes.Any(d => d.Contains("/privateDnsZoneGroups/")));
                Assert.IsTrue(handler.Deletes.Contains(EndpointId));
                Assert.AreEqual(!adoptedDns && !sharedRecords, handler.SawConditionalLinkDelete);
                Assert.AreEqual(adoptedDns || sharedRecords, handler.Stored.ContainsKey(linkId));
                Assert.AreEqual(sharedRecords, handler.ForeignRecordPresent);
                Assert.AreEqual(!adoptedDns, manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.Zone).CreatedByInstaller);
                var deleteCount = handler.Deletes.Count;
                await cleanup.Run(false, CancellationToken.None);
                Assert.AreEqual(deleteCount, handler.Deletes.Count);
            }
        }

        [DataTestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task SdkOrphanedLegacyRecordsOrLinksBlockWithoutEndpointAndWithoutMutations(bool hasRecord)
        {
            var manifest = new PrivateNetworkManifest();
            using (var handler = new NetworkingHandler("synthetic-foreign") { RecordPresent = hasRecord })
            using (var http = new HttpClient(handler))
            {
                handler.Stored.Remove(EndpointId);
                handler.SeedForeignDns();
                var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId, () => { });
                var cleanup = new PrivateNetworkTeardown(provenance, new PrivateNetworkCleanupClient(Group(http), manifest,
                    new[] { SqlId }, new[] { "pe-contoso-sql-sql" }, new ResourceIdentifier(SubnetId).Parent.ToString()), NullLogger.Instance);
                var ex = await Assert.ThrowsExceptionAsync<InstallException>(() => cleanup.Run(false, CancellationToken.None));
                StringAssert.Contains(ex.Message, "BLOCKED");
                Assert.AreEqual(0, handler.Puts);
                Assert.AreEqual(0, handler.Deletes.Count);
                Assert.AreEqual("Unknown", manifest.AppliedTopology);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task SdkSharedZoneGroupReferenceBlocksEvenWithUnchangedAddresses(bool unavailableRecordInventory)
        {
            var manifest = new PrivateNetworkManifest();
            var provenance = new PrivateNetworkProvenance(manifest, ResourceGroupId, () => { });
            provenance.BeginCreate(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint,
                TargetId = SqlId, GroupId = "sqlServer", SubnetId = SubnetId
            });
            provenance.ConfirmCreate(EndpointId);
            using (var handler = new NetworkingHandler(manifest.DeploymentToken))
            using (var http = new HttpClient(handler))
            {
                var group = Group(http);
                var dns = new PrivateDnsZoneInstallTask(TaskConfig.GetConfigForName("privatelink.database.windows.net")
                    .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_VNET_ID, new ResourceIdentifier(SubnetId).Parent.ToString())
                    .AddSetting(PrivateDnsZoneInstallTask.CONFIG_KEY_PE_NAME, "pe-contoso-sql-sql"),
                    NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>())
                { Container = group, Networking = provenance };
                await dns.ExecuteTaskReturnResult(null);
                var otherEndpointId = EndpointId + "-foreign";
                handler.Stored[otherEndpointId] = (JObject)handler.Stored[EndpointId].DeepClone();
                handler.Stored[otherEndpointId]["id"] = otherEndpointId;
                handler.Stored[otherEndpointId]["name"] = "foreign-endpoint";
                handler.Stored[otherEndpointId]["properties"]["privateLinkServiceConnections"][0]["properties"]["privateLinkServiceId"] = SqlId + "-foreign";
                var ownedGroup = manifest.Artifacts.Single(a => a.Kind == PrivateNetworkProvenance.ZoneGroup);
                var foreignGroupId = otherEndpointId + "/privateDnsZoneGroups/foreign-group";
                handler.Stored[foreignGroupId] = (JObject)handler.Stored[ownedGroup.Id].DeepClone();
                handler.Stored[foreignGroupId]["id"] = foreignGroupId;
                handler.Stored[foreignGroupId]["name"] = "foreign-group";
                if (unavailableRecordInventory)
                    handler.Stored[foreignGroupId]["properties"]["privateDnsZoneConfigs"][0]["properties"]["recordSets"] = new JArray();
                var puts = handler.Puts;
                var cleanup = new PrivateNetworkTeardown(provenance, new PrivateNetworkCleanupClient(group, manifest,
                    new[] { SqlId }, new[] { "pe-contoso-sql-sql" }), NullLogger.Instance);
                var ex = await Assert.ThrowsExceptionAsync<InstallException>(() => cleanup.Run(false, CancellationToken.None));
                StringAssert.Contains(ex.Message, "another endpoint DNS zone group");
                Assert.AreEqual(puts, handler.Puts, "All dependencies must pass before even enabling public access.");
                Assert.AreEqual(0, handler.Deletes.Count);
                Assert.IsFalse(ownedGroup.Removed);
                handler.Stored.Remove(foreignGroupId);
                await cleanup.Run(false, CancellationToken.None);
                Assert.AreEqual("Public", manifest.AppliedTopology);
                Assert.IsTrue(handler.Stored.ContainsKey(otherEndpointId), "Unrelated endpoints are never removed.");
            }
        }

        [TestMethod]
        public async Task SdkLegacyReviewCollectsEndpointOrphanSharedDnsRedisAndMissingHistoryWithoutMutation()
        {
            var manifest = new PrivateNetworkManifest();
            var missingId = EndpointId + "-missing";
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = missingId, Kind = PrivateNetworkProvenance.Endpoint, CreatedByInstaller = true, CreationConfirmed = true
            });
            var before = JsonConvert.SerializeObject(manifest);
            using (var handler = new NetworkingHandler("synthetic-foreign") { RecordPresent = true, ForeignRecordPresent = true })
            using (var http = new HttpClient(handler))
            {
                handler.SeedForeignDns();
                var redisId = ResourceGroupId + "/providers/Microsoft.Cache/redis/contoso-retired";
                handler.Stored[redisId] = new JObject
                {
                    ["id"] = redisId, ["name"] = "contoso-retired", ["type"] = "Microsoft.Cache/redis"
                };
                var vaultId = ResourceGroupId + "/providers/Microsoft.KeyVault/vaults/contoso";
                var siteId = ResourceGroupId + "/providers/Microsoft.Web/sites/contoso";
                using (var vault = new ResourceHandler(vaultId, "Microsoft.KeyVault/vaults"))
                using (var site = new ResourceHandler(siteId, "Microsoft.Web/sites"))
                {
                    vault.Stored["properties"]["networkAcls"]["virtualNetworkRules"] = new JArray(new JObject { ["id"] = SubnetId });
                    site.Stored["properties"]["virtualNetworkSubnetId"] = SubnetId;
                    handler.Stored[vaultId] = vault.Stored;
                    handler.Stored[siteId] = site.Stored;
                }
                handler.Stored[EndpointId]["properties"]["privateLinkServiceConnections"][0]["properties"]["privateLinkServiceConnectionState"]["status"] = "Disconnected";
                var client = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { SqlId, vaultId, siteId }, new[] { "pe-contoso-sql-sql" });
                var report = await client.Review(CancellationToken.None);
                StringAssert.Contains(report.Items.Single(i => i.ResourceId == EndpointId).Observation, "Disconnected");
                Assert.IsTrue(report.Items.Any(i => i.ResourceId == ZoneId + "/A/contoso-sql"));
                Assert.IsTrue(report.Items.Any(i => i.ResourceId == ZoneId + "/A/foreign-service"));
                Assert.IsTrue(report.Items.Any(i => i.ResourceId.Contains("/virtualNetworkLinks/")));
                StringAssert.Contains(report.Items.Single(i => i.ResourceId == redisId).Observation, "Retired Redis");
                StringAssert.Contains(report.Items.Single(i => i.ResourceId == missingId).Observation, "not observed");
                StringAssert.Contains(report.Items.Single(i => i.ResourceId == siteId).Observation, "outbound VNet integration");
                Assert.IsTrue(report.Items.Any(i => i.ResourceId == vaultId + "/virtualNetworkRules/" + Uri.EscapeDataString(SubnetId)),
                    "Legacy vault subnet rules are inventoried without manufacturing provenance.");
                Assert.IsFalse(report.DeletionAuthorized);
                Assert.IsFalse(report.PublicDnsVerified);
                Assert.AreEqual(before, JsonConvert.SerializeObject(manifest));
                Assert.IsTrue(handler.Methods.All(m => m == HttpMethod.Get));
                handler.Stored.Remove(EndpointId);
                var orphanReport = await client.Review(CancellationToken.None);
                Assert.IsFalse(orphanReport.Items.Any(i => i.ResourceId == EndpointId));
                Assert.IsTrue(orphanReport.Items.Any(i => i.ResourceId == ZoneId + "/A/contoso-sql"),
                    "A missing legacy endpoint must not hide its leftover DNS.");
                await Assert.ThrowsExceptionAsync<InstallException>(() =>
                    new PrivateNetworkTeardown(new PrivateNetworkProvenance(manifest, ResourceGroupId, () => { }),
                        client, NullLogger.Instance).Run(false, CancellationToken.None));
                Assert.AreEqual(0, handler.Puts, "A review never adopts resources or bypasses teardown safeguards.");
                Assert.AreEqual(0, handler.Deletes.Count);
            }
        }

        [DataTestMethod]
        [DataRow("/privateEndpoints")]
        [DataRow("/privateDnsZones")]
        [DataRow("/resources")]
        public async Task SdkReviewPropagatesDeniedInventoryWithoutMutation(string deniedSuffix)
        {
            var manifest = new PrivateNetworkManifest();
            var original = JsonConvert.SerializeObject(manifest);
            using (var handler = new NetworkingHandler("synthetic-foreign") { DeniedSuffix = deniedSuffix })
            using (var http = new HttpClient(handler))
            {
                var client = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { SqlId }, new[] { "pe-contoso-sql-sql" });
                var ex = await Assert.ThrowsExceptionAsync<RequestFailedException>(() => client.Review(CancellationToken.None));
                Assert.AreEqual(403, ex.Status);
                Assert.IsTrue(handler.Methods.All(m => m == HttpMethod.Get));
                Assert.AreEqual(original, JsonConvert.SerializeObject(manifest));
            }
        }

        [TestMethod]
        public async Task SdkRetiredRedisIsNotASupportedPublicToggleEvenWithRecordedEndpoint()
        {
            var target = ResourceGroupId + "/providers/Microsoft.Cache/redis/contoso";
            var manifest = new PrivateNetworkManifest();
            manifest.Artifacts.Add(new PrivateNetworkArtifact
            {
                Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint, TargetId = target,
                CreatedByInstaller = true, CreationConfirmed = true
            });
            using (var handler = new ResourceHandler(target, "Microsoft.Cache/redis"))
            using (var http = new HttpClient(handler))
            {
                var client = new PrivateNetworkCleanupClient(Group(http), manifest, new[] { target }, new string[0]);
                var ex = await Assert.ThrowsExceptionAsync<InstallException>(() => client.EnablePublicTarget(target, CancellationToken.None));
                StringAssert.Contains(ex.Message, "unsupported public-network target type");
                Assert.AreEqual(0, handler.Mutations);
                Assert.AreEqual(0, handler.Reads);
            }
        }

        [TestMethod]
        public async Task ReviewCommandPreservesSavedConfigAndPublishesNoIncompleteOrOverwrittenReport()
        {
            var directory = CreatePrivateReviewDirectory();
            try
            {
                var config = new App.ControlPanel.Engine.SolutionInstallConfig();
                config.InstallerAccount.Secret = "synthetic-install-secret";
                config.NetworkConfig.VNetName = "contoso-vnet";
                config.Tags.Add(new Common.Entities.Installer.AzTag("Purpose", "Καλημέρα κόσμε"));
                var path = Path.Combine(directory, "contoso-config.json");
                var reportPath = Path.Combine(directory, "contoso-private-review.json");
                config.Save(path, "synthetic-password");
                var original = File.ReadAllBytes(path);
                var calls = 0;
                Func<App.ControlPanel.Engine.SolutionInstallConfig, CancellationToken, Task<PrivateNetworkReview>> review = (loaded, ct) =>
                {
                    calls++;
                    Assert.AreEqual("contoso-vnet", loaded.NetworkConfig.VNetName);
                    return Task.FromResult(new PrivateNetworkReview { ResourceGroupId = ResourceGroupId });
                };
                var args = new[] { App.ControlPanel.Engine.PrivateNetworkReviewCommand.Operation, path, reportPath };
                await App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None, review);
                Assert.AreEqual(1, calls);
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                var output = JObject.Parse(File.ReadAllText(reportPath));
                Assert.IsTrue((bool)output["ReadOnly"]);
                Assert.IsFalse((bool)output["DeletionAuthorized"]);
                Assert.IsNull(output["DeploymentToken"]);
                await Assert.ThrowsExceptionAsync<IOException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None, review));
                Assert.AreEqual(1, calls);
                File.Delete(reportPath);
                await Assert.ThrowsExceptionAsync<RequestFailedException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None,
                        (loaded, ct) => throw new RequestFailedException(403, "Synthetic review denied")));
                Assert.IsFalse(File.Exists(reportPath), "A failed read cannot publish an apparently complete inventory.");
                await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, null, CancellationToken.None, review));
                await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "incorrect-synthetic-password", CancellationToken.None, review));
                await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", new CancellationToken(true), review));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
                Assert.IsFalse(File.Exists(reportPath));
                File.WriteAllText(path, "{ invalid synthetic JSON");
                await Assert.ThrowsExceptionAsync<JsonReaderException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None, review));
                File.Delete(path);
                await Assert.ThrowsExceptionAsync<FileNotFoundException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None, review));
            }
            finally { Directory.Delete(directory, true); }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReviewRejectsCheckoutAndWorktreeOutputBeforeLoadingConfigOrReadingArm(bool gitFile)
        {
            var directory = CreatePrivateReviewDirectory();
            try
            {
                var repository = Path.Combine(directory, "contoso-checkout");
                var outputDirectory = Path.Combine(repository, "ignored-output");
                Directory.CreateDirectory(outputDirectory);
                var marker = Path.Combine(repository, ".git");
                if (gitFile) File.WriteAllText(marker, "gitdir: synthetic-contoso-git-metadata");
                else Directory.CreateDirectory(marker);
                var reportPath = Path.Combine(outputDirectory, "private-review.json");
                var calls = 0;
                var args = new[] { App.ControlPanel.Engine.PrivateNetworkReviewCommand.Operation,
                    Path.Combine(directory, "missing-config.json"), reportPath };
                await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None,
                        (config, ct) => { calls++; return Task.FromResult(new PrivateNetworkReview()); }));
                Assert.AreEqual(0, calls, "Invalid output must be rejected before config/credential loading or ARM inventory.");
                Assert.IsFalse(File.Exists(reportPath));
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public async Task ReviewRejectsJunctionOutputBeforeLoadingConfigOrReadingArm()
        {
            var directory = CreatePrivateReviewDirectory();
            var link = Path.Combine(directory, "contoso-junction");
            try
            {
                var target = Path.Combine(directory, "private-target");
                Directory.CreateDirectory(target);
                CreateReviewJunction(link, target);
                var reportPath = Path.Combine(link, "private-review.json");
                var calls = 0;
                var args = new[] { App.ControlPanel.Engine.PrivateNetworkReviewCommand.Operation,
                    Path.Combine(directory, "missing-config.json"), reportPath };
                await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None,
                        (config, ct) => { calls++; return Task.FromResult(new PrivateNetworkReview()); }));
                Assert.AreEqual(0, calls);
                Assert.IsFalse(File.Exists(Path.Combine(target, "private-review.json")));
            }
            finally
            {
                if (Directory.Exists(link)) Directory.Delete(link);
                Directory.Delete(directory, true);
            }
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public async Task ReviewRevalidatesCheckoutAndJunctionBeforeWriting(bool junction)
        {
            var directory = CreatePrivateReviewDirectory();
            var outputDirectory = Path.Combine(directory, "output");
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var configPath = Path.Combine(directory, "contoso-config.json");
                new App.ControlPanel.Engine.SolutionInstallConfig().Save(configPath, "synthetic-password");
                var reportPath = Path.Combine(outputDirectory, "private-review.json");
                var calls = 0;
                var args = new[] { App.ControlPanel.Engine.PrivateNetworkReviewCommand.Operation, configPath, reportPath };
                await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
                    App.ControlPanel.Engine.PrivateNetworkReviewCommand.Run(args, "synthetic-password", CancellationToken.None,
                        (config, ct) =>
                        {
                            calls++;
                            if (junction)
                            {
                                Directory.Delete(outputDirectory);
                                CreateReviewJunction(outputDirectory, directory);
                            }
                            else File.WriteAllText(Path.Combine(outputDirectory, ".git"), "gitdir: synthetic-contoso-git-metadata");
                            return Task.FromResult(new PrivateNetworkReview());
                        }));
                Assert.AreEqual(1, calls);
                Assert.IsFalse(File.Exists(reportPath), "A path changed during inventory must not receive the private report.");
            }
            finally
            {
                if (junction && Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory);
                Directory.Delete(directory, true);
            }
        }

        private static string CreatePrivateReviewDirectory()
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "m365-network-review-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            DataUtils.PrivateArtifactPath.Validate(Path.Combine(directory, "private-review.json"));
            return directory;
        }

        private static void CreateReviewJunction(string link, string target)
        {
            using (var process = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec"),
                "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode, "Could not create synthetic junction: " + stdout + stderr);
            }
        }

        private sealed class NetworkingHandler : HttpMessageHandler
        {
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
                => SendAsync(request, ct).GetAwaiter().GetResult();

            public Dictionary<string, JObject> Stored = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            public List<string> Deletes = new List<string>();
            public List<HttpMethod> Methods = new List<HttpMethod>();
            public string DeniedSuffix;
            public int Puts;
            public bool RecordPresent, ForeignRecordPresent, SawConditionalLinkDelete;
            public NetworkingHandler(string token)
            {
                var endpoint = JObject.Parse(EndpointHandler.EndpointJson);
                endpoint["tags"][PrivateNetworkProvenance.OwnerTag] = token;
                Stored.Add(EndpointId, endpoint);
                Stored.Add(SqlId, JObject.Parse("{\"id\":\"" + SqlId + "\",\"name\":\"contoso-sql\",\"type\":\"Microsoft.Sql/servers\",\"location\":\"westeurope\",\"properties\":{\"publicNetworkAccess\":\"Disabled\",\"provisioningState\":\"Succeeded\"}}"));
            }
            public void SeedForeignDns()
            {
                var linkId = ZoneId + "/virtualNetworkLinks/privatelink.database.windows.net-vnet-link";
                Stored[ZoneId] = JObject.Parse("{\"id\":\"" + ZoneId + "\",\"name\":\"privatelink.database.windows.net\",\"type\":\"Microsoft.Network/privateDnsZones\",\"location\":\"global\",\"tags\":{},\"properties\":{}}");
                Stored[linkId] = JObject.Parse("{\"id\":\"" + linkId + "\",\"name\":\"privatelink.database.windows.net-vnet-link\",\"type\":\"Microsoft.Network/privateDnsZones/virtualNetworkLinks\",\"location\":\"global\",\"tags\":{},\"properties\":{\"virtualNetwork\":{\"id\":\"" + new ResourceIdentifier(SubnetId).Parent + "\"},\"registrationEnabled\":false}}");
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Methods.Add(request.Method);
                var path = request.RequestUri.AbsolutePath;
                if (DeniedSuffix != null && path.EndsWith(DeniedSuffix, StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.Forbidden, new JObject
                    {
                        ["error"] = new JObject { ["code"] = "AuthorizationFailed" }
                    });
                if (request.Method == HttpMethod.Get && path == ResourceGroupId + "/resources")
                    return Json(HttpStatusCode.OK, new JObject
                    {
                        ["value"] = new JArray(Stored.Values.Where(v =>
                            ((string)v["type"])?.StartsWith("Microsoft.Cache/", StringComparison.OrdinalIgnoreCase) == true))
                    });
                if (request.Method == HttpMethod.Put || request.Method.Method == "PATCH")
                {
                    Puts++;
                    var body = JObject.Parse(await request.Content.ReadAsStringAsync());
                    if (Stored.TryGetValue(path, out var current))
                        current.Merge(body, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                    else
                    {
                        var id = new ResourceIdentifier(path);
                        body["id"] = path;
                        body["name"] = id.Name;
                        body["type"] = id.ResourceType.ToString();
                        body["etag"] = "\"contoso-link-etag\"";
                        if (body["properties"] == null) body["properties"] = new JObject();
                        body["properties"]["provisioningState"] = "Succeeded";
                        Stored[path] = body;
                    }
                    if (path.Contains("/privateDnsZoneGroups/"))
                    {
                        RecordPresent = true;
                        Stored[path]["properties"]["privateDnsZoneConfigs"][0]["properties"]["recordSets"] = new JArray(
                            new JObject
                            {
                                ["recordType"] = "A", ["recordSetName"] = "contoso-sql",
                                ["fqdn"] = "contoso-sql.privatelink.database.windows.net",
                                ["ttl"] = 10, ["ipAddresses"] = new JArray("10.0.0.4")
                            });
                    }
                    return Json(HttpStatusCode.OK, Stored[path]);
                }
                if (request.Method == HttpMethod.Delete)
                {
                    Deletes.Add(path);
                    if (path.Contains("/privateDnsZoneGroups/")) RecordPresent = false;
                    if (path.Contains("/virtualNetworkLinks/"))
                    {
                        SawConditionalLinkDelete = request.Headers.IfMatch.Single().Tag == "\"contoso-link-etag\"";
                        Assert.IsTrue(SawConditionalLinkDelete);
                    }
                    Stored.Remove(path);
                    return Json(HttpStatusCode.OK, new JObject());
                }
                if (Stored.TryGetValue(path, out var resource)) return Json(HttpStatusCode.OK, resource);
                var values = new JArray(Stored.Where(p => p.Key.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase) &&
                    p.Key.Substring(path.Length + 1).IndexOf('/') < 0).Select(p => p.Value));
                if ((path.EndsWith("/ALL", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/A", StringComparison.OrdinalIgnoreCase)) && RecordPresent)
                    values.Add(new JObject
                    {
                        ["id"] = ZoneId + "/A/contoso-sql", ["name"] = "contoso-sql", ["type"] = "Microsoft.Network/privateDnsZones/A",
                        ["properties"] = new JObject
                        {
                            ["ttl"] = 10, ["aRecords"] = new JArray(new JObject { ["ipv4Address"] = "10.0.0.4" })
                        }
                    });
                if ((path.EndsWith("/ALL", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/A", StringComparison.OrdinalIgnoreCase)) && ForeignRecordPresent)
                    values.Add(new JObject
                    {
                        ["id"] = ZoneId + "/A/foreign-service", ["name"] = "foreign-service", ["type"] = "Microsoft.Network/privateDnsZones/A",
                        ["properties"] = new JObject
                        {
                            ["ttl"] = 10, ["aRecords"] = new JArray(new JObject { ["ipv4Address"] = "10.0.0.9" })
                        }
                    });
                if (path.EndsWith("/privateEndpoints") || path.EndsWith("/privateDnsZones") ||
                    path.EndsWith("/privateDnsZoneGroups") || path.EndsWith("/virtualNetworkLinks") ||
                    path.EndsWith("/ALL", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/A", StringComparison.OrdinalIgnoreCase))
                    return Json(HttpStatusCode.OK, new JObject { ["value"] = values });
                return Json(HttpStatusCode.NotFound, new JObject { ["error"] = new JObject { ["code"] = "ResourceNotFound" } });
            }
            private static HttpResponseMessage Json(HttpStatusCode status, JObject body) =>
                new HttpResponseMessage(status) { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
        }

        private sealed class ResourceHandler : HttpMessageHandler
        {
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
                => SendAsync(request, ct).GetAwaiter().GetResult();

            public JObject Stored;
            public int Mutations, Reads;
            public int IntegrationDeletes;
            public bool IgnoreChanges, FailPatchOnce;
            public HttpStatusCode FailureStatus = HttpStatusCode.InternalServerError;
            public string ReadErrorCode;
            private readonly string _id;
            public ResourceHandler(string id, string type)
            {
                _id = id;
                Stored = new JObject
                {
                    ["id"] = id, ["name"] = "contoso", ["type"] = type, ["location"] = "westeurope",
                    ["kind"] = "OpenAI", ["tags"] = new JObject { ["Contoso"] = "preserve" },
                    ["sku"] = new JObject { ["name"] = "Premium", ["tier"] = "Premium", ["capacity"] = 1 },
                    ["properties"] = new JObject
                    {
                        ["publicNetworkAccess"] = type == "Microsoft.Automation/automationAccounts" ? (JToken)false : "Disabled",
                        ["disableLocalAuth"] = true, ["provisioningState"] = "Succeeded",
                        ["tenantId"] = "00000000-0000-0000-0000-000000000000",
                        ["sku"] = new JObject { ["name"] = "standard", ["family"] = "A" },
                        ["networkAcls"] = new JObject
                        {
                            ["defaultAction"] = "Allow", ["bypass"] = "AzureServices",
                            ["virtualNetworkRules"] = new JArray(), ["ipRules"] = new JArray()
                        }
                    }
                };
                if (type == "Microsoft.Web/sites") Stored["properties"]["sku"] = "B1";
            }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.RequestUri.AbsolutePath == _id + "/networkConfig/virtualNetwork" && request.Method == HttpMethod.Delete)
                {
                    IntegrationDeletes++;
                    Stored["properties"]["virtualNetworkSubnetId"] = null;
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                }
                if (request.RequestUri.AbsolutePath == _id + "/config/web" && request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(new JObject
                    {
                        ["id"] = _id + "/config/web", ["name"] = "web",
                        ["properties"] = Stored["properties"]["siteConfig"].DeepClone()
                    }.ToString(), Encoding.UTF8, "application/json") };
                Assert.AreEqual(_id, request.RequestUri.AbsolutePath, "No other resource may be mutated/read in this adapter test.");
                if (request.Method == HttpMethod.Get)
                {
                    Reads++;
                    if (ReadErrorCode != null)
                        return new HttpResponseMessage(HttpStatusCode.NotFound)
                        { Content = new StringContent("{\"error\":{\"code\":\"" + ReadErrorCode + "\"}}", Encoding.UTF8, "application/json") };
                }
                else
                {
                    Mutations++;
                    if (FailPatchOnce)
                    {
                        FailPatchOnce = false;
                        return new HttpResponseMessage(FailureStatus)
                        { Content = new StringContent(FailureStatus == HttpStatusCode.NotFound ?
                            "{\"error\":{\"code\":\"ResourceNotFound\"}}" : "{\"error\":{\"code\":\"SyntheticFailure\"}}",
                            Encoding.UTF8, "application/json") };
                    }
                    var sent = JObject.Parse(await request.Content.ReadAsStringAsync());
                    if (!IgnoreChanges) Stored.Merge(sent, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(Stored.ToString(), Encoding.UTF8, "application/json") };
            }
        }

        private static PrivateEndpointInstallTask EndpointTask(ResourceGroupResource group, PrivateNetworkProvenance provenance) =>
            new PrivateEndpointInstallTask(TaskConfig.GetConfigForName("pe-contoso-sql-sql")
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_TARGET_RESOURCE_ID, SqlId)
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_GROUP_ID, "sqlServer")
                .AddSetting(PrivateEndpointInstallTask.CONFIG_KEY_SUBNET_ID, SubnetId),
                NullLogger.Instance, new AzureLocation("westeurope"), new Dictionary<string, string>())
            { Container = group, Networking = provenance };

        private static ResourceGroupResource Group(HttpClient http)
        {
            var options = new ArmClientOptions { Transport = new HttpClientTransport(http) };
            options.Retry.MaxRetries = 0;
            return new ArmClient(new SyntheticCredential(), "00000000-0000-0000-0000-000000000000", options)
                .GetResourceGroupResource(new ResourceIdentifier(ResourceGroupId));
        }

        private sealed class SyntheticCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new AccessToken("synthetic", DateTimeOffset.MaxValue);
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
                new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private sealed class EndpointHandler : HttpMessageHandler
        {
            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct)
                => SendAsync(request, ct).GetAwaiter().GetResult();

            public const string EndpointJson = "{\"id\":\"" + EndpointId + "\",\"name\":\"pe-contoso-sql-sql\",\"type\":\"Microsoft.Network/privateEndpoints\",\"location\":\"westeurope\",\"tags\":{},\"properties\":{\"provisioningState\":\"Succeeded\",\"subnet\":{\"id\":\"" + SubnetId + "\"},\"privateLinkServiceConnections\":[{\"name\":\"contoso\",\"properties\":{\"privateLinkServiceId\":\"" + SqlId + "\",\"groupIds\":[\"sqlServer\"],\"privateLinkServiceConnectionState\":{\"status\":\"Approved\"}}}]}}";
            public JObject Stored;
            public int Puts;
            public bool FailPutOnce, ListMode, FailReads;
            public Action BeforePut;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (request.Method == HttpMethod.Put)
                {
                    BeforePut?.Invoke();
                    Puts++;
                    var sent = JObject.Parse(await request.Content.ReadAsStringAsync());
                    Stored = JObject.Parse(EndpointJson);
                    Stored["tags"] = sent["tags"].DeepClone();
                    if (FailPutOnce)
                    {
                        FailPutOnce = false;
                        return Json(HttpStatusCode.InternalServerError, "{\"error\":{\"code\":\"SyntheticFailure\",\"message\":\"Contoso synthetic failure\"}}");
                    }
                    return Json(HttpStatusCode.OK, Stored.ToString());
                }
                if (FailReads) return Json(HttpStatusCode.Forbidden, "{\"error\":{\"code\":\"Forbidden\",\"message\":\"Synthetic denied read\"}}");
                if (ListMode)
                {
                    if (request.RequestUri.AbsolutePath.EndsWith("/privateEndpoints"))
                        return Json(HttpStatusCode.OK, new JObject { ["value"] = new JArray(Stored) }.ToString());
                    return Json(HttpStatusCode.OK, "{\"value\":[]}");
                }
                return Stored == null ? Json(HttpStatusCode.NotFound, "{\"error\":{\"code\":\"ResourceNotFound\"}}") :
                    Json(HttpStatusCode.OK, Stored.ToString());
            }
            private static HttpResponseMessage Json(HttpStatusCode status, string body) => new HttpResponseMessage(status)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private sealed class Fixture
        {
            public PrivateNetworkManifest Manifest = new PrivateNetworkManifest();
            public FakeClient Client = new FakeClient();
            public List<string> Saved = new List<string>();
            public bool FailSave;
            public Fixture(bool owned = true)
            {
                if (!owned) return;
                var provenance = new PrivateNetworkProvenance(Manifest, ResourceGroupId, () => { });
                var endpoint = new PrivateNetworkArtifact
                {
                    Id = EndpointId, Kind = PrivateNetworkProvenance.Endpoint, TargetId = SqlId,
                    GroupId = "sqlServer", SubnetId = SubnetId
                };
                var group = new PrivateNetworkArtifact
                {
                    Id = EndpointId + "/privateDnsZoneGroups/contoso-group", Kind = PrivateNetworkProvenance.ZoneGroup,
                    TargetId = EndpointId, ZoneIds = new List<string> { ZoneId },
                    Records = new List<PrivateNetworkRecord> { new PrivateNetworkRecord
                    {
                        ZoneId = ZoneId, Name = "contoso-sql", Type = "A",
                        Addresses = new List<string> { "10.0.0.4" }, CreatedExclusively = true
                    }}
                };
                var link = new PrivateNetworkArtifact
                {
                    Id = ZoneId + "/virtualNetworkLinks/contoso-link", Kind = PrivateNetworkProvenance.Link,
                    TargetId = ResourceGroupId + "/providers/Microsoft.Network/virtualNetworks/contoso-vnet",
                    ZoneIds = new List<string> { ZoneId }
                };
                foreach (var artifact in new[] { endpoint, group, link })
                {
                    provenance.BeginCreate(artifact);
                    provenance.ConfirmCreate(artifact.Id);
                    Client.Live.Add(artifact.Id, new PrivateNetworkSnapshot
                    {
                        Artifact = JsonConvert.DeserializeObject<PrivateNetworkArtifact>(JsonConvert.SerializeObject(artifact)),
                        Tags = artifact.Kind == PrivateNetworkProvenance.ZoneGroup ? null :
                            new Dictionary<string, string> { [PrivateNetworkProvenance.OwnerTag] = Manifest.DeploymentToken }
                    });
                }
                Client.Live[EndpointId].Children.Add(JsonConvert.DeserializeObject<PrivateNetworkArtifact>(JsonConvert.SerializeObject(group)));
                Manifest.AppliedTopology = "Private";
            }
            public Task Run(bool vnet = false) => new PrivateNetworkTeardown(
                new PrivateNetworkProvenance(Manifest, ResourceGroupId, () =>
                {
                    if (FailSave) throw new InvalidOperationException("Synthetic checkpoint failed.");
                    Saved.Add(JsonConvert.SerializeObject(Manifest));
                }), Client, NullLogger.Instance).Run(vnet, CancellationToken.None);
        }

        private sealed class FakeClient : IPrivateNetworkCleanupClient
        {
            public Dictionary<string, PrivateNetworkSnapshot> Live = new Dictionary<string, PrivateNetworkSnapshot>();
            public List<string> Mutations = new List<string>();
            public bool SharedZone, FailEndpointOnce, RecordPresent = true, LeaveRecords, ThrowOnRead;
            public Task<IReadOnlyList<PrivateNetworkSnapshot>> DiscoverEndpoints(CancellationToken ct)
            {
                if (ThrowOnRead) throw new InvalidOperationException("Hybrid must not discover.");
                return Task.FromResult<IReadOnlyList<PrivateNetworkSnapshot>>(Live.Values
                    .Where(a => a.Artifact.Kind == PrivateNetworkProvenance.Endpoint).ToList());
            }
            public Task<PrivateNetworkSnapshot> Read(PrivateNetworkArtifact a, CancellationToken ct) =>
                Task.FromResult(Live.TryGetValue(a.Id, out var snapshot) ? snapshot : null);
            public Task ValidatePublicTarget(string target, CancellationToken ct) => Task.CompletedTask;
            public Task EnablePublicTarget(string target, CancellationToken ct)
            { Mutations.Add("Public:" + target); return Task.CompletedTask; }
            public Task ValidateRecords(PrivateNetworkArtifact group, bool removed, CancellationToken ct)
            {
                if (removed == RecordPresent) throw PrivateNetworkProvenance.Blocked(group.Id, "synthetic stale/shared DNS");
                return Task.CompletedTask;
            }
            public Task<bool> ZoneHasOtherRecords(string zoneId, CancellationToken ct) => Task.FromResult(SharedZone);
            public Task Delete(PrivateNetworkArtifact a, CancellationToken ct)
            {
                if (a.Kind == PrivateNetworkProvenance.Endpoint && FailEndpointOnce)
                { FailEndpointOnce = false; throw new RequestFailedException(500, "Synthetic delete failure"); }
                Mutations.Add("Delete:" + a.Kind);
                Live.Remove(a.Id);
                if (a.Kind == PrivateNetworkProvenance.ZoneGroup)
                {
                    if (!LeaveRecords) RecordPresent = false;
                    if (Live.TryGetValue(EndpointId, out var endpoint)) endpoint.Children.Clear();
                }
                return Task.CompletedTask;
            }
        }
    }
}
