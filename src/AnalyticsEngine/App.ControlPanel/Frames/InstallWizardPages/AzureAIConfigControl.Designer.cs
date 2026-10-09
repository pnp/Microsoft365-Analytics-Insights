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
            this.chkCognitiveEnable = new System.Windows.Forms.CheckBox();
            this.txtCognitiveName = new System.Windows.Forms.TextBox();
            this.lblCognitiveName = new System.Windows.Forms.Label();
            this.chkFoundryEnable = new System.Windows.Forms.CheckBox();
            this.txtFoundryResourceName = new System.Windows.Forms.TextBox();
            this.txtFoundryDeploymentName = new System.Windows.Forms.TextBox();
            this.txtFoundryModelName = new System.Windows.Forms.TextBox();
            this.txtFoundryModelVersion = new System.Windows.Forms.TextBox();
            this.txtFoundryCapacity = new System.Windows.Forms.TextBox();
            this.SuspendLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoScroll = true;
            this.AutoScrollMinSize = new System.Drawing.Size(600, 430);
            AddHeading("Azure AI", 8, 8);
            AddLabel("Optional AI services are provisioned and connected to the runtime automatically.", 8, 36);
            AddServiceIcon("picCognitive", global::App.ControlPanel.Properties.Resources.Cognitive, 74);
            AddHeading("Cognitive Services (Optional)", 64, 78);
            this.chkCognitiveEnable.Name = "chkCognitiveEnable";
            this.chkCognitiveEnable.Text = "Enable cognitive analytics";
            this.chkCognitiveEnable.AutoSize = true;
            this.chkCognitiveEnable.Location = new System.Drawing.Point(352, 82);
            this.chkCognitiveEnable.TabIndex = 0;
            this.Controls.Add(this.chkCognitiveEnable);
            AddField(this.txtCognitiveName, "txtCognitiveName", "Resource name:", 116, 1);
            this.txtCognitiveName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.lblCognitiveName.Name = "lblCognitiveName";
            this.lblCognitiveName.AutoSize = true;
            this.lblCognitiveName.Location = new System.Drawing.Point(156, 143);
            this.Controls.Add(this.lblCognitiveName);
            AddServiceIcon("picFoundry", global::App.ControlPanel.Properties.Resources.AzureOpenAI, 189);
            AddHeading("Azure OpenAI (Optional)", 64, 193);
            this.chkFoundryEnable.Name = "chkFoundryEnable";
            this.chkFoundryEnable.Text = "Enable prompt categorisation service";
            this.chkFoundryEnable.AutoSize = true;
            this.chkFoundryEnable.Location = new System.Drawing.Point(352, 197);
            this.chkFoundryEnable.TabIndex = 2;
            this.Controls.Add(this.chkFoundryEnable);
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Name = "lblFoundryDescription",
                Text = "For prompt categorisation, the installer provisions Azure OpenAI and\r\n" +
                    "connects the runtime automatically.\r\n" +
                    "To categorise prompts, enable 'Copilot AI interaction history' on Targets,\r\n" +
                    "then enable categorisation in Administration > Prompt categories in the portal.",
                Location = new System.Drawing.Point(64, 224), Size = new System.Drawing.Size(535, 60)
            });
            AddField(this.txtFoundryResourceName, "txtFoundryResourceName", "Resource name:", 290, 3);
            this.txtFoundryResourceName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            AddLabel("Endpoint suffix: .openai.azure.com", 352, 293);
            AddField(this.txtFoundryDeploymentName, "txtFoundryDeploymentName", "Deployment:", 320, 4);
            this.txtFoundryDeploymentName.Text = "prompt-categories";
            AddField(this.txtFoundryModelName, "txtFoundryModelName", "Model name:", 350, 6);
            this.txtFoundryModelName.Text = "gpt-4o-mini";
            AddField(this.txtFoundryModelVersion, "txtFoundryModelVersion", "Model version:", 380, 7);
            AddLabel("Capacity:", 352, 323);
            this.txtFoundryCapacity.Name = "txtFoundryCapacity";
            this.txtFoundryCapacity.Location = new System.Drawing.Point(438, 320);
            this.txtFoundryCapacity.Size = new System.Drawing.Size(50, 20);
            this.txtFoundryCapacity.Text = "1";
            this.txtFoundryCapacity.TabIndex = 5;
            this.Controls.Add(this.txtFoundryCapacity);
            this.chkCognitiveEnable.CheckedChanged += new System.EventHandler(this.AISettingsChanged);
            this.txtCognitiveName.TextChanged += new System.EventHandler(this.AISettingsChanged);
            this.chkFoundryEnable.CheckedChanged += new System.EventHandler(this.AISettingsChanged);
            this.Name = "AzureAIConfigControl";
            this.Size = new System.Drawing.Size(632, 537);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void AddLabel(string text, int x, int y)
        {
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Text = text, AutoSize = true, Location = new System.Drawing.Point(x, y)
            });
        }

        private void AddServiceIcon(string name, System.Drawing.Image image, int y)
        {
            this.Controls.Add(new System.Windows.Forms.PictureBox
            {
                Name = name, Image = image,
                Location = new System.Drawing.Point(0, y), Size = new System.Drawing.Size(56, 56),
                SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom, TabStop = false
            });
        }

        private void AddHeading(string text, int x, int y)
        {
            this.Controls.Add(new System.Windows.Forms.Label
            {
                Text = text, AutoSize = true, Location = new System.Drawing.Point(x, y),
                Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold)
            });
        }

        private void AddField(System.Windows.Forms.TextBox field, string name, string label, int y, int tabIndex)
        {
            AddLabel(label, 64, y + 3);
            field.Name = name;
            field.Location = new System.Drawing.Point(156, y);
            field.Size = new System.Drawing.Size(170, 20);
            field.TabIndex = tabIndex;
            this.Controls.Add(field);
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
