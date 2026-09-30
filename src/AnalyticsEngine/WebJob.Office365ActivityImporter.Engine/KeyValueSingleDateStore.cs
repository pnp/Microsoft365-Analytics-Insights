using Common.Entities.State;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine
{
    /// <summary>
    /// A single date value kept under one key of an <see cref="IKeyValueStore"/> - in production a row of the
    /// <see cref="StateStore.TableName"/> Azure Table - so it survives WebJob restarts.
    /// </summary>
    /// <remarks>
    /// Stored in round-trip ("o") format, so an offset is kept
    /// when the value has one. Reads that fail propagate: this store has no opinion on whether an unreadable stamp should
    /// run or skip the work it throttles - its callers decide.
    /// </remarks>
    public class KeyValueSingleDateStore : ISingleDateStore
    {
        private readonly IKeyValueStore _store;
        private readonly string _key;

        public KeyValueSingleDateStore(IKeyValueStore store, string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("A key is required.", nameof(key));

            _store = store ?? throw new ArgumentNullException(nameof(store));
            _key = key;
        }

        public string Key => _key;

        public async Task<DateTime?> GetLastDT()
        {
            var lastVal = await _store.GetStringAsync(_key);
            if (!string.IsNullOrEmpty(lastVal)
                && DateTime.TryParse(lastVal, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }

            return null;
        }

        public async Task SaveDT()
        {
            await SaveDT(DateTime.Now);
        }

        public async Task SaveDT(DateTime dt)
        {
            await _store.SetStringAsync(_key, dt.ToString("o", CultureInfo.InvariantCulture));
        }

        public async Task DeleteDt()
        {
            await _store.DeleteAsync(_key);
        }
    }
}
