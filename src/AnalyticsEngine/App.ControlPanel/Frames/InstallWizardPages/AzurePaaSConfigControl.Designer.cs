namespace App.ControlPanel.Frames.InstallWizard
{
    partial class AzurePaaSConfigControl
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            var resources = new System.ComponentModel.ComponentResourceManager(typeof(AzurePaaSConfigControl));
            this.txtAppInsightsName = new System.Windows.Forms.TextBox { Name = "txtAppInsightsName" };
            this.txtLogAnalyticsName = new System.Windows.Forms.TextBox { Name = "txtLogAnalyticsName" };
            this.txtAppServiceWebAppName = new System.Windows.Forms.TextBox { Name = "txtAppServiceWebAppName" };
            this.txtAppServicePlanName = new System.Windows.Forms.TextBox { Name = "txtAppServicePlanName" };
            this.txtKeyVaultName = new System.Windows.Forms.TextBox
            {
                Name = "txtKeyVaultName", CharacterCasing = System.Windows.Forms.CharacterCasing.Lower
            };
            this.txtAutomationAccountName = new System.Windows.Forms.TextBox { Name = "txtAutomationAccountName" };
            this.lblAppServiceWebAppName = ServicePageLayout.Text("");
            this.lblAppServiceWebAppName.Name = "lblAppServiceWebAppName";
            this.lblKVName = ServicePageLayout.Text("");
            this.lblKVName.Name = "lblKVName";
            this.chkHealthMonitoring = new System.Windows.Forms.CheckBox
            {
                Name = "chkHealthMonitoring", AutoSize = true,
                Text = resources.GetString("HealthMonitoring.Enabled")
            };
            this.txtHealthEmail = new System.Windows.Forms.TextBox { Name = "txtHealthEmail" };
            this.numHealthMinutes = new System.Windows.Forms.NumericUpDown
            {
                Name = "numHealthMinutes", Minimum = int.MinValue, Maximum = int.MaxValue, Value = 15
            };
            ((System.ComponentModel.ISupportInitialize)this.numHealthMinutes).BeginInit();
            this.SuspendLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            var layout = ServicePageLayout.Create(this, "Hosting & services",
                "Resources are created automatically, or reused if they already exist.");
            var insights = ServicePageLayout.AddService(layout, "insightsService", "Application Insights & Log Analytics",
                "Application Insights collects runtime diagnostics. Log Analytics stores that telemetry for queries and retention.",
                global::App.ControlPanel.Properties.Resources.AppInsights);
            ServicePageLayout.AddFields(insights, "Instance name:", txtAppInsightsName, "Workspace:", txtLogAnalyticsName);
            ServicePageLayout.AddRow(insights, chkHealthMonitoring);
            ServicePageLayout.AddFields(insights, resources.GetString("HealthMonitoring.Email"), txtHealthEmail);
            ServicePageLayout.AddFields(insights, resources.GetString("HealthMonitoring.Threshold"), numHealthMinutes);
            ServicePageLayout.AddRow(insights, ServicePageLayout.Text(resources.GetString("HealthMonitoring.Notice")));
            var hosting = ServicePageLayout.AddService(layout, "hostingService", "App Service & hosting plan",
                "The web app hosts the portal and importers. Its plan supplies compute capacity and determines hosting cost.",
                global::App.ControlPanel.Properties.Resources.AppService);
            ServicePageLayout.AddFields(hosting, "Web app name:", txtAppServiceWebAppName, "Plan name:", txtAppServicePlanName);
            ServicePageLayout.AddRow(hosting, lblAppServiceWebAppName);
            var vault = ServicePageLayout.AddService(layout, "vaultService", "Key Vault",
                "Stores runtime and deployment secrets, with controlled access for the solution's applications and installer.",
                global::App.ControlPanel.Properties.Resources.keyvault);
            ServicePageLayout.AddFields(vault, "Vault name:", txtKeyVaultName);
            ServicePageLayout.AddRow(vault, lblKVName);
            var automation = ServicePageLayout.AddService(layout, "automationService", "Automation account",
                "Runs scheduled Graph usage-report maintenance runbooks against the analytics database.",
                global::App.ControlPanel.Properties.Resources.automation);
            ServicePageLayout.AddFields(automation, "Account name:", txtAutomationAccountName);
            this.txtAppServiceWebAppName.TextChanged += this.txtAppServiceWebAppName_TextChanged;
            this.txtKeyVaultName.TextChanged += this.txtKeyVaultName_TextChanged;
            this.chkHealthMonitoring.CheckedChanged += this.HealthMonitoring_CheckedChanged;
            this.Name = "AzurePaaSConfigControl";
            this.Size = new System.Drawing.Size(632, 537);
            ((System.ComponentModel.ISupportInitialize)this.numHealthMinutes).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private System.Windows.Forms.TextBox txtAppInsightsName;
        private System.Windows.Forms.TextBox txtLogAnalyticsName;
        private System.Windows.Forms.TextBox txtAppServiceWebAppName;
        private System.Windows.Forms.TextBox txtAppServicePlanName;
        private System.Windows.Forms.TextBox txtKeyVaultName;
        private System.Windows.Forms.TextBox txtAutomationAccountName;
        private System.Windows.Forms.Label lblAppServiceWebAppName;
        private System.Windows.Forms.Label lblKVName;
        private System.Windows.Forms.CheckBox chkHealthMonitoring;
        private System.Windows.Forms.TextBox txtHealthEmail;
        private System.Windows.Forms.NumericUpDown numHealthMinutes;
    }
}
