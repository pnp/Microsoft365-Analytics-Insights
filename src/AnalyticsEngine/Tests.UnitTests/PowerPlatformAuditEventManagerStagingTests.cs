using Common.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnitTests.FakeLoaderClasses;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI.PowerPlatform;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the Power Platform staging rules that issue #367 freed from the SQL dependency -
    /// share-record expansion and the app display-name fallback. Both decide what rows get written, so
    /// before the <c>IPowerPlatformStagingWriter</c> seam existed they could only be checked against a
    /// live database.
    ///
    /// Runs with zero SQL Server, Graph, Redis or Service Bus.
    /// </summary>
    [TestClass]
    public class PowerPlatformAuditEventManagerStagingTests
    {
        private static CommonAuditEvent NewEvent()
        {
            return new CommonAuditEvent
            {
                TimeStamp = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc),
                Operation = new EventOperation { Name = "ShareApp" },
                User = new User { AzureAdId = "00000000-0000-0000-0000-000000000000", UserPrincipalName = "chris@contoso.onmicrosoft.com" },
                Id = Guid.NewGuid()
            };
        }

        private static PowerPlatformAuditEventManager NewManager(InMemoryPowerPlatformStagingWriter writer)
        {
            return new PowerPlatformAuditEventManager(writer, NullLogger.Instance);
        }

        [TestMethod]
        public async Task PowerPlatform_ShareEvent_StagesOneRowPerPermission_AndSkipsEmptyPrincipalName()
        {
            var writer = new InMemoryPowerPlatformStagingWriter();
            var record = new PowerAppsAuditLogContent
            {
                AppName = "contoso-expenses-app",
                AppDisplayName = "Contoso Expenses",
                Permissions = new List<PowerPlatformPermissionEntry>
                {
                    new PowerPlatformPermissionEntry { PrincipalName = "alex@contoso.onmicrosoft.com", RoleName = "CanEdit" },
                    new PowerPlatformPermissionEntry { PrincipalName = null,                            RoleName = "CanView" },
                    new PowerPlatformPermissionEntry { PrincipalName = string.Empty,                    RoleName = "CanView" },
                    new PowerPlatformPermissionEntry { PrincipalName = "καλημέρα@contoso.onmicrosoft.com", RoleName = "CanView" },
                }
            };

            await NewManager(writer).SaveSinglePowerAppEventToSqlStaging(record, NewEvent());

            Assert.AreEqual(1, writer.PowerAppRows.Count, "The app event itself is staged once.");
            Assert.AreEqual(2, writer.PowerAppShareRows.Count, "One share row per usable recipient; blanks are skipped.");
            CollectionAssert.AreEquivalent(
                new[] { "alex@contoso.onmicrosoft.com", "καλημέρα@contoso.onmicrosoft.com" },
                new List<string> { writer.PowerAppShareRows[0].SharedWithUpn, writer.PowerAppShareRows[1].SharedWithUpn },
                "Non-Latin recipients must survive intact.");
        }

        [TestMethod]
        public async Task PowerPlatform_NoPermissions_StagesNoShareRows()
        {
            var writer = new InMemoryPowerPlatformStagingWriter();

            await NewManager(writer).SaveSinglePowerAppEventToSqlStaging(
                new PowerAppsAuditLogContent { AppName = "contoso-expenses-app", Permissions = null }, NewEvent());

            Assert.AreEqual(1, writer.PowerAppRows.Count);
            Assert.AreEqual(0, writer.PowerAppShareRows.Count);
        }

        [TestMethod]
        public async Task PowerPlatform_AppDisplayNameMissing_FallsBackToAppName()
        {
            var writer = new InMemoryPowerPlatformStagingWriter();

            await NewManager(writer).SaveSinglePowerAppEventToSqlStaging(
                new PowerAppsAuditLogContent { AppName = "contoso-expenses-app", AppDisplayName = null }, NewEvent());
            await NewManager(writer).SaveSinglePowerAppEventToSqlStaging(
                new PowerAppsAuditLogContent { AppName = "contoso-hr-app", AppDisplayName = string.Empty }, NewEvent());

            Assert.AreEqual("contoso-expenses-app", writer.PowerAppRows[0].AppName,
                "With no display name the report would otherwise show a blank app.");
            Assert.AreEqual("contoso-hr-app", writer.PowerAppRows[1].AppName,
                "Empty must fall back too, not just null.");
        }

        [TestMethod]
        public async Task PowerPlatform_AppDisplayNamePresent_IsPreferred()
        {
            var writer = new InMemoryPowerPlatformStagingWriter();

            await NewManager(writer).SaveSinglePowerAppEventToSqlStaging(
                new PowerAppsAuditLogContent { AppName = "contoso-expenses-app", AppDisplayName = "Contoso Expenses" }, NewEvent());

            Assert.AreEqual("Contoso Expenses", writer.PowerAppRows[0].AppName);
            Assert.AreEqual("contoso-expenses-app", writer.PowerAppRows[0].AppId, "The raw name is still kept as the id.");
        }

        [TestMethod]
        public async Task PowerPlatform_NullRecordOrEvent_StagesNothing()
        {
            var writer = new InMemoryPowerPlatformStagingWriter();
            var manager = NewManager(writer);

            await manager.SaveSinglePowerAppEventToSqlStaging(null, NewEvent());
            await manager.SaveSinglePowerAppEventToSqlStaging(new PowerAppsAuditLogContent { AppName = "x" }, null);

            Assert.AreEqual(0, writer.PowerAppRows.Count);
            Assert.AreEqual(0, writer.PowerAppShareRows.Count);
        }

        [TestMethod]
        [ExpectedException(typeof(ArgumentNullException))]
        public void PowerPlatform_NullStagingWriter_Throws()
        {
            new PowerPlatformAuditEventManager((IPowerPlatformStagingWriter)null, NullLogger.Instance);
        }
    }
}
