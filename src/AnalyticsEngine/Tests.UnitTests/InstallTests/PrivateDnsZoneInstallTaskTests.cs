using Azure;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class PrivateDnsZoneInstallTaskTests
    {
        private const string DefaultLinkName = "privatelink.contoso.example-vnet-link";
        private const string VnetId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-network/providers/Microsoft.Network/virtualNetworks/contoso-vnet";
        private const string SameVnetIdDifferentCase = "/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/contoso-network/providers/microsoft.network/virtualnetworks/contoso-vnet";
        private const string OtherVnetId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-network/providers/Microsoft.Network/virtualNetworks/other-vnet";
        private const string IntendedZoneId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-network/providers/Microsoft.Network/privateDnsZones/privatelink.contoso.example";
        private const string OtherZoneId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/contoso-network/providers/Microsoft.Network/privateDnsZones/privatelink.other.example";

        [TestMethod]
        public void FindVNetLinkForVirtualNetwork_DefaultNamedLink_MatchesByVnetId()
        {
            var links = new[]
            {
                new PrivateDnsZoneInstallTask.VNetLinkInfo(DefaultLinkName, VnetId),
            };

            var result = PrivateDnsZoneInstallTask.FindVNetLinkForVirtualNetwork(links, VnetId);

            Assert.IsNotNull(result);
            Assert.AreEqual(DefaultLinkName, result.Name);
        }

        [TestMethod]
        public void FindVNetLinkForVirtualNetwork_DifferentlyNamedLink_MatchesSameVnetCaseInsensitively()
        {
            var links = new[]
            {
                new PrivateDnsZoneInstallTask.VNetLinkInfo("portal-created-link", SameVnetIdDifferentCase),
            };

            var result = PrivateDnsZoneInstallTask.FindVNetLinkForVirtualNetwork(links, VnetId);

            Assert.IsNotNull(result);
            Assert.AreEqual("portal-created-link", result.Name);
        }

        [TestMethod]
        public void FindVNetLinkForVirtualNetwork_DifferentVnet_DoesNotMatch()
        {
            var links = new[]
            {
                new PrivateDnsZoneInstallTask.VNetLinkInfo("other-link", OtherVnetId),
            };

            var result = PrivateDnsZoneInstallTask.FindVNetLinkForVirtualNetwork(links, VnetId);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void DecideZoneGroupAction_NoZoneGroup_CreatesDefault()
        {
            var result = PrivateDnsZoneInstallTask.DecideZoneGroupAction(
                new List<PrivateDnsZoneInstallTask.ZoneGroupInfo>(),
                IntendedZoneId);

            Assert.AreEqual(PrivateDnsZoneInstallTask.ZoneGroupAction.Create, result.Action);
            Assert.IsNull(result.ExistingGroup);
        }

        [TestMethod]
        public void DecideZoneGroupAction_DifferentlyNamedGroupPointingAtIntendedZone_Reuses()
        {
            var result = PrivateDnsZoneInstallTask.DecideZoneGroupAction(
                new[]
                {
                    new PrivateDnsZoneInstallTask.ZoneGroupInfo("default", new[] { IntendedZoneId }),
                },
                IntendedZoneId.ToUpperInvariant());

            Assert.AreEqual(PrivateDnsZoneInstallTask.ZoneGroupAction.Reuse, result.Action);
            Assert.AreEqual("default", result.ExistingGroup.Name);
        }

        [TestMethod]
        public void DecideZoneGroupAction_DifferentlyNamedGroupPointingAtWrongZone_Recreates()
        {
            var result = PrivateDnsZoneInstallTask.DecideZoneGroupAction(
                new[]
                {
                    new PrivateDnsZoneInstallTask.ZoneGroupInfo("default", new[] { OtherZoneId }),
                },
                IntendedZoneId);

            Assert.AreEqual(PrivateDnsZoneInstallTask.ZoneGroupAction.Recreate, result.Action);
            Assert.AreEqual("default", result.ExistingGroup.Name);
        }

        [TestMethod]
        public void IsVNetLinkAlreadyPresentConflict_MatchesConflictErrorCodeEvenWithStatus200()
        {
            var lroConflict = new RequestFailedException(200, "Long-running operation reported a conflict.", "Conflict", null);
            var statusConflictWithoutErrorCode = new RequestFailedException(409, "Conflict.", "AnotherConflictShape", null);

            Assert.IsTrue(PrivateDnsZoneInstallTask.IsVNetLinkAlreadyPresentConflict(lroConflict));
            Assert.IsFalse(PrivateDnsZoneInstallTask.IsVNetLinkAlreadyPresentConflict(statusConflictWithoutErrorCode));
        }

        [TestMethod]
        public void IsZoneGroupAlreadyPresentConflict_MatchesAzureZoneGroupLimitErrorCode()
        {
            var alreadyHasGroup = new RequestFailedException(
                400,
                "A private endpoint can have only one private DNS zone group.",
                "MoreThanOnePrivateDnsZoneGroupPerPrivateEndpointNotAllowed",
                null);
            var genericBadRequest = new RequestFailedException(400, "Bad request.", "BadRequest", null);

            Assert.IsTrue(PrivateDnsZoneInstallTask.IsZoneGroupAlreadyPresentConflict(alreadyHasGroup));
            Assert.IsFalse(PrivateDnsZoneInstallTask.IsZoneGroupAlreadyPresentConflict(genericBadRequest));
        }
    }
}
