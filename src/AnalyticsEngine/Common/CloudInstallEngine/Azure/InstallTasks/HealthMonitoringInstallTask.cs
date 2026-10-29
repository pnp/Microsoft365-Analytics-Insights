using Azure.Core;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.AppService;
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
        public const string SqlServerNameSetting = "SqlServerName";
        public const string SqlDatabaseNameSetting = "SqlDatabaseName";
        // Accepted for older callers, but never trusted as the web app's hosting identity.
        public const string AppServicePlanNameSetting = "AppServicePlanName";
        internal const string OwnerTag = "m365ai-health-owner";
        private const string RuleType = "Microsoft.Insights/scheduledQueryRules";
        private const string GroupType = "Microsoft.Insights/actionGroups";
        private const string MetricType = "Microsoft.Insights/metricAlerts";
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

        public override string TaskName => "Configure default health alerts";
        public override bool IsCritical => false;

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

            var needsPlanScope = _enabled || managed.Contains(ResourceNames[6]) || managed.Contains(ResourceNames[7]);
            var hostingPlan = needsPlanScope ? await ReadHostingPlanIdAsync() : null;
            var template = BuildTemplate(ContainerId, ScopeLocation, managed, hostingPlan);
            if (((JArray)template["resources"]).Count == 0) return null;
            var result = await DeployTemplate(template, BuildParameters(owner));
            _logger.LogInformation(_enabled
                ? "Configured default health alerts. Log/metric alerts and telemetry can incur charges. Import-cycle completion is not proof of non-zero data. Secret expiry is unknown; track it in Entra. Verify notification delivery after installation."
                : "Disabled the installer's owned health alerts. Existing notification action group was retained.");
            return result;
        }

        protected virtual string ContainerId => Container.Id.ToString();

        protected virtual async Task<ResourceIdentifier> ReadHostingPlanIdAsync()
        {
            var site = (await Container.GetWebSiteAsync(_config.ResourceName, cancellationToken: CancellationToken)).Value;
            var plan = site.Data.AppServicePlanId;
            if (plan == null || !string.Equals(plan.ResourceType.ToString(), "Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase))
                throw new InstallException("The App Service did not report a valid hosting plan; no health-alert resources were changed.");
            return plan;
        }

        protected virtual string ScopeLocation
        {
            get
            {
                var scope = Container.GetGenericResources("resourceType eq 'Microsoft.Insights/components'", cancellationToken: CancellationToken)
                    .Single(r => string.Equals(r.Data.Name, _config.GetConfigValue(AppInsightsNameSetting), StringComparison.OrdinalIgnoreCase));
                return scope.Data.Location.Name;
            }
        }

        protected virtual IReadOnlyList<HealthResource> ReadExistingResources()
        {
            var names = new HashSet<string>(ResourceNames, StringComparer.OrdinalIgnoreCase);
            return new[] { RuleType, GroupType, MetricType }
                .SelectMany(type => Container.GetGenericResources("resourceType eq '" + type + "'", cancellationToken: CancellationToken)
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
            _config.ResourceName + "-health-webtraffic",
            _config.ResourceName + "-health-failures",
            _config.ResourceName + "-health-sql-cpu",
            _config.ResourceName + "-health-sql-storage",
            _config.ResourceName + "-health-plan-cpu",
            _config.ResourceName + "-health-plan-memory"
        };

        internal JObject BuildTemplate(string resourceGroupId, string location, ISet<string> existingOwned, ResourceIdentifier hostingPlan)
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
                        ["displayName"] = jobs[i] + " health needs attention",
                        ["description"] = "Missing heartbeat/check telemetry, no cycle completion in 24h, SQL/schema failure, invalid credential, certificate expiry <14d, delegated cost API refusal or persistent collection failure. Cost recovery requires a successful API read, not token refresh. Expected shutdowns require an alert processing rule or opt-out.",
                        ["enabled"] = active, ["severity"] = 2,
                        ["evaluationFrequency"] = "PT5M", ["windowSize"] = "PT5M",
                        ["overrideQueryTimeRange"] = "P2D",
                        ["scopes"] = new JArray(insightsId), ["autoMitigate"] = true,
                        ["criteria"] = new JObject
                        {
                            ["allOf"] = new JArray(new JObject
                            {
                                ["query"] = ImporterHealthQuery(jobs[i], _missingMinutes),
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
            var importsExpected = _expectedJobs.Count > 0;
            if ((_enabled && importsExpected) || existingOwned.Contains(names[3]))
                resources.Add(LogRule(names[3], location, insightsId, groupId, _enabled && importsExpected));

            var sqlId = resourceGroupId + "/providers/Microsoft.Sql/servers/" + _config.GetConfigValue(SqlServerNameSetting)
                + "/databases/" + _config.GetConfigValue(SqlDatabaseNameSetting);
            var planId = hostingPlan?.ToString();
            var metrics = new[]
            {
                new[] { sqlId, "Microsoft.Sql/servers/databases", "cpu_percent", "PT1H" },
                new[] { sqlId, "Microsoft.Sql/servers/databases", "storage_percent", "PT15M" },
                new[] { planId, "Microsoft.Web/serverfarms", "CpuPercentage", "PT1H" },
                new[] { planId, "Microsoft.Web/serverfarms", "MemoryPercentage", "PT1H" }
            };
            for (var i = 0; i < metrics.Length; i++)
            {
                if (!_enabled && !existingOwned.Contains(names[i + 4])) continue;
                var metric = metrics[i];
                resources.Add(new JObject
                {
                    ["type"] = MetricType, ["apiVersion"] = "2018-03-01", ["name"] = names[i + 4],
                    ["location"] = "global", ["tags"] = "[parameters('resourceTags')]",
                    ["properties"] = new JObject
                    {
                        ["description"] = "Sustained capacity above 90 percent. Applies to the shared plan/database, not only this solution. No missing-metric success inference.",
                        ["enabled"] = _enabled, ["severity"] = 2, ["autoMitigate"] = true,
                        ["evaluationFrequency"] = "PT5M", ["windowSize"] = metric[3],
                        ["scopes"] = new JArray(metric[0]),
                        ["criteria"] = new JObject
                        {
                            ["odata.type"] = "Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria",
                            ["allOf"] = new JArray(new JObject
                            {
                                ["name"] = "Capacity", ["metricNamespace"] = metric[1], ["metricName"] = metric[2],
                                ["criterionType"] = "StaticThresholdCriterion", ["operator"] = "GreaterThan",
                                ["threshold"] = 90, ["timeAggregation"] = "Average",
                                ["skipMetricValidation"] = false
                            })
                        },
                        ["actions"] = _enabled
                            ? new JArray(new JObject { ["actionGroupId"] = groupId })
                            : new JArray()
                    }
                });
                if (_enabled) resources.Last["dependsOn"] = new JArray(groupId);
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

        private JObject LogRule(string name, string location, string insightsId, string groupId, bool active)
        {
            var rule = new JObject
            {
                ["type"] = RuleType, ["apiVersion"] = "2021-08-01", ["name"] = name,
                ["kind"] = "LogAlert", ["location"] = location, ["tags"] = "[parameters('resourceTags')]",
                ["properties"] = new JObject
                {
                    ["displayName"] = "Importer SQL or permission failures",
                    ["description"] = "Passive logged failures only. No events does not prove permission or data-flow health.",
                    ["enabled"] = active, ["severity"] = 2, ["evaluationFrequency"] = "PT5M",
                    ["windowSize"] = "PT1H", ["scopes"] = new JArray(insightsId), ["autoMitigate"] = true,
                    ["criteria"] = new JObject
                    {
                        ["allOf"] = new JArray(new JObject
                        {
                            ["query"] = LoggedFailuresQuery, ["timeAggregation"] = "Count",
                            ["operator"] = "GreaterThan", ["threshold"] = 0,
                            ["failingPeriods"] = new JObject { ["numberOfEvaluationPeriods"] = 1, ["minFailingPeriodsToAlert"] = 1 }
                        })
                    },
                    ["actions"] = new JObject { ["actionGroups"] = active ? new JArray(groupId) : new JArray() }
                }
            };
            if (_enabled) rule["dependsOn"] = new JArray(groupId);
            return rule;
        }

        internal const string LoggedFailuresQuery =
            "let Sql = exceptions | where timestamp > ago(1h) " +
            "| where outerType in ('System.Data.SqlClient.SqlException', 'Microsoft.Data.SqlClient.SqlException') " +
            "or innermostType in ('System.Data.SqlClient.SqlException', 'Microsoft.Data.SqlClient.SqlException'); " +
            "union (Sql | summarize Failures=count() | where Failures > 10 | project Signal='PersistentSqlErrors'), " +
            "(Sql | where timestamp > ago(10m) | where outerMessage has 'read-only' or innermostMessage has 'read-only' " +
            "or outerMessage contains 'database is full' or innermostMessage contains 'database is full' " +
            "or outerMessage contains 'insufficient disk space' or innermostMessage contains 'insufficient disk space' " +
            "or outerMessage contains 'has reached its size quota' or innermostMessage contains 'has reached its size quota' " +
            "or outerMessage contains 'Could not allocate space' or innermostMessage contains 'Could not allocate space' " +
            "or (outerMessage has 'transaction' and outerMessage has 'log' and outerMessage has 'full') " +
            "or (innermostMessage has 'transaction' and innermostMessage has 'log' and innermostMessage has 'full') " +
            "| summarize Failures=count() " +
            "| where Failures > 2 | project Signal='SqlCapacityOrReadOnly'), " +
            "(traces | where timestamp > ago(1h) | where operation_Name == 'Office365ActivityImporter' " +
            "| where message startswith \"ERROR: Can't access Teams user data\" " +
            "| project Signal='TeamsPermissionFailure')";

        internal static string ImporterHealthQuery(string job, int minutes)
        {
            // Reuse the allow-list and bounds checks rather than interpolating arbitrary identifiers.
            MissingHeartbeatQuery(job, minutes);
            // Must equal the HealthComponent names DefaultRuntimeHealth.BuildChecks registers for the job (this assembly
            // cannot reference the enum). A rename or a removed check would otherwise leave a permanent Sev-2, and a
            // new one would never be watched; HealthMonitoringTests pins the two lists together.
            var expectedChecks = job == "Office365ActivityImporter"
                ? "'Sql', 'Credential', 'CopilotCostApi', 'CopilotStudioConsumption'"
                : "'Sql', 'Credential'";
            return "let Heartbeats = customEvents | where timestamp > ago(2d) " +
                "| where name == 'ImporterHeartbeat' and tostring(customDimensions.JobName) == '" + job + "'; " +
                "let Checks = customEvents | where timestamp > ago(30m) | where name == 'HealthCheck' " +
                "and tostring(customDimensions.Monitor) == 'DefaultHealth' " +
                "and tostring(customDimensions.JobName) == '" + job + "' " +
                "| summarize arg_max(timestamp, *) by Component=tostring(customDimensions.Component); " +
                "union (Heartbeats | where timestamp > ago(" + minutes + "m) | summarize Heartbeats=count() " +
                "| where Heartbeats == 0 | project Signal='MissingHeartbeat'), " +
                "(Heartbeats | summarize LastCycle=max(todatetime(customDimensions.LastCycleUtc)), " +
                "FirstStart=min(todatetime(customDimensions.ProcessStartedUtc)) " +
                "| where LastCycle < ago(24h) or (isnull(LastCycle) and FirstStart < ago(24h)) " +
                "| project Signal='CycleOverdue'), " +
                "(Checks | where tostring(customDimensions.Status) == 'Unhealthy' " +
                "or (Component == 'Credential' and toint(customDimensions.DaysToExpiry) < 14) " +
                "| project Signal=strcat(Component, 'UnhealthyOrExpiring')), " +
                "(datatable(Component:string) [" + expectedChecks + "] | join kind=leftanti (Checks) on Component " +
                "| project Signal=strcat(Component, 'HealthCheckMissing'))";
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
