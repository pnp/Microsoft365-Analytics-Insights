using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class InstallSolutionControl : UserControl
    {
        public InstallSolutionControl()
        {
            InitializeComponent();
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
                // Add item to log & scroll
                lstLog.Items.Add(installLogLVI);
                lstLog.Items[lstLog.Items.Count - 1].EnsureVisible();
                lstLog.Columns[0].Width = -2;

                if (fatalError)
                {
                    MessageBox.Show(installLogLVI.Text, "Unexpected Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

        }

        #endregion

        private void btnCopyLog_Click(object sender, EventArgs e)
        {
            string msgs = string.Empty;
            foreach (ListViewItem li in lstLog.Items)
            {
                msgs += li.Text + Environment.NewLine;
            }
            msgs = msgs.TrimEnd(Environment.NewLine.ToCharArray());
            if (!string.IsNullOrEmpty(msgs))
            {
                Clipboard.SetText(msgs);

                MessageBox.Show("Output copied to clipboard!", "Done", MessageBoxButtons.OK, MessageBoxIcon.Information);
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

        internal void ClearLog()
        {
            lstLog.Items.Clear();
        }

        public class InstallLogLVI : ListViewItem
        {
            public InstallLogLVI(InstallLogEventArgs issue)
            {
                this.Text = issue.Text;

                if (issue.IsError)
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
