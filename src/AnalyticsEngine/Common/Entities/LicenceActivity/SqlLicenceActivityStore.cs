using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LicenceActivity
{
    /// <summary>
    /// Bounded ADO.NET adapter for licence activity. User calls own one connection and batch; overview
    /// calls own a shared-scope connection and independently bounded workload connections.
    /// No EF context or request synchronization context is shared.
    /// </summary>
    public sealed class SqlLicenceActivityStore : ILicenceActivityStore
    {
        private static readonly SemaphoreSlim OverviewSqlSlots = new SemaphoreSlim(6, 6);
        private readonly string _connectionString;
        private readonly SqlLicenceActivityStoreInstrumentation _instrumentation;

        public SqlLicenceActivityStore(string connectionString)
            : this(connectionString, null)
        {
        }

        internal SqlLicenceActivityStore(
            string connectionString,
            SqlLicenceActivityStoreInstrumentation instrumentation)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException("A SQL connection string is required.", nameof(connectionString));

            _connectionString = connectionString;
            _instrumentation = instrumentation;
        }

        public async Task<LicenceActivityOverview> LoadOverviewAsync(
            LicenceActivityQuery query,
            LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (!sources.UserMetadata)
                throw new InvalidOperationException("Licence activity requires the user metadata import.");

            diagnostics = diagnostics ?? NullLicenceActivityDiagnostics.Instance;
            var sqlWatch = Stopwatch.StartNew();
            diagnostics.Stage("OverviewSqlStarted");
            diagnostics.Stage("CoverageStarted");

            var suffix = Guid.NewGuid().ToString("N");
            var eligibleTable = "##LicenceActivityEligible_" + suffix;
            using (var eligibleConnection = await CreateSharedEligibleUsersAsync(
                eligibleTable, query, sources, diagnostics, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var partTasks = new List<Task<OverviewPart>>();
                    if (sources.UsageReports)
                    {
                        for (var workload = LicenceActivitySql.Teams;
                            workload <= LicenceActivitySql.SharePoint;
                            workload++)
                        {
                            partTasks.Add(ExecuteOverviewPartAsync(
                                LicenceActivitySql.BuildM365OverviewPart(workload, eligibleTable,
                                    maximumSamples: (query.Days + ((int)query.FromUtc.DayOfWeek + 6) % 7 + 6) / 7),
                                LicenceActivitySql.WorkloadName(workload),
                                query, sources, diagnostics, cancellationToken));
                        }
                    }

                    partTasks.Add(ExecuteOverviewPartAsync(
                        LicenceActivitySql.BuildCopilotOverviewPart(sources, eligibleTable),
                        "copilot",
                        query, sources, diagnostics, cancellationToken));

                    var materialisationWatch = Stopwatch.StartNew();
                    diagnostics.Stage("MaterialisationStarted");
                    var parts = await Task.WhenAll(partTasks).ConfigureAwait(false);

                    var baseResult = parts.Select(part => part.Base)
                        .First(value => value != null);
                    var overview = MergeOverview(baseResult, parts, query, sources);
                    materialisationWatch.Stop();
                    sqlWatch.Stop();
                    diagnostics.Stage("MaterialisationCompleted", materialisationWatch.ElapsedMilliseconds);
                    diagnostics.Stage("CoverageCompleted", sqlWatch.ElapsedMilliseconds);
                    diagnostics.Stage("OverviewSqlCompleted", sqlWatch.ElapsedMilliseconds);
                    diagnostics.Stage("ProjectionCompleted");
                    return overview;
                }
                finally
                {
                    await DropSharedEligibleUsersAsync(
                        eligibleConnection, eligibleTable).ConfigureAwait(false);
                }
            }
        }

        public async Task<LicenceActivityUsers> LoadUsersAsync(
            LicenceActivityOverview overview,
            LicenceActivityQuery query,
            LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            if (overview == null) throw new ArgumentNullException(nameof(overview));
            if (query == null) throw new ArgumentNullException(nameof(query));
            if (sources == null) throw new ArgumentNullException(nameof(sources));
            if (!sources.UserMetadata)
                throw new InvalidOperationException("Licence activity requires the user metadata import.");
            if (!query.LicenceTypeId.HasValue)
                throw new ArgumentException("A licenceTypeId is required for individual users.", nameof(query));
            if (overview.Query == null
                || overview.Query.From != query.From
                || overview.Query.To != query.To
                || overview.Query.DepartmentId != query.DepartmentId
                || overview.Query.CountryId != query.CountryId)
            {
                throw new ArgumentException(
                    "Individual users must inherit the overview date range and demographic scope.",
                    nameof(query));
            }
            if (!overview.Licences.Any(l => l.LicenceTypeId == query.LicenceTypeId.Value))
                throw new ArgumentException("The selected licence is not present in this overview.", nameof(query));

            diagnostics = diagnostics ?? NullLicenceActivityDiagnostics.Instance;

            using (var connection = new SqlConnection(_connectionString))
            {
                var connectionWatch = Stopwatch.StartNew();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                connectionWatch.Stop();
                _instrumentation?.ConnectionOpened?.Invoke(connection);
                diagnostics.Stage("ConnectionOpened", connectionWatch.ElapsedMilliseconds);

                using (var command = new SqlCommand(LicenceActivitySql.BuildUsers(overview, query), connection)
                {
                    CommandTimeout = _instrumentation?.CommandTimeoutSeconds
                        ?? LicenceActivitySql.CommandTimeoutSeconds
                })
                {
                    AddUserParameters(command, overview, query, sources);
                    var sqlWatch = Stopwatch.StartNew();
                    diagnostics.Stage("UsersSqlStarted");

                    using (_instrumentation?.TrackCommand())
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var materialisationWatch = Stopwatch.StartNew();
                        diagnostics.Stage("MaterialisationStarted");
                        var users = await ReadUsersAsync(
                            reader, overview, query, diagnostics, cancellationToken).ConfigureAwait(false);
                        materialisationWatch.Stop();
                        sqlWatch.Stop();
                        diagnostics.Stage("MaterialisationCompleted", materialisationWatch.ElapsedMilliseconds);
                        diagnostics.Stage("UsersSqlCompleted", sqlWatch.ElapsedMilliseconds);
                        return users;
                    }
                }
            }
        }

        private async Task<SqlConnection> CreateSharedEligibleUsersAsync(
            string tableName,
            LicenceActivityQuery query,
            LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            var connection = new SqlConnection(_connectionString);
            try
            {
                var watch = Stopwatch.StartNew();
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                watch.Stop();
                if (_instrumentation?.ConnectionOpenedForOperation != null)
                    _instrumentation.ConnectionOpenedForOperation(connection, "eligible");
                else
                    _instrumentation?.ConnectionOpened?.Invoke(connection);
                diagnostics.Stage("ConnectionOpened", watch.ElapsedMilliseconds);

                using (var command = new SqlCommand(
                    LicenceActivitySql.BuildSharedEligibleUsers(tableName),
                    connection)
                {
                    CommandTimeout = _instrumentation?.CommandTimeoutSeconds
                        ?? LicenceActivitySql.CommandTimeoutSeconds
                })
                {
                    AddScopeParameters(command, query, sources);
                    using (_instrumentation?.TrackCommand())
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                return connection;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        private Task DropSharedEligibleUsersAsync(
            SqlConnection connection,
            string tableName)
        {
            if (connection.State != ConnectionState.Open) return Task.CompletedTask;
            using (var command = new SqlCommand("DROP TABLE " + tableName + ";", connection)
            {
                CommandTimeout = LicenceActivitySql.CommandTimeoutSeconds
            })
            {
                using (_instrumentation?.TrackCommand())
                    command.ExecuteNonQuery();
            }
            return Task.CompletedTask;
        }

        private async Task<OverviewPart> ExecuteCoveragePartAsync(
            string sql,
            string operation,
            LicenceActivityQuery query,
            LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            return await ExecuteOverviewSqlAsync(
                sql,
                operation,
                query,
                sources,
                diagnostics,
                async reader =>
                {
                    var part = new OverviewPart();
                    await RequireResultAsync(
                        reader, true, cancellationToken,
                        "Workload", "Status", "Source", "ExpectedSamples").ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException(
                            "A licence activity workload coverage row was not returned.");
                    part.Coverage = new LicenceActivityCoverage
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
                    };

                    await RequireResultAsync(
                        reader, false, cancellationToken,
                        "WorkloadName", "SnapshotDate").ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        part.Coverage.SnapshotDates.Add(ReadUtc(reader, "SnapshotDate"));
                    await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
                    return part;
                },
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<OverviewProjectionResult> ExecuteOverviewDistributionAsync(
            string eligibleTable,
            IReadOnlyList<string> bandTables,
            LicenceActivityQuery query,
            LicenceActivitySources sources,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            return await ExecuteOverviewSqlAsync(
                LicenceActivitySql.BuildOverviewDistributions(eligibleTable, bandTables),
                "distributions",
                query,
                sources,
                diagnostics,
                async reader =>
                {
                    var result = new OverviewProjectionResult();
                    await RequireResultAsync(
                        reader, true, cancellationToken,
                        "LicenceTypeId", "Workload", "High", "Unknown").ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.LicenceDistributions.Add(new LicenceDistributionRow
                        {
                            LicenceTypeId = ReadInt32(reader, "LicenceTypeId"),
                            Distribution = ReadDistribution(reader)
                        });
                    }

                    await RequireResultAsync(
                        reader, false, cancellationToken,
                        "Dimension", "Id", "Workload", "High", "Unknown").ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.DemographicDistributions.Add(new DemographicDistributionRow
                        {
                            Dimension = ReadString(reader, "Dimension"),
                            Id = ReadInt32(reader, "Id"),
                            Distribution = ReadDistribution(reader)
                        });
                    }

                    await RequireResultAsync(
                        reader, false, cancellationToken,
                        "DistinctAssignedUsers", "DemographicsTruncated").ConfigureAwait(false);
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("The licence activity base summary was not returned.");
                    result.Base.DistinctAssignedUsers = ReadInt32(reader, "DistinctAssignedUsers");
                    result.Base.DemographicsTruncated = ReadBoolean(reader, "DemographicsTruncated");

                    await RequireResultAsync(
                        reader, false, cancellationToken,
                        "LicenceTypeId", "Name", "SkuId", "AssignedUsers").ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.Base.Licences.Add(new LicenceActivitySku
                        {
                            LicenceTypeId = ReadInt32(reader, "LicenceTypeId"),
                            Name = ReadNullableString(reader, "Name"),
                            SkuId = ReadNullableString(reader, "SkuId"),
                            AssignedUsers = ReadInt32(reader, "AssignedUsers")
                        });
                    }

                    await RequireResultAsync(
                        reader, false, cancellationToken,
                        "Dimension", "Id", "Name", "AssignedUsers").ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        result.Base.Demographics.Add(new DemographicRow
                        {
                            Dimension = ReadString(reader, "Dimension"),
                            Value = new LicenceActivityDemographic
                            {
                                Id = ReadInt32(reader, "Id"),
                                Name = ReadString(reader, "Name"),
                                AssignedUsers = ReadInt32(reader, "AssignedUsers")
                            }
                        });
                    }
                    await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
                    return result;
                },
                cancellationToken).ConfigureAwait(false);
        }

                private async Task<OverviewPart> ExecuteOverviewPartAsync(
                    string sql,
                    string operation,
                    LicenceActivityQuery query,
                    LicenceActivitySources sources,
                    ILicenceActivityDiagnostics diagnostics,
                    CancellationToken cancellationToken)
                {
                    return await ExecuteOverviewSqlAsync(
                        sql,
                        operation,
                        query,
                        sources,
                        diagnostics,
                        async reader =>
                        {
                            var part = new OverviewPart();
                            await RequireResultAsync(
                                reader, true, cancellationToken,
                                "Workload", "Status", "Source", "ExpectedSamples").ConfigureAwait(false);
                            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                throw new InvalidOperationException("A licence activity workload coverage row was not returned.");
                            part.Coverage = new LicenceActivityCoverage
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
                            };

                            await RequireResultAsync(
                                reader, false, cancellationToken,
                                "WorkloadName", "SnapshotDate").ConfigureAwait(false);
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                part.Coverage.SnapshotDates.Add(ReadUtc(reader, "SnapshotDate"));

                            await RequireResultAsync(
                                reader, false, cancellationToken,
                                "LicenceTypeId", "Workload", "High", "Unknown").ConfigureAwait(false);
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                part.LicenceDistributions.Add(new LicenceDistributionRow
                                {
                                    LicenceTypeId = ReadInt32(reader, "LicenceTypeId"),
                                    Distribution = ReadDistribution(reader)
                                });
                            }

                            await RequireResultAsync(
                                reader, false, cancellationToken,
                                "Dimension", "Id", "Workload", "High", "Unknown").ConfigureAwait(false);
                            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                part.DemographicDistributions.Add(new DemographicDistributionRow
                                {
                                    Dimension = ReadString(reader, "Dimension"),
                                    Id = ReadInt32(reader, "Id"),
                                    Distribution = ReadDistribution(reader)
                                });
                            }

                            if (operation == "teams"
                                || operation == "copilot"
                                || operation == "distribution-teams")
                            {
                                part.Base = new OverviewBase();
                                await RequireResultAsync(
                                    reader, false, cancellationToken,
                                    "DistinctAssignedUsers", "DemographicsTruncated").ConfigureAwait(false);
                                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                    throw new InvalidOperationException("The licence activity base summary was not returned.");
                                part.Base.DistinctAssignedUsers = ReadInt32(reader, "DistinctAssignedUsers");
                                part.Base.DemographicsTruncated = ReadBoolean(reader, "DemographicsTruncated");

                                await RequireResultAsync(
                                    reader, false, cancellationToken,
                                    "LicenceTypeId", "Name", "SkuId", "AssignedUsers").ConfigureAwait(false);
                                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                {
                                    part.Base.Licences.Add(new LicenceActivitySku
                                    {
                                        LicenceTypeId = ReadInt32(reader, "LicenceTypeId"),
                                        Name = ReadNullableString(reader, "Name"),
                                        SkuId = ReadNullableString(reader, "SkuId"),
                                        AssignedUsers = ReadInt32(reader, "AssignedUsers")
                                    });
                                }

                                await RequireResultAsync(
                                    reader, false, cancellationToken,
                                    "Dimension", "Id", "Name", "AssignedUsers").ConfigureAwait(false);
                                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                                {
                                    part.Base.Demographics.Add(new DemographicRow
                                    {
                                        Dimension = ReadString(reader, "Dimension"),
                                        Value = new LicenceActivityDemographic
                                        {
                                            Id = ReadInt32(reader, "Id"),
                                            Name = ReadString(reader, "Name"),
                                            AssignedUsers = ReadInt32(reader, "AssignedUsers")
                                        }
                                    });
                                }
                            }
                            await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);
                            return part;
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                private async Task<T> ExecuteOverviewSqlAsync<T>(
                    string sql,
                    string operation,
                    LicenceActivityQuery query,
                    LicenceActivitySources sources,
                    ILicenceActivityDiagnostics diagnostics,
                    Func<SqlDataReader, Task<T>> materialize,
                    CancellationToken cancellationToken)
                {
                    await OverviewSqlSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var operationWatch = Stopwatch.StartNew();
                    try
                    {
                        using (var connection = new SqlConnection(_connectionString))
                        {
                            var connectionWatch = Stopwatch.StartNew();
                            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                            connectionWatch.Stop();
                            if (_instrumentation?.ConnectionOpenedForOperation != null)
                                _instrumentation.ConnectionOpenedForOperation(connection, operation);
                            else
                                _instrumentation?.ConnectionOpened?.Invoke(connection);
                            diagnostics.Stage("ConnectionOpened", connectionWatch.ElapsedMilliseconds);

                            using (var command = new SqlCommand(sql, connection)
                            {
                                CommandTimeout = _instrumentation?.CommandTimeoutSeconds
                                    ?? LicenceActivitySql.CommandTimeoutSeconds
                            })
                            {
                                AddScopeParameters(command, query, sources);
                                using (_instrumentation?.TrackCommand())
                                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                                    return await materialize(reader).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        operationWatch.Stop();
                        _instrumentation?.OperationCompleted?.Invoke(
                            operation, operationWatch.ElapsedMilliseconds);
                        OverviewSqlSlots.Release();
                    }
                }

                private static LicenceActivityOverview MergeOverview(
                    OverviewBase baseResult,
                    IEnumerable<OverviewPart> loadedParts,
                    LicenceActivityQuery query,
                    LicenceActivitySources sources)
                {
                    var overview = new LicenceActivityOverview
                    {
                        Query = query,
                        DistinctAssignedUsers = baseResult.DistinctAssignedUsers,
                        DemographicsTruncated = baseResult.DemographicsTruncated,
                        Licences = baseResult.Licences
                    };

                    foreach (var row in baseResult.Demographics)
                    {
                        if (row.Dimension == "department") overview.Departments.Add(row.Value);
                        else overview.Countries.Add(row.Value);
                    }

                    var parts = loadedParts.ToList();
                    if (!sources.UsageReports)
                    {
                        for (var workload = LicenceActivitySql.Teams;
                            workload <= LicenceActivitySql.SharePoint;
                            workload++)
                        {
                            parts.Add(DisabledM365Part(workload, query));
                        }
                    }

                    var licences = overview.Licences.ToDictionary(l => l.LicenceTypeId);
                    var demographics = baseResult.Demographics.ToDictionary(
                        d => DemographicKey(d.Dimension, d.Value.Id),
                        d => d.Value,
                        StringComparer.Ordinal);

                    foreach (var part in parts.OrderBy(p => LicenceActivitySql.WorkloadId(p.Coverage.Workload)))
                    {
                        overview.Coverage.Add(part.Coverage);
                        foreach (var distribution in part.LicenceDistributions)
                        {
                            LicenceActivitySku licence;
                            if (licences.TryGetValue(distribution.LicenceTypeId, out licence))
                                licence.Workloads.Add(distribution.Distribution);
                        }
                        foreach (var distribution in part.DemographicDistributions)
                        {
                            LicenceActivityDemographic demographic;
                            if (demographics.TryGetValue(
                                DemographicKey(distribution.Dimension, distribution.Id),
                                out demographic))
                            {
                                demographic.Workloads.Add(distribution.Distribution);
                            }
                        }
                    }

                    foreach (var licence in overview.Licences)
                    {
                        if (licence.Workloads.Count > 0)
                        {
                            licence.AssignedUsers = licence.Workloads.Max(w =>
                                w.High + w.Moderate + w.Low + w.Zero + w.Unknown);
                        }
                        foreach (var workload in LicenceActivityQuery.Workloads)
                        {
                            if (!licence.Workloads.Any(w => w.Workload == workload))
                            {
                                licence.Workloads.Add(new LicenceActivityDistribution
                                {
                                    Workload = workload,
                                    Unknown = licence.AssignedUsers
                                });
                            }
                        }
                        licence.Workloads = licence.Workloads
                            .OrderBy(w => LicenceActivitySql.WorkloadId(w.Workload)).ToList();
                    }
                    foreach (var demographic in overview.Departments.Concat(overview.Countries))
                    {
                        foreach (var workload in LicenceActivityQuery.Workloads)
                        {
                            if (!demographic.Workloads.Any(w => w.Workload == workload))
                            {
                                demographic.Workloads.Add(new LicenceActivityDistribution
                                {
                                    Workload = workload,
                                    Unknown = demographic.AssignedUsers
                                });
                            }
                        }
                        demographic.Workloads = demographic.Workloads
                            .OrderBy(w => LicenceActivitySql.WorkloadId(w.Workload)).ToList();
                    }

                    if (overview.Licences.Count == 0)
                        overview.Messages.Add("No imported licence types are available.");
                    else if (overview.DistinctAssignedUsers == 0)
                        overview.Messages.Add("Licence types are imported, but no users in the selected scope currently hold one.");
                    overview.Messages.Add(
                        "User display names are not imported by this solution. Individual results use the user principal name; search also checks the stored mail address.");
                    foreach (var coverage in overview.Coverage.Where(c => c.Status != LicenceActivitySql.Available))
                    {
                        if (!string.IsNullOrWhiteSpace(coverage.Message))
                            overview.Messages.Add(coverage.Workload + ": " + coverage.Message);
                    }
                    if (overview.DemographicsTruncated)
                        overview.Messages.Add("Department or country breakdowns are limited to the 50 largest values.");
                    return overview;
                }

        private static void MergeOverviewIntoOverview(
            LicenceActivityOverview overview,
            LicenceActivityOverview additional)
        {
            overview.Coverage.AddRange(additional.Coverage);

            var licences = overview.Licences.ToDictionary(licence => licence.LicenceTypeId);
            foreach (var additionalLicence in additional.Licences)
            {
                LicenceActivitySku licence;
                if (licences.TryGetValue(additionalLicence.LicenceTypeId, out licence))
                    licence.Workloads.AddRange(additionalLicence.Workloads);
            }

            var demographics = overview.Departments
                .Select(value => new { Dimension = "department", Value = value })
                .Concat(overview.Countries.Select(
                    value => new { Dimension = "country", Value = value }))
                .ToDictionary(
                    item => DemographicKey(item.Dimension, item.Value.Id),
                    item => item.Value,
                    StringComparer.Ordinal);
            foreach (var item in additional.Departments.Select(
                         value => new { Dimension = "department", Value = value })
                     .Concat(additional.Countries.Select(
                         value => new { Dimension = "country", Value = value })))
            {
                LicenceActivityDemographic demographic;
                if (demographics.TryGetValue(
                    DemographicKey(item.Dimension, item.Value.Id),
                    out demographic))
                {
                    demographic.Workloads.AddRange(item.Value.Workloads);
                }
            }
        }

        private static void MergePartIntoOverview(
            LicenceActivityOverview overview,
            OverviewPart part)
        {
            overview.Coverage.Add(part.Coverage);
            overview.Coverage = overview.Coverage
                .OrderBy(coverage => LicenceActivitySql.WorkloadId(coverage.Workload))
                .ToList();

            var licences = overview.Licences.ToDictionary(licence => licence.LicenceTypeId);
            foreach (var distribution in part.LicenceDistributions)
            {
                LicenceActivitySku licence;
                if (licences.TryGetValue(distribution.LicenceTypeId, out licence))
                    licence.Workloads.Add(distribution.Distribution);
            }

            var demographics = overview.Departments
                .Select(value => new { Dimension = "department", Value = value })
                .Concat(overview.Countries.Select(
                    value => new { Dimension = "country", Value = value }))
                .ToDictionary(
                    item => DemographicKey(item.Dimension, item.Value.Id),
                    item => item.Value,
                    StringComparer.Ordinal);
            foreach (var distribution in part.DemographicDistributions)
            {
                LicenceActivityDemographic demographic;
                if (demographics.TryGetValue(
                    DemographicKey(distribution.Dimension, distribution.Id),
                    out demographic))
                {
                    demographic.Workloads.Add(distribution.Distribution);
                }
            }

            foreach (var licence in overview.Licences)
            {
                if (!licence.Workloads.Any(
                    distribution => distribution.Workload == part.Coverage.Workload))
                {
                    licence.Workloads.Add(new LicenceActivityDistribution
                    {
                        Workload = part.Coverage.Workload,
                        Unknown = licence.AssignedUsers
                    });
                }
                licence.Workloads = licence.Workloads
                    .OrderBy(distribution => LicenceActivitySql.WorkloadId(distribution.Workload))
                    .ToList();
            }
            foreach (var demographic in overview.Departments.Concat(overview.Countries))
            {
                if (!demographic.Workloads.Any(
                    distribution => distribution.Workload == part.Coverage.Workload))
                {
                    demographic.Workloads.Add(new LicenceActivityDistribution
                    {
                        Workload = part.Coverage.Workload,
                        Unknown = demographic.AssignedUsers
                    });
                }
                demographic.Workloads = demographic.Workloads
                    .OrderBy(distribution => LicenceActivitySql.WorkloadId(distribution.Workload))
                    .ToList();
            }

            overview.Messages.Clear();
            if (overview.Licences.Count == 0)
                overview.Messages.Add("No imported licence types are available.");
            else if (overview.DistinctAssignedUsers == 0)
                overview.Messages.Add("Licence types are imported, but no users in the selected scope currently hold one.");
            overview.Messages.Add(
                "User display names are not imported by this solution. Individual results use the user principal name; search also checks the stored mail address.");
            foreach (var coverage in overview.Coverage.Where(
                coverage => coverage.Status != LicenceActivitySql.Available))
            {
                if (!string.IsNullOrWhiteSpace(coverage.Message))
                    overview.Messages.Add(coverage.Workload + ": " + coverage.Message);
            }
            if (overview.DemographicsTruncated)
                overview.Messages.Add("Department or country breakdowns are limited to the 50 largest values.");
        }

        private static OverviewPart DisabledM365Part(int workload, LicenceActivityQuery query)
                {
                    return new OverviewPart
                    {
                        Coverage = new LicenceActivityCoverage
                        {
                            Workload = LicenceActivitySql.WorkloadName(workload),
                            Status = LicenceActivitySql.Disabled,
                            Source = LicenceActivitySql.M365ReportSource,
                            Measure = "published usage-report snapshot evidence",
                            Granularity = "weeklySupportingSnapshot",
                            Message = "The Microsoft 365 usage-report import is disabled. Absence cannot be interpreted as zero.",
                            ExpectedSamples = WeekPortionCount(query)
                        }
                    };
                }

        private static int WeekPortionCount(LicenceActivityQuery query)
                {
                    var first = query.FromUtc.Date.AddDays(-(((int)query.FromUtc.DayOfWeek + 6) % 7));
                    var count = 0;
                    for (var date = first; date <= query.ToUtc.Date; date = date.AddDays(7)) count++;
                    return count;
        }

        private static void AddScopeParameters(
            SqlCommand command,
            LicenceActivityQuery query,
            LicenceActivitySources sources)
        {
            AddDate(command, "@from", query.FromUtc);
            AddDate(command, "@to", query.ToUtc);
            AddDate(command, "@endExclusive", query.EndExclusiveUtc);
            AddDate(command, "@settled", sources.NowUtc.Date.AddDays(-3));
            AddDate(command, "@now", sources.NowUtc.Date);
            AddNullableInt(command, "@departmentId", query.DepartmentId);
            AddNullableInt(command, "@countryId", query.CountryId);
        }

        private static void AddUserParameters(
            SqlCommand command,
            LicenceActivityOverview overview,
            LicenceActivityQuery query,
            LicenceActivitySources sources)
        {
            AddScopeParameters(command, query, sources);
            command.Parameters.Add("@licenceTypeId", SqlDbType.Int).Value = query.LicenceTypeId.Value;
            command.Parameters.Add("@top", SqlDbType.Int).Value = query.Top;
            command.Parameters.Add("@offset", SqlDbType.Int).Value = (query.Page - 1) * query.PageSize;
            command.Parameters.Add("@pageSize", SqlDbType.Int).Value = query.PageSize;

            var escapedSearch = EscapeLikeValue(query.Search);
            command.Parameters.Add("@searchPattern", SqlDbType.NVarChar, 202).Value =
                escapedSearch.Length == 0 ? string.Empty : "%" + escapedSearch + "%";

            var byWorkload = overview.Coverage.ToDictionary(c => c.Workload, StringComparer.Ordinal);
            for (var workload = LicenceActivitySql.Teams; workload <= LicenceActivitySql.Copilot; workload++)
            {
                var name = LicenceActivitySql.WorkloadName(workload);
                LicenceActivityCoverage coverage;
                if (!byWorkload.TryGetValue(name, out coverage))
                {
                    coverage = new LicenceActivityCoverage
                    {
                        Workload = name,
                        Status = LicenceActivitySql.MissingCoverage,
                        Source = string.Empty,
                        Measure = string.Empty
                    };
                }

                command.Parameters.Add("@status" + workload, SqlDbType.VarChar, 32).Value =
                    coverage.Status ?? LicenceActivitySql.MissingCoverage;
                command.Parameters.Add("@source" + workload, SqlDbType.VarChar, 64).Value =
                    coverage.Source ?? string.Empty;
                command.Parameters.Add("@measure" + workload, SqlDbType.NVarChar, 240).Value =
                    coverage.Measure ?? string.Empty;
                command.Parameters.Add("@expected" + workload, SqlDbType.Int).Value =
                    coverage.ExpectedSamples;
                command.Parameters.Add("@observed" + workload, SqlDbType.Int).Value =
                    coverage.ObservedSamples;
                command.Parameters.Add("@period" + workload, SqlDbType.Int).Value =
                    coverage.ReportPeriodDays.HasValue
                        ? (object)coverage.ReportPeriodDays.Value
                        : DBNull.Value;

                if (coverage.SnapshotDates == null) continue;
                for (var index = 0; index < coverage.SnapshotDates.Count; index++)
                {
                    AddDate(
                        command,
                        LicenceActivitySql.SampleParameterName(workload, index),
                        coverage.SnapshotDates[index]);
                }
            }
        }

        private async Task<LicenceActivityOverview> ReadOverviewAsync(
            SqlDataReader reader,
            LicenceActivityQuery query,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            var overview = new LicenceActivityOverview { Query = query };

            await RequireResultAsync(
                reader, true, cancellationToken, "DistinctAssignedUsers", "DemographicsTruncated")
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The licence activity overview summary was not returned.");
            overview.DistinctAssignedUsers = ReadInt32(reader, "DistinctAssignedUsers");
            overview.DemographicsTruncated = ReadBoolean(reader, "DemographicsTruncated");

            await RequireResultAsync(
                reader, false, cancellationToken, "Workload", "Status", "Source", "ExpectedSamples")
                .ConfigureAwait(false);
            var coverageByWorkload = new Dictionary<string, LicenceActivityCoverage>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var coverage = new LicenceActivityCoverage
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
                };
                overview.Coverage.Add(coverage);
                coverageByWorkload[coverage.Workload] = coverage;
            }

            await RequireResultAsync(
                reader, false, cancellationToken, "WorkloadName", "SnapshotDate")
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LicenceActivityCoverage coverage;
                if (coverageByWorkload.TryGetValue(ReadString(reader, "WorkloadName"), out coverage))
                    coverage.SnapshotDates.Add(ReadUtc(reader, "SnapshotDate"));
            }

            await RequireResultAsync(
                reader, false, cancellationToken, "LicenceTypeId", "Name", "SkuId", "AssignedUsers")
                .ConfigureAwait(false);
            var licencesById = new Dictionary<int, LicenceActivitySku>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var licence = new LicenceActivitySku
                {
                    LicenceTypeId = ReadInt32(reader, "LicenceTypeId"),
                    Name = ReadNullableString(reader, "Name"),
                    SkuId = ReadNullableString(reader, "SkuId"),
                    AssignedUsers = ReadInt32(reader, "AssignedUsers")
                };
                overview.Licences.Add(licence);
                licencesById[licence.LicenceTypeId] = licence;
            }

            await RequireResultAsync(
                reader, false, cancellationToken, "LicenceTypeId", "Workload", "High", "Unknown")
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LicenceActivitySku licence;
                if (licencesById.TryGetValue(ReadInt32(reader, "LicenceTypeId"), out licence))
                    licence.Workloads.Add(ReadDistribution(reader));
            }

            await RequireResultAsync(
                reader, false, cancellationToken, "Dimension", "Id", "Name", "AssignedUsers")
                .ConfigureAwait(false);
            var demographics = new Dictionary<string, LicenceActivityDemographic>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var dimension = ReadString(reader, "Dimension");
                var demographic = new LicenceActivityDemographic
                {
                    Id = ReadInt32(reader, "Id"),
                    Name = ReadString(reader, "Name"),
                    AssignedUsers = ReadInt32(reader, "AssignedUsers")
                };
                if (dimension == "department")
                    overview.Departments.Add(demographic);
                else
                    overview.Countries.Add(demographic);
                demographics[DemographicKey(dimension, demographic.Id)] = demographic;
            }

            await RequireResultAsync(
                reader, false, cancellationToken, "Dimension", "Id", "Workload", "High", "Unknown")
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                LicenceActivityDemographic demographic;
                var key = DemographicKey(ReadString(reader, "Dimension"), ReadInt32(reader, "Id"));
                if (demographics.TryGetValue(key, out demographic))
                    demographic.Workloads.Add(ReadDistribution(reader));
            }
            await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);

            if (overview.Licences.Count == 0)
                overview.Messages.Add("No imported licence types are available.");
            else if (overview.DistinctAssignedUsers == 0)
                overview.Messages.Add("Licence types are imported, but no users in the selected scope currently hold one.");

            overview.Messages.Add(
                "User display names are not imported by this solution. Individual results use the user principal name; search also checks the stored mail address.");

            foreach (var coverage in overview.Coverage.Where(c => c.Status != LicenceActivitySql.Available))
            {
                if (!string.IsNullOrWhiteSpace(coverage.Message))
                    overview.Messages.Add(coverage.Workload + ": " + coverage.Message);
            }

            if (overview.DemographicsTruncated)
                overview.Messages.Add("Department or country breakdowns are limited to the 50 largest values.");

            diagnostics.Stage("ProjectionCompleted");
            return overview;
        }

        private async Task<LicenceActivityUsers> ReadUsersAsync(
            SqlDataReader reader,
            LicenceActivityOverview overview,
            LicenceActivityQuery query,
            ILicenceActivityDiagnostics diagnostics,
            CancellationToken cancellationToken)
        {
            var result = new LicenceActivityUsers
            {
                OverviewId = overview.SnapshotId,
                Query = query
            };

            await RequireResultAsync(reader, true, cancellationToken, "TotalUsers", "RankedUsers")
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("The licence activity user summary was not returned.");
            result.TotalUsers = ReadInt32(reader, "TotalUsers");
            result.RankedUsers = ReadInt32(reader, "RankedUsers");

            await RequireResultAsync(
                reader, false, cancellationToken, "ListKind", "Ordinal", "UserId", "TeamsActiveSamples")
                .ConfigureAwait(false);
            var coverage = overview.Coverage.ToDictionary(c => c.Workload, StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var user = ReadUser(reader, coverage);
                switch (ReadInt32(reader, "ListKind"))
                {
                    case 1:
                        result.MostActive.Add(user);
                        break;
                    case 2:
                        result.LeastActive.Add(user);
                        break;
                    case 3:
                        result.Users.Add(user);
                        break;
                }
            }
            await DrainRemainingResultsAsync(reader, cancellationToken).ConfigureAwait(false);

            result.Messages.Add(
                "Most/least lists rank the selected workload by active supporting samples, then average actions and last activity. Complete positive measurements rank before partial positive evidence, which still ranks above measured zero; unknown or incomplete evidence is excluded from least-active.");

            LicenceActivityCoverage selectedCoverage;
            if (coverage.TryGetValue(query.Workload, out selectedCoverage)
                && selectedCoverage.Status != LicenceActivitySql.Available
                && !string.IsNullOrWhiteSpace(selectedCoverage.Message))
            {
                result.Messages.Add(selectedCoverage.Message);
            }
            if (result.TotalUsers > 0 && result.RankedUsers == 0)
                result.Messages.Add("No user has complete or positive evidence for the selected workload and range.");

            diagnostics.Stage("ProjectionCompleted");
            return result;
        }

        private static LicenceActivityUser ReadUser(
            SqlDataReader reader,
            IReadOnlyDictionary<string, LicenceActivityCoverage> coverage)
        {
            var user = new LicenceActivityUser
            {
                UserId = ReadInt32(reader, "UserId"),
                UserPrincipalName = ReadString(reader, "UserPrincipalName"),
                Department = ReadNullableString(reader, "Department"),
                Country = ReadNullableString(reader, "Country"),
                AccountEnabled = ReadNullableBoolean(reader, "AccountEnabled")
            };

            AddEvidence(user, reader, coverage, "teams", "Teams");
            AddEvidence(user, reader, coverage, "outlook", "Outlook");
            AddEvidence(user, reader, coverage, "onedrive", "OneDrive");
            AddEvidence(user, reader, coverage, "sharepoint", "SharePoint");
            AddEvidence(user, reader, coverage, "copilot", "Copilot");
            return user;
        }

        private static void AddEvidence(
            LicenceActivityUser user,
            SqlDataReader reader,
            IReadOnlyDictionary<string, LicenceActivityCoverage> coverageByWorkload,
            string workload,
            string columnPrefix)
        {
            LicenceActivityCoverage coverage;
            if (!coverageByWorkload.TryGetValue(workload, out coverage))
            {
                coverage = new LicenceActivityCoverage
                {
                    Workload = workload,
                    Status = LicenceActivitySql.MissingCoverage,
                    Source = string.Empty,
                    Measure = string.Empty
                };
            }

            var active = ReadInt32(reader, columnPrefix + "ActiveSamples");
            var status = coverage.Status ?? LicenceActivitySql.MissingCoverage;
            var observedSamples = coverage.ObservedSamples;
            if (coverage.Source == LicenceActivitySql.M365ReportSource
                || coverage.Source == LicenceActivitySql.CopilotReportSource)
            {
                var rowPresent = ReadBoolean(reader, columnPrefix + "RowPresent");
                observedSamples = ReadInt32(reader, columnPrefix + "ObservedSamples");
                if (status == LicenceActivitySql.Available)
                {
                    if (!rowPresent)
                        status = LicenceActivitySql.MissingCoverage;
                    else if (!ReadBoolean(reader, columnPrefix + "FrequencyKnown")
                        || observedSamples != coverage.ExpectedSamples)
                        status = LicenceActivitySql.Partial;
                }
            }

            user.Workloads.Add(new LicenceActivityEvidence
            {
                Workload = workload,
                Status = status,
                Band = status == LicenceActivitySql.Available
                    ? LicenceActivityRules.Band(active, observedSamples, coverage.ExpectedSamples)
                    : "unknown",
                Source = coverage.Source,
                Measure = coverage.Measure,
                ActiveSamples = active,
                ObservedSamples = observedSamples,
                ExpectedSamples = coverage.ExpectedSamples,
                AverageActions = ReadNullableDouble(reader, columnPrefix + "AverageActions"),
                LastActivityUtc = ReadNullableUtc(reader, columnPrefix + "LastActivityUtc")
            });
        }

        private async Task RequireResultAsync(
            SqlDataReader reader,
            bool includeCurrent,
            CancellationToken cancellationToken,
            params string[] requiredColumns)
        {
            var hasResult = includeCurrent || await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            while (hasResult)
            {
                if (HasColumns(reader, requiredColumns)) return;
                await CaptureShowplanAsync(reader, cancellationToken).ConfigureAwait(false);
                hasResult = await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "The licence activity SQL batch did not return the expected bounded result set.");
        }

        private async Task CaptureShowplanAsync(
            SqlDataReader reader,
            CancellationToken cancellationToken)
        {
            if (_instrumentation?.ShowplanReceived == null
                || reader.FieldCount != 1
                || (reader.GetName(0).IndexOf("Showplan", StringComparison.OrdinalIgnoreCase) < 0
                    && reader.GetFieldType(0) != typeof(SqlXml)))
                return;

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0))
                {
                    var value = reader.GetValue(0);
                    var xml = value is SqlXml
                        ? ((SqlXml)value).Value
                        : Convert.ToString(value);
                    _instrumentation.ShowplanReceived(xml);
                }
            }
        }

        private async Task DrainRemainingResultsAsync(
            SqlDataReader reader,
            CancellationToken cancellationToken)
        {
            while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                await CaptureShowplanAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        private static bool HasColumns(SqlDataReader reader, IEnumerable<string> columns)
        {
            if (reader.FieldCount == 0) return false;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                present.Add(reader.GetName(index));
            return columns.All(present.Contains);
        }

        private static LicenceActivityDistribution ReadDistribution(SqlDataReader reader)
        {
            return new LicenceActivityDistribution
            {
                Workload = ReadString(reader, "Workload"),
                High = ReadInt32(reader, "High"),
                Moderate = ReadInt32(reader, "Moderate"),
                Low = ReadInt32(reader, "Low"),
                Zero = ReadInt32(reader, "Zero"),
                Unknown = ReadInt32(reader, "Unknown")
            };
        }

        private static string DemographicKey(string dimension, int id)
        {
            return dimension + ":" + id;
        }

        private static string EscapeLikeValue(string value)
        {
            return (value ?? string.Empty)
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_")
                .Replace("[", @"\[");
        }

        private static void AddDate(SqlCommand command, string name, DateTime value)
        {
            command.Parameters.Add(name, SqlDbType.Date).Value = value.Date;
        }

        private static void AddNullableInt(SqlCommand command, string name, int? value)
        {
            command.Parameters.Add(name, SqlDbType.Int).Value =
                value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static int ReadInt32(SqlDataReader reader, string column)
        {
            return Convert.ToInt32(reader.GetValue(reader.GetOrdinal(column)));
        }

        private static int? ReadNullableInt32(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (int?)null : Convert.ToInt32(reader.GetValue(ordinal));
        }

        private static double? ReadNullableDouble(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (double?)null : Convert.ToDouble(reader.GetValue(ordinal));
        }

        private static bool ReadBoolean(SqlDataReader reader, string column)
        {
            return Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(column)));
        }

        private static bool? ReadNullableBoolean(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (bool?)null : Convert.ToBoolean(reader.GetValue(ordinal));
        }

        private static string ReadString(SqlDataReader reader, string column)
        {
            return Convert.ToString(reader.GetValue(reader.GetOrdinal(column)));
        }

        private static string ReadNullableString(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));
        }

        private static DateTime ReadUtc(SqlDataReader reader, string column)
        {
            return DateTime.SpecifyKind(
                Convert.ToDateTime(reader.GetValue(reader.GetOrdinal(column))),
                DateTimeKind.Utc);
        }

        private static DateTime? ReadNullableUtc(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal)
                ? (DateTime?)null
                : DateTime.SpecifyKind(Convert.ToDateTime(reader.GetValue(ordinal)), DateTimeKind.Utc);
        }

        private sealed class OverviewBase
        {
            internal int DistinctAssignedUsers;
            internal bool DemographicsTruncated;
            internal List<LicenceActivitySku> Licences { get; } = new List<LicenceActivitySku>();
            internal List<DemographicRow> Demographics { get; } = new List<DemographicRow>();
        }

        private sealed class OverviewPart
        {
            internal OverviewBase Base;
            internal LicenceActivityCoverage Coverage;
            internal List<LicenceDistributionRow> LicenceDistributions { get; } =
                new List<LicenceDistributionRow>();
            internal List<DemographicDistributionRow> DemographicDistributions { get; } =
                new List<DemographicDistributionRow>();
        }

        private sealed class OverviewProjectionResult
        {
            internal OverviewBase Base { get; } = new OverviewBase();
            internal List<LicenceDistributionRow> LicenceDistributions { get; } =
                new List<LicenceDistributionRow>();
            internal List<DemographicDistributionRow> DemographicDistributions { get; } =
                new List<DemographicDistributionRow>();
        }

        private sealed class DemographicRow
        {
            internal string Dimension;
            internal LicenceActivityDemographic Value;
        }

        private sealed class LicenceDistributionRow
        {
            internal int LicenceTypeId;
            internal LicenceActivityDistribution Distribution;
        }

        private sealed class DemographicDistributionRow
        {
            internal string Dimension;
            internal int Id;
            internal LicenceActivityDistribution Distribution;
        }
    }

    /// <summary>
    /// Test-only hook for collecting SQL Server STATISTICS IO/TIME and XML showplans from a scratch database.
    /// Production construction does not install one.
    /// </summary>
    internal sealed class SqlLicenceActivityStoreInstrumentation
    {
        internal Action<SqlConnection> ConnectionOpened { get; set; }
        internal Action<SqlConnection, string> ConnectionOpenedForOperation { get; set; }
        internal Action<string, long> OperationCompleted { get; set; }
        internal Action<string> ShowplanReceived { get; set; }
        internal int? CommandTimeoutSeconds { get; set; }
        internal Action<bool> CommandActiveChanged { get; set; }

        internal IDisposable TrackCommand()
        {
            return new CommandLifetime(CommandActiveChanged);
        }

        private sealed class CommandLifetime : IDisposable
        {
            private readonly Action<bool> _changed;

            internal CommandLifetime(Action<bool> changed)
            {
                _changed = changed;
                _changed?.Invoke(true);
            }

            public void Dispose()
            {
                _changed?.Invoke(false);
            }
        }
    }
}
