using App.ControlPanel.Engine;
using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzureStorageConfigControl : UserControl
    {
        private string _azureRegion;

        public AzureStorageConfigControl()
        {
            InitializeComponent();
        }

        public string SQLDb { get { return txtSQLDb.Text; } set { txtSQLDb.Text = value; } }
        public string SQLServerName { get { return txtSQLServerName.Text; } set { txtSQLServerName.Text = value; } }
        public string SQLServerPassword { get { return txtSQLServerPassword.Text; } set { txtSQLServerPassword.Text = value; } }
        public string SQLServerUsername { get { return txtSQLServerUsername.Text; } set { txtSQLServerUsername.Text = value; } }
        public string StorageAccount { get { return txtStorageAccount.Text; } set { txtStorageAccount.Text = value; } }
        public string RedisName { get { return txtRedisName.Text; } set { txtRedisName.Text = value; } }
        public string ServiceBusName { get { return txtServiceBusName.Text; } set { txtServiceBusName.Text = value; } }

        /// <summary>
        /// Azure region selected on the base-config page. Azure Managed Redis hostnames are region-qualified
        /// (<c>&lt;name&gt;.&lt;region&gt;.redis.azure.net</c>), so the preview label cannot be built without it.
        /// </summary>
        public string AzureRegion
        {
            get { return _azureRegion; }
            set
            {
                _azureRegion = value;
                UpdateResponsiveUIControls();
            }
        }

        public bool ServiceBusEnabled
        {
            get { return chkServiceBusEnabled.Checked; }
            set
            {
                chkServiceBusEnabled.Checked = value;
                UpdateResponsiveUIControls();
            }
        }

        private void UpdateResponsiveUIControls()
        {
            lblStorageAccountURL.Text = $"https://{txtStorageAccount.Text}.blob.core.windows.net/";
            lblRedisName.Text = BuildRedisHostnamePreview();
            lblServiceBusName.Text = $"{txtServiceBusName.Text}.servicebus.windows.net";
            lblSQLServerName.Text = $"{txtSQLServerName.Text}.database.windows.net";

            // Disable SB name fields when Service Bus is disabled
            txtServiceBusName.Enabled = chkServiceBusEnabled.Checked;
            lblServiceBusName.Enabled = chkServiceBusEnabled.Checked;
        }

        /// <summary>
        /// The installer provisions Azure Managed Redis, whose FQDN is region-qualified. Showing the legacy
        /// classic '.redis.cache.windows.net' name here misled admins configuring firewalls and private DNS
        /// (issue #325). Reuses the verifier's hostname logic so the preview and the Test Configuration DNS
        /// check can never drift apart.
        /// </summary>
        private string BuildRedisHostnamePreview()
        {
            var target = SolutionInstallVerifier.BuildRedisDnsTarget(txtRedisName.Text, _azureRegion);
            if (target == null) return string.Empty;

            // No region picked yet: BuildRedisDnsTarget can only offer the legacy classic name, which is not
            // what a new install deploys. Say so rather than showing a hostname that will not exist.
            if (target.Fqdns.Count == 1)
            {
                return $"{txtRedisName.Text.Trim()}.<region>.redis.azure.net (select an Azure region to preview)";
            }

            return target.Fqdn;
        }

        private void txtStorageAccount_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtRedisName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtServiceBusName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtSQLServerName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void chkServiceBusEnabled_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }
    }
}
