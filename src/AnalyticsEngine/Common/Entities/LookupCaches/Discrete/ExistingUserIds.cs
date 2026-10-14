using Microsoft.Data.SqlClient;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.LookupCaches
{
    /// <summary>
    /// Finds which of a list of user principal names already have a row in <c>dbo.users</c>, and that row's id. For
    /// the writers that insert new users in batches - the user import's bulk insert and the Copilot per-user usage
    /// report - when a batch fails because another import created one of its users a moment earlier (#714).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database does the matching under the actual column collation, not an in-memory UPN comparer.
    /// SQL collations use varchar parameters (nvarchar would convert the column and prevent seeks).
    /// Windows collations use nvarchar: their Unicode comparison supports the cheaper range/merge plan
    /// measured at 200,000 synthetic users (#713). UPNs are ASCII by Entra policy; this does not change the
    /// varchar storage boundary or promise that non-ASCII identifiers round-trip through it.
    /// The column collation is probed once per server/catalog per process, not from the database default.
    /// </para>
    /// <para>
    /// Each match comes back by the name's POSITION in the list rather than by its text, so a caller never compares
    /// names again in memory, under a rule that could disagree with the one that raised the duplicate-key error.
    /// Where several rows share a name, which only a database missing <c>IX_users</c> allows, the lowest id wins,
    /// as it does in <see cref="UserCache.Load"/>.
    /// </para>
    /// <para>
    /// One query per <see cref="MaxNamesPerQuery"/> names, well under SQL Server's 2,100-parameter limit.
    /// </para>
    /// </remarks>
    public static class ExistingUserIds
    {
        /// <summary>The most names sent in one query; longer lists are split.</summary>
        public const int MaxNamesPerQuery = 1000;

        /// <summary>The width of <c>dbo.users.user_name</c>, which is <c>varchar(250)</c>.</summary>
        internal const int UserNameColumnLength = 250;

        /// <summary>Matches the user import's bulk-copy timeout, the operation this lookup recovers.</summary>
        private const int CommandTimeoutSeconds = 600;

        private const string CollationSql = "SELECT collation_name FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.users') AND name = N'user_name'";
        private static readonly ConcurrentDictionary<string, SqlDbType> ParameterTypes = new ConcurrentDictionary<string, SqlDbType>(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProbeGates = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);

        /// <summary>Looks the names up over a connection the caller has already opened.</summary>
        /// <returns>
        /// One entry per name, in the same order: the id of the row the database matched to that name, or null when
        /// there is none. A null name never matches.
        /// </returns>
        public static async Task<int?[]> FindAsync(SqlConnection openConnection, IReadOnlyList<string> userPrincipalNames)
        {
            if (openConnection == null)
            {
                throw new ArgumentNullException(nameof(openConnection));
            }

            if (userPrincipalNames == null) throw new ArgumentNullException(nameof(userPrincipalNames));
            if (userPrincipalNames.Count == 0) return new int?[0];
            var type = await ParameterTypeAsync(openConnection, async () =>
            {
                using (var command = new SqlCommand(CollationSql, openConnection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    return (string)await command.ExecuteScalarAsync();
                }
            });
            return await FindAsync(userPrincipalNames, type, async query =>
            {
                var matches = new List<Match>();
                using (var command = new SqlCommand(query.Sql, openConnection))
                {
                    command.CommandTimeout = CommandTimeoutSeconds;
                    command.Parameters.AddRange(query.Parameters);
                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            matches.Add(new Match { Slot = reader.GetInt32(0), Id = reader.GetInt32(1) });
                        }
                    }
                }
                return matches;
            });
        }

        /// <summary>
        /// Looks the names up through an EF context, which opens its own connection the way it always does, Entra
        /// access token included.
        /// </summary>
        /// <returns>As <see cref="FindAsync(SqlConnection, IReadOnlyList{string})"/>.</returns>
        public static async Task<int?[]> FindAsync(AnalyticsEntitiesContext db, IReadOnlyList<string> userPrincipalNames)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            if (userPrincipalNames == null) throw new ArgumentNullException(nameof(userPrincipalNames));
            if (userPrincipalNames.Count == 0) return new int?[0];
            var type = await ParameterTypeAsync(db.Database.Connection, async () =>
                await db.Database.SqlQuery<string>(CollationSql).SingleAsync());
            return await FindAsync(userPrincipalNames, type, async query =>
                await db.Database.SqlQuery<Match>(query.Sql, query.Parameters).ToListAsync());
        }

        /// <summary>
        /// Resolves a scope to every matching enabled (or unknown-status) user id. Unlike <see cref="FindAsync(AnalyticsEntitiesContext, IReadOnlyList{string})"/>,
        /// this is a set of eligible rows, not a lowest-id answer per name: legacy duplicates must not hide an
        /// enabled account behind a disabled one, or discard another eligible row's watermark.
        /// </summary>
        public static async Task<int[]> FindEnabledIdsAsync(AnalyticsEntitiesContext db, IReadOnlyList<string> userPrincipalNames)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (userPrincipalNames == null) throw new ArgumentNullException(nameof(userPrincipalNames));
            if (userPrincipalNames.Count == 0) return new int[0];
            var type = await ParameterTypeAsync(db.Database.Connection, async () =>
                await db.Database.SqlQuery<string>(CollationSql).SingleAsync());
            var ids = new HashSet<int>();
            for (var offset = 0; offset < userPrincipalNames.Count; offset += MaxNamesPerQuery)
            {
                var query = BuildQuery(userPrincipalNames, offset,
                    Math.Min(MaxNamesPerQuery, userPrincipalNames.Count - offset), type, allEnabledMatches: true);
                if (query == null) continue;
                foreach (var match in await db.Database.SqlQuery<Match>(query.Sql, query.Parameters).ToListAsync())
                    ids.Add(match.Id);
            }
            return ids.ToArray();
        }

        private static async Task<SqlDbType> ParameterTypeAsync(System.Data.Common.DbConnection connection, Func<Task<string>> probe)
        {
            // An omitted initial catalog can resolve differently for different logins. Do not cache that
            // unresolved address; the open-connection entry point already has the actual catalog.
            if (string.IsNullOrEmpty(connection.Database))
                return TypeForCollation(await probe());

            var key = connection.DataSource.Length + ":" + connection.DataSource + connection.Database;
            if (ParameterTypes.TryGetValue(key, out var type)) return type;
            var gate = ProbeGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync();
            try
            {
                if (!ParameterTypes.TryGetValue(key, out type))
                {
                    type = TypeForCollation(await probe());
                    ParameterTypes[key] = type;
                }
                return type;
            }
            finally { gate.Release(); }
        }

        internal static SqlDbType TypeForCollation(string collation)
        {
            if (string.IsNullOrEmpty(collation))
                throw new InvalidOperationException("Cannot determine dbo.users.user_name collation.");
            return collation.StartsWith("SQL_", StringComparison.OrdinalIgnoreCase) ? SqlDbType.VarChar : SqlDbType.NVarChar;
        }

        private static async Task<int?[]> FindAsync(IReadOnlyList<string> userPrincipalNames, SqlDbType type, Func<Query, Task<List<Match>>> run)
        {
            if (userPrincipalNames == null)
            {
                throw new ArgumentNullException(nameof(userPrincipalNames));
            }

            var ids = new int?[userPrincipalNames.Count];
            for (var offset = 0; offset < userPrincipalNames.Count; offset += MaxNamesPerQuery)
            {
                var query = BuildQuery(userPrincipalNames, offset, Math.Min(MaxNamesPerQuery, userPrincipalNames.Count - offset), type);
                if (query == null)
                {
                    continue;
                }

                foreach (var match in await run(query))
                {
                    ids[offset + match.Slot] = match.Id;
                }
            }
            return ids;
        }

        /// <summary>
        /// The query for <paramref name="count"/> names starting at <paramref name="offset"/>, or null when none of
        /// them can match. Slots are relative to <paramref name="offset"/>, so every full slice sends the same text.
        /// </summary>
        internal static Query BuildQuery(IReadOnlyList<string> userPrincipalNames, int offset, int count, SqlDbType type = SqlDbType.VarChar, bool allEnabledMatches = false)
        {
            var sql = new StringBuilder(allEnabledMatches
                ? "SELECT k.slot AS Slot, u.id AS Id FROM (VALUES "
                : "SELECT k.slot AS Slot, MIN(u.id) AS Id FROM (VALUES ");
            var parameters = new List<SqlParameter>(count);
            for (var slot = 0; slot < count; slot++)
            {
                var name = userPrincipalNames[offset + slot];
                if (name == null)
                {
                    continue;
                }

                var parameterName = "@n" + slot;
                if (parameters.Count > 0)
                {
                    sql.Append(',');
                }
                sql.Append('(').Append(slot).Append(',').Append(parameterName).Append(')');
                parameters.Add(UserNameParameter(parameterName, name, type));
            }

            if (parameters.Count == 0)
            {
                return null;
            }

            sql.Append(") AS k(slot, upn) INNER JOIN dbo.users AS u ON u.user_name = k.upn");
            sql.Append(allEnabledMatches
                ? " WHERE u.account_enabled IS NULL OR u.account_enabled = 1 GROUP BY k.slot, u.id;"
                : " GROUP BY k.slot;");
            return new Query(sql.ToString(), parameters.ToArray());
        }

        /// <summary>
        /// A parameter sized for its type, and never smaller than its value: SqlClient silently
        /// truncates a value to its parameter's size, and a truncated name could match somebody else.
        /// </summary>
        internal static SqlParameter UserNameParameter(string parameterName, string value, SqlDbType type = SqlDbType.VarChar)
        {
            var normalSize = type == SqlDbType.VarChar ? UserNameColumnLength : 4000;
            var maxSize = type == SqlDbType.VarChar ? 8000 : 4000;
            var size = value.Length <= normalSize ? normalSize : (value.Length <= maxSize ? value.Length : -1);
            return new SqlParameter(parameterName, type, size) { Value = value };
        }

        internal sealed class Query
        {
            public Query(string sql, SqlParameter[] parameters)
            {
                Sql = sql;
                Parameters = parameters;
            }

            public string Sql { get; }

            public SqlParameter[] Parameters { get; }
        }

        /// <summary>One row of the lookup's result. Public only so EF can materialise it.</summary>
        public sealed class Match
        {
            /// <summary>The name's position in its query, relative to the slice it was sent in.</summary>
            public int Slot { get; set; }

            /// <summary>A matching id; <c>FindAsync</c> returns only the lowest per name.</summary>
            public int Id { get; set; }
        }
    }
}
