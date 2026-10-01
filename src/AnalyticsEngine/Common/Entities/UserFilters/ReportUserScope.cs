using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Common.Entities.UserFilters
{
    /// <summary>
    /// The people a report may describe, as <c>dbo.users</c> ids - the form the reports that aggregate in
    /// SQL need. <see cref="Everyone"/> when no filter applies.
    /// </summary>
    /// <remarks>
    /// Built from compiled filters (<see cref="Matching"/>), so a SQL report narrows to exactly the people
    /// the in-memory reports do: one definition of who a filter matches, evaluated once, against one
    /// directory. Somebody imported after that directory was read is in no restricted scope - nothing is
    /// known about them yet, the same rule as <see cref="CompiledUserFilter.Matches"/>.
    /// </remarks>
    public sealed class ReportUserScope
    {
        /// <summary>No restriction: the whole tenant.</summary>
        public static readonly ReportUserScope Everyone = new ReportUserScope(null, "all");

        private readonly int[] _userIds;

        private ReportUserScope(int[] sortedUserIds, string key)
        {
            _userIds = sortedUserIds;
            Key = key;
        }

        /// <summary>False for <see cref="Everyone"/>.</summary>
        public bool IsRestricted => _userIds != null;

        /// <summary>How many people a restricted scope holds. Zero for <see cref="Everyone"/>.</summary>
        public int Count => _userIds?.Length ?? 0;

        /// <summary>
        /// A short, stable identity for cache keys: the same for every scope holding the same people, so
        /// two readers the filters treat identically share cached results.
        /// </summary>
        public string Key { get; }

        /// <summary>The ids, ascending. Empty for <see cref="Everyone"/>.</summary>
        public IReadOnlyList<int> UserIds => _userIds ?? new int[0];

        public bool Includes(int userId)
        {
            return _userIds == null || Array.BinarySearch(_userIds, userId) >= 0;
        }

        /// <summary>Exactly these people. An empty list is a scope nobody is in, which is not <see cref="Everyone"/>.</summary>
        public static ReportUserScope ForUsers(IEnumerable<int> userIds)
        {
            var ids = (userIds ?? Enumerable.Empty<int>()).Distinct().ToArray();
            Array.Sort(ids);
            return new ReportUserScope(ids, "users:" + ids.Length.ToString(CultureInfo.InvariantCulture) + ":" + Hash(ids));
        }

        /// <summary>
        /// The people every one of the filters matches. Null filters are skipped, so no filter at all is
        /// <see cref="Everyone"/>. The filters must have been compiled against the same directory snapshot.
        /// </summary>
        public static ReportUserScope Matching(params CompiledUserFilter[] filters)
        {
            var active = (filters ?? new CompiledUserFilter[0]).Where(f => f != null).ToArray();
            if (active.Length == 0) return Everyone;

            var snapshot = active[0].Snapshot;
            if (active.Any(f => !ReferenceEquals(f.Snapshot, snapshot)))
            {
                throw new ArgumentException("Filters compiled against different directory snapshots cannot be combined.", nameof(filters));
            }

            var ids = new List<int>();
            for (var row = 0; row < snapshot.PeopleCount; row++)
            {
                var all = true;
                for (var i = 0; i < active.Length && all; i++) all = active[i].MatchesRow(row);
                if (all) ids.Add(snapshot.UserIdAt(row));
            }

            return ForUsers(ids);
        }

        /// <summary>The ids as a JSON array, the form <see cref="ReportScopeSql"/> sends them to SQL Server in.</summary>
        public string ToJson()
        {
            if (_userIds == null) return null;

            var builder = new StringBuilder(_userIds.Length * 7 + 2);
            builder.Append('[');
            for (var i = 0; i < _userIds.Length; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(_userIds[i].ToString(CultureInfo.InvariantCulture));
            }

            return builder.Append(']').ToString();
        }

        private static string Hash(int[] ids)
        {
            var bytes = new byte[ids.Length * 4];
            Buffer.BlockCopy(ids, 0, bytes, 0, bytes.Length);
            using (var sha = SHA256.Create())
            {
                return Convert.ToBase64String(sha.ComputeHash(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
        }
    }

    /// <summary>A report statement was about to run under a restricted scope without saying how it is narrowed.</summary>
    public sealed class ReportScopeNotAppliedException : InvalidOperationException
    {
        public ReportScopeNotAppliedException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Narrows a report's SQL to a <see cref="ReportUserScope"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Markers, not rewriting.</b> Each statement says where and how it is narrowed with a comment:
    /// <c>/*scope: AND s.user_id IN {scopeUsers}*/</c>. Unrestricted, the markers are removed and the
    /// statement is byte for byte what it was before scopes existed - same text, same cached plan, no
    /// cost for a tenant without a filter. Restricted, each marker's text is uncommented with
    /// <see cref="UsersPlaceholder"/> pointing at a temporary table of the scope's ids, filled at the top
    /// of the same batch from one JSON parameter. A temporary table rather than a table variable, so the
    /// optimiser has statistics on how many people the scope holds.</para>
    /// <para><b>Fails closed.</b> A statement run under a restricted scope with no marker at all throws
    /// <see cref="ReportScopeNotAppliedException"/> rather than running tenant-wide: a statement somebody
    /// forgot to narrow is an error in a test, never data from outside the scope on somebody's screen. A
    /// statement that is tenant-wide by design - a count of teams, an "is there any data" probe - says so
    /// with <see cref="TenantWideMarker"/>.</para>
    /// </remarks>
    public static class ReportScopeSql
    {
        /// <summary>The parameter carrying the scope's ids, as a JSON array.</summary>
        public const string ParameterName = "scopeUsers";

        /// <summary>Stands for the scope's ids inside a marker - a parenthesised subquery once applied.</summary>
        public const string UsersPlaceholder = "{scopeUsers}";

        /// <summary>Declares a statement tenant-wide by design, so it may run under a restricted scope unchanged.</summary>
        public const string TenantWideMarker = "/*scope:none*/";

        private const string MarkerOpen = "/*scope:";
        private const string MarkerClose = "*/";
        private const string UsersTable = "#report_scope_users";
        private const string UsersSubquery = "(SELECT scope_users.user_id FROM " + UsersTable + " AS scope_users)";

        /// <summary>
        /// Fills the scope's temporary table. Prepended once to a statement that uses it.
        /// </summary>
        /// <remarks>
        /// No <c>DISTINCT</c>: <see cref="ReportUserScope"/> holds each id once, already sorted, and the primary
        /// key would refuse a duplicate rather than let it through. Measured at 200,000 ids the sort it added
        /// was a sixth of the cost of filling the table.
        /// </remarks>
        internal const string Prelude =
            "SET NOCOUNT ON;\r\n"
            + "IF OBJECT_ID(N'tempdb.." + UsersTable + "') IS NOT NULL DROP TABLE " + UsersTable + ";\r\n"
            + "CREATE TABLE " + UsersTable + " (user_id int NOT NULL PRIMARY KEY);\r\n"
            + "INSERT INTO " + UsersTable + " (user_id) SELECT CAST([value] AS int) FROM OPENJSON(@" + ParameterName + ");\r\n";

        /// <summary>True when the statement carries at least one scope marker.</summary>
        public static bool HasMarkers(string sql)
        {
            return sql != null && sql.IndexOf(MarkerOpen, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// The statement narrowed to <paramref name="scope"/>, or with its markers removed when the scope is
        /// <see cref="ReportUserScope.Everyone"/>.
        /// </summary>
        /// <exception cref="ReportScopeNotAppliedException">A restricted scope, and a statement with no marker.</exception>
        public static string Apply(string sql, ReportUserScope scope)
        {
            if (sql == null) throw new ArgumentNullException(nameof(sql));

            var restricted = scope != null && scope.IsRestricted;
            if (restricted && !HasMarkers(sql))
            {
                throw new ReportScopeNotAppliedException(
                    "A report statement has no user scope marker, so it cannot be narrowed to the people this "
                    + "reader may see. It was not run. Add a /*scope:...*/ marker, or /*scope:none*/ if the "
                    + "statement is tenant-wide by design.");
            }

            var builder = new StringBuilder(sql.Length + (restricted ? Prelude.Length + 256 : 0));
            var usesUsers = false;
            var position = 0;

            while (true)
            {
                var open = sql.IndexOf(MarkerOpen, position, StringComparison.Ordinal);
                if (open < 0) break;

                var close = sql.IndexOf(MarkerClose, open + MarkerOpen.Length, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new ArgumentException("A scope marker is not closed.", nameof(sql));
                }

                builder.Append(sql, position, open - position);

                var fragment = sql.Substring(open + MarkerOpen.Length, close - open - MarkerOpen.Length);
                if (restricted && !string.Equals(fragment.Trim(), "none", StringComparison.Ordinal))
                {
                    if (fragment.IndexOf(UsersPlaceholder, StringComparison.Ordinal) >= 0) usesUsers = true;
                    builder.Append(fragment.Replace(UsersPlaceholder, UsersSubquery));
                }

                position = close + MarkerClose.Length;
            }

            builder.Append(sql, position, sql.Length - position);

            return usesUsers ? Prelude + builder : builder.ToString();
        }

        /// <summary>
        /// The parameter to run an applied statement with, or <c>null</c> when the scope is unrestricted and
        /// the statement needs none.
        /// </summary>
        public static SqlParameter CreateParameter(ReportUserScope scope)
        {
            if (scope == null || !scope.IsRestricted) return null;

            return new SqlParameter(ParameterName, SqlDbType.NVarChar, -1) { Value = scope.ToJson() };
        }

        /// <summary>
        /// The lines a report's "show the SQL" text declares the scope with - nothing for an unrestricted
        /// scope. The ids themselves are left out: there can be hundreds of thousands, and the popover is for
        /// reading how a figure was produced, not for carrying the population across.
        /// </summary>
        public static IEnumerable<string> DescribeScope(ReportUserScope scope)
        {
            if (scope == null || !scope.IsRestricted) yield break;

            yield return string.Format(
                CultureInfo.InvariantCulture,
                "-- Narrowed to the {0:N0} people the report's filters cover. Their ids are not shown here; set @{1} to them as a JSON array to reproduce the figures.",
                scope.Count,
                ParameterName);
            yield return "DECLARE @" + ParameterName + " nvarchar(max) = N'[]';";
        }

        /// <summary>
        /// <see cref="DescribeScope(ReportUserScope)"/> for one statement as it was applied: nothing when the
        /// statement does not use the scope - one that is tenant-wide by design - so its displayed SQL never
        /// claims a narrowing it did not have.
        /// </summary>
        public static IEnumerable<string> DescribeScope(ReportUserScope scope, string appliedSql)
        {
            if (appliedSql == null || appliedSql.IndexOf("@" + ParameterName, StringComparison.Ordinal) < 0)
            {
                return Enumerable.Empty<string>();
            }

            return DescribeScope(scope);
        }

        /// <summary>The statement's parameters with the scope's added when it needs one.</summary>
        public static SqlParameter[] WithScope(SqlParameter[] parameters, ReportUserScope scope)
        {
            var scopeParameter = CreateParameter(scope);
            if (scopeParameter == null) return parameters ?? new SqlParameter[0];

            var list = new List<SqlParameter>(parameters ?? new SqlParameter[0]) { scopeParameter };
            return list.ToArray();
        }
    }
}
