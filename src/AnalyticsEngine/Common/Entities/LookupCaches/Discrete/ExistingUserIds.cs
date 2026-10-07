using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Entity;
using System.Text;
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
    /// The database does the matching, with the comparison the unique index <c>IX_users</c> itself uses: each name
    /// is a <c>varchar</c> parameter, like <c>user_name</c>, compared with <c>=</c> under the column's collation
    /// (case-insensitive by default, trailing spaces ignored). An <c>nvarchar</c> parameter would make SQL Server
    /// convert the column instead, and under a SQL collation - the Azure SQL Database default - that comparison
    /// cannot seek <c>IX_users</c> (#713). <c>user_name</c> is <c>varchar</c> by design: UPNs are ASCII by Entra
    /// policy (#402).
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

        /// <summary>Looks the names up over a connection the caller has already opened.</summary>
        /// <returns>
        /// One entry per name, in the same order: the id of the row the database matched to that name, or null when
        /// there is none. A null name never matches.
        /// </returns>
        public static Task<int?[]> FindAsync(SqlConnection openConnection, IReadOnlyList<string> userPrincipalNames)
        {
            if (openConnection == null)
            {
                throw new ArgumentNullException(nameof(openConnection));
            }

            return FindAsync(userPrincipalNames, async query =>
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
        public static Task<int?[]> FindAsync(AnalyticsEntitiesContext db, IReadOnlyList<string> userPrincipalNames)
        {
            if (db == null)
            {
                throw new ArgumentNullException(nameof(db));
            }

            return FindAsync(userPrincipalNames, async query =>
                await db.Database.SqlQuery<Match>(query.Sql, query.Parameters).ToListAsync());
        }

        private static async Task<int?[]> FindAsync(IReadOnlyList<string> userPrincipalNames, Func<Query, Task<List<Match>>> run)
        {
            if (userPrincipalNames == null)
            {
                throw new ArgumentNullException(nameof(userPrincipalNames));
            }

            var ids = new int?[userPrincipalNames.Count];
            for (var offset = 0; offset < userPrincipalNames.Count; offset += MaxNamesPerQuery)
            {
                var query = BuildQuery(userPrincipalNames, offset, Math.Min(MaxNamesPerQuery, userPrincipalNames.Count - offset));
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
        internal static Query BuildQuery(IReadOnlyList<string> userPrincipalNames, int offset, int count)
        {
            var sql = new StringBuilder("SELECT k.slot AS Slot, MIN(u.id) AS Id FROM (VALUES ");
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
                parameters.Add(UserNameParameter(parameterName, name));
            }

            if (parameters.Count == 0)
            {
                return null;
            }

            sql.Append(") AS k(slot, upn) INNER JOIN dbo.users AS u ON u.user_name = k.upn GROUP BY k.slot;");
            return new Query(sql.ToString(), parameters.ToArray());
        }

        /// <summary>
        /// A <c>varchar</c> parameter sized to the column, and never smaller than its value: SqlClient silently
        /// truncates a value to its parameter's size, and a truncated name could match somebody else.
        /// </summary>
        internal static SqlParameter UserNameParameter(string parameterName, string value)
        {
            var size = value.Length <= UserNameColumnLength
                ? UserNameColumnLength
                : (value.Length <= 8000 ? value.Length : -1);
            return new SqlParameter(parameterName, SqlDbType.VarChar, size) { Value = value };
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

            /// <summary>The lowest id of the rows the database matched to the name.</summary>
            public int Id { get; set; }
        }
    }
}
