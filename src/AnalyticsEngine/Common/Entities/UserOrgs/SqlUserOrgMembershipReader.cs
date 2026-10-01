using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Entities.UserOrgs
{
    /// <summary>
    /// SQL Server implementation of <see cref="IUserOrgMembershipReader"/>.
    /// </summary>
    /// <remarks>
    /// Both queries are shaped around <c>IX_user_org_assignments_value</c> -
    /// <c>(org_type_id, org_value_id) INCLUDE (user_id)</c> - which exists for exactly this question.
    /// Organisation sizes come from one ordered range of that index per org type, aggregated without a
    /// sort. A membership page seeks one organisation's range, pages its users, and only then looks up
    /// department and job title - for the rows actually returned, not for every member.
    /// </remarks>
    internal sealed class SqlUserOrgMembershipReader : SqlUserOrgStoreBase, IUserOrgMembershipReader
    {
        /// <summary>
        /// Every organisation in one org type with its size, largest first.
        /// </summary>
        /// <remarks>
        /// Counted before joining the names, so the aggregate works on integer keys straight off the
        /// index. Organisations with nobody in them are listed with zero rather than hidden: the page's
        /// "Distinct values" figure counts them, so hiding them here would make the two disagree.
        /// </remarks>
        internal const string ValuesSql = @"
SET NOCOUNT ON;

WITH counts AS (
    SELECT a.org_value_id, COUNT_BIG(*) AS members
    FROM dbo.user_org_assignments a
    WHERE a.org_type_id = @orgTypeId
    GROUP BY a.org_value_id
)
SELECT v.id, v.name, ISNULL(c.members, 0) AS member_count
FROM dbo.user_org_values v
LEFT JOIN counts c ON c.org_value_id = v.id
WHERE v.org_type_id = @orgTypeId
  AND (@pattern IS NULL OR v.name LIKE @pattern ESCAPE N'\')
ORDER BY member_count DESC, v.name ASC, v.id ASC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
OPTION (RECOMPILE);

SELECT COUNT_BIG(*)
FROM dbo.user_org_values v
WHERE v.org_type_id = @orgTypeId
  AND (@pattern IS NULL OR v.name LIKE @pattern ESCAPE N'\')
OPTION (RECOMPILE);";

        /// <summary>
        /// One page of one organisation's users, by user principal name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The organisation is looked up by id <b>and</b> org type, so a value id from one type can never
        /// list the members of another. <c>OPTION (RECOMPILE)</c> because the optional search makes this a
        /// catch-all query: compiled once for "no search", a cached plan would be reused for a search and
        /// the other way round. The compile is trivial next to an admin clicking through a list.
        /// </para>
        /// <para>
        /// The escape character is a <c>varchar</c> literal on purpose: <c>user_name</c> is
        /// <c>varchar(250)</c>, and an <c>N'\'</c> here would drag the comparison back to Unicode even
        /// when the pattern is sent as <c>varchar</c>. See <see cref="AddSearchAndPaging"/>.
        /// </para>
        /// </remarks>
        internal const string MembersSql = @"
SET NOCOUNT ON;

DECLARE @valueName NVARCHAR(848) =
    (SELECT v.name FROM dbo.user_org_values v WHERE v.id = @orgValueId AND v.org_type_id = @orgTypeId);

SELECT @valueName AS value_name;

IF @valueName IS NULL RETURN;

-- Page the users first, then look up department and job title for that page alone. Joined before the
-- paging, both lookups would run for every member of a large organisation only for OFFSET/FETCH to
-- throw almost all of them away.
WITH page AS (
    SELECT u.id, u.user_name, u.account_enabled, u.department_id, u.job_title_id
    FROM dbo.user_org_assignments a
    JOIN dbo.users u ON u.id = a.user_id
    WHERE a.org_type_id = @orgTypeId
      AND a.org_value_id = @orgValueId
      AND (@pattern IS NULL OR u.user_name LIKE @pattern ESCAPE '\')
    ORDER BY u.user_name ASC, u.id ASC
    OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
)
SELECT p.id, p.user_name, p.account_enabled, d.name AS department, j.name AS job_title
FROM page p
LEFT JOIN dbo.user_departments d ON d.id = p.department_id
LEFT JOIN dbo.user_job_titles j ON j.id = p.job_title_id
ORDER BY p.user_name ASC, p.id ASC
OPTION (RECOMPILE);

SELECT COUNT_BIG(*)
FROM dbo.user_org_assignments a
JOIN dbo.users u ON u.id = a.user_id
WHERE a.org_type_id = @orgTypeId
  AND a.org_value_id = @orgValueId
  AND (@pattern IS NULL OR u.user_name LIKE @pattern ESCAPE '\')
OPTION (RECOMPILE);";

        public SqlUserOrgMembershipReader(string connectionString) : base(connectionString)
        {
        }

        public async Task<UserOrgValuePage> GetValuesAsync(
            int orgTypeId,
            string search,
            int skip,
            int take,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var page = new UserOrgValuePage();
            var values = new List<UserOrgValueCount>();

            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, ValuesSql))
            {
                cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId;
                AddSearchAndPaging(cmd, search, skip, take, matchesVarcharColumn: false);

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        values.Add(new UserOrgValueCount
                        {
                            Id = reader.GetInt32(0),
                            Name = reader.GetString(1),
                            MemberCount = ToInt(reader.GetInt64(2)),
                        });
                    }

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        page.TotalCount = ToInt(reader.GetInt64(0));
                    }
                }
            }

            page.Values = values;
            return page;
        }

        public async Task<UserOrgMemberPage> GetMembersAsync(
            int orgTypeId,
            int orgValueId,
            string search,
            int skip,
            int take,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            using (var connection = await OpenAsync(cancellationToken).ConfigureAwait(false))
            using (var cmd = Command(connection, MembersSql))
            {
                cmd.Parameters.Add("@orgTypeId", SqlDbType.Int).Value = orgTypeId;
                cmd.Parameters.Add("@orgValueId", SqlDbType.Int).Value = orgValueId;
                AddSearchAndPaging(cmd, search, skip, take, matchesVarcharColumn: true);

                using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
                    {
                        return null;
                    }

                    var page = new UserOrgMemberPage { OrgValueId = orgValueId, OrgValueName = reader.GetString(0) };
                    var members = new List<UserOrgMember>();

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        members.Add(new UserOrgMember
                        {
                            UserId = reader.GetInt32(0),
                            UserPrincipalName = reader.GetString(1),
                            AccountEnabled = reader.IsDBNull(2) ? (bool?)null : reader.GetBoolean(2),
                            Department = ReadString(reader, 3),
                            JobTitle = ReadString(reader, 4),
                        });
                    }

                    await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
                    if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        page.TotalCount = ToInt(reader.GetInt64(0));
                    }

                    page.Members = members;
                    return page;
                }
            }
        }

        /// <summary>
        /// Turns a search term into a LIKE pattern that matches it anywhere, and literally, or <c>null</c>
        /// when there is nothing to search for.
        /// </summary>
        /// <remarks>
        /// <c>%</c>, <c>_</c> and <c>[</c> are wildcards to LIKE, and all three turn up in real
        /// organisation names: "50% FTE", "Sales_EMEA", "[Legacy] Finance". Unescaped, a search for
        /// "50%" would also find "500 Club", and one for "[Legacy]" would match a single letter.
        /// </remarks>
        internal static string ContainsPattern(string search)
        {
            var term = UserOrgRules.NormaliseSearch(search);
            if (term == null)
            {
                return null;
            }

            var pattern = new StringBuilder(term.Length + 8).Append('%');
            foreach (var c in term)
            {
                if (c == '\\' || c == '%' || c == '_' || c == '[')
                {
                    pattern.Append('\\');
                }

                pattern.Append(c);
            }

            return pattern.Append('%').ToString();
        }

        /// <summary>Adds the search pattern and the page window the two queries share.</summary>
        /// <param name="matchesVarcharColumn">
        /// Whether the pattern is matched against <c>dbo.users.user_name</c>, which is <c>varchar</c>
        /// because an Entra UPN is ASCII by policy.
        /// </param>
        /// <remarks>
        /// <para>
        /// Against that column an ASCII pattern is sent as <c>varchar</c>. As <c>nvarchar</c> it would
        /// convert every candidate UPN to Unicode and compare with Unicode rules, twice per request (the
        /// page and its count). Measured on a synthetic 100,000-person organisation, a UPN search took
        /// 1,372 ms that way and 457 ms as <c>varchar</c>, with identical logical reads and results.
        /// </para>
        /// <para>
        /// A pattern with anything outside ASCII stays <c>nvarchar</c>. Converted to <c>varchar</c> it
        /// would be best-fit mapped - "josé" becoming "jose" - and match UPNs it does not; left as
        /// Unicode it correctly matches nothing, because no UPN can contain it.
        /// </para>
        /// </remarks>
        private static void AddSearchAndPaging(SqlCommand cmd, string search, int skip, int take, bool matchesVarcharColumn)
        {
            var pattern = ContainsPattern(search);
            var asVarchar = matchesVarcharColumn && pattern != null && pattern.All(c => c < 128);

            // Sized for the worst case - every character escaped, plus the two wildcards - so a long
            // search is never silently truncated by the parameter.
            cmd.Parameters.Add(
                "@pattern",
                asVarchar ? SqlDbType.VarChar : SqlDbType.NVarChar,
                (UserOrgRules.MaxSearchLength * 2) + 2).Value = pattern == null ? (object)DBNull.Value : pattern;
            cmd.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, skip);
            cmd.Parameters.Add("@take", SqlDbType.Int).Value = Math.Max(1, take);
        }

        private static int ToInt(long value)
        {
            return (int)Math.Min(value, int.MaxValue);
        }
    }
}
