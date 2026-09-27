using Common.Entities.UserFilters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// What a user filter matches, evaluated against a hand-built directory - no database involved.
    /// </summary>
    [TestClass]
    public class UserFilterCompilerTests
    {
        private static readonly DateTime Loaded = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        // A small synthetic tenant. Ids are deliberately sparse so a row index can never be confused
        // with a user id.
        private const int Ceo = 10;
        private const int SalesDirector = 20;
        private const int SalesRep = 30;
        private const int SalesIntern = 40;
        private const int Engineer = 50;
        private const int GreekAnalyst = 60;
        private const int NoDepartment = 70;
        private const int Guest = 80;
        private const int Disabled = 90;

        private const int CostCentreType = 7;
        private const int RetiredType = 8;

        [TestMethod]
        public void Is_MatchesAnyOfTheValues_CaseInsensitively()
        {
            var filter = Compile("[{\"d\":\"department\",\"v\":[\"sales\",\"ENGINEERING\"]}]");

            AssertMatches(filter, SalesDirector, SalesRep, SalesIntern, Engineer);
        }

        [TestMethod]
        public void IsNot_IncludesPeopleWithNoValue()
        {
            // "Not in Sales" includes the people with no department at all - they are not in Sales. That
            // is how an executive reads the sentence, and it is the complement of "is", so the two always
            // partition the directory.
            var filter = Compile("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[\"Sales\"]}]");

            AssertMatches(filter, Ceo, Engineer, GreekAnalyst, NoDepartment, Guest, Disabled);
        }

        [TestMethod]
        public void IsAndIsNot_PartitionTheDirectory()
        {
            var positive = Compile("[{\"d\":\"department\",\"v\":[\"Sales\",\"Finance\"],\"n\":true}]");
            var negative = Compile("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[\"Sales\",\"Finance\"],\"n\":true}]");

            foreach (var id in AllUsers)
            {
                Assert.AreNotEqual(positive.Matches(id), negative.Matches(id), $"User {id} must be in exactly one of the two.");
            }
        }

        [TestMethod]
        public void NotSet_IsSelectableAlongsideValues()
        {
            var filter = Compile("[{\"d\":\"department\",\"v\":[\"Engineering\"],\"n\":true}]");

            // The CEO has no department either.
            AssertMatches(filter, Ceo, Engineer, NoDepartment);
        }

        [TestMethod]
        public void Contains_MatchesPartOfAValue_CaseInsensitively_IncludingGreek()
        {
            var titles = Compile("[{\"d\":\"jobTitle\",\"op\":\"contains\",\"v\":[\"REP\",\"αναλυτ\"]}]");

            AssertMatches(titles, SalesRep, GreekAnalyst);
        }

        [TestMethod]
        public void NotContains_IsTheComplementOfContains()
        {
            var filter = Compile("[{\"d\":\"jobTitle\",\"op\":\"notContains\",\"v\":[\"intern\"]}]");

            Assert.IsFalse(filter.Matches(SalesIntern));
            Assert.IsTrue(filter.Matches(NoDepartment), "No job title contains nothing, so it does not contain 'intern'.");
            Assert.AreEqual(AllUsers.Length - 1, filter.MatchedPeople);
        }

        [TestMethod]
        public void And_BindsTighterThanOr()
        {
            // department = Sales AND country = UK  OR  company = Fabrikam
            //   == (Sales and UK) or Fabrikam - not Sales and (UK or Fabrikam).
            var filter = Compile(
                "[{\"d\":\"department\",\"v\":[\"Sales\"]},{\"d\":\"country\",\"v\":[\"United Kingdom\"]},"
                + "{\"j\":\"or\",\"d\":\"companyName\",\"v\":[\"Fabrikam\"]}]");

            // SalesRep is Sales + UK; Guest is Fabrikam; SalesDirector is Sales but in the US.
            AssertMatches(filter, SalesRep, SalesIntern, Guest);
        }

        [TestMethod]
        public void AnOrOnTheFirstClause_IsIgnored()
        {
            var filter = Compile("[{\"j\":\"or\",\"d\":\"department\",\"v\":[\"Engineering\"]}]");

            AssertMatches(filter, Engineer);
        }

        [TestMethod]
        public void CustomOrganisations_FilterLikeAnyOtherAttribute()
        {
            var filter = Compile("[{\"d\":\"org:" + CostCentreType + "\",\"v\":[\"CC-100 Retail\"]}]");

            AssertMatches(filter, SalesRep, SalesIntern);
        }

        [TestMethod]
        public void CustomOrganisations_MatchUnicodeValues()
        {
            var filter = Compile("[{\"d\":\"org:" + CostCentreType + "\",\"v\":[\"Αθήνα Λειτουργίες\"]}]");

            AssertMatches(filter, GreekAnalyst);
        }

        [TestMethod]
        public void AnOrganisationTypeThatNoLongerExists_MatchesNobody_WhateverTheOperator_AndIsReported()
        {
            // The type was disabled (so the loader never added it) after the filter was saved in a link.
            // "is not" must not flip to "everyone": that would put back the people the filter excluded.
            var positive = Compile("[{\"d\":\"org:" + RetiredType + "\",\"v\":[\"Anything\"]}]");
            var negative = Compile("[{\"d\":\"org:" + RetiredType + "\",\"op\":\"isNot\",\"v\":[\"Anything\"]}]");
            var notSet = Compile("[{\"d\":\"org:" + RetiredType + "\",\"v\":[],\"n\":true}]");

            Assert.AreEqual(0, positive.MatchedPeople);
            Assert.AreEqual(0, negative.MatchedPeople);
            Assert.AreEqual(0, notSet.MatchedPeople);
            Assert.IsFalse(negative.Matches(999), "Nor does a person the snapshot does not hold.");
            CollectionAssert.AreEqual(new[] { "org:" + RetiredType }, positive.UnknownDimensions.ToArray());

            // In an OR the other groups still stand.
            var either = Compile("[{\"d\":\"org:" + RetiredType + "\",\"op\":\"isNot\",\"v\":[\"x\"]},{\"j\":\"or\",\"d\":\"department\",\"v\":[\"Engineering\"]}]");
            AssertMatches(either, Engineer);
        }

        [TestMethod]
        public void Manager_MatchesDirectReportsOnly()
        {
            var filter = Compile("[{\"d\":\"manager\",\"v\":[\"director@contoso.com\"]}]");

            AssertMatches(filter, SalesRep, SalesIntern);
        }

        [TestMethod]
        public void ManagementChain_MatchesEveryoneBelow_AtAnyLevel_ButNotTheManager()
        {
            var filter = Compile("[{\"d\":\"managementChain\",\"v\":[\"ceo@contoso.com\"]}]");

            AssertMatches(filter, SalesDirector, SalesRep, SalesIntern, Engineer);
        }

        [TestMethod]
        public void ManagementChain_Negated_ExcludesTheWholeOrganisation()
        {
            var filter = Compile("[{\"d\":\"managementChain\",\"op\":\"isNot\",\"v\":[\"director@contoso.com\"]}]");

            Assert.IsFalse(filter.Matches(SalesRep));
            Assert.IsFalse(filter.Matches(SalesIntern));
            Assert.IsTrue(filter.Matches(SalesDirector), "The manager does not report to themselves.");
            Assert.IsTrue(filter.Matches(Ceo));
        }

        [TestMethod]
        public void ManagementChain_TerminatesOnACycle()
        {
            // Entra does not prevent a manager cycle, and a half-finished reorganisation produces them.
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "a@contoso.com", ManagerUserId = 2 });
            builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "b@contoso.com", ManagerUserId = 3 });
            builder.AddUser(new UserDirectoryEntry { UserId = 3, UserPrincipalName = "c@contoso.com", ManagerUserId = 1 });
            builder.AddUser(new UserDirectoryEntry { UserId = 4, UserPrincipalName = "d@contoso.com" });
            var snapshot = builder.Build(Loaded);

            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"managementChain\",\"v\":[\"a@contoso.com\"]}]"), snapshot);

            Assert.IsTrue(filter.Matches(3));
            Assert.IsTrue(filter.Matches(2));
            Assert.IsFalse(filter.Matches(4));
            Assert.IsFalse(filter.Matches(1),
                "Reaching the chosen manager again round the cycle does not put them below themselves.");

            var sizes = UserFilterCatalogue.ListValues(snapshot, UserFilterDimensions.ManagementChain, null, 10);
            Assert.IsTrue(sizes.Values.All(v => v.People <= 3), "A cycle must not inflate an organisation's size without bound.");
        }

        [TestMethod]
        public void ManagementChain_KeepsAChosenManagerWhoReportsToAnotherChosenManager()
        {
            // Two chosen managers where one reports to the other: the junior one IS below the senior one,
            // cycle or no cycle, and must stay in.
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "senior@contoso.com", ManagerUserId = 2 });
            builder.AddUser(new UserDirectoryEntry { UserId = 2, UserPrincipalName = "junior@contoso.com", ManagerUserId = 1 });
            builder.AddUser(new UserDirectoryEntry { UserId = 3, UserPrincipalName = "report@contoso.com", ManagerUserId = 2 });
            var snapshot = builder.Build(Loaded);

            var filter = UserFilterCompiler.Compile(
                UserFilterCodec.Parse("[{\"d\":\"managementChain\",\"v\":[\"senior@contoso.com\",\"junior@contoso.com\"]}]"), snapshot);

            Assert.IsTrue(filter.Matches(3));
            Assert.IsTrue(filter.Matches(2), "junior reports to senior.");
            Assert.IsTrue(filter.Matches(1), "senior reports to junior - the data says so, and both were chosen.");
        }

        [TestMethod]
        public void ManagementChain_NotSet_MeansNoManager()
        {
            var filter = Compile("[{\"d\":\"managementChain\",\"v\":[],\"n\":true}]");

            Assert.IsTrue(filter.Matches(Ceo));
            Assert.IsFalse(filter.Matches(SalesRep));
        }

        [TestMethod]
        public void EmailDomain_AttributesAGuestToTheirHomeOrganisation()
        {
            var filter = Compile("[{\"d\":\"emailDomain\",\"v\":[\"partner.example\"]}]");

            AssertMatches(filter, Guest);
        }

        [TestMethod]
        public void UserType_AndAccountStatus_UseFixedTokens()
        {
            AssertMatches(Compile("[{\"d\":\"userType\",\"v\":[\"guest\"]}]"), Guest);
            AssertMatches(Compile("[{\"d\":\"accountStatus\",\"v\":[\"disabled\"]}]"), Disabled);
        }

        [TestMethod]
        public void AUserTheSnapshotDoesNotHold_IsEvaluatedAsHavingNoValues()
        {
            // Imported after the snapshot was read. Guessing either way would be wrong; "no value for
            // anything" is the only honest reading.
            Assert.IsFalse(Compile("[{\"d\":\"department\",\"v\":[\"Sales\"]}]").Matches(999));
            Assert.IsTrue(Compile("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[\"Sales\"]}]").Matches(999));
            Assert.IsTrue(Compile("[{\"d\":\"department\",\"v\":[],\"n\":true}]").Matches(999));
        }

        [TestMethod]
        public void TrailingSpacesInStoredValues_StillMatch()
        {
            // SQL Server ignores trailing spaces when comparing, so a value stored with one must still
            // match what the picker offers.
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "a@contoso.com", Department = "Sales  " });
            var snapshot = builder.Build(Loaded);

            var filter = UserFilterCompiler.Compile(UserFilterCodec.Parse("[{\"d\":\"department\",\"v\":[\"sales\"]}]"), snapshot);

            Assert.IsTrue(filter.Matches(1));
        }

        [TestMethod]
        public void Compile_EmptyFilter_IsNull()
        {
            Assert.IsNull(UserFilterCompiler.Compile(UserFilterExpression.Empty, Snapshot()));
        }

        [TestMethod]
        public void Echo_CarriesTheNormalisedClausesAndCounts_NotSentences()
        {
            var filter = Compile(
                "[{\"d\":\"department\",\"v\":[\" Sales \"]},{\"j\":\"or\",\"d\":\"org:" + CostCentreType + "\",\"op\":\"isNot\",\"v\":[\"CC-100 Retail\"],\"n\":true}]");

            var echo = filter.ToEcho();

            Assert.AreEqual(2, echo.Clauses.Count);
            Assert.AreEqual("and", echo.Clauses[0].Join);
            Assert.AreEqual("Sales", echo.Clauses[0].Values.Single());
            Assert.AreEqual("or", echo.Clauses[1].Join);
            Assert.AreEqual("isNot", echo.Clauses[1].Operator);
            Assert.IsTrue(echo.Clauses[1].IncludeNotSet);
            Assert.AreEqual(AllUsers.Length, echo.DirectoryPeople);
            Assert.AreEqual(filter.MatchedPeople, echo.MatchedPeople);
            Assert.AreEqual("Cost centre", echo.DimensionNames["org:" + CostCentreType],
                "The page describes a custom organisation type by the admin's name for it.");
            Assert.IsFalse(echo.DimensionNames.ContainsKey(UserFilterDimensions.Department),
                "Entra attributes are labelled by the page, in the reader's language.");
        }

        [TestMethod]
        public void DescribeInEnglish_ReadsAsASentence_WithOrGroupsBracketed()
        {
            var filter = Compile(
                "[{\"d\":\"department\",\"v\":[\"Sales\",\"Marketing\"]},{\"d\":\"country\",\"op\":\"isNot\",\"v\":[\"United States\"]},"
                + "{\"j\":\"or\",\"d\":\"org:" + CostCentreType + "\",\"v\":[\"CC-200 Finance\"],\"n\":true},"
                + "{\"j\":\"or\",\"d\":\"jobTitle\",\"op\":\"contains\",\"v\":[\"engineer\"]},"
                + "{\"j\":\"or\",\"d\":\"userType\",\"v\":[\"guest\"]}]");

            Assert.AreEqual(
                "(Department is Sales or Marketing and Country or region is not United States) or "
                + "(Cost centre is CC-200 Finance or not set) or (Job title contains \"engineer\") or (User type is Guest)",
                filter.DescribeInEnglish());
        }

        [DataTestMethod]
        [DataRow("[{\"d\":\"department\",\"v\":[],\"n\":true}]", "Department is not set")]
        [DataRow("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[],\"n\":true}]", "Department is set")]
        [DataRow("[{\"d\":\"department\",\"op\":\"isNot\",\"v\":[\"Sales\"],\"n\":true}]", "Department is set and is not Sales")]
        [DataRow("[{\"d\":\"jobTitle\",\"op\":\"contains\",\"v\":[\"eng\"],\"n\":true}]", "Job title contains \"eng\" or is not set")]
        [DataRow("[{\"d\":\"jobTitle\",\"op\":\"notContains\",\"v\":[\"eng\"],\"n\":true}]", "Job title is set and does not contain \"eng\"")]
        [DataRow("[{\"d\":\"managementChain\",\"v\":[\"ceo@contoso.com\"]}]", "Management chain includes ceo@contoso.com")]
        [DataRow("[{\"d\":\"managementChain\",\"op\":\"isNot\",\"v\":[\"ceo@contoso.com\"]}]", "Management chain does not include ceo@contoso.com")]
        [DataRow("[{\"d\":\"department\",\"v\":[\"A\",\"B\",\"C\"]}]", "Department is A, B or C")]
        public void DescribeInEnglish_NeverReadsAsADoubleNegative(string encoded, string expected)
        {
            Assert.AreEqual(expected, Compile(encoded).DescribeInEnglish());
        }

        [TestMethod]
        public void Catalogue_ListsEntraAttributesThenCustomTypes_WithCounts()
        {
            var list = UserFilterCatalogue.ListDimensions(Snapshot());

            Assert.AreEqual(AllUsers.Length, list.People);
            CollectionAssert.AreEqual(
                UserFilterDimensions.EntraKeys.Concat(new[] { "org:" + CostCentreType }).ToArray(),
                list.Dimensions.Select(d => d.Key).ToArray());

            var custom = list.Dimensions.Last();
            Assert.AreEqual("custom", custom.Kind);
            Assert.AreEqual("Cost centre", custom.Name);
            Assert.AreEqual(CostCentreType, custom.OrgTypeId);
            Assert.AreEqual(4, custom.PeopleWithValue);
            Assert.IsTrue(custom.SupportsTextMatch);

            var department = list.Dimensions.First();
            Assert.AreEqual("entra", department.Kind);
            Assert.IsNull(department.Name, "Entra attributes are named by the portal, never by the server.");

            Assert.IsFalse(list.Dimensions.Single(d => d.Key == UserFilterDimensions.AccountStatus).SupportsTextMatch);
            Assert.IsTrue(list.Dimensions.Single(d => d.Key == UserFilterDimensions.AccountStatus).FixedValues);
            Assert.IsFalse(list.Dimensions.Single(d => d.Key == UserFilterDimensions.ManagementChain).SupportsTextMatch);
        }

        [TestMethod]
        public void Catalogue_ListsValuesLargestFirst_AndSearchesThem()
        {
            var snapshot = Snapshot();

            var departments = UserFilterCatalogue.ListValues(snapshot, UserFilterDimensions.Department, null, 50);
            Assert.AreEqual("Sales", departments.Values[0].Value);
            Assert.AreEqual(3, departments.Values[0].People);
            Assert.AreEqual(2, departments.PeopleWithoutValue, "The CEO and the person with no department.");

            var searched = UserFilterCatalogue.ListValues(snapshot, UserFilterDimensions.Department, "ENG", 50);
            CollectionAssert.AreEqual(new[] { "Engineering" }, searched.Values.Select(v => v.Value).ToArray());

            var truncated = UserFilterCatalogue.ListValues(snapshot, UserFilterDimensions.Department, null, 1);
            Assert.AreEqual(1, truncated.Values.Count);
            Assert.IsTrue(truncated.Truncated);
            Assert.IsTrue(truncated.TotalMatching > 1);

            Assert.IsNull(UserFilterCatalogue.ListValues(snapshot, "org:" + RetiredType, null, 10));
        }

        [TestMethod]
        public void Catalogue_KeepsEveryFixedTokenInItsNaturalOrder_EvenWhenNobodyHoldsIt()
        {
            var builder = new UserDirectorySnapshotBuilder();
            builder.AddUser(new UserDirectoryEntry { UserId = 1, UserPrincipalName = "a@contoso.com", AccountEnabled = true });
            var snapshot = builder.Build(Loaded);

            var statuses = UserFilterCatalogue.ListValues(snapshot, UserFilterDimensions.AccountStatus, null, 10);

            CollectionAssert.AreEqual(new[] { "enabled", "disabled" }, statuses.Values.Select(v => v.Value).ToArray());
            Assert.AreEqual(0, statuses.Values[1].People);
        }

        [TestMethod]
        public void Catalogue_CountsAManagersWholeOrganisationForTheManagementChain()
        {
            var chain = UserFilterCatalogue.ListValues(Snapshot(), UserFilterDimensions.ManagementChain, null, 10);

            Assert.AreEqual("ceo@contoso.com", chain.Values[0].Value);
            Assert.AreEqual(4, chain.Values[0].People, "The director, both of the director's reports and the engineer.");
            Assert.AreEqual(2, chain.Values.Single(v => v.Value == "director@contoso.com").People);
        }

        #region Fixture

        private static readonly int[] AllUsers =
        {
            Ceo, SalesDirector, SalesRep, SalesIntern, Engineer, GreekAnalyst, NoDepartment, Guest, Disabled,
        };

        private static CompiledUserFilter Compile(string encoded)
        {
            return UserFilterCompiler.Compile(UserFilterCodec.Parse(encoded), Snapshot());
        }

        private static void AssertMatches(CompiledUserFilter filter, params int[] expected)
        {
            var actual = AllUsers.Where(filter.Matches).ToArray();
            CollectionAssert.AreEquivalent(expected, actual,
                "Expected users " + string.Join(", ", expected) + " but matched " + string.Join(", ", actual));
            Assert.AreEqual(expected.Length, filter.MatchedPeople);
        }

        private static UserDirectorySnapshot Snapshot()
        {
            var builder = new UserDirectorySnapshotBuilder();

            builder.AddUser(User(Ceo, "ceo@contoso.com", null, "Chief Executive", "United States", null, company: "Contoso"));
            builder.AddUser(User(SalesDirector, "director@contoso.com", "Sales", "Sales Director", "United States", Ceo));
            builder.AddUser(User(SalesRep, "rep@contoso.com", "Sales", "Sales Rep", "United Kingdom", SalesDirector));
            builder.AddUser(User(SalesIntern, "intern@contoso.com", "sales", "Sales Intern", "United Kingdom", SalesDirector));
            builder.AddUser(User(Engineer, "engineer@contoso.com", "Engineering", "Software Engineer", "Ireland", Ceo));
            builder.AddUser(User(GreekAnalyst, "analyst@contoso.com", "Finance", "Αναλυτής", "Greece", null));
            builder.AddUser(User(NoDepartment, "nobody@contoso.com", null, null, null, null));
            builder.AddUser(User(Guest, "alice_partner.example#EXT#@contoso.onmicrosoft.com", "Finance", "Consultant", "France", null, company: "Fabrikam"));
            var disabled = User(Disabled, "left@contoso.com", "Marketing", "Marketer", "Spain", null);
            disabled.AccountEnabled = false;
            builder.AddUser(disabled);

            builder.AddOrgType(CostCentreType, "Cost centre");
            builder.AddOrgAssignment(SalesRep, CostCentreType, "CC-100 Retail");
            builder.AddOrgAssignment(SalesIntern, CostCentreType, "CC-100 Retail");
            builder.AddOrgAssignment(Engineer, CostCentreType, "CC-300 Engineering");
            builder.AddOrgAssignment(GreekAnalyst, CostCentreType, "Αθήνα Λειτουργίες");

            // An assignment for a type the loader did not add - as if disabled between the two reads.
            builder.AddOrgAssignment(SalesRep, RetiredType, "Anything");

            return builder.Build(Loaded);
        }

        private static UserDirectoryEntry User(
            int id, string upn, string department, string jobTitle, string country, int? manager, string company = "Contoso")
        {
            return new UserDirectoryEntry
            {
                UserId = id,
                UserPrincipalName = upn,
                Department = department,
                JobTitle = jobTitle,
                Country = country,
                ManagerUserId = manager,
                CompanyName = company,
                AccountEnabled = true,
            };
        }

        #endregion
    }
}
