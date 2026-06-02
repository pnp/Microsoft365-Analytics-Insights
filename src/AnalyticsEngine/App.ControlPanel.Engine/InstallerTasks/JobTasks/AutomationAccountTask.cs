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

        private readonly bool _allowPublicAccess;

        public AutomationAccountTask(TaskConfig config, ILogger logger, AzureLocation azureLocation, Dictionary<string, string> tags, bool allowPublicAccess = true) : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
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
                    Name = _config.ResourceName,
                    IsPublicNetworkAccessAllowed = _allowPublicAccess
                };
                base.EnsureTagsOnNew(newAutomationAccountInfo.Tags);     // Add configured tags

                _logger.LogInformation($"Creating Automation account '{_config.ResourceName}' (public access: {(_allowPublicAccess ? "enabled" : "disabled")}) ...");
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

                if (automationAccount.Data.IsPublicNetworkAccessAllowed != _allowPublicAccess)
                {
                    _logger.LogInformation($"Updating Automation account '{automationAccount.Data.Name}' public network access to '{(_allowPublicAccess ? "enabled" : "disabled")}'...");
                    var patch = new AutomationAccountPatch
                    {
                        IsPublicNetworkAccessAllowed = _allowPublicAccess
                    };
                    try
                    {
                        var updateReq = await automationAccount.UpdateAsync(patch);
                        automationAccount = updateReq.Value;
                    }
                    catch (RequestFailedException ex)
                    {
                        _logger.LogWarning($"Could not update Automation account '{_config.ResourceName}' public access setting: {ex.Message}");
                    }
                }
            }

            // Vars
            // Re-running the installer must NOT overwrite operator-customized variable values
            // (e.g. an operator who increased WeeksToKeep from 52 to 104). Only create when
            // a variable with this name doesn't already exist.
            _logger.LogInformation($"Ensuring automation variables exist for '{_config.ResourceName}'...");
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

            var variables = automationAccount.GetAutomationVariables();
            await CreateVariableIfMissingAsync(variables, CONFIG_PARAM_NAME_SQL_SERVER, varSqlServer);
            await CreateVariableIfMissingAsync(variables, CONFIG_PARAM_NAME_SQL_DB, varSqlDb);
            await CreateVariableIfMissingAsync(variables, CONFIG_PARAM_NAME_WEEKS_TO_KEEP, varWeeksToKeep);

            // Creds
            _logger.LogInformation($"Creating/updating automation credentials for '{_config.ResourceName}'...");
            const string CRED_SQL_NAME = "SQLCredential";
            var credSql = new AutomationCredentialCreateOrUpdateContent(CRED_SQL_NAME, _config[CONFIG_PARAM_NAME_SQL_USERNAME], _config[CONFIG_PARAM_NAME_SQL_PASSWORD]);
            await automationAccount.GetAutomationCredentials().CreateOrUpdateAsync(WaitUntil.Completed, CRED_SQL_NAME, credSql);

            // Schedules
            // Re-running the installer must NOT update existing schedules: doing so would reset
            // the StartTime to a fresh "next Sunday" calculated at install time, which can disrupt
            // schedule-runbook job bindings and confuse operators who have linked these schedules
            // to runbooks manually. Only create when a schedule with this name doesn't already exist.
            _logger.LogInformation($"Ensuring automation schedules exist for '{_config.ResourceName}'...");

            var nextSunday1pm = NextSundayAt(13, DateTimeKind.Utc);
            var nextSunday6pm = NextSundayAt(18, DateTimeKind.Utc);
            var nextSunday11pm = NextSundayAt(23, DateTimeKind.Utc);
            var nextSunday1pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 1pm", nextSunday1pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };
            var nextSunday6pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 6pm", nextSunday6pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };
            var nextSunday11pmSchedule = new AutomationScheduleCreateOrUpdateContent("Weekly Sunday 11pm", nextSunday11pm, AutomationScheduleFrequency.Week) { Interval = BinaryData.FromString("1") };

            var schedules = automationAccount.GetAutomationSchedules();
            await CreateScheduleIfMissingAsync(schedules, nextSunday1pmSchedule);
            await CreateScheduleIfMissingAsync(schedules, nextSunday6pmSchedule);
            await CreateScheduleIfMissingAsync(schedules, nextSunday11pmSchedule);

            return automationAccount;
        }

        async Task CreateVariableIfMissingAsync(AutomationVariableCollection variables, string varName, AutomationVariableCreateOrUpdateContent varContent)
        {
            if ((await variables.ExistsAsync(varName)).Value)
            {
                _logger.LogInformation($"Automation variable '{varName}' already exists in '{_config.ResourceName}' — leaving existing value untouched.");
                return;
            }

            try
            {
                await variables.CreateOrUpdateAsync(WaitUntil.Completed, varName, varContent);
                _logger.LogInformation($"Created automation variable '{varName}'.");
            }
            catch (ArgumentNullException)
            {
                // Ignore. https://github.com/Azure/azure-sdk-for-net/issues/34261
            }
        }

        async Task CreateScheduleIfMissingAsync(AutomationScheduleCollection schedules, AutomationScheduleCreateOrUpdateContent scheduleContent)
        {
            if ((await schedules.ExistsAsync(scheduleContent.Name)).Value)
            {
                _logger.LogInformation($"Automation schedule '{scheduleContent.Name}' already exists in '{_config.ResourceName}' — leaving existing start time and runbook bindings untouched.");
                return;
            }

            await schedules.CreateOrUpdateAsync(WaitUntil.Completed, scheduleContent.Name, scheduleContent);
            _logger.LogInformation($"Created automation schedule '{scheduleContent.Name}'.");
        }

        public static DateTime Next(DateTime from, DayOfWeek dayOfWeek)
        {
            int start = (int)from.DayOfWeek;
            int target = (int)dayOfWeek;
            if (target <= start)
                target += 7;
            return from.AddDays(target - start);
        }

        /// <summary>
        /// Returns a DateTime object for the next Sunday at a specific time
        /// </summary>
        /// <param name="hour"></param>
        /// <returns></returns>
        /// <exception cref="NotImplementedException"></exception>
        public static DateTime NextSundayAt(int hour24, DateTimeKind kind = DateTimeKind.Local)
        {
            var now = kind == DateTimeKind.Utc ? DateTime.UtcNow : DateTime.Now;
            var nextSunday = Next(now, DayOfWeek.Sunday);
            return new DateTime(nextSunday.Year, nextSunday.Month, nextSunday.Day, hour24, 0, 0, kind);
        }
    }
}
