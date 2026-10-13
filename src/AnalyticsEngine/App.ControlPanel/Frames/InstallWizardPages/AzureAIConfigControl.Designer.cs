namespace App.ControlPanel.Frames.InstallWizard
{
    partial class AzureAIConfigControl
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.chkCognitiveEnable = new System.Windows.Forms.CheckBox
            {
                Name = "chkCognitiveEnable", Text = "Enable cognitive analytics", AutoSize = true
            };
            this.txtCognitiveName = new System.Windows.Forms.TextBox
            {
                Name = "txtCognitiveName", CharacterCasing = System.Windows.Forms.CharacterCasing.Lower
            };
            this.lblCognitiveName = ServicePageLayout.Text("");
            this.lblCognitiveName.Name = "lblCognitiveName";
            this.chkFoundryEnable = new System.Windows.Forms.CheckBox
            {
                Name = "chkFoundryEnable", Text = "Enable prompt categorisation service", AutoSize = true
            };
            this.txtFoundryResourceName = new System.Windows.Forms.TextBox
            {
                Name = "txtFoundryResourceName", CharacterCasing = System.Windows.Forms.CharacterCasing.Lower
            };
            this.txtFoundryDeploymentName = new System.Windows.Forms.TextBox
            {
                Name = "txtFoundryDeploymentName", Text = "prompt-categories"
            };
            this.txtFoundryModelName = new System.Windows.Forms.TextBox { Name = "txtFoundryModelName", Text = "gpt-4o-mini" };
            this.txtFoundryModelVersion = new System.Windows.Forms.TextBox { Name = "txtFoundryModelVersion" };
            this.txtFoundryCapacity = new System.Windows.Forms.TextBox { Name = "txtFoundryCapacity", Text = "1" };
            this.SuspendLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            var layout = ServicePageLayout.Create(this, "Azure AI",
                "Optional AI services are provisioned and connected to the runtime automatically.");
            var cognitive = ServicePageLayout.AddService(layout, "cognitiveService", "Cognitive Services (Optional)",
                "Azure AI Language analyses imported text for language, sentiment and key phrases. " +
                "Enable it only when those cognitive analytics are needed.",
                global::App.ControlPanel.Properties.Resources.Cognitive, iconName: "picCognitive");
            ServicePageLayout.AddRow(cognitive, chkCognitiveEnable);
            ServicePageLayout.AddFields(cognitive, "Resource name:", txtCognitiveName);
            ServicePageLayout.AddRow(cognitive, lblCognitiveName);
            var foundry = ServicePageLayout.AddService(layout, "foundryService", "Azure OpenAI (Optional)",
                "For prompt categorisation, the installer provisions Azure OpenAI and connects the runtime automatically. " +
                "To categorise prompts, enable 'Copilot AI interaction history' on Workloads, " +
                "then enable categorisation in Administration > Prompt categories in the portal.",
                global::App.ControlPanel.Properties.Resources.AzureOpenAI,
                iconName: "picFoundry", descriptionName: "lblFoundryDescription");
            ServicePageLayout.AddRow(foundry, chkFoundryEnable);
            ServicePageLayout.AddFields(foundry, "Resource name:", txtFoundryResourceName);
            ServicePageLayout.AddRow(foundry, ServicePageLayout.Text("Endpoint suffix: .openai.azure.com"));
            ServicePageLayout.AddFields(foundry, "Deployment:", txtFoundryDeploymentName, "Capacity:", txtFoundryCapacity);
            ServicePageLayout.AddFields(foundry, "Model name:", txtFoundryModelName, "Model version:", txtFoundryModelVersion);
            this.chkCognitiveEnable.CheckedChanged += this.AISettingsChanged;
            this.txtCognitiveName.TextChanged += this.AISettingsChanged;
            this.chkFoundryEnable.CheckedChanged += this.AISettingsChanged;
            this.Name = "AzureAIConfigControl";
            this.Size = new System.Drawing.Size(632, 537);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.CheckBox chkCognitiveEnable;
        private System.Windows.Forms.TextBox txtCognitiveName;
        private System.Windows.Forms.Label lblCognitiveName;
        private System.Windows.Forms.CheckBox chkFoundryEnable;
        private System.Windows.Forms.TextBox txtFoundryResourceName;
        private System.Windows.Forms.TextBox txtFoundryDeploymentName;
        private System.Windows.Forms.TextBox txtFoundryModelName;
        private System.Windows.Forms.TextBox txtFoundryModelVersion;
        private System.Windows.Forms.TextBox txtFoundryCapacity;
    }
}
