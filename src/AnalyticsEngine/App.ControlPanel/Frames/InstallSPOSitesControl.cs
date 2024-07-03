using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
using System;
using System.Configuration;
using System.Windows.Forms;
using static App.ControlPanel.Frames.InstallWizard.InstallSolutionControl;

namespace App.ControlPanel.Frames
{
    public partial class InstallSPOSitesControl : UserControl, ISolutionConfigurableComponent
    {
        private BaseInstallProcess _installerEngine = null;
        private readonly InstallSPOSitesControlLogger _logger;
        public InstallSPOSitesControl()
        {
            InitializeComponent();
            _logger = new InstallSPOSitesControlLogger(this);
            azureBaseConfigControl1.OnNeedAppRegistrationCredentials = () => GetConfigFromGUI().InstallerAccount;
        }

        #region Props

        public InstallerFtpConfig FtpConfig { get; set; }
        public TestConfiguration TestsConfig { get; set; }

        #endregion

        #region Event Handling


        private void AzureInstaller_InstallEvent(object sender, InstallLogEventArgs e)
        {
            LogItemOnUIThread(new InstallLogLVI(e));
        }

        private void btnNext_Click(object sender, EventArgs e)
        {
            tabs.SelectedIndex++;
        }

        private void tabs_SelectedIndexChanged(object sender, EventArgs e)
        {
            btnNext.Enabled = tabs.SelectedIndex < (tabs.TabCount - 1);
        }

        #endregion

        private bool ValidatInputAndShowErrors(bool displayErrors)
        {
            var config = GetConfigFromGUI();
            var errs = config.ValidatInputAndGetErrors();

            // Output result
            if (errs.Count > 0)
            {
                if (displayErrors)
                {
                    CommonUIThings.ShowValidationErrors(errs);
                }
                return false;
            }
            else
            {
                return true;
            }
        }

        #region Form Settings & Config


        /// <summary>
        /// Build a install config object from form fields.
        /// </summary>
        private SolutionInstallConfig GetConfigFromGUI()
        {
            var config = new SolutionInstallConfig()
            {
                StorageAccountName = azureStorageConfigControl1.StorageAccount,
                SQLServerName = azureStorageConfigControl1.SQLServerName,
                SQLServerDatabaseName = azureStorageConfigControl1.SQLDb,
                SQLServerAdminUsername = azureStorageConfigControl1.SQLServerUsername,
                SQLServerAdminPassword = azureStorageConfigControl1.SQLServerPassword,
                ServiceBusName = azureStorageConfigControl1.ServiceBusName,
                RedisName = azureStorageConfigControl1.RedisName,
                AllowTelemetry = installSolutionControl1.AllowTelemetry,
                SolutionConfig = importJobSettingsSelection.Config,
                TasksConfig = installSolutionControl1.TasksConfig,
                AppServiceWebAppName = azurePaaSConfigControl1.AppServiceWebAppName,
                AppServicePlanName = azurePaaSConfigControl1.AppServicePlanName,
                AppInsightsWorkspaceName = azurePaaSConfigControl1.AppInsightsWorkspaceName,
                AppInsightsName = azurePaaSConfigControl1.AppInsightsName,
                CognitiveServiceName = azurePaaSConfigControl1.CognitiveServiceName,
                CognitiveServicesEnabled = azurePaaSConfigControl1.CognitiveEnabled,
                SharePointConfig = sharePointConfigControl1.SharePointInstallConfig,
                KeyVaultName = azurePaaSConfigControl1.KeyVaultName,
                AutomationAccountName = azurePaaSConfigControl1.AutomationAccountName,
                ResourceGroupName = azureBaseConfigControl1.ResourceGroup,
                Subscription = azureBaseConfigControl1.AzureSubscription,
                AzureLocationName = azureBaseConfigControl1.AzureLocationString,
                EnvironmentType = azureBaseConfigControl1.EnvironmentType,
                Tags = azureBaseConfigControl1.Tags
            };

            // Accounts
            if (systemCredentialsControl1.InstallerAccountHasValidFields)
            {
                config.InstallerAccount = systemCredentialsControl1.InstallerAccount;
            }
            if (systemCredentialsControl1.RuntimeAccountHasValidFields)
            {
                config.ActivityAccount = systemCredentialsControl1.RuntimeAccount;
            }

            // Sources
            if (rdpSpecificLocation.Checked)
            {
                config.DownloadLatestStable = false;

                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.AITracker).FileLocation =
                    fileSelectionAITracker.SelectedFileName;
                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation =
                    fileSelectionWebjobActivity.SelectedFileName;
                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation =
                    fileSelectionWebjobAppInsights.SelectedFileName;
                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.ControlPanel).FileLocation =
                    fileSelectionControlPanel.SelectedFileName;
                config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation =
                    fileSelectionWebsite.SelectedFileName;
            }
            else
            {
                config.DownloadLatestStable = true;
            }

            return config;
        }


        public void ConfigureUI(SolutionInstallConfig config)
        {
            // Set GUI
            importJobSettingsSelection.Config = config.SolutionConfig;

            azureBaseConfigControl1.ResourceGroup = config.ResourceGroupName;
            azureBaseConfigControl1.AzureSubscription = config.Subscription;
            azureBaseConfigControl1.AzureLocationString = config.AzureLocation;
            azureBaseConfigControl1.EnvironmentType = config.EnvironmentType;
            azureBaseConfigControl1.Tags = config.Tags;

            azureStorageConfigControl1.SQLDb = config.SQLServerDatabaseName;
            azureStorageConfigControl1.SQLServerName = config.SQLServerName;
            azureStorageConfigControl1.SQLServerPassword = config.SQLServerAdminPassword;
            azureStorageConfigControl1.SQLServerUsername = config.SQLServerAdminUsername;
            azureStorageConfigControl1.StorageAccount = config.StorageAccountName;
            azureStorageConfigControl1.RedisName = config.RedisName;
            azureStorageConfigControl1.ServiceBusName = config.ServiceBusName;

            azurePaaSConfigControl1.AppInsightsName = config.AppInsightsName;
            azurePaaSConfigControl1.AppServicePlanName = config.AppServicePlanName;
            azurePaaSConfigControl1.AppServiceWebAppName = config.AppServiceWebAppName;
            azurePaaSConfigControl1.CognitiveServiceName = config.CognitiveServiceName;
            azurePaaSConfigControl1.CognitiveEnabled = config.CognitiveServicesEnabled;
            azurePaaSConfigControl1.AppInsightsWorkspaceName = config.AppInsightsWorkspaceName;
            azurePaaSConfigControl1.KeyVaultName = config.KeyVaultName;
            azurePaaSConfigControl1.AutomationAccountName = config.AutomationAccountName;

            // Sources
            rdpSpecificLocation.Checked = !config.DownloadLatestStable;
            rdbLatest.Checked = config.DownloadLatestStable;

            fileSelectionAITracker.SelectedFileName
                = config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.AITracker).FileLocation;
            fileSelectionWebjobActivity.SelectedFileName
                = config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebJobActivity).FileLocation;
            fileSelectionWebjobAppInsights.SelectedFileName
                = config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebJobAppInsights).FileLocation;
            fileSelectionControlPanel.SelectedFileName
                = config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.ControlPanel).FileLocation;
            fileSelectionWebsite.SelectedFileName
                = config.LocalSourceOverride.GetSolutionComponentLocation(SoftwareComponent.WebSite).FileLocation;
            UpdateSourcesGUI();

            // Tasks
            installSolutionControl1.TasksConfig = config.TasksConfig;
            installSolutionControl1.AllowTelemetry = config.AllowTelemetry;

            // Accounts
            systemCredentialsControl1.InstallerAccount = config.InstallerAccount;
            systemCredentialsControl1.RuntimeAccount = config.RuntimeAccountOffice365;

            // SharePoint config
            sharePointConfigControl1.SharePointInstallConfig = config.SharePointConfig;

            // Show SP tab?
            RefreshTabsConfig();
        }

        #endregion

        #region Exception Handling


        private void HandleNestedException(Exception ex)
        {
            Exception rootExceptions = CommonExceptionHandler.GetRootException(ex);

            if (ex is InvalidFormInputException)
            {
                LogItemOnUIThread(new InstallLogLVI(rootExceptions));
            }
            else
            {
                // Unexpected error
                LogItemOnUIThread(new InstallLogLVI(rootExceptions), true);
            }

        }

        public void LogItemOnUIThread(InstallLogLVI installLogLVI, bool fatalError)
        {
            installSolutionControl1.LogItemOnUIThread(installLogLVI, fatalError);
        }

        public void LogItemOnUIThread(InstallLogLVI installLogLVI)
        {
            installSolutionControl1.LogItemOnUIThread(installLogLVI);
        }


        #endregion

        void SetFormGUIState(AppWaitState state)
        {
            if (state == AppWaitState.Working)
            {
                this.Cursor = Cursors.WaitCursor;
            }
            else
            {
                this.Cursor = Cursors.Default;
            }
            tabs.Enabled = state == AppWaitState.Ready;

            MainForm mainForm = (MainForm)this.ParentForm;
            mainForm.SetFormLoadingState(state);
        }

        // ISolutionConfigurableComponent
        public SolutionInstallConfig GetConfigurationState()
        {
            return GetConfigFromGUI();
        }

        void RefreshTabsConfig()
        {
            // Show SP tab if either web or audit traffic is needed
            if (importJobSettingsSelection.Config.ImportTaskSettings.WebTraffic || importJobSettingsSelection.Config.ImportTaskSettings.ActivityLog)
            {
                if (!_spTabVisible)
                {
                    tabs.TabPages.Insert(5, tabSharePoint);     //5th tab
                    _spTabVisible = true;
                }
            }
            else
            {
                if (_spTabVisible)
                {
                    tabs.TabPages.Remove(tabSharePoint);
                    _spTabVisible = false;
                }
            }
        }

        #region Sources GUI

        private void rdbLatest_CheckedChanged(object sender, EventArgs e)
        {
            UpdateSourcesGUI();
        }

        private void UpdateSourcesGUI()
        {
            grpLocalSources.Enabled = rdpSpecificLocation.Checked;
        }

        private void rdpSpecificLocation_CheckedChanged(object sender, EventArgs e)
        {
            UpdateSourcesGUI();
        }

        #endregion

        void StartBackgroundProcess(InstallTask task)
        {
            SetFormGUIState(AppWaitState.Working);

            // Start new install 
            installSolutionControl1.ClearLog();

            var config = GetConfigFromGUI();


            var urlConfig = ConfigurationManager.AppSettings.Get("SoftwareDownloadURL");
            var softwareConfig = new SoftwareReleaseConfig
            {
                SoftwareDownloadURL = urlConfig
            };

            if (task == InstallTask.Install)
            {
                _installerEngine = new SolutionInstaller(config, _logger, softwareConfig, FtpConfig, Environment.UserName, (this.ParentForm as MainForm).LastPassword);
            }
            else if (task == InstallTask.Test)
            {
                _installerEngine = new SolutionInstallVerifier(config, _logger, FtpConfig, this.TestsConfig);
            }
            else if (task == InstallTask.UninstallFromSharePoint)
            {
                _installerEngine = new SolutionUninstaller(config, _logger);
            }
            installerBackgroundWorker.RunWorkerAsync(task);

            SetFormGUIState(AppWaitState.Working);

        }

        enum InstallTask
        {
            Unknown,
            Install,
            Test,
            UninstallFromSharePoint
        }

        #region Background Workers

        private void installerBackgroundWorker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            var gotSomethingToDo = false;
            if (e.Argument is InstallTask)
            {
                var thingToDo = (InstallTask)e.Argument;

                if (thingToDo == InstallTask.Test)
                {
                    gotSomethingToDo = true;
                    try
                    {
                        ((SolutionInstallVerifier)_installerEngine).RunTests().Wait();
                    }
                    catch (AggregateException ex)
                    {
                        HandleNestedException(ex);
                    }
                }
                else if (thingToDo == InstallTask.Install)
                {
                    gotSomethingToDo = true;
                    try
                    {
                        ((SolutionInstaller)_installerEngine).InstallOrUpdate().Wait();
                    }
                    catch (AggregateException ex)
                    {
                        HandleNestedException(ex);
                    }
                }
                else if (thingToDo == InstallTask.UninstallFromSharePoint)
                {
                    gotSomethingToDo = true;
                    try
                    {
                        ((SolutionUninstaller)_installerEngine).UninstallFromSharePoint(_logger).Wait();
                    }
                    catch (AggregateException ex)
                    {
                        HandleNestedException(ex);
                    }
                }
            }
            if (!gotSomethingToDo)
            {
                LogItemOnUIThread(new InstallLogLVI(new InstallLogEventArgs() { Text = "Internal error: unexpected install task", IsError = true }));
            }
        }

        private void installerBackgroundWorker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            SetFormGUIState(AppWaitState.Ready);
            System.Media.SystemSounds.Beep.Play();
        }

        #endregion

        #region Install Page Events

        private void installSolutionControl1_Install(object sender, EventArgs eventArgs)
        {

            // Sanity
            if (!this.ValidatInputAndShowErrors(true))
            {
                return;
            }

            // Save settings
            (this.ParentForm as MainForm).SaveLastSettings();

            StartBackgroundProcess(InstallTask.Install);
        }

        private void installSolutionControl1_TestConfig(object sender, EventArgs eventArgs)
        {

            // Test against MS solution resources as found in config file
            StartBackgroundProcess(InstallTask.Test);
        }

        private void azureBaseConfigControl1_LoadingSubscriptionStateChange(object sender, bool loading)
        {
            var state = AppWaitState.Ready;
            if (loading) state = AppWaitState.Working;
            SetFormGUIState(state);
        }

        private bool _spTabVisible = true;
        private void importJobSettingsSelection_SolutionSelectionChange(object sender, EventArgs e)
        {
            RefreshTabsConfig();
        }

        private void sharePointConfigControl1_UninstallClicked(object sender, EventArgs e)
        {
            var cfg = this.GetConfigFromGUI();

            var spErrors = cfg.SharePointConfig.ValidatInputAndGetErrors();

            // Sanity
            if (spErrors.Count > 0)
            {
                CommonUIThings.ShowValidationErrors(spErrors);
                return;
            }

            var r = MessageBox.Show($"Are you sure you want to remove AITracker from these {cfg.SharePointConfig.TargetSites.Count} site(s)?",
                "Uninstall SharePoint Online Tracking", MessageBoxButtons.YesNo, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2);
            if (r == DialogResult.Yes)
            {
                tabs.SelectedIndex = tabs.TabCount - 1;
                StartBackgroundProcess(InstallTask.UninstallFromSharePoint);
            }
        }

        #endregion
    }
}
