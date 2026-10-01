using System;
using System.Collections.Generic;
using System.Linq;

namespace Common.Entities.UserScope
{
    /// <summary>
    /// The people <c>UserGroupsFilter</c> resolved to: the direct user members of every matched Entra ID group.
    /// </summary>
    /// <remarks>
    /// Each member is recorded by object id, user principal name and mail address, because the imports identify
    /// people differently: audit events and usage reports by UPN, Copilot Studio credits and Teams messages by
    /// object id, and SharePoint page comments and likes by an email address that is not guaranteed to be the
    /// UPN. Name lookups are case-insensitive to match SQL Server's default collation.
    /// </remarks>
    public sealed class UserScopeMembers
    {
        private readonly HashSet<Guid> _objectIds = new HashSet<Guid>();
        private readonly HashSet<Guid> _disabledObjectIds = new HashSet<Guid>();
        private readonly HashSet<string> _userPrincipalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only mail addresses that differ from the member's UPN, so the common case costs one string, not two.
        private readonly HashSet<string> _otherAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Distinct members added.</summary>
        public int Count { get; private set; }

        /// <summary>Member user principal names, for callers that look members up in <c>dbo.users</c>.</summary>
        public IReadOnlyCollection<string> UserPrincipalNames => _userPrincipalNames;

        /// <summary>Member Entra object ids.</summary>
        public IReadOnlyCollection<Guid> ObjectIds => _objectIds;

        /// <summary>Object ids of members whose account is enabled. Disabled members are still in scope.</summary>
        public IReadOnlyCollection<Guid> EnabledObjectIds =>
            _disabledObjectIds.Count == 0 ? (IReadOnlyCollection<Guid>)_objectIds : _objectIds.Where(id => !_disabledObjectIds.Contains(id)).ToList();

        /// <summary>Adds a member. Returns false when the member was already present.</summary>
        public bool Add(string objectId, string userPrincipalName, string mail, bool accountEnabled = true)
        {
            var upn = Clean(userPrincipalName);
            var address = Clean(mail);

            bool isNew;
            if (Guid.TryParse(objectId, out var id) && id != Guid.Empty)
            {
                isNew = _objectIds.Add(id);
                if (!accountEnabled)
                {
                    _disabledObjectIds.Add(id);
                }
            }
            else
            {
                isNew = upn != null && !_userPrincipalNames.Contains(upn);
            }

            if (upn != null)
            {
                _userPrincipalNames.Add(upn);
            }
            if (address != null && (upn == null || !string.Equals(address, upn, StringComparison.OrdinalIgnoreCase)))
            {
                _otherAddresses.Add(address);
            }

            if (isNew)
            {
                Count++;
            }
            return isNew;
        }

        /// <summary>
        /// True when <paramref name="identifier"/> - a UPN, a mail address, an Entra object id, or a SharePoint
        /// claims login such as <c>i:0#.f|membership|user@contoso.com</c> - belongs to a member.
        /// </summary>
        public bool Contains(string identifier)
        {
            var value = NormaliseIdentifier(identifier);
            if (value == null)
            {
                return false;
            }

            if (Guid.TryParse(value, out var id))
            {
                return _objectIds.Contains(id);
            }

            return _userPrincipalNames.Contains(value) || _otherAddresses.Contains(value);
        }

        /// <summary>True when <paramref name="objectId"/> is a member's Entra object id.</summary>
        public bool Contains(Guid objectId) => _objectIds.Contains(objectId);

        /// <summary>
        /// Trims, and strips a SharePoint claims prefix (<c>i:0#.f|membership|</c>) so the login name SharePoint
        /// reports matches the UPN Graph returns.
        /// </summary>
        internal static string NormaliseIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                return null;
            }

            var value = identifier.Trim();
            var claimSeparator = value.LastIndexOf('|');
            if (claimSeparator >= 0 && claimSeparator < value.Length - 1)
            {
                value = value.Substring(claimSeparator + 1);
            }
            return value.Length == 0 ? null : value;
        }

        private static string Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Who an import may store data about. Every import that stores data about individual people checks this
    /// before it writes - or better, before it asks a remote API for that person's data at all.
    /// </summary>
    /// <remarks>
    /// Immutable and cheap to test against: lookups are hash lookups, so checking every row of a 200,000-user
    /// usage report costs nothing measurable, unlike the one Graph call per user the old per-user check made.
    /// </remarks>
    public sealed class UserImportScope
    {
        private readonly UserScopeMembers _members;

        private UserImportScope(UserScopeMembers members, string description)
        {
            _members = members;
            Description = description;
        }

        /// <summary>No narrowing <c>UserGroupsFilter</c> is configured, so everyone is in scope.</summary>
        public static UserImportScope Unfiltered { get; } =
            new UserImportScope(null, "no UserGroupsFilter is configured, so every user is in scope");

        /// <summary>
        /// Everyone is in scope because the configured filter could not be resolved. This is the fail-open
        /// outcome, and <paramref name="reason"/> should say why.
        /// </summary>
        public static UserImportScope Everyone(string reason) => new UserImportScope(null, reason);

        /// <summary>Only <paramref name="members"/> are in scope.</summary>
        public static UserImportScope ForMembers(UserScopeMembers members, string description)
        {
            if (members == null) throw new ArgumentNullException(nameof(members));
            return new UserImportScope(members, description);
        }

        /// <summary>True when only some people are in scope.</summary>
        public bool IsFiltered => _members != null;

        /// <summary>How many people are in scope, or null when everyone is.</summary>
        public int? MemberCount => _members?.Count;

        /// <summary>The Entra object ids of the people in scope; empty when unfiltered.</summary>
        public IReadOnlyCollection<Guid> MemberObjectIds => _members?.ObjectIds ?? (IReadOnlyCollection<Guid>)Array.Empty<Guid>();

        /// <summary>The Entra object ids of the people in scope whose account is enabled; empty when unfiltered.</summary>
        public IReadOnlyCollection<Guid> EnabledMemberObjectIds => _members?.EnabledObjectIds ?? (IReadOnlyCollection<Guid>)Array.Empty<Guid>();

        /// <summary>A sentence for logs saying what this scope is and where it came from.</summary>
        public string Description { get; }

        /// <summary>
        /// True when data about <paramref name="identifier"/> (a UPN, mail address, claims login or object id)
        /// may be imported. Always true when unfiltered - including for an empty identifier, so an unfiltered
        /// deployment keeps importing exactly what it did before. When filtered, a missing identifier is out
        /// of scope: a row that cannot be tied to a member cannot be shown to belong to one.
        /// </summary>
        public bool IsInScope(string identifier) => _members == null || _members.Contains(identifier);

        /// <summary>True when data about the person with this Entra object id may be imported.</summary>
        public bool IsInScope(Guid objectId) => _members == null || _members.Contains(objectId);

        /// <summary>True when any of <paramref name="identifiers"/> is in scope.</summary>
        public bool IsAnyInScope(params string[] identifiers)
        {
            if (_members == null)
            {
                return true;
            }
            if (identifiers == null)
            {
                return false;
            }
            foreach (var identifier in identifiers)
            {
                if (_members.Contains(identifier))
                {
                    return true;
                }
            }
            return false;
        }

        public override string ToString() => Description;
    }

    /// <summary>What resolving <c>UserGroupsFilter</c> produced.</summary>
    public enum UserImportScopeStatus
    {
        /// <summary>No narrowing filter is configured. Nothing was resolved and nobody is excluded.</summary>
        Unfiltered,

        /// <summary>
        /// The filter was resolved completely. The member set is authoritative - including when it is empty
        /// because no group matched, which is a configuration problem, not a failure.
        /// </summary>
        Resolved,

        /// <summary>
        /// The filter could not be resolved completely: a Graph call failed, a group could not be read, or the
        /// call budget ran out. Any members found are a subset of the real scope.
        /// </summary>
        Unavailable
    }

    /// <summary>Why a resolution is <see cref="UserImportScopeStatus.Unavailable"/>, as a fact a caller can act on.</summary>
    public enum UserImportScopeFailureKind
    {
        /// <summary>Reading a group or its members from Microsoft Graph failed (see the HTTP status, when there is one).</summary>
        DirectoryRead,

        /// <summary>The call budget for one resolution ran out before every matched group had been read.</summary>
        BudgetExhausted,

        /// <summary>No Graph client could be created, typically because the app registration settings are incomplete.</summary>
        ClientUnavailable,

        /// <summary>Anything else.</summary>
        Unexpected
    }

    /// <summary>One Entra ID group the filter matched.</summary>
    public sealed class ResolvedGroup
    {
        public ResolvedGroup(string id, string displayName, IReadOnlyList<string> matchedPatterns, int userMemberCount)
        {
            Id = id;
            DisplayName = displayName;
            MatchedPatterns = matchedPatterns ?? new List<string>();
            UserMemberCount = userMemberCount;
        }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>The filter pattern(s) that selected this group.</summary>
        public IReadOnlyList<string> MatchedPatterns { get; }

        /// <summary>Direct user members read for this group. May be a partial count when resolution stopped early.</summary>
        public int UserMemberCount { get; }
    }

    /// <summary>The outcome of one attempt to resolve <c>UserGroupsFilter</c>.</summary>
    public sealed class UserImportScopeResolution
    {
        private static readonly IReadOnlyList<ResolvedGroup> NoGroups = new List<ResolvedGroup>();
        private static readonly IReadOnlyList<string> NoPatterns = new List<string>();

        private UserImportScopeResolution(UserImportScopeStatus status, UserScopeMembers members, IReadOnlyList<ResolvedGroup> groups,
            IReadOnlyList<string> unmatchedPatterns, string reason, DateTime resolvedUtc,
            UserImportScopeFailureKind? failureKind = null, int? failureHttpStatus = null)
        {
            Status = status;
            Members = members;
            Groups = groups ?? NoGroups;
            UnmatchedPatterns = unmatchedPatterns ?? NoPatterns;
            Reason = reason;
            ResolvedUtc = resolvedUtc;
            FailureKind = failureKind;
            FailureHttpStatus = failureHttpStatus;
        }

        public UserImportScopeStatus Status { get; }

        /// <summary>
        /// Members found. Null when <see cref="UserImportScopeStatus.Unfiltered"/>; possibly partial when
        /// <see cref="UserImportScopeStatus.Unavailable"/>.
        /// </summary>
        public UserScopeMembers Members { get; }

        /// <summary>The Entra ID groups the filter matched, with their member counts.</summary>
        public IReadOnlyList<ResolvedGroup> Groups { get; }

        /// <summary>How many Entra ID groups the filter matched.</summary>
        public int MatchedGroupCount => Groups.Count;

        /// <summary>Filter patterns that matched no group.</summary>
        public IReadOnlyList<string> UnmatchedPatterns { get; }

        /// <summary>Why resolution is <see cref="UserImportScopeStatus.Unavailable"/>; otherwise null.</summary>
        public string Reason { get; }

        /// <summary>
        /// The kind of failure behind <see cref="Reason"/>, for callers that must describe it in their own words;
        /// null unless <see cref="UserImportScopeStatus.Unavailable"/>.
        /// </summary>
        public UserImportScopeFailureKind? FailureKind { get; }

        /// <summary>The HTTP status Microsoft Graph answered with, when that is what failed (403: a missing permission).</summary>
        public int? FailureHttpStatus { get; }

        public DateTime ResolvedUtc { get; }

        /// <summary>Resolved completely, but no Entra ID group matched the filter - so nobody is in scope.</summary>
        public bool MatchedNoGroup => Status == UserImportScopeStatus.Resolved && MatchedGroupCount == 0;

        public static UserImportScopeResolution Unfiltered(DateTime nowUtc)
            => new UserImportScopeResolution(UserImportScopeStatus.Unfiltered, null, null, null, null, nowUtc);

        public static UserImportScopeResolution Resolved(UserScopeMembers members, IReadOnlyList<ResolvedGroup> groups,
            IReadOnlyList<string> unmatchedPatterns, DateTime nowUtc)
            => new UserImportScopeResolution(UserImportScopeStatus.Resolved, members ?? new UserScopeMembers(),
                groups, unmatchedPatterns, null, nowUtc);

        public static UserImportScopeResolution Unavailable(UserScopeMembers partialMembers, IReadOnlyList<ResolvedGroup> groups,
            string reason, DateTime nowUtc, UserImportScopeFailureKind failureKind = UserImportScopeFailureKind.Unexpected, int? httpStatus = null)
            => new UserImportScopeResolution(UserImportScopeStatus.Unavailable, partialMembers ?? new UserScopeMembers(),
                groups, null, string.IsNullOrWhiteSpace(reason) ? "the group membership could not be read" : reason, nowUtc,
                failureKind, httpStatus);
    }
}
