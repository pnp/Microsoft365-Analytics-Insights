using Common.Entities;
using Common.Entities.CopilotAdoption;
using Common.Entities.UserDiagnostics;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserDiagnostics
{
    /// <summary>A <c>dbo.users</c> row as the diagnostics need it.</summary>
    public sealed class StoredUserRow
    {
        public int Id { get; set; }
        public string UserPrincipalName { get; set; }
        public string Mail { get; set; }
        public string AzureAdId { get; set; }
        public bool? AccountEnabled { get; set; }
        public DateTime? LastUpdated { get; set; }
    }

    /// <summary>A user's licence history and the refresh runs that bound it.</summary>
    public sealed class LicenceHistoryRead
    {
        public bool Available { get; set; }
        public DateTime? TrackingBeganUtc { get; set; }
        public DateTime? LastRefreshUtc { get; set; }
        public List<LicenceHistoryRowModel> Rows { get; set; } = new List<LicenceHistoryRowModel>();
        public bool Truncated { get; set; }
    }

    /// <summary>The newest stored row of one coverage category for one user.</summary>
    public sealed class LatestStoredRow
    {
        public DateTime? LatestUtc { get; set; }

        /// <summary>Usage reports only: the last-activity date Microsoft reported on that row.</summary>
        public DateTime? LastActivityDateUtc { get; set; }
    }

    /// <summary>
    /// The database reads behind the per-user diagnostics. Every read is keyed by one user id or is a tenant-wide
    /// aggregate over the small <c>license_types</c> table, so none of them scans a fact table.
    /// </summary>
    public interface IUserDiagnosticsQuery : IReportVerdictQuery
    {
        Task<StoredUserRow> FindUserAsync(string upn);

        /// <summary>The licence types the user holds in <c>user_license_type_lookups</c>.</summary>
        Task<List<StoredLicenceType>> StoredLicenceTypesForUserAsync(int userId);

        Task<LicenceHistoryRead> HistoryAsync(int userId, int take);

        /// <summary>The newest stored row of <paramref name="categoryKey"/> for the user.</summary>
        Task<LatestStoredRow> LatestAsync(int userId, string categoryKey);
    }

    /// <summary>EF/SQL implementation of <see cref="IUserDiagnosticsQuery"/>.</summary>
    public sealed class SqlUserDiagnosticsQuery : IUserDiagnosticsQuery
    {
        private const int CommandTimeoutSecs = 60;

        internal const string HistoryBoundsSql =
            "IF OBJECT_ID(N'dbo.license_refresh_runs', N'U') IS NULL OR OBJECT_ID(N'dbo.user_license_history', N'U') IS NULL\r\n" +
            "    SELECT CAST(0 AS bit) AS Available, CAST(NULL AS datetime2) AS TrackingBeganUtc, CAST(NULL AS datetime2) AS LastRefreshUtc;\r\n" +
            "ELSE\r\n" +
            "    SELECT CAST(1 AS bit) AS Available, MIN(completed_utc) AS TrackingBeganUtc, MAX(completed_utc) AS LastRefreshUtc FROM dbo.license_refresh_runs;";

        /// <summary>Open holdings first, then most recent. Seeks <c>IX_user_license_history_user_from</c>.</summary>
        internal const string HistoryRowsSql =
            "SELECT TOP (@take) h.license_type_id AS LicenceTypeId, lt.name AS Name, lt.sku_id AS SkuPartNumber,\r\n" +
            "       h.valid_from_utc AS ValidFromUtc, h.valid_from_previous_refresh_utc AS ValidFromPreviousRefreshUtc,\r\n" +
            "       h.valid_to_utc AS ValidToUtc, h.valid_to_previous_refresh_utc AS ValidToPreviousRefreshUtc,\r\n" +
            "       h.from_source AS FromSource\r\n" +
            "FROM dbo.user_license_history AS h\r\n" +
            "JOIN dbo.license_types AS lt ON lt.id = h.license_type_id\r\n" +
            "WHERE h.user_id = @userId\r\n" +
            "ORDER BY CASE WHEN h.valid_to_utc IS NULL THEN 0 ELSE 1 END, COALESCE(h.valid_to_utc, h.valid_from_utc) DESC, h.license_type_id;";

        private readonly IAnalyticsDbContextFactory _contextFactory;

        public SqlUserDiagnosticsQuery() : this(DefaultAnalyticsDbContextFactory.Instance)
        {
        }

        public SqlUserDiagnosticsQuery(IAnalyticsDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public async Task<StoredUserRow> FindUserAsync(string upn)
        {
            using (var db = _contextFactory.Create())
            {
                // Equality on the indexed, case-insensitive user_name - never ToLower(), which would scan.
                return await db.users
                    .Where(u => u.UserPrincipalName == upn)
                    .Select(u => new StoredUserRow
                    {
                        Id = u.ID,
                        UserPrincipalName = u.UserPrincipalName,
                        Mail = u.Mail,
                        AzureAdId = u.AzureAdId,
                        AccountEnabled = u.AccountEnabled,
                        LastUpdated = u.LastUpdated,
                    })
                    .FirstOrDefaultAsync();
            }
        }

        public async Task<List<StoredLicenceType>> StoredLicenceTypesForUserAsync(int userId)
        {
            using (var db = _contextFactory.Create())
            {
                return await db.UserLicenseTypeLookups
                    .Where(l => l.UserId == userId)
                    .Select(l => new StoredLicenceType { Id = l.License.ID, Name = l.License.Name, SkuPartNumber = l.License.SKUID })
                    .ToListAsync();
            }
        }

        public async Task<List<LicenceTypeRow>> LicenceTypesAsync()
        {
            using (var db = _contextFactory.Create())
            {
                db.Database.CommandTimeout = CommandTimeoutSecs;
                return await db.Database.SqlQuery<LicenceTypeRow>(CopilotAdoptionSql.LicenceTypesSql).ToListAsync();
            }
        }

        public async Task<DateTime?> LicenceHistoryStartAsync()
        {
            using (var db = _contextFactory.Create())
            {
                return await db.Database.SqlQuery<DateTime?>(CopilotAdoptionSql.LicenceHistoryStartSql).FirstOrDefaultAsync();
            }
        }

        public async Task<List<SeatEvidenceRow>> SeatEvidenceAsync(int userId, IReadOnlyList<int> licenceTypeIds, bool useLicenceHistory,
            DateTime fromUtc, DateTime toExclusiveUtc, DateTime? historyStartUtc)
        {
            var sql = CopilotAdoptionSql.UserSeatEvidenceSql(licenceTypeIds, useLicenceHistory);
            var parameters = new List<object> { new SqlParameter("@userId", SqlDbType.Int) { Value = userId } };
            if (useLicenceHistory)
            {
                parameters.Add(new SqlParameter("@from", SqlDbType.DateTime2) { Value = fromUtc });
                parameters.Add(new SqlParameter("@toExclusive", SqlDbType.DateTime2) { Value = toExclusiveUtc });
                parameters.Add(new SqlParameter("@historyStart", SqlDbType.DateTime2) { Value = historyStartUtc.Value });
            }

            using (var db = _contextFactory.Create())
            {
                db.Database.CommandTimeout = CommandTimeoutSecs;
                return await db.Database.SqlQuery<SeatEvidenceRow>(sql, parameters.ToArray()).ToListAsync();
            }
        }

        public async Task<LicenceHistoryRead> HistoryAsync(int userId, int take)
        {
            using (var db = _contextFactory.Create())
            {
                db.Database.CommandTimeout = CommandTimeoutSecs;
                var bounds = await db.Database.SqlQuery<HistoryBounds>(HistoryBoundsSql).FirstOrDefaultAsync();
                var result = new LicenceHistoryRead
                {
                    Available = bounds?.Available == true,
                    TrackingBeganUtc = AsUtc(bounds?.TrackingBeganUtc),
                    LastRefreshUtc = AsUtc(bounds?.LastRefreshUtc),
                };
                if (!result.Available)
                {
                    return result;
                }

                var rows = await db.Database.SqlQuery<HistoryRow>(HistoryRowsSql,
                        new SqlParameter("@take", SqlDbType.Int) { Value = take + 1 },
                        new SqlParameter("@userId", SqlDbType.Int) { Value = userId })
                    .ToListAsync();
                result.Truncated = rows.Count > take;
                result.Rows = rows.Take(take).Select(r => new LicenceHistoryRowModel
                {
                    LicenceTypeId = r.LicenceTypeId,
                    Name = r.Name,
                    SkuPartNumber = r.SkuPartNumber,
                    ValidFromUtc = DateTime.SpecifyKind(r.ValidFromUtc, DateTimeKind.Utc),
                    ValidFromPreviousRefreshUtc = AsUtc(r.ValidFromPreviousRefreshUtc),
                    ValidToUtc = AsUtc(r.ValidToUtc),
                    ValidToPreviousRefreshUtc = AsUtc(r.ValidToPreviousRefreshUtc),
                    HeldBeforeTrackingBegan = Common.Entities.Licences.LicenceHistorySources.HeldBeforeTrackingBegan(r.FromSource, r.ValidFromUtc, bounds?.TrackingBeganUtc),
                    RecordedByUserRefresh = r.FromSource == Common.Entities.Licences.LicenceHistorySources.UserRefresh,
                }).ToList();
                return result;
            }
        }

        public async Task<LatestStoredRow> LatestAsync(int userId, string categoryKey)
        {
            if (categoryKey == UserCoverageRules.CopilotUsageReportCategory)
            {
                using (var db = _contextFactory.Create())
                {
                    var latest = await db.CopilotUsageUserActivityLogs
                        .Where(x => x.UserID == userId)
                        .OrderByDescending(x => x.Date)
                        .Select(x => new { x.Date, x.LastActivityDate })
                        .FirstOrDefaultAsync();
                    return new LatestStoredRow { LatestUtc = AsUtc(latest?.Date), LastActivityDateUtc = AsUtc(latest?.LastActivityDate) };
                }
            }

            // Every other category is the existing drill-down's newest row, so "latest" here always matches the
            // first row an administrator sees when they open the category.
            var rows = await new UserDataLookup.SqlUserDataLookupQuery(_contextFactory).GetRowsForCategoryAsync(userId, categoryKey, 1);
            var row = rows?.FirstOrDefault();
            return new LatestStoredRow { LatestUtc = AsUtc(row?.Timestamp), LastActivityDateUtc = AsUtc(row?.DetailDateUtc) };
        }

        private static DateTime? AsUtc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : (DateTime?)null;

        private sealed class HistoryBounds
        {
            public bool Available { get; set; }
            public DateTime? TrackingBeganUtc { get; set; }
            public DateTime? LastRefreshUtc { get; set; }
        }

        private sealed class HistoryRow
        {
            public int LicenceTypeId { get; set; }
            public string Name { get; set; }
            public string SkuPartNumber { get; set; }
            public DateTime ValidFromUtc { get; set; }
            public DateTime? ValidFromPreviousRefreshUtc { get; set; }
            public DateTime? ValidToUtc { get; set; }
            public DateTime? ValidToPreviousRefreshUtc { get; set; }
            public byte FromSource { get; set; }
        }
    }
}
