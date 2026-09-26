using Common.Entities.UserOrgs;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Web.AnalyticsWeb.Models.UserOrgs
{
    /// <summary>
    /// "Who is in each organisation?" for the User organisations admin page: an org type's organisations
    /// with their sizes, and the users in any one of them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="UserOrgAdminService"/> because it is read-only and needs only two ports,
    /// which keeps it testable without standing up the whole configuration and import surface. The page
    /// rules live here; the SQL lives behind <see cref="IUserOrgMembershipReader"/>.
    /// </remarks>
    public sealed class UserOrgMembershipService
    {
        /// <summary>Organisations per page when the caller does not say.</summary>
        public const int DefaultValuesPageSize = 25;

        /// <summary>Users per page when the caller does not say.</summary>
        public const int DefaultMembersPageSize = 50;

        /// <summary>Most rows either list returns in one page, however many are asked for.</summary>
        public const int MaxPageSize = 200;

        /// <summary>
        /// Highest page number honoured. The cap keeps <c>(page - 1) * pageSize</c> inside an
        /// <c>int</c> whatever arrives on the query string; a million pages of 200 is far more than any
        /// organisation can hold, so no real page is refused.
        /// </summary>
        public const int MaxPage = 1000000;

        private readonly IUserOrgTypeStore _types;
        private readonly IUserOrgMembershipReader _reader;

        public UserOrgMembershipService(IUserOrgTypeStore types, IUserOrgMembershipReader reader)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        }

        /// <summary>
        /// One page of an org type's organisations, largest first, or <c>null</c> when the org type does
        /// not exist.
        /// </summary>
        /// <remarks>
        /// The type is checked separately because an org type with no organisations yet - an Entra type
        /// waiting for its first import - is a legitimate empty list, not a missing one.
        /// </remarks>
        public async Task<UserOrgValuePageModel> ListValuesAsync(
            int orgTypeId,
            string search,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            var type = await _types.GetAsync(orgTypeId, cancellationToken).ConfigureAwait(false);
            if (type == null)
            {
                return null;
            }

            int number, size;
            ResolvePage(page, pageSize, DefaultValuesPageSize, out number, out size);

            var result = await _reader
                .GetValuesAsync(orgTypeId, search, (number - 1) * size, size, cancellationToken)
                .ConfigureAwait(false);

            return new UserOrgValuePageModel
            {
                OrgTypeId = orgTypeId,
                Page = number,
                PageSize = size,
                Total = result.TotalCount,
                Items = result.Values
                    .Select(v => new UserOrgValueRowModel { Id = v.Id, Name = v.Name, MemberCount = v.MemberCount })
                    .ToList(),
            };
        }

        /// <summary>
        /// One page of the users in an organisation, or <c>null</c> when the organisation does not exist
        /// or belongs to a different org type.
        /// </summary>
        public async Task<UserOrgMemberPageModel> ListMembersAsync(
            int orgTypeId,
            int orgValueId,
            string search,
            int page,
            int pageSize,
            CancellationToken cancellationToken)
        {
            int number, size;
            ResolvePage(page, pageSize, DefaultMembersPageSize, out number, out size);

            var result = await _reader
                .GetMembersAsync(orgTypeId, orgValueId, search, (number - 1) * size, size, cancellationToken)
                .ConfigureAwait(false);

            if (result == null)
            {
                return null;
            }

            return new UserOrgMemberPageModel
            {
                OrgTypeId = orgTypeId,
                ValueId = result.OrgValueId,
                ValueName = result.OrgValueName,
                Page = number,
                PageSize = size,
                Total = result.TotalCount,
                Items = result.Members
                    .Select(m => new UserOrgMemberModel
                    {
                        UserId = m.UserId,
                        UserPrincipalName = m.UserPrincipalName,
                        Department = m.Department,
                        JobTitle = m.JobTitle,
                        AccountEnabled = m.AccountEnabled,
                    })
                    .ToList(),
            };
        }

        /// <summary>
        /// Clamps a requested page to what the lists allow: pages from 1 to <see cref="MaxPage"/>, sizes
        /// from 1 to <see cref="MaxPageSize"/>, and <paramref name="defaultPageSize"/> when none is given.
        /// </summary>
        internal static void ResolvePage(int page, int pageSize, int defaultPageSize, out int number, out int size)
        {
            size = pageSize < 1 ? defaultPageSize : Math.Min(pageSize, MaxPageSize);
            number = Math.Min(Math.Max(page, 1), MaxPage);
        }
    }
}
