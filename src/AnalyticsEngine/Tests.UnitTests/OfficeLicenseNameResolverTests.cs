using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for <see cref="OfficeLicenseNameResolver"/>, which names licence types from the licensing
    /// CSV Microsoft publishes. Its answers become <c>license_types.name</c>, decide which SKUs the
    /// anonymous stats may name, and feed the Copilot seat classification, so a SKU that stops
    /// resolving - or starts resolving differently - is visible to customers.
    /// </summary>
    [TestClass]
    public class OfficeLicenseNameResolverTests
    {
        private const string CsvHeader = "Product_Display_Name,String_Id,GUID,Service_Plan_Name,Service_Plan_Id,Service_Plans_Included_Friendly_Names";
        private const string Nbsp = "\u00A0";

        #region Synthetic CSV

        [TestMethod]
        public void StrayWhitespaceAroundAPartNumberInTheCsv_IsIgnored()
        {
            // The shapes found in Microsoft's own file: a trailing tab, a trailing non-breaking space,
            // a trailing space, and non-breaking spaces where the part number has spaces.
            var resolver = Resolver(
                Row("Contoso Tab Suite", "CONTOSO_TAB\t"),
                Row("Contoso Nbsp Suite", "CONTOSO_NBSP" + Nbsp),
                Row("Contoso Space Suite", " CONTOSO_SPACE "),
                Row("Contoso Suite (no Teams)", "Contoso_w/o" + Nbsp + "Teams" + Nbsp + "Bundle"));

            Assert.AreEqual("Contoso Tab Suite", resolver.GetDisplayNameFor("CONTOSO_TAB"));
            Assert.AreEqual("Contoso Nbsp Suite", resolver.GetDisplayNameFor("CONTOSO_NBSP"));
            Assert.AreEqual("Contoso Space Suite", resolver.GetDisplayNameFor("CONTOSO_SPACE"));
            Assert.AreEqual("Contoso Suite (no Teams)", resolver.GetDisplayNameFor("Contoso_w/o Teams Bundle"),
                "Graph sends ordinary spaces; non-breaking spaces in the CSV must not stop the match.");
        }

        [TestMethod]
        public void StrayWhitespaceInTheRequestedPartNumber_IsIgnored()
        {
            var resolver = Resolver(Row("Contoso Suite", "CONTOSO_SUITE"), Row("Contoso Bundle", "CONTOSO BUNDLE"));

            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor(" CONTOSO_SUITE\t"));
            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor("CONTOSO_SUITE" + Nbsp));
            Assert.AreEqual("Contoso Bundle", resolver.GetDisplayNameFor("CONTOSO" + Nbsp + "BUNDLE"));
        }

        [TestMethod]
        public void Lookup_IsCaseInsensitive()
        {
            var resolver = Resolver(Row("Contoso Suite", "Contoso_Suite"));

            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor("CONTOSO_SUITE"));
            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor("contoso_suite"));
        }

        [TestMethod]
        public void BlankOrUnknownPartNumber_ResolvesToNullWithoutThrowing()
        {
            var resolver = Resolver(Row("Contoso Suite", "CONTOSO_SUITE"));

            Assert.IsNull(resolver.GetDisplayNameFor(null));
            Assert.IsNull(resolver.GetDisplayNameFor(string.Empty));
            Assert.IsNull(resolver.GetDisplayNameFor("   "));
            Assert.IsNull(resolver.GetDisplayNameFor("\t" + Nbsp));
            Assert.IsNull(resolver.GetDisplayNameFor("CONTOSO_UNKNOWN"));
        }

        [TestMethod]
        public void RepeatedPartNumber_KeepsTheFirstRowsName()
        {
            // Licence types are stored by display name, so choosing a different row would rename them.
            var resolver = Resolver(
                Row("Contoso Suite", "CONTOSO_SUITE"),
                Row("Contoso Suite (old name)", "contoso_suite"),
                Row("Contoso Suite (older name)", "CONTOSO_SUITE" + Nbsp));

            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor("CONTOSO_SUITE"));
        }

        [TestMethod]
        public void DisplayName_IsTrimmedAndUsesOrdinarySpaces()
        {
            var resolver = Resolver(Row(" Contoso" + Nbsp + "Suite\t", "CONTOSO_SUITE"));

            Assert.AreEqual("Contoso Suite", resolver.GetDisplayNameFor("CONTOSO_SUITE"));
        }

        [TestMethod]
        public void SkuPartNumbers_AreListedNormalised()
        {
            var resolver = Resolver(Row("Contoso Suite", "CONTOSO_SUITE\t"), Row("Contoso Bundle", "Contoso" + Nbsp + "Bundle"));

            CollectionAssert.AreEquivalent(new[] { "CONTOSO_SUITE", "Contoso Bundle" }, resolver.SkuPartNumbers.ToArray());
        }

        #endregion

        #region The CSV this build ships

        [TestMethod]
        public void ShippedCsv_PartNumbersWithStrayCharactersInMicrosoftsFileResolve()
        {
            // In Microsoft's published file each of these carries a tab, a non-breaking space or a
            // trailing space, and none of them resolved before whitespace was normalised.
            var resolver = new OfficeLicenseNameResolver();

            Assert.AreEqual("Microsoft 365 E3 EEA (no Teams)", resolver.GetDisplayNameFor("O365_w/o Teams Bundle_M3"));
            Assert.AreEqual("Microsoft Teams Shared Devices for Faculty", resolver.GetDisplayNameFor("MCOCAP_FACULTY"));
            Assert.AreEqual("Azure Information Protection Premium P1 for Government", resolver.GetDisplayNameFor("RIGHTSMANAGEMENT_CE_GOV"));
            Assert.AreEqual("Microsoft Entra ID P1_USGOV_GCCHIGH", resolver.GetDisplayNameFor("AAD_PREMIUM_USGOV_GCCHIGH"));
            Assert.AreEqual("Microsoft Defender for Office 365 (Plan 1)_USGOV_GCCHIGH", resolver.GetDisplayNameFor("ATP_ENTERPRISE_USGOV_GCCHIGH"));
            Assert.AreEqual("Microsoft Teams Phone Resource Account_USGOV_GCCHIGH", resolver.GetDisplayNameFor("PHONESYSTEM_VIRTUALUSER_USGOV_GCCHIGH"));
        }

        [TestMethod]
        public void ShippedCsv_NamesRecentSkus()
        {
            // Missing from the copy shipped before the refresh to Microsoft's 19 August 2026 file, so a
            // tenant holding them saw raw part numbers instead of product names.
            var resolver = new OfficeLicenseNameResolver();

            Assert.AreEqual("Microsoft 365 E7", resolver.GetDisplayNameFor("MICROSOFT_365_E7"));
            Assert.AreEqual("Agent 365", resolver.GetDisplayNameFor("AGENT_365"));
            Assert.AreEqual("Microsoft Teams Premium", resolver.GetDisplayNameFor("M365_TEAMS_PREMIUM"));
        }

        [TestMethod]
        public void ShippedCsv_CopilotSeatSkusAreTheReviewedSet()
        {
            // Copilot Adoption counts a licence as a Microsoft 365 Copilot seat by part-number prefix,
            // then falls back to the display name. Refreshing this CSV can therefore start counting a
            // Copilot-branded add-on or trial as a seat - "Microsoft 365 Copilot for Finance (Preview)"
            // would have been one - which inflates the licensed population adoption is measured
            // against. A change here needs a decision, not just a new expected value.
            var resolver = new OfficeLicenseNameResolver();
            var seats = resolver.SkuPartNumbers
                .Where(sku => CopilotLicenceClassifier.IsCopilotSeat(sku, resolver.GetDisplayNameFor(sku)))
                .OrderBy(sku => sku, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "M365_Copilot", "Microsoft_365_Copilot", "Microsoft_365_Copilot_EDU" },
                seats,
                StringComparer.OrdinalIgnoreCase,
                $"The shipped licensing CSV now classifies these SKUs as Microsoft 365 Copilot seats: {string.Join(", ", seats)}. " +
                "Check each one really is a Copilot seat. If it is not, exclude it in CopilotLicenceClassifier; if it is, add it here.");
        }

        #endregion

        private static OfficeLicenseNameResolver Resolver(params string[] rows)
        {
            using (var reader = new StringReader(CsvHeader + "\r\n" + string.Join("\r\n", rows) + "\r\n"))
            {
                return new OfficeLicenseNameResolver(reader);
            }
        }

        private static string Row(string displayName, string skuPartNumber)
        {
            return $"\"{displayName}\",\"{skuPartNumber}\",00000000-0000-0000-0000-000000000000,CONTOSO_PLAN,00000000-0000-0000-0000-000000000000,Contoso Plan";
        }
    }
}
