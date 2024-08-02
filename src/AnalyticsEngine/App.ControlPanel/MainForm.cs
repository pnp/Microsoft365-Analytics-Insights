using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Models;
using DataUtils;
using System;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class MainForm : System.Windows.Forms.Form, ISolutionConfigurableComponent
    {
        public MainForm()
        {
            InitializeComponent();

            // Defaults set on load
        }

        #region Props

        /// <summary>
        /// Current displayed UI element
        /// </summary>
        public ISolutionConfigurableComponent SelectedUI { get; set; }

        /// <summary>
        /// Last loaded config
        /// </summary>
        public SolutionInstallConfig LoadedConfig { get; set; }

        /// <summary>
        /// Last password entered for config file
        /// </summary>
        public string LastPassword { get; set; }

        /// <summary>
        /// Is there a saved configuration open?
        /// </summary>
        public bool HaveSavedConfig { get; set; }
        public string LastConfigFilePath { get; set; }

        public InstallerPreferences SavedPreferences { get; set; }

        #endregion

        private void StartForm_Load(object sender, EventArgs e)
        {
            SetMenu(MainVisualState.MainMenu);
            SetFormLoadingState(AppWaitState.Ready);

            NewConfig();

            // Clear debug UI
            var buildLabel = System.Configuration.ConfigurationManager.AppSettings["BuildLabel"];
            this.Text = "Office 365 Advanced Analytics Engine Installation - " + buildLabel;
#if DEBUG
            this.Text += " - DEBUG";
            chkDisclaimer.Checked = true;
            mnuDebug.Visible = true;
#else
            chkDisclaimer.Checked = false;
#endif
            this.SavedPreferences = SecureLocalPreferences.Load<InstallerPreferences>();
            installSPOSitesControl.FtpConfig = SavedPreferences.FtpConfig;

            // Overwrite tests config if we're using default settings, or we don't have any saved for some reason
            if (this.SavedPreferences.TestsConfig == null)
            {
                this.SavedPreferences.TestsConfig = new TestConfiguration();
            }

            installSPOSitesControl.TestsConfig = SavedPreferences.TestsConfig;

            EnableDisableControls();
        }

        #region Display Functions

        private void EnableDisableControls()
        {
            btnStartInstall.Enabled = chkDisclaimer.Checked;
            solutionTestsConfigurationToolStripMenuItem.Enabled = chkDisclaimer.Checked;
            mnuOpenConfig.Enabled = chkDisclaimer.Checked;
            mnuNewConfig.Enabled = chkDisclaimer.Checked;
            mnuSaveConfigAs.Enabled = chkDisclaimer.Checked;
        }

        void SetMenu(MainVisualState state)
        {
            switch (state)
            {
                case MainVisualState.MainMenu:
                    SelectedUI = this;
                    break;
                case MainVisualState.Install:
                    // Unlock GUI
                    mnuWindow.Visible = true;

                    SelectedUI = installSPOSitesControl;
                    break;
                default:
                    throw new InvalidOperationException("Unknown UI state");
            }

            mnuUpgradeDatabaseSchemaToolStripMenuItem.Visible = state == MainVisualState.Install;
            mnuOpenConfig.Visible = state == MainVisualState.Install;
            mnuNewConfig.Visible = false;       // We don't want people creating new configs halfway through an existing one for now.
            mnuSaveConfigAs.Visible = state == MainVisualState.Install;
            mnuSaveConfig.Visible = state == MainVisualState.Install;
            mnuFileSep.Visible = state == MainVisualState.Install;

            // Load new UI with latest config state
            SelectedUI.ConfigureUI(this.LoadedConfig);

            grpStart.Visible = (state == MainVisualState.MainMenu);
            grpInstall.Visible = (state == MainVisualState.Install);

            grpStart.Dock = DockStyle.Fill;
            grpInstall.Dock = DockStyle.Fill;
        }

        enum MainVisualState
        {
            MainMenu = 0,
            Install = 3
        }

        private void btnStartInstall_Click(object sender, EventArgs e)
        {

            SetMenu(MainVisualState.Install);
        }

        #endregion

        #region Event Handling

        private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new AboutForm().ShowDialog();
        }

        private void upgradeDatabaseSchemaToolStripMenuItem_Click(object sender, EventArgs e)
        {
            new DatabaseUpgradeForm().ShowDialog();
        }

        private void proxyConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var f = new ProxyConfigForm();
            f.FtpConfig = this.SavedPreferences.FtpConfig;
            if (f.ShowDialog() == DialogResult.OK)
            {
                installSPOSitesControl.FtpConfig = f.FtpConfig;
                this.SavedPreferences.FtpConfig = f.FtpConfig;
                this.SavedPreferences.SaveToTempFile();
            }
        }

        private void mnuSaveConfig_Click(object sender, EventArgs e)
        {
            SaveLastSettings();
        }

        private void chkDisclaimer_CheckedChanged(object sender, EventArgs e)
        {
            EnableDisableControls();
        }

        private void mnuExitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void installSolutionToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SetMenu(MainVisualState.Install);
        }

        private void clearSettingsToolStripMenuItem_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.Reset();
            this.SavedPreferences.DeleteTempFile();
            this.SavedPreferences = new InstallerPreferences();
            MessageBox.Show("Settings reset to default");
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void mnuOpenConfig_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                var passwordForm = (new EnterPasswordForm());
                if (passwordForm.ShowDialog() == DialogResult.OK)
                {
                    // Get config from file
                    var loadedConfig = SolutionInstallConfig.LoadFromFile(openFileDialog.FileName, passwordForm.Password);

                    if (!loadedConfig.DecryptedOk)
                    {
                        MessageBox.Show("Could not decrypt this file so some configuration is not loaded. Wrong password?", "Open File", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }

                    // Set as most current
                    this.LoadedConfig = loadedConfig.Config;

                    // Refresh current UI with new config
                    this.SelectedUI.ConfigureUI(loadedConfig.Config);

                    // Remember state
                    SetCurrentSaveContext(openFileDialog.FileName, passwordForm.Password);
                }
            }
        }


        private void mnuSaveConfigAsNewFile_Click(object sender, EventArgs e)
        {
            SaveNewSettingsFile();
        }
        private void mnuNewConfig_Click(object sender, EventArgs e)
        {
            NewConfig();
        }
        #endregion


        public void ConfigureUI(SolutionInstallConfig config)
        {
            // Do nothing. We don't care about new config on this specific form
        }

        public SolutionInstallConfig GetConfigurationState()
        {
            // Just return last loaded config
            return this.LoadedConfig;
        }


        public void SaveLastSettings()
        {
            if (this.HaveSavedConfig)
            {
                SaveCurrentSettings(this.LastConfigFilePath, this.LastPassword);
            }
            else
            {
                SaveNewSettingsFile();
            }
        }

        public void SaveNewSettingsFile()
        {
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                var passwordForm = (new EnterPasswordForm());
                if (passwordForm.ShowDialog() == DialogResult.OK)
                {
                    SaveCurrentSettings(saveFileDialog.FileName, passwordForm.Password);
                }
            }
        }

        public void SaveCurrentSettings(string fileName, string password)
        {

            // Get latest config from current UI
            this.LoadedConfig = this.SelectedUI.GetConfigurationState();

            // Save to disk
            this.LoadedConfig.Save(fileName, password);

            // Remember state
            SetCurrentSaveContext(fileName, password);
        }


        private void SetCurrentSaveContext(string fileName, string password)
        {
            System.IO.FileInfo configFileInfo = new System.IO.FileInfo(fileName);
            mnuSaveConfig.Text = $"Save {configFileInfo.Name}";
            this.LastConfigFilePath = fileName;
            this.LastPassword = password;
            this.HaveSavedConfig = true;
            mnuSaveConfig.Enabled = true;


            txtConfigFile.Text = $"Loaded '{configFileInfo.Name}', modified: '{configFileInfo.LastWriteTime}'";
        }


        private void NewConfig()
        {
            this.HaveSavedConfig = false;
            this.LoadedConfig = SolutionInstallConfig.NewConfig();
            this.LastPassword = string.Empty;
            mnuSaveConfig.Enabled = false;
            txtConfigFile.Text = "No configuration file loaded. Create new one or open existing.";

            // Refresh current UI with new config
            this.SelectedUI.ConfigureUI(LoadedConfig);
        }

        internal void SetFormLoadingState(AppWaitState state)
        {
            toolStripLoading.Visible = state == AppWaitState.Working;
        }

        private void solutionTestsConfigurationToolStripMenuItem_Click(object sender, EventArgs e)
        {

            var f = new TestSettingsForm();
            f.SolutionInstallConfig = this.SelectedUI.GetConfigurationState();            // Give current config so it can lookup FTP & SQL details

            f.TestConfiguration = SavedPreferences.TestsConfig;     // Currently configured test config
            f.FtpConfig = SavedPreferences.FtpConfig;
            if (f.ShowDialog() == DialogResult.OK)
            {
                var newConfig = f.TestConfiguration;
                installSPOSitesControl.TestsConfig = newConfig;
                this.SavedPreferences.TestsConfig = newConfig;
                this.SavedPreferences.SaveToTempFile();
            }
        }
    }
}
