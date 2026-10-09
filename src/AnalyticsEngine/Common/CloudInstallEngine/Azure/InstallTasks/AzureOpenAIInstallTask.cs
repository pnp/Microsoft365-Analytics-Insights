using Azure;
using Azure.Core;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public sealed class FoundryPromptInfo
    {
        public string Endpoint { get; set; }
        public string Deployment { get; set; }
    }

    public static class FoundryPromptAppSettings
    {
        public static Dictionary<string, string> Build(bool enabled, FoundryPromptInfo service)
        {
            if (enabled &&
                (service == null || !IsValidEndpoint(service.Endpoint) ||
                 !Regex.IsMatch(service.Deployment ?? string.Empty, @"\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,63}\z")))
            {
                throw new InstallException("Azure AI Foundry is enabled, but provisioning did not return a valid Azure OpenAI endpoint and deployment. Re-run the installer after resolving the provisioning failure.");
            }

            return new Dictionary<string, string>
            {
                ["FoundryPromptEndpoint"] = enabled ? service?.Endpoint ?? string.Empty : string.Empty,
                ["FoundryPromptDeployment"] = enabled ? service?.Deployment ?? string.Empty : string.Empty,
                ["FoundryPromptKey"] = string.Empty
            };
        }

        private static bool IsValidEndpoint(string endpoint)
        {
            return !string.IsNullOrWhiteSpace(endpoint) && endpoint == endpoint.Trim() &&
                Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
                string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" &&
                Regex.IsMatch(uri.Host, @"\A[a-z0-9][a-z0-9-]{0,61}[a-z0-9]\.(openai|cognitiveservices)\.azure\.com\z",
                    RegexOptions.IgnoreCase);
        }
    }

    /// <summary>Creates or updates the Azure OpenAI account and its configured model deployment.</summary>
    public sealed class AzureOpenAIInstallTask : InstallTaskInAzResourceGroup<FoundryPromptInfo>
    {
        public const string CONFIG_KEY_DEPLOYMENT_NAME = "deploymentName";
        public const string CONFIG_KEY_MODEL_NAME = "modelName";
        public const string CONFIG_KEY_MODEL_VERSION = "modelVersion";
        public const string CONFIG_KEY_CAPACITY = "capacity";

        private readonly bool _allowPublicAccess;

        public AzureOpenAIInstallTask(TaskConfig config, ILogger logger, AzureLocation azureLocation,
            Dictionary<string, string> tags, bool allowPublicAccess = true)
            : base(config, logger, azureLocation, tags)
        {
            _allowPublicAccess = allowPublicAccess;
        }

        public override string TaskName => "get/create Azure AI Foundry OpenAI service and deployment";

        public override async Task<FoundryPromptInfo> ExecuteTaskReturnResult(object contextArg)
        {
            var name = _config.GetNameConfigValue();
            var desiredAccess = _allowPublicAccess ? ServiceAccountPublicNetworkAccess.Enabled : ServiceAccountPublicNetworkAccess.Disabled;
            var account = Container.GetCognitiveServicesAccounts()
                .AsEnumerable()
                .SingleOrDefault(item => string.Equals(item.Data.Name, name, StringComparison.OrdinalIgnoreCase));

            if (account == null)
            {
                var data = BuildAccountData(AzureLocation, name, _allowPublicAccess);
                EnsureTagsOnNew(data.Tags);
                account = (await Container.GetCognitiveServicesAccounts()
                    .CreateOrUpdateAsync(WaitUntil.Completed, name, data)).Value;
                _logger.LogInformation($"Created Azure AI Foundry OpenAI resource '{name}'.");
            }
            else
            {
                if (!string.Equals(account.Data.Kind, "OpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallException($"Azure resource '{name}' exists but is not an OpenAI account. Choose a different Azure AI Foundry resource name.");
                }

                var currentProperties = account.Data.Properties;
                var needsUpdate = currentProperties == null ||
                    string.IsNullOrWhiteSpace(currentProperties.CustomSubDomainName) ||
                    currentProperties.PublicNetworkAccess == null ||
                    currentProperties.PublicNetworkAccess.Value != desiredAccess;

                if (needsUpdate)
                {
                    var updateProperties = currentProperties ?? new CognitiveServicesAccountProperties();
                    if (string.IsNullOrWhiteSpace(updateProperties.CustomSubDomainName))
                    {
                        updateProperties.CustomSubDomainName = name;
                    }
                    updateProperties.PublicNetworkAccess = desiredAccess;
                    var update = new CognitiveServicesAccountData(AzureLocation)
                    {
                        Sku = account.Data.Sku,
                        Kind = account.Data.Kind,
                        Properties = updateProperties
                    };
                    if (account.Data.Tags != null)
                    {
                        foreach (var tag in account.Data.Tags)
                        {
                            update.Tags[tag.Key] = tag.Value;
                        }
                    }
                    account = (await Container.GetCognitiveServicesAccounts()
                        .CreateOrUpdateAsync(WaitUntil.Completed, name, update)).Value;
                }

                await EnsureTagsOnExisting(account.Data.Tags, account.GetTagResource());
                _logger.LogInformation($"Found existing Azure AI Foundry OpenAI resource '{name}'.");
            }

            var deploymentName = _config.GetConfigValue(CONFIG_KEY_DEPLOYMENT_NAME);
            var modelName = _config.GetConfigValue(CONFIG_KEY_MODEL_NAME);
            var modelVersion = _config.GetConfigValue(CONFIG_KEY_MODEL_VERSION);
            var capacity = int.Parse(_config.GetConfigValue(CONFIG_KEY_CAPACITY));
            await account.GetCognitiveServicesAccountDeployments().CreateOrUpdateAsync(
                WaitUntil.Completed,
                deploymentName,
                BuildDeploymentData(modelName, modelVersion, capacity));

            var endpoint = account.Data.Properties?.Endpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                var subdomain = account.Data.Properties?.CustomSubDomainName;
                endpoint = $"https://{(string.IsNullOrWhiteSpace(subdomain) ? name : subdomain)}.openai.azure.com/";
            }

            _logger.LogInformation($"Ensured Azure OpenAI model deployment '{deploymentName}' on resource '{name}'.");
            return new FoundryPromptInfo { Endpoint = endpoint, Deployment = deploymentName };
        }

        internal static CognitiveServicesAccountDeploymentData BuildDeploymentData(string modelName, string modelVersion, int capacity)
        {
            return new CognitiveServicesAccountDeploymentData
            {
                Sku = new CognitiveServicesSku("Standard") { Capacity = capacity },
                Properties = new CognitiveServicesAccountDeploymentProperties
                {
                    Model = new CognitiveServicesAccountDeploymentModel
                    {
                        Format = "OpenAI",
                        Name = modelName,
                        Version = string.IsNullOrWhiteSpace(modelVersion) ? null : modelVersion
                    }
                }
            };
        }

        internal static CognitiveServicesAccountData BuildAccountData(AzureLocation location, string name, bool allowPublicAccess)
        {
            return new CognitiveServicesAccountData(location)
            {
                Sku = new CognitiveServicesSku("S0"),
                Kind = "OpenAI",
                Properties = new CognitiveServicesAccountProperties
                {
                    CustomSubDomainName = name,
                    PublicNetworkAccess = allowPublicAccess
                        ? ServiceAccountPublicNetworkAccess.Enabled
                        : ServiceAccountPublicNetworkAccess.Disabled
                }
            };
        }
    }
}
