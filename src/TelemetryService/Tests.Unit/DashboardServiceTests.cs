using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UsageReporting;
using Web.Dashboard;

namespace Tests.Unit;

[TestClass]
public class DashboardServiceAggregateTests
{
    // Fixed "now" so the freshness buckets are deterministic regardless of when the suite runs.
    private static readonly DateTime Now = new(2026, 08, 19, 12, 00, 00, DateTimeKind.Utc);

    [TestMethod]
    public void Aggregate_NoClients_ReturnsEmptyStatsNotNulls()
    {
        var stats = DashboardService.Aggregate(new List<AnonUsageStatsModel>(), Now);

        Assert.AreEqual(0, stats.ClientCount);
        Assert.AreEqual(0, stats.TotalRows);
        Assert.IsEmpty(stats.TableTotals);
        // The React side renders unconditionally, so these must never be null.
        Assert.IsNotNull(stats.Freshness);
        Assert.IsNotNull(stats.SizeDistribution);
        Assert.IsNotNull(stats.Versions);
        Assert.IsNotNull(stats.ImportFeatures);
        Assert.IsNotNull(stats.SchemaTotals);
    }

    [TestMethod]
    public void Aggregate_NullList_DoesNotThrow()
    {
        var stats = DashboardService.Aggregate(null!, Now);
        Assert.AreEqual(0, stats.ClientCount);
    }

    [TestMethod]
    public void Aggregate_SkipsNullClientsAndNullTables()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            null!,
            TestData.Client("a", Now).WithTables(("dbo", "t1", 10, 1m)),
            new AnonUsageStatsModel { AnonClientId = "b", Generated = Now, TableStats = null },
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(2, stats.ClientCount, "Null entries must not be counted as clients.");
        Assert.AreEqual(10, stats.TotalRows);
    }

    [TestMethod]
    public void Aggregate_SumsRowsAndSizeAcrossClients()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "t1", 100, 10m), ("dbo", "t2", 50, 5m)),
            TestData.Client("b", Now).WithTables(("dbo", "t1", 200, 20m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(350, stats.TotalRows);
        Assert.AreEqual(35m, stats.TotalSpaceMB);
        Assert.AreEqual(2, stats.DistinctTableCount);

        var t1 = stats.TableTotals.Single(t => t.TableName == "t1");
        Assert.AreEqual(300, t1.Rows);
        Assert.AreEqual(2, t1.ClientCount, "t1 is reported by both clients.");
    }

    [TestMethod]
    public void Aggregate_KeepsSameTableNameInDifferentSchemasSeparate()
    {
        // The whole point of SchemaName: an app table and a profiling table can share a name, and
        // merging them would silently overstate both.
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "shared", 100, 10m), ("profiling", "shared", 7, 1m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.HasCount(2, stats.TableTotals);
        Assert.AreEqual(100, stats.TableTotals.Single(t => t.DisplayName == "dbo.shared").Rows);
        Assert.AreEqual(7, stats.TableTotals.Single(t => t.DisplayName == "profiling.shared").Rows);
    }

    [TestMethod]
    public void Aggregate_TableWithoutSchema_UsesBareNameAndUnknownSchemaBucket()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables((null, "legacy", 5, 1m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        var table = stats.TableTotals.Single();
        Assert.AreEqual("legacy", table.DisplayName);
        Assert.IsNull(table.SchemaName);
        Assert.AreEqual("(unknown)", stats.SchemaTotals.Single().SchemaName);
    }

    [TestMethod]
    public void Aggregate_SchemaTotals_RollUpPerSchema()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "t1", 100, 10m), ("dbo", "t2", 50, 5m), ("profiling", "p1", 9, 1m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        var dbo = stats.SchemaTotals.Single(s => s.SchemaName == "dbo");
        Assert.AreEqual(150, dbo.Rows);
        Assert.AreEqual(15m, dbo.TotalSpaceMB);
        Assert.AreEqual(2, dbo.TableCount);
    }

    [TestMethod]
    public void Aggregate_TableTotals_AreSortedByRowsDescending()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "small", 1, 1m), ("dbo", "big", 999, 1m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual("big", stats.TableTotals.First().TableName);
    }

    [TestMethod]
    public void Aggregate_LastUpdated_IsTheMostRecentReport()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now.AddDays(-3)),
            TestData.Client("b", Now.AddHours(-1)),
            TestData.Client("c", Now.AddDays(-10)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(Now.AddHours(-1), stats.LastUpdated);
    }

    #region Freshness

    [TestMethod]
    public void Aggregate_Freshness_BucketsByReportAge()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("fresh", Now.AddHours(-2)),
            TestData.Client("recent", Now.AddDays(-3)),
            TestData.Client("older", Now.AddDays(-20)),
            TestData.Client("stale", Now.AddDays(-90)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(1, stats.Freshness.Last24Hours);
        Assert.AreEqual(1, stats.Freshness.Last7Days);
        Assert.AreEqual(1, stats.Freshness.Last30Days);
        Assert.AreEqual(1, stats.Freshness.Stale);
    }

    [TestMethod]
    public void Aggregate_Freshness_TreatsMissingTimestampAsStale()
    {
        var client = TestData.Client("a", Now);
        client.Generated = null;

        var stats = DashboardService.Aggregate(new List<AnonUsageStatsModel> { client }, Now);

        Assert.AreEqual(1, stats.Freshness.Stale);
    }

    [TestMethod]
    public void Aggregate_Freshness_BucketsAlwaysTotalTheClientCount()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now.AddMinutes(-5)),
            TestData.Client("b", Now.AddDays(-8)),
            TestData.Client("c", Now.AddDays(-31)),
        };

        var stats = DashboardService.Aggregate(clients, Now);
        var f = stats.Freshness;

        Assert.AreEqual(stats.ClientCount, f.Last24Hours + f.Last7Days + f.Last30Days + f.Stale);
    }

    #endregion

    #region Version adoption

    [TestMethod]
    public void Aggregate_Versions_CountsClientsPerBuildAndKeepsUnknowns()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now, build: "1756"),
            TestData.Client("b", Now, build: "1756"),
            TestData.Client("c", Now, build: "1732"),
            TestData.Client("d", Now, build: null),
            TestData.Client("e", Now, build: "   "),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual("1756", stats.Versions.First().BuildVersionLabel, "Most common build first.");
        Assert.AreEqual(2, stats.Versions.First().ClientCount);
        Assert.AreEqual(2, stats.Versions.Single(v => v.BuildVersionLabel == "(unknown)").ClientCount,
            "Null and whitespace labels both fall into (unknown).");
        Assert.AreEqual(stats.ClientCount, stats.Versions.Sum(v => v.ClientCount),
            "Version counts must reconcile against the client total.");
    }

    [TestMethod]
    public void Aggregate_Versions_TracksLastSeen()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now.AddDays(-5), build: "1756"),
            TestData.Client("b", Now.AddDays(-1), build: "1756"),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(Now.AddDays(-1), stats.Versions.Single().LastSeen);
    }

    #endregion

    #region Import feature adoption

    [TestMethod]
    public void Aggregate_ImportFeatures_CountsEnabledAndDisabledPerToggle()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now, imports: "GraphUsersMetadata=True;GraphUserApps=False"),
            TestData.Client("b", Now, imports: "GraphUsersMetadata=True;GraphUserApps=True"),
            TestData.Client("c", Now, imports: "GraphUsersMetadata=False"),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        var meta = stats.ImportFeatures.Single(f => f.Name == "GraphUsersMetadata");
        Assert.AreEqual(2, meta.EnabledCount);
        Assert.AreEqual(1, meta.DisabledCount);
        Assert.AreEqual(3, meta.ReportingClients);

        var apps = stats.ImportFeatures.Single(f => f.Name == "GraphUserApps");
        Assert.AreEqual(1, apps.EnabledCount);
        Assert.AreEqual(2, apps.ReportingClients,
            "Only the clients that mentioned the toggle count towards its denominator.");
    }

    [TestMethod]
    public void Aggregate_ImportFeatures_IgnoresClientsThatReportNothing()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now, imports: "GraphUsersMetadata=True"),
            TestData.Client("b", Now, imports: null),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(1, stats.ImportFeatures.Single().ReportingClients,
            "A client that never reported the toggle must not be counted as having it off.");
    }

    #endregion

    #region Size distribution

    [TestMethod]
    public void Aggregate_SizeDistribution_ComputesMedianAverageAndMax()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "t", 10, 1m)),
            TestData.Client("b", Now).WithTables(("dbo", "t", 20, 2m)),
            TestData.Client("c", Now).WithTables(("dbo", "t", 300, 30m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);
        var sd = stats.SizeDistribution;

        Assert.AreEqual(20, sd.MedianRowsPerClient, "Median of 10/20/300 is 20.");
        Assert.AreEqual(110, sd.AvgRowsPerClient, "Average of 10/20/300 is 110.");
        Assert.AreEqual(300, sd.MaxRowsPerClient);
        Assert.AreEqual(30m, sd.MaxSpaceMBPerClient);
        Assert.AreEqual(1, sd.AvgTablesPerClient);
    }

    [TestMethod]
    public void Aggregate_SizeDistribution_MedianOfEvenCountAveragesTheMiddleTwo()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now).WithTables(("dbo", "t", 10, 1m)),
            TestData.Client("b", Now).WithTables(("dbo", "t", 20, 2m)),
            TestData.Client("c", Now).WithTables(("dbo", "t", 30, 3m)),
            TestData.Client("d", Now).WithTables(("dbo", "t", 40, 4m)),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(25, stats.SizeDistribution.MedianRowsPerClient);
        Assert.AreEqual(2.5m, stats.SizeDistribution.MedianSpaceMBPerClient);
    }

    #endregion

    #region Azure AI totals

    [TestMethod]
    public void Aggregate_AiTotals_OnlyCountClientsThatReportThem()
    {
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("a", Now, aiDataPoints: 100),
            TestData.Client("b", Now, aiDataPoints: 50),
            TestData.Client("c", Now, aiDataPoints: null),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(150, stats.AiDataPointsTotal);
        Assert.AreEqual(2, stats.ClientsReportingAi);
        Assert.AreEqual(3, stats.ClientCount);
    }

    #endregion
}

[TestClass]
public class ParseSettingsTests
{
    [TestMethod]
    public void ParsesStandardSettingsString()
    {
        var parsed = DashboardService.ParseSettings("GraphUsersMetadata=True;GraphUserApps=False");

        Assert.HasCount(2, parsed);
        Assert.IsTrue(parsed["GraphUsersMetadata"]);
        Assert.IsFalse(parsed["GraphUserApps"]);
    }

    [TestMethod]
    public void IsCaseInsensitiveOnNameAndValue()
    {
        var parsed = DashboardService.ParseSettings("graphusersmetadata=TRUE");
        Assert.IsTrue(parsed["GraphUsersMetadata"]);
    }

    [TestMethod]
    public void ToleratesTrailingSeparatorsAndWhitespace()
    {
        var parsed = DashboardService.ParseSettings(" A=True ; B=False ;;");

        Assert.HasCount(2, parsed);
        Assert.IsTrue(parsed["A"]);
        Assert.IsFalse(parsed["B"]);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void ReturnsEmptyForMissingInput(string? input)
    {
        Assert.IsEmpty(DashboardService.ParseSettings(input));
    }

    [TestMethod]
    public void SkipsMalformedEntriesInsteadOfThrowing()
    {
        // The string is produced by whatever build the client happens to be running, so it must
        // never be able to take the dashboard down.
        var parsed = DashboardService.ParseSettings("Good=True;NoEquals;=True;Bad=NotABool;Another=False");

        Assert.HasCount(2, parsed);
        Assert.IsTrue(parsed["Good"]);
        Assert.IsFalse(parsed["Another"]);
    }

    [TestMethod]
    public void HandlesValueContainingEquals()
    {
        // Split on the first '=' only, so an odd value doesn't shift the name.
        var parsed = DashboardService.ParseSettings("Weird=True=False");
        Assert.IsEmpty(parsed, "'True=False' is not a bool, so the entry is skipped, not misread.");
    }
}

[TestClass]
public class DashboardServiceCachingTests
{
    private static DashboardService Build(FakeTelemetryStore store, TimeSpan cache, int maxItems = 5000) =>
        new(store, NullLogger<DashboardService>.Instance, new MemoryCache(new MemoryCacheOptions()), maxItems, cache);

    [TestMethod]
    public async Task StatsAndClients_ShareASingleStoreRead_WhenCachingEnabled()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow).WithTables(("dbo", "t", 1, 1m)));
        var service = Build(store, TimeSpan.FromMinutes(1));

        await service.GetStatsAsync();
        await service.GetClientsAsync();

        Assert.AreEqual(1, store.LoadAllCallCount, "The second call must be served from cache.");
    }

    [TestMethod]
    public async Task CachingDisabled_HitsTheStoreEveryTime()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow));
        var service = Build(store, TimeSpan.Zero);

        await service.GetStatsAsync();
        await service.GetStatsAsync();

        Assert.AreEqual(2, store.LoadAllCallCount);
    }

    [TestMethod]
    public async Task PassesTheConfiguredMaxItemsToTheStore()
    {
        var store = new FakeTelemetryStore();
        var service = Build(store, TimeSpan.Zero, maxItems: 42);

        await service.GetStatsAsync();

        Assert.AreEqual(42, store.LastMaxItems);
    }

    [TestMethod]
    public async Task GetClients_ReturnsMostRecentlyReportedFirst()
    {
        var now = DateTime.UtcNow;
        var store = new FakeTelemetryStore();
        store.Seed(
            TestData.Client("old", now.AddDays(-5)),
            TestData.Client("new", now),
            TestData.Client("mid", now.AddDays(-1)));
        var service = Build(store, TimeSpan.Zero);

        var clients = await service.GetClientsAsync();

        CollectionAssert.AreEqual(new[] { "new", "mid", "old" }, clients.Select(c => c.AnonClientId).ToArray());
    }

    [TestMethod]
    public async Task GetClients_ExposesEnabledImportsParsedFromTheSettingsString()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow, imports: "Zebra=True;Alpha=True;Off=False"));
        var service = Build(store, TimeSpan.Zero);

        var client = (await service.GetClientsAsync()).Single();

        CollectionAssert.AreEqual(new[] { "Alpha", "Zebra" }, client.EnabledImports.ToArray(),
            "Only enabled toggles, sorted for stable display.");
    }

    [TestMethod]
    public async Task GetClients_SummarisesRowsSizeAndTableCount()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("a", DateTime.UtcNow).WithTables(("dbo", "t1", 10, 1m), ("dbo", "t2", 5, 0.5m)));
        var service = Build(store, TimeSpan.Zero);

        var client = (await service.GetClientsAsync()).Single();

        Assert.AreEqual(15, client.Rows);
        Assert.AreEqual(1.5m, client.TotalSpaceMB);
        Assert.AreEqual(2, client.TableCount);
    }
}
