using Common.Entities.Config;
using Common.Entities.UserScope;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Tests.UnitTests.FakeLoaderClasses
{
    /// <summary>Builds <see cref="UserImportScope"/>s and providers for tests, with no Graph behind them.</summary>
    public static class TestUserScopes
    {
        /// <summary>A filtered scope containing exactly these UPNs (object ids are generated).</summary>
        public static UserImportScope Of(params string[] userPrincipalNames)
        {
            var members = new UserScopeMembers();
            foreach (var upn in userPrincipalNames ?? Array.Empty<string>())
            {
                members.Add(Guid.NewGuid().ToString(), upn, upn);
            }
            return UserImportScope.ForMembers(members, "test scope");
        }

        /// <summary>A filtered scope of members given as (object id, UPN, mail).</summary>
        public static UserImportScope OfMembers(params (string ObjectId, string Upn, string Mail)[] people)
        {
            var members = new UserScopeMembers();
            foreach (var person in people ?? Array.Empty<(string, string, string)>())
            {
                members.Add(person.ObjectId, person.Upn, person.Mail);
            }
            return UserImportScope.ForMembers(members, "test scope");
        }

        /// <summary>A provider that always answers with <paramref name="scope"/> (fail-open view) and a matching resolution.</summary>
        public static FixedUserImportScopeProvider Provider(UserImportScope scope, UserImportScopeResolution resolution = null)
            => new FixedUserImportScopeProvider(scope, resolution);
    }

    /// <summary>
    /// <see cref="IUserImportScopeProvider"/> returning fixed answers, counting how often each view is asked for.
    /// </summary>
    public class FixedUserImportScopeProvider : IUserImportScopeProvider
    {
        private readonly UserImportScope _scope;
        private readonly UserImportScopeResolution _resolution;

        public FixedUserImportScopeProvider(UserImportScope scope, UserImportScopeResolution resolution = null, UserGroupsFilterModel filter = null)
        {
            _scope = scope ?? UserImportScope.Unfiltered;
            _resolution = resolution ?? UserImportScopeResolution.Unfiltered(DateTime.UtcNow);
            Filter = filter ?? new UserGroupsFilterModel(_scope.IsFiltered ? "Test group" : string.Empty);
        }

        public UserGroupsFilterModel Filter { get; }
        public int ScopeRequests { get; private set; }
        public int ResolutionRequests { get; private set; }

        public Task<UserImportScope> GetScopeAsync()
        {
            ScopeRequests++;
            return Task.FromResult(_scope);
        }

        public Task<UserImportScopeResolution> GetResolutionAsync()
        {
            ResolutionRequests++;
            return Task.FromResult(_resolution);
        }

        public Task<UserImportScopeResolution> RefreshAsync() => GetResolutionAsync();
    }

    /// <summary>
    /// In-memory <see cref="WebJob.Office365ActivityImporter.Engine.Graph.IUserImportScopeMarkerStore"/>: which
    /// <c>UserGroupsFilter</c> the stored delta token was taken under.
    /// </summary>
    public class InMemoryUserImportScopeMarkerStore : WebJob.Office365ActivityImporter.Engine.Graph.IUserImportScopeMarkerStore
    {
        public string Fingerprint { get; set; }

        public Task<string> GetFingerprintAsync() => Task.FromResult(Fingerprint);

        public Task SetFingerprintAsync(string fingerprint)
        {
            Fingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// In-memory <see cref="IGroupDirectoryReader"/>: groups by id, and their direct user members, paged.
    /// </summary>
    public class FakeGroupDirectoryReader : IGroupDirectoryReader
    {
        private readonly List<DirectoryGroup> _groups = new List<DirectoryGroup>();
        private readonly Dictionary<string, List<DirectoryUser>> _members = new Dictionary<string, List<DirectoryUser>>(StringComparer.OrdinalIgnoreCase);

        public int PageSize { get; set; } = 2;
        public int Calls { get; private set; }

        /// <summary>When set, every read throws this.</summary>
        public Exception FailWith { get; set; }

        /// <summary>When set, reading members of this group id throws.</summary>
        public string FailMembersOfGroupId { get; set; }

        public FakeGroupDirectoryReader AddGroup(string id, string displayName, params DirectoryUser[] members)
        {
            _groups.Add(new DirectoryGroup { Id = id, DisplayName = displayName });
            _members[id] = new List<DirectoryUser>(members);
            return this;
        }

        public static DirectoryUser User(string id, string upn, string mail = null, bool enabled = true)
            => new DirectoryUser { Id = id, UserPrincipalName = upn, Mail = mail ?? upn, AccountEnabled = enabled };

        public Task<DirectoryGroup> GetGroupByIdAsync(Guid groupId)
        {
            Calls++;
            if (FailWith != null) throw FailWith;
            return Task.FromResult(_groups.Find(g => string.Equals(g.Id, groupId.ToString(), StringComparison.OrdinalIgnoreCase)));
        }

        public Task<IReadOnlyList<DirectoryGroup>> FindGroupsByDisplayNameAsync(string displayName)
        {
            Calls++;
            if (FailWith != null) throw FailWith;
            IReadOnlyList<DirectoryGroup> found = _groups.FindAll(g => string.Equals(g.DisplayName, displayName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(found);
        }

        public Task<DirectoryPage<DirectoryGroup>> ListGroupsAsync(string nextLink)
        {
            Calls++;
            if (FailWith != null) throw FailWith;
            return Task.FromResult(Page(_groups, nextLink));
        }

        public Task<DirectoryPage<DirectoryUser>> ListUserMembersAsync(string groupId, string nextLink)
        {
            Calls++;
            if (FailWith != null) throw FailWith;
            if (string.Equals(groupId, FailMembersOfGroupId, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryReadException(System.Net.HttpStatusCode.Forbidden, "https://graph.microsoft.com/v1.0/groups/x/members", "{}");
            }
            return Task.FromResult(Page(_members.TryGetValue(groupId, out var m) ? m : new List<DirectoryUser>(), nextLink));
        }

        private DirectoryPage<T> Page<T>(List<T> all, string nextLink)
        {
            var start = string.IsNullOrEmpty(nextLink) ? 0 : int.Parse(nextLink);
            var page = new DirectoryPage<T> { Items = all.GetRange(start, Math.Min(PageSize, all.Count - start)) };
            if (start + PageSize < all.Count)
            {
                page.NextLink = (start + PageSize).ToString();
            }
            return page;
        }
    }
}
