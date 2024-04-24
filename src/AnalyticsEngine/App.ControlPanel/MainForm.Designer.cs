namespace App.ControlPanel
{
    partial class MainForm
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

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.menu = new System.Windows.Forms.MenuStrip();
            this.mnuFile = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuNewConfig = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuOpenConfig = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuSaveConfig = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuSaveConfigAs = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuFileSep = new System.Windows.Forms.ToolStripSeparator();
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuExit = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuWindow = new System.Windows.Forms.ToolStripMenuItem();
            this.proxyConfigToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.mnuDebug = new System.Windows.Forms.ToolStripMenuItem();
            this.clearSettingsToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.helpToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.aboutToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.openFileDialog = new System.Windows.Forms.OpenFileDialog();
            this.saveFileDialog = new System.Windows.Forms.SaveFileDialog();
            this.statusStrip = new System.Windows.Forms.StatusStrip();
            this.txtConfigFile = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripLoading = new System.Windows.Forms.ToolStripProgressBar();
            this.grpInstall = new System.Windows.Forms.GroupBox();
            this.lblIntroText = new System.Windows.Forms.Label();
            this.chkDisclaimer = new System.Windows.Forms.CheckBox();
            this.btnStartInstall = new System.Windows.Forms.Button();
            this.lblInstallDesc = new System.Windows.Forms.Label();
            this.label1 = new System.Windows.Forms.Label();
            this.grpStart = new System.Windows.Forms.GroupBox();
            this.label2 = new System.Windows.Forms.Label();
            this.solutionTestsConfigurationToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.installSPOSitesControl = new App.ControlPanel.Frames.InstallSPOSitesControl();
            this.menu.SuspendLayout();
            this.statusStrip.SuspendLayout();
            this.grpInstall.SuspendLayout();
            this.grpStart.SuspendLayout();
            this.SuspendLayout();
            // 
            // menu
            // 
            this.menu.ImageScalingSize = new System.Drawing.Size(32, 32);
            this.menu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.mnuFile,
            this.mnuWindow,
            this.mnuDebug,
            this.helpToolStripMenuItem});
            this.menu.Location = new System.Drawing.Point(0, 0);
            this.menu.Name = "menu";
            this.menu.Padding = new System.Windows.Forms.Padding(4, 1, 0, 1);
            this.menu.Size = new System.Drawing.Size(679, 24);
            this.menu.TabIndex = 2;
            this.menu.Text = "menuStrip1";
            // 
            // mnuFile
            // 
            this.mnuFile.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.mnuNewConfig,
            this.mnuOpenConfig,
            this.mnuSaveConfig,
            this.mnuSaveConfigAs,
            this.mnuFileSep,
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem,
            this.mnuExit});
            this.mnuFile.Name = "mnuFile";
            this.mnuFile.Size = new System.Drawing.Size(37, 22);
            this.mnuFile.Text = "&File";
            // 
            // mnuNewConfig
            // 
            this.mnuNewConfig.Enabled = false;
            this.mnuNewConfig.Name = "mnuNewConfig";
            this.mnuNewConfig.Size = new System.Drawing.Size(215, 22);
            this.mnuNewConfig.Text = "New Configuration";
            this.mnuNewConfig.Click += new System.EventHandler(this.mnuNewConfig_Click);
            // 
            // mnuOpenConfig
            // 
            this.mnuOpenConfig.Enabled = false;
            this.mnuOpenConfig.Name = "mnuOpenConfig";
            this.mnuOpenConfig.Size = new System.Drawing.Size(215, 22);
            this.mnuOpenConfig.Text = "&Open Configuration File";
            this.mnuOpenConfig.Click += new System.EventHandler(this.mnuOpenConfig_Click);
            // 
            // mnuSaveConfig
            // 
            this.mnuSaveConfig.Name = "mnuSaveConfig";
            this.mnuSaveConfig.Size = new System.Drawing.Size(215, 22);
            this.mnuSaveConfig.Text = "Save Configuration";
            this.mnuSaveConfig.Click += new System.EventHandler(this.mnuSaveConfig_Click);
            // 
            // mnuSaveConfigAs
            // 
            this.mnuSaveConfigAs.Enabled = false;
            this.mnuSaveConfigAs.Name = "mnuSaveConfigAs";
            this.mnuSaveConfigAs.Size = new System.Drawing.Size(215, 22);
            this.mnuSaveConfigAs.Text = "Save Configuration As";
            this.mnuSaveConfigAs.Click += new System.EventHandler(this.mnuSaveConfigAsNewFile_Click);
            // 
            // mnuFileSep
            // 
            this.mnuFileSep.Name = "mnuFileSep";
            this.mnuFileSep.Size = new System.Drawing.Size(212, 6);
            // 
            // mnuUpgradeDatabaseSchemaToolStripMenuItem
            // 
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem.Name = "mnuUpgradeDatabaseSchemaToolStripMenuItem";
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem.Size = new System.Drawing.Size(215, 22);
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem.Text = "Upgrade Database Schema";
            this.mnuUpgradeDatabaseSchemaToolStripMenuItem.Click += new System.EventHandler(this.upgradeDatabaseSchemaToolStripMenuItem_Click);
            // 
            // mnuExit
            // 
            this.mnuExit.Name = "mnuExit";
            this.mnuExit.Size = new System.Drawing.Size(215, 22);
            this.mnuExit.Text = "&Exit";
            this.mnuExit.Click += new System.EventHandler(this.exitToolStripMenuItem_Click);
            // 
            // mnuWindow
            // 
            this.mnuWindow.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.proxyConfigToolStripMenuItem,
            this.solutionTestsConfigurationToolStripMenuItem});
            this.mnuWindow.Name = "mnuWindow";
            this.mnuWindow.Size = new System.Drawing.Size(63, 22);
            this.mnuWindow.Text = "Window";
            this.mnuWindow.Visible = false;
            // 
            // proxyConfigToolStripMenuItem
            // 
            this.proxyConfigToolStripMenuItem.Name = "proxyConfigToolStripMenuItem";
            this.proxyConfigToolStripMenuItem.Size = new System.Drawing.Size(223, 22);
            this.proxyConfigToolStripMenuItem.Text = "Proxy Configuration";
            this.proxyConfigToolStripMenuItem.Click += new System.EventHandler(this.proxyConfigToolStripMenuItem_Click);
            // 
            // mnuDebug
            // 
            this.mnuDebug.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.clearSettingsToolStripMenuItem});
            this.mnuDebug.Name = "mnuDebug";
            this.mnuDebug.Size = new System.Drawing.Size(54, 22);
            this.mnuDebug.Text = "Debug";
            this.mnuDebug.Visible = false;
            // 
            // clearSettingsToolStripMenuItem
            // 
            this.clearSettingsToolStripMenuItem.Name = "clearSettingsToolStripMenuItem";
            this.clearSettingsToolStripMenuItem.Size = new System.Drawing.Size(146, 22);
            this.clearSettingsToolStripMenuItem.Text = "Clear Settings";
            this.clearSettingsToolStripMenuItem.Click += new System.EventHandler(this.clearSettingsToolStripMenuItem_Click);
            // 
            // helpToolStripMenuItem
            // 
            this.helpToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.aboutToolStripMenuItem});
            this.helpToolStripMenuItem.Name = "helpToolStripMenuItem";
            this.helpToolStripMenuItem.Size = new System.Drawing.Size(44, 22);
            this.helpToolStripMenuItem.Text = "Help";
            // 
            // aboutToolStripMenuItem
            // 
            this.aboutToolStripMenuItem.Name = "aboutToolStripMenuItem";
            this.aboutToolStripMenuItem.Size = new System.Drawing.Size(107, 22);
            this.aboutToolStripMenuItem.Text = "About";
            this.aboutToolStripMenuItem.Click += new System.EventHandler(this.aboutToolStripMenuItem_Click);
            // 
            // openFileDialog
            // 
            this.openFileDialog.Filter = "JSon file|*.json|All files|*.*";
            this.openFileDialog.ShowHelp = true;
            this.openFileDialog.Title = "Load Configuration Details";
            // 
            // saveFileDialog
            // 
            this.saveFileDialog.Filter = "JSon file|*.json|All files|*.*";
            this.saveFileDialog.Title = "Save Configuration Details";
            // 
            // statusStrip
            // 
            this.statusStrip.ImageScalingSize = new System.Drawing.Size(32, 32);
            this.statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.txtConfigFile,
            this.toolStripLoading});
            this.statusStrip.Location = new System.Drawing.Point(0, 599);
            this.statusStrip.Name = "statusStrip";
            this.statusStrip.Size = new System.Drawing.Size(679, 24);
            this.statusStrip.TabIndex = 8;
            this.statusStrip.Text = "statusStrip1";
            // 
            // txtConfigFile
            // 
            this.txtConfigFile.Name = "txtConfigFile";
            this.txtConfigFile.Size = new System.Drawing.Size(562, 19);
            this.txtConfigFile.Spring = true;
            this.txtConfigFile.Text = "txtConfigFile";
            this.txtConfigFile.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // toolStripLoading
            // 
            this.toolStripLoading.Name = "toolStripLoading";
            this.toolStripLoading.Size = new System.Drawing.Size(100, 18);
            this.toolStripLoading.Style = System.Windows.Forms.ProgressBarStyle.Marquee;
            // 
            // grpInstall
            // 
            this.grpInstall.Controls.Add(this.installSPOSitesControl);
            this.grpInstall.Location = new System.Drawing.Point(124, 364);
            this.grpInstall.Name = "grpInstall";
            this.grpInstall.Size = new System.Drawing.Size(492, 185);
            this.grpInstall.TabIndex = 10;
            this.grpInstall.TabStop = false;
            // 
            // lblIntroText
            // 
            this.lblIntroText.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.lblIntroText.Location = new System.Drawing.Point(23, 32);
            this.lblIntroText.Name = "lblIntroText";
            this.lblIntroText.Size = new System.Drawing.Size(535, 47);
            this.lblIntroText.TabIndex = 4;
            this.lblIntroText.Text = resources.GetString("lblIntroText.Text");
            // 
            // chkDisclaimer
            // 
            this.chkDisclaimer.AutoSize = true;
            this.chkDisclaimer.Location = new System.Drawing.Point(26, 181);
            this.chkDisclaimer.Name = "chkDisclaimer";
            this.chkDisclaimer.Size = new System.Drawing.Size(239, 17);
            this.chkDisclaimer.TabIndex = 0;
            this.chkDisclaimer.Text = "I accept that using this tool is at my own risk. ";
            this.chkDisclaimer.UseVisualStyleBackColor = true;
            this.chkDisclaimer.CheckedChanged += new System.EventHandler(this.chkDisclaimer_CheckedChanged);
            // 
            // btnStartInstall
            // 
            this.btnStartInstall.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnStartInstall.Image = global::App.ControlPanel.Properties.Resources.CD_16x;
            this.btnStartInstall.Location = new System.Drawing.Point(458, 232);
            this.btnStartInstall.Name = "btnStartInstall";
            this.btnStartInstall.Size = new System.Drawing.Size(100, 35);
            this.btnStartInstall.TabIndex = 1;
            this.btnStartInstall.Text = "Install Solution";
            this.btnStartInstall.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageBeforeText;
            this.btnStartInstall.UseVisualStyleBackColor = true;
            this.btnStartInstall.Click += new System.EventHandler(this.btnStartInstall_Click);
            // 
            // lblInstallDesc
            // 
            this.lblInstallDesc.AutoSize = true;
            this.lblInstallDesc.Location = new System.Drawing.Point(23, 243);
            this.lblInstallDesc.Name = "lblInstallDesc";
            this.lblInstallDesc.Size = new System.Drawing.Size(327, 13);
            this.lblInstallDesc.TabIndex = 7;
            this.lblInstallDesc.Text = "Click this to install/update the solution or complete a previous setup.";
            // 
            // label1
            // 
            this.label1.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.label1.Location = new System.Drawing.Point(23, 124);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(535, 42);
            this.label1.TabIndex = 8;
            this.label1.Text = "This system comes with absolutely no guarantees - run at your own risk and ensure" +
    " correct permissions to guarantee safe access to Office 365 and Azure. ";
            // 
            // grpStart
            // 
            this.grpStart.Controls.Add(this.label2);
            this.grpStart.Controls.Add(this.label1);
            this.grpStart.Controls.Add(this.lblInstallDesc);
            this.grpStart.Controls.Add(this.btnStartInstall);
            this.grpStart.Controls.Add(this.chkDisclaimer);
            this.grpStart.Controls.Add(this.lblIntroText);
            this.grpStart.Location = new System.Drawing.Point(63, 74);
            this.grpStart.Name = "grpStart";
            this.grpStart.Size = new System.Drawing.Size(564, 274);
            this.grpStart.TabIndex = 9;
            this.grpStart.TabStop = false;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.label2.Location = new System.Drawing.Point(23, 108);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(71, 13);
            this.label2.TabIndex = 9;
            this.label2.Text = "WARNING:";
            // 
            // solutionTestsConfigurationToolStripMenuItem
            // 
            this.solutionTestsConfigurationToolStripMenuItem.Name = "solutionTestsConfigurationToolStripMenuItem";
            this.solutionTestsConfigurationToolStripMenuItem.Size = new System.Drawing.Size(223, 22);
            this.solutionTestsConfigurationToolStripMenuItem.Text = "Solution Tests Configuration";
            this.solutionTestsConfigurationToolStripMenuItem.Click += new System.EventHandler(this.solutionTestsConfigurationToolStripMenuItem_Click);
            // 
            // installSPOSitesControl
            // 
            this.installSPOSitesControl.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.installSPOSitesControl.FtpConfig = null;
            this.installSPOSitesControl.Location = new System.Drawing.Point(10, 19);
            this.installSPOSitesControl.Margin = new System.Windows.Forms.Padding(6);
            this.installSPOSitesControl.Name = "installSPOSitesControl";
            this.installSPOSitesControl.Size = new System.Drawing.Size(476, 160);
            this.installSPOSitesControl.TabIndex = 0;
            this.installSPOSitesControl.TestsConfig = null;
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(679, 623);
            this.Controls.Add(this.grpInstall);
            this.Controls.Add(this.grpStart);
            this.Controls.Add(this.statusStrip);
            this.Controls.Add(this.menu);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.menu;
            this.Name = "MainForm";
            this.Text = "MainForm";
            this.Load += new System.EventHandler(this.StartForm_Load);
            this.menu.ResumeLayout(false);
            this.menu.PerformLayout();
            this.statusStrip.ResumeLayout(false);
            this.statusStrip.PerformLayout();
            this.grpInstall.ResumeLayout(false);
            this.grpStart.ResumeLayout(false);
            this.grpStart.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.MenuStrip menu;
        private System.Windows.Forms.ToolStripMenuItem mnuWindow;
        private System.Windows.Forms.ToolStripMenuItem mnuUpgradeDatabaseSchemaToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem mnuDebug;
        private System.Windows.Forms.ToolStripMenuItem clearSettingsToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem mnuFile;
        private System.Windows.Forms.ToolStripMenuItem mnuOpenConfig;
        private System.Windows.Forms.ToolStripMenuItem mnuSaveConfigAs;
        private System.Windows.Forms.ToolStripSeparator mnuFileSep;
        private System.Windows.Forms.ToolStripMenuItem mnuExit;
        private System.Windows.Forms.OpenFileDialog openFileDialog;
        private System.Windows.Forms.SaveFileDialog saveFileDialog;
        private System.Windows.Forms.ToolStripMenuItem mnuSaveConfig;
        private System.Windows.Forms.ToolStripMenuItem mnuNewConfig;
        private System.Windows.Forms.StatusStrip statusStrip;
        private System.Windows.Forms.ToolStripStatusLabel txtConfigFile;
        private System.Windows.Forms.ToolStripProgressBar toolStripLoading;
        private System.Windows.Forms.ToolStripMenuItem helpToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem aboutToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem proxyConfigToolStripMenuItem;
        private System.Windows.Forms.GroupBox grpInstall;
        private Frames.InstallSPOSitesControl installSPOSitesControl;
        private System.Windows.Forms.Label lblIntroText;
        private System.Windows.Forms.CheckBox chkDisclaimer;
        private System.Windows.Forms.Button btnStartInstall;
        private System.Windows.Forms.Label lblInstallDesc;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.GroupBox grpStart;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.ToolStripMenuItem solutionTestsConfigurationToolStripMenuItem;
    }
}

