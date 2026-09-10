using Common.Entities.Copilot;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UnitTests
{
    /// <summary>
    /// The shared reading of a Copilot audit record's AccessedResources entry.
    ///
    /// Microsoft publishes no enumeration for AccessedResources[].Type - the Purview docs describe it
    /// as carrying "values like the filetype extension (pptx, docx, etc.) or ... the type of resource
    /// (for non-SharePoint resources)" - so the contract these tests pin is not "we know every value".
    /// It is: a value we do not recognise stays visibly unclassified, and is never quietly treated as
    /// either content or web. See issues #468 and #469.
    /// </summary>
    [TestClass]
    public class CopilotAccessedResourceTaxonomyTests
    {
        [TestMethod]
        public void FileKindsAndGraphEntitiesAreTenantContent()
        {
            Assert.AreEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("docx"));
            Assert.AreEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("pdf"));
            Assert.AreEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("EmailMessage"));
            Assert.AreEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("TeamsMessage"));
        }

        [TestMethod]
        public void CitationIsAUsageRoleNotContent()
        {
            // The whole of #468 in one assertion. CITATION says the resource was shown to the user as a
            // cited source; it says nothing about what the resource was, so it cannot be charted as a
            // kind of tenant content.
            Assert.AreEqual(CopilotResourceTypeKind.UsageRole, CopilotAccessedResourceTaxonomy.Classify("CITATION"));
            Assert.AreNotEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("CITATION"));
        }

        [TestMethod]
        public void WebSearchQueryIsExternalGrounding()
        {
            Assert.AreEqual(CopilotResourceTypeKind.ExternalGrounding, CopilotAccessedResourceTaxonomy.Classify("WebSearchQuery"));
        }

        [TestMethod]
        public void ClassificationIsCaseInsensitiveAndIgnoresSurroundingSpace()
        {
            Assert.AreEqual(CopilotResourceTypeKind.UsageRole, CopilotAccessedResourceTaxonomy.Classify("citation"));
            Assert.AreEqual(CopilotResourceTypeKind.TenantContent, CopilotAccessedResourceTaxonomy.Classify("DOCX"));
            Assert.AreEqual(CopilotResourceTypeKind.ExternalGrounding, CopilotAccessedResourceTaxonomy.Classify(" WebSearchQuery "));
        }

        [TestMethod]
        public void AValueMicrosoftHasNotUsedYetIsUnclassified()
        {
            // Not a hypothetical: the field is an open Edm.String, and the previous allowlist-based
            // check treated everything outside its list as "web search only".
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified,
                CopilotAccessedResourceTaxonomy.Classify("SomeTypeMicrosoftAddedLater"));
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified,
                CopilotAccessedResourceTaxonomy.Classify("http://schema.skype.com/hyperlink"));
        }

        [TestMethod]
        public void AMissingTypeIsUnclassified()
        {
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified, CopilotAccessedResourceTaxonomy.Classify(null));
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified, CopilotAccessedResourceTaxonomy.Classify(""));
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified, CopilotAccessedResourceTaxonomy.Classify("   "));

            // And the label the reporting query substitutes for one, which is not a Microsoft value.
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified,
                CopilotAccessedResourceTaxonomy.Classify(CopilotAccessedResourceTaxonomy.UnknownTypeLabel));
        }

        [TestMethod]
        public void UnclassifiedIsTheDefaultEnumMember()
        {
            // So a row deserialised without a kind, or written by an older component, reads as
            // "we do not know" rather than as tenant content.
            Assert.AreEqual(CopilotResourceTypeKind.Unclassified, default(CopilotResourceTypeKind));
        }

        [TestMethod]
        public void EveryKindHasItsOwnHeadingAndExplanation()
        {
            // The workbook and the portal both label the groups; a kind that fell through to a shared
            // default would put two different things under one heading.
            var kinds = new[]
            {
                CopilotResourceTypeKind.TenantContent,
                CopilotResourceTypeKind.UsageRole,
                CopilotResourceTypeKind.ExternalGrounding,
                CopilotResourceTypeKind.Unclassified,
            };

            var labels = new System.Collections.Generic.HashSet<string>();
            foreach (var kind in kinds)
            {
                var label = CopilotAccessedResourceTaxonomy.KindLabel(kind);
                Assert.IsFalse(string.IsNullOrWhiteSpace(label), kind + " has no heading.");
                Assert.IsTrue(labels.Add(label), "Two kinds share the heading " + label + ".");
                Assert.IsFalse(string.IsNullOrWhiteSpace(CopilotAccessedResourceTaxonomy.KindExplanation(kind)),
                    kind + " has no explanation.");
            }
        }

        #region SiteUrl host matching

        [TestMethod]
        public void CommercialSharePointAndOneDriveUrlsAreTenantContent()
        {
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.com/sites/sales/Shared%20Documents/report.docx"));

            // OneDrive for Business lives on the tenant's personal SharePoint host, not on a hostname
            // containing "onedrive".
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso-my.sharepoint.com/personal/user_contoso_com/Documents/notes.docx"));
        }

        [TestMethod]
        public void TeamsAsyncGatewayFileUrlsAreTenantContent()
        {
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://eu-prod.asyncgw.teams.microsoft.com/v1/objects/0-eu-d0-0000/views/original/quarterly.xlsx"));
        }

        [TestMethod]
        public void SovereignCloudUrlsAreTenantContent()
        {
            // The second, independent gap in #469: the old substring list only knew the commercial
            // cloud, so every SharePoint reference in a GCC High, DoD or 21Vianet tenant failed the
            // check.
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.us/sites/ops/doc.docx"), "GCC High");
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint-mil.us/sites/ops/doc.docx"), "DoD");
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.dps.mil/sites/ops/doc.docx"), "DoD");
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.cn/sites/ops/doc.docx"), "21Vianet");
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.de/sites/ops/doc.docx"), "Retired Microsoft Cloud Deutschland");
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://gov.teams.microsoft.us/v1/objects/0-gov-d0-0000/views/original/plan.pptx"), "GCC High Teams");
        }

        [TestMethod]
        public void UnicodePathsDoNotBreakHostMatching()
        {
            // Tenant content routinely carries non-Latin file names; the check must look at the host.
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.com/sites/example/Shared Documents/Καλημέρα κόσμε.pdf"));
        }

        [TestMethod]
        public void ATrailingRootDotDoesNotDefeatHostMatching()
        {
            // .NET keeps the trailing root dot on Uri.Host, so a fully-qualified name would otherwise
            // miss every suffix and be read as grounding from outside the tenant.
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://contoso.sharepoint.com./sites/sales/doc.docx"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(
                "https://contoso.sharepoint.com./sites/sales/doc.docx"));
        }

        [TestMethod]
        public void ANonWebSchemeIsEvidenceOfNothingInEitherDirection()
        {
            // Uri populates Host for file: and ftp: too, so without a scheme check a
            // file://contoso.sharepoint.com/... URL counted as positive tenant evidence. A non-web URL
            // tells us nothing about a Microsoft 365 tenant, so BOTH predicates must stay silent.
            var nonWeb = new[]
            {
                "file://contoso.sharepoint.com/x",
                "ftp://contoso.sharepoint.com/x",
                @"file://server/share/doc.docx",
            };

            foreach (var url in nonWeb)
            {
                Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(url), "tenant: " + url);
                Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(url), "external: " + url);
            }
        }

        [TestMethod]
        public void AnAlternativeUnicodeDotSeparatorDoesNotDefeatHostMatching()
        {
            // Uri.Host keeps a U+3002 ideographic full stop verbatim while Uri.IdnHost canonicalises
            // it, so matching on Host alone let a tenant URL be read as external.
            var ideographicDot = "https://contoso" + (char)0x3002 + "sharepoint.com/sites/sales/doc.docx";

            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(ideographicDot));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(ideographicDot));
        }

        [TestMethod]
        public void AMicrosoftOperatedHostIsNotClaimedToBeExternal()
        {
            // IsExternalWebUrl works by exclusion from a list that cannot be complete - Microsoft can
            // add a content host at any time. Treating a new Microsoft-operated host as proof of
            // grounding from OUTSIDE the tenant would recreate the #469 under-estimate by the opposite
            // route, so the honest answer for one is "cannot tell": false from both predicates.
            var microsoftOperated = new[]
            {
                "https://x.usercontent.microsoft/content/abc",
                "https://x.usgovcloud-usercontent.microsoft/content/abc",
                "https://x.sovcloud-usercontent.cn/content/abc",
            };

            foreach (var url in microsoftOperated)
            {
                Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(url),
                    "Must not be claimed as external: " + url);
                Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(url),
                    "Must not be claimed as tenant content either: " + url);
            }
        }

        [TestMethod]
        public void AHostThatMerelyContainsAMicrosoftDomainIsNotTenantContent()
        {
            // The old check was a substring test over the whole URL, so all of these passed it.
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://sharepoint.com.example.invalid/pretend/doc.docx"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://notsharepoint.com/doc.docx"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(
                "https://example.invalid/?redirect=https://contoso.sharepoint.com/doc.docx"));
        }

        [TestMethod]
        public void ConsumerAndPublicWebUrlsAreNotTenantContent()
        {
            // onedrive.live.com is consumer OneDrive - somebody's personal storage, not the tenant's.
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl("https://onedrive.live.com/view.aspx?id=1"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl("https://www.example.com/an/article"));
        }

        [TestMethod]
        public void AMissingOrUnparseableSiteUrlIsNotEvidence()
        {
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(null));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(""));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl("   "));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl("not a url"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsTenantContentUrl("/sites/sales/doc.docx"));
        }

        [TestMethod]
        public void AWebUrlOnSomebodyElsesHostIsExternalEvidence()
        {
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("https://www.example.com/an/article"));
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("http://example.invalid/page"));
            Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("https://onedrive.live.com/view.aspx?id=1"));
        }

        [TestMethod]
        public void ATenantUrlIsNeverAlsoExternalEvidence()
        {
            // The two checks must never both be true, or a resource would be both grounded and not.
            var tenantUrls = new[]
            {
                "https://contoso.sharepoint.com/sites/sales/doc.docx",
                "https://contoso-my.sharepoint.com/personal/user/Documents/notes.docx",
                "https://eu-prod.asyncgw.teams.microsoft.com/v1/objects/0-eu-d0-0000/views/original/q.xlsx",
                "https://contoso.sharepoint.us/sites/ops/doc.docx",
            };

            foreach (var url in tenantUrls)
            {
                Assert.IsTrue(CopilotAccessedResourceTaxonomy.IsTenantContentUrl(url), url);
                Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(url), url);
            }
        }

        [TestMethod]
        public void NoSiteUrlIsNotExternalEvidenceEither()
        {
            // Saying nothing is not the same as saying "somewhere else". Treating the two alike is the
            // conflation that produced #469, so the absence of a URL must stay silent in BOTH
            // directions.
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(null));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl(""));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("   "));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("not a url"));
            Assert.IsFalse(CopilotAccessedResourceTaxonomy.IsExternalWebUrl("/sites/sales/doc.docx"));
        }

        #endregion
    }
}
