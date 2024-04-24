using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzurePaaSConfigControl : UserControl
    {
        public AzurePaaSConfigControl()
        {
            InitializeComponent();
        }
        private void UpdateResponsiveUIControls()
        {
            txtCognitiveName.Enabled = chkCognitiveEnable.Checked;
            lblAppServiceWebAppName.Text = $"https://{txtAppServiceWebAppName.Text}.azurewebsites.net";
            lblCognitiveName.Text = $"https://{txtCognitiveName.Text}.cognitiveservices.azure.com";
            lblKVName.Text = $"https://{txtKeyVaultName.Text}.vault.azure.net";
        }

        public string AppInsightsName { get { return txtAppInsightsName.Text; } set { txtAppInsightsName.Text = value; } }
        public string AppServicePlanName { get { return txtAppServicePlanName.Text; } set { txtAppServicePlanName.Text = value; } }
        public string AppServiceWebAppName { get { return txtAppServiceWebAppName.Text; } set { txtAppServiceWebAppName.Text = value; } }
        public bool CognitiveEnabled
        {
            get { return chkCognitiveEnable.Checked; }
            set
            {
                chkCognitiveEnable.Checked = value;
                UpdateResponsiveUIControls();
            }
        }
        public string CognitiveServiceName { get { return txtCognitiveName.Text; } set { txtCognitiveName.Text = value; } }
        public string AppInsightsWorkspaceName { get { return txtLogAnalyticsName.Text; } set { txtLogAnalyticsName.Text = value; } }
        public string KeyVaultName { get { return txtKeyVaultName.Text; } set { txtKeyVaultName.Text = value; } }
        public string AutomationAccountName { get { return txtAutomationAccountName.Text; } set { txtAutomationAccountName.Text = value; } }

        private void txtCognitiveName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtKeyVaultName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void txtAppServiceWebAppName_TextChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void chkCognitiveEnable_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }
    }
}
