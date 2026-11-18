using App.ControlPanel;
using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Frames;
using App.ControlPanel.Frames.InstallWizard;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;

namespace Tests.UnitTests.InstallTests
{
    [TestClass]
    public class InstallerLayoutTests
    {
        [DataTestMethod]
        [DataRow(560, 480, 1.0f)]
        [DataRow(480, 360, 1.0f)]
        [DataRow(900, 650, 1.5f)]
        [DataRow(1028, 620, 2.0f)]
        public void DatabaseUpgrade_ActionsStayVisibleAndContentRemainsReachable(int width, int height, float scale)
        {
            RunOnSta(() =>
            {
                using (var form = new DatabaseUpgradeForm())
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-3000, -3000);
                    form.Show();
                    if (scale != 1.0f)
                    {
                        form.Scale(new SizeF(scale, scale));
                        foreach (var control in form.Controls.Cast<Control>())
                            control.Font = new Font(control.Font.FontFamily, control.Font.Size * scale);
                    }
                    var confirm = Find<CheckBox>(form, "chkConfirm");
                    var upgrade = Find<Button>(form, "btnUpgrade");
                    var close = Find<Button>(form, "btnCancel");
                    Assert.IsFalse(upgrade.Enabled, "Opening the dialog must not bypass confirmation.");
                    foreach (var size in new[] { new Size(width, height), new Size(width + 200, height + 200), new Size(width, height) })
                    {
                        form.ClientSize = size;
                        Application.DoEvents();
                        foreach (var control in new Control[] { confirm, upgrade, close })
                        {
                            var bounds = form.RectangleToClient(control.Parent.RectangleToScreen(control.Bounds));
                            Assert.IsTrue(form.ClientRectangle.Contains(bounds),
                                control.Name + " must stay entirely inside the dialog: " + bounds);
                            Assert.IsTrue(control.Height >= control.GetPreferredSize(new Size(control.Width, 0)).Height,
                                control.Name + " must not clip its text.");
                        }
                        var viewport = Find<Panel>(form, "upgradeViewport");
                        foreach (var name in new[] { "txtConnectionString", "txtEntraSecret", "txtLog" })
                        {
                            var field = Find<TextBox>(form, name);
                            viewport.ScrollControlIntoView(field);
                            Application.DoEvents();
                            var bounds = viewport.RectangleToClient(field.Parent.RectangleToScreen(field.Bounds));
                            Assert.IsTrue(viewport.ClientRectangle.Contains(bounds),
                                name + " must be reachable by scrolling: field=" + bounds + "; viewport=" + viewport.ClientRectangle
                                + "; group=" + field.Parent.Bounds + "; scroll=" + viewport.AutoScrollPosition);
                            Assert.IsTrue(field.Width >= 100, name + " must retain a usable editing width.");
                        }
                        Assert.IsFalse(viewport.HorizontalScroll.Visible, "The dialog must not require horizontal scrolling.");
                        var footer = Find<TableLayoutPanel>(form, "upgradeFooter");
                        Assert.IsFalse(viewport.Bounds.IntersectsWith(footer.Bounds), "Scrolling must not obscure the actions.");
                    }
                    Find<TextBox>(form, "txtConnectionString").Text =
                        "data source=contoso.database.windows.net;initial catalog=Analytics;";
                    Assert.IsTrue(Find<GroupBox>(form, "grpEntra").Enabled);
                    confirm.Checked = true;
                    Assert.IsTrue(upgrade.Enabled);
                    var setLoading = typeof(DatabaseUpgradeForm).GetMethod("SetLoadingState", BindingFlags.Instance | BindingFlags.NonPublic);
                    setLoading.Invoke(form, new object[] { true });
                    Assert.IsFalse(upgrade.Enabled);
                    Assert.IsTrue(close.Enabled, "Read-only autodetection must remain abandonable.");
                    var progress = Find<ProgressBar>(form, "progressBar");
                    Assert.IsTrue(form.ClientRectangle.Contains(form.RectangleToClient(progress.Parent.RectangleToScreen(progress.Bounds))));
                    setLoading.Invoke(form, new object[] { false });
                    Assert.IsTrue(upgrade.Enabled);
                    Assert.IsTrue(upgrade.Focus(), "The upgrade action must remain keyboard-reachable.");
                    var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
                    if (!string.IsNullOrEmpty(directory))
                    {
                        using (var image = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                            image.Save(System.IO.Path.Combine(directory, $"schema-upgrade-{width}-{height}.png"));
                        }
                    }
                    close.PerformClick();
                    Assert.IsFalse(form.Visible, "The visible Close action must still work.");
                }
            });
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("  preserved invalid disabled value  ")]
        public void HealthMonitoring_DisabledEmailRoundTripsWithoutNormalisation(string email)
        {
            RunOnSta(() =>
            {
                using (var page = new AzurePaaSConfigControl())
                {
                    page.HealthMonitoring = new Common.Entities.Installer.HealthMonitoringConfig
                    {
                        Enabled = false, NotificationEmail = email, MissingHeartbeatMinutes = -123
                    };
                    Assert.AreEqual(email, page.HealthMonitoring.NotificationEmail);
                    Assert.AreEqual(-123, page.HealthMonitoring.MissingHeartbeatMinutes);
                    Assert.AreEqual(0, page.HealthMonitoring.ValidatInputAndGetErrors().Count);
                }
            });
        }
        [DataTestMethod]
        [DataRow(false, 0)]
        [DataRow(false, int.MinValue)]
        [DataRow(true, 14)]
        [DataRow(true, 1441)]
        public void HealthMonitoring_OutOfRangeSavedThresholdLoadsWithoutLosingValidation(bool enabled, int minutes)
        {
            RunOnSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                {
                    var config = SolutionInstallConfig.NewConfig();
                    config.HealthMonitoring.Enabled = enabled;
                    config.HealthMonitoring.NotificationEmail = "operator@contoso.example";
                    config.HealthMonitoring.MissingHeartbeatMinutes = minutes;
                    wizard.ConfigureUI(config);
                    var settings = wizard.GetConfigurationState().HealthMonitoring;
                    Assert.AreEqual(minutes, settings.MissingHeartbeatMinutes, "Opening a config must not silently change it.");
                    Assert.AreEqual(enabled, settings.Enabled);
                    Assert.AreEqual(enabled ? 1 : 0, settings.ValidatInputAndGetErrors().Count);
                }
            });
        }

        [DataTestMethod]
        [DataRow(600, 500, 1.0f, false)]
        [DataRow(900, 750, 1.5f, false)]
        [DataRow(1200, 1000, 2.0f, false)]
        [DataRow(600, 500, 1.0f, true)]
        [DataRow(900, 750, 1.5f, true)]
        [DataRow(1200, 1000, 2.0f, true)]
        public void HealthMonitoring_FieldsAndNoticeRemainReachable_WhenResizedOrScaled(int width, int height, float scale, bool expanded)
        {
            RunOnSta(() =>
            {
                using (var page = new AzurePaaSConfigControl())
                using (var host = ShowControl(page, new Size(width, height)))
                {
                    page.HealthMonitoring = new Common.Entities.Installer.HealthMonitoringConfig
                    {
                        Enabled = true, NotificationEmail = "operator@contoso.example"
                    };
                    if (scale != 1.0f)
                    {
                        page.Scale(new SizeF(scale, scale));
                        page.Font = new Font(page.Font.FontFamily, page.Font.Size * scale);
                        Application.DoEvents();
                    }
                    if (expanded)
                    {
                        ClickLink(Find<LinkLabel>(page, "lnkHealthDetails"));
                        Application.DoEvents();
                    }
                    var content = Find<TableLayoutPanel>(page, "insightsServiceContent");
                    var fields = new Control[]
                    {
                        Find<CheckBox>(page, "chkHealthMonitoring"),
                        Find<TextBox>(page, "txtHealthEmail"),
                        Find<NumericUpDown>(page, "numHealthMinutes")
                    };
                    foreach (var field in fields)
                    {
                        page.ScrollControlIntoView(field);
                        Application.DoEvents();
                        Assert.IsTrue(page.ClientRectangle.IntersectsWith(page.RectangleToClient(
                            field.Parent.RectangleToScreen(field.Bounds))), field.Name + " must remain reachable by scrolling.");
                    }
                    Assert.IsFalse(page.HorizontalScroll.Visible, "Hosting settings must not require horizontal scrolling.");
                    var check = (CheckBox)fields[0];
                    Assert.IsTrue(check.Width >= check.GetPreferredSize(check.Size).Width, "Alert opt-in must not clip.");
                    var notice = content.Controls.OfType<Label>().Last();
                    Assert.IsTrue(notice.AutoSize, "Cost and health limitations must wrap.");
                    CaptureHealthPreview(host, width, height, expanded ? "expanded-fields" : "fields");
                    page.ScrollControlIntoView(notice);
                    Application.DoEvents();
                    Assert.IsTrue(page.ClientRectangle.IntersectsWith(page.RectangleToClient(
                        notice.Parent.RectangleToScreen(notice.Bounds))), "The expanded cost/coverage notice must be reachable.");
                    Assert.IsFalse(page.HorizontalScroll.Visible);
                    CaptureHealthPreview(host, width, height, expanded ? "expanded-notice" : "notice");
                    var helper = Find<Label>(page, "lblHealthThresholdHelp");
                    Assert.IsTrue(helper.Height >= helper.GetPreferredSize(new Size(helper.Width, 0)).Height,
                        "The heartbeat explanation must wrap without clipping.");
                    if (expanded)
                    {
                        var details = Find<TableLayoutPanel>(page, "healthMonitoringDetails");
                        foreach (var label in details.Controls.OfType<Label>())
                            Assert.IsTrue(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                                "Monitoring help must not clip: " + label.Text);
                        page.ScrollControlIntoView(Find<LinkLabel>(page, "lnkHealthWiki"));
                        Application.DoEvents();
                        Assert.IsFalse(page.HorizontalScroll.Visible);
                        CaptureHealthPreview(host, width, height, "expanded-guide");
                    }
                }
            });
        }

        [TestMethod]
        public void HealthMonitoring_HelpWorksWhenDisabled_WithoutChangingConfiguration()
        {
            RunOnSta(() =>
            {
                using (var page = new AzurePaaSConfigControl())
                using (var host = ShowControl(page, new Size(800, 650)))
                {
                    page.HealthMonitoring = new Common.Entities.Installer.HealthMonitoringConfig
                    {
                        Enabled = false, NotificationEmail = "operator@contoso.example", MissingHeartbeatMinutes = 30
                    };
                    var minutes = Find<NumericUpDown>(page, "numHealthMinutes");
                    StringAssert.Contains(minutes.AccessibleDescription, "no heartbeat");
                    StringAssert.Contains(minutes.AccessibleDescription, "every 5 minutes");
                    var link = Find<LinkLabel>(page, "lnkHealthDetails");
                    var details = Find<TableLayoutPanel>(page, "healthMonitoringDetails");
                    Assert.IsFalse(details.Visible);
                    Assert.IsTrue(link.Enabled, "Help must remain available when monitoring is disabled.");
                    ClickLink(link);
                    Assert.IsTrue(details.Visible);
                    Assert.IsTrue(Find<LinkLabel>(details, "lnkHealthWiki").TabStop,
                        "The documentation link must be reachable by keyboard.");
                    var text = string.Join("\n", details.Controls.OfType<Label>().Select(label => label.Text));
                    StringAssert.Contains(text, "24 hours");
                    StringAssert.Contains(text, "Client-secret expiry is unknown");
                    StringAssert.Contains(text, "Changing this screen alone does not update Azure");
                    ClickLink(link);
                    Assert.IsFalse(details.Visible);
                    Assert.IsTrue(link.Focused || link.CanFocus);
                    Assert.IsFalse(page.HealthMonitoring.Enabled);
                    Assert.AreEqual(30, page.HealthMonitoring.MissingHeartbeatMinutes);
                    Assert.AreEqual("operator@contoso.example", page.HealthMonitoring.NotificationEmail);
                }
            });
        }

        [TestMethod]
        public void TeamsChannelBudget_AdvancedSettingSurvivesWizardAndLegacyConfigReset()
        {
            RunOnSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                {
                    var config = SolutionInstallConfig.NewConfig();
                    config.TeamsMaxChannelsPerTeamPerCycle = 8;
                    wizard.ConfigureUI(config);
                    Assert.AreEqual(8, wizard.GetConfigurationState().TeamsMaxChannelsPerTeamPerCycle);
                    wizard.ConfigureUI(SolutionInstallConfig.NewConfig());
                    Assert.AreEqual(0, wizard.GetConfigurationState().TeamsMaxChannelsPerTeamPerCycle);
                }
            });
        }

        [TestMethod]
        public void HealthMonitoring_RoundTripsThroughWizard_AndOptOutDisablesFields()
        {
            RunOnSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                {
                    var config = SolutionInstallConfig.NewConfig();
                    config.HealthMonitoring.NotificationEmail = "operator@contoso.example";
                    config.HealthMonitoring.MissingHeartbeatMinutes = 30;
                    wizard.ConfigureUI(config);
                    Assert.IsTrue(Find<TextBox>(wizard, "txtHealthEmail").Enabled);
                    Assert.IsTrue(Find<NumericUpDown>(wizard, "numHealthMinutes").Enabled);
                    var roundTrip = wizard.GetConfigurationState();
                    Assert.IsTrue(roundTrip.HealthMonitoring.Enabled);
                    Assert.AreEqual("operator@contoso.example", roundTrip.HealthMonitoring.NotificationEmail);
                    Assert.AreEqual(30, roundTrip.HealthMonitoring.MissingHeartbeatMinutes);
                    Find<CheckBox>(wizard, "chkHealthMonitoring").Checked = false;
                    Assert.IsFalse(Find<TextBox>(wizard, "txtHealthEmail").Enabled);
                    Assert.IsFalse(Find<NumericUpDown>(wizard, "numHealthMinutes").Enabled);
                    Assert.IsFalse(wizard.GetConfigurationState().HealthMonitoring.Enabled);
                }
            });
        }

        [TestMethod]
        public void Welcome_ExplainsConfigurationDoesNotDeploy_AndRetainsRiskAcknowledgement()
        {
            RunOnSta(() =>
            {
                using (var form = new MainForm())
                {
                    var welcome = Find<GroupBox>(form, "grpStart");
                    var start = Find<Button>(welcome, "btnStartInstall");
                    var disclaimer = Find<CheckBox>(welcome, "chkDisclaimer");
                    disclaimer.Checked = true;
                    disclaimer.Checked = false;
                    Assert.IsFalse(start.Enabled);
                    disclaimer.Checked = true;
                    Assert.IsTrue(start.Enabled);
                    StringAssert.Contains(start.Text, "Configure solution");
                    StringAssert.Contains(Find<Label>(welcome, "lblInstallDesc").Text, "does not start installation");
                    Assert.IsNull(form.AcceptButton, "Enter must not become an installation shortcut.");
                    Assert.AreEqual(DockStyle.Fill, Find<TableLayoutPanel>(form, "welcomeLayout").Dock);
                    Assert.IsTrue(Find<Label>(form, "lblIntroText").AutoSize);
                    Assert.IsTrue(Find<Label>(welcome, "label1").AutoSize);
                    Assert.AreEqual(DockStyle.Fill, Find<InstallSPOSitesControl>(form, "installSPOSitesControl").Dock);
                }
            });
        }

        [TestMethod]
        public void MainWindow_MinimumSizeKeepsWelcomeAndDeploymentActionsVisible()
        {
            RunOnSta(() =>
            {
                using (var form = new MainForm())
                {
                    // Do not read the machine's saved preferences or proxy configuration in a UI test.
                    var load = typeof(MainForm).GetMethod("StartForm_Load", BindingFlags.Instance | BindingFlags.NonPublic);
                    form.Load -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), form, load);
                    form.LoadedConfig = SolutionInstallConfig.NewConfig();
                    var state = typeof(MainForm).GetNestedType("MainVisualState", BindingFlags.NonPublic);
                    typeof(MainForm).GetMethod("SetMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                        .Invoke(form, new[] { Enum.Parse(state, "MainMenu") });
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-3000, -3000);
                    form.Size = form.MinimumSize;
                    form.Show();
                    Application.DoEvents();
                    var welcome = Find<GroupBox>(form, "grpStart");
                    var welcomeLayout = Find<TableLayoutPanel>(welcome, "welcomeLayout");
                    Assert.IsTrue(welcomeLayout.ClientRectangle.Contains(Find<Button>(welcome, "btnStartInstall").Bounds));
                    var page = Find<InstallSolutionControl>(form, "installSolutionControl1");
                    var installs = 0;
                    page.Install += (sender, args) => installs++;
                    Find<CheckBox>(welcome, "chkDisclaimer").Checked = true;
                    Find<Button>(welcome, "btnStartInstall").PerformClick();
                    Assert.AreEqual(0, installs, "Entering the wizard must not start deployment.");
                    var navigator = Find<TreeView>(form, "navigator");
                    navigator.SelectedNode = navigator.Nodes.Find("tabInstall", true).Single();
                    Application.DoEvents();
                    var layout = Find<TableLayoutPanel>(page, "installLayout");
                    var log = Find<ListView>(page, "lstLog");
                    var actions = Find<FlowLayoutPanel>(page, "actionLayout");
                    Assert.IsTrue(layout.ClientRectangle.Contains(actions.Bounds));
                    var logBounds = layout.RectangleToClient(log.Parent.RectangleToScreen(log.Bounds));
                    Assert.IsTrue(layout.ClientRectangle.Contains(logBounds));
                    Assert.IsTrue(log.Height >= 100, "The minimum window size must leave a useful log viewport.");
                    Assert.IsFalse(logBounds.IntersectsWith(actions.Bounds), "The log must not extend underneath the actions.");
                    var wizard = Find<InstallSPOSitesControl>(form, "installSPOSitesControl");
                    Assert.IsFalse(Find<TableLayoutPanel>(wizard, "workspaceLayout").Bounds
                        .IntersectsWith(Find<TableLayoutPanel>(wizard, "navigationLayout").Bounds));
                }
            });
        }

        [TestMethod]
        public void Navigation_BackNextAndStepCountsFollowVisiblePages_WithoutChangingConfiguration()
        {
            RunOnSta(() =>
            {
                using (var wizard = new InstallSPOSitesControl())
                using (var host = ShowControl(wizard, new Size(1100, 750)))
                {
                    var config = SolutionInstallConfig.NewConfig();
                    config.SolutionConfig.ImportTaskSettings.WebTraffic = false;
                    config.SolutionConfig.ImportTaskSettings.ActivityLog = false;
                    wizard.ConfigureUI(config);
                    var navigator = Find<TreeView>(wizard, "navigator");
                    var back = Find<Button>(wizard, "btnBack");
                    var next = Find<Button>(wizard, "btnNext");
                    var step = Find<Label>(wizard, "lblStep");
                    Assert.IsFalse(back.Enabled);
                    Assert.IsTrue(next.Enabled);
                    Assert.AreEqual($"Step 1 of {Nodes(navigator).Length}: Overview", step.Text);
                    Assert.AreEqual("&Next: Workloads", next.Text);
                    next.PerformClick();
                    Assert.AreEqual("tabTargets", navigator.SelectedNode.Name);
                    Assert.IsTrue(back.Enabled);
                    back.PerformClick();
                    Assert.AreEqual("tabOverview", navigator.SelectedNode.Name);

                    config.SolutionConfig.ImportTaskSettings.WebTraffic = true;
                    wizard.ConfigureUI(config);
                    Assert.AreEqual($"Step 1 of {Nodes(navigator).Length}: Overview", step.Text);
                    Assert.AreEqual(1, navigator.Nodes.Find("tabSharePoint", true).Length);
                    navigator.SelectedNode = navigator.Nodes.Find("tabSharePoint", true).Single();
                    Assert.AreEqual("&Next: Release && packages", next.Text);
                    next.PerformClick();
                    Assert.AreEqual("tabSources", navigator.SelectedNode.Name);
                    Assert.AreEqual("&Next: Review && deploy", next.Text);
                    next.PerformClick();
                    Assert.AreEqual("tabInstall", navigator.SelectedNode.Name);
                    Assert.IsFalse(next.Enabled);
                    Assert.IsTrue(back.Enabled);
                    Assert.AreEqual($"Step {Nodes(navigator).Length} of {Nodes(navigator).Length}: Review & deploy", step.Text);
                    back.PerformClick();
                    Assert.AreEqual("tabSources", navigator.SelectedNode.Name);
                    Assert.IsTrue(wizard.GetConfigurationState().SolutionConfig.ImportTaskSettings.WebTraffic);
                    Assert.AreEqual(config.ConfigSchemaVersion, wizard.GetConfigurationState().ConfigSchemaVersion);
                }
            });
        }

        [TestMethod]
        public void Navigation_WorkingStateDisablesBothDirections_AndRestoresThem()
        {
            RunOnSta(() =>
            {
                using (var form = new MainForm())
                {
                    var wizard = Find<InstallSPOSitesControl>(form, "installSPOSitesControl");
                    var navigator = Find<TreeView>(wizard, "navigator");
                    navigator.SelectedNode = navigator.Nodes.Find("tabCredentials", true).Single();
                    var setState = typeof(InstallSPOSitesControl).GetMethod("SetFormGUIState",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    setState.Invoke(wizard, new object[] { AppWaitState.Working });
                    Assert.IsFalse(Find<Button>(wizard, "btnBack").Enabled);
                    Assert.IsFalse(Find<Button>(wizard, "btnNext").Enabled);
                    navigator.SelectedNode = navigator.Nodes.Find("tabTargets", true).Single();
                    Assert.AreEqual("tabCredentials", navigator.SelectedNode.Name, "A running operation must block direct sidebar navigation.");
                    setState.Invoke(wizard, new object[] { AppWaitState.Ready });
                    Assert.IsTrue(Find<Button>(wizard, "btnBack").Enabled);
                    Assert.IsTrue(Find<Button>(wizard, "btnNext").Enabled);
                }
            });
        }

        [DataTestMethod]
        [DataRow(600, 500, 1.0f)]
        [DataRow(900, 620, 1.0f)]
        [DataRow(900, 750, 1.5f)]
        [DataRow(1200, 1000, 2.0f)]
        [DataRow(1028, 749, 2.0f)]
        public void InstallPage_OptionsAndActionsDoNotOverlap_WhenResizedOrScaled(int width, int height, float scale)
        {
            RunOnSta(() =>
            {
                using (var page = new InstallSolutionControl())
                using (var host = ShowControl(page, new Size(width, height)))
                {
                    var layout = Find<TableLayoutPanel>(page, "installLayout");
                    if (scale != 1.0f)
                    {
                        page.Scale(new SizeF(scale, scale));
                        layout.Font = new Font(layout.Font.FontFamily, layout.Font.Size * scale);
                        Application.DoEvents();
                    }
                    var options = Find<TableLayoutPanel>(page, "tasksLayout");
                    var checkboxes = options.Controls.Cast<CheckBox>().OrderBy(control => control.Top).ToArray();
                    for (var i = 0; i < checkboxes.Length; i++)
                    {
                        Assert.IsTrue(options.ClientRectangle.Contains(checkboxes[i].Bounds), checkboxes[i].Name);
                        Assert.IsTrue(checkboxes[i].Height >= checkboxes[i].GetPreferredSize(checkboxes[i].Size).Height,
                            checkboxes[i].Name + " text must not clip.");
                        if (i > 0) Assert.IsFalse(checkboxes[i].Bounds.IntersectsWith(checkboxes[i - 1].Bounds));
                    }
                    var log = Find<ListView>(page, "lstLog");
                    var actions = Find<FlowLayoutPanel>(page, "actionLayout");
                    Assert.IsTrue(log.Height >= 100, $"Log viewport must retain 100px; actual={log.Bounds}, layout={layout.Bounds}, actions={actions.Bounds}.");
                    var operations = Find<OperationCentreControl>(page, "operationCentre");
                    Assert.IsTrue(operations.MinimumSize.Height >= 100 + log.Margin.Vertical,
                        "The outer percent row must enforce enough height for its nested viewport.");
                    var logBounds = layout.RectangleToClient(log.Parent.RectangleToScreen(log.Bounds));
                    Assert.IsFalse(logBounds.IntersectsWith(actions.Bounds),
                        $"Log={logBounds}; parent={log.Parent.Bounds}; actions={actions.Bounds}; rows={string.Join(",", layout.GetRowHeights())}");
                    Assert.IsTrue(layout.ClientRectangle.Contains(actions.Bounds), "Action buttons must stay reachable.");
                    var buttons = actions.Controls.Cast<Button>().Where(button => button.Visible).ToArray();
                    foreach (var button in buttons)
                    {
                        Assert.IsTrue(actions.ClientRectangle.Contains(button.Bounds), button.Name);
                        Assert.IsTrue(button.Width >= button.GetPreferredSize(button.Size).Width,
                            button.Name + " text must not clip.");
                    }
                    foreach (var left in buttons)
                    foreach (var right in buttons.Where(button => button != left))
                        Assert.IsFalse(left.Bounds.IntersectsWith(right.Bounds));
                    Assert.AreEqual(4, checkboxes.Length);
                    Assert.IsTrue(Find<Label>(page, "lblDeploymentNotice").AutoSize);
                    Assert.AreEqual(0, log.Items.Count, "Designer placeholder messages must be cleared on load.");
                }
            });
        }

        [TestMethod]
        public void InstallPage_RunningAndCancellationKeepLogAvailable_AndDoNotChangeTasks()
        {
            RunOnSta(() =>
            {
                using (var page = new InstallSolutionControl())
                using (var host = ShowControl(page, new Size(800, 600)))
                {
                    page.TasksConfig = new InstallTasksConfig
                    {
                        InstallLatestSolutionContent = true,
                        UpgradeSchema = true,
                        OpenAdminSitePostInstall = false
                    };
                    page.AllowTelemetry = true;
                    var installs = 0;
                    var tests = 0;
                    var cancellations = 0;
                    page.Install += (sender, args) => installs++;
                    page.TestConfig += (sender, args) => tests++;
                    page.CancelRequested += (sender, args) => cancellations++;
                    Find<Button>(page, "btnRunTests").PerformClick();
                    Assert.AreEqual(1, tests);
                    Assert.AreEqual(0, installs, "Testing must not raise the deployment event.");
                    Find<Button>(page, "btnInstall").PerformClick();
                    Assert.AreEqual(1, installs);
                    page.SetRunningState(true);
                    Assert.IsFalse(Find<Button>(page, "btnInstall").Visible);
                    Assert.IsFalse(Find<Button>(page, "btnRunTests").Enabled);
                    Assert.IsFalse(Find<GroupBox>(page, "grpTasks").Enabled);
                    Assert.IsTrue(Find<ListView>(page, "lstLog").Enabled);
                    Assert.IsTrue(Find<Button>(page, "btnCopyLog").Enabled);
                    var cancel = Find<Button>(page, "btnCancel");
                    Assert.IsTrue(cancel.Visible);
                    Assert.IsTrue(cancel.Enabled);
                    cancel.PerformClick();
                    cancel.PerformClick();
                    Assert.AreEqual(1, cancellations);
                    page.SetRunningState(false);
                    Assert.IsFalse(cancel.Visible);
                    Assert.IsTrue(Find<Button>(page, "btnInstall").Visible);
                    Assert.IsTrue(Find<Button>(page, "btnRunTests").Enabled);
                    Assert.IsTrue(Find<GroupBox>(page, "grpTasks").Enabled);
                    Assert.IsTrue(page.TasksConfig.InstallLatestSolutionContent);
                    Assert.IsTrue(page.TasksConfig.UpgradeSchema);
                    Assert.IsFalse(page.TasksConfig.OpenAdminSitePostInstall);
                    Assert.IsTrue(page.AllowTelemetry);
                }
            });
        }

        private static Form ShowControl(Control control, Size size)
        {
            var host = new Form
            {
                ClientSize = size,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                ShowInTaskbar = false
            };
            control.Dock = DockStyle.Fill;
            host.Controls.Add(control);
            host.Show();
            Application.DoEvents();
            return host;
        }

        private static void CaptureHealthPreview(Form host, int width, int height, string stage)
        {
            var directory = Environment.GetEnvironmentVariable("INSTALLER_PREVIEW_DIR");
            if (string.IsNullOrEmpty(directory)) return;
            using (var image = new Bitmap(host.Width, host.Height))
            {
                host.DrawToBitmap(image, new Rectangle(Point.Empty, host.Size));
                image.Save(System.IO.Path.Combine(directory, $"health144-{width}-{height}-{stage}.png"));
            }
        }

        private static T Find<T>(Control parent, string name) where T : Control =>
            (T)parent.Controls.Find(name, true).Single();

        private static void ClickLink(LinkLabel link) =>
            typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) });

        private static TreeNode[] Nodes(TreeView navigator) => navigator.Nodes.Cast<TreeNode>()
            .SelectMany(group => group.Nodes.Cast<TreeNode>()).ToArray();

        private static void RunOnSta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { Application.EnableVisualStyles(); action(); }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "Installer UI test did not finish.");
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
