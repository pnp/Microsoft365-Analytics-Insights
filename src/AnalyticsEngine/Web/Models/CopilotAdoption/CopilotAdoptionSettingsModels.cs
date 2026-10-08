using Common.Entities.CopilotAdoption;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.CopilotAdoption
{
    /// <summary>What Administration &gt; Copilot Adoption settings shows.</summary>
    public sealed class CopilotAdoptionSettingsModel
    {
        /// <summary>False when no Storage connection string is configured: the defaults apply and cannot be changed.</summary>
        [JsonProperty("durable")]
        public bool Durable { get; set; }

        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("settings")]
        public CopilotAdoptionScoreSettings Settings { get; set; }

        [JsonProperty("defaults")]
        public CopilotAdoptionScoreSettings Defaults { get; set; }

        [JsonProperty("customisedFields")]
        public IReadOnlyList<string> CustomisedFields { get; set; }

        [JsonProperty("updatedBy")]
        public string UpdatedBy { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>Newest first. Names who made each change, which is why the page is Administration-only.</summary>
        [JsonProperty("history")]
        public List<CopilotAdoptionScoreSettingsChange> History { get; set; }

        /// <summary>How long another web instance may keep using the previous settings after a save.</summary>
        [JsonProperty("propagationSeconds")]
        public int PropagationSeconds { get; set; }

        [JsonProperty("minThreshold")]
        public int MinThreshold { get; set; } = CopilotAdoptionScoreSettings.MinThreshold;

        [JsonProperty("maxThreshold")]
        public int MaxThreshold { get; set; } = CopilotAdoptionScoreSettings.MaxThreshold;
    }

    public sealed class CopilotAdoptionSettingsSaveRequest
    {
        /// <summary>The version the administrator was editing; a save made against an older one is refused.</summary>
        [JsonProperty("expectedVersion")]
        public long? ExpectedVersion { get; set; }

        [JsonProperty("settings")]
        public CopilotAdoptionScoreSettings Settings { get; set; }
    }

    public sealed class CopilotAdoptionSettingsResetRequest
    {
        [JsonProperty("expectedVersion")]
        public long? ExpectedVersion { get; set; }
    }

    public sealed class CopilotAdoptionSettingsError
    {
        [JsonProperty("code")]
        public string Code { get; set; }

        /// <summary>Every rule the submitted values broke, as stable codes. Empty unless <see cref="Code"/> is a validation code.</summary>
        [JsonProperty("validationErrors")]
        public IReadOnlyList<string> ValidationErrors { get; set; } = new List<string>();
    }

    /// <summary>The settings page's operations, over the deployment's store and the report's settings provider.</summary>
    internal sealed class CopilotAdoptionSettingsService
    {
        private readonly CopilotAdoptionScoreSettingsProvider _provider;
        private readonly TimeSpan _propagation;

        public CopilotAdoptionSettingsService(CopilotAdoptionScoreSettingsProvider provider, TimeSpan? propagation = null)
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _propagation = propagation ?? CopilotAdoptionScoreSettingsProvider.DefaultRefreshInterval;
        }

        public static CopilotAdoptionSettingsService ForThisDeployment() =>
            new CopilotAdoptionSettingsService(CopilotAdoptionScoreSettingsProvider.Production);

        public async Task<CopilotAdoptionSettingsModel> GetAsync()
        {
            var store = _provider.Store;
            return ToModel(await store.GetAsync().ConfigureAwait(false), store.IsDurable);
        }

        public async Task<CopilotAdoptionSettingsModel> SaveAsync(CopilotAdoptionSettingsSaveRequest request, string changedBy)
        {
            if (request?.Settings == null || request.ExpectedVersion == null)
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest);
            var store = _provider.Store;
            var saved = await store.SaveAsync(request.Settings, request.ExpectedVersion.Value, changedBy).ConfigureAwait(false);
            _provider.Publish(saved);
            return ToModel(saved, store.IsDurable);
        }

        public async Task<CopilotAdoptionSettingsModel> ResetAsync(CopilotAdoptionSettingsResetRequest request, string changedBy)
        {
            if (request?.ExpectedVersion == null)
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest);
            var store = _provider.Store;
            var saved = await store.ResetAsync(request.ExpectedVersion.Value, changedBy).ConfigureAwait(false);
            _provider.Publish(saved);
            return ToModel(saved, store.IsDurable);
        }

        private CopilotAdoptionSettingsModel ToModel(CopilotAdoptionScoreSettingsDocument document, bool durable) =>
            new CopilotAdoptionSettingsModel
            {
                Durable = durable,
                Version = document.Version,
                Settings = document.Settings,
                Defaults = CopilotAdoptionScoreSettings.Defaults,
                CustomisedFields = document.Settings.CustomisedFields,
                UpdatedBy = document.UpdatedBy,
                UpdatedUtc = document.UpdatedUtc,
                History = (document.History ?? new List<CopilotAdoptionScoreSettingsChange>()).ToList(),
                PropagationSeconds = (int)Math.Ceiling(_propagation.TotalSeconds),
            };
    }
}
