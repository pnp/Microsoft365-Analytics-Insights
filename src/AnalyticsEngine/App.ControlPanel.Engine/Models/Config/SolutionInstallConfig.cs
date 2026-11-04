using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Core;
using Common.Entities.Installer;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel.Engine
{
    /// <summary>
    /// The global wanted configuration for the solution. Inherited from BaseSolutionInstallConfig used by the web-jobs.
    /// </summary>
    public class SolutionInstallConfig : BaseSolutionInstallConfig
    {
        #region Constructors

        public SolutionInstallConfig() : base()
        {
            EnvironmentType = EnvironmentTypeEnum.Testing;
            TasksConfig = new InstallTasksConfig();
            SQLServerAdminPassword = string.Empty;
            AzureLocation = new AzureLocation();
            LocalSourceOverride = new LocalStorageInstallSourceInfo();
            Subscription = new AzureSubscription();
            SharePointConfig = new SharePointInstallConfig();
            InstallerAccount = new AppRegistrationCredentials();
            ActivityAccount = new AppRegistrationCredentials();

        }

        #endregion

        public EnvironmentTypeEnum EnvironmentType { get; set; } = EnvironmentTypeEnum.Testing;

        public CloudInstallEngine.Azure.PrivateNetworkManifest PrivateNetworking { get; set; } =
            new CloudInstallEngine.Azure.PrivateNetworkManifest();

        [JsonIgnore]
        public string SavedConfigFileName { get; private set; }

        [JsonIgnore]
        public InstallTasksConfig TasksConfig { get; set; } = new InstallTasksConfig();

        [JsonIgnore]
        public string SQLServerAdminPassword { get; set; } = string.Empty;

        [JsonIgnore]
        public AzureLocation AzureLocation
        {
            get
            {
                var allLocations = AzurePublicCloudEnumerator.GetAzureLocations();
                var location = allLocations.Where(l => l.Name == AzureLocationName).FirstOrDefault();
                if (location.Name == null)
                {
                    location = new AzureLocation();
                }
                return location;
            }
            set
            {
                this.AzureLocationName = value.Name;
            }
        }

        /// <summary>
        /// Local release override - file locations for each component of solution
        /// </summary>
        public LocalStorageInstallSourceInfo LocalSourceOverride { get; set; } = new LocalStorageInstallSourceInfo();

        public AzureSubscription Subscription { get; set; } = new AzureSubscription();

        public SharePointInstallConfig SharePointConfig { get; set; } = new SharePointInstallConfig();

        /// <summary>
        /// Account to create Azure resources with
        /// </summary>
        public AppRegistrationCredentials InstallerAccount { get; set; } = new AppRegistrationCredentials();

        /// <summary>
        /// Account to read Office 365 Activity API with.
        /// </summary>
        [JsonIgnore]
        public AppRegistrationCredentials RuntimeAccountOffice365 => ActivityAccount;

        /// <summary>
        /// RuntimeAccountOffice365. Name maintained to load legacy config files
        /// </summary>
        public AppRegistrationCredentials ActivityAccount { get; set; } = new AppRegistrationCredentials();

        public static SolutionInstallConfig NewConfig()
        {
            var c = new SolutionInstallConfig();
            c.SolutionConfig = new TargetSolutionConfig();        // Everything "true"/enabled by default

            c.AppInsightsName = string.Empty;
            c.AppInsightsWorkspaceName = string.Empty;
            c.AzureLocation = new AzureLocation();
            c.AllowTelemetry = true;
            c.AppServiceWebAppName = string.Empty;
            c.AppServicePlanName = string.Empty;
            c.StorageAccountName = string.Empty;
            c.ResourceGroupName = string.Empty;
            c.SQLServerAdminUsername = "sqladmin";
            c.SQLServerDatabaseName = string.Empty;
            c.SQLServerName = string.Empty;
            c.CognitiveServiceName = string.Empty;
            c.CognitiveServicesEnabled = true;
            c.FoundryPromptEnabled = false;
            c.ServiceBusEnabled = true;
            c.ServiceBusName = string.Empty;
            c.TasksConfig.InstallLatestSolutionContent = true;
            c.TasksConfig.UpgradeSchema = true;
            c.Tags = new List<AzTag>();
            c.NetworkConfig = new VNetConfig();
            c.HealthMonitoring = new HealthMonitoringConfig { Enabled = true };

            var sharePointInstallConfig = SharePointInstallConfig.Empty();
            c.SharePointConfig = sharePointInstallConfig;
            return c;
        }

        #region Load/Save Methods

        public static SolutionInstallConfigLoadResult LoadFromFile(string fileName, string password)
        {
            string jSon = null;
            var tryAgain = true;
            while (tryAgain)
                try
                {
                    jSon = File.ReadAllText(fileName);
                    tryAgain = false;
                }
                catch (IOException ex)
                {
                    var r = MessageBox.Show($"Error loading config file '{fileName}'.\n\n{ex.Message}",
                        "IO Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    tryAgain = (r == DialogResult.Retry);
                }

            if (jSon == null)
            {
                return SolutionInstallConfigLoadResult.NotLoaded($"'{Path.GetFileName(fileName)}' could not be read, so nothing was loaded from it.");
            }

            var result = SolutionInstallConfig.LoadFromJson(jSon, password);

            // Only a file that opened completely may be saved back to: one whose secrets did not decrypt would be
            // rewritten without them, and the private-networking checkpoint writes to this name as well.
            if (result.CanBecomeSaveTarget)
            {
                result.Config.SavedConfigFileName = Path.GetFullPath(fileName);
            }

            if (result.Config != null)
            {
                Console.WriteLine($"Loaded config version '{result.Config.ConfigSchemaVersion}' from '{fileName}'.");
            }

            return result;
        }

        public static SolutionInstallConfigLoadResult LoadFromJson(string json, string password)
        {
            SolutionInstallConfig config = null;
            try
            {
                config = JsonConvert.DeserializeObject<SolutionInstallConfig>(json);
            }
            catch (JsonException)
            {
                // Reported rather than replaced with a new configuration: the caller would otherwise treat a damaged
                // file as an empty one and could save the empty one over it.
            }
            if (config == null)
            {
                return SolutionInstallConfigLoadResult.NotLoaded("This is not an installer configuration file, or it is damaged, so nothing was loaded from it.");
            }

            var result = new SolutionInstallConfigLoadResult() { Config = config, DecryptedOk = true };

            // Decrypt passwords
            try
            {
                config.SQLServerAdminPassword = DecryptLoadedSecret(config.SQLServerAdminPasswordHash, password);
                config.RuntimeAccountOffice365.DecryptSecretFromLoadedHashProperty(password);
                config.InstallerAccount.DecryptSecretFromLoadedHashProperty(password);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Reset hashes, and anything decrypted before the failure: a wrong password can get past the
                // padding check on one value and fail on the next, and what it produced for the first is noise.
                config.SQLServerAdminPasswordHash = string.Empty;
                config.RuntimeAccountOffice365.SecretHash = string.Empty;
                config.InstallerAccount.SecretHash = string.Empty;
                config.SQLServerAdminPassword = string.Empty;
                config.RuntimeAccountOffice365.Secret = string.Empty;
                config.InstallerAccount.Secret = string.Empty;
                result.DecryptedOk = false;
            }

            // Defaults
            config.TasksConfig = new InstallTasksConfig();

            return result;
        }

        /// <summary>
        /// Decrypts a secret read from a config file, or returns an empty string when none was saved.
        /// </summary>
        /// <remarks>
        /// <see cref="StringCipher"/> has no integrity check, so a wrong password is only caught by the padding check,
        /// which random output passes about one time in 256. What comes out then is random bytes, not text. Reject it
        /// too, or a mistyped password would look like a successful load and the next save would keep the noise.
        /// </remarks>
        /// <exception cref="System.Security.Cryptography.CryptographicException">The password is wrong.</exception>
        internal static string DecryptLoadedSecret(string cipherText, string password)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            var plainText = StringCipher.Decrypt(cipherText, password);
            if (plainText.Any(c => c == '\uFFFD' || char.IsControl(c)))
            {
                throw new System.Security.Cryptography.CryptographicException(
                    "The value did not decrypt to text. The password is probably incorrect.");
            }
            return plainText;
        }

        public void Save(string fileName, string password)
        {
            var jSon = ToJson(password, includePrivateNetworking: true);

            var tryAgain = true;
            while (tryAgain)
                try
                {
                    File.WriteAllText(fileName, jSon);
                    SavedConfigFileName = Path.GetFullPath(fileName);
                    tryAgain = false;
                }
                catch (IOException ex)
                {
                    var result = MessageBox.Show($"Error saving config file '{fileName}'.\n\n{ex.Message}",
                        "IO Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    tryAgain = (result == DialogResult.Retry);
                }
        }

        public string ToJson(string password, bool includePrivateNetworking = false)
        {
            // Encrypt passwords
            if (!string.IsNullOrEmpty(this.SQLServerAdminPassword))
            {
                this.SQLServerAdminPasswordHash = StringCipher.Encrypt(this.SQLServerAdminPassword, password);
            }
            else
            {
                this.SQLServerAdminPasswordHash = string.Empty;
            }
            this.RuntimeAccountOffice365.EncryptSecretToHashProperty(password);
            this.InstallerAccount.EncryptSecretToHashProperty(password);

            return ToJObject(includePrivateNetworking).ToString(Formatting.None);
        }

        /// <summary>
        /// The snapshot of this configuration that the installer records in <c>sys_configs</c> with each install, and
        /// that the portal's Install log shows to Portal.Administration holders.
        /// </summary>
        /// <remarks>
        /// It carries no secret, not even an encrypted one: the encrypted values are protected only by the config-file
        /// password, which anyone holding them could try to guess offline. The saved config file keeps them; the snapshot
        /// is a record of what was configured. Needs no password for the same reason.
        /// </remarks>
        public string ToDatabaseSnapshotJson()
        {
            var json = ToJObject(includePrivateNetworking: false);
            InstallConfigSnapshotRedactor.RemoveSecrets(json);
            return json.ToString(Formatting.None);
        }

        Newtonsoft.Json.Linq.JObject ToJObject(bool includePrivateNetworking)
        {
            // What is written is this build's schema, whichever version the config was loaded from: without this, a 2.x
            // file re-saved without its removed properties would still say 2.x - in the file and in the config_states
            // row the installer records (ToDatabaseSnapshotJson).
            this.ConfigSchemaVersion = CurrentConfigSchemaVersion;

            var json = Newtonsoft.Json.Linq.JObject.FromObject(this);
            // Runtime/SQL config snapshots are not an ownership store. Only the saved installer file
            // carries the provenance journal; restoring an old SQL snapshot must not authorize deletes.
            if (!includePrivateNetworking) json.Remove(nameof(PrivateNetworking));
            return json;
        }

        internal void CheckpointPrivateNetworking(string password)
        {
            if (string.IsNullOrWhiteSpace(SavedConfigFileName))
                throw new CloudInstallEngine.Models.InstallException(
                    "Save the installer configuration to disk before changing private networking. Creator provenance must be durable before Azure changes.");

            var checkpoint = SavedConfigFileName + ".network-checkpoint-new";
            try
            {
                File.WriteAllText(checkpoint, ToJson(password, includePrivateNetworking: true));
                File.Replace(checkpoint, SavedConfigFileName, null);
            }
            finally
            {
                if (File.Exists(checkpoint)) File.Delete(checkpoint);
            }
        }

        public void PreservePrivateNetworkingStateFrom(SolutionInstallConfig previous)
        {
            if (previous == null) return;
            PrivateNetworking = previous.PrivateNetworking ?? new CloudInstallEngine.Azure.PrivateNetworkManifest();
            SavedConfigFileName = previous.SavedConfigFileName;
        }

        #endregion

        internal const string CopilotCostStorageRequiredError =
            "Copilot credit history (preview) needs an Azure storage account: its delegated connection is kept in Azure Table storage and it cannot run without it. Provide a storage account name, or turn the preview off.";

        /// <summary>
        /// Check basic settings are valid. Return list of errors.
        /// </summary>
        public override List<string> ValidatInputAndGetErrors()
        {
            var errs = base.ValidatInputAndGetErrors();
            if (HealthMonitoring == null)
                errs.Add("Health monitoring configuration is missing.");
            else
                errs.AddRange(HealthMonitoring.ValidatInputAndGetErrors());

            if (!SolutionConfig.ImportTaskSettings.HaveSomethingToDo())
            {
                errs.Add("Nothing configured to import from Office 365");
            }

            if (SolutionConfig.ImportTaskSettings.WebTraffic)
            {
                // Check SharePoint fields
                var spErrs = this.SharePointConfig.ValidatInputAndGetErrors();
                if (spErrs.Count > 0)
                {
                    errs.Add("SharePoint validation errors:");
                    errs.AddRange(spErrs);
                }
            }
            var duplicateTags = this.Tags.GroupBy(t => t.Name.ToLower()).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicateTags.Count > 0)
            {
                errs.Add($"Duplicate tag names: {string.Join(", ", duplicateTags)}");
            }

            // Software sources
            if (!this.DownloadLatestStable && !this.LocalSourceOverride.IsValid)
            {
                errs.Add($"Invalid local software source override. Please select {this.LocalSourceOverride.MAX_ZIPS} valid zip-files to deploy, or select 'download latest'.");
            }

            // Accounts
            var installAccountErrs = this.InstallerAccount.GetValidationErrors();
            if (installAccountErrs.Count > 0)
            {
                errs.Add("Installer account errors:");
                errs.AddRange(IndentMsgs(installAccountErrs));
            }
            var runtimeAccountOfficeErrs = this.RuntimeAccountOffice365.GetValidationErrors();
            if (runtimeAccountOfficeErrs.Count > 0)
            {
                errs.Add("Office 365 runtime account errors:");
                errs.AddRange(IndentMsgs(runtimeAccountOfficeErrs));
            }

            // AzLocation
            bool haveLocation = (this.AzureLocation.Name != null);
            if (!haveLocation)
            {
                errs.Add("Select an Azure region");
            }

            // Subscription
            bool haveSubScription = (this.Subscription != null && this.Subscription is AzureSubscription && this.Subscription.IsValidSubscription);
            if (!haveSubScription)
            {
                errs.Add("Select or enter valid Azure subscription details");
            }
            if (string.IsNullOrWhiteSpace(this.ResourceGroupName))
            {
                errs.Add("Provide an Azure resource group name");
            }
            else
            {
                bool isValidGroupName = IsRegexExComplaint(this.ResourceGroupName, @"^[-\w\._\(\)]+$", false);
                if (!isValidGroupName)
                {
                    errs.Add("Enter valid resource-group name");
                }
            }

            // Cognitive
            if (this.CognitiveServicesEnabled)
            {
                if (string.IsNullOrWhiteSpace(this.CognitiveServiceName))
                {
                    errs.Add("Provide an Cognitive Services name, or disable this service");
                }
                else
                {
                    bool isValidGroupName = IsRegexExComplaint(this.CognitiveServiceName, @"^[-\w\._\(\)]+$", false);
                    if (!isValidGroupName)
                    {
                        errs.Add("Enter valid Cognitive Services name.");
                    }
                }
            }

            // Automation account
            if (string.IsNullOrWhiteSpace(this.AutomationAccountName))
            {
                errs.Add("Provide an Automation account name.");
            }

            // Azure AI Foundry / Azure OpenAI
            if (this.FoundryPromptEnabled)
            {
                if (!CloudInstallEngine.Azure.OpenAIModelCatalog.DeploymentTypes.Contains(this.FoundryPromptDeploymentType))
                {
                    errs.Add("Choose a valid Azure OpenAI deployment type: Standard, DataZoneStandard or GlobalStandard.");
                }
                if (string.IsNullOrWhiteSpace(this.FoundryPromptResourceName) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(this.FoundryPromptResourceName, @"\A[a-z0-9][a-z0-9-]{0,61}[a-z0-9]\z"))
                {
                    errs.Add("Provide a valid Azure AI Foundry resource name (2-63 lowercase letters, numbers, or hyphens; begin and end with a letter or number).");
                }

                if (!IsRegexExComplaint(this.FoundryPromptDeploymentName ?? string.Empty, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z", false))
                {
                    errs.Add("Provide a valid Azure AI Foundry model deployment name (1-64 letters, numbers, periods, underscores, or hyphens).");
                }

                if (!IsRegexExComplaint(this.FoundryPromptModelName ?? string.Empty, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z", false))
                {
                    errs.Add("Provide a valid Azure OpenAI model name.");
                }

                if (!string.IsNullOrWhiteSpace(this.FoundryPromptModelVersion) &&
                    !IsRegexExComplaint(this.FoundryPromptModelVersion, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z", false))
                {
                    errs.Add("Enter a valid Azure OpenAI model version, or leave it blank to use the Azure default.");
                }

                if (this.FoundryPromptCapacity < 1 || this.FoundryPromptCapacity > 10000)
                {
                    errs.Add("Azure OpenAI deployment capacity must be between 1 and 10000.");
                }
            }

            // Key Vault
            if (string.IsNullOrWhiteSpace(this.KeyVaultName))
            {
                errs.Add("Provide a key vault service name.");
            }
            else
            {
                bool isValidName = IsRegexExComplaint(this.KeyVaultName, @"^[-\w\._\(\)]+$", false);
                if (!isValidName)
                {
                    errs.Add("Enter valid key vault service name.");
                }

                if (this.KeyVaultName.Length < 3 || this.KeyVaultName.Length > 24)
                {
                    errs.Add("Key vault names must be between 3-24 characters");
                }
            }

            // ServiceBus
            if (this.ServiceBusEnabled)
            {
                if (string.IsNullOrWhiteSpace(this.ServiceBusName))
                {
                    errs.Add("Provide a service-bus name.");
                }
                else
                {
                    bool isValidName = IsRegexExComplaint(this.ServiceBusName, @"^[-\w\._\(\)]+$", false);
                    if (!isValidName)
                    {
                        errs.Add("Enter valid service-bus name.");
                    }

                    if (this.ServiceBusName.Length > 50 || this.ServiceBusName.Length < 6)
                    {
                        errs.Add("The service-bus name must be between 6 and 50 characters long.");
                    }
                }
            }
            else if (SolutionConfig != null && SolutionConfig.ImportTaskSettings != null && SolutionConfig.ImportTaskSettings.Calls)
            {
                errs.Add("Teams calls import is enabled, but Service Bus is disabled. Either enable Service Bus or disable the calls import.");
            }

            // SQL
            if (string.IsNullOrWhiteSpace(this.SQLServerDatabaseName))
            {
                errs.Add("Provide an Azure SQL database name");
            }
            if (string.IsNullOrWhiteSpace(this.SQLServerName))
            {
                errs.Add("Provide an Azure SQL Server name");
            }
            else
            {
                if (this.SQLServerName.Any(char.IsUpper))
                {
                    errs.Add("SQL Server names must be lower-case alphanumerics only");
                }
            }
            if (this.SqlAuthMode == SqlServerAuthMode.EntraId)
            {
                // Microsoft Entra ID authentication stores no SQL login, so the deprecated username/password
                // are not required. They are still accepted (and still used) when the installer is pointed at
                // an existing SQL-authentication server - see SqlServerAuthDetection.
                if (!string.IsNullOrWhiteSpace(this.SQLEntraAdminObjectId)
                    && !Guid.TryParse(this.SQLEntraAdminObjectId.Trim(), out _))
                {
                    errs.Add("The Microsoft Entra SQL administrator object ID must be a GUID. Leave it blank to use the installer's own service principal.");
                }
                if (!string.IsNullOrWhiteSpace(this.SQLEntraAdminObjectId) && string.IsNullOrWhiteSpace(this.SQLEntraAdminLogin))
                {
                    errs.Add("Provide the login/display name of the Microsoft Entra SQL administrator alongside its object ID.");
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(this.SQLServerAdminUsername))
                {
                    errs.Add("Provide a Azure SQL administrator username");
                }
                if (string.IsNullOrWhiteSpace(this.SQLServerAdminPassword))
                {
                    errs.Add("Provide an Azure SQL Server password");
                }
            }

            if (!string.IsNullOrWhiteSpace(this.SQLServerAdminPassword) && this.SQLServerAdminPassword.Contains(";"))
            {
                errs.Add("SQL password cannot contain semi-colons (';')");
            }

            // Storage
            if (string.IsNullOrWhiteSpace(this.StorageAccountName))
            {
                errs.Add("Provide an Azure storage account name");
            }
            else
            {
                if (!IsRegexExComplaint(this.StorageAccountName, @"^[a-z0-9]{3,24}$", true))
                {
                    errs.Add("Storage-account names can contain only lowercase letters and numbers. Name must be between 3 and 24 characters.");
                }
            }

            // Copilot credit history (preview) keeps its delegated connection in Azure Table storage and has no
            // in-memory fallback, so say so explicitly rather than leave the generic storage error to explain it.
            if (SolutionConfig?.ImportTaskSettings?.CopilotCostManagement == true && string.IsNullOrWhiteSpace(this.StorageAccountName))
            {
                errs.Add(CopilotCostStorageRequiredError);
            }

            // App Insights + Log Analytics
            if (string.IsNullOrWhiteSpace(this.AppInsightsName))
            {
                errs.Add("Provide an Application Insights name");
            }
            if (string.IsNullOrWhiteSpace(this.AppInsightsWorkspaceName))
            {
                errs.Add("Provide an Application Insights Log Analytics workspace name");
            }
            if (this.AppInsightsName.Contains(" "))
            {
                errs.Add("Application Insights name cannot contain spaces");
            }
            if (this.AppInsightsWorkspaceName.Contains(" "))
            {
                errs.Add("Log Analytics workspace name cannot contain spaces");
            }

            if (string.IsNullOrWhiteSpace(this.AppServiceWebAppName))
            {
                errs.Add("Provide an App Service web-app name");
            }
            if (string.IsNullOrWhiteSpace(this.AppServicePlanName))
            {
                errs.Add("Provide an App Service plan name");
            }

            // VNet
            if (this.NetworkConfig != null && this.NetworkConfig.Enabled)
            {
                errs.AddRange(this.NetworkConfig.ValidatInputAndGetErrors());
            }

            return errs;
        }

        /// <summary>
        /// Adds an indent to each item
        /// </summary>
        private List<string> IndentMsgs(List<string> errs)
        {
            List<string> returnList = new List<string>();

            foreach (var msg in errs)
            {
                returnList.Add("  " + msg);
            }

            return returnList;
        }
    }

    /// <summary>
    /// What came of opening a config file.
    /// </summary>
    public class SolutionInstallConfigLoadResult
    {
        /// <summary>The configuration read, or null when <see cref="LoadedOk"/> is false.</summary>
        public SolutionInstallConfig Config { get; set; }

        /// <summary>
        /// False when the secrets could not be decrypted, normally a wrong password. <see cref="Config"/> then holds the
        /// other settings, with no secrets.
        /// </summary>
        public bool DecryptedOk { get; set; }

        /// <summary>False when the file could not be read or is not a configuration file.</summary>
        public bool LoadedOk => Config != null;

        /// <summary>Why nothing was loaded, when <see cref="LoadedOk"/> is false.</summary>
        public string Error { get; set; }

        /// <summary>
        /// Whether the file may be saved back to. Not after a failed load: saving would write the configuration without
        /// the secrets the file holds (or with a mistyped password) and lose them for good.
        /// </summary>
        public bool CanBecomeSaveTarget => LoadedOk && DecryptedOk;

        internal static SolutionInstallConfigLoadResult NotLoaded(string error) =>
            new SolutionInstallConfigLoadResult { Config = null, DecryptedOk = false, Error = error };
    }
}
