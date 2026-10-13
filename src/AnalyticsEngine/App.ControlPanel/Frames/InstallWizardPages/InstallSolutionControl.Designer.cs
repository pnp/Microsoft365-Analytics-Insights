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
            this.btnCancel = new System.Windows.Forms.Button();
            this.grpTasks = new System.Windows.Forms.GroupBox();
            this.chkInstallOptionAllowUsageStats = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionOpenAdminSite = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionUpgradeDB = new System.Windows.Forms.CheckBox();
            this.chkInstallOptionWebJobs = new System.Windows.Forms.CheckBox();
            this.lblGUIInstallDesc = new System.Windows.Forms.Label();
            this.lblGUIInstallHeader = new System.Windows.Forms.Label();
            this.pictureBox8 = new System.Windows.Forms.PictureBox();
            this.btnInstall = new System.Windows.Forms.Button();
            this.lstLog = new App.ControlPanel.Controls.BufferedListView();
            this.columnHeader1 = ((System.Windows.Forms.ColumnHeader)(new System.Windows.Forms.ColumnHeader()));
            this.imageListIssues = new System.Windows.Forms.ImageList(this.components);
            this.installLayout = new System.Windows.Forms.TableLayoutPanel();
            this.headerLayout = new System.Windows.Forms.TableLayoutPanel();
            this.tasksLayout = new System.Windows.Forms.TableLayoutPanel();
            this.actionLayout = new System.Windows.Forms.FlowLayoutPanel();
            this.lblDeploymentNotice = new System.Windows.Forms.Label();
            this.installLayout.SuspendLayout();
            this.headerLayout.SuspendLayout();
            this.tasksLayout.SuspendLayout();
            this.actionLayout.SuspendLayout();
            this.grpTasks.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox8)).BeginInit();
            this.SuspendLayout();
            // 
            // btnRunTests
            // 
            this.btnRunTests.AutoSize = true;
            this.btnRunTests.MinimumSize = new System.Drawing.Size(150, 34);
            this.btnRunTests.Padding = new System.Windows.Forms.Padding(8, 4, 8, 4);
            this.btnRunTests.AccessibleDescription = "Check the configured resources and permissions without starting installation.";
            this.btnRunTests.Image = ((System.Drawing.Image)(resources.GetObject("btnRunTests.Image")));
            this.btnRunTests.Location = new System.Drawing.Point(276, 360);
            this.btnRunTests.Name = "btnRunTests";
            this.btnRunTests.Size = new System.Drawing.Size(126, 28);
            this.btnRunTests.TabIndex = 1;
            this.btnRunTests.Text = "&Test Configuration";
            this.btnRunTests.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnRunTests.UseVisualStyleBackColor = true;
            this.btnRunTests.Click += new System.EventHandler(this.btnRunTests_Click);
            // 
            // btnCopyLog
            // 
            this.btnCopyLog.AutoSize = true;
            this.btnCopyLog.MinimumSize = new System.Drawing.Size(150, 34);
            this.btnCopyLog.Padding = new System.Windows.Forms.Padding(8, 4, 8, 4);
            this.btnCopyLog.Image = global::App.ControlPanel.Properties.Resources.Copy_16x;
            this.btnCopyLog.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnCopyLog.Location = new System.Drawing.Point(0, 360);
            this.btnCopyLog.Name = "btnCopyLog";
            this.btnCopyLog.Size = new System.Drawing.Size(126, 28);
            this.btnCopyLog.TabIndex = 0;
            this.btnCopyLog.Text = "&Copy full log";
            this.btnCopyLog.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnCopyLog.UseVisualStyleBackColor = true;
            this.btnCopyLog.Click += new System.EventHandler(this.btnCopyLog_Click);
            // 
            // grpTasks
            // 
            this.grpTasks.AutoSize = true;
            this.grpTasks.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.grpTasks.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpTasks.Padding = new System.Windows.Forms.Padding(12);
            this.grpTasks.Controls.Add(this.tasksLayout);
            this.grpTasks.Location = new System.Drawing.Point(0, 88);
            this.grpTasks.Name = "grpTasks";
            this.grpTasks.Size = new System.Drawing.Size(534, 77);
            this.grpTasks.TabIndex = 0;
            this.grpTasks.TabStop = false;
            this.grpTasks.Text = "Installation options";
            // 
            // chkInstallOptionAllowUsageStats
            // 
            this.chkInstallOptionAllowUsageStats.AutoSize = true;
            this.chkInstallOptionAllowUsageStats.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkInstallOptionAllowUsageStats.Margin = new System.Windows.Forms.Padding(0, 4, 0, 4);
            this.chkInstallOptionAllowUsageStats.Location = new System.Drawing.Point(6, 42);
            this.chkInstallOptionAllowUsageStats.Name = "chkInstallOptionAllowUsageStats";
            this.chkInstallOptionAllowUsageStats.Size = new System.Drawing.Size(316, 17);
            this.chkInstallOptionAllowUsageStats.TabIndex = 3;
            this.chkInstallOptionAllowUsageStats.Text = "Allow anonymous usage reporting to help improve the product";
            this.chkInstallOptionAllowUsageStats.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionOpenAdminSite
            // 
            this.chkInstallOptionOpenAdminSite.AutoSize = true;
            this.chkInstallOptionOpenAdminSite.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkInstallOptionOpenAdminSite.Margin = new System.Windows.Forms.Padding(0, 4, 0, 4);
            this.chkInstallOptionOpenAdminSite.Location = new System.Drawing.Point(334, 19);
            this.chkInstallOptionOpenAdminSite.Name = "chkInstallOptionOpenAdminSite";
            this.chkInstallOptionOpenAdminSite.Size = new System.Drawing.Size(234, 17);
            this.chkInstallOptionOpenAdminSite.TabIndex = 2;
            this.chkInstallOptionOpenAdminSite.Text = "Open administration website after installation";
            this.chkInstallOptionOpenAdminSite.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionUpgradeDB
            // 
            this.chkInstallOptionUpgradeDB.AutoSize = true;
            this.chkInstallOptionUpgradeDB.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkInstallOptionUpgradeDB.Margin = new System.Windows.Forms.Padding(0, 4, 0, 4);
            this.chkInstallOptionUpgradeDB.Location = new System.Drawing.Point(334, 42);
            this.chkInstallOptionUpgradeDB.Name = "chkInstallOptionUpgradeDB";
            this.chkInstallOptionUpgradeDB.Size = new System.Drawing.Size(172, 17);
            this.chkInstallOptionUpgradeDB.TabIndex = 1;
            this.chkInstallOptionUpgradeDB.Text = "Upgrade SQL Database schema (if necessary)";
            this.chkInstallOptionUpgradeDB.UseVisualStyleBackColor = true;
            // 
            // chkInstallOptionWebJobs
            // 
            this.chkInstallOptionWebJobs.AutoSize = true;
            this.chkInstallOptionWebJobs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkInstallOptionWebJobs.Margin = new System.Windows.Forms.Padding(0, 4, 0, 4);
            this.chkInstallOptionWebJobs.Location = new System.Drawing.Point(6, 19);
            this.chkInstallOptionWebJobs.Name = "chkInstallOptionWebJobs";
            this.chkInstallOptionWebJobs.Size = new System.Drawing.Size(206, 17);
            this.chkInstallOptionWebJobs.TabIndex = 0;
            this.chkInstallOptionWebJobs.Text = "Update solution with latest release";
            this.chkInstallOptionWebJobs.UseVisualStyleBackColor = true;
            // 
            // lblGUIInstallDesc
            // 
            this.lblGUIInstallDesc.AutoSize = true;
            this.lblGUIInstallDesc.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblGUIInstallDesc.Margin = new System.Windows.Forms.Padding(12, 4, 0, 0);
            this.lblGUIInstallDesc.Location = new System.Drawing.Point(62, 22);
            this.lblGUIInstallDesc.Name = "lblGUIInstallDesc";
            this.lblGUIInstallDesc.Size = new System.Drawing.Size(472, 42);
            this.lblGUIInstallDesc.TabIndex = 86;
            this.lblGUIInstallDesc.Text = resources.GetString("lblGUIInstallDesc.Text");
            // 
            // lblGUIInstallHeader
            // 
            this.lblGUIInstallHeader.AutoSize = true;
            this.lblGUIInstallHeader.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblGUIInstallHeader.Margin = new System.Windows.Forms.Padding(12, 0, 0, 0);
            this.lblGUIInstallHeader.Font = new System.Drawing.Font("Segoe UI", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIInstallHeader.Location = new System.Drawing.Point(-4, 0);
            this.lblGUIInstallHeader.Name = "lblGUIInstallHeader";
            this.lblGUIInstallHeader.Size = new System.Drawing.Size(172, 19);
            this.lblGUIInstallHeader.TabIndex = 85;
            this.lblGUIInstallHeader.Text = "Review and deploy";
            // 
            // pictureBox8
            // 
            this.pictureBox8.Image = global::App.ControlPanel.Properties.Resources.Powershell_script_file;
            this.pictureBox8.Location = new System.Drawing.Point(0, 22);
            this.pictureBox8.Name = "pictureBox8";
            this.pictureBox8.Size = new System.Drawing.Size(56, 56);
            this.pictureBox8.SizeMode = System.Windows.Forms.PictureBoxSizeMode.Zoom;
            this.pictureBox8.TabIndex = 84;
            this.pictureBox8.TabStop = false;
            // 
            // btnInstall
            // 
            this.btnInstall.AutoSize = true;
            this.btnInstall.MinimumSize = new System.Drawing.Size(150, 34);
            this.btnInstall.Padding = new System.Windows.Forms.Padding(8, 4, 8, 4);
            this.btnInstall.AccessibleDescription = "Start deployment. Creates or updates Azure resources, SharePoint components and the selected database schema.";
            this.btnInstall.Image = global::App.ControlPanel.Properties.Resources.CD_16x;
            this.btnInstall.Location = new System.Drawing.Point(408, 360);
            this.btnInstall.Name = "btnInstall";
            this.btnInstall.Size = new System.Drawing.Size(126, 28);
            this.btnInstall.TabIndex = 2;
            this.btnInstall.Text = "&Review deployment...";
            this.btnInstall.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnInstall.UseVisualStyleBackColor = true;
            this.btnInstall.Click += new System.EventHandler(this.btnInstall_Click);
            // 
            // btnCancel
            // 
            this.btnCancel.AutoSize = true;
            this.btnCancel.MinimumSize = new System.Drawing.Size(150, 34);
            this.btnCancel.Padding = new System.Windows.Forms.Padding(8, 4, 8, 4);
            this.btnCancel.Image = global::App.ControlPanel.Properties.Resources.Cancel_16x;
            this.btnCancel.ImageAlign = System.Drawing.ContentAlignment.MiddleLeft;
            this.btnCancel.Location = new System.Drawing.Point(408, 360);
            this.btnCancel.Name = "btnCancel";
            this.btnCancel.Size = new System.Drawing.Size(126, 28);
            this.btnCancel.TabIndex = 2;
            this.btnCancel.Text = "Cancel &operation";
            this.btnCancel.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnCancel.UseVisualStyleBackColor = true;
            this.btnCancel.Visible = false;
            this.btnCancel.Click += new System.EventHandler(this.btnCancel_Click);
            // 
            // lstLog
            // 
            this.lstLog.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lstLog.AccessibleName = "Installation and configuration test log";
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
            this.lstLog.TabIndex = 1;
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
            // installLayout
            //
            this.installLayout.AutoSize = true;
            this.installLayout.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.installLayout.ColumnCount = 1;
            this.installLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.installLayout.Controls.Add(this.headerLayout, 0, 0);
            this.installLayout.Controls.Add(this.grpTasks, 0, 1);
            this.installLayout.Controls.Add(this.lblDeploymentNotice, 0, 2);
            this.installLayout.Controls.Add(this.lstLog, 0, 3);
            this.installLayout.Controls.Add(this.actionLayout, 0, 4);
            this.installLayout.Dock = System.Windows.Forms.DockStyle.Top;
            this.installLayout.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            this.installLayout.Name = "installLayout";
            this.installLayout.Padding = new System.Windows.Forms.Padding(12);
            this.installLayout.RowCount = 5;
            this.installLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.installLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.installLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.installLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.installLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.installLayout.TabIndex = 0;
            //
            // headerLayout
            //
            this.headerLayout.AutoSize = true;
            this.headerLayout.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.headerLayout.ColumnCount = 2;
            this.headerLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.AutoSize));
            this.headerLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.headerLayout.Controls.Add(this.pictureBox8, 0, 0);
            this.headerLayout.SetRowSpan(this.pictureBox8, 2);
            this.headerLayout.Controls.Add(this.lblGUIInstallHeader, 1, 0);
            this.headerLayout.Controls.Add(this.lblGUIInstallDesc, 1, 1);
            this.headerLayout.Dock = System.Windows.Forms.DockStyle.Fill;
            this.headerLayout.Margin = new System.Windows.Forms.Padding(0, 0, 0, 12);
            this.headerLayout.Name = "headerLayout";
            this.headerLayout.RowCount = 2;
            this.headerLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.headerLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            //
            // tasksLayout
            //
            this.tasksLayout.AutoSize = true;
            this.tasksLayout.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.tasksLayout.ColumnCount = 1;
            this.tasksLayout.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tasksLayout.Controls.Add(this.chkInstallOptionWebJobs, 0, 0);
            this.tasksLayout.Controls.Add(this.chkInstallOptionUpgradeDB, 0, 1);
            this.tasksLayout.Controls.Add(this.chkInstallOptionOpenAdminSite, 0, 2);
            this.tasksLayout.Controls.Add(this.chkInstallOptionAllowUsageStats, 0, 3);
            this.tasksLayout.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tasksLayout.Name = "tasksLayout";
            this.tasksLayout.RowCount = 4;
            this.tasksLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.tasksLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.tasksLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.tasksLayout.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.AutoSize));
            this.tasksLayout.TabIndex = 0;
            //
            // lblDeploymentNotice
            //
            this.lblDeploymentNotice.AutoSize = true;
            this.lblDeploymentNotice.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblDeploymentNotice.Margin = new System.Windows.Forms.Padding(3, 8, 3, 12);
            this.lblDeploymentNotice.Name = "lblDeploymentNotice";
            this.lblDeploymentNotice.Text = resources.GetString("lblDeploymentNotice.Text");
            //
            // actionLayout
            //
            this.actionLayout.AutoSize = true;
            this.actionLayout.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.actionLayout.Controls.Add(this.btnCancel);
            this.actionLayout.Controls.Add(this.btnInstall);
            this.actionLayout.Controls.Add(this.btnRunTests);
            this.actionLayout.Controls.Add(this.btnCopyLog);
            this.actionLayout.Dock = System.Windows.Forms.DockStyle.Fill;
            this.actionLayout.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.actionLayout.Margin = new System.Windows.Forms.Padding(0, 8, 0, 0);
            this.actionLayout.Name = "actionLayout";
            this.actionLayout.TabIndex = 2;
            // 
            // InstallSolutionControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoScroll = true;
            this.Controls.Add(this.installLayout);
            this.Name = "InstallSolutionControl";
            this.Size = new System.Drawing.Size(537, 394);
            this.Load += new System.EventHandler(this.InstallSolutionControl_Load);
            this.grpTasks.ResumeLayout(false);
            this.grpTasks.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox8)).EndInit();
            this.installLayout.ResumeLayout(false);
            this.installLayout.PerformLayout();
            this.headerLayout.ResumeLayout(false);
            this.headerLayout.PerformLayout();
            this.tasksLayout.ResumeLayout(false);
            this.tasksLayout.PerformLayout();
            this.actionLayout.ResumeLayout(false);
            this.actionLayout.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.Button btnRunTests;
        private System.Windows.Forms.Button btnCopyLog;
        private System.Windows.Forms.Button btnCancel;
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
        private System.Windows.Forms.TableLayoutPanel installLayout;
        private System.Windows.Forms.TableLayoutPanel headerLayout;
        private System.Windows.Forms.TableLayoutPanel tasksLayout;
        private System.Windows.Forms.FlowLayoutPanel actionLayout;
        private System.Windows.Forms.Label lblDeploymentNotice;
    }
}
