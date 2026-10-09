using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Common.Entities.Agent365
{
    public class Agent365Package
    {
        public string PackageId { get; set; }
        public string AgentIdentityId { get; set; }
        public string DisplayName { get; set; }
        public string PackageType { get; set; }
        public string Platform { get; set; }
        public string Publisher { get; set; }
        public string ManifestId { get; set; }
        public string Version { get; set; }
        public bool? IsBlocked { get; set; }
        public DateTime? LastModifiedUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }
        public bool LastUsedDateTimeProvided { get; set; }
        public int? ActiveUsers { get; set; }
        public int? TotalSessions { get; set; }
        public double? TotalRunTimeHours { get; set; }
        public double? ExceptionRate { get; set; }
        public string SupportedHostsJson { get; set; }
        public List<Agent365PackageElement> Elements { get; set; } = new List<Agent365PackageElement>();
    }

    public class Agent365PackageElement
    {
        public string ElementType { get; set; }
        public string ElementId { get; set; }
    }

    public class Agent365CatalogImportHealth
    {
        public DateTime? LastAttemptUtc { get; set; }
        public DateTime? LastAttemptCompletedUtc { get; set; }
        public bool? LastAttemptSucceeded { get; set; }
        public string LastAttemptError { get; set; }
        public DateTime? LastSuccessfulImportUtc { get; set; }
        public Guid? LatestSuccessfulRunId { get; set; }
        public int PackageCount { get; set; }
        public int NeverUsedCount { get; set; }
    }

    public class Agent365PackageCatalogPage
    {
        public int TotalCount { get; set; }
        public IReadOnlyList<Agent365Package> Packages { get; set; } = new List<Agent365Package>();
    }

    public interface IAgent365PackageCatalogStore
    {
        Task<Guid> BeginImportAsync(DateTime startedUtc);
        Task SavePageAsync(Guid runId, IReadOnlyList<Agent365Package> packages);
        Task CompleteImportAsync(Guid runId, DateTime completedUtc, int packageCount, int elementCount);
        Task FailImportAsync(Guid runId, DateTime completedUtc, string error);
        Task<Agent365CatalogImportHealth> GetImportHealthAsync();
        Task<Agent365PackageCatalogPage> GetCurrentPageAsync(int offset, int pageSize, bool neverUsedOnly);
    }

    /// <summary>
    /// Persists a complete Package Management API snapshot without changing the EF model. Pages are
    /// written under a new run id; only a fully completed run becomes visible to readers.
    /// </summary>
    public class Agent365PackageCatalogStore : IAgent365PackageCatalogStore
    {
        private readonly IAnalyticsDbContextFactory _contextFactory;

        public Agent365PackageCatalogStore(IAnalyticsDbContextFactory contextFactory = null)
        {
            _contextFactory = contextFactory ?? DefaultAnalyticsDbContextFactory.Instance;
        }

        public async Task<Guid> BeginImportAsync(DateTime startedUtc)
        {
            var runId = Guid.NewGuid();
            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "INSERT INTO dbo.copilot_agent_catalog_import_log " +
                    "(run_id, started_utc, is_success) VALUES (@runId, @startedUtc, 0);",
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId },
                    new SqlParameter("@startedUtc", SqlDbType.DateTime2) { Value = startedUtc });
                transaction.Commit();
            }

            return runId;
        }

        public async Task SavePageAsync(Guid runId, IReadOnlyList<Agent365Package> packages)
        {
            if (packages == null) throw new ArgumentNullException(nameof(packages));

            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction())
            {
                foreach (var package in packages)
                {
                    await db.Database.ExecuteSqlCommandAsync(
                        "INSERT INTO dbo.copilot_agent_packages " +
                        "(import_run_id, package_id, agent_identity_id, display_name, package_type, platform, publisher, " +
                        "manifest_id, version, is_blocked, last_modified_utc, last_used_utc, last_used_datetime_provided, " +
                        "active_users, total_sessions, total_run_time_hours, exception_rate, supported_hosts_json) " +
                        "VALUES (@runId, @packageId, @agentIdentityId, @displayName, @packageType, @platform, @publisher, " +
                        "@manifestId, @version, @isBlocked, @lastModifiedUtc, @lastUsedUtc, @lastUsedProvided, " +
                        "@activeUsers, @totalSessions, @totalRunTimeHours, @exceptionRate, @supportedHostsJson);",
                        new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId },
                        NVarChar("@packageId", 450, package.PackageId),
                        NVarChar("@agentIdentityId", 450, package.AgentIdentityId),
                        NVarChar("@displayName", 1000, package.DisplayName),
                        NVarChar("@packageType", 100, package.PackageType),
                        NVarChar("@platform", 200, package.Platform),
                        NVarChar("@publisher", 1000, package.Publisher),
                        NVarChar("@manifestId", 450, package.ManifestId),
                        NVarChar("@version", 100, package.Version),
                        new SqlParameter("@isBlocked", SqlDbType.Bit) { Value = (object)package.IsBlocked ?? DBNull.Value },
                        DateTimeParameter("@lastModifiedUtc", package.LastModifiedUtc),
                        DateTimeParameter("@lastUsedUtc", package.LastUsedUtc),
                        new SqlParameter("@lastUsedProvided", SqlDbType.Bit) { Value = package.LastUsedDateTimeProvided },
                        new SqlParameter("@activeUsers", SqlDbType.Int) { Value = (object)package.ActiveUsers ?? DBNull.Value },
                        new SqlParameter("@totalSessions", SqlDbType.Int) { Value = (object)package.TotalSessions ?? DBNull.Value },
                        new SqlParameter("@totalRunTimeHours", SqlDbType.Float) { Value = (object)package.TotalRunTimeHours ?? DBNull.Value },
                        new SqlParameter("@exceptionRate", SqlDbType.Float) { Value = (object)package.ExceptionRate ?? DBNull.Value },
                        NVarChar("@supportedHostsJson", -1, package.SupportedHostsJson));

                    foreach (var element in package.Elements ?? new List<Agent365PackageElement>())
                    {
                        await db.Database.ExecuteSqlCommandAsync(
                            "INSERT INTO dbo.copilot_agent_package_elements " +
                            "(import_run_id, package_id, element_type, element_id) VALUES (@runId, @packageId, @elementType, @elementId);",
                            new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId },
                            NVarChar("@packageId", 450, package.PackageId),
                            NVarChar("@elementType", 100, element.ElementType),
                            NVarChar("@elementId", 450, element.ElementId));
                    }
                }

                transaction.Commit();
            }
        }

        public async Task CompleteImportAsync(Guid runId, DateTime completedUtc, int packageCount, int elementCount)
        {
            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "UPDATE dbo.copilot_agent_catalog_import_log " +
                    "SET completed_utc = @completedUtc, is_success = 1, package_count = @packageCount, " +
                    "element_count = @elementCount, error = NULL WHERE run_id = @runId;",
                    new SqlParameter("@completedUtc", SqlDbType.DateTime2) { Value = completedUtc },
                    new SqlParameter("@packageCount", SqlDbType.Int) { Value = packageCount },
                    new SqlParameter("@elementCount", SqlDbType.Int) { Value = elementCount },
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });

                await db.Database.ExecuteSqlCommandAsync(
                    "DELETE elements " +
                    "FROM dbo.copilot_agent_package_elements elements " +
                    "INNER JOIN dbo.copilot_agent_catalog_import_log runs ON runs.run_id = elements.import_run_id " +
                    "WHERE elements.import_run_id <> @runId AND runs.completed_utc IS NOT NULL;",
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
                await db.Database.ExecuteSqlCommandAsync(
                    "DELETE packages " +
                    "FROM dbo.copilot_agent_packages packages " +
                    "INNER JOIN dbo.copilot_agent_catalog_import_log runs ON runs.run_id = packages.import_run_id " +
                    "WHERE packages.import_run_id <> @runId AND runs.completed_utc IS NOT NULL;",
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
                transaction.Commit();
            }
        }

        public async Task FailImportAsync(Guid runId, DateTime completedUtc, string error)
        {
            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction())
            {
                await db.Database.ExecuteSqlCommandAsync(
                    "DELETE FROM dbo.copilot_agent_package_elements WHERE import_run_id = @runId;",
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
                await db.Database.ExecuteSqlCommandAsync(
                    "DELETE FROM dbo.copilot_agent_packages WHERE import_run_id = @runId;",
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
                await db.Database.ExecuteSqlCommandAsync(
                    "UPDATE dbo.copilot_agent_catalog_import_log " +
                    "SET completed_utc = @completedUtc, is_success = 0, error = @error WHERE run_id = @runId;",
                    new SqlParameter("@completedUtc", SqlDbType.DateTime2) { Value = completedUtc },
                    NVarChar("@error", 1000, error),
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId });
                transaction.Commit();
            }
        }

        public async Task<Agent365CatalogImportHealth> GetImportHealthAsync()
        {
            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction(IsolationLevel.RepeatableRead))
            {
                const string sql =
                    "SELECT " +
                    "(SELECT TOP (1) started_utc FROM dbo.copilot_agent_catalog_import_log ORDER BY started_utc DESC, run_id DESC) AS LastAttemptUtc, " +
                    "(SELECT TOP (1) completed_utc FROM dbo.copilot_agent_catalog_import_log ORDER BY started_utc DESC, run_id DESC) AS LastAttemptCompletedUtc, " +
                    "(SELECT TOP (1) CAST(is_success AS bit) FROM dbo.copilot_agent_catalog_import_log ORDER BY started_utc DESC, run_id DESC) AS LastAttemptSucceeded, " +
                    "(SELECT TOP (1) error FROM dbo.copilot_agent_catalog_import_log ORDER BY started_utc DESC, run_id DESC) AS LastAttemptError, " +
                    "(SELECT TOP (1) completed_utc FROM dbo.copilot_agent_catalog_import_log WHERE is_success = 1 ORDER BY completed_utc DESC, run_id DESC) AS LastSuccessfulImportUtc, " +
                    "(SELECT TOP (1) run_id FROM dbo.copilot_agent_catalog_import_log WHERE is_success = 1 ORDER BY completed_utc DESC, run_id DESC) AS LatestSuccessfulRunId;";
                var latest = (await db.Database.SqlQuery<Agent365CatalogImportHealth>(sql).ToListAsync()).FirstOrDefault()
                    ?? new Agent365CatalogImportHealth();

                if (latest.LatestSuccessfulRunId.HasValue)
                {
                    var runId = new SqlParameter("@runId", SqlDbType.UniqueIdentifier)
                    {
                        Value = latest.LatestSuccessfulRunId.Value
                    };
                    latest.PackageCount = (await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.copilot_agent_packages WHERE import_run_id = @runId;", runId)
                        .ToListAsync()).FirstOrDefault();
                    latest.NeverUsedCount = (await db.Database.SqlQuery<int>(
                        "SELECT COUNT(*) FROM dbo.copilot_agent_packages " +
                        "WHERE import_run_id = @runId AND last_used_datetime_provided = 1 AND last_used_utc IS NULL;",
                        new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = latest.LatestSuccessfulRunId.Value })
                        .ToListAsync()).FirstOrDefault();
                }
                transaction.Commit();
                return latest;
            }
        }

        public async Task<Agent365PackageCatalogPage> GetCurrentPageAsync(int offset, int pageSize, bool neverUsedOnly)
        {
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (pageSize < 1 || pageSize > 500) throw new ArgumentOutOfRangeException(nameof(pageSize));

            using (var db = _contextFactory.Create())
            using (var transaction = db.Database.BeginTransaction(IsolationLevel.RepeatableRead))
            {
                var runId = (await db.Database.SqlQuery<Guid?>(
                    "SELECT TOP (1) run_id FROM dbo.copilot_agent_catalog_import_log " +
                    "WHERE is_success = 1 ORDER BY completed_utc DESC, run_id DESC;")
                    .ToListAsync()).FirstOrDefault();
                if (!runId.HasValue)
                {
                    transaction.Commit();
                    return new Agent365PackageCatalogPage();
                }

                var runParameter = new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId.Value };
                var neverUsedFilter = neverUsedOnly
                    ? " AND last_used_datetime_provided = 1 AND last_used_utc IS NULL"
                    : string.Empty;
                var count = (await db.Database.SqlQuery<int>(
                    "SELECT COUNT(*) FROM dbo.copilot_agent_packages WHERE import_run_id = @runId" + neverUsedFilter + ";",
                    runParameter)
                    .ToListAsync()).FirstOrDefault();
                var sql =
                    "SELECT p.package_id AS PackageId, p.agent_identity_id AS AgentIdentityId, " +
                    "p.display_name AS DisplayName, p.package_type AS PackageType, p.platform AS Platform, " +
                    "p.publisher AS Publisher, p.manifest_id AS ManifestId, p.version AS Version, p.is_blocked AS IsBlocked, " +
                    "p.last_modified_utc AS LastModifiedUtc, p.last_used_utc AS LastUsedUtc, " +
                    "p.last_used_datetime_provided AS LastUsedDateTimeProvided, p.active_users AS ActiveUsers, " +
                    "p.total_sessions AS TotalSessions, p.total_run_time_hours AS TotalRunTimeHours, " +
                    "p.exception_rate AS ExceptionRate, p.supported_hosts_json AS SupportedHostsJson " +
                    "FROM dbo.copilot_agent_packages p WHERE p.import_run_id = @runId" +
                    (neverUsedOnly ? " AND p.last_used_datetime_provided = 1 AND p.last_used_utc IS NULL " : " ") +
                    "ORDER BY p.display_name, p.package_id OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;";
                var packages = await db.Database.SqlQuery<Agent365Package>(
                    sql,
                    new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId.Value },
                    new SqlParameter("@offset", SqlDbType.Int) { Value = offset },
                    new SqlParameter("@pageSize", SqlDbType.Int) { Value = pageSize }).ToListAsync();

                if (packages.Count > 0)
                {
                    var elementParameters = new List<SqlParameter>
                    {
                        new SqlParameter("@runId", SqlDbType.UniqueIdentifier) { Value = runId.Value }
                    };
                    var packageParameters = new List<string>();
                    for (var i = 0; i < packages.Count; i++)
                    {
                        var parameterName = "@package" + i;
                        packageParameters.Add(parameterName);
                        elementParameters.Add(NVarChar(parameterName, 450, packages[i].PackageId));
                    }

                    var elementsSql =
                        "SELECT e.package_id AS PackageId, e.element_type AS ElementType, e.element_id AS ElementId " +
                        "FROM dbo.copilot_agent_package_elements e " +
                        "WHERE e.import_run_id = @runId AND e.package_id IN (" + string.Join(", ", packageParameters) + ") " +
                        "ORDER BY e.package_id, e.element_type, e.element_id;";
                    var elements = await db.Database.SqlQuery<Agent365PackageElementRow>(
                        elementsSql, elementParameters.Cast<object>().ToArray()).ToListAsync();
                    var elementsByPackage = elements.GroupBy(e => e.PackageId)
                        .ToDictionary(g => g.Key, g => (IList<Agent365PackageElement>)g
                            .Select(e => new Agent365PackageElement { ElementType = e.ElementType, ElementId = e.ElementId })
                            .ToList(), StringComparer.Ordinal);
                    foreach (var package in packages)
                    {
                        if (elementsByPackage.TryGetValue(package.PackageId, out var packageElements))
                        {
                            package.Elements = packageElements.ToList();
                        }
                    }
                }

                transaction.Commit();
                return new Agent365PackageCatalogPage { TotalCount = count, Packages = packages };
            }
        }

        private static SqlParameter NVarChar(string name, int size, string value)
        {
            return new SqlParameter(name, SqlDbType.NVarChar, size)
            {
                Value = (object)value ?? DBNull.Value
            };
        }

        private static SqlParameter DateTimeParameter(string name, DateTime? value)
        {
            return new SqlParameter(name, SqlDbType.DateTime2)
            {
                Value = (object)value ?? DBNull.Value
            };
        }

        public class Agent365PackageElementRow : Agent365PackageElement
        {
            public Agent365PackageElementRow()
            {
            }

            public string PackageId { get; set; }
        }
    }
}
