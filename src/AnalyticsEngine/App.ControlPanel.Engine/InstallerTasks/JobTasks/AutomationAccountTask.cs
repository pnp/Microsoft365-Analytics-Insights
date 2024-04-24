using Azure;
using Azure.Core;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Automation.Models;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace App.ControlPanel.Engine.InstallerTasks.Tasks
{
    /// <summary>
    /// Create automation account and set up account variables and credentials
    /// </summary>
    public class AutomationAccountTask : InstallTaskInAzResourceGroup<AutomationAccountResource>
    {
        public const string CONFIG_PARAM_NAME_SQL_SERVER = "SqlServer";
        public const string CONFIG_PARAM_NAME_SQL_DB = "SqlDatabase";
        public const string CONFIG_PARAM_NAME_WEEKS_TO_KEEP = "WeeksToKeep";

        public const string CONFIG_PARAM_NAME_SQL_USERNAME = "sqlusername";
        public const string CONFIG_PARAM_NAME_SQL_PASSWORD = "sqlpassword";

        public AutomationAccountTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags) : base(config, logger, azureLocation, tags)
        {
        }

        public override async Task<AutomationAccountResource> ExecuteTaskReturnResult(object contextArg)
        {
            // Get/create app-service with plan
            var automationAccount = Container.GetAutomationAccounts().Where(s => s.Data.Name == _config.ResourceName).SingleOrDefault();
            if (automationAccount == null)
            {
                var newAutomationAccountInfo = new AutomationAccountCreateOrUpdateContent()
                {
                    Location = base.AzureLocation,
                    Sku = new AutomationSku(AutomationSkuName.Free),
                    Name = _config.ResourceName
                };
                base.EnsureTagsOnNew(newAutomationAccountInfo.Tags);     // Add configured tags

                _logger.LogInformation($"Creating Automation account '{_config.ResourceName}' ...");
                try
                {
                    var newAccountReq = await Container.GetAutomationAccounts().CreateOrUpdateAsync(WaitUntil.Completed, _config.ResourceName, newAutomationAccountInfo);
                    automationAccount = newAccountReq.Value;
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogError($"Failed to create Automation account '{_config.ResourceName}'. {ex.Message}. Skipping automation install.");
                    return null;
                }
                _logger.LogInformation($"Created Automation account '{automationAccount.Data.Name}' at SKU '{nameof(AutomationSkuName.Free)}'.");
            }
            else
            {
                _logger.LogInformation($"Using existing Automation account'{automationAccount.Data.Name}'.");
                await base.EnsureTagsOnExisting(automationAccount.Data.Tags, automationAccount.GetTagResource());
            }

            // Vars
            _logger.LogInformation($"Creating/updating automation variables for '{_config.ResourceName}'...");
            var varSqlServer = new AutomationVariableCreateOrUpdateContent(CONFIG_PARAM_NAME_SQL_SERVER)
            {
                Value = $"\"{_config[CONFIG_PARAM_NAME_SQL_SERVER]}\"",
                IsEncrypted = false,
                Description = "SQL Server name"
            };
            var varSqlDb = new AutomationVariableCreateOrUpdateContent(CONFIG_PARAM_NAME_SQL_DB)
            {
                Value = $"\"{_config[CONFIG_PARAM_NAME_SQL_DB]}\"",
                IsEncrypted = false,
                Description = "SQL Database name"
            };
            var varWeeksToKeep = new AutomationVariableCreateOrUpdateContent(CONFIG_PARAM_NAME_WEEKS_TO_KEEP)
            {
                Value = "52", // 1 year
                IsEncrypted = false,
                Description = "Number of weeks to keep data"
            };

            // Update variables
            var variables = automationAccount.GetAutomationVariables();
            await UpdateVarCatchArgumentNullException(variables, CONFIG_PARAM_NAME_SQL_SERVER, varSqlServer);
            await UpdateVarCatchArgumentNullException(variables, CONFIG_PARAM_NAME_SQL_DB, varSqlDb);
            await UpdateVarCatchArgumentNullException(variables, CONFIG_PARAM_NAME_WEEKS_TO_KEEP, varWeeksToKeep);

            // Creds
            _logger.LogInformation($"Creating/updating automation credentials for '{_config.ResourceName}'...");
            const string CRED_SQL_NAME = "SQLCredential";
            var credSql = new AutomationCredentialCreateOrUpdateContent(CRED_SQL_NAME, _config[CONFIG_PARAM_NAME_SQL_USERNAME], _config[CONFIG_PARAM_NAME_SQL_PASSWORD]);
            await automationAccount.GetAutomationCredentials().CreateOrUpdateAsync(WaitUntil.Completed, CRED_SQL_NAME, credSql);

            // Schedules
            _logger.LogInformation($"Creating/updating automation schedules for '{_config.ResourceName}'...");

            var nextSunday1pm = Next(DateTime.UtcNow.AddHours(13), DayOfWeek.Sunday);
            var nextSunday6pm = Next(DateTime.UtcNow.AddHours(13), DayOfWeek.Sunday);
            var nextSunday11pm = Next(DateTime.UtcNow.AddHours(13), DayOfWeek.Sunday);
            var nextSunday1pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 1pm", nextSunday1pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };
            var nextSunday6pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 6pm", nextSunday1pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };
            var nextSunday11pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 11pm", nextSunday1pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };

            var schedules = automationAccount.GetAutomationSchedules();
            await schedules.CreateOrUpdateAsync(WaitUntil.Completed, nextSunday1pmSchedule.Name, nextSunday1pmSchedule);
            await schedules.CreateOrUpdateAsync(WaitUntil.Completed, nextSunday6pmSchedule.Name, nextSunday6pmSchedule);
            await schedules.CreateOrUpdateAsync(WaitUntil.Completed, nextSunday11pmSchedule.Name, nextSunday11pmSchedule);

            return automationAccount;
        }

        async Task UpdateVarCatchArgumentNullException(AutomationVariableCollection variables, string varName, AutomationVariableCreateOrUpdateContent varContent)
        {
            try
            {
                await variables.CreateOrUpdateAsync(WaitUntil.Completed, varName, varContent);
            }
            catch (ArgumentNullException)
            {
                // Ignore. https://github.com/Azure/azure-sdk-for-net/issues/34261
            }
        }

        public static DateTime Next(DateTime from, DayOfWeek dayOfWeek)
        {
            int start = (int)from.DayOfWeek;
            int target = (int)dayOfWeek;
            if (target <= start)
                target += 7;
            return from.AddDays(target - start);
        }
    }
}
