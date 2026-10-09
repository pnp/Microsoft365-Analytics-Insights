using System;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizard
{
    public partial class AzureAIConfigControl : UserControl
    {
        public AzureAIConfigControl()
        {
            InitializeComponent();
            UpdateResponsiveUIControls();
        }

        private void UpdateResponsiveUIControls()
        {
            txtCognitiveName.Enabled = chkCognitiveEnable.Checked;
            lblCognitiveName.Text = $"https://{txtCognitiveName.Text}.cognitiveservices.azure.com";
            txtFoundryResourceName.Enabled = chkFoundryEnable.Checked;
            txtFoundryDeploymentName.Enabled = chkFoundryEnable.Checked;
            txtFoundryModelName.Enabled = chkFoundryEnable.Checked;
            txtFoundryModelVersion.Enabled = chkFoundryEnable.Checked;
            txtFoundryCapacity.Enabled = chkFoundryEnable.Checked;
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
        public int FoundryPromptCapacity
        {
            get { return int.TryParse(txtFoundryCapacity.Text, out var capacity) ? capacity : 0; }
            set { txtFoundryCapacity.Text = value.ToString(); }
        }

        private void AISettingsChanged(object sender, EventArgs e)
        {
            UpdateResponsiveUIControls();
        }
    }
}
