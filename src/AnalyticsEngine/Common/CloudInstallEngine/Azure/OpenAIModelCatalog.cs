using Azure.Core;
using Azure.ResourceManager.CognitiveServices;
using Azure.ResourceManager.CognitiveServices.Models;
using Azure.ResourceManager.Resources;
using CloudInstallEngine.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudInstallEngine.Azure
{
    public sealed class OpenAIModelChoice
    {
        public string Name { get; internal set; }
        public string Version { get; internal set; }
        public string DeploymentType { get; internal set; }
        public string Lifecycle { get; internal set; }
        public bool IsDefaultVersion { get; internal set; }
        public bool IsClassifierCompatible { get; internal set; }
        public bool IsSmallModel => Name.EndsWith("-mini", StringComparison.Ordinal) || Name.EndsWith("-nano", StringComparison.Ordinal);
        public CognitiveServicesCapacityConfig Capacity { get; internal set; }

        public bool SupportsCapacity(int value)
        {
            return value >= 1 && value <= 10000 &&
                (Capacity == null ||
                    ((!Capacity.Minimum.HasValue || value >= Capacity.Minimum.Value) &&
                     (!Capacity.Maximum.HasValue || value <= Capacity.Maximum.Value) &&
                     (Capacity.AllowedValues.Count == 0 || Capacity.AllowedValues.Contains(value)) &&
                     (!Capacity.Step.HasValue || Capacity.Step.Value <= 0 ||
                         (value - (Capacity.Minimum ?? 0)) % Capacity.Step.Value == 0)));
        }

        public int SuggestedCapacity
        {
            get
            {
                var candidates = new[] { 1, Capacity?.Default ?? 1, Capacity?.Minimum ?? 1 }
                    .Concat(Capacity?.AllowedValues ?? Enumerable.Empty<int>());
                var value = candidates.FirstOrDefault(SupportsCapacity);
                if (value == 0)
                    throw new InstallException("Azure did not report a usable deployment capacity. Check the model in the Azure portal.");
                return value;
            }
        }
    }

    public static class OpenAIModelCatalog
    {
        public static readonly IReadOnlyList<string> DeploymentTypes =
            Array.AsReadOnly(new[] { "Standard", "DataZoneStandard", "GlobalStandard" });

        public static string DeploymentTypeLabel(string deploymentType)
        {
            switch (deploymentType)
            {
                case "Standard": return "Regional Standard";
                case "DataZoneStandard": return "Data Zone Standard";
                case "GlobalStandard": return "Global Standard";
                default: return deploymentType;
            }
        }

        public static string ResidencyDescription(string deploymentType)
        {
            switch (deploymentType)
            {
                case "Standard": return "Regional Standard: processing stays in the resource's Azure geography; it may span regions within that geography.";
                case "DataZoneStandard": return "Data Zone Standard: processing may occur anywhere in the resource's Microsoft-defined data zone, not just its region.";
                case "GlobalStandard": return "Global Standard: prompts and responses may be processed in any Azure region worldwide.";
                default: return "Unknown deployment type. Choose Regional, Data Zone or Global Standard.";
            }
        }

        public static async Task<IReadOnlyList<OpenAIModelChoice>> LoadAsync(
            SubscriptionResource subscription, AzureLocation location, CancellationToken cancellationToken)
        {
            var models = new List<CognitiveServicesModel>();
            // The SDK's AsyncPageable follows nextLink and propagates authentication/network failures.
            var enumerator = subscription.GetModelsAsync(location, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    models.Add(enumerator.Current);
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            return CreateChoices(models);
        }

        internal static IReadOnlyList<OpenAIModelChoice> CreateChoices(IEnumerable<CognitiveServicesModel> models)
        {
            var choices = new List<OpenAIModelChoice>();
            foreach (var entry in models)
            {
                var model = entry.Model;
                if (model == null || !string.Equals(entry.Kind, "OpenAI", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(model.Format, "OpenAI", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(model.Name) || string.IsNullOrWhiteSpace(model.Version))
                    continue;
                var lifecycle = model.LifecycleStatus?.ToString() ?? "Unspecified";
                if (string.Equals(lifecycle, "Deprecated", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var sku in model.Skus)
                {
                    if (!DeploymentTypes.Contains(sku.Name) || (sku.DeprecationOn.HasValue && sku.DeprecationOn.Value <= DateTimeOffset.UtcNow))
                        continue;
                    choices.Add(new OpenAIModelChoice
                    {
                        Name = model.Name, Version = model.Version, DeploymentType = sku.Name,
                        Lifecycle = lifecycle, IsDefaultVersion = model.IsDefaultVersion == true,
                        Capacity = sku.Capacity,
                        IsClassifierCompatible = IsCompatible(model.Name, model.Version) &&
                            model.Capabilities.TryGetValue("chatCompletion", out var chat) &&
                            string.Equals(chat, "true", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            return choices.OrderByDescending(x => x.IsSmallModel)
                .ThenBy(x => x.Lifecycle == "Preview" || x.Lifecycle == "Deprecating")
                .ThenBy(x => x.Name, StringComparer.Ordinal)
                .ThenByDescending(x => x.IsDefaultVersion)
                .ThenByDescending(x => x.Version, StringComparer.Ordinal)
                .ThenBy(x => DeploymentTypes.ToList().IndexOf(x.DeploymentType))
                .ToList();
        }

        // These non-reasoning versions support the classifier's strict JSON schema, temperature and max_tokens.
        // Unknown/reasoning models remain available for manual configuration, not recommended by the picker.
        private static bool IsCompatible(string name, string version)
        {
            return (name == "gpt-4o-mini" && version == "2024-07-18") ||
                (name == "gpt-4o" && (version == "2024-08-06" || version == "2024-11-20")) ||
                ((name == "gpt-4.1" || name == "gpt-4.1-mini" || name == "gpt-4.1-nano") && version == "2025-04-14");
        }

        public static OpenAIModelChoice ValidateSelection(IEnumerable<OpenAIModelChoice> choices,
            string name, string version, string deploymentType, int capacity)
        {
            var selected = choices.FirstOrDefault(x => x.Name == name && x.DeploymentType == deploymentType &&
                (string.IsNullOrWhiteSpace(version) ? x.IsDefaultVersion : x.Version == version));
            if (selected == null)
                throw new InstallException("Azure does not list this model/version/deployment type in the account's region. " +
                    "Use Browse available models on Azure AI; do not change processing geography without approval.");
            if (!selected.SupportsCapacity(capacity))
                throw new InstallException("The configured Azure OpenAI capacity is not supported for this model deployment. " +
                    "Use the capacity shown by Browse available models.");
            return selected;
        }
    }
}
