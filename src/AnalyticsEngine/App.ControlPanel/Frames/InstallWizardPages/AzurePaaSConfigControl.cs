using Common.Entities.Installer;
using System.ComponentModel;
using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzurePaaSConfigControl : UserControl
    {
        private bool _healthEmailWasNull;
        public AzurePaaSConfigControl()
        {
            InitializeComponent();
            UpdateResponsiveUIControls();
            UpdateHealthMonitoringControls();
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
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public HealthMonitoringConfig HealthMonitoring
        {
            get => new HealthMonitoringConfig
            {
                Enabled = chkHealthMonitoring.Checked,
                NotificationEmail = _healthEmailWasNull && txtHealthEmail.Text.Length == 0 ? null : txtHealthEmail.Text,
                MissingHeartbeatMinutes = (int)numHealthMinutes.Value
            };
            set
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                chkHealthMonitoring.Checked = value.Enabled;
                _healthEmailWasNull = value.NotificationEmail == null;
                txtHealthEmail.Text = value.NotificationEmail;
                numHealthMinutes.Value = value.MissingHeartbeatMinutes;
                UpdateHealthMonitoringControls();
            }
        }

        private void HealthMonitoring_CheckedChanged(object sender, EventArgs e) => UpdateHealthMonitoringControls();

        private void UpdateHealthMonitoringControls()
        {
            txtHealthEmail.Enabled = numHealthMinutes.Enabled = chkHealthMonitoring.Checked;
        }
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
