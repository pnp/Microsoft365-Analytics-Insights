using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// The tenant-wide engagement-score settings an administrator may change (issues #683 and #684): the
    /// percentage weight of each score component, and the score at which each band starts.
    /// </summary>
    /// <remarks>
    /// <para>Deliberately only these six numbers. The active-day targets, Cowork thresholds, reclaim rules,
    /// privacy minimums and memory/history caps stay as <see cref="CopilotAdoptionOptions"/> defaults: they
    /// are either calibrated against Microsoft's own reports or protect the server, and none was asked for.</para>
    /// <para>Weights are whole percentages so an administrator types 50/30/20 rather than 0.5/0.3/0.2, and
    /// <see cref="ApplyTo"/> divides by 100 - which gives exactly the doubles the defaults have always used,
    /// so default settings change no score by even a rounding error.</para>
    /// </remarks>
    public sealed class CopilotAdoptionScoreSettings : IEquatable<CopilotAdoptionScoreSettings>
    {
        public const int DefaultFrequencyWeightPercent = 50;
        public const int DefaultDepthWeightPercent = 30;
        public const int DefaultBreadthWeightPercent = 20;
        public const int DefaultDevelopingScore = 25;
        public const int DefaultEstablishedScore = 50;
        public const int DefaultChampionScore = 75;

        /// <summary>The lowest Developing threshold allowed. Zero would leave Trialling unreachable.</summary>
        public const int MinThreshold = 1;
        public const int MaxThreshold = 100;

        [JsonProperty("frequencyWeightPercent")]
        public int FrequencyWeightPercent { get; set; } = DefaultFrequencyWeightPercent;

        [JsonProperty("depthWeightPercent")]
        public int DepthWeightPercent { get; set; } = DefaultDepthWeightPercent;

        [JsonProperty("breadthWeightPercent")]
        public int BreadthWeightPercent { get; set; } = DefaultBreadthWeightPercent;

        [JsonProperty("developingScore")]
        public int DevelopingScore { get; set; } = DefaultDevelopingScore;

        [JsonProperty("establishedScore")]
        public int EstablishedScore { get; set; } = DefaultEstablishedScore;

        [JsonProperty("championScore")]
        public int ChampionScore { get; set; } = DefaultChampionScore;

        public static CopilotAdoptionScoreSettings Defaults => new CopilotAdoptionScoreSettings();

        [JsonIgnore]
        public bool IsDefault => Equals(Defaults);

        /// <summary>Stable camelCase names of the settings that differ from the defaults, in display order.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> CustomisedFields =>
            Fields().Where(f => f.Value != f.Default).Select(f => f.Name).ToList();

        internal IEnumerable<(string Name, int Value, int Default)> Fields()
        {
            yield return (CopilotAdoptionScoreSettingsFields.FrequencyWeightPercent, FrequencyWeightPercent, DefaultFrequencyWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.DepthWeightPercent, DepthWeightPercent, DefaultDepthWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.BreadthWeightPercent, BreadthWeightPercent, DefaultBreadthWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.DevelopingScore, DevelopingScore, DefaultDevelopingScore);
            yield return (CopilotAdoptionScoreSettingsFields.EstablishedScore, EstablishedScore, DefaultEstablishedScore);
            yield return (CopilotAdoptionScoreSettingsFields.ChampionScore, ChampionScore, DefaultChampionScore);
        }

        /// <summary>Every rule the values break, as stable codes the portal translates. Empty when valid.</summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (FrequencyWeightPercent < 0 || DepthWeightPercent < 0 || BreadthWeightPercent < 0
                || FrequencyWeightPercent > 100 || DepthWeightPercent > 100 || BreadthWeightPercent > 100)
            {
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.WeightOutOfRange);
            }
            else if (FrequencyWeightPercent + DepthWeightPercent + BreadthWeightPercent != 100)
            {
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.WeightsMustTotal100);
            }

            if (new[] { DevelopingScore, EstablishedScore, ChampionScore }.Any(v => v < MinThreshold || v > MaxThreshold))
            {
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.ThresholdOutOfRange);
            }
            else if (!(DevelopingScore < EstablishedScore && EstablishedScore < ChampionScore))
            {
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.ThresholdsNotAscending);
            }

            return errors;
        }

        /// <summary>
        /// Writes these settings into <paramref name="options"/>. Default settings write the values
        /// <see cref="CopilotAdoptionOptions"/> already starts with, so nothing changes.
        /// </summary>
        public void ApplyTo(CopilotAdoptionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.FrequencyWeight = FrequencyWeightPercent / 100d;
            options.DepthWeight = DepthWeightPercent / 100d;
            options.BreadthWeight = BreadthWeightPercent / 100d;
            options.DevelopingScore = DevelopingScore;
            options.EstablishedScore = EstablishedScore;
            options.ChampionScore = ChampionScore;
        }

        public CopilotAdoptionScoreSettings Clone() => (CopilotAdoptionScoreSettings)MemberwiseClone();

        /// <summary>A compact, culture-invariant fingerprint of the values, used in the analysis cache key.</summary>
        public string Fingerprint() =>
            string.Join("-", Fields().Select(f => f.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public bool Equals(CopilotAdoptionScoreSettings other) =>
            other != null && Fields().Select(f => f.Value).SequenceEqual(other.Fields().Select(f => f.Value));

        public override bool Equals(object obj) => Equals(obj as CopilotAdoptionScoreSettings);

        public override int GetHashCode() => Fingerprint().GetHashCode();
    }

    public static class CopilotAdoptionScoreSettingsFields
    {
        public const string FrequencyWeightPercent = "frequencyWeightPercent";
        public const string DepthWeightPercent = "depthWeightPercent";
        public const string BreadthWeightPercent = "breadthWeightPercent";
        public const string DevelopingScore = "developingScore";
        public const string EstablishedScore = "establishedScore";
        public const string ChampionScore = "championScore";
    }

    /// <summary>Stable error codes. The portal maps each to a translated sentence; never reword them.</summary>
    public static class CopilotAdoptionScoreSettingsErrorCodes
    {
        public const string WeightOutOfRange = "weightOutOfRange";
        public const string WeightsMustTotal100 = "weightsMustTotal100";
        public const string ThresholdOutOfRange = "thresholdOutOfRange";
        public const string ThresholdsNotAscending = "thresholdsNotAscending";
        public const string InvalidRequest = "invalidRequest";
        public const string VersionConflict = "settingsChanged";
        public const string StorageNotConfigured = "storageNotConfigured";
        public const string StateUnavailable = "stateUnavailable";

        /// <summary>
        /// Sent by the adoption report API (503) when the settings cannot be read. The report refuses rather than
        /// silently scoring with the defaults, which would publish figures computed with rules nobody chose.
        /// </summary>
        public const string ReportSettingsUnavailable = "copilotAdoptionSettingsUnavailable";
    }

    /// <summary>
    /// The settings in force, with the version that identifies them. Version 0 means "never saved": the
    /// built-in defaults.
    /// </summary>
    public sealed class CopilotAdoptionEffectiveScoreSettings
    {
        public CopilotAdoptionEffectiveScoreSettings(long version, CopilotAdoptionScoreSettings values, DateTime? updatedUtc = null)
        {
            Version = Math.Max(0, version);
            Values = (values ?? CopilotAdoptionScoreSettings.Defaults).Clone();
            UpdatedUtc = updatedUtc;
        }

        public static CopilotAdoptionEffectiveScoreSettings Defaults { get; } =
            new CopilotAdoptionEffectiveScoreSettings(0, CopilotAdoptionScoreSettings.Defaults);

        public long Version { get; }
        public DateTime? UpdatedUtc { get; }
        private CopilotAdoptionScoreSettings Values { get; }

        public bool IsCustomised => !Values.IsDefault;

        public CopilotAdoptionScoreSettings GetValues() => Values.Clone();

        /// <summary>
        /// The part of the analysis cache key that identifies these settings. EMPTY for default values, so a
        /// tenant that never changes them keeps exactly the keys it has always had - and, after a reset, an
        /// analysis computed with the defaults is reused rather than recomputed.
        /// </summary>
        public string CacheKeySuffix => IsCustomised ? "::settings:v" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Values.Fingerprint() : string.Empty;

        /// <summary>Applies the values to <paramref name="options"/> and records what was applied.</summary>
        public void ApplyTo(CopilotAdoptionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            Values.ApplyTo(options);
            options.ScoreSettings = new CopilotAdoptionScoreSettingsInfo(
                IsCustomised ? Version : 0, Values.CustomisedFields, IsCustomised ? UpdatedUtc : null);
        }
    }

    /// <summary>
    /// What the report says about the score settings it used. Immutable, so <see cref="CopilotAdoptionOptions.Clone"/>
    /// can share it. Never says who changed them: the summary is read by everyone who can open the report.
    /// </summary>
    public sealed class CopilotAdoptionScoreSettingsInfo
    {
        [JsonConstructor]
        public CopilotAdoptionScoreSettingsInfo(long version, IEnumerable<string> customisedFields, DateTime? updatedUtc)
        {
            Version = version;
            CustomisedFields = (customisedFields ?? Enumerable.Empty<string>()).ToList().AsReadOnly();
            UpdatedUtc = updatedUtc;
        }

        public static CopilotAdoptionScoreSettingsInfo Defaults { get; } =
            new CopilotAdoptionScoreSettingsInfo(0, null, null);

        [JsonProperty("version")]
        public long Version { get; }

        [JsonProperty("customised")]
        public bool Customised => CustomisedFields.Count > 0;

        [JsonProperty("customisedFields")]
        public IReadOnlyList<string> CustomisedFields { get; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; }

        [JsonProperty("defaults")]
        public CopilotAdoptionScoreSettings DefaultValues => CopilotAdoptionScoreSettings.Defaults;
    }

    /// <summary>One change in the settings' audit trail.</summary>
    public sealed class CopilotAdoptionScoreSettingsChange
    {
        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("changedBy")]
        public string ChangedBy { get; set; }

        [JsonProperty("changedUtc")]
        public DateTime ChangedUtc { get; set; }

        [JsonProperty("changes")]
        public List<CopilotAdoptionScoreSettingsFieldChange> Changes { get; set; } = new List<CopilotAdoptionScoreSettingsFieldChange>();
    }

    public sealed class CopilotAdoptionScoreSettingsFieldChange
    {
        [JsonProperty("field")]
        public string Field { get; set; }

        [JsonProperty("oldValue")]
        public int OldValue { get; set; }

        [JsonProperty("newValue")]
        public int NewValue { get; set; }
    }

    public static class CopilotAdoptionScoreSettingsActions
    {
        public const string Save = "save";
        public const string Reset = "reset";
    }

    /// <summary>The stored document: the values in force, their version and the audit trail.</summary>
    public sealed class CopilotAdoptionScoreSettingsDocument
    {
        public const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        [JsonProperty("version")]
        public long Version { get; set; }

        [JsonProperty("settings")]
        public CopilotAdoptionScoreSettings Settings { get; set; } = CopilotAdoptionScoreSettings.Defaults;

        [JsonProperty("updatedBy")]
        public string UpdatedBy { get; set; }

        [JsonProperty("updatedUtc")]
        public DateTime? UpdatedUtc { get; set; }

        /// <summary>Newest first, at most <see cref="CopilotAdoptionScoreSettingsStore.MaxHistory"/> entries.</summary>
        [JsonProperty("history")]
        public List<CopilotAdoptionScoreSettingsChange> History { get; set; } = new List<CopilotAdoptionScoreSettingsChange>();

        public CopilotAdoptionEffectiveScoreSettings ToEffective() =>
            new CopilotAdoptionEffectiveScoreSettings(Version, Settings, UpdatedUtc);
    }

    /// <summary>The settings could not be read or written. Never answered with the defaults.</summary>
    public sealed class CopilotAdoptionScoreSettingsUnavailableException : Exception
    {
        public CopilotAdoptionScoreSettingsUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    /// <summary>A save or reset refused for a reason the administrator can act on. <see cref="Code"/> is stable.</summary>
    public sealed class CopilotAdoptionScoreSettingsRejectedException : Exception
    {
        public CopilotAdoptionScoreSettingsRejectedException(string code, IReadOnlyList<string> validationErrors = null) : base(code)
        {
            Code = code;
            ValidationErrors = validationErrors ?? new List<string>();
        }

        public string Code { get; }
        public IReadOnlyList<string> ValidationErrors { get; }
    }

    /// <summary>
    /// Keeps the settings document in the <see cref="StatePartitions.CopilotAdoptionSettings"/> partition of the
    /// AnalyticsState table: one row, so a read is one point lookup however large the tenant.
    /// </summary>
    /// <remarks>
    /// <para>Saves carry the version the administrator edited, and are refused with
    /// <see cref="CopilotAdoptionScoreSettingsErrorCodes.VersionConflict"/> when somebody saved in between. The
    /// check is read-then-write because <see cref="IKeyValueStore"/> has no conditional write, so two saves within
    /// the same few milliseconds can still both succeed; the later one wins and both appear in the history.</para>
    /// <para>A store that is not durable (no Storage connection string) still answers reads with the defaults -
    /// there is nothing to read - but refuses to save, because a save that vanishes on the next restart and is
    /// never seen by the other instances would be worse than no save.</para>
    /// </remarks>
    public sealed class CopilotAdoptionScoreSettingsStore
    {
        public const string DocumentKey = "Current";
        public const int MaxHistory = 50;
        private const int MaxNameLength = 256;

        private readonly IKeyValueStore _values;
        private readonly Func<DateTime> _utcNow;

        public CopilotAdoptionScoreSettingsStore(IKeyValueStore values, bool isDurable, Func<DateTime> utcNow = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            IsDurable = isDurable;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public bool IsDurable { get; }

        /// <summary>The stored document, or a version-0 default document when none has been saved.</summary>
        public async Task<CopilotAdoptionScoreSettingsDocument> GetAsync()
        {
            string json;
            try
            {
                json = await _values.GetStringAsync(DocumentKey).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    $"Couldn't read the Copilot Adoption score settings from {_values.Description}: {ex.Message}", ex);
            }

            if (json == null) return new CopilotAdoptionScoreSettingsDocument();

            CopilotAdoptionScoreSettingsDocument document;
            try
            {
                document = JsonConvert.DeserializeObject<CopilotAdoptionScoreSettingsDocument>(json);
            }
            catch (JsonException ex)
            {
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    "The stored Copilot Adoption score settings are not readable JSON.", ex);
            }

            // A document that is unreadable, from a newer schema, or breaks the rules is reported - not quietly
            // replaced by the defaults, which would silently change every score in the report.
            if (document?.Settings == null)
                throw new CopilotAdoptionScoreSettingsUnavailableException("The stored Copilot Adoption score settings are empty.");
            if (document.SchemaVersion > CopilotAdoptionScoreSettingsDocument.CurrentSchemaVersion)
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    $"The stored Copilot Adoption score settings use schema {document.SchemaVersion}, newer than this build understands.");
            var errors = document.Settings.Validate();
            if (errors.Count > 0)
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    "The stored Copilot Adoption score settings are invalid: " + string.Join(", ", errors));

            document.History = document.History ?? new List<CopilotAdoptionScoreSettingsChange>();
            return document;
        }

        public Task<CopilotAdoptionScoreSettingsDocument> SaveAsync(CopilotAdoptionScoreSettings settings, long expectedVersion, string changedBy)
        {
            if (settings == null) throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest);
            var errors = settings.Validate();
            if (errors.Count > 0) throw new CopilotAdoptionScoreSettingsRejectedException(errors[0], errors);
            return WriteAsync(settings.Clone(), expectedVersion, changedBy, CopilotAdoptionScoreSettingsActions.Save);
        }

        public Task<CopilotAdoptionScoreSettingsDocument> ResetAsync(long expectedVersion, string changedBy) =>
            WriteAsync(CopilotAdoptionScoreSettings.Defaults, expectedVersion, changedBy, CopilotAdoptionScoreSettingsActions.Reset);

        private async Task<CopilotAdoptionScoreSettingsDocument> WriteAsync(
            CopilotAdoptionScoreSettings settings, long expectedVersion, string changedBy, string action)
        {
            if (!IsDurable) throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured);

            var current = await GetAsync().ConfigureAwait(false);
            if (current.Version != expectedVersion)
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict);

            var changes = current.Settings.Fields().Zip(settings.Fields(), (was, proposed) => new { old = was, proposed })
                .Where(p => p.old.Value != p.proposed.Value)
                .Select(p => new CopilotAdoptionScoreSettingsFieldChange { Field = p.proposed.Name, OldValue = p.old.Value, NewValue = p.proposed.Value })
                .ToList();

            // Nothing to change is not an error, and not a new version: re-saving the same numbers must not
            // invalidate every instance's cached analysis.
            if (changes.Count == 0) return current;

            var who = string.IsNullOrWhiteSpace(changedBy) ? null : changedBy.Trim();
            if (who != null && who.Length > MaxNameLength) who = who.Substring(0, MaxNameLength);
            var now = _utcNow();
            var next = new CopilotAdoptionScoreSettingsDocument
            {
                Version = current.Version + 1,
                Settings = settings,
                UpdatedBy = who,
                UpdatedUtc = now,
                History = new[]
                {
                    new CopilotAdoptionScoreSettingsChange
                    {
                        Version = current.Version + 1,
                        Action = action,
                        ChangedBy = who,
                        ChangedUtc = now,
                        Changes = changes,
                    },
                }.Concat(current.History).Take(MaxHistory).ToList(),
            };

            try
            {
                await _values.SetStringAsync(DocumentKey, JsonConvert.SerializeObject(next)).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    $"Couldn't save the Copilot Adoption score settings to {_values.Description}: {ex.Message}", ex);
            }
            return next;
        }
    }
}
