using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzurePaaSConfigControl : UserControl
    {
        public AzurePaaSConfigControl()
        {
            InitializeComponent();
            UpdateResponsiveUIControls();
        }
        private void UpdateResponsiveUIControls()
        {
            lblAppServiceWebAppName.Text = $"https://{txtAppServiceWebAppName.Text}.azurewebsites.net";
            lblKVName.Text = $"https://{txtKeyVaultName.Text}.vault.azure.net";
        }

        public string AppInsightsName { get { return txtAppInsightsName.Text; } set { txtAppInsightsName.Text = value; } }
        public string AppServicePlanName { get { return txtAppServicePlanName.Text; } set { txtAppServicePlanName.Text = value; } }
        public string AppServiceWebAppName { get { return txtAppServiceWebAppName.Text; } set { txtAppServiceWebAppName.Text = value; } }
        public string AppInsightsWorkspaceName { get { return txtLogAnalyticsName.Text; } set { txtLogAnalyticsName.Text = value; } }
        public string KeyVaultName { get { return txtKeyVaultName.Text; } set { txtKeyVaultName.Text = value; } }
        public string AutomationAccountName { get { return txtAutomationAccountName.Text; } set { txtAutomationAccountName.Text = value; } }
        private void txtKeyVaultName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtAppServiceWebAppName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

    }
}
