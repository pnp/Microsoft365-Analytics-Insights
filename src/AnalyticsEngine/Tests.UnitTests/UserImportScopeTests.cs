using Common.Entities.Config;
using Common.Entities.UserScope;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Tests.UnitTests.FakeLoaderClasses;
using FixedClock = UnitTests.FakeLoaderClasses.FixedClock;

namespace Tests.UnitTests
{
    /// <summary>
    /// The shared <c>UserGroupsFilter</c> scope: who it matches, how the filter is resolved group-first, and how the
    /// process-lifetime provider caches, falls back and fails open. No Graph and no SQL.
    /// </summary>
    [TestClass]
    public class UserImportScopeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        private const string PilotGroupId = "11111111-1111-1111-1111-111111111111";
        private const string AliceId = "aaaaaaaa-0000-0000-0000-000000000001";
        private const string BobId = "aaaaaaaa-0000-0000-0000-000000000002";
        private const string CarolId = "aaaaaaaa-0000-0000-0000-000000000003";

        #region Scope matching

        [TestMethod]
        public void UnfilteredScope_LetsEveryoneThrough_IncludingRowsWithNoIdentity()
        {
            var scope = UserImportScope.Unfiltered;

            Assert.IsFalse(scope.IsFiltered);
            Assert.IsNull(scope.MemberCount);
            Assert.IsTrue(scope.IsInScope("anyone@contoso.com"));
            Assert.IsTrue(scope.IsInScope((string)null), "An unfiltered deployment must keep importing exactly what it did before.");
            Assert.IsTrue(scope.IsInScope(Guid.NewGuid()));
            Assert.IsTrue(scope.IsAnyInScope());
        }

        [TestMethod]
        public void FilteredScope_MatchesAMemberByUpnMailOrObjectId_CaseInsensitively()
        {
            var scope = TestUserScopes.OfMembers((AliceId, "alice@contoso.com", "alice.smith@contoso.com"));

            Assert.IsTrue(scope.IsFiltered);
            Assert.AreEqual(1, scope.MemberCount);
            Assert.IsTrue(scope.IsInScope("ALICE@contoso.com"), "UPN, case-insensitive like SQL Server's default collation.");
            Assert.IsTrue(scope.IsInScope("Alice.Smith@Contoso.com"), "Mail - page comment/like authors are keyed by email, not UPN.");
            Assert.IsTrue(scope.IsInScope(AliceId.ToUpperInvariant()), "Entra object id, as Copilot Studio credits and Teams messages identify people.");
            Assert.IsTrue(scope.IsInScope(new Guid(AliceId)));
            Assert.IsTrue(scope.IsInScope("i:0#.f|membership|alice@contoso.com"), "A SharePoint claims login reduces to the UPN.");

            Assert.IsFalse(scope.IsInScope("bob@contoso.com"));
            Assert.IsFalse(scope.IsInScope(new Guid(BobId)));
        }

        [TestMethod]
        public void FilteredScope_RowWithNoIdentity_IsOutOfScope()
        {
            var scope = TestUserScopes.Of("alice@contoso.com");

            // A row that cannot be tied to a member cannot be shown to belong to one - under the old per-user check a
            // failed lookup counted as "no groups", which counted as a match.
            Assert.IsFalse(scope.IsInScope((string)null));
            Assert.IsFalse(scope.IsInScope(string.Empty));
            Assert.IsFalse(scope.IsInScope("   "));
            Assert.IsFalse(scope.IsAnyInScope(null, "", "outsider@contoso.invalid"));
            Assert.IsTrue(scope.IsAnyInScope(null, "alice@contoso.com"));
        }

        [TestMethod]
        public void IsNarrowing_OnlyForAFilterThatActuallyRestricts()
        {
            Assert.IsFalse(new UserGroupsFilterModel(null).IsNarrowing);
            Assert.IsFalse(new UserGroupsFilterModel("  ").IsNarrowing);
            Assert.IsFalse(new UserGroupsFilterModel("*").IsNarrowing, "'*' matches every group, so it narrows nothing.");
            Assert.IsFalse(new UserGroupsFilterModel("Pilot;*").IsNarrowing, "Patterns are OR'd: one match-all pattern widens the whole filter.");
            Assert.IsTrue(new UserGroupsFilterModel("Pilot").IsNarrowing);
            Assert.IsTrue(new UserGroupsFilterModel("Pilot*").IsNarrowing);
        }

        #endregion

        #region Group-first resolution

        private static FakeGroupDirectoryReader Directory()
        {
            return new FakeGroupDirectoryReader { PageSize = 2 }
                .AddGroup(PilotGroupId, "Copilot Pilot",
                    FakeGroupDirectoryReader.User(AliceId, "alice@contoso.com"),
                    FakeGroupDirectoryReader.User(BobId, "bob@contoso.com"),
                    FakeGroupDirectoryReader.User(CarolId, "carol@contoso.com", enabled: false))
                // Non-Latin display names are real (and matched by wildcard, client-side).
                .AddGroup("22222222-2222-2222-2222-222222222222", "Ομάδα Πωλήσεων Αθήνα",
                    FakeGroupDirectoryReader.User(BobId, "bob@contoso.com"),
                    FakeGroupDirectoryReader.User("aaaaaaaa-0000-0000-0000-000000000004", "dimitris@contoso.com"))
                .AddGroup("33333333-3333-3333-3333-333333333333", "Everyone Else",
                    FakeGroupDirectoryReader.User("aaaaaaaa-0000-0000-0000-000000000009", "zed@contoso.com"));
        }

        private static GroupMembershipResolver Resolver(IGroupDirectoryReader reader, int maxCalls = GroupMembershipResolver.DefaultMaxTotalGraphCalls)
            => new GroupMembershipResolver(reader, null, new FixedClock(Now), maxTotalGraphCalls: maxCalls);

        [TestMethod]
        public async Task Resolver_ResolvesAnObjectIdAnExactNameAndAWildcard()
        {
            var resolution = await Resolver(Directory()).ResolveAsync(new UserGroupsFilterModel($"{PilotGroupId};Ομάδα*"));

            Assert.AreEqual(UserImportScopeStatus.Resolved, resolution.Status);
            Assert.AreEqual(2, resolution.MatchedGroupCount);
            Assert.AreEqual(4, resolution.Members.Count, "alice, bob and carol from the pilot group, plus dimitris; bob is counted once.");

            var pilot = resolution.Groups.Single(g => g.Id == PilotGroupId);
            Assert.AreEqual(3, pilot.UserMemberCount, "Each group reports its own direct membership, for the admin page.");
            CollectionAssert.AreEqual(new[] { PilotGroupId }, pilot.MatchedPatterns.ToList());

            var greek = resolution.Groups.Single(g => g.DisplayName == "Ομάδα Πωλήσεων Αθήνα");
            Assert.AreEqual(2, greek.UserMemberCount);
            CollectionAssert.AreEqual(new[] { "Ομάδα*" }, greek.MatchedPatterns.ToList());

            Assert.AreEqual(0, resolution.UnmatchedPatterns.Count);
            Assert.AreEqual(Now, resolution.ResolvedUtc);
        }

        [TestMethod]
        public async Task Resolver_PagesThroughEveryMember()
        {
            // Page size 2 and three members: the per-user memberOf check read one page only, which is exactly how a
            // member could be wrongly judged out of scope.
            var resolution = await Resolver(Directory()).ResolveAsync(new UserGroupsFilterModel("Copilot Pilot"));

            var scope = UserImportScope.ForMembers(resolution.Members, "test");
            Assert.IsTrue(scope.IsInScope("carol@contoso.com"), "The member on the second page must be in scope.");
            Assert.AreEqual(3, resolution.Members.Count);
        }

        [TestMethod]
        public async Task Resolver_DisabledMembersAreInScopeButNotCountedAsEnabled()
        {
            var resolution = await Resolver(Directory()).ResolveAsync(new UserGroupsFilterModel("Copilot Pilot"));

            Assert.AreEqual(3, resolution.Members.ObjectIds.Count, "A disabled account's data is still in scope.");
            CollectionAssert.AreEquivalent(new[] { new Guid(AliceId), new Guid(BobId) }, resolution.Members.EnabledObjectIds.ToList(),
                "...but it is not a user the directory import can add, so it must not trigger catch-up re-reads.");
        }

        [TestMethod]
        public async Task Resolver_FilterThatMatchesNoGroup_ResolvesToNobody_NotToAFailure()
        {
            var resolution = await Resolver(Directory()).ResolveAsync(new UserGroupsFilterModel("Copilot Pilott;Missing*"));

            Assert.AreEqual(UserImportScopeStatus.Resolved, resolution.Status, "A typo is an answer - nobody - not an outage.");
            Assert.IsTrue(resolution.MatchedNoGroup);
            Assert.AreEqual(0, resolution.Members.Count);
            CollectionAssert.AreEquivalent(new[] { "Copilot Pilott", "Missing*" }, resolution.UnmatchedPatterns.ToList());
        }

        [TestMethod]
        public async Task Resolver_GroupWhoseMembersCannotBeRead_IsUnavailable_NotEmpty()
        {
            var reader = Directory();
            reader.FailMembersOfGroupId = PilotGroupId;

            var resolution = await Resolver(reader).ResolveAsync(new UserGroupsFilterModel("Copilot Pilot"));

            // Treating an unreadable group as an empty one would silently drop its members from every import.
            Assert.AreEqual(UserImportScopeStatus.Unavailable, resolution.Status);
            StringAssert.Contains(resolution.Reason, "Copilot Pilot");
            StringAssert.Contains(resolution.Reason, "could not be read");
        }

        [TestMethod]
        public async Task Resolver_DirectoryOutage_IsUnavailable()
        {
            var reader = Directory();
            reader.FailWith = new DirectoryReadException(HttpStatusCode.Forbidden, "https://graph.microsoft.com/v1.0/groups", "{}");

            foreach (var filter in new[] { PilotGroupId, "Copilot Pilot", "Copilot*" })
            {
                var resolution = await Resolver(reader).ResolveAsync(new UserGroupsFilterModel(filter));

                Assert.AreEqual(UserImportScopeStatus.Unavailable, resolution.Status,
                    $"A failed lookup for '{filter}' must never look like 'no group matched'.");
                StringAssert.Contains(resolution.Reason, "Group.Read.All", "A 403 names the permission to grant.");
            }
        }

        [TestMethod]
        public async Task Resolver_ExhaustedBudget_IsUnavailable()
        {
            var resolution = await Resolver(Directory(), maxCalls: 2).ResolveAsync(new UserGroupsFilterModel("Copilot Pilot"));

            Assert.AreEqual(UserImportScopeStatus.Unavailable, resolution.Status);
            StringAssert.Contains(resolution.Reason, "budget");
        }

        [TestMethod]
        public async Task Resolver_BlankOrMatchAllFilter_IsUnfilteredWithoutReadingTheDirectory()
        {
            var reader = Directory();
            foreach (var filter in new[] { null, "", "*", "Copilot Pilot;*" })
            {
                var resolution = await Resolver(reader).ResolveAsync(new UserGroupsFilterModel(filter));
                Assert.AreEqual(UserImportScopeStatus.Unfiltered, resolution.Status, $"'{filter}' narrows nothing.");
            }
            Assert.AreEqual(0, reader.Calls, "Expanding '*' would page the whole directory to conclude 'everyone' (issue #297).");
        }

        #endregion

        #region Provider: caching, last-known-good, fail-open

        private sealed class ScriptedResolver : IGroupMembershipResolver
        {
            private readonly Queue<Func<UserImportScopeResolution>> _script = new Queue<Func<UserImportScopeResolution>>();
            public int Calls;
            public int DelayMs { get; set; }

            public ScriptedResolver Then(Func<UserImportScopeResolution> next)
            {
                _script.Enqueue(next);
                return this;
            }

            public async Task<UserImportScopeResolution> ResolveAsync(UserGroupsFilterModel filter)
            {
                Interlocked.Increment(ref Calls);
                if (DelayMs > 0) await Task.Delay(DelayMs);
                var next = _script.Count > 1 ? _script.Dequeue() : _script.Peek();
                return next();
            }
        }

        private static UserImportScopeResolution ResolvedTo(FixedClock clock, params string[] upns)
        {
            var members = new UserScopeMembers();
            foreach (var upn in upns) members.Add(Guid.NewGuid().ToString(), upn, null);
            return UserImportScopeResolution.Resolved(members,
                new List<ResolvedGroup> { new ResolvedGroup("g", "Copilot Pilot", new List<string> { "Copilot Pilot" }, upns.Length) },
                new List<string>(), clock.UtcNow);
        }

        private static UserImportScopeResolution Failed(FixedClock clock)
            => UserImportScopeResolution.Unavailable(null, null, "Graph returned HTTP 503", clock.UtcNow);

        [TestMethod]
        public async Task Provider_Unfiltered_NeverResolvesAnything()
        {
            var resolver = new ScriptedResolver().Then(() => throw new AssertFailedException("must not resolve"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("*"), resolver, null);

            Assert.IsFalse((await provider.GetScopeAsync()).IsFiltered);
            Assert.AreEqual(UserImportScopeStatus.Unfiltered, (await provider.RefreshAsync()).Status);
            Assert.AreEqual(0, resolver.Calls);
        }

        [TestMethod]
        public async Task Provider_ReusesAResolutionUntilTheRefreshIntervalPasses()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver().Then(() => ResolvedTo(clock, "alice@contoso.com"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            await provider.GetScopeAsync();
            clock.Advance(TimeSpan.FromMinutes(59));
            await provider.GetScopeAsync();
            Assert.AreEqual(1, resolver.Calls, "Every import in the process shares one resolution per interval.");

            clock.Advance(TimeSpan.FromMinutes(2));
            await provider.GetScopeAsync();
            Assert.AreEqual(2, resolver.Calls, "...and it is refreshed once the interval has passed.");
        }

        [TestMethod]
        public async Task Provider_FailsOpenToEveryone_WhenNothingHasEverResolved()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver().Then(() => Failed(clock));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            var scope = await provider.GetScopeAsync();

            Assert.IsFalse(scope.IsFiltered, "Fail open: with no scope ever resolved, everyone is imported.");
            StringAssert.Contains(scope.Description, "failing open");
            Assert.AreEqual(UserImportScopeStatus.Unavailable, (await provider.GetResolutionAsync()).Status,
                "The raw resolution still says it failed, for the import that must fail closed.");
        }

        [TestMethod]
        public async Task Provider_ReusesTheLastGoodScope_WhenARefreshFails()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver()
                .Then(() => ResolvedTo(clock, "alice@contoso.com"))
                .Then(() => Failed(clock));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            await provider.GetScopeAsync();
            clock.Advance(TimeSpan.FromHours(2));
            var scope = await provider.GetScopeAsync();

            Assert.AreEqual(2, resolver.Calls);
            Assert.IsTrue(scope.IsFiltered, "A failed refresh must not widen the scope to everyone when a good one is known.");
            Assert.IsTrue(scope.IsInScope("alice@contoso.com"));
            Assert.IsFalse(scope.IsInScope("bob@contoso.com"));
            StringAssert.Contains(scope.Description, "could not be refreshed");
        }

        [TestMethod]
        public async Task Provider_RetriesAFailureSoonerThanASuccess()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver()
                .Then(() => Failed(clock))
                .Then(() => ResolvedTo(clock, "alice@contoso.com"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            Assert.IsFalse((await provider.GetScopeAsync()).IsFiltered);
            clock.Advance(TimeSpan.FromMinutes(1));
            await provider.GetScopeAsync();
            Assert.AreEqual(1, resolver.Calls, "A failure is not retried on every call...");

            clock.Advance(UserImportScopeProvider.DefaultRetryInterval);
            Assert.IsTrue((await provider.GetScopeAsync()).IsFiltered, "...but it is retried after the short retry interval, not an hour.");
            Assert.AreEqual(2, resolver.Calls);
        }

        [TestMethod]
        public async Task Provider_RefreshAsync_ResolvesAgainImmediately()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver()
                .Then(() => ResolvedTo(clock, "alice@contoso.com"))
                .Then(() => ResolvedTo(clock, "alice@contoso.com", "bob@contoso.com"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            await provider.GetScopeAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
            var refreshed = await provider.RefreshAsync();

            Assert.AreEqual(2, resolver.Calls, "A forced refresh ignores the cached resolution.");
            Assert.AreEqual(2, refreshed.Members.Count);
            Assert.IsTrue((await provider.GetScopeAsync()).IsInScope("bob@contoso.com"), "...and becomes the current one.");
        }

        [TestMethod]
        public async Task Provider_ConcurrentCallersShareOneResolution()
        {
            var clock = new FixedClock(Now);
            var resolver = new ScriptedResolver { DelayMs = 100 }.Then(() => ResolvedTo(clock, "alice@contoso.com"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, clock);

            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => provider.GetScopeAsync()));

            Assert.AreEqual(1, resolver.Calls, "Concurrent imports must not each read the directory.");
        }

        [TestMethod]
        public async Task Provider_ResolverThatThrows_IsTreatedAsUnavailable()
        {
            var resolver = new ScriptedResolver().Then(() => throw new InvalidOperationException("boom"));
            var provider = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"), resolver, null, new FixedClock(Now));

            var resolution = await provider.GetResolutionAsync();

            Assert.AreEqual(UserImportScopeStatus.Unavailable, resolution.Status);
            StringAssert.Contains(resolution.Reason, "boom");
        }

        [TestMethod]
        public async Task Provider_CycleSummary_IsAnErrorWhenFailingOpenAndAWarningWhenNobodyIsInScope()
        {
            var clock = new FixedClock(Now);

            var failingLogger = new RecordingLogger();
            var failing = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilot"),
                new ScriptedResolver().Then(() => Failed(clock)), failingLogger, clock);
            await failing.GetScopeForCycleAsync();
            Assert.IsTrue(failingLogger.Entries.Any(e => e.Level == LogLevel.Error && e.Message.StartsWith("User import scope for this cycle")),
                "Failing open imports everyone: that must be an error, every cycle.");

            var emptyLogger = new RecordingLogger();
            var noMatch = new UserImportScopeProvider(new UserGroupsFilterModel("Copilot Pilott"),
                new ScriptedResolver().Then(() => UserImportScopeResolution.Resolved(new UserScopeMembers(), new List<ResolvedGroup>(),
                    new List<string> { "Copilot Pilott" }, clock.UtcNow)), emptyLogger, clock);
            var scope = await noMatch.GetScopeForCycleAsync();
            Assert.IsTrue(scope.IsFiltered);
            Assert.AreEqual(0, scope.MemberCount, "A filter matching no group means nobody is in scope.");
            var summary = emptyLogger.Entries.Single(e => e.Message.StartsWith("User import scope for this cycle"));
            Assert.AreEqual(LogLevel.Warning, summary.Level);
            StringAssert.Contains(summary.Message, "matches no Entra ID group");
        }

        #endregion
    }
}
