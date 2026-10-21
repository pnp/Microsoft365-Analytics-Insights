using Common.Entities.Config;
using Common.Entities.State;
using Common.Entities.UserDiagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDiagnostics
{
    /// <summary>A completion stamp from the importer's schedule partition, and whether it could be read.</summary>
    public sealed class ImportStamp
    {
        public ImportStamp(string state, DateTime? lastSuccessUtc)
        {
            State = state;
            LastSuccessUtc = lastSuccessUtc;
        }

        /// <summary>One of <see cref="StampStates"/>.</summary>
        public string State { get; }

        public DateTime? LastSuccessUtc { get; }
    }

    /// <summary>Reads importer completion stamps. Never throws: an unreadable store is a state, not an error.</summary>
    public interface IImportStampReader
    {
        Task<IReadOnlyDictionary<string, ImportStamp>> ReadAsync(IEnumerable<string> keys);
    }

    /// <summary>
    /// Reads the <see cref="StatePartitions.ImportSchedule"/> stamps the importer writes (see <see cref="ImportScheduleKeys"/>).
    /// </summary>
    public sealed class TableImportStampReader : IImportStampReader
    {
        private readonly Func<IKeyValueStore> _open;

        public TableImportStampReader(Func<IKeyValueStore> open)
        {
            _open = open;
        }

        public static TableImportStampReader ForThisDeployment()
        {
            var config = new AppConfig();
            return new TableImportStampReader(StateStore.IsConfigured(config)
                ? () => StateStore.TryOpen(config, StatePartitions.ImportSchedule)
                : (Func<IKeyValueStore>)null);
        }

        public async Task<IReadOnlyDictionary<string, ImportStamp>> ReadAsync(IEnumerable<string> keys)
        {
            var result = new Dictionary<string, ImportStamp>(StringComparer.Ordinal);
            if (_open == null)
            {
                foreach (var key in keys) result[key] = new ImportStamp(StampStates.StorageNotConfigured, null);
                return result;
            }

            IKeyValueStore store;
            try
            {
                store = _open();
            }
            catch (Exception)
            {
                foreach (var key in keys) result[key] = new ImportStamp(StampStates.StorageUnavailable, null);
                return result;
            }

            foreach (var key in keys)
            {
                if (result.ContainsKey(key)) continue;
                try
                {
                    result[key] = Parse(await store.GetStringAsync(key));
                }
                catch (Exception)
                {
                    result[key] = new ImportStamp(StampStates.StorageUnavailable, null);
                }
            }
            return result;
        }

        /// <summary>The importer writes round-trip ("o") timestamps, some with a local offset; they are returned as UTC.</summary>
        internal static ImportStamp Parse(string raw)
        {
            if (!string.IsNullOrEmpty(raw)
                && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                var utc = parsed.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc) : parsed.ToUniversalTime();
                return new ImportStamp(StampStates.Recorded, utc);
            }
            return new ImportStamp(StampStates.NotRecorded, null);
        }
    }
}
