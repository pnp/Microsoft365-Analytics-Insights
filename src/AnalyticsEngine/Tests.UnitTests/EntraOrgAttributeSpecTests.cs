using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Parsing and validation of the admin-supplied Entra attribute name.
    /// </summary>
    /// <remarks>
    /// This matters more than an ordinary parser test. The parsed <c>$select</c> fragment goes into the
    /// <c>/users/delta</c> request that drives the entire user import, and Microsoft Graph answers an
    /// unknown property with a 400 that fails the <b>whole</b> request - not just that one field. A name
    /// that should have been rejected here would therefore stop user metadata importing altogether.
    /// </remarks>
    [TestClass]
    public class EntraOrgAttributeSpecTests
    {
        private static EntraOrgAttributeSpec Parse(string value)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(
                EntraOrgAttributeSpec.TryParse(value, out spec, out error),
                $"'{value}' should parse, but was rejected with: {error}");
            return spec;
        }

        private static string Reject(string value)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsFalse(
                EntraOrgAttributeSpec.TryParse(value, out spec, out error),
                $"'{value}' should have been rejected but parsed as {spec}.");
            Assert.IsNull(spec, "A rejected value must not also produce a spec.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(error), "A rejection must explain itself to the admin.");
            return error;
        }

        [TestMethod]
        public void ExtensionAttributeSlots_SelectTheContainerAndPathToTheSlot()
        {
            for (var slot = 1; slot <= 15; slot++)
            {
                var spec = Parse("extensionAttribute" + slot);

                Assert.AreEqual(EntraOrgAttributeKind.OnPremisesExtensionAttribute, spec.Kind);
                Assert.AreEqual(
                    "onPremisesExtensionAttributes",
                    spec.SelectFragment,
                    "Graph documents selecting the parent property, not a sub-path.");
                CollectionAssert.AreEqual(
                    new[] { "onPremisesExtensionAttributes", "extensionAttribute" + slot },
                    spec.JsonPath.ToArray());
                Assert.AreEqual(1024, spec.SourceMaxLength);
            }
        }

        [TestMethod]
        public void ExtensionAttributeSlots_OutsideOneToFifteen_AreRejected()
        {
            Reject("extensionAttribute0");
            Reject("extensionAttribute16");
            Reject("extensionAttribute99");
        }

        [TestMethod]
        public void ExtensionAttributeSlot_CasingAndLeadingZeroCollapseToOneCanonicalName()
        {
            // Without this, the same physical slot could be configured twice under two
            // different-looking names - and each spelling would hash differently into the delta-token
            // key, discarding a good token for no reason.
            Assert.AreEqual("extensionAttribute7", Parse("EXTENSIONATTRIBUTE7").Canonical);
            Assert.AreEqual("extensionAttribute7", Parse("extensionattribute07").Canonical);
            Assert.AreEqual("extensionAttribute7", Parse("onPremisesExtensionAttributes.extensionAttribute7").Canonical);
            Assert.AreEqual("extensionAttribute7", Parse("  extensionAttribute7  ").Canonical);
        }

        [TestMethod]
        public void EmployeeOrgData_SelectsTheParentAndKeepsTheSubProperty()
        {
            var spec = Parse("employeeOrgData.costCenter");

            Assert.AreEqual(EntraOrgAttributeKind.EmployeeOrgData, spec.Kind);
            Assert.AreEqual("employeeOrgData", spec.SelectFragment);
            CollectionAssert.AreEqual(new[] { "employeeOrgData", "costCenter" }, spec.JsonPath.ToArray());

            Assert.AreEqual("employeeOrgData.division", Parse("employeeorgdata.DIVISION").Canonical);
        }

        [TestMethod]
        public void EmployeeOrgData_RejectsUnknownSubPropertiesAndTheBareContainer()
        {
            StringAssert.Contains(Reject("employeeOrgData.teamName"), "costCenter");
            StringAssert.Contains(Reject("employeeOrgData"), "container");
            StringAssert.Contains(Reject("onPremisesExtensionAttributes"), "container");
        }

        [TestMethod]
        public void BuiltInProperties_AreSelectedDirectly()
        {
            var spec = Parse("employeeType");

            Assert.AreEqual(EntraOrgAttributeKind.BuiltInProperty, spec.Kind);
            Assert.AreEqual("employeeType", spec.SelectFragment);
            CollectionAssert.AreEqual(new[] { "employeeType" }, spec.JsonPath.ToArray());
            Assert.AreEqual("employeeId", Parse("EMPLOYEEID").Canonical, "Casing should canonicalise.");
        }

        [TestMethod]
        public void BuiltInProperties_ExcludeAnythingAlreadyImportedAsItsOwnDimension()
        {
            // department, jobTitle, companyName, officeLocation, country, state and usageLocation are
            // already imported onto dbo.users and surfaced as dimensions. Offering them again as orgs
            // would duplicate the data and confuse the admin about which one a report is using.
            foreach (var alreadyImported in new[]
                     {
                         "department", "jobTitle", "companyName", "officeLocation",
                         "country", "state", "usageLocation",
                     })
            {
                Reject(alreadyImported);
            }
        }

        [TestMethod]
        public void DirectoryExtension_RequiresTheFullAppIdFormat()
        {
            var spec = Parse("extension_0123456789abcdef0123456789abcdef_costCentre");

            Assert.AreEqual(EntraOrgAttributeKind.DirectoryExtension, spec.Kind);
            Assert.AreEqual("extension_0123456789abcdef0123456789abcdef_costCentre", spec.SelectFragment);
            Assert.AreEqual(
                256,
                spec.SourceMaxLength,
                "A String directory extension caps at 256 characters, unlike an extensionAttribute's 1024.");

            Reject("extension_tooshort_costCentre");
            Reject("extension_0123456789abcdef0123456789abcdeZ_costCentre");
            Reject("extension_0123456789abcdef0123456789abcdef");
            StringAssert.Contains(
                Reject("extension_0123456789abcdef0123456789abcdef_costCentre.sub"),
                "flat property");
        }

        [TestMethod]
        public void DirectoryExtension_AcceptsUnderscoresInTheName()
        {
            // Entra Connect syncs on-premises attributes as directory extensions whose names carry
            // underscores of their own - extension_{appId}_msDS_cloudExtensionAttribute1 - and those are
            // exactly the attributes a customer with an unfinished Entra clean-up is likely to have.
            const string synced = "extension_0123456789abcdef0123456789abcdef_msDS_cloudExtensionAttribute1";

            var spec = Parse(synced);

            Assert.AreEqual(EntraOrgAttributeKind.DirectoryExtension, spec.Kind);
            Assert.AreEqual(synced, spec.SelectFragment);
            Assert.AreEqual(synced, spec.Canonical);
            CollectionAssert.AreEqual(new[] { synced }, spec.JsonPath.ToArray());

            Assert.AreEqual(
                "extension_0123456789abcdef0123456789abcdef_cost_centre_2",
                Parse("extension_0123456789abcdef0123456789abcdef_cost_centre_2").Canonical);
        }

        [TestMethod]
        public void DirectoryExtension_NameMustStartWithALetterOrDigit()
        {
            Reject("extension_0123456789abcdef0123456789abcdef__costCentre");
            Reject("extension_0123456789abcdef0123456789abcdef_");
            Reject("extension_0123456789abcdef0123456789abcdef_cost-centre");
        }

        [TestMethod]
        public void EveryRefusal_CarriesAStableCodeAndTheValueItIsAbout()
        {
            // The portal words these in the reader's language from the code, and quotes the value back
            // from Values - so the code must be the right one and the value must be what was typed,
            // not a server-written sentence.
            AssertRefusal(null, UserOrgMessageCodes.AttributeRequired, null, null);
            AssertRefusal("  ", UserOrgMessageCodes.AttributeRequired, null, null);
            AssertRefusal("extensions/com.contoso.orgData", UserOrgMessageCodes.OpenExtension, null, null);
            AssertRefusal("cost centre", UserOrgMessageCodes.AttributeHasSpaces, "attribute", "cost centre");
            AssertRefusal("extension_tooshort_costCentre", UserOrgMessageCodes.BadDirectoryExtension, "attribute", "extension_tooshort_costCentre");
            AssertRefusal("a.b.c", UserOrgMessageCodes.TooManyDots, "attribute", "a.b.c");
            AssertRefusal("employeeOrgData.", UserOrgMessageCodes.NothingAfterDot, "attribute", "employeeOrgData.");
            AssertRefusal("employeeOrgData.teamName", UserOrgMessageCodes.NotEmployeeOrgDataProperty, "property", "teamName");
            AssertRefusal(
                "extension_0123456789abcdef0123456789abcdef_costCentre.sub",
                UserOrgMessageCodes.DirectoryExtensionSubProperty,
                "attribute",
                "extension_0123456789abcdef0123456789abcdef_costCentre.sub");
            AssertRefusal("9contoso.businessUnit", UserOrgMessageCodes.UnknownContainer, "container", "9contoso");
            AssertRefusal("contoso_orgData.9unit", UserOrgMessageCodes.BadSchemaProperty, "property", "9unit");
            AssertRefusal("employeeOrgData", UserOrgMessageCodes.EmployeeOrgDataContainer, null, null);
            AssertRefusal("onPremisesExtensionAttributes", UserOrgMessageCodes.OnPremisesContainer, null, null);
            AssertRefusal("totallyUnknownProperty", UserOrgMessageCodes.UnsupportedAttribute, "attribute", "totallyUnknownProperty");
            AssertRefusal("department", UserOrgMessageCodes.UnsupportedAttribute, "attribute", "department");
            AssertRefusal("extensionAttribute16", UserOrgMessageCodes.BadOnPremisesAttribute, "attribute", "extensionAttribute16");
            AssertRefusal("extensionAttributeX", UserOrgMessageCodes.BadOnPremisesAttribute, "attribute", "extensionAttributeX");
        }

        [TestMethod]
        public void ANameLongerThanTheColumnIsRefusedRatherThanCut()
        {
            // entra_attribute_name is nvarchar(200), and its parameter would silently cut a longer name
            // down to a different attribute - one that was never tested against Graph.
            const string container = "contoso_orgData.";
            var longest = container + new string('p', EntraOrgAttributeSpec.MaxNameLength - container.Length);

            Assert.AreEqual(longest, Parse("  " + longest + "  ").Canonical, "The limit applies to the trimmed name.");

            EntraOrgAttributeSpec spec;
            EntraOrgAttributeParseFailure failure;
            Assert.IsFalse(EntraOrgAttributeSpec.TryParse(longest + "p", out spec, out failure));
            Assert.AreEqual(UserOrgMessageCodes.AttributeTooLong, failure.Code);
            Assert.AreEqual(EntraOrgAttributeSpec.MaxNameLength, failure.Values["max"], "The limit is a number the portal formats.");
        }

        [TestMethod]
        public void TheStringOverload_ReportsTheSameEnglishAsTheCodedOne()
        {
            EntraOrgAttributeSpec spec;
            string error;
            EntraOrgAttributeParseFailure failure;

            Assert.IsFalse(EntraOrgAttributeSpec.TryParse("employeeOrgData.teamName", out spec, out error));
            Assert.IsFalse(EntraOrgAttributeSpec.TryParse("employeeOrgData.teamName", out spec, out failure));
            Assert.AreEqual(failure.Message, error);

            Assert.IsTrue(EntraOrgAttributeSpec.TryParse("employeeType", out spec, out failure));
            Assert.IsNull(failure, "A name that parses has no failure.");
        }

        private static void AssertRefusal(string value, string code, string valueName, string expected)
        {
            EntraOrgAttributeSpec spec;
            EntraOrgAttributeParseFailure failure;

            Assert.IsFalse(EntraOrgAttributeSpec.TryParse(value, out spec, out failure), $"'{value}' should be refused.");
            Assert.IsNull(spec);
            Assert.IsNotNull(failure);
            Assert.AreEqual(code, failure.Code, $"The code for '{value}'.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(failure.Message), "The English fallback must still be there.");

            if (valueName == null)
            {
                Assert.AreEqual(0, failure.Values.Count, $"'{value}' names no value.");
            }
            else
            {
                Assert.AreEqual(1, failure.Values.Count, $"'{value}' names one value.");
                Assert.AreEqual(expected, failure.Values[valueName], $"The {valueName} quoted for '{value}'.");
            }
        }

        [TestMethod]
        public void SchemaExtension_SelectsTheContainerAndKeepsTheSubProperty()
        {
            var spec = Parse("contoso_orgData.businessUnit");

            Assert.AreEqual(EntraOrgAttributeKind.SchemaExtension, spec.Kind);
            Assert.AreEqual("contoso_orgData", spec.SelectFragment);
            CollectionAssert.AreEqual(new[] { "contoso_orgData", "businessUnit" }, spec.JsonPath.ToArray());
        }

        [TestMethod]
        public void OpenExtensions_AreRejectedWithTheReasonWhy()
        {
            // Open extensions are only retrievable via $expand=extensions, and Graph does not support
            // $expand on /users/delta - so they can never work here, however the admin spells them.
            var error = Reject("extensions/com.contoso.orgData");

            StringAssert.Contains(error, "Open extensions");
            StringAssert.Contains(error, "$expand");
            StringAssert.Contains(error, "/users/delta");
        }

        [TestMethod]
        public void EmptyAndMalformedNamesAreRejected()
        {
            Reject(null);
            Reject("");
            Reject("   ");
            Reject("not a property");
            Reject("a.b.c");
            Reject("employeeOrgData.");
            Reject("totallyUnknownProperty");
        }

        [TestMethod]
        public void BuildSelectFragments_DeduplicatesSoFifteenSlotsCostOneProperty()
        {
            var specs = Enumerable.Range(1, 15)
                .Select(i => Parse("extensionAttribute" + i))
                .ToList();

            var fragments = EntraOrgAttributeSpec.BuildSelectFragments(specs);

            CollectionAssert.AreEqual(
                new[] { "onPremisesExtensionAttributes" },
                fragments.ToArray(),
                "All fifteen slots live under one Graph property, so $select should name it once.");
        }

        [TestMethod]
        public void BuildSelectFragments_IsStableRegardlessOfInputOrder()
        {
            // The fragment list is hashed into the delta-token cache key. If the order moved with the
            // order org types happened to come back from the database, the key would change for no
            // reason - throwing away a valid delta token and forcing a full re-enumeration of every
            // user in the tenant.
            var a = new List<EntraOrgAttributeSpec>
            {
                Parse("employeeType"),
                Parse("extensionAttribute3"),
                Parse("employeeOrgData.costCenter"),
            };
            var b = new List<EntraOrgAttributeSpec>
            {
                Parse("employeeOrgData.division"),
                Parse("employeeType"),
                Parse("extensionAttribute9"),
            };

            CollectionAssert.AreEqual(
                EntraOrgAttributeSpec.BuildSelectFragments(a).ToArray(),
                EntraOrgAttributeSpec.BuildSelectFragments(b).ToArray(),
                "Different slots of the same containers must produce the same $select fragments.");

            CollectionAssert.AreEqual(
                new[] { "employeeOrgData", "employeeType", "onPremisesExtensionAttributes" },
                EntraOrgAttributeSpec.BuildSelectFragments(a).ToArray());
        }

        [TestMethod]
        public void BuildSelectFragments_HandlesNullAndEmptyInput()
        {
            Assert.AreEqual(0, EntraOrgAttributeSpec.BuildSelectFragments(null).Count);
            Assert.AreEqual(0, EntraOrgAttributeSpec.BuildSelectFragments(new EntraOrgAttributeSpec[0]).Count);
            Assert.AreEqual(0, EntraOrgAttributeSpec.BuildSelectFragments(new EntraOrgAttributeSpec[] { null }).Count);
        }

        [TestMethod]
        public void OnPremisesExtensionAttributeNames_ListsAllFifteenSlotsForThePicker()
        {
            var names = EntraOrgAttributeSpec.OnPremisesExtensionAttributeNames();

            Assert.AreEqual(15, names.Count);
            Assert.AreEqual("extensionAttribute1", names[0]);
            Assert.AreEqual("extensionAttribute15", names[14]);

            foreach (var name in names)
            {
                Parse(name);
            }
        }
    }
}
