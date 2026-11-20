using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Common.Entities.State;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    [TestClass]
    [TestCategory("AzuriteBenchmark")]
    public class UsageReportCoverageAzuriteBenchmarkTests
    {
        private const string AccountName = "devstoreaccount1";
        private const string AccountKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
        private const int ColdSamples = 5;
        private const int WarmSamples = 7;
        private const int SeedDays = 180;
        private const int SeedEntities = SeedDays * 4;

        public TestContext TestContext { get; set; }

        [TestMethod]
        public async Task ReadAsync_UsesBoundedAzuritePointReads_For28And180Days()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUN_USAGE_REPORT_COVERAGE_BENCHMARK"),
                "true", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("Run Run-UsageReportCoverageBenchmark.ps1 to opt into this local-only benchmark.");
            }

            int port;
            if (!int.TryParse(Environment.GetEnvironmentVariable("USAGE_REPORT_COVERAGE_AZURITE_PORT"), out port)
                || port < 1 || port > 65535)
            {
                Assert.Fail("The opt-in harness must provide a valid task-local Azurite port.");
            }

            var endpoint = new Uri($"http://127.0.0.1:{port}/{AccountName}");
            Assert.IsTrue(endpoint.IsLoopback, "This benchmark must never connect beyond loopback.");
            var connectionString = $"DefaultEndpointsProtocol=http;AccountName={AccountName};AccountKey={AccountKey};TableEndpoint={endpoint};";
            var tableName = "CoverageBench" + Guid.NewGuid().ToString("N");
            var partition = "SyntheticCoverage" + Guid.NewGuid().ToString("N");
            var setupTable = new TableClient(connectionString, tableName);
            var tableCreated = false;

            try
            {
                await setupTable.CreateAsync().ConfigureAwait(false);
                tableCreated = true;

                var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var seedStore = new UsageReportCoverageStore(new AzureTableKeyValueStore(setupTable, partition), true);
                foreach (var source in UsageReportCoverageStore.Sources)
                {
                    for (var offset = 0; offset < SeedDays; offset++)
                    {
                        var day = start.AddDays(offset);
                        await seedStore.CompleteDayAsync(source, day, true, true, true, day.AddDays(10), 3)
                            .ConfigureAwait(false);
                    }
                }

                var actualEntities = setupTable.Query<TableEntity>(
                    TableClient.CreateQueryFilter($"PartitionKey eq {partition}")).Count();
                Assert.AreEqual(SeedEntities, actualEntities, "Only the synthetic 4-source × 180-day ledger is seeded.");

                TestContext.WriteLine("Service: task-local Azurite Table emulator; endpoint is loopback only.");
                TestContext.WriteLine($"Seed: {actualEntities} synthetic ledger entities; no people or fact rows.");
                foreach (var days in new[] { 28, 180 })
                {
                    await MeasureWindowAsync(connectionString, tableName, partition, start, days)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                if (tableCreated)
                {
                    Assert.IsTrue(tableName.StartsWith("CoverageBench", StringComparison.Ordinal));
                    Assert.IsTrue(endpoint.IsLoopback);
                    await setupTable.DeleteAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task MeasureWindowAsync(string connectionString, string tableName, string partition,
            DateTime start, int days)
        {
            var through = start.AddDays(days - 1);
            var expectedReads = UsageReportCoverageStore.Sources.Length * days;
            var cold = new List<Sample>();
            for (var i = 0; i < ColdSamples; i++)
            {
                using (var client = new ObservedClient(connectionString, tableName, partition))
                {
                    cold.Add(await MeasureAsync(client, start, through, days, expectedReads).ConfigureAwait(false));
                }
            }

            var warm = new List<Sample>();
            using (var client = new ObservedClient(connectionString, tableName, partition))
            {
                await client.Coverage.ReadAsync(start, through).ConfigureAwait(false);
                for (var i = 0; i < WarmSamples; i++)
                {
                    warm.Add(await MeasureAsync(client, start, through, days, expectedReads).ConfigureAwait(false));
                }
            }

            TestContext.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0}-day: {1} logical reads and HTTP GETs per run; cold-client median {2:F2} ms ({3} samples, peak {4} HTTP in flight); " +
                "warm median {5:F2} ms ({6} samples, peak {7} HTTP in flight). Cold runs use a fresh SDK client; Azurite itself remains warm.",
                days, expectedReads, Median(cold), cold.Count, cold.Max(s => s.PeakHttpRequests),
                Median(warm), warm.Count, warm.Max(s => s.PeakHttpRequests)));
            TestContext.WriteLine("  Cold ms: " + string.Join(", ", cold.Select(s => s.ElapsedMilliseconds.ToString("F2", CultureInfo.InvariantCulture))));
            TestContext.WriteLine("  Warm ms: " + string.Join(", ", warm.Select(s => s.ElapsedMilliseconds.ToString("F2", CultureInfo.InvariantCulture))));
        }

        private static async Task<Sample> MeasureAsync(ObservedClient client, DateTime start, DateTime through,
            int expectedDays, int expectedReads)
        {
            client.Reset();
            var timer = Stopwatch.StartNew();
            var result = await client.Coverage.ReadAsync(start, through).ConfigureAwait(false);
            timer.Stop();

            Assert.AreEqual(UsageReportCoverageStore.Sources.Length, result.Count);
            Assert.IsTrue(result.All(coverage => coverage.IsComplete && coverage.ExpectedDays == expectedDays),
                "Every source should read only the pre-seeded, successfully empty days.");
            Assert.AreEqual(expectedReads, client.Store.ReadCount, "IKeyValueStore calls are counted at the production store boundary.");
            Assert.AreEqual(expectedReads, client.Http.GetRequests,
                "Every successful AzureTableKeyValueStore GetStringAsync maps to one Table-service point-read GET.");
            Assert.IsTrue(client.Store.PeakReads <= UsageReportCoverageStore.ReadConcurrency);
            Assert.IsTrue(client.Http.PeakRequests <= UsageReportCoverageStore.ReadConcurrency);

            return new Sample
            {
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
                PeakHttpRequests = client.Http.PeakRequests,
            };
        }

        private static double Median(IEnumerable<Sample> samples)
        {
            var ordered = samples.Select(sample => sample.ElapsedMilliseconds).OrderBy(value => value).ToArray();
            return ordered[ordered.Length / 2];
        }

        private sealed class Sample
        {
            public double ElapsedMilliseconds { get; set; }
            public int PeakHttpRequests { get; set; }
        }

        private sealed class ObservedClient : IDisposable
        {
            private readonly HttpClient _httpClient;

            public ObservedClient(string connectionString, string tableName, string partition)
            {
                Http = new RequestObserver();
                _httpClient = new HttpClient(Http) { Timeout = Timeout.InfiniteTimeSpan };
                var options = new TableClientOptions
                {
                    Transport = new HttpClientTransport(_httpClient),
                };
                var table = new TableClient(connectionString, tableName, options);
                Store = new ReadCountingStore(new AzureTableKeyValueStore(table, partition));
                Coverage = new UsageReportCoverageStore(Store, true);
            }

            public RequestObserver Http { get; }
            public ReadCountingStore Store { get; }
            public UsageReportCoverageStore Coverage { get; }

            public void Reset()
            {
                Store.Reset();
                Http.Reset();
            }

            public void Dispose() => _httpClient.Dispose();
        }

        private sealed class RequestObserver : DelegatingHandler
        {
            private int _getRequests;
            private int _activeRequests;
            private int _peakRequests;

            public RequestObserver()
            {
                InnerHandler = new HttpClientHandler();
            }

            public int GetRequests => Volatile.Read(ref _getRequests);
            public int PeakRequests => Volatile.Read(ref _peakRequests);

            public void Reset()
            {
                Interlocked.Exchange(ref _getRequests, 0);
                Interlocked.Exchange(ref _activeRequests, 0);
                Interlocked.Exchange(ref _peakRequests, 0);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                if (request.Method != HttpMethod.Get)
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }

                Interlocked.Increment(ref _getRequests);
                var active = Interlocked.Increment(ref _activeRequests);
                UpdatePeak(active);
                try
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeRequests);
                }
            }

            private void UpdatePeak(int active)
            {
                var peak = Volatile.Read(ref _peakRequests);
                while (active > peak)
                {
                    var previous = Interlocked.CompareExchange(ref _peakRequests, active, peak);
                    if (previous == peak) return;
                    peak = previous;
                }
            }
        }

        private sealed class ReadCountingStore : IKeyValueStore
        {
            private readonly IKeyValueStore _inner;
            private int _readCount;
            private int _activeReads;
            private int _peakReads;

            public ReadCountingStore(IKeyValueStore inner) => _inner = inner;

            public string Description => _inner.Description;
            public int ReadCount => Volatile.Read(ref _readCount);
            public int PeakReads => Volatile.Read(ref _peakReads);

            public void Reset()
            {
                Interlocked.Exchange(ref _readCount, 0);
                Interlocked.Exchange(ref _activeReads, 0);
                Interlocked.Exchange(ref _peakReads, 0);
            }

            public async Task<string> GetStringAsync(string key, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _readCount);
                var active = Interlocked.Increment(ref _activeReads);
                UpdatePeak(active);
                try
                {
                    return await _inner.GetStringAsync(key, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeReads);
                }
            }

            public Task SetStringAsync(string key, string value, TimeSpan? timeToLive = null,
                CancellationToken cancellationToken = default)
                => _inner.SetStringAsync(key, value, timeToLive, cancellationToken);

            public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
                => _inner.DeleteAsync(key, cancellationToken);

            public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
                => _inner.ExistsAsync(key, cancellationToken);

            private void UpdatePeak(int active)
            {
                var peak = Volatile.Read(ref _peakReads);
                while (active > peak)
                {
                    var previous = Interlocked.CompareExchange(ref _peakReads, active, peak);
                    if (previous == peak) return;
                    peak = previous;
                }
            }
        }
    }
}
