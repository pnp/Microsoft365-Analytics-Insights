using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.SPO.AppCatalog;
using App.ControlPanel.Engine.SPO.Auth;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Windows.Forms;
using static App.ControlPanel.Frames.InstallWizard.InstallSolutionControl;

namespace App.ControlPanel.Frames
{
    public partial class InstallSPOSitesControl : UserControl, ISolutionConfigurableComponent
    {
        private BaseInstallProcess _installerEngine = null;
        private readonly InstallSPOSitesControlLogger _logger;
        // CTS for the currently-running install/test/uninstall. Replaced on each StartBackgroundProcess
        // so a previous run's cancellation can't affect the next one.
        private CancellationTokenSource _runCts;
        public InstallSPOSitesControl()
        {
            InitializeComponent();
            _logger = new InstallSPOSitesControlLogger(this);
            installSolutionControl1.CancelRequested += InstallSolutionControl1_CancelRequested;
            tabs.Selecting += Tabs_Selecting;
            azureBaseConfigControl1.OnNeedAppRegistrationCredentials = () => GetConfigFromGUI().InstallerAccount;
            azureBaseConfigControl1.AzureLocationChanged += (s, region) => azureStorageConfigControl1.AzureRegion = region;
            networkingConfigControl1.OnNeedAzureCredentials = () =>
            {
                var config = GetConfigFromGUI();
                return (
                    config.InstallerAccount?.DirectoryId,
                    config.InstallerAccount?.ClientId,
                    config.InstallerAccount?.Secret,
                    config.Subscription?.SubId,
                    config.ResourceGroupName
                );
            };
        }

        #region Props

        public InstallerProxyConfig ProxyConfig { get; set; }
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
                ServiceBusEnabled = azureStorageConfigControl1.ServiceBusEnabled,
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
                Tags = azureBaseConfigControl1.Tags,
                NetworkConfig = new VNetConfig
                {
                    Enabled = networkingConfigControl1.VNetEnabled,
                    VNetName = networkingConfigControl1.VNetName,
                    SubnetName = networkingConfigControl1.SubnetName,
                    AddressPrefix = networkingConfigControl1.AddressPrefix,
                    SubnetAddressPrefix = networkingConfigControl1.SubnetAddressPrefix,
                    AppServiceIntegrationSubnetName = networkingConfigControl1.AppServiceSubnetName,
                    AppServiceIntegrationSubnetAddressPrefix = networkingConfigControl1.AppServiceSubnetAddressPrefix,
                    DeployDnsZones = networkingConfigControl1.DeployDnsZones,
                    AllowPublicAccess = networkingConfigControl1.AllowPublicAccess,
                    CustomEndpointNames = networkingConfigControl1.GetEndpointNames(),
                    HybridWorkerVmResourceId = networkingConfigControl1.HybridWorkerVmResourceId
                }
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
            // Take the region from the picker rather than the raw config: the picker rejects a value that is
            // not a known Azure region (it falls back to its "no region" placeholder), and the preview label
            // must agree with what the Azure tab actually shows. Set explicitly because SelectedIndexChanged
            // does not fire when the assignment leaves the selection unchanged.
            azureStorageConfigControl1.AzureRegion = azureBaseConfigControl1.AzureLocationString;
            azureStorageConfigControl1.RedisName = config.RedisName;
            azureStorageConfigControl1.ServiceBusName = config.ServiceBusName;
            azureStorageConfigControl1.ServiceBusEnabled = config.ServiceBusEnabled;

            azurePaaSConfigControl1.AppInsightsName = config.AppInsightsName;
            azurePaaSConfigControl1.AppServicePlanName = config.AppServicePlanName;
            azurePaaSConfigControl1.AppServiceWebAppName = config.AppServiceWebAppName;
            azurePaaSConfigControl1.CognitiveServiceName = config.CognitiveServiceName;
            azurePaaSConfigControl1.CognitiveEnabled = config.CognitiveServicesEnabled;
            azurePaaSConfigControl1.AppInsightsWorkspaceName = config.AppInsightsWorkspaceName;
            azurePaaSConfigControl1.KeyVaultName = config.KeyVaultName;
            azurePaaSConfigControl1.AutomationAccountName = config.AutomationAccountName;

            // Networking
            if (config.NetworkConfig != null)
            {
                networkingConfigControl1.VNetEnabled = config.NetworkConfig.Enabled;
                networkingConfigControl1.VNetName = config.NetworkConfig.VNetName;
                networkingConfigControl1.SubnetName = config.NetworkConfig.SubnetName;
                networkingConfigControl1.AddressPrefix = config.NetworkConfig.AddressPrefix;
                networkingConfigControl1.SubnetAddressPrefix = config.NetworkConfig.SubnetAddressPrefix;
                networkingConfigControl1.AppServiceSubnetName = config.NetworkConfig.AppServiceIntegrationSubnetName;
                networkingConfigControl1.AppServiceSubnetAddressPrefix = config.NetworkConfig.AppServiceIntegrationSubnetAddressPrefix;
                networkingConfigControl1.DeployDnsZones = config.NetworkConfig.DeployDnsZones;
                networkingConfigControl1.AllowPublicAccess = config.NetworkConfig.AllowPublicAccess;
                networkingConfigControl1.SetEndpointNames(config.NetworkConfig.CustomEndpointNames);
                networkingConfigControl1.HybridWorkerVmResourceId = config.NetworkConfig.HybridWorkerVmResourceId;
            }

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
            // GetRootException walks to the deepest inner exception. That is right for a raw infrastructure
            // failure, but wrong for the SharePoint sign-in and app-catalog errors: those carry a message
            // that tells the admin exactly what to do - grant consent, set the tenant, or re-run and use the
            // sign-in URL from the log - wrapped around an MSAL or cancellation exception whose own message
            // is just "operation cancelled". Because the worker blocks on the task, the chain also arrives
            // wrapped in an AggregateException. Prefer the actionable exception wherever it sits.
            Exception toShow = FindActionableException(ex) ?? CommonExceptionHandler.GetRootException(ex);

            if (ex is InvalidFormInputException)
            {
                LogItemOnUIThread(new InstallLogLVI(toShow));
            }
            else
            {
                // Unexpected error
                LogItemOnUIThread(new InstallLogLVI(toShow), true);
            }

        }

        /// <summary>
        /// Finds the first exception in the chain that was raised specifically to be shown to the admin,
        /// looking through AggregateException branches. Returns null when there isn't one.
        /// </summary>
        static Exception FindActionableException(Exception ex)
        {
            while (ex != null)
            {
                if (ex is SpoAuthenticationException || ex is SpoAppCatalogException)
                {
                    return ex;
                }

                if (ex is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.Flatten().InnerExceptions)
                    {
                        var found = FindActionableException(inner);
                        if (found != null)
                        {
                            return found;
                        }
                    }

                    return null;
                }

                ex = ex.InnerException;
            }

            return null;
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

            // Don't disable the whole tab control — that greys out the log ListView and
            // disables scrolling, which is exactly what the user complained about. Instead,
            // disable each tab page except the install tab (so the user can't change config
            // mid-run) and prevent navigation away from the install tab via the Selecting hook.
            // The install tab itself stays enabled so log scrolling, copy-to-clipboard, and the
            // Cancel button keep working.
            bool isWorking = state == AppWaitState.Working;
            foreach (TabPage tp in tabs.TabPages)
            {
                if (tp == tabInstall) continue;
                tp.Enabled = !isWorking;
            }
            installSolutionControl1.SetRunningState(isWorking);
            btnNext.Enabled = !isWorking && tabs.SelectedIndex < (tabs.TabCount - 1);

            MainForm mainForm = (MainForm)this.ParentForm;
            mainForm.SetFormLoadingState(state);
        }

        /// <summary>
        /// Block navigation away from the install tab while a background process is running.
        /// </summary>
        private void Tabs_Selecting(object sender, TabControlCancelEventArgs e)
        {
            if (_runCts != null && !_runCts.IsCancellationRequested && e.TabPage != tabInstall)
            {
                e.Cancel = true;
            }
        }

        /// <summary>
        /// User clicked Cancel — request co-operative cancellation. The install engine checks the token
        /// between phases / task batches; in-flight Azure SDK calls will complete before we stop.
        /// </summary>
        private void InstallSolutionControl1_CancelRequested(object sender, EventArgs e)
        {
            if (_runCts == null || _runCts.IsCancellationRequested) return;
            _logger.LogWarning("Cancellation requested — install will stop at the next safe checkpoint. In-flight Azure operations will complete first.");
            try { _runCts.Cancel(); } catch (ObjectDisposedException) { /* race with completion */ }
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


            var softwareConfig = new SoftwareReleaseConfig();

            // Fresh cancellation source per run. Dispose the previous one (if any) defensively;
            // it should already be Disposed in RunWorkerCompleted but guard against unusual paths.
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();

            if (task == InstallTask.Install)
            {
                _installerEngine = new SolutionInstaller(config, _logger, softwareConfig, ProxyConfig, Environment.UserName, (this.ParentForm as MainForm).LastPassword);
            }
            else if (task == InstallTask.Test)
            {
                _installerEngine = new SolutionInstallVerifier(config, _logger, this.TestsConfig);
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
                        ((SolutionInstaller)_installerEngine).InstallOrUpdate(_runCts?.Token ?? CancellationToken.None).Wait();
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

            // Release the cancellation source — a fresh one is created per run in StartBackgroundProcess.
            try { _runCts?.Dispose(); } catch (Exception) { }
            _runCts = null;
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

            // A private deployment can leave Service Bus (and therefore the Teams calls import) unreachable if the
            // namespace isn't Premium. Make that impossible to miss before a real deployment starts - see issue #228.
            var serviceBusWarning = PreInstallAdvisor.GetServiceBusPrivateDeploymentWarning(GetConfigFromGUI());
            if (serviceBusWarning != null)
            {
                var proceed = MessageBox.Show(this, serviceBusWarning, "Service Bus cannot be private on Standard SKU",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (proceed != DialogResult.Yes)
                {
                    return;
                }
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
