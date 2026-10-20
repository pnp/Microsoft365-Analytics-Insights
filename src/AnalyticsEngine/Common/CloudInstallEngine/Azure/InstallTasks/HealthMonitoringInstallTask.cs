using Azure.Core;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure.InstallTasks
{
    public class HealthMonitoringInstallTask : GenericARMInstallTask
    {
        public const string AppInsightsNameSetting = "AppInsightsName";
        internal const string OwnerTag = "m365ai-health-owner";
        private const string RuleType = "Microsoft.Insights/scheduledQueryRules";
        private const string GroupType = "Microsoft.Insights/actionGroups";
        private readonly bool _enabled;
        private readonly string _email;
        private readonly int _missingMinutes;
        private readonly HashSet<string> _expectedJobs;

        public HealthMonitoringInstallTask(TaskConfig config, ILogger logger, AzureLocation location,
            Dictionary<string, string> tags, bool enabled, string email, int missingMinutes,
            bool activityExpected, bool webTrafficExpected) : base(config, logger, location, tags)
        {
            if (enabled && (missingMinutes < 15 || missingMinutes > 1440))
                throw new ArgumentOutOfRangeException(nameof(missingMinutes));
            if (enabled && string.IsNullOrWhiteSpace(email))
                throw new ArgumentException("Enabled health alerts require a notification email.", nameof(email));
            _enabled = enabled;
            _email = email?.Trim() ?? string.Empty;
            _missingMinutes = enabled ? missingMinutes : 15;
            _expectedJobs = new HashSet<string>(StringComparer.Ordinal);
            if (activityExpected) _expectedJobs.Add("Office365ActivityImporter");
            if (webTrafficExpected) _expectedJobs.Add("AppInsightsImporter");
        }

        public override string TaskName => "Configure importer health alerts";

        public override async Task<ArmDeploymentResource> ExecuteTaskReturnResult(object contextArg)
        {
            var owner = OwnershipKey(ContainerId, _config.ResourceName);
            var existing = ReadExistingResources();
            var managed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in existing)
            {
                if (resource.Tags.TryGetValue(OwnerTag, out var actualOwner) && actualOwner == owner)
                    managed.Add(resource.Name);
                else if (_enabled)
                    throw new InstallException("A health-alert resource name is already used by an unowned resource. No alert resources were changed.");
                else
                    _logger.LogWarning("Leaving an unowned health-alert resource unchanged.");
            }

            if (!_enabled && managed.Count == 0)
            {
                _logger.LogInformation("Health alert provisioning is disabled; no owned alerts need updating.");
                return null;
            }

            var template = BuildTemplate(ContainerId, ScopeLocation, managed);
            if (((JArray)template["resources"]).Count == 0) return null;
            var result = await DeployTemplate(template, BuildParameters(owner));
            _logger.LogInformation(_enabled
                ? "Configured importer liveness alerts. Azure Monitor log alerts can incur charges; verify notification delivery with an action-group test."
                : "Disabled the installer's importer alerts. Existing notification action group was retained.");
            return result;
        }

        protected virtual string ContainerId => Container.Id.ToString();

        protected virtual string ScopeLocation
        {
            get
            {
                var scope = Container.GetGenericResources("resourceType eq 'Microsoft.Insights/components'")
                    .Single(r => string.Equals(r.Data.Name, _config.GetConfigValue(AppInsightsNameSetting), StringComparison.OrdinalIgnoreCase));
                return scope.Data.Location.Name;
            }
        }

        protected virtual IReadOnlyList<HealthResource> ReadExistingResources()
        {
            var names = new HashSet<string>(ResourceNames, StringComparer.OrdinalIgnoreCase);
            return new[] { RuleType, GroupType }
                .SelectMany(type => Container.GetGenericResources("resourceType eq '" + type + "'")
                    .Where(r => type == GroupType
                        ? string.Equals(r.Data.Name, ResourceNames[0], StringComparison.OrdinalIgnoreCase)
                        : names.Contains(r.Data.Name) && !string.Equals(r.Data.Name, ResourceNames[0], StringComparison.OrdinalIgnoreCase)))
                .Select(r => new HealthResource(r.Data.Name, new Dictionary<string, string>(r.Data.Tags, StringComparer.OrdinalIgnoreCase)))
                .ToList();
        }

        protected virtual Task<ArmDeploymentResource> DeployTemplate(JObject template, object parameters)
            => ApplyTemplate(parameters, template.ToString());

        private object BuildParameters(string owner)
        {
            // ARM parameter values keep operator tags as data, including bracket-wrapped strings.
            var tags = new Dictionary<string, string>();
            foreach (var tag in Tags)
                if (!string.Equals(tag.Key, OwnerTag, StringComparison.OrdinalIgnoreCase))
                    tags[tag.Key] = tag.Value;
            tags[OwnerTag] = owner;
            return new
            {
                resourceTags = new { value = tags },
                notificationEmail = new { value = _email }
            };
        }

        internal string[] ResourceNames => new[]
        {
            _config.ResourceName + "-health-notifications",
            _config.ResourceName + "-health-activity",
            _config.ResourceName + "-health-webtraffic"
        };

        internal JObject BuildTemplate(string resourceGroupId, string location, ISet<string> existingOwned)
        {
            var names = ResourceNames;
            var groupId = resourceGroupId + "/providers/" + GroupType + "/" + names[0];
            var insightsId = resourceGroupId + "/providers/Microsoft.Insights/components/" + _config.GetConfigValue(AppInsightsNameSetting);
            var resources = new JArray();

            if (_enabled)
            {
                resources.Add(new JObject
                {
                    ["type"] = GroupType, ["apiVersion"] = "2023-01-01", ["name"] = names[0],
                    ["location"] = "global", ["tags"] = "[parameters('resourceTags')]",
                    ["properties"] = new JObject
                    {
                        ["groupShortName"] = "MAI-health", ["enabled"] = true,
                        ["emailReceivers"] = new JArray(new JObject
                        {
                            ["name"] = "Operator", ["emailAddress"] = "[parameters('notificationEmail')]",
                            ["useCommonAlertSchema"] = true
                        })
                    }
                });
            }

            var jobs = new[] { "Office365ActivityImporter", "AppInsightsImporter" };
            for (var i = 0; i < jobs.Length; i++)
            {
                var active = _enabled && _expectedJobs.Contains(jobs[i]);
                if (!active && !existingOwned.Contains(names[i + 1])) continue;
                var rule = new JObject
                {
                    ["type"] = RuleType, ["apiVersion"] = "2021-08-01", ["name"] = names[i + 1],
                    ["kind"] = "LogAlert",
                    ["location"] = location, ["tags"] = "[parameters('resourceTags')]",
                    ["properties"] = new JObject
                    {
                        ["displayName"] = jobs[i] + " heartbeat missing",
                        ["description"] = "Process liveness only, not successful import or data freshness. Expected shutdowns need an Azure alert processing rule or explicit opt-out.",
                        ["enabled"] = active, ["severity"] = 2,
                        ["evaluationFrequency"] = "PT5M", ["windowSize"] = "PT5M",
                        ["overrideQueryTimeRange"] = "PT" + _missingMinutes + "M",
                        ["scopes"] = new JArray(insightsId), ["autoMitigate"] = true,
                        ["criteria"] = new JObject
                        {
                            ["allOf"] = new JArray(new JObject
                            {
                                ["query"] = MissingHeartbeatQuery(jobs[i], _missingMinutes),
                                ["timeAggregation"] = "Count", ["operator"] = "GreaterThan",
                                ["threshold"] = 0,
                                ["failingPeriods"] = new JObject { ["numberOfEvaluationPeriods"] = 1, ["minFailingPeriodsToAlert"] = 1 }
                            })
                        },
                        ["actions"] = new JObject { ["actionGroups"] = active ? new JArray(groupId) : new JArray() }
                    }
                };
                if (_enabled) rule["dependsOn"] = new JArray(groupId);
                resources.Add(rule);
            }
            return new JObject
            {
                ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                ["contentVersion"] = "1.0.0.0",
                ["parameters"] = new JObject
                {
                    ["resourceTags"] = new JObject { ["type"] = "object" },
                    ["notificationEmail"] = new JObject { ["type"] = "string" }
                },
                ["resources"] = resources
            };
        }

        internal static string MissingHeartbeatQuery(string job, int minutes)
        {
            if (job != "Office365ActivityImporter" && job != "AppInsightsImporter")
                throw new ArgumentException("Unrecognised importer job.", nameof(job));
            if (minutes < 15 || minutes > 1440) throw new ArgumentOutOfRangeException(nameof(minutes));
            // summarize without grouping returns zero even when this job has never emitted a heartbeat.
            return "customEvents | where timestamp > ago(" + minutes + "m) " +
                "| where name == 'ImporterHeartbeat' and tostring(customDimensions.JobName) == '" + job + "' " +
                "| summarize Heartbeats = count() | where Heartbeats == 0";
        }

        internal static string OwnershipKey(string resourceGroupId, string appName)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(
                    (resourceGroupId + "/providers/Microsoft.Web/sites/" + appName).ToLowerInvariant())))
                    .Replace("-", "").ToLowerInvariant();
        }

        public sealed class HealthResource
        {
            public HealthResource(string name, IReadOnlyDictionary<string, string> tags)
            {
                Name = name;
                Tags = tags;
            }
            public string Name { get; }
            public IReadOnlyDictionary<string, string> Tags { get; }
        }
    }
}
