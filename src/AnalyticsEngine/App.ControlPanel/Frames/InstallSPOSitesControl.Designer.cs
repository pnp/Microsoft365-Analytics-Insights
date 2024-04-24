namespace App.ControlPanel.Frames
{
    partial class InstallSPOSitesControl
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
            App.ControlPanel.Engine.Entities.InstallTasksConfig installTasksConfig2 = new App.ControlPanel.Engine.Entities.InstallTasksConfig();
            this.tabs = new System.Windows.Forms.TabControl();
            this.tabTargets = new System.Windows.Forms.TabPage();
            this.importJobSettingsSelection = new App.ControlPanel.Controls.TargetSolutionConfigControl();
            this.tabCredentials = new System.Windows.Forms.TabPage();
            this.systemCredentialsControl1 = new App.ControlPanel.Frames.InstallWizard.SystemCredentialsControl();
            this.tabPageAzureConfig = new System.Windows.Forms.TabPage();
            this.azureBaseConfigControl1 = new App.ControlPanel.Frames.InstallWizardPages.AzureBaseConfigControl();
            this.tabAzureResources = new System.Windows.Forms.TabPage();
            this.azurePaaSConfigControl1 = new App.ControlPanel.Frames.InstallWizard.AzurePaaSConfigControl();
            this.tabAzureStorage = new System.Windows.Forms.TabPage();
            this.azureStorageConfigControl1 = new App.ControlPanel.Frames.InstallWizard.AzureStorageConfigControl();
            this.tabSharePoint = new System.Windows.Forms.TabPage();
            this.sharePointConfigControl1 = new App.ControlPanel.Frames.InstallWizard.SharePointConfigControl();
            this.tabSources = new System.Windows.Forms.TabPage();
            this.grpLocalSources = new System.Windows.Forms.GroupBox();
            this.fileSelectionWebsite = new App.ControlPanel.Controls.FileSelection();
            this.fileSelectionControlPanel = new App.ControlPanel.Controls.FileSelection();
            this.fileSelectionWebjobAppInsights = new App.ControlPanel.Controls.FileSelection();
            this.fileSelectionWebjobActivity = new App.ControlPanel.Controls.FileSelection();
            this.fileSelectionAITracker = new App.ControlPanel.Controls.FileSelection();
            this.lblGUIAppSourcesDesc = new System.Windows.Forms.Label();
            this.lblGUIAppSourcesHeader = new System.Windows.Forms.Label();
            this.pictureBox12 = new System.Windows.Forms.PictureBox();
            this.rdpSpecificLocation = new System.Windows.Forms.RadioButton();
            this.rdbLatest = new System.Windows.Forms.RadioButton();
            this.tabInstall = new System.Windows.Forms.TabPage();
            this.installSolutionControl1 = new App.ControlPanel.Frames.InstallWizard.InstallSolutionControl();
            this.installerBackgroundWorker = new System.ComponentModel.BackgroundWorker();
            this.btnNext = new System.Windows.Forms.Button();
            this.tabs.SuspendLayout();
            this.tabTargets.SuspendLayout();
            this.tabCredentials.SuspendLayout();
            this.tabPageAzureConfig.SuspendLayout();
            this.tabAzureResources.SuspendLayout();
            this.tabAzureStorage.SuspendLayout();
            this.tabSharePoint.SuspendLayout();
            this.tabSources.SuspendLayout();
            this.grpLocalSources.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox12)).BeginInit();
            this.tabInstall.SuspendLayout();
            this.SuspendLayout();
            // 
            // tabs
            // 
            this.tabs.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabs.Controls.Add(this.tabTargets);
            this.tabs.Controls.Add(this.tabCredentials);
            this.tabs.Controls.Add(this.tabPageAzureConfig);
            this.tabs.Controls.Add(this.tabAzureResources);
            this.tabs.Controls.Add(this.tabAzureStorage);
            this.tabs.Controls.Add(this.tabSharePoint);
            this.tabs.Controls.Add(this.tabSources);
            this.tabs.Controls.Add(this.tabInstall);
            this.tabs.Location = new System.Drawing.Point(0, 0);
            this.tabs.Name = "tabs";
            this.tabs.SelectedIndex = 0;
            this.tabs.Size = new System.Drawing.Size(646, 569);
            this.tabs.TabIndex = 29;
            this.tabs.SelectedIndexChanged += new System.EventHandler(this.tabs_SelectedIndexChanged);
            // 
            // tabTargets
            // 
            this.tabTargets.Controls.Add(this.importJobSettingsSelection);
            this.tabTargets.Location = new System.Drawing.Point(4, 22);
            this.tabTargets.Name = "tabTargets";
            this.tabTargets.Padding = new System.Windows.Forms.Padding(3);
            this.tabTargets.Size = new System.Drawing.Size(638, 543);
            this.tabTargets.TabIndex = 9;
            this.tabTargets.Text = "Targets";
            this.tabTargets.UseVisualStyleBackColor = true;
            // 
            // importJobSettingsSelection
            // 
            this.importJobSettingsSelection.Dock = System.Windows.Forms.DockStyle.Fill;
            this.importJobSettingsSelection.Location = new System.Drawing.Point(3, 3);
            this.importJobSettingsSelection.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.importJobSettingsSelection.Name = "importJobSettingsSelection";
            this.importJobSettingsSelection.Size = new System.Drawing.Size(632, 537);
            this.importJobSettingsSelection.TabIndex = 0;
            this.importJobSettingsSelection.SolutionSelectionChange += new System.EventHandler(this.importJobSettingsSelection_SolutionSelectionChange);
            // 
            // tabCredentials
            // 
            this.tabCredentials.Controls.Add(this.systemCredentialsControl1);
            this.tabCredentials.Location = new System.Drawing.Point(4, 22);
            this.tabCredentials.Name = "tabCredentials";
            this.tabCredentials.Padding = new System.Windows.Forms.Padding(3);
            this.tabCredentials.Size = new System.Drawing.Size(638, 543);
            this.tabCredentials.TabIndex = 1;
            this.tabCredentials.Text = "Credentials";
            this.tabCredentials.UseVisualStyleBackColor = true;
            // 
            // systemCredentialsControl1
            // 
            this.systemCredentialsControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.systemCredentialsControl1.Location = new System.Drawing.Point(3, 3);
            this.systemCredentialsControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.systemCredentialsControl1.Name = "systemCredentialsControl1";
            this.systemCredentialsControl1.Size = new System.Drawing.Size(632, 537);
            this.systemCredentialsControl1.TabIndex = 0;
            // 
            // tabPageAzureConfig
            // 
            this.tabPageAzureConfig.Controls.Add(this.azureBaseConfigControl1);
            this.tabPageAzureConfig.Location = new System.Drawing.Point(4, 22);
            this.tabPageAzureConfig.Name = "tabPageAzureConfig";
            this.tabPageAzureConfig.Padding = new System.Windows.Forms.Padding(3);
            this.tabPageAzureConfig.Size = new System.Drawing.Size(638, 543);
            this.tabPageAzureConfig.TabIndex = 5;
            this.tabPageAzureConfig.Text = "Azure Config";
            this.tabPageAzureConfig.UseVisualStyleBackColor = true;
            // 
            // azureBaseConfigControl1
            // 
            this.azureBaseConfigControl1.AzureLocationString = "---";
            this.azureBaseConfigControl1.AzureSubscription = null;
            this.azureBaseConfigControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.azureBaseConfigControl1.EnvironmentType = App.ControlPanel.Engine.Models.EnvironmentTypeEnum.Testing;
            this.azureBaseConfigControl1.Location = new System.Drawing.Point(3, 3);
            this.azureBaseConfigControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.azureBaseConfigControl1.Name = "azureBaseConfigControl1";
            this.azureBaseConfigControl1.ResourceGroup = "txtResourceGroup";
            this.azureBaseConfigControl1.Size = new System.Drawing.Size(632, 537);
            this.azureBaseConfigControl1.TabIndex = 0;
            this.azureBaseConfigControl1.LoadingSubscriptionStateChange += new System.EventHandler<bool>(this.azureBaseConfigControl1_LoadingSubscriptionStateChange);
            // 
            // tabAzureResources
            // 
            this.tabAzureResources.Controls.Add(this.azurePaaSConfigControl1);
            this.tabAzureResources.Location = new System.Drawing.Point(4, 22);
            this.tabAzureResources.Name = "tabAzureResources";
            this.tabAzureResources.Padding = new System.Windows.Forms.Padding(3);
            this.tabAzureResources.Size = new System.Drawing.Size(638, 543);
            this.tabAzureResources.TabIndex = 2;
            this.tabAzureResources.Text = "Azure PaaS";
            this.tabAzureResources.UseVisualStyleBackColor = true;
            // 
            // azurePaaSConfigControl1
            // 
            this.azurePaaSConfigControl1.AppInsightsName = "";
            this.azurePaaSConfigControl1.AppInsightsWorkspaceName = "";
            this.azurePaaSConfigControl1.AppServicePlanName = "";
            this.azurePaaSConfigControl1.AppServiceWebAppName = "";
            this.azurePaaSConfigControl1.CognitiveEnabled = false;
            this.azurePaaSConfigControl1.CognitiveServiceName = "";
            this.azurePaaSConfigControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.azurePaaSConfigControl1.KeyVaultName = "";
            this.azurePaaSConfigControl1.Location = new System.Drawing.Point(3, 3);
            this.azurePaaSConfigControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.azurePaaSConfigControl1.Name = "azurePaaSConfigControl1";
            this.azurePaaSConfigControl1.Size = new System.Drawing.Size(632, 537);
            this.azurePaaSConfigControl1.TabIndex = 0;
            // 
            // tabAzureStorage
            // 
            this.tabAzureStorage.Controls.Add(this.azureStorageConfigControl1);
            this.tabAzureStorage.Location = new System.Drawing.Point(4, 22);
            this.tabAzureStorage.Name = "tabAzureStorage";
            this.tabAzureStorage.Padding = new System.Windows.Forms.Padding(3);
            this.tabAzureStorage.Size = new System.Drawing.Size(638, 543);
            this.tabAzureStorage.TabIndex = 8;
            this.tabAzureStorage.Text = "Azure Storage";
            this.tabAzureStorage.UseVisualStyleBackColor = true;
            // 
            // azureStorageConfigControl1
            // 
            this.azureStorageConfigControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.azureStorageConfigControl1.Location = new System.Drawing.Point(3, 3);
            this.azureStorageConfigControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.azureStorageConfigControl1.Name = "azureStorageConfigControl1";
            this.azureStorageConfigControl1.RedisName = "";
            this.azureStorageConfigControl1.ServiceBusName = "";
            this.azureStorageConfigControl1.Size = new System.Drawing.Size(632, 537);
            this.azureStorageConfigControl1.SQLDb = "";
            this.azureStorageConfigControl1.SQLServerName = "";
            this.azureStorageConfigControl1.SQLServerPassword = "";
            this.azureStorageConfigControl1.SQLServerUsername = "";
            this.azureStorageConfigControl1.StorageAccount = "";
            this.azureStorageConfigControl1.TabIndex = 1;
            // 
            // tabSharePoint
            // 
            this.tabSharePoint.Controls.Add(this.sharePointConfigControl1);
            this.tabSharePoint.Location = new System.Drawing.Point(4, 22);
            this.tabSharePoint.Name = "tabSharePoint";
            this.tabSharePoint.Padding = new System.Windows.Forms.Padding(3);
            this.tabSharePoint.Size = new System.Drawing.Size(638, 543);
            this.tabSharePoint.TabIndex = 0;
            this.tabSharePoint.Text = "SharePoint";
            this.tabSharePoint.UseVisualStyleBackColor = true;
            // 
            // sharePointConfigControl1
            // 
            this.sharePointConfigControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.sharePointConfigControl1.Location = new System.Drawing.Point(3, 3);
            this.sharePointConfigControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.sharePointConfigControl1.Name = "sharePointConfigControl1";
            this.sharePointConfigControl1.Size = new System.Drawing.Size(632, 537);
            this.sharePointConfigControl1.TabIndex = 0;
            this.sharePointConfigControl1.UninstallClicked += new System.EventHandler(this.sharePointConfigControl1_UninstallClicked);
            // 
            // tabSources
            // 
            this.tabSources.Controls.Add(this.grpLocalSources);
            this.tabSources.Controls.Add(this.lblGUIAppSourcesDesc);
            this.tabSources.Controls.Add(this.lblGUIAppSourcesHeader);
            this.tabSources.Controls.Add(this.pictureBox12);
            this.tabSources.Controls.Add(this.rdpSpecificLocation);
            this.tabSources.Controls.Add(this.rdbLatest);
            this.tabSources.Location = new System.Drawing.Point(4, 22);
            this.tabSources.Name = "tabSources";
            this.tabSources.Padding = new System.Windows.Forms.Padding(3);
            this.tabSources.Size = new System.Drawing.Size(638, 543);
            this.tabSources.TabIndex = 7;
            this.tabSources.Text = "Sources";
            this.tabSources.UseVisualStyleBackColor = true;
            // 
            // grpLocalSources
            // 
            this.grpLocalSources.Controls.Add(this.fileSelectionWebsite);
            this.grpLocalSources.Controls.Add(this.fileSelectionControlPanel);
            this.grpLocalSources.Controls.Add(this.fileSelectionWebjobAppInsights);
            this.grpLocalSources.Controls.Add(this.fileSelectionWebjobActivity);
            this.grpLocalSources.Controls.Add(this.fileSelectionAITracker);
            this.grpLocalSources.Location = new System.Drawing.Point(36, 194);
            this.grpLocalSources.Name = "grpLocalSources";
            this.grpLocalSources.Size = new System.Drawing.Size(566, 218);
            this.grpLocalSources.TabIndex = 102;
            this.grpLocalSources.TabStop = false;
            this.grpLocalSources.Text = "Local sources:";
            // 
            // fileSelectionWebsite
            // 
            this.fileSelectionWebsite.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.fileSelectionWebsite.Label = "Website.zip";
            this.fileSelectionWebsite.Location = new System.Drawing.Point(21, 164);
            this.fileSelectionWebsite.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.fileSelectionWebsite.Name = "fileSelectionWebsite";
            this.fileSelectionWebsite.SelectedFileName = "";
            this.fileSelectionWebsite.Size = new System.Drawing.Size(539, 27);
            this.fileSelectionWebsite.TabIndex = 106;
            // 
            // fileSelectionControlPanel
            // 
            this.fileSelectionControlPanel.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.fileSelectionControlPanel.Label = "ControlPanelApp.zip";
            this.fileSelectionControlPanel.Location = new System.Drawing.Point(21, 132);
            this.fileSelectionControlPanel.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.fileSelectionControlPanel.Name = "fileSelectionControlPanel";
            this.fileSelectionControlPanel.SelectedFileName = "";
            this.fileSelectionControlPanel.Size = new System.Drawing.Size(539, 27);
            this.fileSelectionControlPanel.TabIndex = 105;
            // 
            // fileSelectionWebjobAppInsights
            // 
            this.fileSelectionWebjobAppInsights.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.fileSelectionWebjobAppInsights.Label = "AppInsightsImporter.zip";
            this.fileSelectionWebjobAppInsights.Location = new System.Drawing.Point(21, 99);
            this.fileSelectionWebjobAppInsights.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.fileSelectionWebjobAppInsights.Name = "fileSelectionWebjobAppInsights";
            this.fileSelectionWebjobAppInsights.SelectedFileName = "";
            this.fileSelectionWebjobAppInsights.Size = new System.Drawing.Size(539, 27);
            this.fileSelectionWebjobAppInsights.TabIndex = 104;
            // 
            // fileSelectionWebjobActivity
            // 
            this.fileSelectionWebjobActivity.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.fileSelectionWebjobActivity.Label = "Office365ActivityImporter.zip";
            this.fileSelectionWebjobActivity.Location = new System.Drawing.Point(21, 66);
            this.fileSelectionWebjobActivity.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.fileSelectionWebjobActivity.Name = "fileSelectionWebjobActivity";
            this.fileSelectionWebjobActivity.SelectedFileName = "";
            this.fileSelectionWebjobActivity.Size = new System.Drawing.Size(539, 27);
            this.fileSelectionWebjobActivity.TabIndex = 103;
            // 
            // fileSelectionAITracker
            // 
            this.fileSelectionAITracker.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.fileSelectionAITracker.Label = "AITrackerInstaller.zip";
            this.fileSelectionAITracker.Location = new System.Drawing.Point(21, 33);
            this.fileSelectionAITracker.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.fileSelectionAITracker.Name = "fileSelectionAITracker";
            this.fileSelectionAITracker.SelectedFileName = "";
            this.fileSelectionAITracker.Size = new System.Drawing.Size(530, 27);
            this.fileSelectionAITracker.TabIndex = 102;
            // 
            // lblGUIAppSourcesDesc
            // 
            this.lblGUIAppSourcesDesc.AutoSize = true;
            this.lblGUIAppSourcesDesc.Location = new System.Drawing.Point(76, 42);
            this.lblGUIAppSourcesDesc.Name = "lblGUIAppSourcesDesc";
            this.lblGUIAppSourcesDesc.Size = new System.Drawing.Size(254, 13);
            this.lblGUIAppSourcesDesc.TabIndex = 100;
            this.lblGUIAppSourcesDesc.Text = "Where should the solution binaries be installed from?";
            // 
            // lblGUIAppSourcesHeader
            // 
            this.lblGUIAppSourcesHeader.AutoSize = true;
            this.lblGUIAppSourcesHeader.Font = new System.Drawing.Font("Calibri", 12F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.lblGUIAppSourcesHeader.Location = new System.Drawing.Point(10, 10);
            this.lblGUIAppSourcesHeader.Name = "lblGUIAppSourcesHeader";
            this.lblGUIAppSourcesHeader.Size = new System.Drawing.Size(144, 19);
            this.lblGUIAppSourcesHeader.TabIndex = 99;
            this.lblGUIAppSourcesHeader.Text = "Application Sources";
            // 
            // pictureBox12
            // 
            this.pictureBox12.Image = global::App.ControlPanel.Properties.Resources.AppService;
            this.pictureBox12.Location = new System.Drawing.Point(14, 32);
            this.pictureBox12.Name = "pictureBox12";
            this.pictureBox12.Size = new System.Drawing.Size(56, 56);
            this.pictureBox12.TabIndex = 98;
            this.pictureBox12.TabStop = false;
            // 
            // rdpSpecificLocation
            // 
            this.rdpSpecificLocation.AutoSize = true;
            this.rdpSpecificLocation.Location = new System.Drawing.Point(36, 146);
            this.rdpSpecificLocation.Name = "rdpSpecificLocation";
            this.rdpSpecificLocation.Size = new System.Drawing.Size(105, 17);
            this.rdpSpecificLocation.TabIndex = 1;
            this.rdpSpecificLocation.Text = "Local downloads";
            this.rdpSpecificLocation.UseVisualStyleBackColor = true;
            this.rdpSpecificLocation.CheckedChanged += new System.EventHandler(this.rdpSpecificLocation_CheckedChanged);
            // 
            // rdbLatest
            // 
            this.rdbLatest.AutoSize = true;
            this.rdbLatest.Checked = true;
            this.rdbLatest.Location = new System.Drawing.Point(36, 123);
            this.rdbLatest.Name = "rdbLatest";
            this.rdbLatest.Size = new System.Drawing.Size(241, 17);
            this.rdbLatest.TabIndex = 0;
            this.rdbLatest.TabStop = true;
            this.rdbLatest.Text = "Latest stable release (download automatically)";
            this.rdbLatest.UseVisualStyleBackColor = true;
            this.rdbLatest.CheckedChanged += new System.EventHandler(this.rdbLatest_CheckedChanged);
            // 
            // tabInstall
            // 
            this.tabInstall.Controls.Add(this.installSolutionControl1);
            this.tabInstall.Location = new System.Drawing.Point(4, 22);
            this.tabInstall.Name = "tabInstall";
            this.tabInstall.Padding = new System.Windows.Forms.Padding(3);
            this.tabInstall.Size = new System.Drawing.Size(638, 543);
            this.tabInstall.TabIndex = 8;
            this.tabInstall.Text = "Install";
            this.tabInstall.UseVisualStyleBackColor = true;
            // 
            // installSolutionControl1
            // 
            this.installSolutionControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.installSolutionControl1.Location = new System.Drawing.Point(3, 3);
            this.installSolutionControl1.Margin = new System.Windows.Forms.Padding(4, 5, 4, 5);
            this.installSolutionControl1.Name = "installSolutionControl1";
            this.installSolutionControl1.Size = new System.Drawing.Size(632, 537);
            this.installSolutionControl1.TabIndex = 0;
            installTasksConfig2.InstallLatestSolutionContent = false;
            installTasksConfig2.OpenAdminSitePostInstall = false;
            installTasksConfig2.RegisterConfig = true;
            installTasksConfig2.UpgradeSchema = false;
            this.installSolutionControl1.TasksConfig = installTasksConfig2;
            this.installSolutionControl1.Install += new App.ControlPanel.Frames.InstallWizard.InstallSolutionControl.InstallEventHander(this.installSolutionControl1_Install);
            this.installSolutionControl1.TestConfig += new App.ControlPanel.Frames.InstallWizard.InstallSolutionControl.TestConfigEventHander(this.installSolutionControl1_TestConfig);
            // 
            // installerBackgroundWorker
            // 
            this.installerBackgroundWorker.DoWork += new System.ComponentModel.DoWorkEventHandler(this.installerBackgroundWorker_DoWork);
            this.installerBackgroundWorker.RunWorkerCompleted += new System.ComponentModel.RunWorkerCompletedEventHandler(this.installerBackgroundWorker_RunWorkerCompleted);
            // 
            // btnNext
            // 
            this.btnNext.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnNext.Location = new System.Drawing.Point(549, 570);
            this.btnNext.Name = "btnNext";
            this.btnNext.Size = new System.Drawing.Size(94, 22);
            this.btnNext.TabIndex = 30;
            this.btnNext.Text = "Next ->";
            this.btnNext.UseVisualStyleBackColor = true;
            this.btnNext.Click += new System.EventHandler(this.btnNext_Click);
            // 
            // InstallSPOSitesControl
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.Controls.Add(this.btnNext);
            this.Controls.Add(this.tabs);
            this.Name = "InstallSPOSitesControl";
            this.Size = new System.Drawing.Size(646, 595);
            this.tabs.ResumeLayout(false);
            this.tabTargets.ResumeLayout(false);
            this.tabCredentials.ResumeLayout(false);
            this.tabPageAzureConfig.ResumeLayout(false);
            this.tabAzureResources.ResumeLayout(false);
            this.tabAzureStorage.ResumeLayout(false);
            this.tabSharePoint.ResumeLayout(false);
            this.tabSources.ResumeLayout(false);
            this.tabSources.PerformLayout();
            this.grpLocalSources.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.pictureBox12)).EndInit();
            this.tabInstall.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion
        private System.Windows.Forms.TabControl tabs;
        private System.Windows.Forms.TabPage tabSharePoint;
        private System.Windows.Forms.TabPage tabCredentials;
        private System.Windows.Forms.TabPage tabInstall;
        private System.ComponentModel.BackgroundWorker installerBackgroundWorker;
        private System.Windows.Forms.Button btnNext;
        private System.Windows.Forms.TabPage tabPageAzureConfig;
        private System.Windows.Forms.TabPage tabSources;
        private System.Windows.Forms.GroupBox grpLocalSources;
        private Controls.FileSelection fileSelectionWebjobAppInsights;
        private Controls.FileSelection fileSelectionWebjobActivity;
        private Controls.FileSelection fileSelectionAITracker;
        private System.Windows.Forms.Label lblGUIAppSourcesDesc;
        private System.Windows.Forms.Label lblGUIAppSourcesHeader;
        private System.Windows.Forms.PictureBox pictureBox12;
        private System.Windows.Forms.RadioButton rdpSpecificLocation;
        private System.Windows.Forms.RadioButton rdbLatest;
        private Controls.FileSelection fileSelectionControlPanel;
        private Controls.FileSelection fileSelectionWebsite;
        private System.Windows.Forms.TabPage tabAzureResources;
        private System.Windows.Forms.TabPage tabAzureStorage;
        private System.Windows.Forms.TabPage tabTargets;
        private Controls.TargetSolutionConfigControl importJobSettingsSelection;
        private InstallWizard.SharePointConfigControl sharePointConfigControl1;
        private InstallWizard.InstallSolutionControl installSolutionControl1;
        private InstallWizard.AzurePaaSConfigControl azurePaaSConfigControl1;
        private InstallWizard.AzureStorageConfigControl azureStorageConfigControl1;
        private InstallWizard.SystemCredentialsControl systemCredentialsControl1;
        private InstallWizardPages.AzureBaseConfigControl azureBaseConfigControl1;
    }
}
