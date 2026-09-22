using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// The link between the configured org attributes, the Graph <c>$select</c>, and the delta-token
    /// cache key.
    /// </summary>
    /// <remarks>
    /// These two facts have to move together. Graph freezes <c>$select</c> when a delta token is minted,
    /// so a token created without an org attribute keeps returning responses without it forever. If the
    /// token key did not follow the selection, adding an org type would appear to work and then silently
    /// never populate for any user who did not otherwise change - which on an established tenant is
    /// almost everybody.
    /// </remarks>
    [TestClass]
    public class GraphUserOrgSelectionTests
    {
        [TestMethod]
        public void NoOrgTypes_ProducesTheExactKeyAndSelectUsedBeforeThisFeature()
        {
            // The upgrade guarantee. Qualifying the key unconditionally would discard every existing
            // customer's delta token and turn their next import into a full re-enumeration of the whole
            // tenant, for a feature they may never turn on.
            foreach (var selection in new[]
                     {
                         GraphUserOrgSelection.None,
                         GraphUserOrgSelection.FromAttributeNames(null),
                         GraphUserOrgSelection.FromAttributeNames(new string[0]),
                     })
            {
                Assert.IsTrue(selection.IsEmpty);
                Assert.AreEqual(string.Empty, selection.DeltaKeyQualifier);
                Assert.AreEqual(
                    GraphUserDeltaQuery.Select,
                    selection.BuildSelect(GraphUserDeltaQuery.Select),
                    "With no org types the request must be byte-identical to the one this product has always issued.");
            }
        }

        [TestMethod]
        public void ConfiguringAnOrgAttribute_ChangesBothTheSelectAndTheKey()
        {
            var selection = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute7" });

            Assert.IsFalse(selection.IsEmpty);
            StringAssert.Contains(selection.BuildSelect(GraphUserDeltaQuery.Select), "onPremisesExtensionAttributes");
            Assert.AreNotEqual(string.Empty, selection.DeltaKeyQualifier);
            StringAssert.StartsWith(selection.DeltaKeyQualifier, "-o");
        }

        [TestMethod]
        public void TheKeyQualifierIsStableAcrossCalls()
        {
            // A qualifier that moved between runs would discard the delta token on every web-job
            // restart. This is why it is a SHA-256 of the fragments and not string.GetHashCode, which
            // is randomised per process on modern .NET.
            var a = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute7", "employeeType" });
            var b = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute7", "employeeType" });

            Assert.AreEqual(a.DeltaKeyQualifier, b.DeltaKeyQualifier);
        }

        [TestMethod]
        public void TheKeyQualifierIgnoresTheOrderTypesComeBackFromTheDatabase()
        {
            var a = GraphUserOrgSelection.FromAttributeNames(new[] { "employeeType", "extensionAttribute7" });
            var b = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute7", "employeeType" });

            Assert.AreEqual(
                a.DeltaKeyQualifier,
                b.DeltaKeyQualifier,
                "Ordering must not invalidate a perfectly good delta token.");
        }

        [TestMethod]
        public void TwoSlotsOfTheSameContainerDoNotChangeTheKey()
        {
            // All fifteen extensionAttribute slots arrive under one Graph property, so switching from
            // slot 3 to slot 9 does not change what is requested - and must not throw the token away.
            var three = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute3" });
            var nine = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute9" });

            Assert.AreEqual(three.DeltaKeyQualifier, nine.DeltaKeyQualifier);
            Assert.AreEqual(three.BuildSelect("id"), nine.BuildSelect("id"));
        }

        [TestMethod]
        public void AddingADifferentContainerDoesChangeTheKey()
        {
            var one = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute3" });
            var two = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute3", "employeeType" });

            Assert.AreNotEqual(
                one.DeltaKeyQualifier,
                two.DeltaKeyQualifier,
                "A genuinely new property must invalidate the token, or it would never be populated.");
        }

        [TestMethod]
        public void BuildSelect_DoesNotDuplicateAPropertyTheBaseQueryAlreadyNames()
        {
            // Graph rejects a repeated property in $select, and that 400 would fail the entire request.
            var selection = GraphUserOrgSelection.FromSpecs(new[] { ParseSpec("employeeType") });

            var built = selection.BuildSelect("id,employeeType,mail");

            Assert.AreEqual("id,employeeType,mail", built);
            Assert.AreEqual(
                1,
                built.Split(',').Count(p => p.Trim() == "employeeType"),
                "The property must appear exactly once.");
        }

        [TestMethod]
        public void BuildSelect_IgnoresCasingWhenDeDuplicating()
        {
            var selection = GraphUserOrgSelection.FromSpecs(new[] { ParseSpec("employeeType") });

            Assert.AreEqual("id,EMPLOYEETYPE", selection.BuildSelect("id,EMPLOYEETYPE"));
        }

        [TestMethod]
        public void BuildSelect_AppendsToTheRealDeltaQueryWithoutLosingAnything()
        {
            var selection = GraphUserOrgSelection.FromAttributeNames(
                new[] { "extensionAttribute1", "employeeOrgData.costCenter" });

            var built = selection.BuildSelect(GraphUserDeltaQuery.Select);
            var properties = built.Split(',').Select(p => p.Trim()).ToList();

            foreach (var original in GraphUserDeltaQuery.Select.Split(',').Select(p => p.Trim()))
            {
                CollectionAssert.Contains(properties, original, $"'{original}' must survive.");
            }

            CollectionAssert.Contains(properties, "onPremisesExtensionAttributes");
            CollectionAssert.Contains(properties, "employeeOrgData");
            Assert.AreEqual(properties.Count, properties.Distinct().Count(), "No property may repeat.");
        }

        [TestMethod]
        public void UnparseableAttributesAreReportedRatherThanThrown()
        {
            // One unreadable row of configuration must not be able to stop the user import.
            var selection = GraphUserOrgSelection.FromAttributeNames(new[] { "extensionAttribute1", "nonsense" });

            CollectionAssert.AreEqual(new[] { "nonsense" }, selection.UnparseableAttributeNames.ToArray());
            CollectionAssert.AreEqual(new[] { "onPremisesExtensionAttributes" }, selection.SelectFragments.ToArray());
        }

        private static EntraOrgAttributeSpec ParseSpec(string name)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(EntraOrgAttributeSpec.TryParse(name, out spec, out error), error);
            return spec;
        }
    }

    /// <summary>
    /// Turning a batch of Graph users into org assignment updates.
    /// </summary>
    [TestClass]
    public class UserOrgMappingRulesTests
    {
        private const string GreekOrgName = "Καλημέρα κόσμε";

        private static GraphUser User(string upn, string extensionAttributesJson = null)
        {
            var user = new GraphUser { UserPrincipalName = upn };
            if (extensionAttributesJson != null)
            {
                user.AdditionalProperties = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<Dictionary<string, Newtonsoft.Json.Linq.JToken>>(extensionAttributesJson);
            }
            return user;
        }

        private static UserOrgTypeAttribute Type(int id, string attribute)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(EntraOrgAttributeSpec.TryParse(attribute, out spec, out error), error);
            return new UserOrgTypeAttribute(id, spec);
        }

        private static Dictionary<string, int> Users(params KeyValuePair<string, int>[] pairs)
        {
            var map = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairs)
            {
                map[pair.Key] = pair.Value;
            }
            return map;
        }

        private static KeyValuePair<string, int> Pair(string upn, int id)
        {
            return new KeyValuePair<string, int>(upn, id);
        }

        [TestMethod]
        public void BuildUpdates_ResolvesAValuePerConfiguredOrgType()
        {
            var graphUsers = new[]
            {
                User("a@contoso.com", @"{ ""onPremisesExtensionAttributes"": { ""extensionAttribute1"": ""CC-1"", ""extensionAttribute2"": ""Retail"" } }"),
            };
            var types = new[] { Type(10, "extensionAttribute1"), Type(11, "extensionAttribute2") };

            var updates = UserOrgMappingRules.BuildUpdates(graphUsers, types, Users(Pair("a@contoso.com", 5)));

            Assert.AreEqual(2, updates.Count);
            Assert.AreEqual("CC-1", updates.Single(u => u.OrgTypeId == 10).OrgValue);
            Assert.AreEqual("Retail", updates.Single(u => u.OrgTypeId == 11).OrgValue);
            Assert.IsTrue(updates.All(u => u.UserId == 5));
        }

        [TestMethod]
        public void BuildUpdates_EmitsANullSoAMissingAttributeClearsTheValue()
        {
            // The user is in this response precisely because something about them changed, so an
            // attribute that is no longer there has genuinely gone - and the assignment must go with it.
            var graphUsers = new[] { User("a@contoso.com", @"{ ""onPremisesExtensionAttributes"": { } }") };

            var updates = UserOrgMappingRules.BuildUpdates(
                graphUsers, new[] { Type(10, "extensionAttribute1") }, Users(Pair("a@contoso.com", 5)));

            Assert.AreEqual(1, updates.Count);
            Assert.IsNull(updates[0].OrgValue);
        }

        [TestMethod]
        public void BuildUpdates_TreatsAnExplicitNullTheSameAsAnAbsentKey()
        {
            var absent = UserOrgMappingRules.BuildUpdates(
                new[] { User("a@contoso.com", "{ }") },
                new[] { Type(10, "extensionAttribute1") },
                Users(Pair("a@contoso.com", 5)));

            var explicitNull = UserOrgMappingRules.BuildUpdates(
                new[] { User("a@contoso.com", @"{ ""onPremisesExtensionAttributes"": { ""extensionAttribute1"": null } }") },
                new[] { Type(10, "extensionAttribute1") },
                Users(Pair("a@contoso.com", 5)));

            Assert.IsNull(absent.Single().OrgValue);
            Assert.IsNull(explicitNull.Single().OrgValue);
        }

        [TestMethod]
        public void BuildUpdates_ProducesNothingForUsersNotInTheBatch()
        {
            // This is what stops a routine delta cycle, which returns only changed users, from wiping
            // the org values of the whole tenant.
            var updates = UserOrgMappingRules.BuildUpdates(
                new[] { User("changed@contoso.com", @"{ ""employeeType"": ""Staff"" }") },
                new[] { Type(10, "employeeType") },
                Users(Pair("changed@contoso.com", 1), Pair("untouched@contoso.com", 2)));

            Assert.AreEqual(1, updates.Count);
            Assert.AreEqual(1, updates[0].UserId);
        }

        [TestMethod]
        public void BuildUpdates_SkipsGraphUsersWithNoDatabaseRow()
        {
            var updates = UserOrgMappingRules.BuildUpdates(
                new[] { User("ghost@contoso.com", @"{ ""employeeType"": ""Staff"" }") },
                new[] { Type(10, "employeeType") },
                Users(Pair("someone-else@contoso.com", 1)));

            Assert.AreEqual(0, updates.Count);
        }

        [TestMethod]
        public void BuildUpdates_MatchesUpnsCaseInsensitively()
        {
            var updates = UserOrgMappingRules.BuildUpdates(
                new[] { User("Person@Contoso.com", @"{ ""employeeType"": ""Staff"" }") },
                new[] { Type(10, "employeeType") },
                Users(Pair("person@contoso.com", 7)));

            Assert.AreEqual(7, updates.Single().UserId);
        }

        [TestMethod]
        public void BuildUpdates_NormalisesTheValue()
        {
            var updates = UserOrgMappingRules.BuildUpdates(
                new[] { User("a@contoso.com", @"{ ""employeeType"": ""   "" }") },
                new[] { Type(10, "employeeType") },
                Users(Pair("a@contoso.com", 1)));

            Assert.IsNull(updates.Single().OrgValue, "A whitespace-only attribute means no value.");
        }

        [TestMethod]
        public void BuildUpdates_PreservesNonLatinValues()
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(
                new Dictionary<string, object> { { "employeeType", GreekOrgName } });

            var updates = UserOrgMappingRules.BuildUpdates(
                new[] { User("a@contoso.com", json) },
                new[] { Type(10, "employeeType") },
                Users(Pair("a@contoso.com", 1)));

            Assert.AreEqual(GreekOrgName, updates.Single().OrgValue);
        }

        [TestMethod]
        public void BuildUpdates_HandlesNullAndEmptyInputs()
        {
            Assert.AreEqual(0, UserOrgMappingRules.BuildUpdates(null, new[] { Type(10, "employeeType") }, Users()).Count);
            Assert.AreEqual(0, UserOrgMappingRules.BuildUpdates(new GraphUser[0], null, Users()).Count);
            Assert.AreEqual(0, UserOrgMappingRules.BuildUpdates(new GraphUser[0], new UserOrgTypeAttribute[0], Users()).Count);
            Assert.AreEqual(0, UserOrgMappingRules.BuildUpdates(new GraphUser[] { null }, new[] { Type(10, "employeeType") }, Users()).Count);
        }

        [TestMethod]
        public void ParseOrgTypes_SkipsAndReportsTypesWhoseAttributeNoLongerParses()
        {
            IReadOnlyList<string> skipped;

            var parsed = UserOrgMappingRules.ParseOrgTypes(
                new[]
                {
                    new UserOrgType { Id = 1, Name = "Good", EntraAttributeName = "extensionAttribute1" },
                    new UserOrgType { Id = 2, Name = "Broken", EntraAttributeName = "nonsense" },
                },
                out skipped);

            Assert.AreEqual(1, parsed.Count);
            Assert.AreEqual(1, parsed[0].OrgTypeId);
            CollectionAssert.AreEqual(new[] { "Broken" }, skipped.ToArray());
        }
    }
}
