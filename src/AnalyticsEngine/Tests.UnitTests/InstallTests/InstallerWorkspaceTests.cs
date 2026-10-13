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
                    form.LoadedConfig = SolutionInstallConfig.NewConfig();
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
                    foreach (var name in new[] { "tabTargets", "tabOverview", "tabSources", "tabInstall" })
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
                    }
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
