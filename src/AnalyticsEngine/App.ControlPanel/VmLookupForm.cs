using Azure.Identity;
using Azure.ResourceManager;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace App.ControlPanel
{
    /// <summary>
    /// Dialog to look up a VM by resource group and name using Azure credentials.
    /// </summary>
    public partial class VmLookupForm : Form
    {
        public VmLookupForm()
        {
            InitializeComponent();
        }

        /// <summary>
        /// The selected VM Resource ID result.
        /// </summary>
        public string VmResourceId { get; private set; } = string.Empty;

        /// <summary>
        /// Set before showing the dialog to pre-populate the subscription ID.
        /// </summary>
        public string SubscriptionId { get; set; } = string.Empty;

        /// <summary>
        /// Set before showing the dialog to pre-populate the resource group.
        /// </summary>
        public string ResourceGroupName { get; set; } = string.Empty;

        /// <summary>
        /// Set before showing the dialog to provide Azure credentials for lookup.
        /// </summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>
        /// Set before showing the dialog to provide Azure credentials for lookup.
        /// </summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>
        /// Set before showing the dialog to provide Azure credentials for lookup.
        /// </summary>
        public string ClientSecret { get; set; } = string.Empty;

        private void VmLookupForm_Load(object sender, EventArgs e)
        {
            txtResourceGroup.Text = ResourceGroupName;
            cmbVmName.Items.Clear();
            UpdateUIState();
        }

        private void UpdateUIState()
        {
            bool hasLookupResult = cmbVmName.Items.Count > 0;
            btnOkLookup.Enabled = hasLookupResult && cmbVmName.SelectedItem != null;
        }

        private async void btnLookup_Click(object sender, EventArgs e)
        {
            var resourceGroup = txtResourceGroup.Text.Trim();
            if (string.IsNullOrWhiteSpace(resourceGroup))
            {
                MessageBox.Show("Enter a resource group name.", "Missing Input", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnLookup.Enabled = false;
            lblStatus.Text = "Looking up VMs...";
            cmbVmName.Items.Clear();
            Cursor = Cursors.WaitCursor;

            try
            {
                var vms = await Task.Run(() => LookupVms(resourceGroup));

                if (vms == null || vms.Length == 0)
                {
                    lblStatus.Text = "No VMs found in that resource group.";
                    lblStatus.ForeColor = System.Drawing.Color.DarkOrange;
                }
                else
                {
                    foreach (var vm in vms)
                    {
                        cmbVmName.Items.Add(vm);
                    }
                    cmbVmName.SelectedIndex = 0;
                    lblStatus.Text = $"Found {vms.Length} VM(s).";
                    lblStatus.ForeColor = System.Drawing.Color.Green;
                }
            }
            catch (Exception ex)
            {
                lblStatus.Text = $"Lookup failed: {ex.Message}";
                lblStatus.ForeColor = System.Drawing.Color.Red;
            }
            finally
            {
                btnLookup.Enabled = true;
                Cursor = Cursors.Default;
                UpdateUIState();
            }
        }

        private VmInfo[] LookupVms(string resourceGroup)
        {
            if (string.IsNullOrWhiteSpace(TenantId) || string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
            {
                throw new InvalidOperationException("Azure credentials (tenant ID, client ID, secret) are not configured. Enter the VM Resource ID manually below.");
            }

            if (string.IsNullOrWhiteSpace(SubscriptionId))
            {
                throw new InvalidOperationException("Subscription ID is not configured. Enter the VM Resource ID manually below.");
            }

            var credential = new ClientSecretCredential(TenantId, ClientId, ClientSecret);
            var client = new ArmClient(credential);
            var subscription = client.GetSubscriptions().FirstOrDefault(s => s.Data.SubscriptionId == SubscriptionId);
            if (subscription == null)
            {
                throw new InvalidOperationException($"Cannot find subscription '{SubscriptionId}'. Check credentials have access.");
            }

            var rg = subscription.GetResourceGroups().FirstOrDefault(r => string.Equals(r.Data.Name, resourceGroup, StringComparison.OrdinalIgnoreCase));
            if (rg == null)
            {
                throw new InvalidOperationException($"Resource group '{resourceGroup}' not found in subscription.");
            }

            // List VMs using generic resource filter
            var vmType = new Azure.Core.ResourceType("Microsoft.Compute/virtualMachines");
            var resources = rg.GetGenericResources(filter: $"resourceType eq 'Microsoft.Compute/virtualMachines'");

            return resources
                .Select(r => new VmInfo { Name = r.Data.Name, ResourceId = r.Data.Id.ToString() })
                .OrderBy(v => v.Name)
                .ToArray();
        }

        private void btnOkLookup_Click(object sender, EventArgs e)
        {
            if (cmbVmName.SelectedItem is VmInfo vm)
            {
                VmResourceId = vm.ResourceId;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }

        private void cmbVmName_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateUIState();
        }

        private class VmInfo
        {
            public string Name { get; set; }
            public string ResourceId { get; set; }
            public override string ToString() => Name;
        }
    }
}
