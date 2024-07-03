using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;

namespace UsageReporting
{
    /// <summary>
    /// Something that has to go in Cosmos DB
    /// </summary>
    public abstract class StatsCosmosDoc
    {
        public abstract string id { get; set; }
    }

    /// <summary>
    /// Stats for this solution runtime. Anonimised. 
    /// </summary>
    public class AnonUsageStatsModel : StatsCosmosDoc
    {
        public AnonUsageStatsModel() { }

        public string AnonClientId { get; set; } = null;

        public override string id { set => AnonClientId = value; get => AnonClientId; } // For Cosmos PK

        /// <summary>
        /// Amount of records retrieved thanks to AI calls to Azure
        /// </summary>
        public int? DataPointsFromAITotal { get; set; } = null;
        public string ConfiguredSolutionsEnabledDescription { get; set; } = null;
        public string ConfiguredImportsEnabledDescription { get; set; } = null;
        public List<TableStat> TableStats { get; set; } = null;
        public string BuildVersionLabel { get; set; } = null;

        public DateTime? Generated { get; set; } = null;

        [JsonIgnore] public bool IsValid => !string.IsNullOrEmpty(AnonClientId) && Generated != null && Generated.Value > DateTime.MinValue;


        public class TableStat
        {
            public string TableName { get; set; }
            public decimal TotalSpaceMB { get; set; }
            public long Rows { get; set; }

            public override string ToString()
            {
                return $"{TableName}: {Rows} rows, {TotalSpaceMB}mb";
            }
        }

        /// <summary>
        /// Update this with whatever an update has
        /// </summary>
        public AnonUsageStatsModel UpdateWith(AnonUsageStatsModel updateFromClientWithNewId)
        {
            if (updateFromClientWithNewId != null && updateFromClientWithNewId.IsValid)
            {
                if (updateFromClientWithNewId.TableStats != null)
                {
                    TableStats = new List<TableStat>(updateFromClientWithNewId.TableStats);
                }

                if (!string.IsNullOrEmpty(updateFromClientWithNewId.ConfiguredImportsEnabledDescription))
                {
                    ConfiguredImportsEnabledDescription = updateFromClientWithNewId.ConfiguredImportsEnabledDescription;
                }
                if (!string.IsNullOrEmpty(updateFromClientWithNewId.ConfiguredSolutionsEnabledDescription))
                {
                    ConfiguredSolutionsEnabledDescription = updateFromClientWithNewId.ConfiguredSolutionsEnabledDescription;
                }
                if (!string.IsNullOrEmpty(updateFromClientWithNewId.BuildVersionLabel))
                {
                    BuildVersionLabel = updateFromClientWithNewId.BuildVersionLabel;
                }
                if (updateFromClientWithNewId.DataPointsFromAITotal.HasValue)
                {
                    DataPointsFromAITotal = updateFromClientWithNewId.DataPointsFromAITotal;
                }

                Generated = updateFromClientWithNewId.Generated;
            }

            return this;
        }

        public override string ToString()
        {
            return $"AnonClientId: {AnonClientId}, Generated: {Generated}, DataPointsFromAITotal: {DataPointsFromAITotal}, ConfiguredSolutionsEnabledDescription: {ConfiguredSolutionsEnabledDescription}, ConfiguredImportsEnabledDescription: {ConfiguredImportsEnabledDescription}, TableStats: {TableStats}, BuildVersionLabel: {BuildVersionLabel}";
        }

        /// <summary>
        /// Aribrary way to generate a hash for a payload between the client and the server
        /// </summary>
        public bool IsValidSecretForThisObject(string cipher, string sharedSecretValue)
        {
            CheckEncryptionParamsValid();
            return StringUtils.IsHashedMatch(sharedSecretValue + Generated.Value.Ticks, cipher);
        }

        /// <summary>
        /// Aribrary way to generate a hash for a payload between the client and the server
        /// </summary>
        public string GenerateSecretFromObjectProps(string sharedSecretValue)
        {
            CheckEncryptionParamsValid();
            return StringUtils.GetHashedStringWithSalt(sharedSecretValue + Generated.Value.Ticks);
        }

        private void CheckEncryptionParamsValid()
        {
            if (!Generated.HasValue)
            {
                throw new InvalidOperationException("Invalid ");
            }
        }
    }

    /// <summary>
    /// An update sent
    /// </summary>
    public class HistoricalUpdate : StatsCosmosDoc
    {
        public HistoricalUpdate()
        {
        }

        public HistoricalUpdate(AnonUsageStatsModel update) : this()
        {
            Update = update;
            id = Update != null && Update.Generated != null && Update.Generated.HasValue ? $"{Update.AnonClientId}-{Update.Generated.Value.Ticks}" : null;
        }


        public AnonUsageStatsModel Update { get; set; }
        public override string id { get; set; }
    }

    public class TelemetryPayload
    {
        public TelemetryPayload()
        {
        }

        public TelemetryPayload(AnonUsageStatsModel stats, string sharedSecretValue) : this()
        {
            StatsModel = stats;
            Secret = stats.GenerateSecretFromObjectProps(sharedSecretValue);
        }

        public AnonUsageStatsModel StatsModel { get; set; }
        public string Secret { get; set; }
    }
}
