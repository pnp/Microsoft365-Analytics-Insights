using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using UsageReporting;
using Web.Dashboard;

namespace Tests.Unit;

/// <summary>
/// Aggregation of the anonymised adoption block, and the maintainer annotations that put a name to an
/// otherwise anonymous client.
/// </summary>
[TestClass]
public class DashboardAdoptionTests
{
    private static readonly DateTime Now = new(2026, 08, 19, 12, 00, 00, DateTimeKind.Utc);

    #region Adoption aggregation

    [TestMethod]
    public void Aggregate_NoAdoptionBlocks_ReturnsEmptyInsightsNotNull()
    {
        var clients = new List<AnonUsageStatsModel> { TestData.Client("a", Now) };

        var stats = DashboardService.Aggregate(clients, Now);

        // The React side renders unconditionally.
        Assert.IsNotNull(stats.Adoption);
        Assert.AreEqual(0, stats.Adoption.ClientsReporting);
        Assert.IsNotNull(stats.Adoption.Skus);
        Assert.IsNotNull(stats.Adoption.Coverage);
        Assert.IsNotNull(stats.Adoption.DataSources);
        Assert.IsNotNull(stats.Adoption.Freshness);
    }

    [TestMethod]
    public void Aggregate_OnlySomeClientsReportAdoption()
    {
        // The normal case for a long time: older builds send nothing at all.
        var clients = new List<AnonUsageStatsModel>
        {
            TestData.Client("old-build", Now),
            WithAdoption(TestData.Client("new-build", Now), licensed: 1000, active: 500, rate: 50),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(2, stats.ClientCount);
        Assert.AreEqual(1, stats.Adoption.ClientsReporting);
        Assert.AreEqual(1, stats.Adoption.ClientsWithCopilotFigures);
        Assert.AreEqual(1000, stats.Adoption.TotalLicensedUsers);
    }

    [TestMethod]
    public void Aggregate_MedianRateIsOfTheClientsOwnRatesNotOfTheTotals()
    {
        // The distinction that matters: one huge tenant must not decide the headline figure. Here the
        // pooled rate would be ~10% (10,100 active of 100,200 seats) but the median client rate is 50%.
        var clients = new List<AnonUsageStatsModel>
        {
            WithAdoption(TestData.Client("small-a", Now), licensed: 100, active: 90, rate: 90),
            WithAdoption(TestData.Client("small-b", Now), licensed: 100, active: 10, rate: 10),
            WithAdoption(TestData.Client("huge", Now), licensed: 100_000, active: 10_000, rate: 50),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(50, stats.Adoption.MedianAdoptionRatePct);

        // Linearly interpolated over [10, 50, 90], so the quartiles land between the samples rather
        // than on them. They still do the job they exist for: showing that the install base is spread
        // wide rather than clustered around the median.
        Assert.AreEqual(30, stats.Adoption.LowerQuartileAdoptionRatePct);
        Assert.AreEqual(70, stats.Adoption.UpperQuartileAdoptionRatePct);
    }

    [TestMethod]
    public void Aggregate_SuppressedClientsCountSeparatelyAndContributeNoFigures()
    {
        var suppressed = TestData.Client("tiny", Now);
        suppressed.Adoption = new AnonAdoptionStats
        {
            GeneratedUtc = Now,
            Suppressed = true,
            SuppressionReason = AnonStatsPrivacy.SuppressionReasons.BelowMinimumPopulation,
        };

        var clients = new List<AnonUsageStatsModel>
        {
            suppressed,
            WithAdoption(TestData.Client("big", Now), licensed: 500, active: 250, rate: 50),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        Assert.AreEqual(2, stats.Adoption.ClientsReporting);
        Assert.AreEqual(1, stats.Adoption.ClientsSuppressed);
        Assert.AreEqual(1, stats.Adoption.ClientsWithCopilotFigures);
        Assert.AreEqual(500, stats.Adoption.TotalLicensedUsers, "A suppressed client must add nothing to the totals.");
    }

    [TestMethod]
    public void Aggregate_SuppressedClientStillContributesItsLicenceEstate()
    {
        var suppressed = TestData.Client("tiny", Now);
        suppressed.Adoption = new AnonAdoptionStats
        {
            GeneratedUtc = Now,
            Suppressed = true,
            Licences = new AnonLicenceAdoptionStats
            {
                Skus = new List<AnonLicenceSkuStats>
                {
                    new() { SkuPartNumber = "ENTERPRISEPACK", AssignedUsers = 300 },
                },
            },
        };

        var stats = DashboardService.Aggregate(new List<AnonUsageStatsModel> { suppressed }, Now);

        Assert.AreEqual(1, stats.Adoption.Skus.Count);
        Assert.AreEqual(300, stats.Adoption.Skus[0].AssignedUsers);
    }

    [TestMethod]
    public void Aggregate_SkuPopularityCountsClientsAndSeats()
    {
        var a = WithAdoption(TestData.Client("a", Now), licensed: 100, active: 50, rate: 50);
        a.Adoption!.Licences = Licences(("ENTERPRISEPACK", 500), ("Microsoft_365_Copilot", 100));

        var b = WithAdoption(TestData.Client("b", Now), licensed: 200, active: 100, rate: 50);
        b.Adoption!.Licences = Licences(("ENTERPRISEPACK", 800));

        var stats = DashboardService.Aggregate(new List<AnonUsageStatsModel> { a, b }, Now);

        var e3 = stats.Adoption.Skus.Single(s => s.SkuPartNumber == "ENTERPRISEPACK");
        Assert.AreEqual(2, e3.ClientCount);
        Assert.AreEqual(1300, e3.AssignedUsers);

        Assert.AreEqual("ENTERPRISEPACK", stats.Adoption.Skus[0].SkuPartNumber, "Most widely held first.");
    }

    [TestMethod]
    public void Aggregate_AdoptionFreshnessIsSeparateFromReportFreshness()
    {
        // The whole point of the block's own timestamp: the report uploaded today, but the analysis
        // behind it ran nine days ago. Reading the report date as the analysis date would overstate it.
        var client = WithAdoption(TestData.Client("a", Now), licensed: 100, active: 50, rate: 50);
        client.Adoption!.GeneratedUtc = Now.AddDays(-9);

        var stats = DashboardService.Aggregate(new List<AnonUsageStatsModel> { client }, Now);

        Assert.AreEqual(1, stats.Freshness.Last24Hours, "The report itself is fresh.");
        Assert.AreEqual(1, stats.Adoption.Freshness.Last30Days, "The analysis behind it is nine days old.");
        Assert.AreEqual(0, stats.Adoption.Freshness.Last24Hours);
    }

    [TestMethod]
    public void Aggregate_DataSourcesAreCountedOverReportingClientsOnly()
    {
        var withAudit = WithAdoption(TestData.Client("a", Now), licensed: 100, active: 50, rate: 50);
        withAudit.Adoption!.Copilot!.AuditAvailable = true;

        var withoutAudit = WithAdoption(TestData.Client("b", Now), licensed: 100, active: 50, rate: 50);
        withoutAudit.Adoption!.Copilot!.AuditAvailable = false;

        var clients = new List<AnonUsageStatsModel>
        {
            withAudit,
            withoutAudit,
            TestData.Client("no-adoption-block", Now),
        };

        var stats = DashboardService.Aggregate(clients, Now);

        var audit = stats.Adoption.DataSources.Single(d => d.Name == "Copilot audit");
        Assert.AreEqual(1, audit.EnabledCount);
        Assert.AreEqual(1, audit.DisabledCount);
        Assert.AreEqual(2, audit.ReportingClients, "A client with no block at all must not be counted as 'off'.");
    }

    [TestMethod]
    public void Percentile_SingleValueAndEmptyAreSafe()
    {
        Assert.AreEqual(0, DashboardService.Percentile(new List<double>(), 0.5));
        Assert.AreEqual(42, DashboardService.Percentile(new List<double> { 42 }, 0.5));
        Assert.AreEqual(2.5, DashboardService.Percentile(new List<double> { 1, 2, 3, 4 }, 0.5));
    }

    #endregion

    #region Annotations

    [TestMethod]
    public async Task GetClients_JoinsTheMaintainerAnnotation()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("client-1", Now));

        var annotations = new FakeClientAnnotationStore();
        annotations.Seed(new ClientAnnotation
        {
            id = "client-1",
            DisplayName = "Contoso Ltd",
            Notes = "Asked to be identified after raising a support case.",
        });

        var clients = await BuildService(store, annotations).GetClientsAsync();

        Assert.AreEqual("Contoso Ltd", clients[0].AnnotationDisplayName);
        Assert.IsNotNull(clients[0].AnnotationNotes);
    }

    [TestMethod]
    public async Task GetClients_UnannotatedClientStaysAnonymous()
    {
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("client-1", Now));

        var clients = await BuildService(store, new FakeClientAnnotationStore()).GetClientsAsync();

        Assert.IsNull(clients[0].AnnotationDisplayName);
    }

    [TestMethod]
    public async Task GetClients_AnnotationStoreFailureDoesNotTakeTheClientListDown()
    {
        // An annotation is a convenience label. Losing it must not lose the dashboard.
        var store = new FakeTelemetryStore();
        store.Seed(TestData.Client("client-1", Now));

        var annotations = new FakeClientAnnotationStore { ThrowOnLoad = true };

        var clients = await BuildService(store, annotations).GetClientsAsync();

        Assert.AreEqual(1, clients.Count);
        Assert.IsNull(clients[0].AnnotationDisplayName);
    }

    [TestMethod]
    public async Task GetClients_SurfacesTheAdoptionSummaryPerClient()
    {
        var store = new FakeTelemetryStore();
        store.Seed(WithAdoption(TestData.Client("client-1", Now), licensed: 900, active: 450, rate: 50));

        var clients = await BuildService(store, new FakeClientAnnotationStore()).GetClientsAsync();

        Assert.AreEqual(900, clients[0].CopilotLicensedUsers);
        Assert.AreEqual(450, clients[0].CopilotActiveUsers);
        Assert.AreEqual(50, clients[0].CopilotAdoptionRatePct);
        Assert.IsFalse(clients[0].AdoptionSuppressed);
        Assert.IsNotNull(clients[0].AdoptionGeneratedUtc);
    }

    #endregion

    #region Helpers

    private static DashboardService BuildService(FakeTelemetryStore store, FakeClientAnnotationStore annotations) =>
        new(store, NullLogger<DashboardService>.Instance, new MemoryCache(new MemoryCacheOptions()),
            5000, TimeSpan.Zero, annotations);

    private static AnonUsageStatsModel WithAdoption(
        AnonUsageStatsModel model, long licensed, long active, double rate)
    {
        model.Adoption = new AnonAdoptionStats
        {
            GeneratedUtc = model.Generated,
            WindowDays = 28,
            Copilot = new AnonCopilotAdoptionStats
            {
                LicensedUsers = licensed,
                ActiveUsers = active,
                AdoptionRatePct = rate,
                HabitRatePct = rate / 2,
            },
        };
        return model;
    }

    private static AnonLicenceAdoptionStats Licences(params (string Sku, long Users)[] skus)
    {
        return new AnonLicenceAdoptionStats
        {
            Skus = skus
                .Select(s => new AnonLicenceSkuStats { SkuPartNumber = s.Sku, AssignedUsers = s.Users })
                .ToList(),
        };
    }

    #endregion
}
