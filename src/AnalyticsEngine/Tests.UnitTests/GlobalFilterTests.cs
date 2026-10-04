using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The administrator's global report filter: its wire form, how it resolves for the person viewing a
    /// report, and how a report's SQL is narrowed to the people it allows. In memory - no database.
    /// </summary>
    [TestClass]
    public class GlobalFilterTests
    {
        private static readonly DateTime Loaded = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        private const string RepObjectId = "00000000-0000-0000-0000-000000000003";

        private const int Ceo = 1;
        private const int SalesDirector = 2;
        private const int SalesRep = 3;
        private const int SalesPeer = 4;
        private const int Engineer = 5;
        private const int NoDepartment = 6;
        private const int GreekAnalyst = 7;

        private static readonly int[] Everyone = { Ceo, SalesDirector, SalesRep, SalesPeer, Engineer, NoDepartment, GreekAnalyst };

        #region Wire form

        [TestMethod]
        public void Codec_ReadsAConditionOnTheViewersOwnValue_AndWritesItBack()
        {
            const string encoded = "[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"},{\"d\":\"userType\",\"v\":[\"member\"]}]";
            var definition = GlobalFilterCodec.Parse(encoded);

            Assert.AreEqual(2, definition.Clauses.Count);
            Assert.AreEqual("department", definition.Clauses[0].ViewerAttribute);
            Assert.AreEqual(0, definition.Clauses[0].Clause.Values.Count);
            Assert.IsNull(definition.Clauses[1].ViewerAttribute);
            Assert.IsTrue(definition.UsesViewer);
            Assert.AreEqual(encoded, GlobalFilterCodec.Serialize(definition));
        }

        [TestMethod]
        public void Codec_BlankIsNoFilter()
        {
            Assert.IsTrue(GlobalFilterCodec.Parse(null).IsEmpty);
            Assert.IsTrue(GlobalFilterCodec.Parse("").IsEmpty);
            Assert.IsTrue(GlobalFilterCodec.Parse("   ").IsEmpty);
            Assert.IsNull(GlobalFilterCodec.Serialize(GlobalFilterDefinition.Empty));
        }

        [TestMethod]
        public void Codec_RefusesAnythingAfterTheFilter_RatherThanReadingOnlyItsFirstArray()
        {
            // The first array is empty - "no filter" - so stopping there would run every report unrestricted.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[][{\"d\":\"department\",\"v\":[\"Sales\"]}]"));
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}] true"));
            Assert.ThrowsException<UserFilterFormatException>(() => UserFilterCodec.Parse("[] [{\"d\":\"department\",\"v\":[\"Sales\"]}]"));

            // Whitespace and a comment after it are still one filter.
            Assert.AreEqual(1, GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]  \r\n").Clauses.Count);
            Assert.AreEqual(1, GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}] /* note */").Clauses.Count);
        }

        [TestMethod]
        public void Codec_RefusesComparisonsTheEditorNeverOffers()
        {
            // Text search on the viewer's own value.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"op\":\"contains\",\"v\":[\"Sal\"],\"vu\":\"department\"}]"));
            // A department compared with the viewer's country.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"country\"}]"));
            // Not a string.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":7}]"));
            // No values and no viewer: a condition on nothing.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[]}]"));
            // Not an attribute anybody holds.
            Assert.ThrowsException<UserFilterFormatException>(() =>
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"salary\"}]"));
        }

        [TestMethod]
        public void ViewerAttributes_AreTheOnesTheEditorOffers()
        {
            CollectionAssert.AreEqual(new[] { "department" }, GlobalFilterViewerAttributes.AllowedFor("department").ToArray());
            CollectionAssert.AreEqual(new[] { "org:12" }, GlobalFilterViewerAttributes.AllowedFor("org:12").ToArray());
            CollectionAssert.AreEqual(new[] { "userName" }, GlobalFilterViewerAttributes.AllowedFor("userName").ToArray());
            CollectionAssert.AreEqual(new[] { "userName", "manager" }, GlobalFilterViewerAttributes.AllowedFor("manager").ToArray());
            CollectionAssert.AreEqual(new[] { "userName", "manager" }, GlobalFilterViewerAttributes.AllowedFor("managementChain").ToArray());
            Assert.AreEqual(0, GlobalFilterViewerAttributes.AllowedFor("salary").Count);
        }

        #endregion

        #region Resolving for the viewer

        [TestMethod]
        public void Resolve_ShowsEachViewerTheirOwnDepartment()
        {
            var snapshot = Snapshot();
            var definition = GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]");

            var forRep = Resolve(definition, snapshot, SalesRep);
            AssertMatches(Compile(forRep, snapshot), SalesDirector, SalesRep, SalesPeer);
            Assert.AreEqual("Sales", forRep.Clauses[0].ViewerValue);
            Assert.IsFalse(forRep.AnyUnresolved);

            var forEngineer = Resolve(definition, snapshot, Engineer);
            AssertMatches(Compile(forEngineer, snapshot), Engineer);

            // Two people the filter treats identically share a key - and so share cached results.
            Assert.AreEqual(forRep.Key, Resolve(definition, snapshot, SalesPeer).Key);
            Assert.AreNotEqual(forRep.Key, forEngineer.Key);
        }

        [TestMethod]
        public void Resolve_KeepsNonLatinValues()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, GreekAnalyst);

            Assert.AreEqual("Οικονομικά", resolved.Clauses[0].ViewerValue);
            AssertMatches(Compile(resolved, snapshot), GreekAnalyst);
        }

        [TestMethod]
        public void Resolve_AddsTheViewersValueToTheFixedOnes()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Engineering\"],\"vu\":\"department\"}]"), snapshot, SalesRep);

            AssertMatches(Compile(resolved, snapshot), SalesDirector, SalesRep, SalesPeer, Engineer);
        }

        [TestMethod]
        public void Resolve_AValueTheViewerDoesNotHave_MatchesNobody()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"],\"vu\":\"department\"}]"), snapshot, NoDepartment);

            Assert.IsTrue(resolved.Clauses[0].Unresolved);
            Assert.IsNull(resolved.Clauses[0].ViewerValue);
            AssertMatches(Compile(resolved, snapshot));
        }

        [TestMethod]
        public void Resolve_ANegatedConditionWithNothingToCompare_StillMatchesNobody()
        {
            // "Department is not the viewer's department" for a viewer with none would otherwise match
            // everyone: a filter set to restrict must never widen because the directory lacks a value.
            var snapshot = Snapshot();
            var resolved = Resolve(
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[],\"vu\":\"department\"}]"), snapshot, NoDepartment);

            AssertMatches(Compile(resolved, snapshot));
        }

        [TestMethod]
        public void Resolve_AViewerTheDirectoryDoesNotHold_MatchesNobodyOnViewerConditionsOnly()
        {
            var snapshot = Snapshot();

            var viewerConditions = GlobalFilterResolver.Resolve(
                GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, null);
            Assert.IsFalse(viewerConditions.ViewerFound);
            AssertMatches(Compile(viewerConditions, snapshot));

            // A filter with fixed values only is the same for everyone, found or not.
            var fixedOnly = GlobalFilterResolver.Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Engineering\"]}]"), snapshot, null);
            AssertMatches(Compile(fixedOnly, snapshot), Engineer);
        }

        [TestMethod]
        public void Resolve_ComparesTheHierarchyWithTheViewer()
        {
            var snapshot = Snapshot();

            // "Manager is the viewer": their direct reports.
            var team = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"manager\",\"v\":[],\"vu\":\"userName\"}]"), snapshot, SalesDirector);
            AssertMatches(Compile(team, snapshot), SalesRep, SalesPeer);

            // "Manager is the viewer's manager": everyone who shares it, the viewer included.
            var peers = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"manager\",\"v\":[],\"vu\":\"manager\"}]"), snapshot, SalesRep);
            AssertMatches(Compile(peers, snapshot), SalesRep, SalesPeer);

            // "Management chain includes the viewer": everyone below them, at any level.
            var organisation = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"managementChain\",\"v\":[],\"vu\":\"userName\"}]"), snapshot, Ceo);
            AssertMatches(Compile(organisation, snapshot), SalesDirector, SalesRep, SalesPeer, Engineer, NoDepartment, GreekAnalyst);

            // "Only my own figures".
            var self = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"userName\",\"v\":[],\"vu\":\"userName\"}]"), snapshot, Engineer);
            AssertMatches(Compile(self, snapshot), Engineer);
        }

        [TestMethod]
        public void TryFindPerson_ByObjectIdFirst_ThenBySignInName()
        {
            var snapshot = Snapshot();

            Assert.IsTrue(snapshot.TryFindPerson(new Guid(RepObjectId), null, out var byId));
            Assert.AreEqual(SalesRep, snapshot.UserIdAt(byId));

            Assert.IsTrue(snapshot.TryFindPerson(null, "ENGINEER@contoso.com", out var byName));
            Assert.AreEqual(Engineer, snapshot.UserIdAt(byName));

            // An object id the import has not recorded falls back to the sign-in name.
            Assert.IsTrue(snapshot.TryFindPerson(Guid.NewGuid(), "engineer@contoso.com", out var fallback));
            Assert.AreEqual(Engineer, snapshot.UserIdAt(fallback));

            Assert.IsFalse(snapshot.TryFindPerson(Guid.NewGuid(), "stranger@contoso.com", out _));
            Assert.IsFalse(snapshot.TryFindPerson(Guid.Empty, null, out _));
        }

        [TestMethod]
        public void TryFindPerson_RefusesASignInNameOnAStaleDuplicateOfAnotherAccount()
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "current@contoso.com", EntraObjectId = RepObjectId });
            // A stale duplicate carrying the same object id under an older address: the object-id lookup keeps
            // the first row, but this one is still that account's, never a stand-in for whoever holds its name.
            builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "previous@contoso.com", EntraObjectId = RepObjectId });
            var snapshot = builder.Build(new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));

            Assert.IsFalse(snapshot.TryFindPerson(new Guid("00000000-0000-0000-0000-0000000000ff"), "previous@contoso.com", out _));
            Assert.IsTrue(snapshot.TryFindPerson(null, "previous@contoso.com", out var byName), "With no object id in the token, the name still finds it.");
            Assert.AreEqual(2, snapshot.UserIdAt(byName));
            Assert.IsTrue(snapshot.TryFindPerson(new Guid(RepObjectId), null, out var byObjectId));
            Assert.AreEqual(1, snapshot.UserIdAt(byObjectId));
        }

        [TestMethod]
        public void TryFindPerson_RefusesASignInNameRecordedUnderAnotherAccount()
        {
            // Rep's row is recorded under RepObjectId. A token carrying a different object id but rep's sign-in name
            // is another account - a new starter given a leaver's address before the next import - not rep.
            var snapshot = Snapshot();

            Assert.IsFalse(snapshot.TryFindPerson(new Guid("00000000-0000-0000-0000-0000000000ff"), "rep@contoso.com", out var row));
            Assert.AreEqual(-1, row);

            // Without an object id in the token, the name is all there is to go on.
            Assert.IsTrue(snapshot.TryFindPerson(null, "rep@contoso.com", out var byName));
            Assert.AreEqual(SalesRep, snapshot.UserIdAt(byName));
        }

        #endregion

        #region Narrowing a report

        [TestMethod]
        public void Scope_IsThePeopleEveryFilterMatches()
        {
            var snapshot = Snapshot();
            var global = Compile(Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, SalesRep), snapshot);
            var reader = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"manager\",\"v\":[\"director@contoso.com\"]}]"), snapshot);

            var scope = ReportUserScope.Matching(global, reader);
            CollectionAssert.AreEqual(new[] { SalesRep, SalesPeer }, scope.UserIds.ToArray());
            Assert.IsTrue(scope.Includes(SalesRep));
            Assert.IsFalse(scope.Includes(Engineer));
            Assert.AreEqual("[3,4]", scope.ToJson());
            Assert.AreEqual(scope.Key, ReportUserScope.ForUsers(new[] { SalesPeer, SalesRep, SalesRep }).Key);

            Assert.AreSame(ReportUserScope.Everyone, ReportUserScope.Matching(null, null));
            Assert.IsTrue(ReportUserScope.Everyone.Includes(Engineer));
        }

        [TestMethod]
        public void Scope_AnEmptyScopeIsNobody_NotEveryone()
        {
            var nobody = ReportUserScope.ForUsers(new int[0]);

            Assert.IsTrue(nobody.IsRestricted);
            Assert.IsFalse(nobody.Includes(Ceo));
            Assert.AreEqual("[]", nobody.ToJson());
        }

        [TestMethod]
        public void Sql_Unrestricted_IsTheStatementWithItsMarkersRemoved()
        {
            const string sql = "SELECT COUNT(*) FROM dbo.hits h WHERE h.hit_timestamp >= @from/*scope: AND h.user_id IN {scopeUsers}*/";

            Assert.AreEqual("SELECT COUNT(*) FROM dbo.hits h WHERE h.hit_timestamp >= @from", ReportScopeSql.Apply(sql, ReportUserScope.Everyone));
            Assert.AreEqual("SELECT 1", ReportScopeSql.Apply("SELECT 1", ReportUserScope.Everyone));
            Assert.IsNull(ReportScopeSql.CreateParameter(ReportUserScope.Everyone));
            Assert.AreEqual(0, ReportScopeSql.DescribeScope(ReportUserScope.Everyone).Count());
        }

        [TestMethod]
        public void Sql_Restricted_FillsATemporaryTableAndNarrowsEveryMarker()
        {
            const string sql = "SELECT COUNT(*) FROM dbo.hits h WHERE h.hit_timestamp >= @from/*scope: AND h.user_id IN {scopeUsers}*/";
            var scope = ReportUserScope.ForUsers(new[] { 3, 4 });

            var applied = ReportScopeSql.Apply(sql, scope);

            StringAssert.StartsWith(applied, ReportScopeSql.Prelude);
            StringAssert.EndsWith(applied,
                "SELECT COUNT(*) FROM dbo.hits h WHERE h.hit_timestamp >= @from AND h.user_id IN (SELECT scope_users.user_id FROM #report_scope_users AS scope_users)");
            StringAssert.Contains(applied, "OPENJSON(@scopeUsers)");

            var parameter = ReportScopeSql.CreateParameter(scope);
            Assert.AreEqual("scopeUsers", parameter.ParameterName);
            Assert.AreEqual("[3,4]", parameter.Value);
            Assert.AreEqual(1, ReportScopeSql.WithScope(null, scope).Length);
        }

        [TestMethod]
        public void Sql_Restricted_RefusesAStatementThatDoesNotSayHowItIsNarrowed()
        {
            var scope = ReportUserScope.ForUsers(new[] { 3 });

            Assert.ThrowsException<ReportScopeNotAppliedException>(() => ReportScopeSql.Apply("SELECT COUNT(*) FROM dbo.hits", scope));
        }

        [TestMethod]
        public void Sql_Restricted_RunsATenantWideStatementUnchanged()
        {
            var scope = ReportUserScope.ForUsers(new[] { 3 });

            Assert.AreEqual("SELECT COUNT(*) FROM dbo.teams", ReportScopeSql.Apply("SELECT COUNT(*) FROM dbo.teams" + ReportScopeSql.TenantWideMarker, scope));
        }

        #endregion

        #region What the reader is told

        [TestMethod]
        public void ReaderFilterEcho_IsCountedWithinTheGlobalFilter()
        {
            var snapshot = Snapshot();
            var global = Compile(Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, SalesRep), snapshot);
            var reader = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Engineering\",\"Sales\"]}]"), snapshot);

            var unrestricted = reader.ToEcho();
            Assert.AreEqual(4, unrestricted.MatchedPeople);
            Assert.AreEqual(Everyone.Length, unrestricted.DirectoryPeople);

            // Within the global filter the reader sees three people, all of whom match - not "4 of 7", which
            // would count an engineer they cannot see.
            var within = reader.ToEcho(global);
            Assert.AreEqual(3, within.MatchedPeople);
            Assert.AreEqual(3, within.DirectoryPeople);
        }

        [TestMethod]
        public void GlobalFilterEcho_ReportsTheViewersValueAndTheCoverage()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, SalesRep);
            var echo = GlobalFilterEcho.From(resolved, Compile(resolved, snapshot), snapshot);

            Assert.AreEqual(3, echo.MatchedPeople);
            Assert.AreEqual(Everyone.Length, echo.DirectoryPeople);
            Assert.IsTrue(echo.ViewerFound);
            Assert.AreEqual("department", echo.Clauses[0].ViewerAttribute);
            Assert.AreEqual("Sales", echo.Clauses[0].ViewerValue);
            Assert.AreEqual(0, echo.Clauses[0].Values.Count);
            Assert.IsFalse(echo.Clauses[0].Unresolved);
        }

        [TestMethod]
        public void Echo_WithoutPeople_CountsNamedPeopleAndWithholdsTheViewersOwnSignInName()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(GlobalFilterCodec.Parse(
                "[{\"d\":\"userName\",\"v\":[\"ceo@contoso.com\",\"engineer@contoso.com\"]},"
                + "{\"j\":\"or\",\"d\":\"manager\",\"v\":[],\"vu\":\"manager\"},"
                + "{\"j\":\"or\",\"d\":\"department\",\"v\":[\"Engineering\"],\"vu\":\"department\"}]"), snapshot, SalesRep);
            var echo = GlobalFilterEcho.From(resolved, Compile(resolved, snapshot), snapshot);

            var hidden = echo.WithoutPeople();

            Assert.AreEqual(0, hidden.Clauses[0].Values.Count);
            Assert.AreEqual(2, hidden.Clauses[0].HiddenValues);
            Assert.IsNull(hidden.Clauses[1].ViewerValue);
            Assert.IsTrue(hidden.Clauses[1].ViewerValueHidden);
            CollectionAssert.AreEqual(new[] { "Engineering" }, hidden.Clauses[2].Values.ToArray(), "Departments are not people.");
            Assert.AreEqual("Sales", hidden.Clauses[2].ViewerValue);
            Assert.AreEqual(echo.MatchedPeople, hidden.MatchedPeople);

            // The original is untouched: it may be someone else's echo.
            Assert.AreEqual(2, echo.Clauses[0].Values.Count);
            Assert.AreEqual("director@contoso.com", echo.Clauses[1].ViewerValue);
        }

        [TestMethod]
        public void Describer_WithoutPeople_NamesNobody()
        {
            var snapshot = Snapshot();
            var resolved = Resolve(GlobalFilterCodec.Parse(
                "[{\"d\":\"userName\",\"v\":[\"ceo@contoso.com\",\"engineer@contoso.com\"]},"
                + "{\"j\":\"or\",\"d\":\"manager\",\"v\":[],\"vu\":\"manager\"},"
                + "{\"j\":\"or\",\"d\":\"managementChain\",\"v\":[],\"vu\":\"userName\"}]"), snapshot, SalesRep);

            var withNames = GlobalFilterDescriber.Describe(resolved, null);
            StringAssert.Contains(withNames, "director@contoso.com");

            var withoutNames = GlobalFilterDescriber.Describe(resolved, null, hidePeople: true);
            Assert.IsFalse(withoutNames.Contains("@"), withoutNames);
            StringAssert.Contains(withoutNames, "one of 2 named people");
            StringAssert.Contains(withoutNames, "the viewer's manager");
            StringAssert.Contains(withoutNames, "includes the viewer");
        }

        [TestMethod]
        public void Picker_ManagementChainCountsAreWorkedOutOncePerCompiledFilter()
        {
            var snapshot = Snapshot();
            var compiled = Compile(Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"Sales\"]}]"), snapshot, SalesRep), snapshot);

            // Walking every matched person's chain costs the hierarchy's depth per person; the picker asks for it
            // on every load, so it is kept with the compiled filter the resolver shares.
            Assert.AreSame(compiled.ChainCounts, compiled.ChainCounts);
            CollectionAssert.AreEqual(snapshot.ChainCountsFor(compiled.MatchesRow), compiled.ChainCounts);
        }

        [TestMethod]
        public void Picker_OffersOnlyValuesThePeopleInTheGlobalFilterHold()
        {
            var snapshot = Snapshot();
            var global = Compile(Resolve(GlobalFilterCodec.Parse("[{\"d\":\"department\",\"v\":[],\"vu\":\"department\"}]"), snapshot, SalesRep), snapshot);

            var departments = UserFilterCatalogue.ListValues(snapshot, "department", null, 200, global);
            Assert.AreEqual(1, departments.Values.Count);
            Assert.AreEqual("Sales", departments.Values[0].Value);
            Assert.AreEqual(3, departments.Values[0].People);
            Assert.AreEqual(0, departments.PeopleWithoutValue);

            // A sign-in name is a person: nobody outside the filter is named.
            var names = UserFilterCatalogue.ListValues(snapshot, "userName", null, 200, global);
            CollectionAssert.AreEquivalent(
                new[] { "director@contoso.com", "rep@contoso.com", "peer@contoso.com" },
                names.Values.Select(v => v.Value).ToArray());

            Assert.AreEqual(3, UserFilterCatalogue.ListDimensions(snapshot, global).People);
        }

        #endregion

        #region Fixture

        private static ResolvedGlobalFilter Resolve(GlobalFilterDefinition definition, UserDirectorySnapshot snapshot, int viewerUserId)
        {
            Assert.IsTrue(snapshot.TryGetRow(viewerUserId, out var row));
            return GlobalFilterResolver.Resolve(definition, snapshot, row);
        }

        private static CompiledUserFilter Compile(ResolvedGlobalFilter resolved, UserDirectorySnapshot snapshot)
        {
            return UserFilterCompiler.Compile(resolved.Expression, snapshot);
        }

        private static void AssertMatches(CompiledUserFilter filter, params int[] expected)
        {
            Assert.IsNotNull(filter, "A global filter with conditions always compiles to a filter.");
            var actual = Everyone.Where(filter.Matches).ToArray();
            CollectionAssert.AreEquivalent(expected, actual,
                "Expected users " + string.Join(", ", expected) + " but matched " + string.Join(", ", actual));
            Assert.AreEqual(expected.Length, filter.MatchedPeople);
        }

        private static UserDirectorySnapshot Snapshot()
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(User(Ceo, "ceo@contoso.com", null, null));
            builder.AddUser(User(SalesDirector, "director@contoso.com", "Sales", Ceo));
            var rep = User(SalesRep, "rep@contoso.com", "Sales", SalesDirector);
            rep.EntraObjectId = RepObjectId;
            builder.AddUser(rep);
            builder.AddUser(User(SalesPeer, "peer@contoso.com", "Sales", SalesDirector));
            builder.AddUser(User(Engineer, "engineer@contoso.com", "Engineering", Ceo));
            builder.AddUser(User(NoDepartment, "nobody@contoso.com", null, Ceo));
            builder.AddUser(User(GreekAnalyst, "analyst@contoso.com", "Οικονομικά", Ceo));
            return builder.Build(Loaded);
        }

        private static UserDirectoryEntry User(int id, string upn, string department, int? manager)
        {
            return new UserDirectoryEntry
            {
                UserId = id,
                UserPrincipalName = upn,
                Department = department,
                ManagerUserId = manager,
                CompanyName = "Contoso",
                AccountEnabled = true,
            };
        }

        #endregion
    }
}
