using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using System;
using System.ComponentModel;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class InstallSolutionControl : UserControl
    {
        private readonly OperationCentreControl _operations;
        private readonly Button _reviewOptions = new Button { Name = "reviewOptions", Text = "&Review options",
            AutoSize = true, Padding = new Padding(8, 4, 8, 4), MinimumSize = new System.Drawing.Size(150, 34), Visible = false };
        public InstallSolutionControl()
        {
            InitializeComponent();
            _operations = new OperationCentreControl(lstLog) { Dock = DockStyle.Fill };
            installLayout.Controls.Add(_operations, 0, 3);
            actionLayout.Controls.Add(_reviewOptions);
            _reviewOptions.Click += (sender, args) =>
            {
                grpTasks.Visible = true;
                lblDeploymentNotice.Visible = true;
                lblGUIInstallHeader.Text = "Review and deploy";
                lblGUIInstallDesc.Text = "Review your options and the previous operation before deploying again.";
                _operations.ShowPhases(false);
                _reviewOptions.Visible = false;
            };
        }

        private void InstallSolutionControl_Load(object sender, EventArgs e)
        {
            lstLog.Items.Clear();
        }

        [Browsable(false)]
        public bool AllowTelemetry { get => chkInstallOptionAllowUsageStats.Checked; set { chkInstallOptionAllowUsageStats.Checked = value; } }

        [Browsable(false)]
        public InstallTasksConfig TasksConfig
        {
            get
            {
                var tasksConfig = new InstallTasksConfig();
                tasksConfig.InstallLatestSolutionContent = chkInstallOptionWebJobs.Checked;
                tasksConfig.UpgradeSchema = chkInstallOptionUpgradeDB.Checked;
                tasksConfig.OpenAdminSitePostInstall = chkInstallOptionOpenAdminSite.Checked;
                return tasksConfig;
            }
            set
            {
                chkInstallOptionWebJobs.Checked = value.InstallLatestSolutionContent;
                chkInstallOptionUpgradeDB.Checked = value.UpgradeSchema;
                chkInstallOptionOpenAdminSite.Checked = value.OpenAdminSitePostInstall;
            }
        }

        public delegate void InstallEventHander(object sender, EventArgs eventArgs);
        public event InstallEventHander Install;

        public delegate void TestConfigEventHander(object sender, EventArgs eventArgs);
        public event TestConfigEventHander TestConfig;

        /// <summary>Raised when the user clicks Cancel during a running install.</summary>
        public event EventHandler CancelRequested;

        /// <summary>
        /// Toggle button visibility / enabled state to reflect whether an install is currently running.
        /// When running: Install/Upgrade and Test Configuration are disabled, the Install Tasks
        /// checkboxes are locked (so the user can't change tasks mid-run), and Cancel takes the
        /// install button's slot. When idle: Install/Upgrade returns, Cancel is hidden, tasks editable.
        /// </summary>
        public void SetRunningState(bool running)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => SetRunningState(running)));
                return;
            }
            btnInstall.Visible = !running;
            btnRunTests.Enabled = !running;
            btnCancel.Visible = running;
            btnCancel.Enabled = running;
            // Lock the Install Tasks checkboxes — changing them mid-run would silently desync from
            // what the engine actually executed. Log ListView, Copy-to-clipboard, and Cancel stay
            // enabled so the user can still read/scroll/copy progress and abort.
            grpTasks.Enabled = !running;
            _reviewOptions.Enabled = !running;
        }

        #region Logging

        internal void LogItemOnUIThread(InstallLogLVI installLogLVI)
        {
            LogItemOnUIThread(installLogLVI, false);
        }
        internal void LogItemOnUIThread(InstallLogLVI installLogLVI, bool fatalError)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => LogItemOnUIThread(installLogLVI, fatalError)));
            }
            else
            {
                _operations.Append(installLogLVI);

                if (fatalError)
                {
                    MessageBox.Show(installLogLVI.Text, "Unexpected Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

        }

        #endregion

        private void btnCopyLog_Click(object sender, EventArgs e)
        {
            var msgs = _operations.FullLogText;
            if (!string.IsNullOrEmpty(msgs))
            {
                if (MessageBox.Show(this, "The full log can contain account names, resource names, URLs and other sensitive configuration. " +
                    "Review and sanitise it before sharing. Copy the full log to the clipboard?",
                    "Copy full log", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                Clipboard.SetText(msgs);
            }
        }

        private void btnInstall_Click(object sender, EventArgs e)
        {
            Install?.Invoke(this, EventArgs.Empty);
        }

        private void btnRunTests_Click(object sender, EventArgs e)
        {
            TestConfig?.Invoke(this, EventArgs.Empty);
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            // Single-shot: once clicked, disable the button until SetRunningState toggles it back.
            btnCancel.Enabled = false;
            _operations.RequestCancellation();
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        internal void ClearLog()
        {
            _operations.ClearLog();
        }

        public void BeginOperation(string name, bool cancellable)
        {
            _operations.BeginRun(name);
            _reviewOptions.Visible = false;
            grpTasks.Visible = false;
            lblDeploymentNotice.Visible = false;
            lblGUIInstallHeader.Text = "Operation centre";
            lblGUIInstallDesc.Text = "Observed engine phases and messages. No estimated percentage or deployment plan.";
            btnCancel.Visible = cancellable;
        }

        public void CompleteOperation()
        {
            _operations.CompleteRun();
            _reviewOptions.Visible = true;
            lblGUIInstallHeader.Text = "Operation finished";
            lblGUIInstallDesc.Text = "Review errors, warnings and verification results below. Finished does not necessarily mean successful.";
        }

        public class InstallLogLVI : ListViewItem
        {
            public LogLevel Level { get; }
            public string PhaseName { get; }
            public InstallLogLVI(InstallLogEventArgs issue)
            {
                this.Text = issue.Text;
                Level = issue.Level ?? (issue.IsError ? LogLevel.Error : LogLevel.Information);
                PhaseName = issue.PhaseName;

                if (Level >= LogLevel.Warning)
                {
                    this.ImageIndex = 0;
                }
                else
                {
                    this.ImageIndex = 1;
                }
            }
            public InstallLogLVI(Exception ex) : this(new InstallLogEventArgs() { IsError = true, Text = "FATAL: " + ex.Message }) { }
        }
    }
}
