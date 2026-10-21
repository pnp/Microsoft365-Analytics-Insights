using Common.Entities.State;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.CopilotAdoption
{
    /// <summary>
    /// Tenant-wide adoption scoring and decision settings an administrator may change (issues #683 and #684).
    /// </summary>
    /// <remarks>
    /// <para>Tenant-wide scoring and decision controls only. History/query windows, normalisation periods,
    /// workload calibration targets and performance caps remain fixed.</para>
    /// <para>Weights are whole percentages so an administrator types 50/30/20 rather than 0.5/0.3/0.2, and
    /// <see cref="ApplyTo"/> divides by 100 - which gives exactly the doubles the defaults have always used,
    /// so default settings change no score by even a rounding error.</para>
    /// </remarks>
    [JsonConverter(typeof(CopilotAdoptionScoreSettingsConverter))]
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

        [JsonProperty("frequencyTargetRatio")]
        public double FrequencyTargetRatio { get; set; } = 0.6;
        [JsonProperty("workingDaysPerWeek")]
        public int WorkingDaysPerWeek { get; set; } = 5;
        [JsonProperty("depthTargetInteractionsPerActiveDay")]
        public double DepthTargetInteractionsPerActiveDay { get; set; } = 5;
        [JsonProperty("depthMinActiveDays")]
        public int DepthMinActiveDays { get; set; } = 3;
        [JsonProperty("breadthTargetApps")]
        public int BreadthTargetApps { get; set; } = 3;
        [JsonProperty("habitModerateMinDays")]
        public int HabitModerateMinDays { get; set; } = 6;
        [JsonProperty("habitFrequentMinDays")]
        public int HabitFrequentMinDays { get; set; } = 11;
        [JsonProperty("habitDailyMinDays")]
        public int HabitDailyMinDays { get; set; } = 20;
        [JsonProperty("opportunityRecommendScore")]
        public int OpportunityRecommendScore { get; set; } = 50;
        [JsonProperty("opportunityProvenDemandMinActiveDays")]
        public int OpportunityProvenDemandMinActiveDays { get; set; } = 3;
        [JsonProperty("opportunityUnlicensedCopilotWeight")]
        public int OpportunityUnlicensedCopilotWeight { get; set; } = 35;
        [JsonProperty("opportunityCollaborationWeight")]
        public int OpportunityCollaborationWeight { get; set; } = 25;
        [JsonProperty("opportunityEmailWeight")]
        public int OpportunityEmailWeight { get; set; } = 20;
        [JsonProperty("opportunityDocumentWeight")]
        public int OpportunityDocumentWeight { get; set; } = 20;
        [JsonProperty("coworkLoadMinScore")]
        public int CoworkLoadMinScore { get; set; } = 50;
        [JsonProperty("coworkFluencyMinScore")]
        public int CoworkFluencyMinScore { get; set; } = 50;
        [JsonProperty("coworkRegularMinActiveDays")]
        public int CoworkRegularMinActiveDays { get; set; } = 3;
        [JsonProperty("coworkCollaborationWeight")]
        public int CoworkCollaborationWeight { get; set; } = 20;
        [JsonProperty("coworkMeetingWeight")]
        public int CoworkMeetingWeight { get; set; } = 35;
        [JsonProperty("coworkEmailWeight")]
        public int CoworkEmailWeight { get; set; } = 25;
        [JsonProperty("coworkDocumentWeight")]
        public int CoworkDocumentWeight { get; set; } = 20;
        [JsonProperty("agentReviewInactiveDays")]
        public int AgentReviewInactiveDays { get; set; } = 30;
        [JsonProperty("agentRetireInactiveDays")]
        public int AgentRetireInactiveDays { get; set; } = 90;
        [JsonProperty("agentNewDays")]
        public int AgentNewDays { get; set; } = 30;
        [JsonProperty("agentMinUsers")]
        public int AgentMinUsers { get; set; } = 3;
        [JsonProperty("reclaimGraceDays")]
        public int ReclaimGraceDays { get; set; } = 30;
        [JsonProperty("minSeatsPerSegment")]
        public int MinSeatsPerSegment { get; set; } = 5;

        public static CopilotAdoptionScoreSettings Defaults => new CopilotAdoptionScoreSettings();

        [JsonIgnore]
        internal IReadOnlyCollection<string> ProvidedFields { get; set; }

        [JsonIgnore]
        public bool IsDefault => Equals(Defaults);

        /// <summary>Stable camelCase names of the settings that differ from the defaults, in display order.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> CustomisedFields =>
            Fields().Where(f => f.Value != f.Default).Select(f => f.Name).ToList();

        internal IEnumerable<(string Name, double Value, double Default)> Fields()
        {
            yield return (CopilotAdoptionScoreSettingsFields.FrequencyWeightPercent, FrequencyWeightPercent, DefaultFrequencyWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.DepthWeightPercent, DepthWeightPercent, DefaultDepthWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.BreadthWeightPercent, BreadthWeightPercent, DefaultBreadthWeightPercent);
            yield return (CopilotAdoptionScoreSettingsFields.DevelopingScore, DevelopingScore, DefaultDevelopingScore);
            yield return (CopilotAdoptionScoreSettingsFields.EstablishedScore, EstablishedScore, DefaultEstablishedScore);
            yield return (CopilotAdoptionScoreSettingsFields.ChampionScore, ChampionScore, DefaultChampionScore);
            yield return ("frequencyTargetRatio", FrequencyTargetRatio, 0.6);
            yield return ("workingDaysPerWeek", WorkingDaysPerWeek, 5);
            yield return ("depthTargetInteractionsPerActiveDay", DepthTargetInteractionsPerActiveDay, 5);
            yield return ("depthMinActiveDays", DepthMinActiveDays, 3);
            yield return ("breadthTargetApps", BreadthTargetApps, 3);
            yield return ("habitModerateMinDays", HabitModerateMinDays, 6);
            yield return ("habitFrequentMinDays", HabitFrequentMinDays, 11);
            yield return ("habitDailyMinDays", HabitDailyMinDays, 20);
            yield return ("opportunityRecommendScore", OpportunityRecommendScore, 50);
            yield return ("opportunityProvenDemandMinActiveDays", OpportunityProvenDemandMinActiveDays, 3);
            yield return ("opportunityUnlicensedCopilotWeight", OpportunityUnlicensedCopilotWeight, 35);
            yield return ("opportunityCollaborationWeight", OpportunityCollaborationWeight, 25);
            yield return ("opportunityEmailWeight", OpportunityEmailWeight, 20);
            yield return ("opportunityDocumentWeight", OpportunityDocumentWeight, 20);
            yield return ("coworkLoadMinScore", CoworkLoadMinScore, 50);
            yield return ("coworkFluencyMinScore", CoworkFluencyMinScore, 50);
            yield return ("coworkRegularMinActiveDays", CoworkRegularMinActiveDays, 3);
            yield return ("coworkCollaborationWeight", CoworkCollaborationWeight, 20);
            yield return ("coworkMeetingWeight", CoworkMeetingWeight, 35);
            yield return ("coworkEmailWeight", CoworkEmailWeight, 25);
            yield return ("coworkDocumentWeight", CoworkDocumentWeight, 20);
            yield return ("agentReviewInactiveDays", AgentReviewInactiveDays, 30);
            yield return ("agentRetireInactiveDays", AgentRetireInactiveDays, 90);
            yield return ("agentNewDays", AgentNewDays, 30);
            yield return ("agentMinUsers", AgentMinUsers, 3);
            yield return ("reclaimGraceDays", ReclaimGraceDays, 30);
            yield return ("minSeatsPerSegment", MinSeatsPerSegment, 5);
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

            if (!FiniteInRange(FrequencyTargetRatio, double.Epsilon, 1)
                || WorkingDaysPerWeek < 1 || WorkingDaysPerWeek > 7
                || !FiniteInRange(DepthTargetInteractionsPerActiveDay, double.Epsilon, 1000000)
                || DepthMinActiveDays < 1 || DepthMinActiveDays > 365
                || BreadthTargetApps < 1 || BreadthTargetApps > 100)
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.TargetOutOfRange);
            if (!(1 <= HabitModerateMinDays && HabitModerateMinDays < HabitFrequentMinDays
                && HabitFrequentMinDays < HabitDailyMinDays && HabitDailyMinDays <= 28))
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.HabitDaysNotAscending);
            if (!ValidWeights(OpportunityUnlicensedCopilotWeight, OpportunityCollaborationWeight, OpportunityEmailWeight, OpportunityDocumentWeight)
                || !ValidWeights(CoworkCollaborationWeight, CoworkMeetingWeight, CoworkEmailWeight, CoworkDocumentWeight))
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.DecisionWeightsInvalid);
            if (new[] { OpportunityRecommendScore, CoworkLoadMinScore, CoworkFluencyMinScore }.Any(v => v < 1 || v > 100)
                || OpportunityProvenDemandMinActiveDays < 1 || OpportunityProvenDemandMinActiveDays > 365
                || CoworkRegularMinActiveDays < 1 || CoworkRegularMinActiveDays > 365)
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.DecisionThresholdInvalid);
            // WindowStartUtc includes today: 120 days of history yields at most 119 days of observed age.
            // New uses <=, and must not mask retirement when an older first interaction was outside the query.
            if (!(1 <= AgentReviewInactiveDays && AgentReviewInactiveDays < AgentRetireInactiveDays && AgentRetireInactiveDays <= 120)
                || AgentNewDays < 1 || AgentNewDays > CopilotAdoptionOptions.Default.AgentHistoryDays - 2
                || AgentNewDays >= AgentRetireInactiveDays || AgentMinUsers < 3 || AgentMinUsers > 200000)
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.AgentSettingsInvalid);
            if (ReclaimGraceDays < 1 || ReclaimGraceDays > 365)
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.SeatWindowsInvalid);
            if (MinSeatsPerSegment < 5 || MinSeatsPerSegment > 200000)
                errors.Add(CopilotAdoptionScoreSettingsErrorCodes.SegmentMinimumInvalid);
            return errors;
        }

        private static bool FiniteInRange(double value, double min, double max) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= min && value <= max;

        private static bool ValidWeights(params int[] values) =>
            values.All(v => v >= 0 && v <= 100) && values.Sum() == 100;

        /// <summary>
        /// Writes these settings into <paramref name="options"/>. Default settings write the values
        /// <see cref="CopilotAdoptionOptions"/> already starts with, so nothing changes.
        /// </summary>
        public void ApplyTo(CopilotAdoptionOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var errors = Validate();
            if (errors.Count > 0) throw new CopilotAdoptionScoreSettingsRejectedException(errors[0], errors);
            options.FrequencyWeight = FrequencyWeightPercent / 100d;
            options.DepthWeight = DepthWeightPercent / 100d;
            options.BreadthWeight = BreadthWeightPercent / 100d;
            options.DevelopingScore = DevelopingScore;
            options.EstablishedScore = EstablishedScore;
            options.ChampionScore = ChampionScore;
            options.FrequencyTargetRatio = FrequencyTargetRatio;
            options.WorkingDaysPerWeek = WorkingDaysPerWeek;
            options.DepthTargetInteractionsPerActiveDay = DepthTargetInteractionsPerActiveDay;
            options.DepthMinActiveDays = DepthMinActiveDays;
            options.BreadthTargetApps = BreadthTargetApps;
            options.HabitModerateMinDays = HabitModerateMinDays;
            options.HabitFrequentMinDays = HabitFrequentMinDays;
            options.HabitDailyMinDays = HabitDailyMinDays;
            options.OpportunityRecommendScore = OpportunityRecommendScore;
            options.OpportunityProvenDemandMinActiveDays = OpportunityProvenDemandMinActiveDays;
            options.OpportunityUnlicensedCopilotWeight = OpportunityUnlicensedCopilotWeight;
            options.OpportunityCollaborationWeight = OpportunityCollaborationWeight;
            options.OpportunityEmailWeight = OpportunityEmailWeight;
            options.OpportunityDocumentWeight = OpportunityDocumentWeight;
            options.CoworkLoadMinScore = CoworkLoadMinScore;
            options.CoworkFluencyMinScore = CoworkFluencyMinScore;
            options.CoworkRegularMinActiveDays = CoworkRegularMinActiveDays;
            options.CoworkCollaborationWeight = CoworkCollaborationWeight;
            options.CoworkMeetingWeight = CoworkMeetingWeight;
            options.CoworkEmailWeight = CoworkEmailWeight;
            options.CoworkDocumentWeight = CoworkDocumentWeight;
            options.AgentReviewInactiveDays = AgentReviewInactiveDays;
            options.AgentRetireInactiveDays = AgentRetireInactiveDays;
            options.AgentNewDays = AgentNewDays;
            options.AgentMinUsers = AgentMinUsers;
            options.ReclaimGraceDays = ReclaimGraceDays;
            options.MinSeatsPerSegment = MinSeatsPerSegment;
        }

        public CopilotAdoptionScoreSettings Clone() => (CopilotAdoptionScoreSettings)MemberwiseClone();

        /// <summary>A compact, culture-invariant fingerprint of the values, used in the analysis cache key.</summary>
        public string Fingerprint() =>
            string.Join("-", Fields().Select(f => f.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

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
        public const string TargetOutOfRange = "targetOutOfRange";
        public const string HabitDaysNotAscending = "habitDaysNotAscending";
        public const string DecisionWeightsInvalid = "decisionWeightsInvalid";
        public const string DecisionThresholdInvalid = "decisionThresholdInvalid";
        public const string AgentSettingsInvalid = "agentSettingsInvalid";
        public const string SeatWindowsInvalid = "seatWindowsInvalid";
        public const string SegmentMinimumInvalid = "segmentMinimumInvalid";
        public const string SeatImpactConfirmationRequired = "seatImpactConfirmationRequired";
        public const string VersionConflict = "settingsChanged";
        public const string StorageNotConfigured = "storageNotConfigured";
        public const string StateUnavailable = "stateUnavailable";

        /// <summary>
        /// Sent by the adoption report API (503) when the settings cannot be read. The report refuses rather than
        /// silently scoring with the defaults, which would publish figures computed with rules nobody chose.
        /// </summary>
        public const string ReportSettingsUnavailable = "copilotAdoptionSettingsUnavailable";
    }

    /// <summary>Reject unknown fields, coercion and fractional integers instead of silently changing a rule.</summary>
    public sealed class CopilotAdoptionScoreSettingsConverter : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type objectType) => objectType == typeof(CopilotAdoptionScoreSettings);
        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer) => throw new NotSupportedException();
        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var json = Newtonsoft.Json.Linq.JObject.Load(reader,
                new Newtonsoft.Json.Linq.JsonLoadSettings { DuplicatePropertyNameHandling = Newtonsoft.Json.Linq.DuplicatePropertyNameHandling.Error });
            var properties = typeof(CopilotAdoptionScoreSettings).GetProperties()
                .Select(p => new { Property = p, Json = p.GetCustomAttributes(typeof(JsonPropertyAttribute), false).Cast<JsonPropertyAttribute>().SingleOrDefault() })
                .Where(p => p.Json != null).ToDictionary(p => p.Json.PropertyName, p => p.Property, StringComparer.Ordinal);
            foreach (var field in json.Properties())
            {
                if (!properties.TryGetValue(field.Name, out var property)
                    || (field.Value.Type != Newtonsoft.Json.Linq.JTokenType.Integer
                        && field.Value.Type != Newtonsoft.Json.Linq.JTokenType.Float))
                    throw new JsonSerializationException("Unknown or non-numeric adoption setting.");
                if (property.PropertyType == typeof(int))
                {
                    var value = (double)field.Value;
                    if (double.IsNaN(value) || double.IsInfinity(value) || value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                        throw new JsonSerializationException("Adoption setting requires a whole number.");
                }
            }
            var result = new CopilotAdoptionScoreSettings();
            using (var values = json.CreateReader()) serializer.Populate(values, result);
            result.ProvidedFields = json.Properties().Select(p => p.Name).ToList().AsReadOnly();
            return result;
        }
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
        public double OldValue { get; set; }

        [JsonProperty("newValue")]
        public double NewValue { get; set; }
    }

    public static class CopilotAdoptionScoreSettingsActions
    {
        public const string Save = "save";
        public const string Reset = "reset";
    }

    /// <summary>The stored document: the values in force, their version and the audit trail.</summary>
    public sealed class CopilotAdoptionScoreSettingsDocument
    {
        public const int CurrentSchemaVersion = 2;

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
    /// write itself is conditional on the row's ETag as it was read (<see cref="IConditionalKeyValueStore"/>), so
    /// of two saves or resets made against the same version exactly one lands, whichever web instance handles them;
    /// the other is refused rather than overwriting it. Each history entry's old values are therefore the values
    /// of the version it replaced, and the versions in the history form one unbroken sequence.</para>
    /// <para>A store that is not durable (no Storage connection string) still answers reads with the defaults -
    /// there is nothing to read - but refuses to save, because a save that vanishes on the next restart and is
    /// never seen by the other instances would be worse than no save.</para>
    /// </remarks>
    public sealed class CopilotAdoptionScoreSettingsStore
    {
        public const string DocumentKey = "Current";
        public const int MaxHistory = 50;
        public static readonly TimeSpan DefaultStorageTimeout = TimeSpan.FromSeconds(10);
        private const int MaxNameLength = 256;

        private readonly IKeyValueStore _values;
        private readonly IConditionalKeyValueStore _conditional;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _storageTimeout;

        /// <param name="values">Must support conditional writes when <paramref name="isDurable"/>: every
        /// <see cref="StateStore"/> partition does.</param>
        public CopilotAdoptionScoreSettingsStore(
            IKeyValueStore values, bool isDurable, Func<DateTime> utcNow = null, TimeSpan? storageTimeout = null)
        {
            _values = values ?? throw new ArgumentNullException(nameof(values));
            _conditional = values as IConditionalKeyValueStore;
            if (isDurable && _conditional == null)
                throw new ArgumentException("A durable settings store needs conditional writes, so concurrent saves cannot overwrite each other.", nameof(values));
            IsDurable = isDurable;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _storageTimeout = storageTimeout ?? DefaultStorageTimeout;
            if (_storageTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(storageTimeout));
        }

        public bool IsDurable { get; }

        /// <summary>The stored document, or a version-0 default document when none has been saved.</summary>
        public Task<CopilotAdoptionScoreSettingsDocument> GetAsync(CancellationToken cancellationToken = default) =>
            WithStorageDeadlineAsync(async token => (await ReadAsync(token).ConfigureAwait(false)).Document, cancellationToken);

        private async Task<(CopilotAdoptionScoreSettingsDocument Document, string VersionToken)> ReadAsync(CancellationToken cancellationToken)
        {
            string json, token = null;
            try
            {
                if (_conditional != null)
                {
                    var read = await _conditional.GetVersionedAsync(DocumentKey, cancellationToken).ConfigureAwait(false);
                    json = read.Value;
                    token = read.VersionToken;
                }
                else
                {
                    json = await _values.GetStringAsync(DocumentKey, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    $"Couldn't read the Copilot Adoption score settings from {_values.Description}: {ex.Message}", ex);
            }

            if (json == null) return (new CopilotAdoptionScoreSettingsDocument(), token);

            CopilotAdoptionScoreSettingsDocument document;
            try
            {
                document = JsonConvert.DeserializeObject<CopilotAdoptionScoreSettingsDocument>(json,
                    new JsonSerializerSettings { MissingMemberHandling = MissingMemberHandling.Error });
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
            if (document.SchemaVersion < 1 || document.SchemaVersion > CopilotAdoptionScoreSettingsDocument.CurrentSchemaVersion || document.Version < 0)
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    "The stored Copilot Adoption score settings have an unsupported schema or version.");
            var errors = document.Settings.Validate();
            if (errors.Count > 0)
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    "The stored Copilot Adoption score settings are invalid: " + string.Join(", ", errors));

            document.History = document.History ?? new List<CopilotAdoptionScoreSettingsChange>();
            return (document, token);
        }

        public Task<CopilotAdoptionScoreSettingsDocument> SaveAsync(
            CopilotAdoptionScoreSettings settings, long expectedVersion, string changedBy, CancellationToken cancellationToken = default)
        {
            if (settings == null) throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest);
            var errors = settings.Validate();
            if (errors.Count > 0) throw new CopilotAdoptionScoreSettingsRejectedException(errors[0], errors);
            return WithStorageDeadlineAsync(
                token => WriteAsync(settings.Clone(), expectedVersion, changedBy, CopilotAdoptionScoreSettingsActions.Save, token), cancellationToken);
        }

        public Task<CopilotAdoptionScoreSettingsDocument> ResetAsync(
            long expectedVersion, string changedBy, CancellationToken cancellationToken = default) =>
            WithStorageDeadlineAsync(
                token => WriteAsync(CopilotAdoptionScoreSettings.Defaults, expectedVersion, changedBy, CopilotAdoptionScoreSettingsActions.Reset, token), cancellationToken);

        private async Task<CopilotAdoptionScoreSettingsDocument> WriteAsync(
            CopilotAdoptionScoreSettings settings, long expectedVersion, string changedBy, string action, CancellationToken cancellationToken)
        {
            if (!IsDurable) throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.StorageNotConfigured);

            var (current, versionToken) = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (current.Version != expectedVersion)
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict);
            // Older editors may know only the original six settings. Missing additive fields may default on
            // read, but must not silently erase an administrator's newer custom rules on write.
            if (settings.ProvidedFields != null && current.Settings.CustomisedFields.Any(f => !settings.ProvidedFields.Contains(f)))
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.InvalidRequest);

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

            bool written;
            try
            {
                written = await _conditional.TrySetStringAsync(DocumentKey, JsonConvert.SerializeObject(next), versionToken, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new CopilotAdoptionScoreSettingsUnavailableException(
                    $"Couldn't save the Copilot Adoption score settings to {_values.Description}: {ex.Message}", ex);
            }

            // Another administrator's save or reset landed between the read and this write. Refused, not retried: the
            // change was made against values that are no longer in force, so its author must see the new ones first.
            if (!written)
                throw new CopilotAdoptionScoreSettingsRejectedException(CopilotAdoptionScoreSettingsErrorCodes.VersionConflict);
            return next;
        }

        private async Task<T> WithStorageDeadlineAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken caller)
        {
            caller.ThrowIfCancellationRequested();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller))
            {
                deadline.CancelAfter(_storageTimeout);
                var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (deadline.Token.Register(() => cancelled.TrySetResult(true)))
                {
                    try
                    {
                        // The token reaches lazy table opening, the SDK request and its retries. The race is
                        // only a backstop for legacy/non-cooperative stores, not a substitute for cancellation.
                        var task = operation(deadline.Token);
                        if (!task.IsCompleted && await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                        {
                            _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            deadline.Token.ThrowIfCancellationRequested();
                        }
                        var result = await task.ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        return result;
                    }
                    catch (Exception ex) when (caller.IsCancellationRequested)
                    {
                        throw new OperationCanceledException("The score settings request was cancelled.", ex, caller);
                    }
                    catch (Exception ex) when (deadline.IsCancellationRequested)
                    {
                        throw new CopilotAdoptionScoreSettingsUnavailableException(
                            "The Copilot Adoption score settings storage deadline expired.",
                            new TimeoutException("The score settings storage operation timed out.", ex));
                    }
                    catch (OperationCanceledException ex)
                    {
                        throw new CopilotAdoptionScoreSettingsUnavailableException(
                            "The Copilot Adoption score settings storage operation was cancelled.", ex);
                    }
                }
            }
        }
    }
}
