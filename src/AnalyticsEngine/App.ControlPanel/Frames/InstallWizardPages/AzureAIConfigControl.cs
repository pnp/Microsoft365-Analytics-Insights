using System;
using System.Windows.Forms;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using CloudInstallEngine.Azure;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzureAIConfigControl : UserControl
    {
        public Func<(string TenantId, string ClientId, string Secret, string SubscriptionId, string Region)> OnNeedModelCredentials { get; set; }
        public AzureAIConfigControl()
        {
            InitializeComponent();
            UpdateResponsiveUIControls();
        }

        private void UpdateResponsiveUIControls()
        {
            txtCognitiveName.Enabled = chkCognitiveEnable.Checked;
            lblCognitiveName.Text = string.IsNullOrWhiteSpace(txtCognitiveName.Text)
                ? "Endpoint suffix: .cognitiveservices.azure.com"
                : $"https://{txtCognitiveName.Text}.cognitiveservices.azure.com";
            txtFoundryResourceName.Enabled = chkFoundryEnable.Checked;
            txtFoundryDeploymentName.Enabled = chkFoundryEnable.Checked;
            txtFoundryModelName.Enabled = chkFoundryEnable.Checked;
            txtFoundryModelVersion.Enabled = chkFoundryEnable.Checked;
            txtFoundryCapacity.Enabled = chkFoundryEnable.Checked;
            cmbFoundryDeploymentType.Enabled = chkFoundryEnable.Checked;
            btnBrowseModels.Enabled = chkFoundryEnable.Checked;
            lblFoundryResidency.Text = OpenAIModelCatalog.ResidencyDescription(FoundryPromptDeploymentType);
        }

        public bool CognitiveEnabled
        {
            get { return chkCognitiveEnable.Checked; }
            set { chkCognitiveEnable.Checked = value; UpdateResponsiveUIControls(); }
        }
        public string CognitiveServiceName { get { return txtCognitiveName.Text; } set { txtCognitiveName.Text = value; } }
        public bool FoundryPromptEnabled
        {
            get { return chkFoundryEnable.Checked; }
            set { chkFoundryEnable.Checked = value; UpdateResponsiveUIControls(); }
        }
        public string FoundryPromptResourceName { get { return txtFoundryResourceName.Text; } set { txtFoundryResourceName.Text = value; } }
        public string FoundryPromptDeploymentName { get { return txtFoundryDeploymentName.Text; } set { txtFoundryDeploymentName.Text = value; } }
        public string FoundryPromptModelName { get { return txtFoundryModelName.Text; } set { txtFoundryModelName.Text = value; } }
        public string FoundryPromptModelVersion { get { return txtFoundryModelVersion.Text; } set { txtFoundryModelVersion.Text = value; } }
        public string FoundryPromptDeploymentType
        {
            get { return cmbFoundryDeploymentType.SelectedItem as string; }
            set
            {
                // Preserve invalid saved values so validation reports them rather than changing residency.
                if (value != null && !cmbFoundryDeploymentType.Items.Contains(value))
                    cmbFoundryDeploymentType.Items.Add(value);
                cmbFoundryDeploymentType.SelectedItem = value;
                UpdateResponsiveUIControls();
            }
        }
        public int FoundryPromptCapacity
        {
            get { return int.TryParse(txtFoundryCapacity.Text, out var capacity) ? capacity : 0; }
            set { txtFoundryCapacity.Text = value.ToString(); }
        }

        private void AISettingsChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }

        private void BrowseModels(object sender, EventArgs e)
        {
            var credentials = OnNeedModelCredentials?.Invoke() ?? default;
            using (var browser = new OpenAIModelBrowserForm(FoundryPromptDeploymentType, FoundryPromptModelName, FoundryPromptModelVersion))
            {
                browser.ContextDescription = "Region: " + (string.IsNullOrWhiteSpace(credentials.Region) ? "(not configured)" : credentials.Region) +
                    ". Uses the selected subscription and installer identity.";
                browser.LoadModelsAsync = async cancellation =>
                {
                    if (string.IsNullOrWhiteSpace(credentials.TenantId) || string.IsNullOrWhiteSpace(credentials.ClientId) ||
                        string.IsNullOrWhiteSpace(credentials.Secret) || string.IsNullOrWhiteSpace(credentials.SubscriptionId) ||
                        string.IsNullOrWhiteSpace(credentials.Region))
                        throw new InvalidOperationException("Set installer credentials, subscription and Azure region first.");
                    var client = new ArmClient(new ClientSecretCredential(credentials.TenantId, credentials.ClientId, credentials.Secret));
                    var subscription = client.GetSubscriptionResource(new ResourceIdentifier("/subscriptions/" + credentials.SubscriptionId));
                    return await OpenAIModelCatalog.LoadAsync(subscription, new AzureLocation(credentials.Region), cancellation);
                };
                if (browser.ShowDialog(this) != DialogResult.OK) return;
                FoundryPromptModelName = browser.SelectedModel.Name;
                FoundryPromptModelVersion = browser.SelectedModel.Version;
                FoundryPromptDeploymentType = browser.SelectedModel.DeploymentType;
                FoundryPromptCapacity = browser.SelectedCapacity;
            }
        }
    }
}
