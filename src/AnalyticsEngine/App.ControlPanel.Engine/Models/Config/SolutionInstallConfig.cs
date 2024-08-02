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
            c.RedisName = string.Empty;
            c.CognitiveServicesEnabled = true;
            c.ServiceBusName = string.Empty;
            c.TasksConfig.InstallLatestSolutionContent = true;
            c.TasksConfig.UpgradeSchema = true;
            c.Tags = new List<AzTag>();

            var sharePointInstallConfig = SharePointInstallConfig.Empty();
            c.SharePointConfig = sharePointInstallConfig;
            return c;
        }

        #region Load/Save Methods

        public static SolutionInstallConfigLoadResult LoadFromFile(string fileName, string password)
        {
            var jSon = string.Empty;
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

            var result = SolutionInstallConfig.LoadFromJson(jSon, password);

            Console.WriteLine($"Loaded config version '{result.Config.ConfigSchemaVersion}' from '{fileName}'.");

            return result;
        }

        public static SolutionInstallConfigLoadResult LoadFromJson(string json, string password)
        {
            SolutionInstallConfig config = null;
            try
            {
                config = JsonConvert.DeserializeObject<SolutionInstallConfig>(json);
            }
            catch (JsonReaderException)
            {
                config = SolutionInstallConfig.NewConfig();
            }

            var result = new SolutionInstallConfigLoadResult() { Config = config, DecryptedOk = true };

            // Decrypt passwords
            try
            {
                if (!string.IsNullOrEmpty(config.SQLServerAdminPasswordHash))
                    config.SQLServerAdminPassword = StringCipher.Decrypt(config.SQLServerAdminPasswordHash, password);
                else
                    config.SQLServerAdminPassword = string.Empty;

                config.RuntimeAccountOffice365.DecryptSecretFromLoadedHashProperty(password);
                config.InstallerAccount.DecryptSecretFromLoadedHashProperty(password);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Reset hashes
                config.SQLServerAdminPasswordHash = string.Empty;
                config.RuntimeAccountOffice365.SecretHash = string.Empty;
                config.InstallerAccount.SecretHash = string.Empty;
                result.DecryptedOk = false;
            }

            // Defaults
            config.TasksConfig = new InstallTasksConfig();

            return result;
        }

        public void Save(string fileName, string password)
        {
            var jSon = ToJson(password);

            var tryAgain = true;
            while (tryAgain)
                try
                {
                    File.WriteAllText(fileName, jSon);
                    tryAgain = false;
                }
                catch (IOException ex)
                {
                    var result = MessageBox.Show($"Error saving config file '{fileName}'.\n\n{ex.Message}",
                        "IO Error", MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    tryAgain = (result == DialogResult.Retry);
                }
        }

        public string ToJson(string password)
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

            return JsonConvert.SerializeObject(this);
        }

        #endregion

        /// <summary>
        /// Check basic settings are valid. Return list of errors.
        /// </summary>
        public override List<string> ValidatInputAndGetErrors()
        {
            var errs = base.ValidatInputAndGetErrors();

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

            if (SolutionConfig.SolutionTargeted == SolutionImportType.Adoptify)
            {
                // Check Adoptify config if targeted
                errs.AddRange(SolutionConfig.Adoptify.ValidatInputAndGetErrors());
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

            // Redis
            if (string.IsNullOrWhiteSpace(this.RedisName))
            {
                errs.Add("Provide a redis service name.");
            }
            else
            {
                bool isValidName = IsRegexExComplaint(this.RedisName, @"^[-\w\._\(\)]+$", false);
                if (!isValidName)
                {
                    errs.Add("Enter valid redis service name.");
                }
            }

            // Automation account
            if (string.IsNullOrWhiteSpace(this.AutomationAccountName))
            {
                errs.Add("Provide an Automation account name.");
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
            if (string.IsNullOrWhiteSpace(this.SQLServerAdminUsername))
            {
                errs.Add("Provide a Azure SQL administrator username");
            }
            if (string.IsNullOrWhiteSpace(this.SQLServerAdminPassword))
            {
                errs.Add("Provide an Azure SQL Server password");
            }
            else
            {
                if (this.SQLServerAdminPassword.Contains(";"))
                {
                    errs.Add("SQL password cannot contain semi-colons (';')");
                }
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
    /// For getting load results in case decryption fails due to bad password. Some config restored anyway.
    /// </summary>
    public class SolutionInstallConfigLoadResult
    {
        public SolutionInstallConfig Config { get; set; }
        public bool DecryptedOk { get; set; }
    }
}
