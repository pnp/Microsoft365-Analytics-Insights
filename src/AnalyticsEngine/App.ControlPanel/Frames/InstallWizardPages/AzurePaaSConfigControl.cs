using Common.Entities.Installer;
using System.ComponentModel;
using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzurePaaSConfigControl : UserControl
    {
        private bool _healthEmailWasNull;
        private TableLayoutPanel _healthDetails;
        public AzurePaaSConfigControl()
        {
            InitializeComponent();
            InitializeHealthMonitoringHelp();
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

        private void InitializeHealthMonitoringHelp()
        {
            var resources = new ComponentResourceManager(typeof(AzurePaaSConfigControl));
            if (components == null) components = new Container();
            var toolTip = new ToolTip(components)
            {
                AutoPopDelay = 30000,
                ToolTipTitle = resources.GetString("HealthMonitoring.ToolTipTitle"),
                ShowAlways = true
            };
            var thresholdHelp = resources.GetString("HealthMonitoring.ThresholdHelp");
            toolTip.SetToolTip(numHealthMinutes, thresholdHelp);
            toolTip.SetToolTip(lblHealthThresholdHelp, thresholdHelp);
            toolTip.SetToolTip(chkHealthMonitoring, resources.GetString("HealthMonitoring.Coverage"));
            numHealthMinutes.AccessibleName = resources.GetString("HealthMonitoring.Threshold");
            numHealthMinutes.AccessibleDescription = thresholdHelp;

            _healthDetails = new TableLayoutPanel
            {
                Name = "healthMonitoringDetails", AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top,
                ColumnCount = 1, Visible = false, Margin = Padding.Empty
            };
            _healthDetails.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var section in new[] { "Heartbeat", "Coverage", "Limitations", "Costs", "Maintenance", "Delivery" })
            {
                ServicePageLayout.AddRow(_healthDetails,
                    ServicePageLayout.Text(resources.GetString("HealthMonitoring." + section + "Heading"), 9, true));
                ServicePageLayout.AddRow(_healthDetails,
                    ServicePageLayout.Text(resources.GetString("HealthMonitoring." + section)));
            }
            var wiki = new LinkLabel
            {
                Name = "lnkHealthWiki", AutoSize = true,
                Text = resources.GetString("HealthMonitoring.Wiki"),
                Margin = new Padding(0, 4, 0, 4)
            };
            wiki.LinkClicked += HealthWiki_LinkClicked;
            ServicePageLayout.AddRow(_healthDetails, wiki);
            ServicePageLayout.AddRow((TableLayoutPanel)lnkHealthDetails.Parent, _healthDetails);
        }

        private void HealthDetails_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            _healthDetails.Visible = !_healthDetails.Visible;
            var resources = new ComponentResourceManager(typeof(AzurePaaSConfigControl));
            lnkHealthDetails.Text = resources.GetString(_healthDetails.Visible
                ? "HealthMonitoring.HideDetails" : "HealthMonitoring.ShowDetails");
        }

        private void HealthWiki_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            const string url = "https://github.com/pnp/Microsoft365-Analytics-Insights/wiki/Installer-health-monitoring";
            try
            {
                System.Diagnostics.Process.Start(url);
            }
            catch (Exception ex)
            {
                var resources = new ComponentResourceManager(typeof(AzurePaaSConfigControl));
                MessageBox.Show(this, resources.GetString("HealthMonitoring.OpenWikiError") + "\r\n" + url + "\r\n\r\n" + ex.Message,
                    resources.GetString("HealthMonitoring.Wiki"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

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
