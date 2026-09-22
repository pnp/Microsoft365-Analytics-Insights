using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The pure rules: normalising values, digging a configured attribute out of Graph's JSON, and
    /// collapsing a batch down to one update per (user, org type) slot.
    /// </summary>
    [TestClass]
    public class UserOrgRulesTests
    {
        /// <summary>
        /// A Greek org name. Org values come from a customer tenant and routinely carry non-Latin
        /// scripts, which is why every column that stores one is <c>nvarchar</c>.
        /// </summary>
        private const string GreekOrgName = "Καλημέρα κόσμε";

        private static IDictionary<string, JToken> Props(string json)
        {
            return JsonConvert.DeserializeObject<Dictionary<string, JToken>>(json);
        }

        private static EntraOrgAttributeSpec Spec(string name)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(EntraOrgAttributeSpec.TryParse(name, out spec, out error), error);
            return spec;
        }

        #region NormaliseOrgValue

        [TestMethod]
        public void NormaliseOrgValue_TrimsAndTreatsBlankAsNoValue()
        {
            Assert.AreEqual("Retail", UserOrgRules.NormaliseOrgValue("  Retail  "));
            Assert.IsNull(UserOrgRules.NormaliseOrgValue(null));
            Assert.IsNull(UserOrgRules.NormaliseOrgValue(""));
            Assert.IsNull(UserOrgRules.NormaliseOrgValue("   "));
            Assert.IsNull(UserOrgRules.NormaliseOrgValue("\t\r\n"));
        }

        [TestMethod]
        public void NormaliseOrgValue_PreservesNonLatinScripts()
        {
            Assert.AreEqual(GreekOrgName, UserOrgRules.NormaliseOrgValue("  " + GreekOrgName + "  "));
        }

        [TestMethod]
        public void NormaliseOrgValue_TruncatesRatherThanDroppingTheUser()
        {
            // Dropping a user out of their org because somebody pasted an essay into a spreadsheet cell
            // would be a worse outcome than storing a shortened name, so this truncates - and
            // WouldTruncate lets the caller say so.
            var tooLong = new string('x', UserOrgRules.MaxOrgValueLength + 50);

            var normalised = UserOrgRules.NormaliseOrgValue(tooLong);

            Assert.AreEqual(UserOrgRules.MaxOrgValueLength, normalised.Length);
            Assert.IsTrue(UserOrgRules.WouldTruncate(tooLong));
            Assert.IsFalse(UserOrgRules.WouldTruncate("Retail"));
        }

        [TestMethod]
        public void NormaliseOrgValue_TruncationDoesNotLeaveTrailingWhitespace()
        {
            // SQL Server ignores trailing spaces when comparing, so a value stored with them would
            // match a lookup that does not have them - and then a second import would create a
            // near-duplicate value row. Trimming after the cut keeps stored and compared identical.
            var value = new string('x', UserOrgRules.MaxOrgValueLength - 1) + "   tail";

            var normalised = UserOrgRules.NormaliseOrgValue(value);

            Assert.AreEqual(normalised, normalised.TrimEnd());
        }

        #endregion

        #region NormaliseUpn / org type name

        [TestMethod]
        public void NormaliseUpn_TrimsAndRejectsOverLongOrBlank()
        {
            Assert.AreEqual("person@contoso.com", UserOrgRules.NormaliseUpn(" person@contoso.com "));
            Assert.IsNull(UserOrgRules.NormaliseUpn(null));
            Assert.IsNull(UserOrgRules.NormaliseUpn("   "));
            Assert.IsNull(UserOrgRules.NormaliseUpn(new string('a', UserOrgRules.MaxUpnLength + 1)));
        }

        [TestMethod]
        public void NormaliseUpn_LeavesCaseAlone()
        {
            // The database collation is case-insensitive and every in-memory lookup uses
            // OrdinalIgnoreCase, so lower-casing would allocate a fresh string per row - 200,000 of
            // them on a large tenant - and buy nothing.
            Assert.AreEqual("Person@Contoso.com", UserOrgRules.NormaliseUpn("Person@Contoso.com"));
        }

        [TestMethod]
        public void TryNormaliseOrgTypeName_TrimsAndEnforcesTheColumnWidth()
        {
            string name;
            string error;

            Assert.IsTrue(UserOrgRules.TryNormaliseOrgTypeName("  Cost Centre ", out name, out error));
            Assert.AreEqual("Cost Centre", name);

            Assert.IsFalse(UserOrgRules.TryNormaliseOrgTypeName("   ", out name, out error));
            Assert.IsFalse(string.IsNullOrWhiteSpace(error));

            Assert.IsFalse(UserOrgRules.TryNormaliseOrgTypeName(
                new string('n', UserOrgRules.MaxOrgTypeNameLength + 1), out name, out error));
        }

        #endregion

        #region ExtractRawValue

        [TestMethod]
        public void ExtractRawValue_ReadsANestedExtensionAttributeSlot()
        {
            var props = Props(@"{ ""onPremisesExtensionAttributes"": { ""extensionAttribute3"": ""CC-1042"" } }");

            Assert.AreEqual("CC-1042", UserOrgRules.ExtractRawValue(props, Spec("extensionAttribute3")));
        }

        [TestMethod]
        public void ExtractRawValue_ReadsAFlatDirectoryExtension()
        {
            var props = Props(@"{ ""extension_0123456789abcdef0123456789abcdef_costCentre"": ""Retail"" }");

            Assert.AreEqual(
                "Retail",
                UserOrgRules.ExtractRawValue(props, Spec("extension_0123456789abcdef0123456789abcdef_costCentre")));
        }

        [TestMethod]
        public void ExtractRawValue_AbsentKeyAndExplicitNullAreBothNoValue()
        {
            // Graph omits a property that has never been set, and returns null for one that has been
            // cleared - and for directory extensions the docs do not commit to which you get. Treating
            // them differently would be reading meaning into an implementation detail.
            var spec = Spec("extensionAttribute1");

            Assert.IsNull(UserOrgRules.ExtractRawValue(Props("{ }"), spec), "Absent container.");
            Assert.IsNull(
                UserOrgRules.ExtractRawValue(Props(@"{ ""onPremisesExtensionAttributes"": { } }"), spec),
                "Container present, slot absent.");
            Assert.IsNull(
                UserOrgRules.ExtractRawValue(Props(@"{ ""onPremisesExtensionAttributes"": { ""extensionAttribute1"": null } }"), spec),
                "Slot present but null.");
            Assert.IsNull(
                UserOrgRules.ExtractRawValue(Props(@"{ ""onPremisesExtensionAttributes"": null }"), spec),
                "Container present but null.");
        }

        [TestMethod]
        public void ExtractRawValue_IsCaseInsensitiveOnPropertyNames()
        {
            var props = Props(@"{ ""onpremisesextensionattributes"": { ""EXTENSIONATTRIBUTE5"": ""Ops"" } }");

            Assert.AreEqual("Ops", UserOrgRules.ExtractRawValue(props, Spec("extensionAttribute5")));
        }

        [TestMethod]
        public void ExtractRawValue_AcceptsANumericValue()
        {
            // A cost centre of 4021 is an ordinary org value; refusing it because the JSON happened to
            // be unquoted would be surprising.
            var props = Props(@"{ ""employeeOrgData"": { ""costCenter"": 4021 } }");

            Assert.AreEqual("4021", UserOrgRules.ExtractRawValue(props, Spec("employeeOrgData.costCenter")));
        }

        [TestMethod]
        public void ExtractRawValue_RejectsObjectsAndArrays()
        {
            var spec = Spec("employeeType");

            Assert.IsNull(UserOrgRules.ExtractRawValue(Props(@"{ ""employeeType"": { ""a"": 1 } }"), spec));
            Assert.IsNull(UserOrgRules.ExtractRawValue(Props(@"{ ""employeeType"": [ ""a"" ] }"), spec));
        }

        [TestMethod]
        public void ExtractRawValue_PreservesNonLatinScripts()
        {
            var props = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(
                JsonConvert.SerializeObject(new Dictionary<string, object>
                {
                    { "employeeType", GreekOrgName },
                }));

            Assert.AreEqual(GreekOrgName, UserOrgRules.ExtractRawValue(props, Spec("employeeType")));
        }

        [TestMethod]
        public void ExtractRawValue_HandlesNullInputsWithoutThrowing()
        {
            Assert.IsNull(UserOrgRules.ExtractRawValue(null, Spec("employeeType")));
            Assert.IsNull(UserOrgRules.ExtractRawValue(Props("{ }"), null));
        }

        #endregion

        #region ParseSpecs

        [TestMethod]
        public void ParseSpecs_SkipsUnparseableValuesInsteadOfThrowing()
        {
            // Throwing here would take down the whole user import, which is precisely the failure this
            // feature exists to avoid. A stored value that no longer parses is reported, not fatal.
            IReadOnlyList<string> unparseable;

            var specs = UserOrgRules.ParseSpecs(
                new[] { "extensionAttribute1", "nonsense", "employeeType" },
                out unparseable);

            CollectionAssert.AreEqual(
                new[] { "extensionAttribute1", "employeeType" },
                specs.Select(s => s.Canonical).ToArray());
            CollectionAssert.AreEqual(new[] { "nonsense" }, unparseable.ToArray());
        }

        [TestMethod]
        public void ParseSpecs_HandlesNull()
        {
            IReadOnlyList<string> unparseable;

            var specs = UserOrgRules.ParseSpecs(null, out unparseable);

            Assert.AreEqual(0, specs.Count);
            Assert.AreEqual(0, unparseable.Count);
        }

        #endregion

        #region DeduplicateUpdates

        [TestMethod]
        public void DeduplicateUpdates_KeepsTheLastUpdateForASlot()
        {
            // user_org_assignments is keyed on (user_id, org_type_id), so two rows for one slot in a
            // single bulk batch would violate the primary key. Last-wins matches what an admin expects
            // from a spreadsheet that lists somebody twice: the later line is the correction.
            var updates = new List<UserOrgAssignmentUpdate>
            {
                new UserOrgAssignmentUpdate(1, 10, "First"),
                new UserOrgAssignmentUpdate(2, 10, "Other"),
                new UserOrgAssignmentUpdate(1, 10, "Second"),
                new UserOrgAssignmentUpdate(1, 11, "DifferentType"),
            };

            int duplicates;
            var result = UserOrgRules.DeduplicateUpdates(updates, out duplicates);

            Assert.AreEqual(1, duplicates);
            Assert.AreEqual(3, result.Count);

            var slot = result.Single(u => u.UserId == 1 && u.OrgTypeId == 10);
            Assert.AreEqual("Second", slot.OrgValue);

            Assert.AreEqual(
                "DifferentType",
                result.Single(u => u.UserId == 1 && u.OrgTypeId == 11).OrgValue,
                "A different org type is a different slot and must survive.");
        }

        [TestMethod]
        public void DeduplicateUpdates_PreservesFirstAppearanceOrdering()
        {
            var updates = new List<UserOrgAssignmentUpdate>
            {
                new UserOrgAssignmentUpdate(1, 10, "A"),
                new UserOrgAssignmentUpdate(2, 10, "B"),
                new UserOrgAssignmentUpdate(1, 10, "A2"),
            };

            int duplicates;
            var result = UserOrgRules.DeduplicateUpdates(updates, out duplicates);

            Assert.AreEqual(1, result[0].UserId);
            Assert.AreEqual("A2", result[0].OrgValue, "The surviving value replaces in place.");
            Assert.AreEqual(2, result[1].UserId);
        }

        [TestMethod]
        public void DeduplicateUpdates_ACollapsedSlotCanEndUpClearingTheValue()
        {
            var updates = new List<UserOrgAssignmentUpdate>
            {
                new UserOrgAssignmentUpdate(1, 10, "Retail"),
                new UserOrgAssignmentUpdate(1, 10, null),
            };

            int duplicates;
            var result = UserOrgRules.DeduplicateUpdates(updates, out duplicates);

            Assert.AreEqual(1, result.Count);
            Assert.IsNull(result[0].OrgValue, "The last line wins even when it is a clear.");
        }

        [TestMethod]
        public void DeduplicateUpdates_HandlesNullAndEmptyInput()
        {
            int duplicates;

            Assert.AreEqual(0, UserOrgRules.DeduplicateUpdates(null, out duplicates).Count);
            Assert.AreEqual(0, duplicates);

            Assert.AreEqual(0, UserOrgRules.DeduplicateUpdates(new UserOrgAssignmentUpdate[0], out duplicates).Count);

            var withNull = UserOrgRules.DeduplicateUpdates(new UserOrgAssignmentUpdate[] { null }, out duplicates);
            Assert.AreEqual(0, withNull.Count, "A null entry is skipped, not counted as a duplicate.");
            Assert.AreEqual(0, duplicates);
        }

        #endregion
    }
}
