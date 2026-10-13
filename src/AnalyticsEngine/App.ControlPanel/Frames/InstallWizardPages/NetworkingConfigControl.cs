using Common.Entities.Installer;
using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class NetworkingConfigControl : UserControl
    {
        public NetworkingConfigControl()
        {
            InitializeComponent();
            ConfigureLayout();
        }

        private void ConfigureLayout()
        {
            Font = new System.Drawing.Font("Segoe UI", 9F);
            AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblHeader.Font = new System.Drawing.Font("Segoe UI", 16F, System.Drawing.FontStyle.Bold);
            foreach (var control in new Control[] { lblHeader, lblDescription, chkEnableVNet })
            {
                control.AutoSize = true;
                control.Dock = DockStyle.Fill;
                ServicePageLayout.AddRow(layout, control);
            }
            var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var subnets = ServicePageLayout.AddFieldGrid(settings);
            ServicePageLayout.AddFieldGridRow(subnets, lblVNetName.Text, txtVNetName, lblAddressPrefix.Text, txtAddressPrefix);
            ServicePageLayout.AddFieldGridRow(subnets, lblSubnetName.Text, txtSubnetName, lblSubnetAddressPrefix.Text, txtSubnetAddressPrefix);
            ServicePageLayout.AddFieldGridRow(subnets, lblAppSubnetName.Text, txtAppSubnetName, lblAppSubnetAddressPrefix.Text, txtAppSubnetAddressPrefix);
            foreach (var control in new Control[] { chkDeployDnsZones, lblDnsZonesHelp, chkAllowPublicAccess, lblAllowPublicAccessHelp })
            {
                control.AutoSize = true;
                control.Dock = DockStyle.Fill;
                ServicePageLayout.AddRow(settings, control);
            }
            var endpoints = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(8) };
            endpoints.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblPeHelp.AutoSize = true;
            lblPeHelp.Dock = DockStyle.Fill;
            ServicePageLayout.AddRow(endpoints, lblPeHelp);
            var names = ServicePageLayout.AddFieldGrid(endpoints);
            ServicePageLayout.AddFieldGridRow(names, lblPeSql.Text, txtPeSql, lblPeApp.Text, txtPeApp);
            ServicePageLayout.AddFieldGridRow(names, lblPeStorage.Text, txtPeStorage, lblPeStorageTable.Text, txtPeStorageTable);
            ServicePageLayout.AddFieldGridRow(names, lblPeKeyVault.Text, txtPeKeyVault, lblPeServiceBus.Text, txtPeServiceBus);
            ServicePageLayout.AddFieldGridRow(names, lblPeCognitive.Text, txtPeCognitive, lblPeAutomation.Text, txtPeAutomation);
            grpEndpointNames.Controls.Clear();
            grpEndpointNames.AutoSize = true;
            grpEndpointNames.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grpEndpointNames.Dock = DockStyle.Fill;
            grpEndpointNames.Controls.Add(endpoints);
            ServicePageLayout.AddRow(settings, grpEndpointNames);
            ServicePageLayout.AddFields(settings, lblHybridWorkerVm.Text, txtHybridWorkerVm);
            btnBrowseVm.AutoSize = true;
            btnBrowseVm.Anchor = AnchorStyles.Left;
            ServicePageLayout.AddRow(settings, btnBrowseVm);
            foreach (var label in new[] { lblHybridWorkerVmHelp, lblSkuWarning })
            {
                label.AutoSize = true;
                label.Dock = DockStyle.Fill;
                ServicePageLayout.AddRow(settings, label);
            }
            grpVNetSettings.Controls.Clear();
            grpVNetSettings.AutoSize = true;
            grpVNetSettings.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grpVNetSettings.Dock = DockStyle.Fill;
            grpVNetSettings.Controls.Add(settings);
            ServicePageLayout.AddRow(layout, grpVNetSettings);
            Controls.Add(layout);
            layout.BringToFront();
        }

        /// <summary>
        /// Callback to retrieve current Azure credentials from the parent form.
        /// Returns (tenantId, clientId, secret, subscriptionId, resourceGroupName).
        /// </summary>
        public Func<(string TenantId, string ClientId, string Secret, string SubscriptionId, string ResourceGroupName)> OnNeedAzureCredentials;

        public bool VNetEnabled
        {
            get { return chkEnableVNet.Checked; }
            set
            {
                chkEnableVNet.Checked = value;
                UpdateResponsiveUIControls();
            }
        }

        public string VNetName { get { return txtVNetName.Text; } set { txtVNetName.Text = value; } }
        public string SubnetName { get { return txtSubnetName.Text; } set { txtSubnetName.Text = value; } }
        public string AddressPrefix { get { return txtAddressPrefix.Text; } set { txtAddressPrefix.Text = value; } }
        public string SubnetAddressPrefix { get { return txtSubnetAddressPrefix.Text; } set { txtSubnetAddressPrefix.Text = value; } }
        public string AppServiceSubnetName { get { return txtAppSubnetName.Text; } set { txtAppSubnetName.Text = value; } }
        public string AppServiceSubnetAddressPrefix { get { return txtAppSubnetAddressPrefix.Text; } set { txtAppSubnetAddressPrefix.Text = value; } }
        public bool DeployDnsZones { get { return chkDeployDnsZones.Checked; } set { chkDeployDnsZones.Checked = value; } }
        public bool AllowPublicAccess { get { return chkAllowPublicAccess.Checked; } set { chkAllowPublicAccess.Checked = value; } }
        public string HybridWorkerVmResourceId { get { return txtHybridWorkerVm.Text; } set { txtHybridWorkerVm.Text = value; } }

        public PrivateEndpointNames GetEndpointNames()
        {
            return new PrivateEndpointNames
            {
                SqlServer = txtPeSql.Text.Trim(),
                AppService = txtPeApp.Text.Trim(),
                Storage = txtPeStorage.Text.Trim(),
                StorageTable = txtPeStorageTable.Text.Trim(),
                KeyVault = txtPeKeyVault.Text.Trim(),
                ServiceBus = txtPeServiceBus.Text.Trim(),
                CognitiveServices = txtPeCognitive.Text.Trim(),
                AutomationAccount = txtPeAutomation.Text.Trim()
            };
        }

        public void SetEndpointNames(PrivateEndpointNames names)
        {
            if (names == null) return;
            txtPeSql.Text = names.SqlServer ?? string.Empty;
            txtPeApp.Text = names.AppService ?? string.Empty;
            txtPeStorage.Text = names.Storage ?? string.Empty;
            txtPeStorageTable.Text = names.StorageTable ?? string.Empty;
            txtPeKeyVault.Text = names.KeyVault ?? string.Empty;
            txtPeServiceBus.Text = names.ServiceBus ?? string.Empty;
            txtPeCognitive.Text = names.CognitiveServices ?? string.Empty;
            txtPeAutomation.Text = names.AutomationAccount ?? string.Empty;
        }

        private void UpdateResponsiveUIControls()
        {
            grpVNetSettings.Enabled = chkEnableVNet.Checked;
        }

        private void chkEnableVNet_CheckedChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void btnBrowseVm_Click(object sender, EventArgs e)
        {
            using (var form = new VmLookupForm())
            {
                // Try to populate credentials from the parent form
                if (OnNeedAzureCredentials != null)
                {
                    try
                    {
                        var creds = OnNeedAzureCredentials();
                        form.TenantId = creds.TenantId ?? string.Empty;
                        form.ClientId = creds.ClientId ?? string.Empty;
                        form.ClientSecret = creds.Secret ?? string.Empty;
                        form.SubscriptionId = creds.SubscriptionId ?? string.Empty;
                        form.ResourceGroupName = creds.ResourceGroupName ?? string.Empty;
                    }
                    catch
                    {
                        // Credentials not available; user can still enter manually
                    }
                }

                if (form.ShowDialog(this.ParentForm) == DialogResult.OK)
                {
                    txtHybridWorkerVm.Text = form.VmResourceId;
                }
            }
        }
    }
}
