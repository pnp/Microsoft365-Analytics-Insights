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
                    var operations = Find<OperationCentreControl>(page, "operationCentre");
                    Assert.IsTrue(operations.MinimumSize.Height >= 100 + log.Margin.Vertical,
                        "The outer percent row must enforce enough height for its nested viewport.");
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

        private static T Find<T>(Control parent, string name) where T : Control =>
            (T)parent.Controls.Find(name, true).Single();

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
