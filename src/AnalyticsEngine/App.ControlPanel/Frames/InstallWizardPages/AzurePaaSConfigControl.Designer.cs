namespace App.ControlPanel.Frames.InstallWizard
{
    partial class AzurePaaSConfigControl
    {
        /// <summary> 
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary> 
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Component Designer generated code

        /// <summary> 
        /// Required method for Designer support - do not modify 
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(AzurePaaSConfigControl));
            this.lblKVName = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderKeyVault = new System.Windows.Forms.Label();
            this.txtKeyVaultName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureKeyVaultDesc = new System.Windows.Forms.Label();
            this.txtLogAnalyticsName = new System.Windows.Forms.TextBox();
            this.label1 = new System.Windows.Forms.Label();
            this.lblCognitiveName = new System.Windows.Forms.Label();
            this.lblAppServiceWebAppName = new System.Windows.Forms.Label();
            this.chkCognitiveEnable = new System.Windows.Forms.CheckBox();
            this.lblGUIAzureHeaderCognitive = new System.Windows.Forms.Label();
            this.txtCognitiveName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureCognitiveDesc = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderDesc = new System.Windows.Forms.Label();
            this.lblGUIAzureHeader = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderAppService = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderAppInsights = new System.Windows.Forms.Label();
            this.txtAppServicePlanName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureAppServicePlanName = new System.Windows.Forms.Label();
            this.txtAppServiceWebAppName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureAppServiceName = new System.Windows.Forms.Label();
            this.txtAppInsightsName = new System.Windows.Forms.TextBox();
            this.lblGUIAzureAppInsightsName = new System.Windows.Forms.Label();
            this.lblGUIAzureHeaderAutomation = new System.Windows.Forms.Label();
            this.txtAutomationAccountName = new System.Windows.Forms.TextBox();
            this.lblGUIAutomationDesc = new System.Windows.Forms.Label();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.pictureBox2 = new System.Windows.Forms.PictureBox();
            this.pictureBox3 = new System.Windows.Forms.PictureBox();
            this.picAppService = new System.Windows.Forms.PictureBox();
            this.picAppInsights = new System.Windows.Forms.PictureBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picAppService)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.picAppInsights)).BeginInit();
            this.SuspendLayout();
            // 
            // lblKVName
            // 
            this.lblKVName.AutoSize = true;
            this.lblKVName.Location = new System.Drawing.Point(153, 353);
            this.lblKVName.Name = "lblKVName";
            this.lblKVName.Size = new System.Drawing.Size(148, 13);
            this.lblKVName.TabIndex = 181;
            this.lblKVName.Text = "https://sfbdev.vault.azure.net";
            // 
            // lblGUIAzureHeaderKeyVault
            // 
            this.lblGUIAzureHeaderKeyVault.AutoSize = true;
            this.lblGUIAzureHeaderKeyVault.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderKeyVault.Location = new System.Drawing.Point(62, 307);
            this.lblGUIAzureHeaderKeyVault.Name = "lblGUIAzureHeaderKeyVault";
            this.lblGUIAzureHeaderKeyVault.Size = new System.Drawing.Size(73, 19);
            this.lblGUIAzureHeaderKeyVault.TabIndex = 180;
            this.lblGUIAzureHeaderKeyVault.Text = "Key Vault";
            // 
            // txtKeyVaultName
            // 
            this.txtKeyVaultName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtKeyVaultName.Location = new System.Drawing.Point(156, 330);
            this.txtKeyVaultName.Name = "txtKeyVaultName";
            this.txtKeyVaultName.Size = new System.Drawing.Size(150, 20);
            this.txtKeyVaultName.TabIndex = 170;
            this.txtKeyVaultName.Text = "txtkeyvaultname";
            this.txtKeyVaultName.TextChanged += new System.EventHandler(this.txtKeyVaultName_TextChanged);
            // 
            // lblGUIAzureKeyVaultDesc
            // 
            this.lblGUIAzureKeyVaultDesc.AutoSize = true;
            this.lblGUIAzureKeyVaultDesc.Location = new System.Drawing.Point(63, 333);
            this.lblGUIAzureKeyVaultDesc.Name = "lblGUIAzureKeyVaultDesc";
            this.lblGUIAzureKeyVaultDesc.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureKeyVaultDesc.TabIndex = 178;
            this.lblGUIAzureKeyVaultDesc.Text = "Name:";
            // 
            // txtLogAnalyticsName
            // 
            this.txtLogAnalyticsName.Location = new System.Drawing.Point(438, 74);
            this.txtLogAnalyticsName.Name = "txtLogAnalyticsName";
            this.txtLogAnalyticsName.Size = new System.Drawing.Size(150, 20);
            this.txtLogAnalyticsName.TabIndex = 176;
            this.txtLogAnalyticsName.Text = "txtLogAnalyticsName";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(352, 77);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(73, 13);
            this.label1.TabIndex = 175;
            this.label1.Text = "Log Analytics:";
            // 
            // lblCognitiveName
            // 
            this.lblCognitiveName.AutoSize = true;
            this.lblCognitiveName.Location = new System.Drawing.Point(153, 268);
            this.lblCognitiveName.Name = "lblCognitiveName";
            this.lblCognitiveName.Size = new System.Drawing.Size(263, 13);
            this.lblCognitiveName.TabIndex = 174;
            this.lblCognitiveName.Text = "https://spoinsightsdemo.cognitiveservices.azure.com/";
            // 
            // lblAppServiceWebAppName
            // 
            this.lblAppServiceWebAppName.AutoSize = true;
            this.lblAppServiceWebAppName.Location = new System.Drawing.Point(153, 183);
            this.lblAppServiceWebAppName.Name = "lblAppServiceWebAppName";
            this.lblAppServiceWebAppName.Size = new System.Drawing.Size(209, 13);
            this.lblAppServiceWebAppName.TabIndex = 173;
            this.lblAppServiceWebAppName.Text = "https://spoinsightsdemo.azurewebsites.net";
            // 
            // chkCognitiveEnable
            // 
            this.chkCognitiveEnable.AutoSize = true;
            this.chkCognitiveEnable.Location = new System.Drawing.Point(355, 247);
            this.chkCognitiveEnable.Name = "chkCognitiveEnable";
            this.chkCognitiveEnable.Size = new System.Drawing.Size(149, 17);
            this.chkCognitiveEnable.TabIndex = 160;
            this.chkCognitiveEnable.Text = "Enable cognitive analytics";
            this.chkCognitiveEnable.UseVisualStyleBackColor = true;
            this.chkCognitiveEnable.CheckedChanged += new System.EventHandler(this.chkCognitiveEnable_CheckedChanged);
            // 
            // lblGUIAzureHeaderCognitive
            // 
            this.lblGUIAzureHeaderCognitive.AutoSize = true;
            this.lblGUIAzureHeaderCognitive.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderCognitive.Location = new System.Drawing.Point(62, 222);
            this.lblGUIAzureHeaderCognitive.Name = "lblGUIAzureHeaderCognitive";
            this.lblGUIAzureHeaderCognitive.Size = new System.Drawing.Size(204, 19);
            this.lblGUIAzureHeaderCognitive.TabIndex = 172;
            this.lblGUIAzureHeaderCognitive.Text = "Cognitive Services (Optional)";
            // 
            // txtCognitiveName
            // 
            this.txtCognitiveName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtCognitiveName.Location = new System.Drawing.Point(156, 245);
            this.txtCognitiveName.Name = "txtCognitiveName";
            this.txtCognitiveName.Size = new System.Drawing.Size(150, 20);
            this.txtCognitiveName.TabIndex = 159;
            this.txtCognitiveName.Text = "txtcognitivename";
            this.txtCognitiveName.TextChanged += new System.EventHandler(this.txtCognitiveName_TextChanged);
            // 
            // lblGUIAzureCognitiveDesc
            // 
            this.lblGUIAzureCognitiveDesc.AutoSize = true;
            this.lblGUIAzureCognitiveDesc.Location = new System.Drawing.Point(63, 248);
            this.lblGUIAzureCognitiveDesc.Name = "lblGUIAzureCognitiveDesc";
            this.lblGUIAzureCognitiveDesc.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureCognitiveDesc.TabIndex = 170;
            this.lblGUIAzureCognitiveDesc.Text = "Name:";
            // 
            // lblGUIAzureHeaderDesc
            // 
            this.lblGUIAzureHeaderDesc.AutoSize = true;
            this.lblGUIAzureHeaderDesc.Location = new System.Drawing.Point(-3, 19);
            this.lblGUIAzureHeaderDesc.Name = "lblGUIAzureHeaderDesc";
            this.lblGUIAzureHeaderDesc.Size = new System.Drawing.Size(610, 13);
            this.lblGUIAzureHeaderDesc.TabIndex = 169;
            this.lblGUIAzureHeaderDesc.Text = "These Azure \"Platform-as-a-Service\" resources need to be created for the solution" +
    ". If they exist already they won\'t be recreated. ";
            // 
            // lblGUIAzureHeader
            // 
            this.lblGUIAzureHeader.AutoSize = true;
            this.lblGUIAzureHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUIAzureHeader.Name = "lblGUIAzureHeader";
            this.lblGUIAzureHeader.Size = new System.Drawing.Size(155, 19);
            this.lblGUIAzureHeader.TabIndex = 168;
            this.lblGUIAzureHeader.Text = "Azure Paas Resources";
            // 
            // lblGUIAzureHeaderAppService
            // 
            this.lblGUIAzureHeaderAppService.AutoSize = true;
            this.lblGUIAzureHeaderAppService.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderAppService.Location = new System.Drawing.Point(62, 137);
            this.lblGUIAzureHeaderAppService.Name = "lblGUIAzureHeaderAppService";
            this.lblGUIAzureHeaderAppService.Size = new System.Drawing.Size(90, 19);
            this.lblGUIAzureHeaderAppService.TabIndex = 167;
            this.lblGUIAzureHeaderAppService.Text = "App Service";
            // 
            // lblGUIAzureHeaderAppInsights
            // 
            this.lblGUIAzureHeaderAppInsights.AutoSize = true;
            this.lblGUIAzureHeaderAppInsights.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderAppInsights.Location = new System.Drawing.Point(61, 52);
            this.lblGUIAzureHeaderAppInsights.Name = "lblGUIAzureHeaderAppInsights";
            this.lblGUIAzureHeaderAppInsights.Size = new System.Drawing.Size(143, 19);
            this.lblGUIAzureHeaderAppInsights.TabIndex = 166;
            this.lblGUIAzureHeaderAppInsights.Text = "Application Insights";
            // 
            // txtAppServicePlanName
            // 
            this.txtAppServicePlanName.Location = new System.Drawing.Point(438, 159);
            this.txtAppServicePlanName.Name = "txtAppServicePlanName";
            this.txtAppServicePlanName.Size = new System.Drawing.Size(150, 20);
            this.txtAppServicePlanName.TabIndex = 158;
            this.txtAppServicePlanName.Text = "txtAppServicePlanName";
            // 
            // lblGUIAzureAppServicePlanName
            // 
            this.lblGUIAzureAppServicePlanName.AutoSize = true;
            this.lblGUIAzureAppServicePlanName.Location = new System.Drawing.Point(352, 163);
            this.lblGUIAzureAppServicePlanName.Name = "lblGUIAzureAppServicePlanName";
            this.lblGUIAzureAppServicePlanName.Size = new System.Drawing.Size(60, 13);
            this.lblGUIAzureAppServicePlanName.TabIndex = 163;
            this.lblGUIAzureAppServicePlanName.Text = "Plan name:";
            // 
            // txtAppServiceWebAppName
            // 
            this.txtAppServiceWebAppName.CharacterCasing = System.Windows.Forms.CharacterCasing.Lower;
            this.txtAppServiceWebAppName.Location = new System.Drawing.Point(156, 160);
            this.txtAppServiceWebAppName.Name = "txtAppServiceWebAppName";
            this.txtAppServiceWebAppName.Size = new System.Drawing.Size(150, 20);
            this.txtAppServiceWebAppName.TabIndex = 157;
            this.txtAppServiceWebAppName.Text = "txtappservicewebappname";
            this.txtAppServiceWebAppName.TextChanged += new System.EventHandler(this.txtAppServiceWebAppName_TextChanged);
            // 
            // lblGUIAzureAppServiceName
            // 
            this.lblGUIAzureAppServiceName.AutoSize = true;
            this.lblGUIAzureAppServiceName.Location = new System.Drawing.Point(63, 163);
            this.lblGUIAzureAppServiceName.Name = "lblGUIAzureAppServiceName";
            this.lblGUIAzureAppServiceName.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAzureAppServiceName.TabIndex = 162;
            this.lblGUIAzureAppServiceName.Text = "Name:";
            // 
            // txtAppInsightsName
            // 
            this.txtAppInsightsName.Location = new System.Drawing.Point(156, 74);
            this.txtAppInsightsName.Name = "txtAppInsightsName";
            this.txtAppInsightsName.Size = new System.Drawing.Size(150, 20);
            this.txtAppInsightsName.TabIndex = 156;
            this.txtAppInsightsName.Text = "txtAppInsightsName";
            // 
            // lblGUIAzureAppInsightsName
            // 
            this.lblGUIAzureAppInsightsName.AutoSize = true;
            this.lblGUIAzureAppInsightsName.Location = new System.Drawing.Point(63, 77);
            this.lblGUIAzureAppInsightsName.Name = "lblGUIAzureAppInsightsName";
            this.lblGUIAzureAppInsightsName.Size = new System.Drawing.Size(80, 13);
            this.lblGUIAzureAppInsightsName.TabIndex = 161;
            this.lblGUIAzureAppInsightsName.Text = "Instance name:";
            // 
            // lblGUIAzureHeaderAutomation
            // 
            this.lblGUIAzureHeaderAutomation.AutoSize = true;
            this.lblGUIAzureHeaderAutomation.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAzureHeaderAutomation.Location = new System.Drawing.Point(62, 392);
            this.lblGUIAzureHeaderAutomation.Name = "lblGUIAzureHeaderAutomation";
            this.lblGUIAzureHeaderAutomation.Size = new System.Drawing.Size(152, 19);
            this.lblGUIAzureHeaderAutomation.TabIndex = 185;
            this.lblGUIAzureHeaderAutomation.Text = "Automation Account";
            // 
            // txtAutomationAccountName
            // 
            this.txtAutomationAccountName.Location = new System.Drawing.Point(156, 415);
            this.txtAutomationAccountName.Name = "txtAutomationAccountName";
            this.txtAutomationAccountName.Size = new System.Drawing.Size(150, 20);
            this.txtAutomationAccountName.TabIndex = 171;
            this.txtAutomationAccountName.Text = "txtAutomationAccountName";
            // 
            // lblGUIAutomationDesc
            // 
            this.lblGUIAutomationDesc.AutoSize = true;
            this.lblGUIAutomationDesc.Location = new System.Drawing.Point(63, 418);
            this.lblGUIAutomationDesc.Name = "lblGUIAutomationDesc";
            this.lblGUIAutomationDesc.Size = new System.Drawing.Size(38, 13);
            this.lblGUIAutomationDesc.TabIndex = 183;
            this.lblGUIAutomationDesc.Text = "Name:";
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = global::App.ControlPanel.Properties.Resources.automation;
            this.pictureBox1.Location = new System.Drawing.Point(0, 392);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(56, 56);
            this.pictureBox1.TabIndex = 184;
            this.pictureBox1.TabStop = false;
            // 
            // pictureBox2
            // 
            this.pictureBox2.Image = global::App.ControlPanel.Properties.Resources.keyvault;
            this.pictureBox2.Location = new System.Drawing.Point(0, 307);
            this.pictureBox2.Name = "pictureBox2";
            this.pictureBox2.Size = new System.Drawing.Size(56, 56);
            this.pictureBox2.TabIndex = 179;
            this.pictureBox2.TabStop = false;
            // 
            // pictureBox3
            // 
            this.pictureBox3.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox3.Image")));
            this.pictureBox3.Location = new System.Drawing.Point(0, 222);
            this.pictureBox3.Name = "pictureBox3";
            this.pictureBox3.Size = new System.Drawing.Size(56, 56);
            this.pictureBox3.TabIndex = 171;
            this.pictureBox3.TabStop = false;
            // 
            // picAppService
            // 
            this.picAppService.Image = global::App.ControlPanel.Properties.Resources.AppService;
            this.picAppService.Location = new System.Drawing.Point(0, 137);
            this.picAppService.Name = "picAppService";
            this.picAppService.Size = new System.Drawing.Size(56, 56);
            this.picAppService.TabIndex = 165;
            this.picAppService.TabStop = false;
            // 
            // picAppInsights
            // 
            this.picAppInsights.Image = global::App.ControlPanel.Properties.Resources.AppInsights;
            this.picAppInsights.Location = new System.Drawing.Point(0, 52);
            this.picAppInsights.Name = "picAppInsights";
            this.picAppInsights.Size = new System.Drawing.Size(56, 56);
            this.picAppInsights.TabIndex = 164;
            this.picAppInsights.TabStop = false;
            // 
            // AzurePaaSConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUIAzureHeaderAutomation);
            this.Controls.Add(this.txtAutomationAccountName);
            this.Controls.Add(this.lblGUIAutomationDesc);
            this.Controls.Add(this.pictureBox1);
            this.Controls.Add(this.lblKVName);
            this.Controls.Add(this.lblGUIAzureHeaderKeyVault);
            this.Controls.Add(this.txtKeyVaultName);
            this.Controls.Add(this.lblGUIAzureKeyVaultDesc);
            this.Controls.Add(this.pictureBox2);
            this.Controls.Add(this.txtLogAnalyticsName);
            this.Controls.Add(this.label1);
            this.Controls.Add(this.lblCognitiveName);
            this.Controls.Add(this.lblAppServiceWebAppName);
            this.Controls.Add(this.chkCognitiveEnable);
            this.Controls.Add(this.lblGUIAzureHeaderCognitive);
            this.Controls.Add(this.txtCognitiveName);
            this.Controls.Add(this.lblGUIAzureCognitiveDesc);
            this.Controls.Add(this.pictureBox3);
            this.Controls.Add(this.lblGUIAzureHeaderDesc);
            this.Controls.Add(this.lblGUIAzureHeader);
            this.Controls.Add(this.lblGUIAzureHeaderAppService);
            this.Controls.Add(this.lblGUIAzureHeaderAppInsights);
            this.Controls.Add(this.txtAppServicePlanName);
            this.Controls.Add(this.lblGUIAzureAppServicePlanName);
            this.Controls.Add(this.txtAppServiceWebAppName);
            this.Controls.Add(this.lblGUIAzureAppServiceName);
            this.Controls.Add(this.txtAppInsightsName);
            this.Controls.Add(this.lblGUIAzureAppInsightsName);
            this.Controls.Add(this.picAppService);
            this.Controls.Add(this.picAppInsights);
            this.Name = "AzurePaaSConfigControl";
            this.Size = new System.Drawing.Size(613, 472);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picAppService)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.picAppInsights)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Label lblKVName;
        private System.Windows.Forms.Label lblGUIAzureHeaderKeyVault;
        private System.Windows.Forms.TextBox txtKeyVaultName;
        private System.Windows.Forms.Label lblGUIAzureKeyVaultDesc;
        private System.Windows.Forms.PictureBox pictureBox2;
        private System.Windows.Forms.TextBox txtLogAnalyticsName;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.Label lblCognitiveName;
        private System.Windows.Forms.Label lblAppServiceWebAppName;
        private System.Windows.Forms.CheckBox chkCognitiveEnable;
        private System.Windows.Forms.Label lblGUIAzureHeaderCognitive;
        private System.Windows.Forms.TextBox txtCognitiveName;
        private System.Windows.Forms.Label lblGUIAzureCognitiveDesc;
        private System.Windows.Forms.PictureBox pictureBox3;
        private System.Windows.Forms.Label lblGUIAzureHeaderDesc;
        private System.Windows.Forms.Label lblGUIAzureHeader;
        private System.Windows.Forms.Label lblGUIAzureHeaderAppService;
        private System.Windows.Forms.Label lblGUIAzureHeaderAppInsights;
        private System.Windows.Forms.TextBox txtAppServicePlanName;
        private System.Windows.Forms.Label lblGUIAzureAppServicePlanName;
        private System.Windows.Forms.TextBox txtAppServiceWebAppName;
        private System.Windows.Forms.Label lblGUIAzureAppServiceName;
        private System.Windows.Forms.TextBox txtAppInsightsName;
        private System.Windows.Forms.Label lblGUIAzureAppInsightsName;
        private System.Windows.Forms.PictureBox picAppService;
        private System.Windows.Forms.PictureBox picAppInsights;
        private System.Windows.Forms.Label lblGUIAzureHeaderAutomation;
        private System.Windows.Forms.TextBox txtAutomationAccountName;
        private System.Windows.Forms.Label lblGUIAutomationDesc;
        private System.Windows.Forms.PictureBox pictureBox1;
    }
}
