using Azure.Core;
using Azure.ResourceManager.Models;
using CloudInstallEngine.Azure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Covers <see cref="ManagedIdentityPlanner"/>, which decides what goes in the <c>identity</c> block
    /// when the installer enables a system-assigned managed identity on a resource that already exists.
    ///
    /// ARM replaces that block wholesale instead of merging it, so the naive "just send SystemAssigned"
    /// patch deletes any user-assigned identity an operator attached - silently, and on every re-run of
    /// the installer. The opposite mistake is just as real: always sending SystemAssignedUserAssigned
    /// fails outright when there are no user-assigned identities to list. Both directions are asserted.
    /// </summary>
    [TestClass]
    public class ManagedIdentityPlannerTests
    {
        private static ResourceIdentifier UserAssignedId(string name)
        {
            return new ResourceIdentifier(
                "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-contoso"
                + "/providers/Microsoft.ManagedIdentity/userAssignedIdentities/" + name);
        }

        [TestMethod]
        public void NoIdentityAtAll_NeedsOne()
        {
            Assert.IsTrue(ManagedIdentityPlanner.NeedsSystemAssignedIdentity(null));
        }

        [TestMethod]
        public void UserAssignedOnly_NeedsASystemAssignedOne()
        {
            var existing = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned);
            existing.UserAssignedIdentities[UserAssignedId("contoso-uami")] = new UserAssignedIdentity();

            Assert.IsTrue(ManagedIdentityPlanner.NeedsSystemAssignedIdentity(existing),
                "A user-assigned-only identity has no top-level PrincipalId, so the runbooks still have "
                + "no identity of their own to authenticate with.");
        }

        [TestMethod]
        public void NoExistingIdentity_SendsPlainSystemAssigned()
        {
            var planned = ManagedIdentityPlanner.BuildSystemAssignedPreservingUserAssigned(null);

            Assert.AreEqual(ManagedServiceIdentityType.SystemAssigned, planned.ManagedServiceIdentityType,
                "ARM rejects SystemAssignedUserAssigned when the userAssignedIdentities map is empty, so "
                + "the common case must stay on the plain type.");
            Assert.AreEqual(0, planned.UserAssignedIdentities.Count);
        }

        [TestMethod]
        public void ExistingIdentityWithNoUserAssigned_SendsPlainSystemAssigned()
        {
            var existing = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssigned);

            var planned = ManagedIdentityPlanner.BuildSystemAssignedPreservingUserAssigned(existing);

            Assert.AreEqual(ManagedServiceIdentityType.SystemAssigned, planned.ManagedServiceIdentityType);
            Assert.AreEqual(0, planned.UserAssignedIdentities.Count);
        }

        [TestMethod]
        public void ExistingUserAssignedIdentities_ArePreserved()
        {
            var first = UserAssignedId("contoso-uami-1");
            var second = UserAssignedId("contoso-uami-2");
            var existing = new ManagedServiceIdentity(ManagedServiceIdentityType.UserAssigned);
            existing.UserAssignedIdentities[first] = new UserAssignedIdentity();
            existing.UserAssignedIdentities[second] = new UserAssignedIdentity();

            var planned = ManagedIdentityPlanner.BuildSystemAssignedPreservingUserAssigned(existing);

            Assert.AreEqual(ManagedServiceIdentityType.SystemAssignedUserAssigned,
                planned.ManagedServiceIdentityType,
                "Dropping to plain SystemAssigned here is what deleted the operator's identities.");
            Assert.AreEqual(2, planned.UserAssignedIdentities.Count);
            Assert.IsTrue(planned.UserAssignedIdentities.ContainsKey(first));
            Assert.IsTrue(planned.UserAssignedIdentities.ContainsKey(second));
        }

        [TestMethod]
        public void PreservedUserAssignedIdentities_AreSentAsEmptyValues()
        {
            var id = UserAssignedId("contoso-uami");
            var existing = new ManagedServiceIdentity(ManagedServiceIdentityType.SystemAssignedUserAssigned);
            existing.UserAssignedIdentities[id] = new UserAssignedIdentity();

            var planned = ManagedIdentityPlanner.BuildSystemAssignedPreservingUserAssigned(existing);

            // clientId / principalId are read-only outputs; the request references the identity by
            // resource id alone, so the value must carry nothing back to ARM.
            Assert.IsNotNull(planned.UserAssignedIdentities[id]);
            Assert.IsNull(planned.UserAssignedIdentities[id].ClientId);
            Assert.IsNull(planned.UserAssignedIdentities[id].PrincipalId);
        }
    }
}
