namespace App.ControlPanel.Frames.InstallWizard
{
    partial class InstallSolutionControl
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
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(InstallSolutionControl));
            System.Windows.Forms.ListViewItem listViewItem1 = new System.Windows.Forms.ListViewItem("Message", 1);
            System.Windows.Forms.ListViewItem listViewItem2 = new System.Windows.Forms.ListViewItem("Error", 0);
            this.btnRunTests = new System.Windows.Forms.Button();
            this.btnCopyLog = new System.Windows.Forms.Button();
            this.grpTasks = new System.Windows.Forms.GroupBox();
            this.chkInstallOptionAllowUsageStats = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionOpenAdminSite = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionUpgradeDB = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionWebJobs = new System.Windows.Forms.CheckBox();
            this.lblGUIInstallDesc = new System.Windows.Forms.Label();
            this.lblGUIInstallHeader = new System.Windows.Forms.Label();
            this.pictureBox8 = new System.Windows.Forms.PictureBox();
            this.btnInstall = new System.Windows.Forms.Button();
            this.lstLog = new System.Windows.Forms.ListView();
            this.columnHeader1 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.imageListIssues = new System.Windows.Forms.ImageList(this.components);
            this.grpTasks.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox8)).BeginInit();
            this.SuspendLayout();
            // 
            // btnRunTests
            // 
            this.btnRunTests.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnRunTests.Image = ((System.Drawing.Image)(resources.GetObject("btnRunTests.Image")));
            this.btnRunTests.Location = new System.Drawing.Point(276, 360);
            this.btnRunTests.Name = "btnRunTests";
            this.btnRunTests.Size = new System.Drawing.Size(126, 28);
            this.btnRunTests.TabIndex = 89;
            this.btnRunTests.Text = "Test Configuration";
            this.btnRunTests.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnRunTests.UseVisualStyleBackColor = true;
            this.btnRunTests.Click += new System.EventHandler(this.btnRunTests_Click);
            // 
            // btnCopyLog
            // 
            this.btnCopyLog.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnCopyLog.Image = global::App.ControlPanel.Properties.Resources.Copy_16x;
            this.btnCopyLog.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnCopyLog.Location = new System.Drawing.Point(0, 360);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(126, 28);
            this.btnCopyLog.TabIndex = 88;
            this.btnCopyLog.Text = "Copy to Clipboard";
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.btnCopyLog_Click);
            // 
            // grpTasks
            // 
            this.grpTasks.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.grpTasks.Controls.Add(this.chkInstallOptionAllowUsageStats);
            this.grpTasks.Controls.Add(this.chkInstallOptionOpenAdminSite);
            this.grpTasks.Controls.Add(this.chkInstallOptionUpgradeDB);
            this.grpTasks.Controls.Add(this.chkInstallOptionWebJobs);
            this.grpTasks.Location = new System.Drawing.Point(0, 88);
            this.grpTasks.Name = "grpTasks";
            this.grpTasks.Size = new System.Drawing.Size(534, 77);
            this.grpTasks.TabIndex = 87;
            this.grpTasks.TabStop = false;
            this.grpTasks.Text = "Install Tasks:";
            // 
            // chkInstallOptionAllowUsageStats
            // 
            this.chkInstallOptionAllowUsageStats.AutoSize = true;
            this.chkInstallOptionAllowUsageStats.Location = new System.Drawing.Point(6, 42);
            this.chkInstallOptionAllowUsageStats.Name = "chkInstallOptionAllowUsageStats";
            this.chkInstallOptionAllowUsageStats.Size = new System.Drawing.Size(316, 17);
            this.chkInstallOptionAllowUsageStats.TabIndex = 5;
            this.chkInstallOptionAllowUsageStats.Text = "Allow anonymous usage reporting to help improve the product";
            this.chkInstallOptionAllowUsageStats.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionOpenAdminSite
            // 
            this.chkInstallOptionOpenAdminSite.AutoSize = true;
            this.chkInstallOptionOpenAdminSite.Location = new System.Drawing.Point(334, 19);
            this.chkInstallOptionOpenAdminSite.Name = "chkInstallOptionOpenAdminSite";
            this.chkInstallOptionOpenAdminSite.Size = new System.Drawing.Size(234, 17);
            this.chkInstallOptionOpenAdminSite.TabIndex = 4;
            this.chkInstallOptionOpenAdminSite.Text = "Open administration website after installation";
            this.chkInstallOptionOpenAdminSite.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionUpgradeDB
            // 
            this.chkInstallOptionUpgradeDB.AutoSize = true;
            this.chkInstallOptionUpgradeDB.Location = new System.Drawing.Point(334, 42);
            this.chkInstallOptionUpgradeDB.Name = "chkInstallOptionUpgradeDB";
            this.chkInstallOptionUpgradeDB.Size = new System.Drawing.Size(172, 17);
            this.chkInstallOptionUpgradeDB.TabIndex = 2;
            this.chkInstallOptionUpgradeDB.Text = "Upgrade SQL Database schema (if necessary)";
            this.chkInstallOptionUpgradeDB.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionWebJobs
            // 
            this.chkInstallOptionWebJobs.AutoSize = true;
            this.chkInstallOptionWebJobs.Location = new System.Drawing.Point(6, 19);
            this.chkInstallOptionWebJobs.Name = "chkInstallOptionWebJobs";
            this.chkInstallOptionWebJobs.Size = new System.Drawing.Size(206, 17);
            this.chkInstallOptionWebJobs.TabIndex = 1;
            this.chkInstallOptionWebJobs.Text = "Update app-service with latest release";
            this.chkInstallOptionWebJobs.UseVisualStyleBackColor = true;
            // 
            // lblGUIInstallDesc
            // 
            this.lblGUIInstallDesc.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblGUIInstallDesc.Location = new System.Drawing.Point(62, 22);
            this.lblGUIInstallDesc.Name = "lblGUIInstallDesc";
            this.lblGUIInstallDesc.Size = new System.Drawing.Size(472, 42);
            this.lblGUIInstallDesc.TabIndex = 86;
            this.lblGUIInstallDesc.Text = "Once everything is ready, click \"install\" to install and/or update the solution. " +
    "Status messages and responses from Azure/SharePoint Online will be reported belo" +
    "w.";
            // 
            // lblGUIInstallHeader
            // 
            this.lblGUIInstallHeader.AutoSize = true;
            this.lblGUIInstallHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIInstallHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUIInstallHeader.Name = "lblGUIInstallHeader";
            this.lblGUIInstallHeader.Size = new System.Drawing.Size(172, 19);
            this.lblGUIInstallHeader.TabIndex = 85;
            this.lblGUIInstallHeader.Text = "Ready to Install/Update";
            // 
            // pictureBox8
            // 
            this.pictureBox8.Image = global::App.ControlPanel.Properties.Resources.Powershell_script_file;
            this.pictureBox8.Location = new System.Drawing.Point(0, 22);
            this.pictureBox8.Name = "pictureBox8";
            this.pictureBox8.Size = new System.Drawing.Size(56, 56);
            this.pictureBox8.TabIndex = 84;
            this.pictureBox8.TabStop = false;
            // 
            // btnInstall
            // 
            this.btnInstall.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnInstall.Image = global::App.ControlPanel.Properties.Resources.CD_16x;
            this.btnInstall.Location = new System.Drawing.Point(408, 360);
            this.btnInstall.Name = "btnInstall";
            this.btnInstall.Size = new System.Drawing.Size(126, 28);
            this.btnInstall.TabIndex = 90;
            this.btnInstall.Text = "Install/Upgrade";
            this.btnInstall.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnInstall.UseVisualStyleBackColor = true;
            this.btnInstall.Click += new System.EventHandler(this.btnInstall_Click);
            // 
            // lstLog
            // 
            this.lstLog.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lstLog.Columns.AddRange(new System.Windows.Forms.ColumnHeader[] {
            this.columnHeader1});
            this.lstLog.HideSelection = false;
            this.lstLog.Items.AddRange(new System.Windows.Forms.ListViewItem[] {
            listViewItem1,
            listViewItem2});
            this.lstLog.Location = new System.Drawing.Point(0, 171);
            this.lstLog.Name = "lstLog";
            this.lstLog.Size = new System.Drawing.Size(534, 183);
            this.lstLog.SmallImageList = this.imageListIssues;
            this.lstLog.TabIndex = 83;
            this.lstLog.UseCompatibleStateImageBehavior = false;
            this.lstLog.View = System.Windows.Forms.View.Details;
            // 
            // columnHeader1
            // 
            this.columnHeader1.Text = "Message";
            this.columnHeader1.Width = 479;
            // 
            // imageListIssues
            // 
            this.imageListIssues.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageListIssues.ImageStream")));
            this.imageListIssues.TransparentColor = System.Drawing.Color.Transparent;
            this.imageListIssues.Images.SetKeyName(0, "Exclamation_16x.png");
            this.imageListIssues.Images.SetKeyName(1, "InformationSymbol_16x.png");
            // 
            // InstallSolutionControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.btnRunTests);
            this.Controls.Add(this.btnCopyLog);
            this.Controls.Add(this.grpTasks);
            this.Controls.Add(this.lblGUIInstallDesc);
            this.Controls.Add(this.lblGUIInstallHeader);
            this.Controls.Add(this.pictureBox8);
            this.Controls.Add(this.btnInstall);
            this.Controls.Add(this.lstLog);
            this.Name = "InstallSolutionControl";
            this.Size = new System.Drawing.Size(537, 394);
            this.Load += new System.EventHandler(this.InstallSolutionControl_Load);
            this.grpTasks.ResumeLayout(false);
            this.grpTasks.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox8)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnRunTests;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.GroupBox grpTasks;
        private System.Windows.Forms.CheckBox chkInstallOptionAllowUsageStats;
        private System.Windows.Forms.CheckBox chkInstallOptionOpenAdminSite;
        private System.Windows.Forms.CheckBox chkInstallOptionUpgradeDB;
        private System.Windows.Forms.CheckBox chkInstallOptionWebJobs;
        private System.Windows.Forms.Label lblGUIInstallDesc;
        private System.Windows.Forms.Label lblGUIInstallHeader;
        private System.Windows.Forms.PictureBox pictureBox8;
        private System.Windows.Forms.Button btnInstall;
        private System.Windows.Forms.ListView lstLog;
        private System.Windows.Forms.ColumnHeader columnHeader1;
        private System.Windows.Forms.ImageList imageListIssues;
    }
}
