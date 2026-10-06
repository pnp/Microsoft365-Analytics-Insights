using DataUtils.Sql;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.ActivityAnalysis
{
    /// <summary>Where the Activity analysis page's figures come from - SQL Server in production, hand-built data in tests.</summary>
    public interface IActivityAnalysisSource
    {
        /// <summary>Whether the profiling tables exist, which metric columns they have, and which weeks are compiled.</summary>
        Task<ActivityAnalysisSchema> ReadSchemaAsync(CancellationToken cancellationToken);

        /// <summary>One period of the weekly table, for every person in <c>profiling.users</c>.</summary>
        /// <exception cref="ActivityAnalysisNotInstalledException">The profiling tables do not exist.</exception>
        Task<ActivityAnalysisReadModel> LoadReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken);

        /// <summary>Every available metric's weekly figures for exactly these people (ascending user ids).</summary>
        Task<ActivityAnalysisWeeklyTotals> LoadWeeklyTotalsAsync(
            ActivityAnalysisReadModel model, IReadOnlyList<int> userIds, CancellationToken cancellationToken);
    }

    /// <summary>The profiling tables a request needs do not exist: the runbooks' schema was never installed.</summary>
    public sealed class ActivityAnalysisNotInstalledException : Exception
    {
        public ActivityAnalysisNotInstalledException()
            : base("The weekly profiling tables are not installed in this database.") { }
    }

    /// <summary>
    /// Reads <c>profiling.ActivitiesWeeklyColumns</c>, <c>profiling.users</c> and the licence tables.
    /// </summary>
    /// <remarks>
    /// Never <c>profiling.ActivitiesWeekly</c>: that is the same data one row per user, week and metric - zeros included,
    /// some 600 million rows a year at 200,000 users - which the wide table answers in a fraction of the reads.
    /// No new index: the period is read with one statement (<see cref="ActivityAnalysisSql.BuildReadModel"/>), cached, and
    /// shared by every reader.
    /// </remarks>
    public sealed class SqlActivityAnalysisSource : IActivityAnalysisSource
    {
        private readonly string _connectionString;
        private readonly int _commandTimeoutSeconds;

        public SqlActivityAnalysisSource(string connectionString, int commandTimeoutSeconds = ActivityAnalysisSql.CommandTimeoutSeconds)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ArgumentException("A connection string to the Analytics database is required.", nameof(connectionString));
            }

            _connectionString = connectionString;
            _commandTimeoutSeconds = commandTimeoutSeconds;
        }

        public async Task<ActivityAnalysisSchema> ReadSchemaAsync(CancellationToken cancellationToken)
        {
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                return await ReadSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<ActivityAnalysisReadModel> LoadReadModelAsync(ActivityAnalysisPeriod period, CancellationToken cancellationToken)
        {
            if (period == null) throw new ArgumentNullException(nameof(period));

            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

                // The columns are read on every load, not taken from the cached availability: the query must name
                // only columns this database has, and an upgrade may have added some since.
                var schema = await ReadSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                if (!schema.Installed) throw new ActivityAnalysisNotInstalledException();

                var metrics = schema.AvailableMetrics;
                var builder = new ActivityAnalysisReadModelBuilder(period, metrics);
                var indexes = metrics.Select(m => m.Index).ToArray();
                var count = indexes.Length;

                using (var command = new SqlCommand(ActivityAnalysisSql.BuildReadModel(metrics), connection) { CommandTimeout = _commandTimeoutSeconds })
                {
                    AddPeriod(command, period);
                    using (cancellationToken.Register(command.Cancel))
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        // Read synchronously row by row: a per-row ReadAsync costs more than the row, and this runs on
                        // the shared load's own worker, never on a request's thread.
                        while (reader.Read())
                        {
                            if (reader.GetInt32(0) == 1)
                            {
                                if (reader.IsDBNull(2)) continue;
                                var week = period.WeekIndexOf(reader.GetDateTime(2));
                                if (week < 0) continue;
                                for (var i = 0; i < count; i++)
                                {
                                    var sum = reader.IsDBNull(3 + count + i) ? 0L : reader.GetInt64(3 + count + i);
                                    var active = reader.IsDBNull(3 + 2 * count + i) ? 0 : reader.GetInt32(3 + 2 * count + i);
                                    builder.AddWeek(week, indexes[i], sum, active);
                                }
                            }
                            else
                            {
                                if (reader.IsDBNull(1)) continue;
                                var userId = reader.GetInt64(1);
                                if (userId < int.MinValue || userId > int.MaxValue) continue;
                                var person = builder.AddPerson((int)userId);
                                for (var i = 0; i < count; i++)
                                {
                                    if (!reader.IsDBNull(3 + i)) builder.AddTotal(person, indexes[i], reader.GetInt32(3 + i));
                                }
                            }
                        }

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                        while (reader.Read())
                        {
                            builder.AddLicenceType(
                                reader.GetInt32(0),
                                reader.IsDBNull(1) ? null : reader.GetString(1),
                                reader.IsDBNull(2) ? null : reader.GetString(2));
                        }

                        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                        while (reader.Read())
                        {
                            builder.AddHolding(reader.GetInt32(0), reader.GetInt32(1));
                        }
                    }
                }

                return builder.Build(DateTime.UtcNow);
            }
        }

        public async Task<ActivityAnalysisWeeklyTotals> LoadWeeklyTotalsAsync(
            ActivityAnalysisReadModel model, IReadOnlyList<int> userIds, CancellationToken cancellationToken)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (userIds == null) throw new ArgumentNullException(nameof(userIds));

            var period = model.Period;
            var totals = new ActivityAnalysisWeeklyTotals(period.Weeks);
            var metrics = model.AvailableMetrics;
            if (userIds.Count == 0 || metrics.Count == 0) return totals;

            var count = metrics.Count;
            using (var connection = AzureSqlTokenAuth.CreateConnection(_connectionString))
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using (var command = new SqlCommand(ActivityAnalysisSql.BuildWeeklyTotals(metrics), connection) { CommandTimeout = _commandTimeoutSeconds })
                {
                    AddPeriod(command, period);
                    command.Parameters.Add(new SqlParameter("@people", SqlDbType.NVarChar, -1) { Value = ToJson(userIds) });
                    using (cancellationToken.Register(command.Cancel))
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                    {
                        while (reader.Read())
                        {
                            if (reader.IsDBNull(0)) continue;
                            var week = period.WeekIndexOf(reader.GetDateTime(0));
                            if (week < 0) continue;
                            for (var i = 0; i < count; i++)
                            {
                                var sum = reader.IsDBNull(1 + i) ? 0L : reader.GetInt64(1 + i);
                                var active = reader.IsDBNull(1 + count + i) ? 0 : reader.GetInt32(1 + count + i);
                                totals.Add(week, metrics[i].Index, sum, active);
                            }
                        }
                    }
                }
            }

            return totals;
        }

        private async Task<ActivityAnalysisSchema> ReadSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
        {
            using (var command = new SqlCommand(ActivityAnalysisSql.Schema, connection) { CommandTimeout = _commandTimeoutSeconds })
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || !reader.GetBoolean(0))
                {
                    return ActivityAnalysisSchema.NotInstalled;
                }

                var columns = new List<string>();
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) columns.Add(reader.GetString(0));

                DateTime? earliest = null;
                DateTime? latest = null;
                await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    earliest = reader.IsDBNull(0) ? (DateTime?)null : reader.GetDateTime(0);
                    latest = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1);
                }

                return new ActivityAnalysisSchema(true, columns, earliest, latest);
            }
        }

        private static void AddPeriod(SqlCommand command, ActivityAnalysisPeriod period)
        {
            command.Parameters.Add(new SqlParameter("@from", SqlDbType.Date) { Value = period.From });
            command.Parameters.Add(new SqlParameter("@to", SqlDbType.Date) { Value = period.To });
        }

        /// <summary>The ids as a JSON array of numbers - built from integers, so nothing in it can be anything else.</summary>
        internal static string ToJson(IReadOnlyList<int> userIds)
        {
            var json = new StringBuilder(userIds.Count * 7 + 2);
            json.Append('[');
            for (var i = 0; i < userIds.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append(userIds[i].ToString(CultureInfo.InvariantCulture));
            }

            return json.Append(']').ToString();
        }
    }
}
