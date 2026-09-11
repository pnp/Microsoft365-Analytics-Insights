using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataUtils.Sql;

namespace Common.Entities.LicenceActivity
{
    public interface ILicenceActivityReadModelLoader
    {
        Task<LicenceActivityReadModel> LoadReadModelAsync(
            LicenceActivityQuery range, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken);
    }

    public sealed partial class SqlLicenceActivityStore
    {
        public async Task<LicenceActivityReadModel> LoadReadModelAsync(
            LicenceActivityQuery range, LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (range == null) throw new ArgumentNullException(nameof(range));
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (!sources.UserMetadata)
                throw new InvalidOperationException("Licence activity requires the user metadata import.");
            if (range.DepartmentId.HasValue || range.CountryId.HasValue || range.LicenceTypeId.HasValue)
                throw new ArgumentException("A shared read model must cover the unfiltered date range.", nameof(range));

            diagnostics = diagnostics ?? NullLicenceActivityDiagnostics.Instance;
            var watch = Stopwatch.StartNew();
            diagnostics.Stage("OverviewSqlStarted");
            var eligibleTable = "##LicenceActivityEligible_" + Guid.NewGuid().ToString("N");
            using (var owner = await CreateSharedEligibleUsersAsync(
                eligibleTable, range, sources, diagnostics, cancellationToken).ConfigureAwait(false))
            {
                _instrumentation?.OperationCompleted?.Invoke("eligible", watch.ElapsedMilliseconds);
                try
                {
                    var directoryTask = LoadReadModelDirectoryAsync(eligibleTable, cancellationToken);
                    var factsTask = LoadReadModelFactsAsync(range, sources, eligibleTable, diagnostics, cancellationToken);
                    // Observe both branches before releasing their shared table, including on failure.
                    await Task.WhenAll(directoryTask, factsTask).ConfigureAwait(false);
                    var directory = await directoryTask.ConfigureAwait(false);
                    var facts = await factsTask.ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    diagnostics.Stage("MaterialisationStarted");
                    var partWatch = Stopwatch.StartNew();
                    var model = new LicenceActivityReadModel(
                        range, directory.Licences, directory.Users, directory.Memberships,
                        facts.Overview.Coverage, facts.Scores, sources.UsageReportsGroupFiltered);
                    _instrumentation?.OperationCompleted?.Invoke("read-model", partWatch.ElapsedMilliseconds);
                    diagnostics.Stage("MaterialisationCompleted", watch.ElapsedMilliseconds);
                    diagnostics.Stage("OverviewSqlCompleted", watch.ElapsedMilliseconds);
                    return model;
                }
                finally
                {
                    var releaseWatch = Stopwatch.StartNew();
                    await DropSharedEligibleUsersAsync(owner, eligibleTable).ConfigureAwait(false);
                    _instrumentation?.OperationCompleted?.Invoke("release-eligible", releaseWatch.ElapsedMilliseconds);
                }
            }
        }

        private async Task<ReadModelFacts> LoadReadModelFactsAsync(
            LicenceActivityQuery range, LicenceActivitySources sources, string eligibleTable,
            ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            diagnostics.Stage("CoverageStarted");
            var watch = Stopwatch.StartNew();
            var overview = await ExecuteOverviewSqlAsync(
                LicenceActivitySql.BuildReadModelCoverage(sources, eligibleTable),
                "coverage", range, sources, diagnostics,
                reader => ReadModelCoverageAsync(reader, range, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            if (!sources.UsageReports)
            {
                overview.Coverage.RemoveAll(c => c.Workload != "copilot");
                overview.Coverage.InsertRange(0, Enumerable.Range(LicenceActivitySql.Teams, 4)
                    .Select(workload => DisabledM365Part(workload, range).Coverage));
            }
            diagnostics.Stage("CoverageCompleted", watch.ElapsedMilliseconds);
            var tasks = overview.Coverage.Select(async coverage =>
            {
                var scores = await LoadReadModelEvidenceAsync(
                    overview, coverage, eligibleTable, sources, diagnostics, cancellationToken).ConfigureAwait(false);
                return new KeyValuePair<string, IReadOnlyDictionary<int, LicenceActivityScore>>(coverage.Workload, scores);
            }).ToArray();
            var evidence = await Task.WhenAll(tasks).ConfigureAwait(false);
            return new ReadModelFacts
            {
                Overview = overview,
                Scores = evidence.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
        }

        private async Task<ReadModelDirectory> LoadReadModelDirectoryAsync(
            string eligibleTable, CancellationToken cancellationToken)
        {
            await OverviewSqlSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            var directory = new ReadModelDirectory();
            try
            {
                using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    if (_instrumentation?.ConnectionOpenedForOperation != null)
                        _instrumentation.ConnectionOpenedForOperation(connection, "directory");
                    else
                        _instrumentation?.ConnectionOpened?.Invoke(connection);
                    using (var command = new SqlCommand(LicenceActivitySql.BuildReadModelDirectory(eligibleTable), connection)
                    {
                        CommandTimeout = _instrumentation?.CommandTimeoutSeconds ?? LicenceActivitySql.CommandTimeoutSeconds
                    })
                    using (_instrumentation?.TrackCommand())
                    using (cancellationToken.Register(command.Cancel))
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        await RequireResultAsync(reader, true, cancellationToken, "id", "name", "sku_id").ConfigureAwait(false);
                        while (ReadModelRow(reader, cancellationToken))
                            directory.Licences.Add(new LicenceActivitySku
                            {
                                LicenceTypeId = reader.GetInt32(0),
                                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                                SkuId = reader.IsDBNull(2) ? null : reader.GetString(2)
                            });
                        await RequireResultAsync(reader, false, cancellationToken, "user_id", "user_name", "mail").ConfigureAwait(false);
                        while (ReadModelRow(reader, cancellationToken))
                            directory.Users.Add(new LicenceActivityDirectoryUser
                            {
                                UserId = reader.GetInt32(0),
                                UserPrincipalName = reader.GetString(1),
                                Mail = reader.IsDBNull(2) ? null : reader.GetString(2),
                                DepartmentId = reader.GetInt32(3),
                                Department = reader.IsDBNull(4) ? null : reader.GetString(4),
                                CountryId = reader.GetInt32(5),
                                Country = reader.IsDBNull(6) ? null : reader.GetString(6),
                                AccountEnabled = reader.IsDBNull(7) ? (bool?)null : reader.GetBoolean(7)
                            });
                        await RequireResultAsync(reader, false, cancellationToken, "user_id", "license_type_id").ConfigureAwait(false);
                        while (ReadModelRow(reader, cancellationToken))
                            directory.Memberships.Add(new LicenceActivityMembership(reader.GetInt32(0), reader.GetInt32(1)));
                        await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
                    }
                }
                return directory;
            }
            finally
            {
                _instrumentation?.OperationCompleted?.Invoke("directory", watch.ElapsedMilliseconds);
                OverviewSqlSlots.Release();
            }
        }

        private async Task<LicenceActivityOverview> ReadModelCoverageAsync(
            SqlDataReader reader, LicenceActivityQuery range, CancellationToken cancellationToken)
        {
            var result = new LicenceActivityOverview { Query = range };
            await RequireResultAsync(reader, true, cancellationToken,
                "Workload", "Status", "Source", "ExpectedSamples").ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                result.Coverage.Add(new LicenceActivityCoverage
                {
                    Workload = ReadString(reader, "Workload"),
                    Status = ReadString(reader, "Status"),
                    Source = ReadString(reader, "Source"),
                    Measure = ReadString(reader, "Measure"),
                    Granularity = ReadString(reader, "Granularity"),
                    Message = ReadNullableString(reader, "Message"),
                    EffectiveFromUtc = ReadNullableUtc(reader, "EffectiveFromUtc"),
                    EffectiveToUtc = ReadNullableUtc(reader, "EffectiveToUtc"),
                    LatestImportUtc = ReadNullableUtc(reader, "LatestImportUtc"),
                    LagDays = ReadInt32(reader, "LagDays"),
                    ReportPeriodDays = ReadNullableInt32(reader, "ReportPeriodDays"),
                    ExpectedSamples = ReadInt32(reader, "ExpectedSamples"),
                    ObservedSamples = ReadInt32(reader, "ObservedSamples"),
                    UnmatchedUsers = ReadInt32(reader, "UnmatchedUsers")
                });
            await RequireResultAsync(reader, false, cancellationToken, "WorkloadName", "SnapshotDate").ConfigureAwait(false);
            var byWorkload = result.Coverage.ToDictionary(c => c.Workload, StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                byWorkload[ReadString(reader, "WorkloadName")].SnapshotDates.Add(ReadUtc(reader, "SnapshotDate"));
            await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
            return result;
        }

        private async Task<IReadOnlyDictionary<int, LicenceActivityScore>> LoadReadModelEvidenceAsync(
            LicenceActivityOverview overview, LicenceActivityCoverage coverage, string eligibleTable,
            LicenceActivitySources sources, ILicenceActivityDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            if (coverage.SnapshotDates.Count == 0
                && (coverage.Source == LicenceActivitySql.M365ReportSource
                    || coverage.Source == LicenceActivitySql.CopilotReportSource))
                return new Dictionary<int, LicenceActivityScore>();
            await OverviewSqlSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            try
            {
                using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    if (_instrumentation?.ConnectionOpenedForOperation != null)
                        _instrumentation.ConnectionOpenedForOperation(connection, coverage.Workload);
                    else
                        _instrumentation?.ConnectionOpened?.Invoke(connection);
                    using (var command = new SqlCommand(
                        LicenceActivitySql.BuildReadModelEvidence(overview, coverage, eligibleTable), connection)
                    {
                        CommandTimeout = _instrumentation?.CommandTimeoutSeconds ?? LicenceActivitySql.CommandTimeoutSeconds
                    })
                    {
                        AddScopeParameters(command, overview.Query, sources);
                        AddCoverageParameters(command, overview);
                        using (_instrumentation?.TrackCommand())
                        using (cancellationToken.Register(command.Cancel))
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                        {
                            await RequireResultAsync(reader, true, cancellationToken,
                                "user_id", "active_samples", "observed_samples").ConfigureAwait(false);
                            var scores = new Dictionary<int, LicenceActivityScore>();
                            while (ReadModelRow(reader, cancellationToken))
                                scores.Add(reader.GetInt32(0), new LicenceActivityScore(
                                    reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3),
                                    reader.IsDBNull(4) ? (double?)null : reader.GetDouble(4),
                                    reader.IsDBNull(5) ? (DateTime?)null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)));
                            await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
                            return scores;
                        }
                    }
                }
            }
            finally
            {
                _instrumentation?.OperationCompleted?.Invoke(coverage.Workload, watch.ElapsedMilliseconds);
                OverviewSqlSlots.Release();
            }
        }

        private static bool ReadModelRow(SqlDataReader reader, CancellationToken cancellationToken)
        {
            // These bulk reads run on bounded background workers. Avoid an async state machine
            // per scalar row; the command cancellation registration interrupts blocked I/O.
            cancellationToken.ThrowIfCancellationRequested();
            return reader.Read();
        }

        private sealed class ReadModelDirectory
        {
            internal List<LicenceActivitySku> Licences { get; } = new List<LicenceActivitySku>();
            internal List<LicenceActivityDirectoryUser> Users { get; } = new List<LicenceActivityDirectoryUser>();
            internal List<LicenceActivityMembership> Memberships { get; } = new List<LicenceActivityMembership>();
        }

        private sealed class ReadModelFacts
        {
            internal LicenceActivityOverview Overview { get; set; }
            internal IReadOnlyDictionary<string, IReadOnlyDictionary<int, LicenceActivityScore>> Scores { get; set; }
        }
    }
}
