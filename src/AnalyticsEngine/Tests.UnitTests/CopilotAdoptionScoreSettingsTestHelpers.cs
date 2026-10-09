extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Controllers;
using AnalyticsWeb::Web.AnalyticsWeb.Security;
using Common.Entities.CopilotAdoption;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using AdoptionCache = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisCache;
using AdoptionRunner = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionAnalysisRunner;
using RunTelemetry = Common.Entities.CopilotAdoption.ICopilotAdoptionRunTelemetry;
using SettingsProvider = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionScoreSettingsProvider;
using SettingsService = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.CopilotAdoptionSettingsService;
using SettingsSource = AnalyticsWeb::Web.AnalyticsWeb.Models.CopilotAdoption.ICopilotAdoptionScoreSettingsSource;

namespace Tests.UnitTests
{
    public partial class CopilotAdoptionScoreSettingsTests
    {
        private static CopilotAdoptionScoreSettings With(params Action<CopilotAdoptionScoreSettings>[] changes)
        {
            var settings = CopilotAdoptionScoreSettings.Defaults;
            foreach (var change in changes) change(settings);
            return settings;
        }

        private static CopilotAdoptionOptions Apply(CopilotAdoptionScoreSettings settings)
        {
            Assert.AreEqual(0, settings.Validate().Count, "The test's settings must be valid.");
            var options = CopilotAdoptionOptions.Default;
            settings.ApplyTo(options);
            return options;
        }

        private static LicensedUserUsageRow Row(long interactions, int activeDays, int apps) => new LicensedUserUsageRow
        {
            UserId = 1,
            UserPrincipalName = "person@contoso.com",
            Interactions = interactions,
            ActiveDays = activeDays,
            AppsUsed = apps,
            LastInteractionUtc = activeDays > 0 ? Now.AddDays(-1) : (DateTime?)null,
            FirstInteractionUtc = activeDays > 0 ? Now.AddDays(-90) : (DateTime?)null,
        };

        private static async Task<CopilotAdoptionScoreSettingsRejectedException> Rejected(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (CopilotAdoptionScoreSettingsRejectedException ex)
            {
                return ex;
            }

            Assert.Fail("Expected the settings to be rejected.");
            return null;
        }

        private static async Task Unavailable(Func<Task> action)
        {
            try
            {
                await action();
            }
            catch (CopilotAdoptionScoreSettingsUnavailableException)
            {
                return;
            }

            Assert.Fail("Expected the settings to be reported unavailable.");
        }

        private static CopilotAdoptionAnalysis Analysis(CopilotAdoptionEffectiveScoreSettings settings)
        {
            var options = CopilotAdoptionOptions.Default;
            settings.ApplyTo(options);
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;
            summary.Options = options;
            summary.GeneratedUtc = Now;
            summary.WindowDays = options.WindowDays;
            summary.FromUtc = Now.AddDays(-options.WindowDays);
            summary.ToUtc = Now;
            summary.DataSources.AuditAvailable = true;
            for (var i = 1; i <= 3; i++)
            {
                analysis.LicensedUsers.Add(CopilotAdoptionScoring.Score(Row(i * 10, i * 4, i), summary.FromUtc, Now, true, options));
            }

            summary.LicensedUsers = analysis.LicensedUsers.Count;
            new CopilotAdoptionService().FinaliseSummary(analysis);
            return analysis;
        }

        private static string WorkbookText(byte[] bytes)
        {
            var text = new StringBuilder();
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal)))
                {
                    using (var part = entry.Open())
                    {
                        foreach (var node in XDocument.Load(part).Descendants().Where(e => !e.HasElements))
                        {
                            text.AppendLine(node.Value);
                        }
                    }
                }
            }

            return text.ToString();
        }

        private static SettingsService Service(IKeyValueStore values, bool durable)
        {
            var store = new CopilotAdoptionScoreSettingsStore(values, durable);
            return new SettingsService(new SettingsProvider(() => store));
        }

        private static PortalTestHost SettingsHost(SettingsService service, IPrincipal principal, PortalAccessPolicy policy) =>
            new PortalTestHost(
                new[] { typeof(CopilotAdoptionSettingsAPIController) },
                principal,
                policy,
                _ => new CopilotAdoptionSettingsAPIController(() => service));

        private static HttpRequestMessage Post(string url, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Requested-With", "XMLHttpRequest");
            request.Headers.Add("Sec-Fetch-Site", "same-origin");
            return request;
        }

        private static string SaveBody(long? expectedVersion, int championScore) =>
            new JObject
            {
                ["expectedVersion"] = expectedVersion.HasValue ? (JToken)expectedVersion.Value : JValue.CreateNull(),
                ["settings"] = new JObject
                {
                    ["frequencyWeightPercent"] = 50,
                    ["depthWeightPercent"] = 30,
                    ["breadthWeightPercent"] = 20,
                    ["developingScore"] = 25,
                    ["establishedScore"] = 50,
                    ["championScore"] = championScore,
                },
            }.ToString();

        private static async Task<string> Code(HttpResponseMessage response) =>
            (string)JObject.Parse(await response.Content.ReadAsStringAsync())["code"];

        private sealed class FailingStore : IConditionalKeyValueStore
        {
            internal volatile bool Fail = true;

            public string Description => "synthetic failing store";

            public Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default) =>
                Fail ? Task.FromException<string>(new InvalidOperationException("Synthetic storage outage.")) : Task.FromResult<string>(null);

            public Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default) =>
                Fail ? Task.FromException<VersionedValue>(new InvalidOperationException("Synthetic storage outage.")) : Task.FromResult(new VersionedValue(null, null));

            public Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default) =>
                Fail ? Task.FromException<bool>(new InvalidOperationException("Synthetic storage outage.")) : Task.FromResult(true);

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
                Fail ? Task.FromException(new InvalidOperationException("Synthetic storage outage.")) : Task.CompletedTask;

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(false);
        }

        /// <summary>
        /// Counts reads, and can hold a number of versioned reads (<see cref="HoldNext"/>) until all of them have been made,
        /// so two writers are guaranteed to have read the same predecessor before either writes.
        /// </summary>
        private sealed class CountingStore : IConditionalKeyValueStore
        {
            private readonly InMemoryKeyValueStore _inner = new InMemoryKeyValueStore();
            private readonly TaskCompletionSource<bool> _allHeldReadsMade = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _reads;
            private int _versionedReads;
            private int _holdFrom;
            private int _holdReads;

            /// <summary>Holds the next <paramref name="reads"/> versioned reads until all of them have been made.</summary>
            internal void HoldNext(int reads)
            {
                _holdFrom = Volatile.Read(ref _versionedReads);
                Volatile.Write(ref _holdReads, reads);
            }

            internal int Reads => Volatile.Read(ref _reads);
            internal CancellationToken LastReadToken { get; private set; }
            internal readonly TaskCompletionSource<bool> ReadStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal void ReleaseHeldReads() => _allHeldReadsMade.TrySetResult(true);
            internal bool StallWrites;
            internal CancellationToken LastWriteToken { get; private set; }

            public string Description => _inner.Description;

            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _reads);
                await Task.Delay(20, cancellationToken);
                return await _inner.GetStringAsync(key, cancellationToken);
            }

            public async Task<VersionedValue> GetVersionedAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _reads);
                LastReadToken = cancellationToken;
                ReadStarted.TrySetResult(true);
                var read = await _inner.GetVersionedAsync(key, cancellationToken);
                var n = Interlocked.Increment(ref _versionedReads) - _holdFrom;
                var hold = Volatile.Read(ref _holdReads);
                if (n <= hold)
                {
                    if (n == hold) _allHeldReadsMade.TrySetResult(true);
                    await _allHeldReadsMade.Task;
                }
                return read;
            }

            public async Task<bool> TrySetStringAsync(string key, string value, string expectedVersionToken, CancellationToken cancellationToken = default)
            {
                LastWriteToken = cancellationToken;
                if (StallWrites) await Task.Delay(Timeout.Infinite, cancellationToken);
                return await _inner.TrySetStringAsync(key, value, expectedVersionToken, cancellationToken);
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default) =>
                _inner.SetStringAsync(key, value, timeToLive, cancellationToken);

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => _inner.ExistsAsync(key, cancellationToken);
        }

        private sealed class MutableSource : SettingsSource
        {
            internal CopilotAdoptionEffectiveScoreSettings Current = CopilotAdoptionEffectiveScoreSettings.Defaults;
            internal bool Fail;

            public CopilotAdoptionEffectiveScoreSettings LastKnown => Current;

            public Task<CopilotAdoptionEffectiveScoreSettings> GetAsync(CancellationToken cancellationToken = default) =>
                Fail
                    ? Task.FromException<CopilotAdoptionEffectiveScoreSettings>(new CopilotAdoptionScoreSettingsUnavailableException("Synthetic outage.", new InvalidOperationException()))
                    : Task.FromResult(Current);
        }

        private sealed class RecordingRunner : AdoptionRunner
        {
            internal readonly List<CopilotAdoptionEffectiveScoreSettings> Runs = new List<CopilotAdoptionEffectiveScoreSettings>();

            public Task<CopilotAdoptionAnalysis> RunAsync(int windowDays, DateTime? fromUtc, DateTime? toUtc, DateTime? toExclusiveUtc, bool usesExplicitDates, List<int> seatLicenceTypeIds, CopilotAdoptionEffectiveScoreSettings scoreSettings, RunTelemetry telemetry)
            {
                lock (Runs) Runs.Add(scoreSettings);
                return Task.FromResult(Analysis(scoreSettings));
            }
        }

        private sealed class DictionaryCache : AdoptionCache
        {
            private readonly Dictionary<string, CopilotAdoptionAnalysis> _entries = new Dictionary<string, CopilotAdoptionAnalysis>();
            internal int ReadCount;

            public bool TryGet(string key, out CopilotAdoptionAnalysis analysis)
            {
                Interlocked.Increment(ref ReadCount);
                lock (_entries) return _entries.TryGetValue(key, out analysis);
            }

            public void Set(string key, CopilotAdoptionAnalysis analysis, TimeSpan ttl)
            {
                lock (_entries) _entries[key] = analysis;
            }
        }
    }
}
