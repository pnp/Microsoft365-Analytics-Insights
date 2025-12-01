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
            this.rdbInsights = new System.Windows.Forms.RadioButton();
            this.pnlSolutionSelectionContainer = new System.Windows.Forms.Panel();
            this.grpProductCfgInsights = new System.Windows.Forms.GroupBox();
            this.label2 = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.chkUserMetadata = new System.Windows.Forms.CheckBox();
            this.chkCalls = new System.Windows.Forms.CheckBox();
            this.chkAuditLog = new System.Windows.Forms.CheckBox();
            this.chkUsageReports = new System.Windows.Forms.CheckBox();
            this.chkTeams = new System.Windows.Forms.CheckBox();
            this.pictureBox2 = new System.Windows.Forms.PictureBox();
            this.pictureBox1 = new System.Windows.Forms.PictureBox();
            this.lblGUITargetsDescr = new System.Windows.Forms.Label();
            this.lblGUITargetsHeader = new System.Windows.Forms.Label();
            this.pnlAuditLogOptions = new System.Windows.Forms.Panel();
            this.chkAuditLogGeneral = new System.Windows.Forms.CheckBox();
            this.chkAuditLogSharePoint = new System.Windows.Forms.CheckBox();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).BeginInit();
            this.pnlSolutionSelectionContainer.SuspendLayout();
            this.grpProductCfgInsights.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).BeginInit();
            this.pnlAuditLogOptions.SuspendLayout();
            this.SuspendLayout();
            // 
            // pictureBox4
            // 
            this.pictureBox4.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox4.Image")));
            this.pictureBox4.Location = new System.Drawing.Point(0, 82);
            this.pictureBox4.Name = "pictureBox4";
            this.pictureBox4.Size = new System.Drawing.Size(84, 86);
            this.pictureBox4.SizeMode = System.Windows.Forms.PictureBoxSizeMode.StretchImage;
            this.pictureBox4.TabIndex = 10;
            this.pictureBox4.TabStop = false;
            // 
            // rdbInsights
            // 
            this.rdbInsights.AutoSize = true;
            this.rdbInsights.Location = new System.Drawing.Point(90, 111);
            this.rdbInsights.Name = "rdbInsights";
            this.rdbInsights.Size = new System.Drawing.Size(172, 24);
            this.rdbInsights.TabIndex = 13;
            this.rdbInsights.TabStop = true;
            this.rdbInsights.Text = "Advanced Analytics";
            this.rdbInsights.UseVisualStyleBackColor = true;
            this.rdbInsights.CheckedChanged += new System.EventHandler(this.rdbSolutionOps_CheckedChanged);
            // 
            // pnlSolutionSelectionContainer
            // 
            this.pnlSolutionSelectionContainer.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnlSolutionSelectionContainer.Controls.Add(this.grpProductCfgInsights);
            this.pnlSolutionSelectionContainer.Location = new System.Drawing.Point(0, 175);
            this.pnlSolutionSelectionContainer.Name = "pnlSolutionSelectionContainer";
            this.pnlSolutionSelectionContainer.Size = new System.Drawing.Size(1044, 725);
            this.pnlSolutionSelectionContainer.TabIndex = 16;
            // 
            // grpProductCfgInsights
            // 
            this.grpProductCfgInsights.Controls.Add(this.pnlAuditLogOptions);
            this.grpProductCfgInsights.Controls.Add(this.label2);
            this.grpProductCfgInsights.Controls.Add(this.label1);
            this.grpProductCfgInsights.Controls.Add(this.chkUserMetadata);
            this.grpProductCfgInsights.Controls.Add(this.chkCalls);
            this.grpProductCfgInsights.Controls.Add(this.chkAuditLog);
            this.grpProductCfgInsights.Controls.Add(this.chkUsageReports);
            this.grpProductCfgInsights.Controls.Add(this.chkTeams);
            this.grpProductCfgInsights.Controls.Add(this.pictureBox2);
            this.grpProductCfgInsights.Controls.Add(this.pictureBox1);
            this.grpProductCfgInsights.Location = new System.Drawing.Point(-6, 22);
            this.grpProductCfgInsights.Name = "grpProductCfgInsights";
            this.grpProductCfgInsights.Size = new System.Drawing.Size(508, 572);
            this.grpProductCfgInsights.TabIndex = 16;
            this.grpProductCfgInsights.TabStop = false;
            this.grpProductCfgInsights.Text = "Advanced Analytics and Insights Options:";
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(96, 243);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(268, 29);
            this.label2.TabIndex = 21;
            this.label2.Text = "Office 365 General Data";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Font = new System.Drawing.Font("Microsoft Sans Serif", 12F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label1.Location = new System.Drawing.Point(96, 37);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(143, 29);
            this.label1.TabIndex = 20;
            this.label1.Text = "Teams Data";
            // 
            // chkUserMetadata
            // 
            this.chkUserMetadata.AutoSize = true;
            this.chkUserMetadata.Location = new System.Drawing.Point(6, 354);
            this.chkUserMetadata.Name = "chkUserMetadata";
            this.chkUserMetadata.Size = new System.Drawing.Size(284, 24);
            this.chkUserMetadata.TabIndex = 16;
            this.chkUserMetadata.Text = "User Azure AD extended metadata";
            this.chkUserMetadata.UseVisualStyleBackColor = true;
            this.chkUserMetadata.CheckedChanged += new System.EventHandler(this.chkUserMetadata_CheckedChanged);
            // 
            // chkCalls
            // 
            this.chkCalls.AutoSize = true;
            this.chkCalls.Location = new System.Drawing.Point(6, 148);
            this.chkCalls.Name = "chkCalls";
            this.chkCalls.Size = new System.Drawing.Size(194, 24);
            this.chkCalls.TabIndex = 12;
            this.chkCalls.Text = "Call and meetings logs";
            this.chkCalls.UseVisualStyleBackColor = true;
            this.chkCalls.CheckedChanged += new System.EventHandler(this.chkCalls_CheckedChanged);
            // 
            // chkAuditLog
            // 
            this.chkAuditLog.AutoSize = true;
            this.chkAuditLog.Location = new System.Drawing.Point(6, 386);
            this.chkAuditLog.Name = "chkAuditLog";
            this.chkAuditLog.Size = new System.Drawing.Size(328, 24);
            this.chkAuditLog.TabIndex = 19;
            this.chkAuditLog.Text = "Audit data (SharePoint + General/Copilot)";
            this.chkAuditLog.UseVisualStyleBackColor = true;
            this.chkAuditLog.CheckedChanged += new System.EventHandler(this.chkAuditLog_CheckedChanged);
            // 
            // chkUsageReports
            // 
            this.chkUsageReports.AutoSize = true;
            this.chkUsageReports.Location = new System.Drawing.Point(6, 325);
            this.chkUsageReports.Name = "chkUsageReports";
            this.chkUsageReports.Size = new System.Drawing.Size(490, 24);
            this.chkUsageReports.TabIndex = 15;
            this.chkUsageReports.Text = "Usage reports (Teams, OneDrive, SharePoint, Yammer, Outlook)";
            this.chkUsageReports.UseVisualStyleBackColor = true;
            this.chkUsageReports.CheckedChanged += new System.EventHandler(this.chkUsageReports_CheckedChanged);
            // 
            // chkTeams
            // 
            this.chkTeams.AutoSize = true;
            this.chkTeams.Location = new System.Drawing.Point(6, 117);
            this.chkTeams.Name = "chkTeams";
            this.chkTeams.Size = new System.Drawing.Size(230, 24);
            this.chkTeams.TabIndex = 10;
            this.chkTeams.Text = "Teams + channels adoption";
            this.chkTeams.UseVisualStyleBackColor = true;
            this.chkTeams.CheckedChanged += new System.EventHandler(this.chkTeams_CheckedChanged);
            // 
            // pictureBox2
            // 
            this.pictureBox2.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox2.Image")));
            this.pictureBox2.Location = new System.Drawing.Point(6, 231);
            this.pictureBox2.Name = "pictureBox2";
            this.pictureBox2.Size = new System.Drawing.Size(84, 86);
            this.pictureBox2.TabIndex = 13;
            this.pictureBox2.TabStop = false;
            // 
            // pictureBox1
            // 
            this.pictureBox1.Image = ((System.Drawing.Image)(resources.GetObject("pictureBox1.Image")));
            this.pictureBox1.Location = new System.Drawing.Point(6, 25);
            this.pictureBox1.Name = "pictureBox1";
            this.pictureBox1.Size = new System.Drawing.Size(84, 86);
            this.pictureBox1.TabIndex = 11;
            this.pictureBox1.TabStop = false;
            // 
            // lblGUITargetsDescr
            // 
            this.lblGUITargetsDescr.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUITargetsDescr.Location = new System.Drawing.Point(-2, 43);
            this.lblGUITargetsDescr.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblGUITargetsDescr.Name = "lblGUITargetsDescr";
            this.lblGUITargetsDescr.Size = new System.Drawing.Size(1046, 35);
            this.lblGUITargetsDescr.TabIndex = 78;
            this.lblGUITargetsDescr.Text = "What solution are you installing?";
            // 
            // lblGUITargetsHeader
            // 
            this.lblGUITargetsHeader.AutoSize = true;
            this.lblGUITargetsHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUITargetsHeader.Location = new System.Drawing.Point(-3, 0);
            this.lblGUITargetsHeader.Margin = new System.Windows.Forms.Padding(4, 0, 4, 0);
            this.lblGUITargetsHeader.Name = "lblGUITargetsHeader";
            this.lblGUITargetsHeader.Size = new System.Drawing.Size(159, 29);
            this.lblGUITargetsHeader.TabIndex = 77;
            this.lblGUITargetsHeader.Text = "Import Targets";
            // 
            // pnlAuditLogOptions
            // 
            this.pnlAuditLogOptions.Controls.Add(this.chkAuditLogSharePoint);
            this.pnlAuditLogOptions.Controls.Add(this.chkAuditLogGeneral);
            this.pnlAuditLogOptions.Location = new System.Drawing.Point(46, 416);
            this.pnlAuditLogOptions.Name = "pnlAuditLogOptions";
            this.pnlAuditLogOptions.Size = new System.Drawing.Size(288, 77);
            this.pnlAuditLogOptions.TabIndex = 23;
            // 
            // chkAuditLogGeneral
            // 
            this.chkAuditLogGeneral.AutoSize = true;
            this.chkAuditLogGeneral.Location = new System.Drawing.Point(0, 3);
            this.chkAuditLogGeneral.Name = "chkAuditLogGeneral";
            this.chkAuditLogGeneral.Size = new System.Drawing.Size(145, 24);
            this.chkAuditLogGeneral.TabIndex = 23;
            this.chkAuditLogGeneral.Text = "General/Copilot\r\n";
            this.chkAuditLogGeneral.UseVisualStyleBackColor = true;
            // 
            // chkAuditLogSharePoint
            // 
            this.chkAuditLogSharePoint.AutoSize = true;
            this.chkAuditLogSharePoint.Location = new System.Drawing.Point(0, 33);
            this.chkAuditLogSharePoint.Name = "chkAuditLogSharePoint";
            this.chkAuditLogSharePoint.Size = new System.Drawing.Size(114, 24);
            this.chkAuditLogSharePoint.TabIndex = 24;
            this.chkAuditLogSharePoint.Text = "SharePoint";
            this.chkAuditLogSharePoint.UseVisualStyleBackColor = true;
            // 
            // TargetSolutionConfigControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(9F, 20F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.lblGUITargetsDescr);
            this.Controls.Add(this.lblGUITargetsHeader);
            this.Controls.Add(this.pnlSolutionSelectionContainer);
            this.Controls.Add(this.rdbInsights);
            this.Controls.Add(this.pictureBox4);
            this.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.Name = "TargetSolutionConfigControl";
            this.Size = new System.Drawing.Size(1050, 903);
            this.Load += new System.EventHandler(this.ImportJobSettingsSelection_Load);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox4)).EndInit();
            this.pnlSolutionSelectionContainer.ResumeLayout(false);
            this.grpProductCfgInsights.ResumeLayout(false);
            this.grpProductCfgInsights.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox2)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox1)).EndInit();
            this.pnlAuditLogOptions.ResumeLayout(false);
            this.pnlAuditLogOptions.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.PictureBox pictureBox4;
        private System.Windows.Forms.RadioButton rdbInsights;
        private System.Windows.Forms.Panel pnlSolutionSelectionContainer;
        private System.Windows.Forms.GroupBox grpProductCfgInsights;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.CheckBox chkUserMetadata;
        private System.Windows.Forms.CheckBox chkCalls;
        private System.Windows.Forms.CheckBox chkAuditLog;
        private System.Windows.Forms.CheckBox chkUsageReports;
        private System.Windows.Forms.CheckBox chkTeams;
        private System.Windows.Forms.PictureBox pictureBox2;
        private System.Windows.Forms.PictureBox pictureBox1;
        private System.Windows.Forms.Label lblGUITargetsDescr;
        private System.Windows.Forms.Label lblGUITargetsHeader;
        private System.Windows.Forms.Panel pnlAuditLogOptions;
        private System.Windows.Forms.CheckBox chkAuditLogSharePoint;
        private System.Windows.Forms.CheckBox chkAuditLogGeneral;
    }
}
