using Common.Entities;
using Common.Entities.Config;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WebJob.Office365ActivityImporter.Engine;
using WebJob.Office365ActivityImporter.Engine.Graph.Sections;

namespace UnitTests.FakeLoaderClasses
{
    /// <summary>
    /// A Graph import section that records whether it ran and answers however the test tells it to.
    /// Lets the <c>GraphImporter</c> orchestration loop be driven with zero SQL Server, Graph, Redis or
    /// Service Bus (issue #376).
    /// </summary>
    public class FakeGraphImportSection : IGraphImportSection
    {
        private FakeGraphImportSection(string name, string cadenceKey, int intervalHours)
        {
            Name = name;
            DisabledMessage = $"Skipping {name}";
            CadenceKey = cadenceKey;
            IntervalHours = intervalHours;
        }

        /// <summary>A section the orchestrator must cadence-gate.</summary>
        public static FakeGraphImportSection Gated(string name, string cadenceKey, int intervalHours)
            => new FakeGraphImportSection(name, cadenceKey, intervalHours);

        /// <summary>A section with no cadence key, which the orchestrator must run every cycle.</summary>
        public static FakeGraphImportSection Ungated(string name)
            => new FakeGraphImportSection(name, null, 0);

        public string Name { get; }
        public string DisabledMessage { get; set; }
        public string CadenceKey { get; }
        public int IntervalHours { get; }

        /// <summary>Whether the tenant has this import switched on. Defaults to on.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>What <see cref="RunAsync"/> reports. Ignored when <see cref="FailWith"/> is set.</summary>
        public bool Result { get; set; } = true;

        /// <summary>
        /// When set, <see cref="RunAsync"/> returns a FAULTED task rather than throwing synchronously - a
        /// synchronous throw would land in the caller's catch even if the caller forgot to await, so it
        /// could not tell a dropped await from a handled failure.
        /// </summary>
        public Exception FailWith { get; set; }

        /// <summary>Runs while the section body executes, for asserting ordering / interleaving.</summary>
        public Action OnRun { get; set; }

        public int RunCount { get; private set; }
        public bool WasRun => RunCount > 0;

        /// <summary>The <c>ImportTaskSettings</c> instance the orchestrator asked <see cref="IsEnabled"/> about.</summary>
        public ImportTaskSettings LastEnabledCheckArgument { get; private set; }

        public bool IsEnabled(ImportTaskSettings settings)
        {
            LastEnabledCheckArgument = settings;
            return Enabled;
        }

        public Task<bool> RunAsync()
        {
            RunCount++;
            OnRun?.Invoke();

            if (FailWith != null) return Task.FromException<bool>(FailWith);
            return Task.FromResult(Result);
        }
    }

    /// <summary>
    /// Hands the orchestrator a fixed list of sections and records how it was asked for them.
    /// </summary>
    public class FakeGraphImportSectionFactory : IGraphImportSectionFactory
    {
        private readonly IReadOnlyList<IGraphImportSection> _sections;

        public FakeGraphImportSectionFactory(params IGraphImportSection[] sections)
        {
            _sections = sections ?? new IGraphImportSection[0];
        }

        public int CreateSectionsCallCount { get; private set; }
        public AppConfig LastSettingsArgument { get; private set; }

        public IReadOnlyList<IGraphImportSection> CreateSections(AppConfig settings)
        {
            CreateSectionsCallCount++;
            LastSettingsArgument = settings;
            return _sections;
        }
    }

    /// <summary>
    /// <see cref="IImportLastRunStore"/> that records every read and write, so a test can assert not just
    /// the resulting state but that the orchestrator wrote (or did not write) at all.
    ///
    /// Set <see cref="ReadsAlwaysReturnNull"/> to reproduce the documented Redis fail-open contract: a
    /// <c>RedisImportLastRunStore</c> whose backing cache is unreachable returns null from a read and
    /// swallows a write, so the section runs rather than being skipped by a cache blip.
    /// </summary>
    public class RecordingImportLastRunStore : IImportLastRunStore
    {
        private readonly Dictionary<string, DateTime> _lastRuns = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public List<string> Reads { get; } = new List<string>();
        public List<KeyValuePair<string, DateTime>> Writes { get; } = new List<KeyValuePair<string, DateTime>>();
        public List<string> Clears { get; } = new List<string>();

        public bool ReadsAlwaysReturnNull { get; set; }
        public bool WritesAreSwallowed { get; set; }

        /// <summary>Seeds a last-run time without recording it as a write.</summary>
        public RecordingImportLastRunStore Seed(string key, DateTime whenUtc)
        {
            _lastRuns[key] = whenUtc.ToUniversalTime();
            return this;
        }

        public Task<DateTime?> GetLastRunUtc(string key)
        {
            Reads.Add(key);
            if (ReadsAlwaysReturnNull) return Task.FromResult((DateTime?)null);
            return Task.FromResult(_lastRuns.TryGetValue(key, out var dt) ? (DateTime?)dt : null);
        }

        public Task SetLastRunUtc(string key, DateTime whenUtc)
        {
            Writes.Add(new KeyValuePair<string, DateTime>(key, whenUtc));
            if (!WritesAreSwallowed) _lastRuns[key] = whenUtc.ToUniversalTime();
            return Task.CompletedTask;
        }

        public Task Clear(string key)
        {
            Clears.Add(key);
            _lastRuns.Remove(key);
            return Task.CompletedTask;
        }
    }
}
