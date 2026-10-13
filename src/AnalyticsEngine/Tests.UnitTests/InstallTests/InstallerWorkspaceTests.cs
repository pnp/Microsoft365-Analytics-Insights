using App.ControlPanel;
using App.ControlPanel.Engine;
using App.ControlPanel.Frames;
using App.ControlPanel.Frames.InstallWizard;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.Entities;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class InstallerWorkspaceTests
    {
        [TestMethod]
        public void CaptureWorkspace_SyntheticNativeWindow()
        {
            var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
            if (string.IsNullOrEmpty(directory)) Assert.Inconclusive("Opt-in native screenshots: set INSTALLER_PREVIEW_DIR.");
            RunSta(() =>
            {
                using (var form = new MainForm())
                {
                    var load = typeof(MainForm).GetMethod("StartForm_Load", BindingFlags.NonPublic | BindingFlags.Instance);
                    form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
                    form.LoadedConfig = SyntheticConfig();
                    form.LoadedConfig.ResourceGroupName = "contoso-analytics";
                    form.LoadedConfig.AppServiceWebAppName = "contoso-analytics-web";
                    form.LoadedConfig.AppServicePlanName = "contoso-analytics-plan";
                    form.LoadedConfig.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
                    form.LoadedConfig.SolutionConfig.ImportTaskSettings.Copilot = true;
                    form.LoadedConfig.SolutionConfig.ImportTaskSettings.WebTraffic = true;
                    form.Text = "Analytics Installer - synthetic design preview";
                    ((ToolStripStatusLabel)typeof(MainForm).GetField("txtConfigFile", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(form)).Text = "Synthetic configuration preview - no deployment";
                    var state = typeof(MainForm).GetNestedType("MainVisualState", BindingFlags.NonPublic);
                    typeof(MainForm).GetMethod("SetMenu", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(form, new[] { Enum.Parse(state, "MainMenu") });
                    form.Show();
                    Find<CheckBox>(Find<GroupBox>(form, "grpStart"), "chkDisclaimer").Checked = true;
                    Find<Button>(form, "btnStartInstall").PerformClick();
                    typeof(MainForm).GetMethod("SetFormLoadingState", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(form, new object[] { AppWaitState.Ready });
                    Application.DoEvents();
                    var navigator = Find<TreeView>(form, "navigator");
                    var prefix = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_STAGE") ?? "workspace";
                    foreach (var name in new[] { "tabTargets", "tabOverview", "tabSources", "tabCredentials",
                        "tabPageAzureConfig", "tabNetworking", "tabSharePoint", "tabAzureResources",
                        "tabAzureStorage", "tabAzureAI", "tabInstall" })
                    {
                        var node = navigator.Nodes.Find(name, true).SingleOrDefault();
                        if (node == null) continue;
                        navigator.SelectedNode = node;
                        Application.DoEvents();
                        using (var image = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                            image.Save(Path.Combine(directory, prefix + "-" + name + ".png"));
                        }
                        if (name == "tabSources")
                        {
                            Find<RadioButton>(form, "rdpSpecificLocation").Checked = true;
                            Application.DoEvents();
                            using (var image = new Bitmap(form.Width, form.Height))
                            {
                                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                                image.Save(Path.Combine(directory, prefix + "-local-packages.png"));
                            }
                            Find<RadioButton>(form, "rdbLatest").Checked = true;
                        }
                        if (name != "tabInstall")
                        {
                            var previousSize = form.Size;
                            form.Size = form.MinimumSize;
                            Capture(form, directory, prefix + "-" + name + "-minimum");
                            if (name == "tabAzureStorage")
                            {
                                var storage = Find<AzureStorageConfigControl>(form, "azureStorageConfigControl1");
                                storage.AutoScrollPosition = new Point(0, storage.DisplayRectangle.Height);
                                Capture(form, directory, prefix + "-" + name + "-minimum-details");
                                storage.AutoScrollPosition = Point.Empty;
                            }
                            form.Size = previousSize;
                        }
                    }
                    Find<InstallSPOSitesControl>(form, "installSPOSitesControl").ConfigureUI(SyntheticConfig());
                    navigator.SelectedNode = navigator.Nodes.Find("tabOverview", true).Single();
                    Capture(form, directory, prefix + "-overview-valid");
                    navigator.SelectedNode = navigator.Nodes.Find("tabInstall", true).Single();
                    var page = Find<InstallSolutionControl>(form, "installSolutionControl1");
                    var wizard = Find<InstallSPOSitesControl>(form, "installSPOSitesControl");
                    page.SetRunningState(true);
                    page.BeginOperation("Synthetic install preview", true);
                    wizard.LogItemOnUIThread(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs
                    {
                        Text = "=== Phase: Azure backend resources ===",
                        PhaseName = "Azure backend resources",
                        Level = LogLevel.Information
                    }));
                    wizard.LogItemOnUIThread(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs
                    {
                        Text = "Synthetic example: configuration inspected; no Azure calls made.",
                        Level = LogLevel.Information
                    }));
                    wizard.LogItemOnUIThread(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs
                    {
                        Text = "=== Phase: RBAC role assignments ===",
                        PhaseName = "RBAC role assignments",
                        Level = LogLevel.Information
                    }));
                    wizard.LogItemOnUIThread(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs
                    {
                        Text = "Synthetic warning: a permission needs operator review.",
                        Level = LogLevel.Warning
                    }));
                    Capture(form, directory, prefix + "-running");
                    Find<Button>(page, "btnCancel").PerformClick();
                    Capture(form, directory, prefix + "-cancellation");
                    wizard.LogItemOnUIThread(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs
                    {
                        Text = "Synthetic error: deployment did not complete. No resources have been changed.",
                        Level = LogLevel.Error,
                        IsError = true
                    }));
                    page.CompleteOperation();
                    page.SetRunningState(false);
                    Find<ComboBox>(page, "logSeverity").SelectedIndex = 1;
                    Capture(form, directory, prefix + "-issues");
                    var reviewConfig = SyntheticConfig();
                    reviewConfig.SolutionConfig.ImportTaskSettings.GraphUsageReports = true;
                    using (var review = new DeploymentReviewForm(reviewConfig, new[] { "Copilot interactions", "Usage reports" }))
                    {
                        review.Show(form);
                        Capture(review, directory, prefix + "-confirmation");
                        Find<CheckBox>(review, "acknowledgeDeployment").Checked = true;
                        Assert.IsTrue(Find<Button>(review, "confirmDeployment").Enabled);
                        Find<Button>(review, "cancelReview").PerformClick();
                        Assert.AreEqual(DialogResult.Cancel, review.DialogResult);
                    }
                    form.Size = form.MinimumSize;
                    Capture(form, directory, prefix + "-minimum-window");
                }
            });
        }

        [TestMethod]
        public void Overview_GreenCheckOnlyMarksValidLocalConfiguration()
        {
            RunSta(() =>
            {
                using (var overview = new ConfigurationOverviewControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(740, 540) })
                {
                    host.Controls.Add(overview);
                    host.Show();
                    overview.RefreshConfiguration(SyntheticConfig(), 1);
                    var icon = Find<Label>(overview, "configurationValidationIcon");
                    Assert.IsTrue(icon.Visible);
                    Assert.AreEqual(Color.Green, icon.ForeColor);
                    Assert.IsFalse(Find<ListBox>(overview, "configurationIssues").Visible);
                    var factsHeight = Find<ListView>(overview, "configurationFacts").Height;
                    StringAssert.Contains(Find<Label>(overview, "configurationValidation").Text, "have not been verified");
                    var invalid = SyntheticConfig();
                    invalid.ResourceGroupName = "";
                    overview.RefreshConfiguration(invalid, 1);
                    Assert.IsFalse(icon.Visible);
                    Assert.IsTrue(Find<ListBox>(overview, "configurationIssues").Visible);
                    Assert.IsTrue(Find<ListView>(overview, "configurationFacts").Height < factsHeight);
                    overview.RefreshConfiguration(SyntheticConfig(), 1);
                    Assert.IsFalse(Find<ListBox>(overview, "configurationIssues").Visible);
                    Assert.AreEqual(factsHeight, Find<ListView>(overview, "configurationFacts").Height);
                    overview.RefreshConfiguration(invalid, 1);
                    StringAssert.Contains(Find<Label>(overview, "configurationValidation").Text, "review before deployment");
                }
            });
        }

        [DataTestMethod]
        [DataRow(730, 520)]
        [DataRow(940, 650)]
        public void ServicePages_UseAvailableWidthAndKeepExplanationsAndFieldsReachable(int width, int height)
        {
            RunSta(() =>
            {
                using (var hosting = new AzurePaaSConfigControl())
                using (var storage = new AzureStorageConfigControl())
                using (var ai = new AzureAIConfigControl())
                using (var host = new Form { ClientSize = new Size(width, height) })
                {
                    storage.SqlAuthMode = SqlServerAuthMode.EntraId;
                    Assert.AreEqual("Endpoint suffix: .cognitiveservices.azure.com", Find<Label>(ai, "lblCognitiveName").Text);
                    ai.CognitiveServiceName = "contoso-language";
                    Assert.AreEqual("https://contoso-language.cognitiveservices.azure.com", Find<Label>(ai, "lblCognitiveName").Text);
                    foreach (var page in new UserControl[] { hosting, storage, ai })
                    {
                        host.Controls.Clear();
                        page.Dock = DockStyle.Fill;
                        host.Controls.Add(page);
                        host.Show();
                        Application.DoEvents();
                        var layout = Find<TableLayoutPanel>(page, "servicePageLayout");
                        Assert.IsTrue(layout.Width >= page.ClientSize.Width - 20, page.Name + " must fill the page width.");
                        Assert.IsTrue(layout.Height >= page.ClientSize.Height, page.Name + " must use the page height.");
                        var services = layout.Controls.OfType<TableLayoutPanel>().ToArray();
                        var lastContent = services.Last().Controls.OfType<TableLayoutPanel>().Single();
                        var contentBottom = page.PointToClient(lastContent.PointToScreen(new Point(0, lastContent.Height))).Y;
                        Assert.IsTrue(contentBottom >= page.ClientSize.Height - 30,
                            page.Name + " must distribute spare height between sections instead of leaving it below them.");
                        foreach (var service in layout.Controls.OfType<TableLayoutPanel>())
                        {
                            var content = service.Controls.OfType<TableLayoutPanel>().Single();
                            var explanation = content.Controls.OfType<Label>().Skip(1).First();
                            Assert.IsTrue(explanation.Text.Length > 40, service.Name + " must explain its purpose.");
                            Assert.IsTrue(explanation.Width >= content.Width - 8, explanation.Name + " must fill its content width.");
                            Assert.IsTrue(explanation.Height >= explanation.GetPreferredSize(explanation.Size).Height,
                                explanation.Name + " must wrap without clipping.");
                            Assert.IsTrue(service.ClientRectangle.Contains(content.Bounds), service.Name + " must contain its fields.");
                        }
                        foreach (var field in Descendants(page).OfType<TextBox>().Where(field => field.Visible))
                        {
                            Assert.IsTrue(field.Parent.ClientRectangle.Contains(field.Bounds), field.Name + " must fit its row.");
                            Assert.IsTrue(field.Width >= 100, field.Name + " must retain a usable editing width.");
                        }
                        if (page == storage)
                        {
                            var usersButton = Find<Button>(storage, "btnSqlDatabaseUsers");
                            Assert.IsTrue(usersButton.Width <= 170 && usersButton.Height >= 32 && usersButton.Height <= 50,
                                "The database-users action must remain a compact button beside its wrapping summary.");
                            var usersLabel = Find<Label>(storage, "lblSqlDatabaseUsers");
                            Assert.IsTrue(usersLabel.Parent.ClientRectangle.Contains(usersLabel.Bounds));
                            Assert.IsTrue(usersLabel.Height >= usersLabel.GetPreferredSize(usersLabel.Size).Height);
                            var usersBounds = storage.RectangleToClient(usersLabel.RectangleToScreen(usersLabel.ClientRectangle));
                            Assert.IsTrue(storage.ClientRectangle.Contains(usersBounds),
                                "The database-users summary in Entra mode must fit at the minimum page size without scrolling.");
                        }
                        page.AutoScrollPosition = new Point(0, layout.Height);
                        Application.DoEvents();
                        var lastService = layout.Controls.OfType<TableLayoutPanel>().Last();
                        var last = Descendants(lastService).Last(control => control.Visible && control is TextBox);
                        var bounds = page.RectangleToClient(last.RectangleToScreen(last.ClientRectangle));
                        Assert.IsTrue(page.ClientRectangle.Contains(bounds), last.Name + " must be reachable by scrolling.");
                    }
                    storage.SqlAuthMode = SqlServerAuthMode.EntraId;
                    Assert.IsFalse(Find<TextBox>(storage, "txtSQLServerPassword").Enabled);
                    Assert.IsFalse(Find<TextBox>(storage, "txtSQLServerPassword").Parent.Visible);
                    storage.SQLServerUsername = "synthetic-sql-admin";
                    storage.SQLServerPassword = "synthetic-only";
                    storage.SqlAuthMode = SqlServerAuthMode.SqlLogin;
                    Assert.IsTrue(Find<TextBox>(storage, "txtSQLServerPassword").Enabled);
                    Assert.IsTrue(Find<TextBox>(storage, "txtSQLServerPassword").Parent.Visible);
                    storage.SqlAuthMode = SqlServerAuthMode.EntraId;
                    storage.SqlAuthMode = SqlServerAuthMode.SqlLogin;
                    Assert.AreEqual("synthetic-sql-admin", storage.SQLServerUsername);
                    Assert.AreEqual("synthetic-only", storage.SQLServerPassword);
                    storage.ServiceBusEnabled = false;
                    Assert.IsFalse(Find<TextBox>(storage, "txtServiceBusName").Enabled);
                    storage.ServiceBusEnabled = true;
                    Assert.IsTrue(Find<TextBox>(storage, "txtServiceBusName").Enabled);
                }
            });
        }

        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

        [TestMethod]
        public void AzureEnvironment_DescriptionAndTierLinksDoNotOverlap()
        {
            RunSta(() =>
            {
                using (var azure = new App.ControlPanel.Frames.InstallWizardPages.AzureBaseConfigControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(730, 520) })
                {
                    host.Controls.Add(azure);
                    host.Show();
                    Application.DoEvents();
                    Assert.IsTrue(Find<Label>(azure, "lblGUIAzureConfigDesc").Top > Find<Label>(azure, "lblGUIAzureConfigHeader").Bottom);
                    foreach (var tier in new[] { "Testing", "Production", "Testing" })
                    {
                        Find<ComboBox>(azure, "cmbEnvType").SelectedItem = tier;
                        Application.DoEvents();
                        foreach (var link in Descendants(azure).OfType<LinkLabel>().Where(link => link.Visible && link.Name.StartsWith("lnk")))
                        {
                            var caption = link.Parent.Controls.OfType<Label>().Single(label => label != link);
                            Assert.IsTrue(caption.Right <= link.Left, link.Name + " must not be obscured by its caption.");
                            Assert.IsTrue(link.Parent.ClientRectangle.Contains(link.Bounds));
                            Assert.IsTrue(azure.ClientRectangle.Contains(azure.RectangleToClient(link.RectangleToScreen(link.ClientRectangle))));
                        }
                    }
                }
            });
        }

        [TestMethod]
        public void NetworkingGuidance_WrapsWithoutOverlappingAndRemainsReachable()
        {
            RunSta(() =>
            {
                using (var networking = new NetworkingConfigControl { Dock = DockStyle.Fill, VNetEnabled = true })
                using (var host = new Form { ClientSize = new Size(730, 520) })
                {
                    host.Controls.Add(networking);
                    host.Show();
                    Application.DoEvents();
                    var help = Find<Label>(networking, "lblAllowPublicAccessHelp");
                    var endpoints = Find<GroupBox>(networking, "grpEndpointNames");
                    Assert.IsTrue(help.Height >= help.GetPreferredSize(help.Size).Height);
                    Assert.IsTrue(help.Bottom <= endpoints.Top, "Public-access guidance must not overlap endpoint settings.");
                    var warning = Find<Label>(networking, "lblSkuWarning");
                    networking.AutoScrollPosition = new Point(0, networking.DisplayRectangle.Height);
                    Application.DoEvents();
                    Assert.IsTrue(networking.ClientRectangle.Contains(networking.RectangleToClient(warning.RectangleToScreen(warning.ClientRectangle))),
                        "The network cost warning must be reachable at minimum size.");
                    networking.VNetEnabled = false;
                    Assert.IsFalse(Find<TextBox>(networking, "txtVNetName").Enabled);
                }
            });
        }

        [TestMethod]
        public void ServiceSpacing_ContractsAfterResizeAndLegacyLoginChanges()
        {
            RunSta(() =>
            {
                using (var storage = new AzureStorageConfigControl { Dock = DockStyle.Fill, SqlAuthMode = SqlServerAuthMode.EntraId })
                using (var host = new Form { ClientSize = new Size(730, 520) })
                {
                    host.Controls.Add(storage);
                    host.Show();
                    Application.DoEvents();
                    var layout = Find<TableLayoutPanel>(storage, "servicePageLayout");
                    var originalHeight = layout.Height;
                    for (var i = 0; i < 3; i++)
                    {
                        host.ClientSize = new Size(940, 850);
                        storage.SqlAuthMode = SqlServerAuthMode.SqlLogin;
                        Application.DoEvents();
                        host.ClientSize = new Size(730, 520);
                        storage.SqlAuthMode = SqlServerAuthMode.EntraId;
                        storage.AutoScrollPosition = Point.Empty;
                        Application.DoEvents();
                        Assert.AreEqual(originalHeight, layout.Height, "Flexible gaps must not accumulate height on resize or auth changes.");
                        var users = Find<Button>(storage, "btnSqlDatabaseUsers");
                        Assert.IsTrue(storage.ClientRectangle.Contains(storage.RectangleToClient(users.RectangleToScreen(users.ClientRectangle))));
                    }
                }
            });
        }

        [TestMethod]
        public void ReleaseSource_LatestVersionLinkIsReachableWithoutChangingTheSource()
        {
            RunSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(980, 580) })
                {
                    host.Controls.Add(wizard);
                    host.Show();
                    wizard.ConfigureUI(SyntheticConfig());
                    Find<TreeView>(wizard, "navigator").SelectedNode =
                        Find<TreeView>(wizard, "navigator").Nodes.Find("tabSources", true).Single();
                    Application.DoEvents();
                    var link = Find<LinkLabel>(wizard, "checkLatestVersion");
                    Assert.IsTrue(link.Visible && link.Enabled && link.TabStop);
                    Assert.AreEqual($"https://github.com/{SoftwareReleaseConfig.GITHUB_REPO_OWNER}/{SoftwareReleaseConfig.GITHUB_REPO_NAME}/releases/latest", link.Tag);
                    Assert.IsTrue(wizard.GetConfigurationState().DownloadLatestStable);
                    Find<RadioButton>(wizard, "rdpSpecificLocation").Checked = true;
                    Assert.IsTrue(link.Visible && link.Enabled);
                    Assert.IsFalse(wizard.GetConfigurationState().DownloadLatestStable);
                    Assert.IsFalse(Find<Label>(wizard, "lblStep").UseMnemonic);
                }
            });
        }

        [TestMethod]
        public void LogAppend_DoesNotRebuildRowsResizeColumnsOrRelayoutStatus()
        {
            RunSta(() =>
            {
                using (var log = new CountingLogListView { Dock = DockStyle.Fill, View = View.Details })
                using (var operations = new OperationCentreControl(log) { Dock = DockStyle.Fill })
                using (var host = new Form { ClientSize = new Size(740, 530) })
                {
                    log.Columns.Add("Message", 300);
                    host.Controls.Add(operations);
                    host.Show();
                    operations.BeginRun("Synthetic append replay");
                    Find<CheckBox>(operations, "followLog").Checked = false;
                    for (var i = 0; i < 60; i++)
                        operations.Append(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs { Text = "Synthetic message " + i }));
                    Application.DoEvents();
                    log.Items[20].Selected = true;
                    log.TopItem = log.Items[15];
                    var first = log.Items[0];
                    var top = log.TopItem;
                    var statusChanges = 0;
                    Find<Label>(operations, "operationStatus").TextChanged += (sender, args) => statusChanges++;
                    log.ColumnResizes = log.RowClears = 0;
                    for (var i = 60; i < 260; i++)
                        operations.Append(new InstallSolutionControl.InstallLogLVI(new InstallLogEventArgs { Text = "Synthetic message " + i }));
                    Assert.AreEqual(0, log.ColumnResizes, "Appending must not resend column widths and repaint the whole list.");
                    Assert.AreEqual(0, log.RowClears, "Appending must not rebuild existing rows.");
                    Assert.AreEqual(0, statusChanges, "The timer, not each message, refreshes the status layout.");
                    Assert.AreSame(first, log.Items[0]);
                    Assert.AreSame(top, log.TopItem, "Paused follow must preserve the viewport.");
                    Assert.IsTrue(log.Items[20].Selected);
                    Assert.IsTrue(log.IsBuffered);
                    Assert.AreEqual(260, log.Items.Count);
                    operations.CompleteRun();
                }
            });
        }

        private sealed class CountingLogListView : App.ControlPanel.Controls.BufferedListView
        {
            internal int ColumnResizes;
            internal int RowClears;
            internal bool IsBuffered => DoubleBuffered;
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x101e) ColumnResizes++;
                if (message.Msg == 0x1009) RowClears++;
                base.WndProc(ref message);
            }
        }

        [TestMethod]
        public void BackingOutOfDeploymentReview_DoesNotSaveOrCreateAnEngine()
        {
            RunSta(() =>
            {
                using (var form = new MainForm())
                using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
                {
                    var load = typeof(MainForm).GetMethod("StartForm_Load", BindingFlags.NonPublic | BindingFlags.Instance);
                    form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
                    form.LoadedConfig = SyntheticConfig();
                    var state = typeof(MainForm).GetNestedType("MainVisualState", BindingFlags.NonPublic);
                    typeof(MainForm).GetMethod("SetMenu", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(form, new[] { Enum.Parse(state, "MainMenu") });
                    form.Show();
                    Find<CheckBox>(Find<GroupBox>(form, "grpStart"), "chkDisclaimer").Checked = true;
                    Find<Button>(form, "btnStartInstall").PerformClick();
                    var wizard = Find<InstallSPOSitesControl>(form, "installSPOSitesControl");
                    Find<TreeView>(wizard, "navigator").SelectedNode =
                        Find<TreeView>(wizard, "navigator").Nodes.Find("tabInstall", true).Single();
                    var reviewShown = false;
                    timer.Tick += (sender, args) =>
                    {
                        var review = Application.OpenForms.OfType<DeploymentReviewForm>().SingleOrDefault();
                        if (review == null) return;
                        reviewShown = true;
                        timer.Stop();
                        Find<Button>(review, "cancelReview").PerformClick();
                    };
                    timer.Start();
                    Find<Button>(wizard, "btnInstall").PerformClick();
                    timer.Stop();
                    Assert.IsTrue(reviewShown, "The actual deployment button must present the scope review.");
                    Assert.IsFalse(form.HaveSavedConfig, "Backing out must not start the save/deploy sequence.");
                    Assert.IsNull(typeof(InstallSPOSitesControl).GetField("_installerEngine", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wizard));
                    Assert.IsNull(typeof(InstallSPOSitesControl).GetField("_runCts", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(wizard));
                }
            });
        }

        [TestMethod]
        public void DeploymentReview_RequiresAcknowledgementAndValidInput_AndHasSafeDefault()
        {
            RunSta(() =>
            {
                var config = SyntheticConfig();
                Assert.AreEqual(0, config.ValidatInputAndGetErrors().Count,
                    string.Join("; ", config.ValidatInputAndGetErrors()));
                using (var review = new DeploymentReviewForm(config, new[] { "Copilot interactions" }))
                {
                    review.Show();
                    Assert.IsFalse(Find<Button>(review, "confirmDeployment").Enabled);
                    Assert.IsNull(review.AcceptButton);
                    Assert.AreEqual(DialogResult.Cancel, ((Button)review.CancelButton).DialogResult);
                    Find<CheckBox>(review, "acknowledgeDeployment").Checked = true;
                    Assert.IsTrue(Find<Button>(review, "confirmDeployment").Enabled);
                    Find<CheckBox>(review, "acknowledgeDeployment").Checked = false;
                    Assert.IsFalse(Find<Button>(review, "confirmDeployment").Enabled);
                    Find<Button>(review, "cancelReview").PerformClick();
                    Assert.AreEqual(DialogResult.Cancel, review.DialogResult);
                }
                config.ResourceGroupName = string.Empty;
                using (var review = new DeploymentReviewForm(config, new[] { "Copilot interactions" }))
                {
                    Find<CheckBox>(review, "acknowledgeDeployment").Checked = true;
                    Assert.IsFalse(Find<Button>(review, "confirmDeployment").Enabled);
                    StringAssert.Contains(Find<TextBox>(review, "reviewValidation").Text, "resource group");
                }
                using (var review = new DeploymentReviewForm(SyntheticConfig(), new[] { "Copilot interactions" }))
                {
                    review.Show();
                    Find<CheckBox>(review, "acknowledgeDeployment").Checked = true;
                    Find<Button>(review, "confirmDeployment").PerformClick();
                    Assert.AreEqual(DialogResult.OK, review.DialogResult);
                }
            });
        }

        [TestMethod]
        public void ReviewScope_ListsConfiguredSitesAndLocalPackages_ButNeverCredentials()
        {
            RunSta(() =>
            {
                var config = SyntheticConfig();
                config.SolutionConfig.ImportTaskSettings.ActivityLog = true;
                config.SharePointConfig.TargetSites.Add("https://contoso.sharepoint.com/sites/synthetic");
                config.DownloadLatestStable = false;
                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation =
                    @"C:\Synthetic\Website.zip";
                using (var review = new DeploymentReviewForm(config, new[] { "Copilot interactions", "SharePoint audit" }))
                {
                    var facts = Find<ListView>(review, "reviewFacts").Items.Cast<ListViewItem>()
                        .Select(item => item.SubItems[1].Text).ToArray();
                    CollectionAssert.Contains(facts, "https://contoso.sharepoint.com/sites/synthetic");
                    CollectionAssert.Contains(facts, @"C:\Synthetic\Website.zip");
                    Assert.IsFalse(facts.Any(value => value.Contains(config.InstallerAccount.Secret)));
                    Assert.IsFalse(Find<Button>(review, "confirmDeployment").Enabled);
                }
            });
        }

        [DataTestMethod]
        [DataRow(750, 600, 1.0f)]
        [DataRow(1150, 900, 1.5f)]
        public void WorkloadDecisions_KeepGuidanceAndHelpLinksInWrappingLayouts(int width, int height, float scale)
        {
            RunSta(() =>
            {
                using (var targets = new App.ControlPanel.Controls.TargetSolutionConfigControl())
                using (var host = new Form { ClientSize = new Size(width, height) })
                {
                    targets.Dock = DockStyle.Fill;
                    host.Controls.Add(targets);
                    host.Show();
                    if (scale != 1.0f) targets.Scale(new SizeF(scale, scale));
                    Application.DoEvents();
                    var caption = Find<Label>(targets, "lblGUITargetsHeader");
                    var scroller = Find<Panel>(targets, "pnlSolutionSelectionContainer");
                    Assert.IsFalse(caption.Bounds.IntersectsWith(scroller.Bounds), "The page heading must remain above its content.");
                    var group = Find<TableLayoutPanel>(targets, "grpProductCfgInsights");
                    var columns = group.Controls.OfType<TableLayoutPanel>().Single();
                    Assert.IsTrue(group.ClientRectangle.Contains(columns.Bounds),
                        $"The workload layout {columns.Bounds} must fit its group {group.ClientRectangle}.");
                    foreach (var name in new[] { "lblCopilotDesc", "lnkInteractionHistoryHelp", "chkUsageReports", "chkDlp", "lnkDlpHelp",
                        "chkAgent365Catalog", "lnkAgent365CatalogHelp" })
                    {
                        var field = Find<Control>(targets, name);
                        Assert.IsInstanceOfType(field.Parent, typeof(TableLayoutPanel));
                        Assert.IsTrue(field.Parent.ClientRectangle.Contains(field.Bounds), name + " must remain inside its layout.");
                        Assert.IsTrue(field.Height >= field.GetPreferredSize(field.Size).Height, name + " text must not clip.");
                        if (field is CheckBox checkbox)
                        {
                            var text = TextRenderer.MeasureText(checkbox.Text, checkbox.Font,
                                new Size(Math.Max(100, checkbox.Width - 24), int.MaxValue), TextFormatFlags.WordBreak);
                            Assert.IsTrue(checkbox.Height >= text.Height + 8, name + " needs room for every wrapped line.");
                        }
                    }
                }
            });
        }

        [TestMethod]
        public void OperationCentre_UsesRealLoggerSeverityAndPhase_AndFiltersWithoutLosingMessages()
        {
            RunSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                using (var host = new Form())
                {
                    host.Controls.Add(wizard);
                    host.Show();
                    var navigator = Find<TreeView>(wizard, "navigator");
                    navigator.SelectedNode = navigator.Nodes.Find("tabInstall", true).Single();
                    var page = Find<InstallSolutionControl>(wizard, "installSolutionControl1");
                    page.SetRunningState(true);
                    page.BeginOperation("Synthetic tests", true);
                    var loggerType = typeof(MainForm).Assembly.GetType("App.ControlPanel.InstallSPOSitesControlLogger");
                    var logger = (ILogger)Activator.CreateInstance(loggerType, wizard);
                    logger.LogInformation("=== Phase: Azure backend resources ===");
                    logger.LogInformation("Synthetic info");
                    logger.LogWarning("Synthetic warning");
                    logger.LogError("Synthetic error");
                    var phases = Find<ListView>(page, "operationPhases");
                    Assert.AreEqual("Azure backend resources", phases.Items[0].Text);
                    var log = Find<ListView>(page, "lstLog");
                    Assert.AreEqual(4, log.Items.Count);
                    Find<CheckBox>(page, "followLog").Checked = false;
                    Find<ComboBox>(page, "logSeverity").SelectedIndex = 1;
                    Assert.AreEqual(2, log.Items.Count);
                    Find<TextBox>(page, "logSearch").Text = "WARNING";
                    Assert.AreEqual(1, log.Items.Count);
                    logger.LogWarning("Another synthetic warning");
                    Assert.AreEqual(2, log.Items.Count);
                    var operations = Find<OperationCentreControl>(page, "operationCentre");
                    Assert.AreEqual(5, operations.FullLogText.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length);
                    Find<TextBox>(page, "logSearch").Text = string.Empty;
                    Find<ComboBox>(page, "logSeverity").SelectedIndex = 0;
                    Assert.AreEqual(5, log.Items.Count);
                    Find<Button>(page, "btnCancel").PerformClick();
                    StringAssert.Contains(Find<Label>(page, "operationStatus").Text, "waiting for a safe checkpoint");
                    page.CompleteOperation();
                    page.SetRunningState(false);
                    StringAssert.Contains(Find<Label>(page, "operationStatus").Text, "Finished after a cancellation request");
                    StringAssert.Contains(Find<Label>(page, "operationStatus").Text, "1 error message(s), 2 warning message(s)");
                    Assert.IsFalse(phases.Items[0].SubItems[1].Text.Contains("In progress"));
                    Assert.IsFalse(Find<CheckBox>(page, "followLog").Checked);
                    Find<Button>(page, "reviewOptions").PerformClick();
                    Assert.IsTrue(Find<GroupBox>(page, "grpTasks").Visible);
                    page.BeginOperation("Next synthetic test", false);
                    Assert.AreEqual(0, log.Items.Count);
                    Assert.AreEqual(0, phases.Items.Count);
                    StringAssert.Contains(Find<Label>(page, "operationStatus").Text, "0 error message(s), 0 warning message(s)");
                    Assert.IsFalse(Find<Button>(page, "btnCancel").Visible, "Verification has no cancellable engine contract.");
                }
            });
        }

        private static SolutionInstallConfig SyntheticConfig()
        {
            var config = SolutionInstallConfig.NewConfig();
            config.SolutionConfig.ImportTaskSettings.Copilot = true;
            config.Subscription = new AzureSubscription(Guid.Empty.ToString(), "Contoso synthetic subscription");
            config.AzureLocationName = "westeurope";
            config.ResourceGroupName = "contoso-analytics";
            config.AppServiceWebAppName = "contoso-analytics-web";
            config.AppServicePlanName = "contoso-analytics-plan";
            config.AppInsightsName = "contoso-insights";
            config.AppInsightsWorkspaceName = "contoso-logs";
            config.KeyVaultName = "contoso-analytics-vault";
            config.AutomationAccountName = "contoso-automation";
            config.StorageAccountName = "contosoanalytics";
            config.SQLServerName = "contoso-sql";
            config.SQLServerDatabaseName = "ContosoAnalytics";
            config.SqlAuthMode = SqlServerAuthMode.EntraId;
            config.CognitiveServicesEnabled = false;
            config.ServiceBusEnabled = false;
            config.InstallerAccount = SyntheticAccount();
            config.ActivityAccount = SyntheticAccount();
            return config;
        }

        private static AppRegistrationCredentials SyntheticAccount() => new AppRegistrationCredentials
        {
            ClientId = Guid.Empty.ToString(),
            DirectoryId = Guid.Empty.ToString(),
            Secret = "synthetic-only"
        };

        internal static T Find<T>(Control parent, string name) where T : Control =>
            (T)parent.Controls.Find(name, true).Single();

        private static void Capture(Form form, string directory, string name)
        {
            Application.DoEvents();
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(directory, name + ".png"));
            }
        }

        internal static void RunSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    Application.EnableVisualStyles();
                    action();
                }
                catch (Exception ex) { failure = ex; }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(60)), "Native installer test timed out.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
