using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzureStorageConfigControl : UserControl
    {
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

        private void UpdateResponsiveUIControls()
        {
            lblStorageAccountURL.Text = $"https://{txtStorageAccount.Text}.blob.core.windows.net/";
            lblRedisName.Text = $"{txtRedisName.Text}.redis.cache.windows.net";
            lblServiceBusName.Text = $"{txtServiceBusName.Text}.servicebus.windows.net";
            lblSQLServerName.Text = $"{txtSQLServerName.Text}.database.windows.net";
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
    }
}
