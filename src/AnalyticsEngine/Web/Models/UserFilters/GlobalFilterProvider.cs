using Common.Entities.Config;
using Common.Entities.UserFilters;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserFilters
{
    /// <summary>The global filter as this web process currently holds it.</summary>
    internal sealed class GlobalFilterState
    {
        internal GlobalFilterState(GlobalFilterRecord record, GlobalFilterDefinition definition, string parseError)
        {
            Record = record ?? throw new ArgumentNullException(nameof(record));
            Definition = definition;
            ParseError = parseError;
        }

        public GlobalFilterRecord Record { get; }

        /// <summary>The parsed definition; <c>null</c> when the stored text could not be read (see <see cref="ParseError"/>).</summary>
        public GlobalFilterDefinition Definition { get; }

        /// <summary>Why the stored filter could not be read - a newer version wrote it, or it was edited by hand. Null when it was read.</summary>
        public string ParseError { get; }

        /// <summary>True when a global filter is defined, whether or not it could be read.</summary>
        public bool IsDefined => ParseError != null || (Definition != null && !Definition.IsEmpty);
    }

    /// <summary>Supplies the administrator's global filter to every report.</summary>
    internal interface IGlobalFilterProvider
    {
        /// <summary>The current filter. Throws when it cannot be read - callers fail closed.</summary>
        Task<GlobalFilterState> GetAsync(CancellationToken cancellationToken);

        /// <summary>Forgets the cached filter, so the next request reads it again - after a save.</summary>
        void Invalidate();

        IGlobalFilterStore Store { get; }
    }

    /// <summary>Providers for tests and for controllers built without the deployment's database.</summary>
    internal static class GlobalFilterProviders
    {
        /// <summary>No global filter, ever. Only test constructors use this; production reads the database.</summary>
        public static readonly IGlobalFilterProvider None = new FixedGlobalFilterProvider(
            new GlobalFilterRecord { StorageAvailable = false });

        /// <summary>A provider that always answers with <paramref name="record"/> - for tests.</summary>
        public static IGlobalFilterProvider Fixed(GlobalFilterRecord record) => new FixedGlobalFilterProvider(record);

        private sealed class FixedGlobalFilterProvider : IGlobalFilterProvider
        {
            private readonly GlobalFilterState _state;

            public FixedGlobalFilterProvider(GlobalFilterRecord record)
            {
                _state = CachedGlobalFilterProvider.Parse(record ?? throw new ArgumentNullException(nameof(record)));
            }

            public IGlobalFilterStore Store => throw new NotSupportedException("A fixed global filter cannot be saved.");

            public Task<GlobalFilterState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_state);

            public void Invalidate()
            {
            }
        }
    }

    /// <summary>
    /// Holds the global filter for <see cref="_freshFor"/> per web process, so a report request costs no
    /// extra database round trip.
    /// </summary>
    /// <remarks>
    /// <para>A save invalidates this process's copy at once. Another instance of a scaled-out web app picks
    /// the change up when its copy expires - within a minute - which the administration page says.</para>
    /// <para><b>Fails closed.</b> A filter that cannot be read is an exception, never "no filter": the
    /// caller refuses the report rather than showing a reader the people the filter was set to hide.
    /// The one exception is a database that predates the filter's table, which can hold no filter.</para>
    /// <para>Concurrent first requests share one read, and the read is never tied to a request's
    /// cancellation, for the same reasons as <see cref="CachedUserDirectorySource"/>.</para>
    /// </remarks>
    internal sealed class CachedGlobalFilterProvider : IGlobalFilterProvider
    {
        public static readonly CachedGlobalFilterProvider Default = new CachedGlobalFilterProvider(
            new Lazy<IGlobalFilterStore>(() => GlobalFilterStores.Create(new AppConfig().ConnectionStrings.SQL)),
            TimeSpan.FromSeconds(60));

        private readonly Lazy<IGlobalFilterStore> _store;
        private readonly TimeSpan _freshFor;
        private readonly Func<DateTime> _utcNow;
        private readonly object _sync = new object();

        private GlobalFilterState _current;
        private DateTime _currentLoadedUtc;
        private Task<GlobalFilterState> _loading;
        private int _generation;

        public CachedGlobalFilterProvider(IGlobalFilterStore store, TimeSpan freshFor, Func<DateTime> utcNow = null)
            : this(new Lazy<IGlobalFilterStore>(() => store ?? throw new ArgumentNullException(nameof(store))), freshFor, utcNow)
        {
        }

        private CachedGlobalFilterProvider(Lazy<IGlobalFilterStore> store, TimeSpan freshFor, Func<DateTime> utcNow = null)
        {
            _store = store;
            _freshFor = freshFor;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public IGlobalFilterStore Store => _store.Value;

        public async Task<GlobalFilterState> GetAsync(CancellationToken cancellationToken)
        {
            Task<GlobalFilterState> loading;
            lock (_sync)
            {
                if (_current != null && _utcNow() - _currentLoadedUtc < _freshFor) return _current;

                if (_loading == null)
                {
                    _loading = LoadAsync(_generation);
                }

                loading = _loading;
            }

            var completed = await Task.WhenAny(loading, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            if (completed != loading)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return await loading.ConfigureAwait(false);
        }

        public void Invalidate()
        {
            lock (_sync)
            {
                _current = null;
                _loading = null;
                _generation++;
            }
        }

        private async Task<GlobalFilterState> LoadAsync(int generation)
        {
            // Off the request's synchronisation context: the read is shared by every waiting request.
            await Task.Yield();

            GlobalFilterState state;
            try
            {
                var record = await Store.GetAsync(CancellationToken.None).ConfigureAwait(false);
                state = Parse(record);
            }
            catch
            {
                lock (_sync)
                {
                    if (generation == _generation) _loading = null;
                }

                throw;
            }

            lock (_sync)
            {
                if (generation == _generation)
                {
                    _current = state;
                    _currentLoadedUtc = _utcNow();
                    _loading = null;
                }
            }

            return state;
        }

        /// <summary>Parses a stored record, keeping a definition that cannot be read as an error rather than as no filter.</summary>
        internal static GlobalFilterState Parse(GlobalFilterRecord record)
        {
            if (!record.StorageAvailable || string.IsNullOrWhiteSpace(record.FilterJson))
            {
                return new GlobalFilterState(record, GlobalFilterDefinition.Empty, null);
            }

            try
            {
                return new GlobalFilterState(record, GlobalFilterCodec.Parse(record.FilterJson), null);
            }
            catch (UserFilterFormatException ex)
            {
                return new GlobalFilterState(record, null, ex.Message);
            }
        }
    }
}
