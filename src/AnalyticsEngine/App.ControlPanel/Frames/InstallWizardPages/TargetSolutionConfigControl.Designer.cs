namespace App.ControlPanel.Controls
{
    partial class TargetSolutionConfigControl
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(TargetSolutionConfigControl));
            this.pictureBox4 = new System.Windows.Forms.PictureBox();
            this.pictureBox5 = new System.Windows.Forms.PictureBox();
            this.rdbInsights = new System.Windows.Forms.RadioButton();
            this.rdbAdoptify = new System.Windows.Forms.RadioButton();
            this.pnlSolutionSelectionContainer = new System.Windows.Forms.Panel();
            this.grpProductCfgInsights = new System.Windows.Forms.GroupBox();
            this.label3 = new System.Windows.Forms.Label();
            this.label2 = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.chkUserMetadata = new System.Windows.Forms.CheckBox();
            this.chkUserApps = new System.Windows.Forms.CheckBox();
            this.chkWeb = new System.Windows.Forms.CheckBox();
            this.chkCalls = new System.Windows.Forms.CheckBox();
            this.pictureBox3 = new System.Windows.Forms.PictureBox();
            this.chkAuditLog = new System.Windows.Forms.CheckBox();
            this.chkUsageReports = new System.Windows.Forms.CheckBox();
            this.chkTeams = new System.Windows.Forms.CheckBox();
            this.pictureBox2 = new System.Windows.Forms.PictureBox();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.lblGUITargetsDescr = new System.Windows.Forms.Label();
            this.lblGUITargetsHeader = new System.Windows.Forms.Label();
            this.grpProductCfgAdoptify = new System.Windows.Forms.GroupBox();
            this.chkInstallAdoptifyDefaultContent = new System.Windows.Forms.CheckBox();
            this.chkInstallAdoptifySchema = new System.Windows.Forms.CheckBox();
            this.label4 = new System.Windows.Forms.Label();
            this.cmbLanguage = new System.Windows.Forms.ComboBox();
            this.lblAdoptifyLanguage = new System.Windows.Forms.Label();
            this.label5 = new System.Windows.Forms.Label();
            this.lblAdoptifySite = new System.Windows.Forms.Label();
            this.txtAdoptifySiteUrl = new System.Windows.Forms.TextBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox5)).BeginInit();
            this.pnlSolutionSelectionContainer.SuspendLayout();
            this.grpProductCfgInsights.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.grpProductCfgAdoptify.SuspendLayout();
            this.SuspendLayout();
            // 
            // pictureBox4
            // 
            this.pictureBox4.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox4.Image")));
            this.pictureBox4.Location = new System.Drawing.Point(0, 53);
            this.pictureBox4.Margin = new System.Windows.Forms.Padding(2);
            this.pictureBox4.Name = "pictureBox4";
            this.pictureBox4.Size = new System.Drawing.Size(56, 56);
            this.pictureBox4.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            this.pictureBox4.TabIndex = 10;
            this.pictureBox4.TabStop = false;
            // 
            // pictureBox5
            // 
            this.pictureBox5.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox5.Image")));
            this.pictureBox5.Location = new System.Drawing.Point(226, 53);
            this.pictureBox5.Margin = new System.Windows.Forms.Padding(2);
            this.pictureBox5.Name = "pictureBox5";
            this.pictureBox5.Size = new System.Drawing.Size(56, 56);
            this.pictureBox5.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            this.pictureBox5.TabIndex = 12;
            this.pictureBox5.TabStop = false;
            // 
            // rdbInsights
            // 
            this.rdbInsights.AutoSize = true;
            this.rdbInsights.Location = new System.Drawing.Point(60, 72);
            this.rdbInsights.Margin = new System.Windows.Forms.Padding(2);
            this.rdbInsights.Name = "rdbInsights";
            this.rdbInsights.Size = new System.Drawing.Size(119, 17);
            this.rdbInsights.TabIndex = 13;
            this.rdbInsights.TabStop = true;
            this.rdbInsights.Text = "Advanced Analytics";
            this.rdbInsights.UseVisualStyleBackColor = true;
            this.rdbInsights.CheckedChanged += new System.EventHandler(this.rdbSolutionOps_CheckedChanged);
            // 
            // rdbAdoptify
            // 
            this.rdbAdoptify.AutoSize = true;
            this.rdbAdoptify.Location = new System.Drawing.Point(286, 72);
            this.rdbAdoptify.Margin = new System.Windows.Forms.Padding(2);
            this.rdbAdoptify.Name = "rdbAdoptify";
            this.rdbAdoptify.Size = new System.Drawing.Size(124, 17);
            this.rdbAdoptify.TabIndex = 14;
            this.rdbAdoptify.TabStop = true;
            this.rdbAdoptify.Text = "Adoptify Gamification";
            this.rdbAdoptify.UseVisualStyleBackColor = true;
            this.rdbAdoptify.CheckedChanged += new System.EventHandler(this.rdbSolutionOps_CheckedChanged);
            // 
            // pnlSolutionSelectionContainer
            // 
            this.pnlSolutionSelectionContainer.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlSolutionSelectionContainer.Controls.Add(this.grpProductCfgAdoptify);
            this.pnlSolutionSelectionContainer.Controls.Add(this.grpProductCfgInsights);
            this.pnlSolutionSelectionContainer.Location = new System.Drawing.Point(0, 114);
            this.pnlSolutionSelectionContainer.Margin = new System.Windows.Forms.Padding(2);
            this.pnlSolutionSelectionContainer.Name = "pnlSolutionSelectionContainer";
            this.pnlSolutionSelectionContainer.Size = new System.Drawing.Size(696, 471);
            this.pnlSolutionSelectionContainer.TabIndex = 16;
            // 
            // grpProductCfgInsights
            // 
            this.grpProductCfgInsights.Controls.Add(this.label3);
            this.grpProductCfgInsights.Controls.Add(this.label2);
            this.grpProductCfgInsights.Controls.Add(this.label1);
            this.grpProductCfgInsights.Controls.Add(this.chkUserMetadata);
            this.grpProductCfgInsights.Controls.Add(this.chkUserApps);
            this.grpProductCfgInsights.Controls.Add(this.chkWeb);
            this.grpProductCfgInsights.Controls.Add(this.chkCalls);
            this.grpProductCfgInsights.Controls.Add(this.pictureBox3);
            this.grpProductCfgInsights.Controls.Add(this.chkAuditLog);
            this.grpProductCfgInsights.Controls.Add(this.chkUsageReports);
            this.grpProductCfgInsights.Controls.Add(this.chkTeams);
            this.grpProductCfgInsights.Controls.Add(this.pictureBox2);
            this.grpProductCfgInsights.Controls.Add(this.pictureBox1);
            this.grpProductCfgInsights.Location = new System.Drawing.Point(-4, 14);
            this.grpProductCfgInsights.Margin = new System.Windows.Forms.Padding(2);
            this.grpProductCfgInsights.Name = "grpProductCfgInsights";
            this.grpProductCfgInsights.Padding = new System.Windows.Forms.Padding(2);
            this.grpProductCfgInsights.Size = new System.Drawing.Size(339, 372);
            this.grpProductCfgInsights.TabIndex = 16;
            this.grpProductCfgInsights.TabStop = false;
            this.grpProductCfgInsights.Text = "Advanced Analytics and Insights Options:";
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label3.Location = new System.Drawing.Point(64, 277);
            this.label3.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(176, 20);
            this.label3.TabIndex = 22;
            this.label3.Text = "SharePoint Online Data";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(64, 158);
            this.label2.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(182, 20);
            this.label2.TabIndex = 21;
            this.label2.Text = "Office 365 General Data";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(64, 24);
            this.label1.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(96, 20);
            this.label1.TabIndex = 20;
            this.label1.Text = "Teams Data";
            // 
            // chkUserMetadata
            // 
            this.chkUserMetadata.AutoSize = true;
            this.chkUserMetadata.Location = new System.Drawing.Point(4, 230);
            this.chkUserMetadata.Margin = new System.Windows.Forms.Padding(2);
            this.chkUserMetadata.Name = "chkUserMetadata";
            this.chkUserMetadata.Size = new System.Drawing.Size(190, 17);
            this.chkUserMetadata.TabIndex = 16;
            this.chkUserMetadata.Text = "User Azure AD extended metadata";
            this.chkUserMetadata.UseVisualStyleBackColor = true;
            this.chkUserMetadata.CheckedChanged += new System.EventHandler(this.chkUserMetadata_CheckedChanged);
            // 
            // chkUserApps
            // 
            this.chkUserApps.AutoSize = true;
            this.chkUserApps.Location = new System.Drawing.Point(4, 115);
            this.chkUserApps.Margin = new System.Windows.Forms.Padding(2);
            this.chkUserApps.Name = "chkUserApps";
            this.chkUserApps.Size = new System.Drawing.Size(115, 17);
            this.chkUserApps.TabIndex = 14;
            this.chkUserApps.Text = "User installed apps";
            this.chkUserApps.UseVisualStyleBackColor = true;
            this.chkUserApps.CheckedChanged += new System.EventHandler(this.chkUserApps_CheckedChanged);
            // 
            // chkWeb
            // 
            this.chkWeb.AutoSize = true;
            this.chkWeb.Location = new System.Drawing.Point(6, 330);
            this.chkWeb.Margin = new System.Windows.Forms.Padding(2);
            this.chkWeb.Name = "chkWeb";
            this.chkWeb.Size = new System.Drawing.Size(92, 17);
            this.chkWeb.TabIndex = 17;
            this.chkWeb.Text = "Web sessions";
            this.chkWeb.UseVisualStyleBackColor = true;
            this.chkWeb.CheckedChanged += new System.EventHandler(this.chkWeb_CheckedChanged);
            // 
            // chkCalls
            // 
            this.chkCalls.AutoSize = true;
            this.chkCalls.Location = new System.Drawing.Point(4, 96);
            this.chkCalls.Margin = new System.Windows.Forms.Padding(2);
            this.chkCalls.Name = "chkCalls";
            this.chkCalls.Size = new System.Drawing.Size(131, 17);
            this.chkCalls.TabIndex = 12;
            this.chkCalls.Text = "Call and meetings logs";
            this.chkCalls.UseVisualStyleBackColor = true;
            this.chkCalls.CheckedChanged += new System.EventHandler(this.chkCalls_CheckedChanged);
            // 
            // pictureBox3
            // 
            this.pictureBox3.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox3.Image")));
            this.pictureBox3.Location = new System.Drawing.Point(4, 269);
            this.pictureBox3.Margin = new System.Windows.Forms.Padding(2);
            this.pictureBox3.Name = "pictureBox3";
            this.pictureBox3.Size = new System.Drawing.Size(56, 56);
            this.pictureBox3.TabIndex = 18;
            this.pictureBox3.TabStop = false;
            // 
            // chkAuditLog
            // 
            this.chkAuditLog.AutoSize = true;
            this.chkAuditLog.Location = new System.Drawing.Point(6, 348);
            this.chkAuditLog.Margin = new System.Windows.Forms.Padding(2);
            this.chkAuditLog.Name = "chkAuditLog";
            this.chkAuditLog.Size = new System.Drawing.Size(135, 17);
            this.chkAuditLog.TabIndex = 19;
            this.chkAuditLog.Text = "Audit data (SharePoint)";
            this.chkAuditLog.UseVisualStyleBackColor = true;
            this.chkAuditLog.CheckedChanged += new System.EventHandler(this.chkAuditLog_CheckedChanged);
            // 
            // chkUsageReports
            // 
            this.chkUsageReports.AutoSize = true;
            this.chkUsageReports.Location = new System.Drawing.Point(4, 211);
            this.chkUsageReports.Margin = new System.Windows.Forms.Padding(2);
            this.chkUsageReports.Name = "chkUsageReports";
            this.chkUsageReports.Size = new System.Drawing.Size(329, 17);
            this.chkUsageReports.TabIndex = 15;
            this.chkUsageReports.Text = "Usage reports (Teams, OneDrive, SharePoint, Yammer, Outlook)";
            this.chkUsageReports.UseVisualStyleBackColor = true;
            this.chkUsageReports.CheckedChanged += new System.EventHandler(this.chkUsageReports_CheckedChanged);
            // 
            // chkTeams
            // 
            this.chkTeams.AutoSize = true;
            this.chkTeams.Location = new System.Drawing.Point(4, 76);
            this.chkTeams.Margin = new System.Windows.Forms.Padding(2);
            this.chkTeams.Name = "chkTeams";
            this.chkTeams.Size = new System.Drawing.Size(157, 17);
            this.chkTeams.TabIndex = 10;
            this.chkTeams.Text = "Teams + channels adoption";
            this.chkTeams.UseVisualStyleBackColor = true;
            this.chkTeams.CheckedChanged += new System.EventHandler(this.chkTeams_CheckedChanged);
            // 
            // pictureBox2
            // 
            this.pictureBox2.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox2.Image")));
            this.pictureBox2.Location = new System.Drawing.Point(4, 150);
            this.pictureBox2.Margin = new System.Windows.Forms.Padding(2);
            this.pictureBox2.Name = "pictureBox2";
            this.pictureBox2.Size = new System.Drawing.Size(56, 56);
            this.pictureBox2.TabIndex = 13;
            this.pictureBox2.TabStop = false;
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox1.Image")));
            this.pictureBox1.Location = new System.Drawing.Point(4, 16);
            this.pictureBox1.Margin = new System.Windows.Forms.Padding(2);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(56, 56);
            this.pictureBox1.TabIndex = 11;
            this.pictureBox1.TabStop = false;
            // 
            // lblGUITargetsDescr
            // 
            this.lblGUITargetsDescr.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUITargetsDescr.Location = new System.Drawing.Point(-1, 28);
            this.lblGUITargetsDescr.Name = "lblGUITargetsDescr";
            this.lblGUITargetsDescr.Size = new System.Drawing.Size(697, 23);
            this.lblGUITargetsDescr.TabIndex = 78;
            this.lblGUITargetsDescr.Text = "What solution are you installing?";
            // 
            // lblGUITargetsHeader
            // 
            this.lblGUITargetsHeader.AutoSize = true;
            this.lblGUITargetsHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUITargetsHeader.Location = new System.Drawing.Point(-2, 0);
            this.lblGUITargetsHeader.Name = "lblGUITargetsHeader";
            this.lblGUITargetsHeader.Size = new System.Drawing.Size(109, 19);
            this.lblGUITargetsHeader.TabIndex = 77;
            this.lblGUITargetsHeader.Text = "Import Targets";
            // 
            // grpProductCfgAdoptify
            // 
            this.grpProductCfgAdoptify.Controls.Add(this.chkInstallAdoptifyDefaultContent);
            this.grpProductCfgAdoptify.Controls.Add(this.chkInstallAdoptifySchema);
            this.grpProductCfgAdoptify.Controls.Add(this.label4);
            this.grpProductCfgAdoptify.Controls.Add(this.cmbLanguage);
            this.grpProductCfgAdoptify.Controls.Add(this.lblAdoptifyLanguage);
            this.grpProductCfgAdoptify.Controls.Add(this.label5);
            this.grpProductCfgAdoptify.Controls.Add(this.lblAdoptifySite);
            this.grpProductCfgAdoptify.Controls.Add(this.txtAdoptifySiteUrl);
            this.grpProductCfgAdoptify.Location = new System.Drawing.Point(339, 14);
            this.grpProductCfgAdoptify.Margin = new System.Windows.Forms.Padding(2);
            this.grpProductCfgAdoptify.Name = "grpProductCfgAdoptify";
            this.grpProductCfgAdoptify.Padding = new System.Windows.Forms.Padding(2);
            this.grpProductCfgAdoptify.Size = new System.Drawing.Size(349, 299);
            this.grpProductCfgAdoptify.TabIndex = 20;
            this.grpProductCfgAdoptify.TabStop = false;
            this.grpProductCfgAdoptify.Text = "Adoptify Options:";
            // 
            // chkInstallAdoptifyDefaultContent
            // 
            this.chkInstallAdoptifyDefaultContent.AutoSize = true;
            this.chkInstallAdoptifyDefaultContent.Location = new System.Drawing.Point(25, 256);
            this.chkInstallAdoptifyDefaultContent.Name = "chkInstallAdoptifyDefaultContent";
            this.chkInstallAdoptifyDefaultContent.Size = new System.Drawing.Size(192, 17);
            this.chkInstallAdoptifyDefaultContent.TabIndex = 7;
            this.chkInstallAdoptifyDefaultContent.Text = "Default quest, level, badge content";
            this.chkInstallAdoptifyDefaultContent.UseVisualStyleBackColor = true;
            // 
            // chkInstallAdoptifySchema
            // 
            this.chkInstallAdoptifySchema.AutoSize = true;
            this.chkInstallAdoptifySchema.Location = new System.Drawing.Point(25, 233);
            this.chkInstallAdoptifySchema.Name = "chkInstallAdoptifySchema";
            this.chkInstallAdoptifySchema.Size = new System.Drawing.Size(137, 17);
            this.chkInstallAdoptifySchema.TabIndex = 6;
            this.chkInstallAdoptifySchema.Text = "SharePoint site schema";
            this.chkInstallAdoptifySchema.UseVisualStyleBackColor = true;
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(12, 208);
            this.label4.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(77, 13);
            this.label4.TabIndex = 5;
            this.label4.Text = "What to install:";
            // 
            // cmbLanguage
            // 
            this.cmbLanguage.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbLanguage.FormattingEnabled = true;
            this.cmbLanguage.Location = new System.Drawing.Point(21, 151);
            this.cmbLanguage.Name = "cmbLanguage";
            this.cmbLanguage.Size = new System.Drawing.Size(167, 21);
            this.cmbLanguage.TabIndex = 4;
            // 
            // lblAdoptifyLanguage
            // 
            this.lblAdoptifyLanguage.AutoSize = true;
            this.lblAdoptifyLanguage.Location = new System.Drawing.Point(12, 135);
            this.lblAdoptifyLanguage.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblAdoptifyLanguage.Name = "lblAdoptifyLanguage";
            this.lblAdoptifyLanguage.Size = new System.Drawing.Size(99, 13);
            this.lblAdoptifyLanguage.TabIndex = 3;
            this.lblAdoptifyLanguage.Text = "Language to install:";
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(15, 84);
            this.label5.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(477, 13);
            this.label5.TabIndex = 2;
            this.label5.Text = "The site-collection must already exist. A browser pop-up will launch for you to a" +
    "uthenticate against it.";
            // 
            // lblAdoptifySite
            // 
            this.lblAdoptifySite.AutoSize = true;
            this.lblAdoptifySite.Location = new System.Drawing.Point(12, 34);
            this.lblAdoptifySite.Margin = new System.Windows.Forms.Padding(2, 0, 2, 0);
            this.lblAdoptifySite.Name = "lblAdoptifySite";
            this.lblAdoptifySite.Size = new System.Drawing.Size(340, 13);
            this.lblAdoptifySite.TabIndex = 1;
            this.lblAdoptifySite.Text = "Adoptify site URL (e.g \'https://contoso.sharepoint.com/sites/adoptify\'):";
            // 
            // txtAdoptifySiteUrl
            // 
            this.txtAdoptifySiteUrl.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtAdoptifySiteUrl.Location = new System.Drawing.Point(21, 55);
            this.txtAdoptifySiteUrl.Margin = new System.Windows.Forms.Padding(2);
            this.txtAdoptifySiteUrl.Name = "txtAdoptifySiteUrl";
            this.txtAdoptifySiteUrl.Size = new System.Drawing.Size(239, 20);
            this.txtAdoptifySiteUrl.TabIndex = 0;
            this.txtAdoptifySiteUrl.Text = "txtAdoptifySiteUrl";
            // 
            // ImportJobSettingsSelection
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUITargetsDescr);
            this.Controls.Add(this.lblGUITargetsHeader);
            this.Controls.Add(this.pnlSolutionSelectionContainer);
            this.Controls.Add(this.rdbAdoptify);
            this.Controls.Add(this.rdbInsights);
            this.Controls.Add(this.pictureBox5);
            this.Controls.Add(this.pictureBox4);
            this.Name = "ImportJobSettingsSelection";
            this.Size = new System.Drawing.Size(700, 587);
            this.Load += new System.EventHandler(this.ImportJobSettingsSelection_Load);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox5)).EndInit();
            this.pnlSolutionSelectionContainer.ResumeLayout(false);
            this.grpProductCfgInsights.ResumeLayout(false);
            this.grpProductCfgInsights.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.grpProductCfgAdoptify.ResumeLayout(false);
            this.grpProductCfgAdoptify.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.PictureBox pictureBox4;
        private System.Windows.Forms.PictureBox pictureBox5;
        private System.Windows.Forms.RadioButton rdbInsights;
        private System.Windows.Forms.RadioButton rdbAdoptify;
        private System.Windows.Forms.Panel pnlSolutionSelectionContainer;
        private System.Windows.Forms.GroupBox grpProductCfgInsights;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.CheckBox chkUserMetadata;
        private System.Windows.Forms.CheckBox chkUserApps;
        private System.Windows.Forms.CheckBox chkWeb;
        private System.Windows.Forms.CheckBox chkCalls;
        private System.Windows.Forms.PictureBox pictureBox3;
        private System.Windows.Forms.CheckBox chkAuditLog;
        private System.Windows.Forms.CheckBox chkUsageReports;
        private System.Windows.Forms.CheckBox chkTeams;
        private System.Windows.Forms.PictureBox pictureBox2;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.Label lblGUITargetsDescr;
        private System.Windows.Forms.Label lblGUITargetsHeader;
        private System.Windows.Forms.GroupBox grpProductCfgAdoptify;
        private System.Windows.Forms.CheckBox chkInstallAdoptifyDefaultContent;
        private System.Windows.Forms.CheckBox chkInstallAdoptifySchema;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.ComboBox cmbLanguage;
        private System.Windows.Forms.Label lblAdoptifyLanguage;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.Label lblAdoptifySite;
        private System.Windows.Forms.TextBox txtAdoptifySiteUrl;
    }
}
