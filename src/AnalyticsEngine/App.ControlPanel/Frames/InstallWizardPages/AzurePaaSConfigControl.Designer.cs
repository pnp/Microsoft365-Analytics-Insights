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
            this.txtAppInsightsName = new System.Windows.Forms.TextBox();
            this.txtLogAnalyticsName = new System.Windows.Forms.TextBox();
            this.txtAppServiceWebAppName = new System.Windows.Forms.TextBox();
            this.txtAppServicePlanName = new System.Windows.Forms.TextBox();
            this.txtKeyVaultName = new System.Windows.Forms.TextBox();
            this.txtAutomationAccountName = new System.Windows.Forms.TextBox();
            this.lblAppServiceWebAppName = new System.Windows.Forms.Label();
            this.lblKVName = new System.Windows.Forms.Label();
            this.SuspendLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoScroll = true;
            this.AutoScrollMinSize = new System.Drawing.Size(600, 390);
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Name = "lblGUIAzureHeader", Text = "Azure PaaS Resources", AutoSize = true,
                Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold),
                Location = new System.Drawing.Point(8, 8)
            });
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Name = "lblGUIAzureHeaderDesc", Text = "Resources are created automatically, or reused if they already exist.",
                AutoSize = true, Location = new System.Drawing.Point(8, 34)
            });
            AddServiceHeader("Application Insights", 62, global::App.ControlPanel.Properties.Resources.AppInsights);
            AddField(this.txtAppInsightsName, "txtAppInsightsName", "Instance name:", 90, 0);
            AddField(this.txtLogAnalyticsName, "txtLogAnalyticsName", "Log Analytics:", 90, 1);
            AddServiceHeader("App Service", 147, global::App.ControlPanel.Properties.Resources.AppService);
            AddField(this.txtAppServiceWebAppName, "txtAppServiceWebAppName", "Name:", 175, 0);
            AddField(this.txtAppServicePlanName, "txtAppServicePlanName", "Plan name:", 175, 1);
            this.txtAppServiceWebAppName.TextChanged += new System.EventHandler(this.txtAppServiceWebAppName_TextChanged);
            this.lblAppServiceWebAppName.Name = "lblAppServiceWebAppName";
            this.lblAppServiceWebAppName.AutoSize = true;
            this.lblAppServiceWebAppName.Location = new System.Drawing.Point(156, 199);
            this.Controls.Add(this.lblAppServiceWebAppName);
            AddServiceHeader("Key Vault", 232, global::App.ControlPanel.Properties.Resources.keyvault);
            AddField(this.txtKeyVaultName, "txtKeyVaultName", "Name:", 260, 0);
            this.txtKeyVaultName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtKeyVaultName.TextChanged += new System.EventHandler(this.txtKeyVaultName_TextChanged);
            this.lblKVName.Name = "lblKVName";
            this.lblKVName.AutoSize = true;
            this.lblKVName.Location = new System.Drawing.Point(156, 284);
            this.Controls.Add(this.lblKVName);
            AddServiceHeader("Automation Account", 317, global::App.ControlPanel.Properties.Resources.automation);
            AddField(this.txtAutomationAccountName, "txtAutomationAccountName", "Name:", 345, 0);
            this.Name = "AzurePaaSConfigControl";
            this.Size = new System.Drawing.Size(632, 537);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void AddServiceHeader(string text, int y, System.Drawing.Image image)
        {
            this.Controls.Add(new System.Windows.Forms.PictureBox
            {
                Image = image, Location = new System.Drawing.Point(0, y), Size = new System.Drawing.Size(56, 56), TabStop = false
            });
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Text = text, AutoSize = true, Location = new System.Drawing.Point(62, y),
                Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold)
            });
        }

        private void AddField(System.Windows.Forms.TextBox field, string name, string label, int y, int column)
        {
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Text = label, AutoSize = true, Location = new System.Drawing.Point(column == 0 ? 63 : 352, y + 3)
            });
            field.Name = name;
            field.Location = new System.Drawing.Point(column == 0 ? 156 : 438, y);
            field.Size = new System.Drawing.Size(150, 20);
            field.TabIndex = this.Controls.Count;
            this.Controls.Add(field);
        }

        private System.Windows.Forms.TextBox txtAppInsightsName;
        private System.Windows.Forms.TextBox txtLogAnalyticsName;
        private System.Windows.Forms.TextBox txtAppServiceWebAppName;
        private System.Windows.Forms.TextBox txtAppServicePlanName;
        private System.Windows.Forms.TextBox txtKeyVaultName;
        private System.Windows.Forms.TextBox txtAutomationAccountName;
        private System.Windows.Forms.Label lblAppServiceWebAppName;
        private System.Windows.Forms.Label lblKVName;
    }
}
