using UsageReporting;

namespace Tests.Unit;

/// <summary>
/// In-memory stand-ins for the Cosmos-backed telemetry store, so the services under test can be
/// exercised without Azure. Deliberately hand-rolled rather than mocked: the interfaces are tiny
/// and the fakes double as assertion helpers (SavedModels, LoadCallCount).
/// </summary>
internal sealed class FakeTelemetryStore : ITelemetrySaveAdaptor, ITelemetryQueryAdaptor
{
    private readonly List<AnonUsageStatsModel> _current = new();

    public List<AnonUsageStatsModel> SavedModels { get; } = new();
    public int LoadAllCallCount { get; private set; }
    public int? LastMaxItems { get; private set; }

    public void Seed(params AnonUsageStatsModel[] models) => _current.AddRange(models);

    public Task<AnonUsageStatsModel> LoadCurrentRecordByClientId(AnonUsageStatsModel model)
    {
        var match = _current.FirstOrDefault(m =>
            string.Equals(m.AnonClientId, model.AnonClientId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(match!);
    }

    public Task SaveOrUpdate(AnonUsageStatsModel newVersion)
    {
        SavedModels.Add(newVersion);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AnonUsageStatsModel>> LoadAllCurrentAsync(int? maxItems = null)
    {
        LoadAllCallCount++;
        LastMaxItems = maxItems;
        return Task.FromResult<IReadOnlyList<AnonUsageStatsModel>>(_current.ToList());
    }
}

internal static class TestData
{
    /// <summary>Builds a client report. Chain <see cref="WithTables"/> to attach table stats.</summary>
    public static AnonUsageStatsModel Client(
        string id,
        DateTime? generated = null,
        string? build = null,
        string? imports = null,
        int? aiDataPoints = null)
    {
        return new AnonUsageStatsModel
        {
            AnonClientId = id,
            Generated = generated ?? DateTime.UtcNow,
            BuildVersionLabel = build,
            ConfiguredImportsEnabledDescription = imports,
            DataPointsFromAITotal = aiDataPoints,
            TableStats = new List<AnonUsageStatsModel.TableStat>()
        };
    }

    /// <summary>Attaches table stats given as (schema, table, rows, sizeMB).</summary>
    public static AnonUsageStatsModel WithTables(
        this AnonUsageStatsModel model,
        params (string? Schema, string Table, long Rows, decimal SizeMB)[] tables)
    {
        model.TableStats = tables.Select(t => new AnonUsageStatsModel.TableStat
        {
            SchemaName = t.Schema,
            TableName = t.Table,
            Rows = t.Rows,
            TotalSpaceMB = t.SizeMB
        }).ToList();
        return model;
    }
}
