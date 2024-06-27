using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.ResourceManager.Automation;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Storage;
using CloudInstallEngine;
using CloudInstallEngine.Azure.InstallTasks;
using Common.Entities.Installer;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace App.ControlPanel.Engine.InstallerTasks
{
    public class DownloadLatestAppServiceContentInstallJob : AppServiceContentInstallJob
    {
        public DownloadLatestAppServiceContentInstallJob(ILogger logger, SubscriptionResource subscription, SoftwareReleaseConfig softwareReleaseConfig,
            InstallerFtpConfig ftpConfig, SolutionInstallConfig config, bool downloadReleaseOnly, StorageAccountResource storageAccount,
            AutomationAccountResource automationAccount)
            : base(logger, subscription, null, softwareReleaseConfig, config, ftpConfig, downloadReleaseOnly, storageAccount, automationAccount)
        {
        }
    }

    public class UseLocalAppServiceContentInstallJob : AppServiceContentInstallJob
    {
        public UseLocalAppServiceContentInstallJob(ILogger logger, SubscriptionResource subscription, LocalStorageInstallSourceInfo localOverrideSources,
            InstallerFtpConfig ftpConfig, SolutionInstallConfig config, bool downloadReleaseOnly, StorageAccountResource storageAccount,
            AutomationAccountResource automationAccount)
            : base(logger, subscription, localOverrideSources, null, config, ftpConfig, downloadReleaseOnly, storageAccount, automationAccount)
        {
        }
    }

    /// <summary>
    /// Local or downloaded sources - works for both. Either scenario hard-coded with concrete class.
    /// </summary>
    public abstract class AppServiceContentInstallJob : BaseAnalyticsSolutionInstallJob
    {
        BaseInstallTask _sourceGetTask = null;
        public AppServiceContentInstallJob(ILogger logger, SubscriptionResource subscription, LocalStorageInstallSourceInfo localOverrideSources,
            SoftwareReleaseConfig softwareReleaseConfig, SolutionInstallConfig config, InstallerFtpConfig ftpConfig, bool downloadReleaseOnly,
            StorageAccountResource storageAccount, AutomationAccountResource automationAccount)
            : base(logger, config, subscription)
        {
            // If we have no local source or stable release info, we can't do much
            if (localOverrideSources == null && softwareReleaseConfig == null) throw new ArgumentNullException(nameof(softwareReleaseConfig));

            // Get local or from releases storage account?
            if (localOverrideSources == null)
            {
                var downloadLatestStableCfg = TaskConfig.GetConfigForPropAndVal(LatestStableSoftwarePackageDownloadTask.CFG_KEY_ContainerName, softwareReleaseConfig.ContainerName)
                            .AddSetting(LatestStableSoftwarePackageDownloadTask.CFG_KEY_AccountBaseUrl, softwareReleaseConfig.AccountBaseUrl)
                            .AddSetting(LatestStableSoftwarePackageDownloadTask.CFG_KEY_SAS, softwareReleaseConfig.SAS);

                _sourceGetTask = new LatestStableSoftwarePackageDownloadTask(downloadLatestStableCfg, logger);
            }
            else
                _sourceGetTask = new UseLocalOverrideDownloadTask(localOverrideSources, TaskConfig.NoConfig, logger);

            // Install solution?
            var installTasks = new List<BaseInstallTask> { _sourceGetTask };
            if (!downloadReleaseOnly)
            {
                installTasks.Add(new InstallAppServiceContentsTask(ftpConfig, TaskConfig.GetConfigForName(config.AppServiceWebAppName), logger, config.AzureLocation, config.Tags.ToDictionary()));
            }
            else
            {
                logger.LogInformation("Skipping install of solution packages");
            }

            // Deploy profiling PS?
            if (config.SolutionConfig.ImportTaskSettings.GraphUsageReports && config.TasksConfig.InstallLatestSolutionContent)
            {
                // Add to install tasks
                if (automationAccount != null)
                {
                    installTasks.Add(new RunChildJobTask(new RunbooksInstallJob(logger, config, subscription, storageAccount, automationAccount), logger));
                }
                else
                {
                    logger.LogError("Skipping runbook install - no automation account found");
                }
            }

            AddTasks(installTasks);
        }

        public LocalStorageInstallSourceInfo LocalStorageInstallSourceInfo => GetTaskResult<LocalStorageInstallSourceInfo>(_sourceGetTask);
    }
}
