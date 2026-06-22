using CloudInstallEngine.Azure;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;

namespace Tests.UnitTests.InstallTests
{
    /// <summary>
    /// Pure-logic tests for <see cref="RbacPermissionProbe"/>'s evaluation of whether an account can create
    /// role assignments (the installer "Test Configuration" pre-flight check). No network is required - the live
    /// ARM call is exercised separately; these cover the Azure RBAC wildcard / notActions matching that decides
    /// the pass/fail, using the real built-in role permission sets (Owner, User Access Administrator, RBAC
    /// Administrator, Contributor, Reader).
    /// </summary>
    [TestClass]
    public class RbacPermissionProbeTests
    {
        private const string WriteAction = RbacPermissionProbe.RoleAssignmentWriteAction; // Microsoft.Authorization/roleAssignments/write

        private static RbacPermissionEntry Entry(string[] actions, string[] notActions = null) =>
            new RbacPermissionEntry
            {
                Actions = new List<string>(actions),
                NotActions = new List<string>(notActions ?? new string[0])
            };

        #region WildcardMatches

        [TestMethod]
        public void WildcardMatches_StarMatchesEverything()
        {
            Assert.IsTrue(RbacPermissionProbe.WildcardMatches(WriteAction, "*"));
        }

        [TestMethod]
        public void WildcardMatches_ProviderWildcardsMatch()
        {
            Assert.IsTrue(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Authorization/*"));
            Assert.IsTrue(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Authorization/roleAssignments/*"));
            Assert.IsTrue(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Authorization/*/Write"));
        }

        [TestMethod]
        public void WildcardMatches_ExactMatchIsCaseInsensitive()
        {
            Assert.IsTrue(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Authorization/roleAssignments/WRITE"));
        }

        [TestMethod]
        public void WildcardMatches_NonMatchingPatterns()
        {
            Assert.IsFalse(RbacPermissionProbe.WildcardMatches(WriteAction, "*/read"));
            Assert.IsFalse(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Storage/*"));
            Assert.IsFalse(RbacPermissionProbe.WildcardMatches(WriteAction, "Microsoft.Authorization/roleDefinitions/write"));
            Assert.IsFalse(RbacPermissionProbe.WildcardMatches(WriteAction, ""));
            Assert.IsFalse(RbacPermissionProbe.WildcardMatches(WriteAction, null));
        }

        #endregion

        #region ActionIsAllowed - built-in roles

        [TestMethod]
        public void Owner_CanAssignRoles()
        {
            // Owner: actions ["*"], no notActions.
            var perms = new[] { Entry(new[] { "*" }) };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void UserAccessAdministrator_CanAssignRoles()
        {
            var perms = new[] { Entry(new[] { "*/read", "Microsoft.Authorization/*", "Microsoft.Support/*" }) };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void RbacAdministrator_CanAssignRoles()
        {
            // Role Based Access Control Administrator (abridged): grants roleAssignments writes explicitly.
            var perms = new[] { Entry(new[] { "Microsoft.Authorization/roleAssignments/write", "Microsoft.Authorization/roleAssignments/delete", "*/read" }) };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void Contributor_CannotAssignRoles()
        {
            // Contributor: actions ["*"] but notActions excludes Authorization writes/deletes - the canonical
            // failure mode this whole feature is designed to catch.
            var perms = new[] { Entry(
                new[] { "*" },
                new[] { "Microsoft.Authorization/*/Delete", "Microsoft.Authorization/*/Write", "Microsoft.Authorization/elevateAccess/Action" }) };
            Assert.IsFalse(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void Reader_CannotAssignRoles()
        {
            var perms = new[] { Entry(new[] { "*/read" }) };
            Assert.IsFalse(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void CustomRole_WithExactAction_CanAssignRoles()
        {
            var perms = new[] { Entry(new[] { "Microsoft.Authorization/roleAssignments/write", "Microsoft.Resources/deployments/*" }) };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        #endregion

        #region ActionIsAllowed - multiple roles & edge cases

        [TestMethod]
        public void MultipleRoles_AnyGrantingEntryWins()
        {
            // Reader + a custom role that grants the write -> allowed.
            var perms = new[]
            {
                Entry(new[] { "*/read" }),
                Entry(new[] { "Microsoft.Authorization/roleAssignments/*" })
            };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        [TestMethod]
        public void ContributorPlusUserAccessAdministrator_CanAssignRoles()
        {
            // Contributor excludes the write, but a separate User Access Administrator assignment grants it.
            var contributor = Entry(new[] { "*" }, new[] { "Microsoft.Authorization/*/Write", "Microsoft.Authorization/*/Delete" });
            var userAccessAdmin = Entry(new[] { "*/read", "Microsoft.Authorization/*" });
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, new[] { contributor, userAccessAdmin }));
        }

        [TestMethod]
        public void EmptyOrNullPermissions_NotAllowed()
        {
            Assert.IsFalse(RbacPermissionProbe.ActionIsAllowed(WriteAction, new RbacPermissionEntry[0]));
            Assert.IsFalse(RbacPermissionProbe.ActionIsAllowed(WriteAction, null));
        }

        [TestMethod]
        public void NullEntriesAreSkipped()
        {
            var perms = new RbacPermissionEntry[] { null, Entry(new[] { "*" }) };
            Assert.IsTrue(RbacPermissionProbe.ActionIsAllowed(WriteAction, perms));
        }

        #endregion
    }
}
