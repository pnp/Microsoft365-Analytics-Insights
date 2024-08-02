using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using System;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class TestSettingsForm : Form
    {
        private InMemoryLogger _logger = new InMemoryLogger();
        public TestSettingsForm()
        {
            InitializeComponent();
        }


        void SetLoadLoading(bool loading)
        {
            this.Cursor = loading ? Cursors.WaitCursor : Cursors.Default;
            pnlAll.Enabled = !loading;
        }

        /// <summary>
        /// Set UI or get from UI
        /// </summary>
        public TestConfiguration TestConfiguration
        {
            get
            {
                var connectionString = string.Empty;
                if (txtSqlServer.Text.Length > 0 && txtSqlUsername.Text.Length > 0 && txtSqlPassword.Text.Length > 0)
                {
                    connectionString = DatabasePaaSInfo.GetConnectionString(txtSqlServer.Text, null, txtSqlUsername.Text, txtSqlPassword.Text);
                }
                return new TestConfiguration()
                {
                    FtpHostname = txtFtpServer.Text,
                    FtpPassword = txtFtpPassword.Text,
                    FtpUsername = txtFtpUsername.Text,
                    SQLConnectionString = connectionString
                };
            }
            set
            {
                var defaultConfig = new TestConfiguration();
                if (value != null) defaultConfig = value;

                var sqlConnectionInfo = new System.Data.SqlClient.SqlConnectionStringBuilder(defaultConfig.SQLConnectionString);
                txtSqlServer.Text = sqlConnectionInfo?.DataSource;
                txtFtpServer.Text = defaultConfig.FtpHostname;
                txtFtpUsername.Text = defaultConfig.FtpUsername;
                txtSqlUsername.Text = sqlConnectionInfo?.UserID;
                txtSqlPassword.Text = sqlConnectionInfo?.Password;
                txtFtpPassword.Text = defaultConfig.FtpPassword;
            }
        }
        public SolutionInstallConfig SolutionInstallConfig { get; set; }
        public InstallerFtpConfig FtpConfig { get; internal set; }

        private void btnAutoDetect_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (SolutionInstallVerifier.ConfigIsReadyForFtpAndSqlAutodetection(SolutionInstallConfig))
            {
                backgroundWorkerAutoDetectFTP.RunWorkerAsync();
                SetLoadLoading(true);
            }
            else
            {
                MessageBox.Show("The entered configuration in the main form is not valid to detect your FTP/SQL details. " +
                    "You need at least: an installer account, resource-group, subscription ID, SQL details, and app-service",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
        }

        private void backgroundWorkerAutoDetectFTP_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            var j = new SolutionInstallVerifier(SolutionInstallConfig, _logger, FtpConfig, this.TestConfiguration);

            try
            {
                var ftpProfile = j.GetFtpAndSQLDetails(SolutionInstallConfig.SQLServerAdminPassword).Result;
                e.Result = ftpProfile;
            }
            catch (Exception ex)
            {
                e.Result = ex;
            }
        }

        private void backgroundWorkerAutoDetectFTP_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            SetLoadLoading(false);
            if (e.Result is AutodetectedSqlAndFtpDetails)
            {
                var r = (AutodetectedSqlAndFtpDetails)e.Result;
                var connectionString = string.Empty;
                if (!string.IsNullOrEmpty(r.Sql?.SqlUsername) && !string.IsNullOrEmpty(r.Sql?.SqlPassword) && !string.IsNullOrEmpty(r.Sql?.SqlFqdn))
                {
                    connectionString = DatabasePaaSInfo.GetConnectionString(r.Sql?.SqlFqdn, null, r.Sql?.SqlUsername, r.Sql?.SqlPassword);
                }

                this.TestConfiguration = new TestConfiguration
                {
                    FtpHostname = r.Ftp?.Domain,
                    FtpPassword = r.Ftp?.Password,
                    FtpUsername = r.Ftp?.Username,
                    SQLConnectionString = connectionString
                };

                if (r.Sql == null || r.Ftp == null)
                {
                    MessageBox.Show($"Couldn't get some/all of your Azure resources data: {_logger.GetMessages()}",
                        "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
                else
                {
                    MessageBox.Show($"Your resources auto-detected succesfully. Save the detected configuration to use them for solution tests",
                        "Autodetect Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            else if (e.Result is Exception)
            {
                MessageBox.Show($"Couldn't get some/all of your Azure resources data: {((Exception)e.Result).Message}",
                        "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            else
                MessageBox.Show($"Unexpected error getting FTP details: {_logger.GetMessages()}",
                    "Can't Autodetect", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
        }

        private void btnOk_Click(object sender, EventArgs e)
        {
            if (this.TestConfiguration.IsValid)
            {
                this.DialogResult = DialogResult.OK;
            }
            else
            {
                MessageBox.Show("The entered configuration is not valid to run connectivity tests with", "Validation Failure", MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.DialogResult = DialogResult.Cancel;
            }
        }
        private void btnClose_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
        }
    }
}
