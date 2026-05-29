using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Entities.Serialisation;

namespace Tests.UnitTests
{
    /// <summary>
    /// Pure-deserialisation tests for the unified Power Platform admin activity record
    /// (Workload="PowerPlatform", RecordType=256, type=PowerPlatformAdministratorActivityRecord).
    /// These records store their event data inside a PropertyCollection rather than as top-level
    /// fields, so they have their own loader branch and mapping helpers in
    /// <see cref="PowerPlatformAdminActivityRecordContent"/>.
    /// </summary>
    [TestClass]
    public class PowerPlatformAdminActivityRecordParseTests
    {
        private readonly ILogger _logger = new LoggerFactory().CreateLogger("PowerPlatformParseTests");

        /// <summary>
        /// Real LaunchPowerApp event sample (sanitised) showing the new unified schema.
        /// Lifted from a tenant where the legacy "PowerApps" workload was never emitted.
        /// </summary>
        private const string LaunchPowerAppSampleJson = @"{
            ""PropertyCollection"": [
                { ""Name"": ""powerplatform.analytics.resource.power_app.display_name"", ""Value"": ""TestEmptyApp"" },
                { ""Name"": ""powerplatform.analytics.resource.power_app.id"", ""Value"": ""6c39e97c-6ddd-4a37-afd4-494a605925cf"" },
                { ""Name"": ""powerplatform.analytics.resource.environment.name"", ""Value"": ""dev-na-ba50088f"" },
                { ""Name"": ""powerplatform.analytics.operation.is_successful"", ""Value"": ""True"" },
                { ""Name"": ""powerplatform.analytics.correlation.id"", ""Value"": ""1fc61149-5c1d-4f0f-bacb-5772595172b3"" },
                { ""Name"": ""enduser.ip_address"", ""Value"": ""167.220.196.178"" },
                { ""Name"": ""user_agent.original"", ""Value"": ""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36"" },
                { ""Name"": ""powerplatform.analytics.resource.type"", ""Value"": ""PowerApp"" },
                { ""Name"": ""version"", ""Value"": ""1.0"" },
                { ""Name"": ""type"", ""Value"": ""PowerPlatformAdministratorActivityRecord"" },
                { ""Name"": ""powerplatform.analytics.activity.name"", ""Value"": ""LaunchPowerApp"" },
                { ""Name"": ""powerplatform.analytics.activity.id"", ""Value"": ""e2a84675-8952-4d91-a2ea-4d89f310f1a7"" },
                { ""Name"": ""powerplatform.analytics.resource.environment.id"", ""Value"": ""aa289efd-09f4-eaeb-bbb8-c08fc9e27d09"" },
                { ""Name"": ""enduser.id"", ""Value"": ""7ff1e0f1-e2a4-48a0-874e-34db43893df1"" },
                { ""Name"": ""powerplatform.analytics.resource.tenant.id"", ""Value"": ""33333333-3333-3333-3333-333333333333"" },
                { ""Name"": ""enduser.principal_name"", ""Value"": ""AmberR@CONTOSO.OnMicrosoft.com"" },
                { ""Name"": ""enduser.role"", ""Value"": ""Admin"" }
            ],
            ""EnvironmentId"": ""aa289efd-09f4-eaeb-bbb8-c08fc9e27d09"",
            ""UserId"": ""AmberR@CONTOSO.OnMicrosoft.com"",
            ""ClientIP"": ""167.220.196.178"",
            ""Id"": ""e2a84675-8952-4d91-a2ea-4d89f310f1a7"",
            ""RecordType"": 256,
            ""CreationTime"": ""2026-05-19T13:17:34"",
            ""Operation"": ""LaunchPowerApp"",
            ""OrganizationId"": ""33333333-3333-3333-3333-333333333333"",
            ""UserType"": 2,
            ""UserKey"": ""7ff1e0f1-e2a4-48a0-874e-34db43893df1"",
            ""Workload"": ""PowerPlatform"",
            ""ResultStatus"": ""Succeeded"",
            ""Version"": 1,
            ""RequiresCustomerKeyEncryption"": false
        }";

        [TestMethod]
        public void Workload_IsRecognisedAsPowerPlatform()
        {
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            var baseRecord = token.ToObject<WorkloadOnlyAuditLogContent>();

            Assert.AreEqual(ActivityImportConstants.WORKLOAD_POWER_PLATFORM, baseRecord.Workload,
                "Sample event must surface as the new unified PowerPlatform workload, otherwise it falls through every branch in ActivityReportWebLoader and is dropped.");
        }

        [TestMethod]
        public void Deserialises_TopLevel_And_PropertyCollection()
        {
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            Assert.IsNotNull(record);
            Assert.AreEqual("LaunchPowerApp", record.Operation);
            Assert.AreEqual(ActivityImportConstants.WORKLOAD_POWER_PLATFORM, record.Workload);

            Assert.IsNotNull(record.PropertyCollection);
            Assert.AreEqual(17, record.PropertyCollection.Count);

            // Case-insensitive lookup helper
            Assert.AreEqual("PowerApp", record.ResourceType);
            Assert.AreEqual("PowerApp", record.GetProperty("POWERPLATFORM.ANALYTICS.RESOURCE.TYPE"));
            Assert.IsNull(record.GetProperty("does-not-exist"));
        }

        [TestMethod]
        public void LaunchPowerApp_Maps_To_PowerAppsAuditLogContent()
        {
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsInstanceOfType(mapped, typeof(PowerAppsAuditLogContent),
                "PowerApp resource type must surface as PowerAppsAuditLogContent so the existing PowerPlatformAuditEventManager.SaveSinglePowerAppEventToSqlStaging path can stage it.");

            var apps = (PowerAppsAuditLogContent)mapped;
            Assert.AreEqual("6c39e97c-6ddd-4a37-afd4-494a605925cf", apps.AppName, "AppName must come from powerplatform.analytics.resource.power_app.id (this is the GUID downstream code keys off).");
            Assert.AreEqual("TestEmptyApp", apps.AppDisplayName);
            Assert.AreEqual("aa289efd-09f4-eaeb-bbb8-c08fc9e27d09", apps.EnvironmentName, "EnvironmentName must hold the environment GUID (legacy schema overloads this field; staging code reads it as EnvironmentId).");
            Assert.AreEqual("dev-na-ba50088f", apps.EnvironmentDisplayName, "EnvironmentDisplayName must hold the human-readable env name from powerplatform.analytics.resource.environment.name so it can land in power_app_environments.name.");
            Assert.AreEqual("1fc61149-5c1d-4f0f-bacb-5772595172b3", apps.AppSessionId);
            Assert.IsTrue(apps.UserAgent != null && apps.UserAgent.StartsWith("Mozilla/5.0"));
            Assert.AreEqual("LaunchPowerApp", apps.Operation);
            Assert.AreEqual("AmberR@CONTOSO.OnMicrosoft.com", apps.UserId);
        }

        [TestMethod]
        public void UnknownResourceType_DoesNotThrow_AndReturnsNull()
        {
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            // Mutate the resource type to an unknown value.
            foreach (var prop in token["PropertyCollection"])
            {
                if ((string)prop["Name"] == "powerplatform.analytics.resource.type")
                {
                    prop["Value"] = "SomethingNew";
                }
            }
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsNull(mapped, "Unknown resource types must be skipped (and logged) rather than partially-mapped into garbage rows.");
        }

        [TestMethod]
        public void PowerApp_WithoutAppId_IsSkipped()
        {
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            // Drop the app-id property so the record is structurally invalid.
            var propertyCollection = (JArray)token["PropertyCollection"];
            for (var i = propertyCollection.Count - 1; i >= 0; i--)
            {
                if ((string)propertyCollection[i]["Name"] == "powerplatform.analytics.resource.power_app.id")
                {
                    propertyCollection.RemoveAt(i);
                }
            }
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsNull(mapped, "A PowerApp record with no app-id has nothing useful to stage; loader must skip it instead of writing a row with AppName=null.");
        }

        [TestMethod]
        public void PowerApp_NonLaunchOperation_IsSkipped()
        {
            // We only persist Power App launches + shares today. Other PowerApp operations
            // (edit / publish / delete) must be ignored rather than half-mapped.
            foreach (var op in new[] { "EditPowerApp", "PublishPowerApp", "DeletePowerApp", "" })
            {
                var token = JObject.Parse(LaunchPowerAppSampleJson);
                token["Operation"] = op;
                var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

                var mapped = record.ToWorkloadSpecificContent(_logger);

                Assert.IsNull(mapped, $"Operation '{op}' is not LaunchPowerApp/SharePowerApp and must not be persisted.");
            }
        }

        [TestMethod]
        public void PowerApp_LaunchOperation_IsCaseInsensitive()
        {
            // Microsoft has shipped audit-log Operation strings with inconsistent casing before;
            // be defensive so a "launchpowerapp" sample doesn't silently disappear.
            var token = JObject.Parse(LaunchPowerAppSampleJson);
            token["Operation"] = "launchpowerapp";
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsInstanceOfType(mapped, typeof(PowerAppsAuditLogContent));
        }

        /// <summary>
        /// Synthesised SharePowerApp sample. Microsoft hasn't published a confirmed sample for
        /// the unified schema yet - this is best-effort and follows the OpenTelemetry naming
        /// convention used by the verified LaunchPowerApp event.
        /// </summary>
        private const string SharePowerAppBestEffortSampleJson = @"{
            ""PropertyCollection"": [
                { ""Name"": ""powerplatform.analytics.resource.power_app.display_name"", ""Value"": ""TestEmptyApp"" },
                { ""Name"": ""powerplatform.analytics.resource.power_app.id"", ""Value"": ""6c39e97c-6ddd-4a37-afd4-494a605925cf"" },
                { ""Name"": ""powerplatform.analytics.resource.environment.id"", ""Value"": ""aa289efd-09f4-eaeb-bbb8-c08fc9e27d09"" },
                { ""Name"": ""powerplatform.analytics.resource.environment.name"", ""Value"": ""dev-na-ba50088f"" },
                { ""Name"": ""powerplatform.analytics.resource.type"", ""Value"": ""PowerApp"" },
                { ""Name"": ""powerplatform.analytics.activity.name"", ""Value"": ""SharePowerApp"" },
                { ""Name"": ""powerplatform.analytics.resource.principal.id"", ""Value"": ""11111111-2222-3333-4444-555555555555"" },
                { ""Name"": ""powerplatform.analytics.resource.principal.name"", ""Value"": ""BobS@CONTOSO.OnMicrosoft.com"" },
                { ""Name"": ""powerplatform.analytics.resource.principal.type"", ""Value"": ""User"" },
                { ""Name"": ""powerplatform.analytics.resource.role.name"", ""Value"": ""CanView"" },
                { ""Name"": ""enduser.principal_name"", ""Value"": ""AmberR@CONTOSO.OnMicrosoft.com"" }
            ],
            ""Id"": ""00000000-1111-2222-3333-444444444444"",
            ""RecordType"": 256,
            ""CreationTime"": ""2026-05-19T13:17:34"",
            ""Operation"": ""SharePowerApp"",
            ""OrganizationId"": ""33333333-3333-3333-3333-333333333333"",
            ""UserId"": ""AmberR@CONTOSO.OnMicrosoft.com"",
            ""Workload"": ""PowerPlatform""
        }";

        [TestMethod]
        public void SharePowerApp_Maps_With_SingleRecipient()
        {
            var token = JObject.Parse(SharePowerAppBestEffortSampleJson);
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsInstanceOfType(mapped, typeof(PowerAppsAuditLogContent),
                "SharePowerApp must surface as PowerAppsAuditLogContent so the existing share-staging path can persist it.");
            var apps = (PowerAppsAuditLogContent)mapped;
            Assert.AreEqual("6c39e97c-6ddd-4a37-afd4-494a605925cf", apps.AppName);
            Assert.AreEqual("aa289efd-09f4-eaeb-bbb8-c08fc9e27d09", apps.EnvironmentName);
            Assert.AreEqual("dev-na-ba50088f", apps.EnvironmentDisplayName);

            Assert.IsNotNull(apps.Permissions, "Share events must produce a Permissions list so PowerPlatformAuditEventManager stages one row per recipient.");
            Assert.AreEqual(1, apps.Permissions.Count, "The unified schema is flat - one event == one recipient.");
            Assert.AreEqual("BobS@CONTOSO.OnMicrosoft.com", apps.Permissions[0].PrincipalName);
            Assert.AreEqual("11111111-2222-3333-4444-555555555555", apps.Permissions[0].PrincipalObjectId);
            Assert.AreEqual("User", apps.Permissions[0].PrincipalType);
            Assert.AreEqual("CanView", apps.Permissions[0].RoleName);
        }

        [TestMethod]
        public void SharePowerApp_WithoutPrincipalName_IsSkipped()
        {
            // If we can't identify the recipient there's no value in a share-table row; mapper
            // must skip + warn (the property names are best-effort and may move).
            var token = JObject.Parse(SharePowerAppBestEffortSampleJson);
            var properties = (JArray)token["PropertyCollection"];
            for (var i = properties.Count - 1; i >= 0; i--)
            {
                if ((string)properties[i]["Name"] == "powerplatform.analytics.resource.principal.name")
                {
                    properties.RemoveAt(i);
                }
            }
            var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

            var mapped = record.ToWorkloadSpecificContent(_logger);

            Assert.IsNull(mapped, "Share events with no recipient name must be skipped so we never write Permissions rows with SharedWithUpn=NULL.");
        }

        [TestMethod]
        public void SharePowerApp_LegacyOperationNames_AreAccepted()
        {
            // We accept the legacy Operation strings case-insensitively in case Microsoft keeps
            // the legacy naming for the unified schema (e.g. "ShareApp", "AddPermissionsToApp").
            foreach (var op in new[] { "ShareApp", "addpermissionstoapp", "EditPowerAppRolePermission" })
            {
                var token = JObject.Parse(SharePowerAppBestEffortSampleJson);
                token["Operation"] = op;
                var record = token.ToObject<PowerPlatformAdminActivityRecordContent>();

                var mapped = record.ToWorkloadSpecificContent(_logger);

                Assert.IsInstanceOfType(mapped, typeof(PowerAppsAuditLogContent),
                    $"Operation '{op}' should be treated as a share event (legacy compatibility).");
                Assert.AreEqual(1, ((PowerAppsAuditLogContent)mapped).Permissions.Count);
            }
        }

        /// <summary>
        /// PowerBIOps.IsSupported is the gate used by ActivityReportLoader to filter the long
        /// tail of Power BI operations down to the ones we actually persist. It must accept
        /// exactly "ViewReport" (case-insensitive) and reject everything else.
        /// </summary>
        [TestMethod]
        public void PowerBIOps_IsSupported_ReturnsTrueOnlyForViewReport()
        {
            Assert.IsTrue(ActivityImportConstants.PowerBIOps.IsSupported("ViewReport"));
            Assert.IsTrue(ActivityImportConstants.PowerBIOps.IsSupported("viewreport"), "Comparison must be case-insensitive.");

            foreach (var op in new[] { "Login", "AddDatasetUser", "PublishReport", "ViewDashboard", "CreateReport", "", null })
            {
                Assert.IsFalse(ActivityImportConstants.PowerBIOps.IsSupported(op),
                    $"Operation '{op ?? "<null>"}' must not be persisted - only ViewReport is supported.");
            }
        }
    }
}
