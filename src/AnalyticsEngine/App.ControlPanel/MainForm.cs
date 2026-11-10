using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using DataUtils;
using System;
using System.IO;
using System.Windows.Forms;

namespace App.ControlPanel
{
    public partial class MainForm : System.Windows.Forms.Form, ISolutionConfigurableComponent
    {
        public MainForm()
        {
            InitializeComponent();
            RetryFailedSave = message => MessageBox.Show(this, message, "Save Configuration",
                MessageBoxButtons.RetryCancel, MessageBoxIcon.Error) == DialogResult.Retry;
            ShowSaveNotice = message => MessageBox.Show(this, message, "Save Configuration",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            installSPOSitesControl.OpenConfigurationRequested += mnuOpenConfig_Click;
            installSPOSitesControl.NewConfigurationRequested += (sender, args) =>
            {
                if (MessageBox.Show(this, "Discard the current edits and start a new configuration?",
                    "New configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                    NewConfig();
            };

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

        /// <summary>Shown why a configuration file could not be saved; returns true to try again. Asks with a message box.</summary>
        public Func<string, bool> RetryFailedSave { get; set; }

        /// <summary>
        /// Shown what the operator needs to know about a configuration file just saved: where the copy kept for older installers is.
        /// Shows a message box.
        /// </summary>
        public Action<string> ShowSaveNotice { get; set; }

        #endregion

        private void StartForm_Load(object sender, EventArgs e)
        {
            SetMenu(MainVisualState.MainMenu);
            SetFormLoadingState(AppWaitState.Ready);

            NewConfig();

            // Clear debug UI
            var buildLabel = Common.Entities.BuildConstants.BuildLabel;
            this.Text = "Office 365 Advanced Analytics Engine Installation - " + buildLabel;
#if DEBUG
            this.Text += " - DEBUG";
            chkDisclaimer.Checked = true;
            mnuDebug.Visible = true;
#else
            chkDisclaimer.Checked = false;
#endif
            string unreadablePreferences = null;
            this.SavedPreferences = SecureLocalPreferences.Load<InstallerPreferences>(problem => unreadablePreferences = problem);
            if (unreadablePreferences != null)
            {
                // Without this the saved proxy would silently stop being used, and an install behind it would then fail.
                MessageBox.Show($"{unreadablePreferences}\r\n\r\nSet any proxy and solution-test settings again: choose Configure " +
                    "solution, then Window > Proxy Configuration or Solution Tests Configuration.",
                    "Installer Preferences", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (this.SavedPreferences.ProxyConfig == null)
            {
                this.SavedPreferences.ProxyConfig = new InstallerProxyConfig();
            }
            if (!InstallerNetworkProxy.TryApplyProcessWide(this.SavedPreferences.ProxyConfig, null, out var proxyError))
            {
                // The Window menu is hidden until the admin chooses Configure solution (SetMenu), so say how to
                // reach it: naming a menu that is not on screen sends them looking for something that isn't there.
                MessageBox.Show($"The saved installer proxy settings can't be used: {proxyError}\r\n\r\n" +
                    "The installer will use the system proxy until you correct them: choose Configure solution, " +
                    "then Window > Proxy Configuration.",
                    "Proxy Configuration", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            installSPOSitesControl.ProxyConfig = SavedPreferences.ProxyConfig;

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
            // Disposed explicitly: ShowDialog only hides the form, so without this it would linger with its
            // background worker attached until the installer exits.
            using (var f = new DatabaseUpgradeForm())
            {
                // Give the current config so the form can autodetect the target database and default the
                // Microsoft Entra ID credential to the installer's own app registration.
                f.SolutionInstallConfig = this.SelectedUI?.GetConfigurationState();
                f.ShowDialog();
            }
        }

        private void proxyConfigToolStripMenuItem_Click(object sender, EventArgs e)
        {
            var f = new ProxyConfigForm();
            f.ProxyConfig = this.SavedPreferences.ProxyConfig;
            if (f.ShowDialog() == DialogResult.OK)
            {
                installSPOSitesControl.ProxyConfig = f.ProxyConfig;
                this.SavedPreferences.ProxyConfig = f.ProxyConfig;
                InstallerNetworkProxy.ApplyProcessWide(this.SavedPreferences.ProxyConfig, null);
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
            ResetSavedPreferences();
            MessageBox.Show("Settings reset to default");
        }

        /// <summary>
        /// Deletes the saved preferences file and goes back to the defaults. Works when nothing was loaded, as when start-up
        /// stopped before reading the file.
        /// </summary>
        public void ResetSavedPreferences()
        {
            new InstallerPreferences().DeleteTempFile();
            this.SavedPreferences = new InstallerPreferences();
        }

        private void exitToolStripMenuItem_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void mnuOpenConfig_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                OpenConfigFile(openFileDialog.FileName, AskForPassword, AskAfterFailedDecryption,
                    error => MessageBox.Show(this, error, "Open File", MessageBoxButtons.OK, MessageBoxIcon.Error));
            }
        }

        /// <summary>
        /// Opens a config file. One that did not open completely - a wrong password, or a damaged file - never becomes
        /// the file that Save writes to: saving it back would replace the secrets it holds with blanks, and client
        /// secrets can't be read back from Azure.
        /// </summary>
        /// <param name="askForPassword">Returns the password to try, or null to give up.</param>
        /// <param name="askAfterFailedDecryption">
        /// Yes to try another password, No to open the other settings without the secrets, anything else to give up.
        /// </param>
        /// <param name="showError">Says why a file could not be opened at all.</param>
        public void OpenConfigFile(string fileName, Func<string> askForPassword,
            Func<string, DialogResult> askAfterFailedDecryption, Action<string> showError)
        {
            while (true)
            {
                var password = askForPassword();
                if (password == null) return;

                // Get config from file
                var loadedConfig = SolutionInstallConfig.LoadFromFile(fileName, password);
                if (!loadedConfig.LoadedOk)
                {
                    // Nothing to show, so whatever was open stays open, with its own save target.
                    showError(loadedConfig.Error);
                    return;
                }

                if (!loadedConfig.DecryptedOk)
                {
                    var choice = askAfterFailedDecryption(fileName);
                    if (choice == DialogResult.Yes) continue;
                    if (choice != DialogResult.No) return;
                }

                // Set as most current
                this.LoadedConfig = loadedConfig.Config;

                // Refresh current UI with new config
                this.SelectedUI.ConfigureUI(loadedConfig.Config);

                // Remember state
                if (loadedConfig.CanBecomeSaveTarget)
                {
                    SetCurrentSaveContext(fileName, password);
                }
                else
                {
                    ClearSaveContext($"Opened '{Path.GetFileName(fileName)}' without its secrets. Saving asks for a new file, so this one is not changed.");
                }
                return;
            }
        }

        static string AskForPassword()
        {
            using (var passwordForm = new EnterPasswordForm())
            {
                return passwordForm.ShowDialog() == DialogResult.OK ? passwordForm.Password : null;
            }
        }

        DialogResult AskAfterFailedDecryption(string fileName)
        {
            return MessageBox.Show(this,
                $"Could not decrypt the secrets in '{Path.GetFileName(fileName)}' with that password.\r\n\r\n" +
                "Yes: enter the password again.\r\n" +
                "No: open the other settings without the SQL administrator password and the app registration secrets. " +
                "The file is not changed: saving asks for a new file.\r\n" +
                "Cancel: don't open the file.",
                "Open File", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button1);
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


        /// <summary>
        /// Saves to the file Save writes to, or asks for one. Installing calls it first, and doesn't start when it returns false.
        /// </summary>
        /// <returns>Whether the configuration was saved.</returns>
        public bool SaveLastSettings()
        {
            if (this.HaveSavedConfig)
            {
                return SaveCurrentSettings(this.LastConfigFilePath, this.LastPassword);
            }
            else
            {
                return SaveNewSettingsFile();
            }
        }

        public bool SaveNewSettingsFile()
        {
            if (saveFileDialog.ShowDialog() == DialogResult.OK)
            {
                using (var passwordForm = new EnterPasswordForm { ConfirmPassword = true })
                {
                    if (passwordForm.ShowDialog() == DialogResult.OK)
                    {
                        return SaveCurrentSettings(saveFileDialog.FileName, passwordForm.Password);
                    }
                }
            }
            return false;
        }

        /// <returns>
        /// Whether the file was saved. When it wasn't, it is unchanged, and Save still writes to the file it wrote to before.
        /// </returns>
        public bool SaveCurrentSettings(string fileName, string password)
        {

            // Get latest config from current UI
            this.LoadedConfig = this.SelectedUI.GetConfigurationState();

            // Save to disk
            if (!this.LoadedConfig.Save(fileName, password, RetryFailedSave, ShowSaveNotice)) return false;

            // Remember state
            SetCurrentSaveContext(fileName, password);
            return true;
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

        /// <summary>Forgets the file Save writes to, so the next save asks for one.</summary>
        private void ClearSaveContext(string status)
        {
            mnuSaveConfig.Text = "Save Configuration";
            this.LastConfigFilePath = null;
            this.LastPassword = string.Empty;
            this.HaveSavedConfig = false;
            mnuSaveConfig.Enabled = false;

            txtConfigFile.Text = status;
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
            mnuOpenConfig.Enabled = state == AppWaitState.Ready && chkDisclaimer.Checked;
            mnuNewConfig.Enabled = state == AppWaitState.Ready && chkDisclaimer.Checked;
            mnuSaveConfigAs.Enabled = state == AppWaitState.Ready;
            mnuSaveConfig.Enabled = state == AppWaitState.Ready && HaveSavedConfig;
            mnuWindow.Enabled = state == AppWaitState.Ready;
            mnuUpgradeDatabaseSchemaToolStripMenuItem.Enabled = state == AppWaitState.Ready;
        }

        private void solutionTestsConfigurationToolStripMenuItem_Click(object sender, EventArgs e)
        {

            var f = new TestSettingsForm();
            f.SolutionInstallConfig = this.SelectedUI.GetConfigurationState();            // Give current config so it can look up SQL details

            f.TestConfiguration = SavedPreferences.TestsConfig;     // Currently configured test config
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
