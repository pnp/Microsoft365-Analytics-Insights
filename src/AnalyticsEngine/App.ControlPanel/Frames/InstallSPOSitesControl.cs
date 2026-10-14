using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using App.ControlPanel.Engine.SPO.AppCatalog;
using App.ControlPanel.Engine.SPO.Auth;
using App.ControlPanel.Frames.InstallWizard;
using Common.Entities.Installer;
using DataUtils;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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
        private bool _isWorking;
        private readonly List<Panel> _pages = new List<Panel>();
        private Panel _selectedPage;
        private readonly Panel tabOverview = new Panel { Name = "tabOverview", Text = "Overview" };
        private readonly ConfigurationOverviewControl _overview = new ConfigurationOverviewControl { Dock = DockStyle.Fill };
        public event EventHandler OpenConfigurationRequested;
        public event EventHandler NewConfigurationRequested;
        public InstallSPOSitesControl()
        {
            InitializeComponent();
            lblStep.UseMnemonic = false;
            ConfigureSourcesLayout();
            _logger = new InstallSPOSitesControlLogger(this);
            installSolutionControl1.CancelRequested += InstallSolutionControl1_CancelRequested;
            tabOverview.Controls.Add(_overview);
            pageHost.Controls.Add(tabOverview);
            _overview.OpenRequested += (sender, args) => OpenConfigurationRequested?.Invoke(this, args);
            _overview.NewRequested += (sender, args) => NewConfigurationRequested?.Invoke(this, args);
            _overview.ContinueRequested += (sender, args) => SelectPage(tabTargets);
            _pages.AddRange(new[] { tabOverview, tabTargets, tabCredentials, tabPageAzureConfig, tabAzureResources,
                tabAzureAI, tabAzureStorage, tabNetworking, tabSharePoint, tabSources, tabInstall });
            foreach (var page in _pages)
            {
                page.Dock = DockStyle.Fill;
                page.Visible = false;
            }
            RebuildNavigator();
            SelectPage(tabOverview);
            azureBaseConfigControl1.OnNeedAppRegistrationCredentials = () => GetConfigFromGUI().InstallerAccount;
            azureAIConfigControl1.OnNeedModelCredentials = () =>
            {
                var config = GetConfigFromGUI();
                return (config.InstallerAccount?.DirectoryId, config.InstallerAccount?.ClientId,
                    config.InstallerAccount?.Secret, config.Subscription?.SubId, config.AzureLocationName);
            };
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
            if (btnNext.Enabled)
            {
                SelectPage(_pages[_pages.IndexOf(_selectedPage) + 1]);
            }
        }

        private void btnBack_Click(object sender, EventArgs e)
        {
            if (btnBack.Enabled)
            {
                SelectPage(_pages[_pages.IndexOf(_selectedPage) - 1]);
            }
        }

        private void navigator_BeforeSelect(object sender, TreeViewCancelEventArgs e)
        {
            e.Cancel = _isWorking && e.Node.Tag != _selectedPage;
        }

        private void navigator_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node.Tag is Panel page) SelectPage(page);
        }

        private void SelectPage(Panel page)
        {
            if (_isWorking && page != _selectedPage) return;
            foreach (Panel child in pageHost.Controls) child.Visible = child == page;
            _selectedPage = page;
            page.BringToFront();
            if (page == tabOverview) _overview.RefreshConfiguration(GetConfigFromGUI(), SelectedWorkloadCount());
            var node = navigator.Nodes.Cast<TreeNode>().SelectMany(group => group.Nodes.Cast<TreeNode>())
                .FirstOrDefault(item => item.Tag == page);
            if (navigator.SelectedNode != node) navigator.SelectedNode = node;
            UpdateWizardNavigation();
        }

        private void RebuildNavigator()
        {
            navigator.BeginUpdate();
            navigator.Nodes.Clear();
            var configuration = navigator.Nodes.Add("Configuration");
            var infrastructure = navigator.Nodes.Add("Infrastructure");
            var deployment = navigator.Nodes.Add("Deployment");
            foreach (var page in _pages)
            {
                var group = page == tabOverview || page == tabTargets || page == tabCredentials ? configuration
                    : page == tabSharePoint || page == tabSources || page == tabInstall ? deployment : infrastructure;
                group.Nodes.Add(new TreeNode(PageTitle(page)) { Name = page.Name, Tag = page });
            }
            navigator.ExpandAll();
            navigator.EndUpdate();
        }

        private string PageTitle(Panel page)
        {
            if (page == tabTargets) return "Workloads";
            if (page == tabCredentials) return "Accounts & permissions";
            if (page == tabPageAzureConfig) return "Azure environment";
            if (page == tabAzureResources) return "Hosting & services";
            if (page == tabAzureStorage) return "Database & storage";
            if (page == tabSources) return "Release & packages";
            if (page == tabInstall) return "Review & deploy";
            return page.Text;
        }

        private int SelectedWorkloadCount() => WorkloadCheckboxes(importJobSettingsSelection).Count(checkbox => checkbox.Checked);

        private static IEnumerable<CheckBox> WorkloadCheckboxes(Control parent) =>
            parent.Controls.Cast<Control>().SelectMany(child => child is CheckBox checkbox
                ? new[] { checkbox } : WorkloadCheckboxes(child));

        private void UpdateWizardNavigation()
        {
            var index = _pages.IndexOf(_selectedPage);
            btnBack.Enabled = !_isWorking && index > 0;
            btnNext.Enabled = !_isWorking && index >= 0 && index < _pages.Count - 1;
            btnNext.Text = index >= 0 && index < _pages.Count - 1
                ? "&Next: " + PageTitle(_pages[index + 1]).Replace("&", "&&")
                : "&Next";
            lblStep.Text = index >= 0
                ? $"Step {index + 1} of {_pages.Count}: {PageTitle(_selectedPage)}"
                : string.Empty;
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
                SqlAuthMode = azureStorageConfigControl1.SqlAuthMode,
                SQLEntraDatabaseUsers = azureStorageConfigControl1.SqlDatabaseUsers,
                ServiceBusName = azureStorageConfigControl1.ServiceBusName,
                ServiceBusEnabled = azureStorageConfigControl1.ServiceBusEnabled,
                AllowTelemetry = installSolutionControl1.AllowTelemetry,
                SolutionConfig = importJobSettingsSelection.Config,
                TasksConfig = installSolutionControl1.TasksConfig,
                AppServiceWebAppName = azurePaaSConfigControl1.AppServiceWebAppName,
                AppServicePlanName = azurePaaSConfigControl1.AppServicePlanName,
                AppInsightsWorkspaceName = azurePaaSConfigControl1.AppInsightsWorkspaceName,
                AppInsightsName = azurePaaSConfigControl1.AppInsightsName,
                CognitiveServiceName = azureAIConfigControl1.CognitiveServiceName,
                CognitiveServicesEnabled = azureAIConfigControl1.CognitiveEnabled,
                SharePointConfig = sharePointConfigControl1.SharePointInstallConfig,
                KeyVaultName = azurePaaSConfigControl1.KeyVaultName,
                AutomationAccountName = azurePaaSConfigControl1.AutomationAccountName,
                FoundryPromptEnabled = azureAIConfigControl1.FoundryPromptEnabled,
                FoundryPromptResourceName = azureAIConfigControl1.FoundryPromptResourceName,
                FoundryPromptDeploymentName = azureAIConfigControl1.FoundryPromptDeploymentName,
                FoundryPromptModelName = azureAIConfigControl1.FoundryPromptModelName,
                FoundryPromptModelVersion = azureAIConfigControl1.FoundryPromptModelVersion,
                FoundryPromptDeploymentType = azureAIConfigControl1.FoundryPromptDeploymentType,
                FoundryPromptCapacity = azureAIConfigControl1.FoundryPromptCapacity,
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
            azureStorageConfigControl1.SqlAuthMode = config.SqlAuthMode;
            azureStorageConfigControl1.SqlDatabaseUsers = config.SQLEntraDatabaseUsers;
            azureStorageConfigControl1.StorageAccount = config.StorageAccountName;
            azureStorageConfigControl1.ServiceBusName = config.ServiceBusName;
            azureStorageConfigControl1.ServiceBusEnabled = config.ServiceBusEnabled;

            azurePaaSConfigControl1.AppInsightsName = config.AppInsightsName;
            azurePaaSConfigControl1.AppServicePlanName = config.AppServicePlanName;
            azurePaaSConfigControl1.AppServiceWebAppName = config.AppServiceWebAppName;
            azureAIConfigControl1.CognitiveServiceName = config.CognitiveServiceName;
            azureAIConfigControl1.CognitiveEnabled = config.CognitiveServicesEnabled;
            azurePaaSConfigControl1.AppInsightsWorkspaceName = config.AppInsightsWorkspaceName;
            azurePaaSConfigControl1.KeyVaultName = config.KeyVaultName;
            azurePaaSConfigControl1.AutomationAccountName = config.AutomationAccountName;
            azureAIConfigControl1.FoundryPromptEnabled = config.FoundryPromptEnabled;
            azureAIConfigControl1.FoundryPromptResourceName = config.FoundryPromptResourceName;
            azureAIConfigControl1.FoundryPromptDeploymentName = config.FoundryPromptDeploymentName;
            azureAIConfigControl1.FoundryPromptModelName = config.FoundryPromptModelName;
            azureAIConfigControl1.FoundryPromptModelVersion = config.FoundryPromptModelVersion;
            azureAIConfigControl1.FoundryPromptDeploymentType = config.FoundryPromptDeploymentType;
            azureAIConfigControl1.FoundryPromptCapacity = config.FoundryPromptCapacity;

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

            // Keep the operation centre usable while locking configuration and navigation.
            bool isWorking = state == AppWaitState.Working;
            _isWorking = isWorking;
            foreach (Panel tp in _pages)
            {
                if (tp == tabInstall) continue;
                tp.Enabled = !isWorking;
            }
            installSolutionControl1.SetRunningState(isWorking);
            UpdateWizardNavigation();

            MainForm mainForm = (MainForm)this.ParentForm;
            mainForm.SetFormLoadingState(state);
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
                if (!_pages.Contains(tabSharePoint))
                {
                    _pages.Insert(_pages.IndexOf(tabSources), tabSharePoint);
                }
            }
            else
            {
                if (_pages.Contains(tabSharePoint))
                {
                    _pages.Remove(tabSharePoint);
                    if (_selectedPage == tabSharePoint) SelectPage(tabSources);
                }
            }
            RebuildNavigator();
            SelectPage(_selectedPage);
            UpdateWizardNavigation();
        }

        #region Sources GUI

        private void rdbLatest_CheckedChanged(object sender, EventArgs e)
        {
            UpdateSourcesGUI();
        }

        private void UpdateSourcesGUI()
        {
            grpLocalSources.Enabled = rdpSpecificLocation.Checked;
            grpLocalSources.Visible = rdpSpecificLocation.Checked;
        }

        private void ConfigureSourcesLayout()
        {
            tabSources.AutoScroll = true;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                ColumnCount = 1, Padding = new Padding(16), Font = new System.Drawing.Font("Segoe UI", 9F) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lblGUIAppSourcesHeader.Text = "Choose your release source";
            lblGUIAppSourcesHeader.Font = new System.Drawing.Font("Segoe UI", 18F, System.Drawing.FontStyle.Bold);
            lblGUIAppSourcesDesc.Text = "Use the stable release for a normal install. Expand local packages only when " +
                "you have downloaded a specific release or need an offline source.";
            pictureBox12.Visible = false;
            var files = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            files.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (var field in new[] { fileSelectionAITracker, fileSelectionWebjobActivity,
                fileSelectionWebjobAppInsights, fileSelectionControlPanel, fileSelectionWebsite })
            {
                field.Dock = DockStyle.Fill;
                field.Margin = new Padding(4, 8, 4, 8);
                files.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                files.Controls.Add(field, 0, files.RowCount++);
            }
            grpLocalSources.Text = "Advanced: local release packages";
            grpLocalSources.AutoSize = true;
            grpLocalSources.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grpLocalSources.Controls.Add(files);
            var latestRelease = new LinkLabel
            {
                Name = "checkLatestVersion",
                Text = "Check latest version",
                AccessibleDescription = "Opens the latest stable GitHub release and its upgrade notes in your browser.",
                AutoSize = true,
                Tag = $"https://github.com/{SoftwareReleaseConfig.GITHUB_REPO_OWNER}/{SoftwareReleaseConfig.GITHUB_REPO_NAME}/releases/latest"
            };
            latestRelease.LinkClicked += (sender, args) =>
            {
                var url = (string)latestRelease.Tag;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    latestRelease.LinkVisited = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't open {url}\r\n\r\n{ex.Message}", "Check latest version",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            foreach (Control field in new Control[] { lblGUIAppSourcesHeader, lblGUIAppSourcesDesc,
                rdbLatest, latestRelease, rdpSpecificLocation, grpLocalSources })
            {
                field.Dock = DockStyle.Fill;
                field.Margin = new Padding(0, 6, 0, 12);
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(field, 0, layout.RowCount++);
            }
            tabSources.Controls.Add(layout);
            UpdateSourcesGUI();
        }

        private void rdpSpecificLocation_CheckedChanged(object sender, EventArgs e)
        {
            UpdateSourcesGUI();
        }

        #endregion

        void StartBackgroundProcess(InstallTask task)
        {
            // Before anything changes state: every installer call below goes out through this proxy, and a
            // failure after SetFormGUIState(Working) would leave the form stuck in its working state (#613).
            if (!InstallerNetworkProxy.TryApplyProcessWide(ProxyConfig, _logger, out var proxyError))
            {
                MessageBox.Show(this, $"The installer proxy settings can't be used: {proxyError}\r\n\r\n" +
                    "Correct them in Window > Proxy Configuration, then try again.",
                    "Proxy Configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetFormGUIState(AppWaitState.Working);

            var config = GetConfigFromGUI();

            var softwareConfig = new SoftwareReleaseConfig();

            // Fresh cancellation source per run. Dispose the previous one (if any) defensively;
            // it should already be Disposed in RunWorkerCompleted but guard against unusual paths.
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            installSolutionControl1.BeginOperation(task == InstallTask.Install ? "Install / upgrade"
                : task == InstallTask.Test ? "Configuration tests" : "Remove SharePoint tracker", task == InstallTask.Install);

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
            if (e.Error != null) HandleNestedException(e.Error);
            installSolutionControl1.CompleteOperation();
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
            var config = GetConfigFromGUI();
            var workloads = WorkloadCheckboxes(importJobSettingsSelection).Where(checkbox => checkbox.Checked)
                .Select(checkbox => checkbox.Text).ToArray();
            using (var review = new DeploymentReviewForm(config, workloads))
            {
                if (review.ShowDialog(this) != DialogResult.OK) return;
            }

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
            if (!(this.ParentForm as MainForm).SaveLastSettings()) return;

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
                SelectPage(tabInstall);
                StartBackgroundProcess(InstallTask.UninstallFromSharePoint);
            }
        }

        #endregion
    }
}
