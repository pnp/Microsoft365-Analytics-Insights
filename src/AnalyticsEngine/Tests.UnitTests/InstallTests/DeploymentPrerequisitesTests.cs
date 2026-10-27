using App.ControlPanel;
using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Prerequisites;
using App.ControlPanel.Frames;
using App.ControlPanel.Frames.InstallWizard;
using Common.Entities;
using Common.Entities.Installer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static Tests.UnitTests.InstallTests.InstallerWorkspaceTests;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class DeploymentPrerequisitesTests
    {
        private static SolutionInstallConfig MinimalConfig() => new SolutionInstallConfig
        {
            CognitiveServicesEnabled = false, ServiceBusEnabled = false, FoundryPromptEnabled = false,
            HealthMonitoring = new HealthMonitoringConfig { Enabled = false }
        };

        [TestMethod]
        public void Catalogue_DecidesPrerequisitesForEveryImportToggle()
        {
            CollectionAssert.AreEquivalent(ImportTaskSettings.GetImportPropertyNames().ToArray(),
                DeploymentPrerequisites.WorkloadProperties.ToArray(),
                "A new import toggle must declare its prerequisite decision in the catalogue.");
            foreach (var property in ImportTaskSettings.GetImportPropertyInfos())
            {
                var config = MinimalConfig();
                property.SetValue(config.SolutionConfig.ImportTaskSettings, true);
                var report = DeploymentPrerequisites.Build(config);
                Assert.AreEqual(1, report.Workloads.Count, property.Name);
                Assert.IsTrue(report.Requirements.Any(r => r.Workloads.Contains(report.Workloads[0])),
                    property.Name + " must explain its own access or setup requirements.");
                Assert.IsTrue(report.WorkloadPermissions.Any(p => p.Workload == report.Workloads[0]
                    && !string.IsNullOrWhiteSpace(p.Why)), property.Name + " needs a visible per-workload API permission decision.");
            }
        }

        [DataTestMethod]
        [DataRow(nameof(ImportTaskSettings.GraphUsersMetadata), "Directory.Read.All|Organization.Read.All|User.Read.All")]
        [DataRow(nameof(ImportTaskSettings.GraphUsageReports), "Reports.Read.All")]
        [DataRow(nameof(ImportTaskSettings.GraphCopilotUsageReports), "Reports.Read.All")]
        [DataRow(nameof(ImportTaskSettings.GraphTeams), "Group.Read.All")]
        [DataRow(nameof(ImportTaskSettings.Calls), "CallRecords.Read.All|User.Read.All")]
        [DataRow(nameof(ImportTaskSettings.SentEmails), "Mail.Read")]
        [DataRow(nameof(ImportTaskSettings.ActivityLog), "ActivityFeed.Read")]
        [DataRow(nameof(ImportTaskSettings.Copilot), "ActivityFeed.Read")]
        [DataRow(nameof(ImportTaskSettings.ImportPowerPlatform), "ActivityFeed.Read")]
        [DataRow(nameof(ImportTaskSettings.ImportDlp), "ActivityFeed.ReadDlp")]
        [DataRow(nameof(ImportTaskSettings.WebTraffic), "")]
        [DataRow(nameof(ImportTaskSettings.Agent365PackageCatalog), "CopilotPackages.Read.All")]
        [DataRow(nameof(ImportTaskSettings.CopilotInteractionHistory), "AiEnterpriseInteraction.Read.All|GroupMember.Read.All|User.Read.All")]
        [DataRow(nameof(ImportTaskSettings.CopilotStudioCredits), "")]
        [DataRow(nameof(ImportTaskSettings.AzureCostManagement), "")]
        [DataRow(nameof(ImportTaskSettings.CopilotCostManagement), "User.Read.All")]
        public void SingleWorkload_DoesNotRequestUnrelatedApplicationPermissions(string property, string permissions)
        {
            var config = MinimalConfig();
            ImportTaskSettings.GetImportPropertyInfos().Single(p => p.Name == property)
                .SetValue(config.SolutionConfig.ImportTaskSettings, true);
            var report = DeploymentPrerequisites.Build(config);
            CollectionAssert.AreEquivalent(permissions.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries),
                report.Requirements.Where(r => r.Category == PrerequisiteCategory.ApplicationPermissions).Select(r => r.Name).ToArray());
            Assert.IsFalse(report.Requirements.Any(r => r.Id == "resource-language"));
            Assert.IsFalse(report.Requirements.Any(r => r.Id == "resource-openai"));
        }

        [TestMethod]
        public void SharedPermissions_AreDeduplicatedAndKeepAllReasons()
        {
            var config = MinimalConfig();
            config.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
            config.SolutionConfig.ImportTaskSettings.GraphCopilotUsageReports = true;
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            config.SolutionConfig.ImportTaskSettings.ImportPowerPlatform = true;
            var report = DeploymentPrerequisites.Build(config);
            Assert.AreEqual(2, report.Requirements.Single(r => r.Name == "Reports.Read.All").Workloads.Count);
            Assert.AreEqual(2, report.Requirements.Single(r => r.Id == "check-report-identities").Workloads.Count);
            Assert.AreEqual(2, report.Requirements.Single(r => r.Name == "ActivityFeed.Read").Workloads.Count);
            Assert.AreEqual(report.Requirements.Count, report.Requirements.Select(r => r.Id).Distinct().Count());
            Assert.IsTrue(report.Requirements.Where(r => r.Category == PrerequisiteCategory.OptionalEnrichment)
                .Any(r => r.Name == "OnlineMeetings.Read.All"));
            StringAssert.Contains(report.ToMarkdown(), "Optional Copilot file, meeting and history features");
        }

        [TestMethod]
        public void DirectoryGrant_CoversGroupReadsWithoutRequestingRedundantGrants()
        {
            var config = MinimalConfig();
            config.SolutionConfig.ImportTaskSettings.GraphUsersMetadata = true;
            config.SolutionConfig.ImportTaskSettings.GraphTeams = true;
            config.SolutionConfig.ImportTaskSettings.CopilotInteractionHistory = true;
            var report = DeploymentPrerequisites.Build(config);
            Assert.IsFalse(report.Requirements.Any(r => r.Name == "Group.Read.All" || r.Name == "GroupMember.Read.All"));
            var directory = report.Requirements.Single(r => r.Name == "Directory.Read.All");
            Assert.AreEqual(3, directory.Workloads.Count);
            StringAssert.Contains(directory.Purpose, "GroupMember.Read.All");
            config.SolutionConfig.ImportTaskSettings.GraphUsersMetadata = false;
            report = DeploymentPrerequisites.Build(config);
            Assert.IsFalse(report.Requirements.Any(r => r.Name == "GroupMember.Read.All"));
            Assert.AreEqual(2, report.Requirements.Single(r => r.Name == "Group.Read.All").Workloads.Count);
        }

        [TestMethod]
        public void PermissionRows_KeepReasonsSpecificToEachImportAndExplainCoveredGrants()
        {
            var config = MinimalConfig();
            config.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
            config.SolutionConfig.ImportTaskSettings.GraphCopilotUsageReports = true;
            config.SolutionConfig.ImportTaskSettings.ActivityLog = true;
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            config.SolutionConfig.ImportTaskSettings.GraphUsersMetadata = true;
            config.SolutionConfig.ImportTaskSettings.GraphTeams = true;
            var report = DeploymentPrerequisites.Build(config);
            var ordinaryReports = report.WorkloadPermissions.Single(p => p.Permission == "Reports.Read.All"
                && p.Workload == "Microsoft 365 usage reports");
            var copilotReports = report.WorkloadPermissions.Single(p => p.Permission == "Reports.Read.All"
                && p.Workload == "Microsoft 365 Copilot usage reports");
            StringAssert.Contains(ordinaryReports.Why, "email");
            Assert.IsFalse(ordinaryReports.Why.Contains("Copilot user-count"));
            StringAssert.Contains(copilotReports.Why, "Copilot user-count");
            StringAssert.Contains(report.WorkloadPermissions.Single(p => p.Permission == "ActivityFeed.Read"
                && p.Workload == "SharePoint / OneDrive audit").Why, "Audit.SharePoint");
            StringAssert.Contains(report.WorkloadPermissions.Single(p => p.Permission == "ActivityFeed.Read"
                && p.Workload == "Microsoft 365 Copilot audit").Why, "CopilotInteraction");
            StringAssert.Contains(report.WorkloadPermissions.Single(p => p.Permission == "Directory.Read.All"
                && p.Workload == "Teams deep analytics").Why, "Group.Read.All");
            var markdown = report.ToMarkdown();
            Assert.IsTrue(markdown.IndexOf("## Runtime API permissions by selected workload", StringComparison.Ordinal)
                < markdown.IndexOf("## Runtime application permissions", StringComparison.Ordinal),
                "The per-import list must be prominent, not buried in the consolidated categories.");
        }

        [TestMethod]
        public void PermissionRows_ExplicitlyShowNoGraphGrantAndSeparateDelegatedAccess()
        {
            var config = MinimalConfig();
            config.SolutionConfig.ImportTaskSettings.WebTraffic = true;
            config.SolutionConfig.ImportTaskSettings.AzureCostManagement = true;
            config.SolutionConfig.ImportTaskSettings.CopilotStudioCredits = true;
            var report = DeploymentPrerequisites.Build(config);
            Assert.AreEqual(3, report.WorkloadPermissions.Count(p => p.NoApplicationGrant));
            Assert.IsTrue(report.WorkloadPermissions.Any(p => !p.NoApplicationGrant
                && p.Requirement.Category == PrerequisiteCategory.DelegatedConnections));
            StringAssert.Contains(report.WorkloadPermissions.Single(p => p.Workload == "SharePoint page traffic").Why, "no runtime Graph");
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            report = DeploymentPrerequisites.Build(config);
            Assert.IsTrue(report.WorkloadPermissions.Where(p => p.Workload == "Microsoft 365 Copilot audit")
                .Any(p => p.Need == "Optional extra" && p.Permission == "OnlineMeetings.Read.All"));
            Assert.IsFalse(report.Requirements.Any(r => r.Category == PrerequisiteCategory.ApplicationPermissions
                && r.Name == "OnlineMeetings.Read.All"));
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void SelectedOptionalResources_ExplainRemovalAndCallsDependencyWithoutChangingSettings(bool calls)
        {
            var config = MinimalConfig();
            config.CognitiveServicesEnabled = true;
            config.FoundryPromptEnabled = true;
            config.ServiceBusEnabled = true;
            config.SolutionConfig.ImportTaskSettings.Calls = calls;
            var before = config.SolutionConfig.ImportTaskSettings.ToSettingsString();
            var report = DeploymentPrerequisites.Build(config);
            Assert.AreEqual(3, report.OptionalComponents.Count);
            var bus = report.OptionalComponents.Single(c => c.Id == "service-bus");
            Assert.AreEqual(!calls, bus.CanRemoveWithoutChangingImports);
            StringAssert.Contains(bus.FunctionalityLost, "call-record");
            StringAssert.Contains(report.OptionalComponents.Single(c => c.Id == "language").FunctionalityLost, "sentiment");
            StringAssert.Contains(report.OptionalComponents.Single(c => c.Id == "openai").FunctionalityLost, "Prompt-category");
            var markdown = report.ToMarkdown();
            StringAssert.Contains(markdown, "Selected Azure components you could leave out");
            StringAssert.Contains(markdown, "does not delete existing Azure resources");
            StringAssert.Contains(markdown, calls ? "First deselect Teams calls" : "Teams calls import is not selected");
            Assert.AreEqual(before, config.SolutionConfig.ImportTaskSettings.ToSettingsString());
            Assert.IsTrue(config.CognitiveServicesEnabled && config.FoundryPromptEnabled && config.ServiceBusEnabled);
            config.CognitiveServicesEnabled = config.ServiceBusEnabled = config.FoundryPromptEnabled = false;
            Assert.AreEqual(0, DeploymentPrerequisites.Build(config).OptionalComponents.Count,
                "Only actually selected add-on services belong in the removal review.");
        }

        [TestMethod]
        public void Infrastructure_TracksSelectedOptionsAndSeparatesManagedIdentities()
        {
            var config = MinimalConfig();
            var initial = DeploymentPrerequisites.Build(config);
            Assert.AreEqual(0, initial.Workloads.Count);
            Assert.IsFalse(initial.Requirements.Any(r => r.Category == PrerequisiteCategory.ApplicationPermissions));
            Assert.IsFalse(initial.Requirements.Any(r => r.Id == "resource-service-bus"));
            config.ServiceBusEnabled = true;
            config.CognitiveServicesEnabled = true;
            config.FoundryPromptEnabled = true;
            config.HealthMonitoring.Enabled = true;
            config.NetworkConfig.Enabled = true;
            config.NetworkConfig.AllowPublicAccess = false;
            config.NetworkConfig.DeployDnsZones = false;
            config.SqlAuthMode = SqlServerAuthMode.EntraId;
            config.SQLEntraDatabaseUsers.Add(new SqlDatabaseUser { Login = "reader@contoso.example" });
            config.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
            var report = DeploymentPrerequisites.Build(config);
            foreach (var id in new[] { "resource-service-bus", "resource-language", "resource-openai",
                "resource-health", "resource-private-network", "resource-private-dns", "resource-automation", "resource-hybrid-worker" })
                Assert.IsTrue(report.Requirements.Any(r => r.Id == id), id);
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "resource-private-dns").AdminAction, "disabled");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "sql-runtime").Identity, "App Service");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "automation-sql").Identity, "Automation");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "sql-additional-users").Purpose, "db_owner, not read-only");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "resource-sql").AdminAction, "S2");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "access-service-bus").AdminAction, "Data Sender + Data Receiver");

            config.ServiceBusEnabled = false;
            config.SolutionConfig.ImportTaskSettings.Calls = true;
            StringAssert.Contains(DeploymentPrerequisites.Build(config).Requirements
                .Single(r => r.Id == "resource-service-bus").AdminAction, "disabled");
            StringAssert.Contains(DeploymentPrerequisites.Build(config).Requirements
                .Single(r => r.Id == "resource-service-bus").Responsibility, "Not currently scheduled");
            config.NetworkConfig.AllowPublicAccess = true;
            Assert.IsFalse(DeploymentPrerequisites.Build(config).Requirements.Any(r => r.Id == "resource-hybrid-worker"),
                "Publicly reachable data services do not require a new Hybrid Worker VM when none is selected.");
            Assert.IsTrue(DeploymentPrerequisites.Build(config).Requirements.Any(r => r.Id == "resource-automation"),
                "The installer still provisions Automation for usage reports without a Hybrid Worker.");
        }

        [TestMethod]
        public void DelegatedBillingConnections_AreNotMistakenForRuntimeApplicationGrants()
        {
            var config = MinimalConfig();
            config.SolutionConfig.ImportTaskSettings.CopilotStudioCredits = true;
            config.SolutionConfig.ImportTaskSettings.CopilotCostManagement = true;
            var report = DeploymentPrerequisites.Build(config);
            var delegated = report.Requirements.Where(r => r.Category == PrerequisiteCategory.DelegatedConnections).ToArray();
            Assert.AreEqual(5, delegated.Length);
            StringAssert.Contains(delegated.Single(r => r.Id == "studio-connection").AdminAction, "/signin-agent-costs");
            StringAssert.Contains(delegated.Single(r => r.Name == "CopilotCostManagement.Read.All").AdminAction, "Global Reader");
            Assert.IsFalse(report.Requirements.Any(r => r.Name == "Licensing.Read"));
            StringAssert.Contains(report.ToMarkdown(), "beta and not supported for production");
        }

        [TestMethod]
        public void PlatformChecks_ListSelectedDestinationsAndProvidersWithoutUnrelatedServices()
        {
            var config = MinimalConfig();
            config.DownloadLatestStable = false;
            config.SolutionConfig.ImportTaskSettings.WebTraffic = true;
            var report = DeploymentPrerequisites.Build(config);
            Assert.IsFalse(report.Requirements.Any(r => r.Id == "check-network" || r.Id == "check-release-download"
                || r.Id == "check-calls-network" || r.Id == "check-ai-network" || r.Id == "check-private-dns-routing"));
            var providers = report.Requirements.Single(r => r.Id == "check-provider-registration").AdminAction;
            StringAssert.Contains(providers, "Microsoft.Web");
            Assert.IsFalse(providers.Contains("Microsoft.ServiceBus") || providers.Contains("Microsoft.Network")
                || providers.Contains("Microsoft.CognitiveServices") || providers.Contains("Microsoft.Automation"));
            var apis = report.Requirements.Single(r => r.Id == "check-runtime-api-network").AdminAction;
            Assert.IsFalse(apis.Contains("manage.office.com") || apis.Contains("api.powerplatform.com"));
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "check-sql-network").Scope, "TCP 1433");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "check-storage-network").Scope, ".table.core.windows.net");

            config.DownloadLatestStable = true;
            config.NetworkConfig.Enabled = true;
            config.NetworkConfig.AllowPublicAccess = false;
            config.CognitiveServicesEnabled = true;
            config.FoundryPromptEnabled = true;
            config.SolutionConfig.ImportTaskSettings.Calls = true;
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            config.SolutionConfig.ImportTaskSettings.CopilotStudioCredits = true;
            config.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
            report = DeploymentPrerequisites.Build(config);
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "check-calls-network").Scope, "TCP 5671");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "check-calls-network").AdminAction, "only TCP 443 is not sufficient");
            providers = report.Requirements.Single(r => r.Id == "check-provider-registration").AdminAction;
            foreach (var provider in new[] { "Microsoft.ServiceBus", "Microsoft.Network", "Microsoft.CognitiveServices", "Microsoft.Automation" })
                StringAssert.Contains(providers, provider);
            apis = report.Requirements.Single(r => r.Id == "check-runtime-api-network").AdminAction;
            foreach (var host in new[] { "graph.microsoft.com", "manage.office.com", "api.powerplatform.com", "reports.office.com" })
                StringAssert.Contains(apis, host);
            var dns = report.Requirements.Single(r => r.Id == "check-private-dns-routing").AdminAction;
            StringAssert.Contains(dns, "privatelink.table.core.windows.net");
            StringAssert.Contains(dns, "privatelink.openai.azure.com");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "check-policy-compatibility").AdminAction, "SCM basic publishing");
            Assert.IsTrue(report.Requirements.Any(r => r.Id == "check-release-download"));
        }

        [TestMethod]
        public void Handoff_ExplainsAutomaticSetupAndManualExceptionsForANewReader()
        {
            var config = MinimalConfig();
            config.SqlAuthMode = SqlServerAuthMode.EntraId;
            config.SolutionConfig.ImportTaskSettings.AzureCostManagement = true;
            config.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
            config.NetworkConfig.Enabled = true;
            config.NetworkConfig.AllowPublicAccess = false;
            config.NetworkConfig.DeployDnsZones = false;
            var report = DeploymentPrerequisites.Build(config);
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "resource-sql").Responsibility, "Installer creates");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "sql-runtime").Responsibility, "Installer configures");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "azure-cost-reader").Responsibility, "does not grant");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "resource-hybrid-worker").Responsibility, "does not create the VM");
            StringAssert.Contains(report.Requirements.Single(r => r.Id == "resource-private-dns").Responsibility, "Network owner supplies DNS");
            var markdown = report.ToMarkdown();
            foreach (var heading in new[] { "What this document is for", "How setup is divided",
                "Identities and permission types", "Selected deployment options", "**Who sets this up:**", "**Setup notes and checks:**" })
                StringAssert.Contains(markdown, heading);
            Assert.IsFalse(markdown.Contains("**Administrator action:**"));
            Assert.IsFalse(markdown.Contains("Connectivity, providers, policy and capacity"));
            Assert.IsTrue(report.Requirements.All(r => !string.IsNullOrWhiteSpace(r.Responsibility)));
        }

        [TestMethod]
        public void Markdown_ExcludesEnteredValuesAndDoesNotMutateConfiguration()
        {
            const string sentinel = "contoso-sensitive-synthetic-sentinel";
            var config = MinimalConfig();
            config.ResourceGroupName = config.StorageAccountName = config.AppServiceWebAppName =
                config.SQLServerDatabaseName = config.CognitiveServiceName = sentinel;
            config.Subscription.SubId = config.InstallerAccount.ClientId = config.InstallerAccount.Secret =
                config.RuntimeAccountOffice365.ClientId = config.RuntimeAccountOffice365.Secret = sentinel;
            config.SharePointConfig.TargetSites.Add("https://" + sentinel + ".sharepoint.com/sites/test");
            config.SqlAuthMode = SqlServerAuthMode.EntraId;
            config.SQLEntraDatabaseUsers.Add(new SqlDatabaseUser { Login = sentinel, ObjectId = sentinel });
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            var before = config.SolutionConfig.ImportTaskSettings.ToSettingsString();
            var report = DeploymentPrerequisites.Build(config);
            var markdown = report.ToMarkdown();
            Assert.IsFalse(markdown.Contains(sentinel));
            Assert.IsFalse(string.Join("\n", report.Requirements.Select(r => r.Details)).Contains(sentinel));
            Assert.AreEqual(before, config.SolutionConfig.ImportTaskSettings.ToSettingsString());
            Assert.AreEqual(sentinel, config.RuntimeAccountOffice365.Secret);
            StringAssert.Contains(markdown, "Administrator handoff checklist");
            StringAssert.Contains(markdown, "Office 365 Management APIs");
            foreach (var requirement in report.Requirements)
            {
                Assert.IsTrue(Uri.TryCreate(requirement.Reference, UriKind.Absolute, out var uri));
                Assert.AreEqual("https", uri.Scheme);
                Assert.IsTrue(!string.IsNullOrEmpty(requirement.AdminAction) && !string.IsNullOrEmpty(requirement.Scope));
            }
        }

        [DataTestMethod]
        [DataRow(700, 640, 1.0f)]
        [DataRow(600, 500, 1.0f)]
        [DataRow(900, 750, 1.5f)]
        [DataRow(1200, 1000, 2.0f)]
        public void PrerequisitesPage_KeepsActionsAndDetailsReachableAndExportsFreshSelections(int width, int height, float scale)
        {
            RunSta(() =>
            {
                var config = MinimalConfig();
                foreach (var property in ImportTaskSettings.GetImportPropertyInfos())
                    property.SetValue(config.SolutionConfig.ImportTaskSettings, true);
                using (var page = new DeploymentPrerequisitesControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false })
                {
                    page.ConfigurationProvider = () => config;
                    page.RefreshConfiguration(config);
                    host.Controls.Add(page);
                    host.Show();
                    if (scale != 1.0f) page.Font = new Font(page.Font.FontFamily, page.Font.Size * scale);
                    Application.DoEvents();
                    var grid = Find<DataGridView>(page, "prerequisiteRequirements");
                    Assert.AreEqual(page.Report.WorkloadPermissions.Count, grid.Rows.Count);
                    var category = Find<ComboBox>(page, "prerequisiteCategory");
                    Assert.AreEqual("Runtime API by workload", category.SelectedItem.ToString(),
                        "Runtime permissions must be the initial view when imports are selected.");
                    Assert.IsTrue(grid.Columns["Why"].Visible);
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row => row.Tag is WorkloadPermissionRow));
                    category.SelectedItem = category.Items.Cast<object>().Single(item => item.ToString() == "Application permissions");
                    Assert.IsTrue(category.Width >= category.Items.Cast<object>()
                        .Max(item => TextRenderer.MeasureText(item.ToString(), category.Font).Width) + 20,
                        "Category names must remain readable at enlarged fonts.");
                    Assert.IsTrue(grid.Rows.Count > 0);
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row =>
                        ((DeploymentRequirement)row.Tag).Category == PrerequisiteCategory.ApplicationPermissions));
                    grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[0];
                    StringAssert.Contains(Find<TextBox>(page, "prerequisiteDetails").Text,
                        ((DeploymentRequirement)grid.CurrentRow.Tag).Name);
                    foreach (var name in new[] { "exportPrerequisites", "prerequisiteDetails", "prerequisiteReference" })
                    {
                        var control = Find<Control>(page, name);
                        page.ScrollControlIntoView(control);
                        Application.DoEvents();
                        Assert.IsTrue(page.ClientRectangle.IntersectsWith(page.RectangleToClient(
                            control.Parent.RectangleToScreen(control.Bounds))), name + " must be reachable.");
                        Assert.IsTrue(control.Width > 0 && control.Height > 0, name + " must have usable bounds.");
                    }
                    Assert.IsFalse(page.HorizontalScroll.Visible, "The page must wrap rather than require horizontal scrolling.");
                    Assert.IsTrue(grid.Height >= 100, "Keep a useful requirements viewport.");
                    Assert.IsTrue(Find<TextBox>(page, "prerequisiteDetails").Height >= 80, "Keep a useful detail viewport.");
                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    if (!string.IsNullOrEmpty(directory))
                    {
                        if (width == 700) page.ExportMarkdown(Path.Combine(directory, "prerequisites-example.md"));
                        page.AutoScrollPosition = Point.Empty;
                        using (var image = new Bitmap(host.Width, host.Height))
                        {
                            host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                            image.Save(Path.Combine(directory, $"prerequisites-{width}-{height}-{scale}.png"));
                        }
                        page.ScrollControlIntoView(Find<Control>(page, "prerequisiteReference"));
                        using (var image = new Bitmap(host.Width, host.Height))
                        {
                            host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                            image.Save(Path.Combine(directory, $"prerequisites-details-{width}-{height}-{scale}.png"));
                        }
                    }
                    var path = Path.Combine(Path.GetTempPath(), "prerequisites-" + Guid.NewGuid().ToString("N") + ".md");
                    try
                    {
                        config.SolutionConfig.ImportTaskSettings = new ImportTaskSettings { WebTraffic = true };
                        page.ExportMarkdown(path);
                        var actual = File.ReadAllText(path);
                        Assert.AreEqual(DeploymentPrerequisites.Build(config).ToMarkdown(), actual);
                        Assert.IsFalse(actual.Contains("Mail.Read"), "Export must refresh the configuration, not reuse stale preview rows.");
                        StringAssert.Contains(actual, "SharePoint page traffic");
                        StringAssert.Contains(actual, "Administrator handoff checklist");
                    }
                    finally { if (File.Exists(path)) File.Delete(path); }
                }
            });
        }

        [DataTestMethod]
        [DataRow(700, 640, 1.0f)]
        [DataRow(600, 500, 1.0f)]
        [DataRow(900, 750, 1.5f)]
        [DataRow(1200, 1000, 2.0f)]
        public void ResourceAndPermissionViews_ShowSelectedTradeoffsAndReasonsWithoutClipping(int width, int height, float scale)
        {
            RunSta(() =>
            {
                var config = MinimalConfig();
                config.ServiceBusEnabled = config.CognitiveServicesEnabled = config.FoundryPromptEnabled = true;
                config.SolutionConfig.ImportTaskSettings.SentEmails = true;
                config.SolutionConfig.ImportTaskSettings.Calls = true;
                using (var page = new DeploymentPrerequisitesControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false })
                {
                    page.RefreshConfiguration(config);
                    host.Controls.Add(page);
                    host.Show();
                    if (scale != 1.0f) page.Font = new Font(page.Font.FontFamily, page.Font.Size * scale);
                    Application.DoEvents();
                    var views = Find<ComboBox>(page, "prerequisiteCategory");
                    var grid = Find<DataGridView>(page, "prerequisiteRequirements");
                    Assert.AreEqual("Runtime API by workload", views.SelectedItem.ToString());
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().Any(row =>
                        (row.Tag as WorkloadPermissionRow)?.Permission == "Mail.Read"));
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row => !string.IsNullOrWhiteSpace((string)row.Cells["Why"].Value)));
                    StringAssert.Contains(Find<Label>(page, "prerequisiteWorkloads").Text, "Azure AI Language");
                    StringAssert.Contains(Find<Label>(page, "prerequisiteWorkloads").Text, "required for Calls");
                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    CaptureView("workload-permissions");
                    views.SelectedItem = views.Items.Cast<object>().Single(item => item.ToString() == "Selected optional resources");
                    Assert.AreEqual(3, grid.Rows.Count);
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row => row.Tag is OptionalAzureComponent));
                    var bus = grid.Rows.Cast<DataGridViewRow>().Single(row => ((OptionalAzureComponent)row.Tag).Id == "service-bus");
                    grid.CurrentCell = bus.Cells[0];
                    StringAssert.Contains(Find<TextBox>(page, "prerequisiteDetails").Text, "First deselect Teams calls");
                    StringAssert.Contains(Find<Label>(page, "prerequisiteCategoryHelp").Text, "does not delete");
                    Assert.IsFalse(page.HorizontalScroll.Visible);
                    Assert.IsTrue(grid.Height >= 100);
                    Assert.IsTrue(Find<Label>(page, "prerequisiteCategoryHelp").Height >=
                        Find<Label>(page, "prerequisiteCategoryHelp").GetPreferredSize(new Size(grid.Width, 0)).Height);
                    CaptureView("selected-resources");
                    config.ServiceBusEnabled = config.CognitiveServicesEnabled = config.FoundryPromptEnabled = false;
                    page.RefreshConfiguration(config);
                    Assert.AreEqual("Runtime API by workload", views.SelectedItem.ToString());
                    Assert.IsFalse(views.Items.Cast<object>().Any(item => item.ToString() == "Selected optional resources"));
                    StringAssert.Contains(Find<Label>(page, "prerequisiteWorkloads").Text, "Service Bus provisioning is currently off");

                    void CaptureView(string name)
                    {
                        if (string.IsNullOrEmpty(directory)) return;
                        page.AutoScrollPosition = Point.Empty;
                        Application.DoEvents();
                        using (var image = new Bitmap(host.Width, host.Height))
                        {
                            host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                            image.Save(Path.Combine(directory, $"prerequisites-{name}-{width}-{scale}.png"));
                        }
                    }
                }
            });
        }

        [DataTestMethod]
        [DataRow(700, 640, 1.0f)]
        [DataRow(900, 750, 1.5f)]
        [DataRow(1200, 1000, 2.0f)]
        public void OptionalCopilotCategory_ExplainsItsPurposeAndDisappearsWhenNotApplicable(int width, int height, float scale)
        {
            RunSta(() =>
            {
                var config = MinimalConfig();
                config.SolutionConfig.ImportTaskSettings.Copilot = true;
                using (var page = new DeploymentPrerequisitesControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(width, height), ShowInTaskbar = false })
                {
                    page.RefreshConfiguration(config);
                    host.Controls.Add(page);
                    host.Show();
                    if (scale != 1.0f) page.Font = new Font(page.Font.FontFamily, page.Font.Size * scale);
                    Application.DoEvents();
                    var categories = Find<ComboBox>(page, "prerequisiteCategory");
                    var grid = Find<DataGridView>(page, "prerequisiteRequirements");
                    var optional = categories.Items.Cast<object>().Single(item => item.ToString() == "Copilot extras (optional)");
                    categories.SelectedItem = optional;
                    Assert.AreEqual(3, grid.Rows.Count);
                    var help = Find<Label>(page, "prerequisiteCategoryHelp");
                    StringAssert.Contains(help.Text, "readable file/site and meeting context");
                    StringAssert.Contains(help.Text, "not required for raw audit ingestion");
                    Assert.IsTrue(help.Height >= help.GetPreferredSize(new Size(help.Width, 0)).Height);
                    Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row =>
                        ((DeploymentRequirement)row.Tag).Category == PrerequisiteCategory.OptionalEnrichment));
                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    if (!string.IsNullOrEmpty(directory))
                    {
                        using (var image = new Bitmap(host.Width, host.Height))
                        {
                            host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                            image.Save(Path.Combine(directory, $"prerequisites-copilot-extras-{scale}.png"));
                        }
                    }
                    config.SolutionConfig.ImportTaskSettings.Copilot = false;
                    page.RefreshConfiguration(config);
                    Assert.IsFalse(categories.Items.Cast<object>().Any(item => item.ToString() == "Copilot extras (optional)"));
                    Assert.AreEqual("All requirements", categories.SelectedItem.ToString());
                    Assert.IsFalse(page.Report.ToMarkdown().Contains("## Optional Copilot"));
                    foreach (var item in categories.Items.Cast<object>().ToArray())
                    {
                        categories.SelectedItem = item;
                        Assert.IsTrue(grid.Rows.Count > 0, "Empty categories must not be offered.");
                        Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(row =>
                            row.Tag is DeploymentRequirement && !string.IsNullOrEmpty(((DeploymentRequirement)row.Tag).Responsibility)));
                    }
                }
            });
        }

        [TestMethod]
        public void WizardPrerequisites_AppearUnderDeploymentAndRefreshFromUnsavedWorkloads()
        {
            RunSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(1100, 760), ShowInTaskbar = false })
                {
                    host.Controls.Add(wizard);
                    host.Show();
                    var config = MinimalConfig();
                    config.SolutionConfig.ImportTaskSettings.SentEmails = true;
                    wizard.ConfigureUI(config);
                    var navigation = Find<TreeView>(wizard, "navigator");
                    var node = navigation.Nodes.Find("tabPrerequisites", true).Single();
                    Assert.AreEqual("Deployment", node.Parent.Text);
                    navigation.SelectedNode = node;
                    var page = Find<DeploymentPrerequisitesControl>(wizard, "deploymentPrerequisites");
                    Assert.IsTrue(page.Visible);
                    Assert.IsTrue(page.Report.Requirements.Any(r => r.Name == "Mail.Read"));
                    navigation.SelectedNode = navigation.Nodes.Find("tabTargets", true).Single();
                    Find<App.ControlPanel.Controls.TargetSolutionConfigControl>(wizard, "importJobSettingsSelection").Config =
                        new TargetSolutionConfig { ImportTaskSettings = new ImportTaskSettings { WebTraffic = true } };
                    navigation.SelectedNode = navigation.Nodes.Find("tabPrerequisites", true).Single();
                    Assert.IsFalse(page.Report.Requirements.Any(r => r.Name == "Mail.Read"));
                    Assert.IsTrue(page.Report.Workloads.Contains("SharePoint page traffic"));
                    Assert.IsNull(typeof(InstallSPOSitesControl).GetField("_installerEngine",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(wizard),
                        "Planning must not create an installer engine or start deployment.");
                }
            });
        }
    }
}
