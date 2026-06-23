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
            this.lblGUITargetsHeader = new System.Windows.Forms.Label();
            this.pnlSolutionSelectionContainer.SuspendLayout();
            this.grpProductCfgInsights.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.SuspendLayout();
            // 
            // pnlSolutionSelectionContainer
            // 
            this.pnlSolutionSelectionContainer.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlSolutionSelectionContainer.Controls.Add(this.grpProductCfgInsights);
            this.pnlSolutionSelectionContainer.Location = new System.Drawing.Point(0, 24);
            this.pnlSolutionSelectionContainer.Margin = new System.Windows.Forms.Padding(2);
            this.pnlSolutionSelectionContainer.Name = "pnlSolutionSelectionContainer";
            this.pnlSolutionSelectionContainer.Size = new System.Drawing.Size(696, 561);
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
            // ImportJobSettingsSelection
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUITargetsHeader);
            this.Controls.Add(this.pnlSolutionSelectionContainer);
            this.Name = "ImportJobSettingsSelection";
            this.Size = new System.Drawing.Size(700, 587);
            this.Load += new System.EventHandler(this.ImportJobSettingsSelection_Load);
            this.pnlSolutionSelectionContainer.ResumeLayout(false);
            this.grpProductCfgInsights.ResumeLayout(false);
            this.grpProductCfgInsights.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox3)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

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
        private System.Windows.Forms.Label lblGUITargetsHeader;
    }
}
