using App.ControlPanel.Engine;
using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class DatabaseUpgradeForm : Form
    {
        public DatabaseUpgradeForm()
        {
            InitializeComponent();
        }

        private void backgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            // Test upgrade
            try
            {
                DatabaseUpgrader.CheckDbUpgraded(new Engine.Models.DatabaseUpgradeInfo { ConnectionString = txtConnectionString.Text }, msg => LogEvent(msg));
            }
            catch (Exception ex)
            {
                LogEvent(ex.ToString());
            }
        }

        void LogEvent(string msg)
        {
            if (this.InvokeRequired)
            {
                this.Invoke(new Action(() => LogEvent(msg)));
                return;
            }
            txtLog.AppendText(msg + Environment.NewLine);
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void chkConfirm_CheckedChanged(object sender, EventArgs e)
        {
            UpgradeButtonDisableCheck();
        }

        private void DatabaseUpgradeForm_Load(object sender, EventArgs e)
        {
            txtLog.Text = string.Empty;
            SetLoadingState(false);
        }

        private void DatabaseUpgradeForm_Activated(object sender, EventArgs e)
        {
            // On load is too soon, apparently
            UpgradeButtonDisableCheck();
        }
        private void UpgradeButtonDisableCheck()
        {
            btnUpgrade.Enabled = chkConfirm.Checked;
        }
        void SetLoadingState(bool loading)
        {
            this.btnUpgrade.Enabled = !loading;
            this.btnCancel.Enabled = !loading;
            progressBar.Visible = loading;
        }

        private void btnUpgrade_Click(object sender, EventArgs e)
        {
            txtLog.Text = string.Empty;
            SetLoadingState(true);
            backgroundWorker.RunWorkerAsync();
        }

        private void backgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetLoadingState(false);
        }

    }
}
