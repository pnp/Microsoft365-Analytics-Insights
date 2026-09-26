using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tests for the email-domain dimension: deriving the domain from a UPN, comparing adoption
    /// across the organisations sharing a tenant, and narrowing the whole report to one of them.
    ///
    /// <para>Every figure here ends up in a licence conversation with a specific acquired business,
    /// so the two failure modes that matter are attributing somebody to the wrong organisation and
    /// showing a tenant-wide number under a heading naming one organisation. Both are pinned down
    /// below.</para>
    /// </summary>
    [TestClass]
    public class CopilotAdoptionEmailDomainTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc);

        #region Deriving the domain

        [TestMethod]
        public void Domain_IsThePartAfterTheAtSign()
        {
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.From("alice@contoso.com"));
            Assert.AreEqual("fabrikam.co.uk", CopilotAdoptionEmailDomain.From("bob.smith@fabrikam.co.uk"));
        }

        [TestMethod]
        public void Domain_IsLowerCased_SoOneOrganisationIsNeverTwoRows()
        {
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.From("Alice@Contoso.COM"));
            Assert.AreEqual(
                CopilotAdoptionEmailDomain.From("alice@contoso.com"),
                CopilotAdoptionEmailDomain.From("BOB@CONTOSO.COM"),
                "DNS names are case-insensitive, so two casings of one domain must group together.");
        }

        [TestMethod]
        public void Domain_LowerCasesInvariantly_SoATurkishServerDoesNotSplitADomain()
        {
            // ToLower() under tr-TR maps 'I' to a dotless 'i', which would put INSIGHTS.com and
            // insights.com in different buckets depending on which server ran the report.
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("tr-TR");

                Assert.AreEqual("insights.com", CopilotAdoptionEmailDomain.From("alice@INSIGHTS.com"));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [TestMethod]
        public void Domain_IsNull_WhenThereIsNothingToDeriveItFrom()
        {
            Assert.IsNull(CopilotAdoptionEmailDomain.From(null));
            Assert.IsNull(CopilotAdoptionEmailDomain.From(string.Empty));
            Assert.IsNull(CopilotAdoptionEmailDomain.From("   "));
            Assert.IsNull(CopilotAdoptionEmailDomain.From("no-at-sign-here"));
            Assert.IsNull(CopilotAdoptionEmailDomain.From("trailing@"));
        }

        [TestMethod]
        public void Domain_FallsBackToMail_OnlyWhenTheUpnYieldsNothing()
        {
            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("not-a-upn", "alice@contoso.com"),
                "A UPN that is not in name@domain form should not lose the person entirely.");

            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("alice@contoso.com", "alice@vanity-brand.example"),
                "The sign-in domain wins. A vanity mail domain would split one organisation in two.");
        }

        [TestMethod]
        public void Domain_OfAGuest_IsTheirHomeOrganisation_NotTheInvitingTenant()
        {
            // Entra writes an invited guest as alice_contoso.com#EXT#@fabrikam.onmicrosoft.com. The
            // part after the final '@' is the INVITING tenant and is identical for every guest in
            // the directory, so grouping on it would collapse every partner into one bucket named
            // after the host. This is the whole reason the derivation is not Split('@').Last().
            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("alice_contoso.com#EXT#@fabrikam.onmicrosoft.com"));

            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("first_last_contoso.com#EXT#@fabrikam.onmicrosoft.com"),
                "The local part commonly contains an underscore of its own.");
        }

        [TestMethod]
        public void Domain_OfAGuestWhoseHomeDomainCannotBeRead_IsUnknownRatherThanTheHostTenant()
        {
            // Attributing them to the inviting tenant would silently merge an outsider into the
            // host organisation's adoption figures.
            Assert.IsNull(CopilotAdoptionEmailDomain.From("alias#EXT#@fabrikam.onmicrosoft.com"));
            Assert.IsNull(CopilotAdoptionEmailDomain.From("alice_nodomain#EXT#@fabrikam.onmicrosoft.com"));
        }

        [TestMethod]
        public void IsExternalGuest_RecognisesTheEntraMarker()
        {
            Assert.IsTrue(CopilotAdoptionEmailDomain.IsExternalGuest("alice_contoso.com#EXT#@fabrikam.onmicrosoft.com"));
            Assert.IsFalse(CopilotAdoptionEmailDomain.IsExternalGuest("alice@contoso.com"));
            Assert.IsFalse(CopilotAdoptionEmailDomain.IsExternalGuest(null));
        }

        [TestMethod]
        public void Domain_PrefersTheMailDomainOverTheTenantsOwnOnmicrosoftSuffix()
        {
            // Some tenants pin every UPN to the default suffix and carry the real per-company domain
            // on mail. Taking the UPN there would put every subsidiary in one bucket named after the
            // tenant and silently defeat the whole comparison.
            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("alice@fabrikamgroup.onmicrosoft.com", "alice@contoso.com"));

            // Still returned when mail offers nothing better, rather than dropping the person.
            Assert.AreEqual(
                "fabrikamgroup.onmicrosoft.com",
                CopilotAdoptionEmailDomain.From("alice@fabrikamgroup.onmicrosoft.com", null));
            Assert.AreEqual(
                "fabrikamgroup.onmicrosoft.com",
                CopilotAdoptionEmailDomain.From("alice@fabrikamgroup.onmicrosoft.com", "alice@fabrikamgroup.onmicrosoft.com"));

            Assert.IsTrue(CopilotAdoptionEmailDomain.IsDefaultTenantDomain("contoso.onmicrosoft.com"));
            Assert.IsFalse(CopilotAdoptionEmailDomain.IsDefaultTenantDomain("contoso.com"));
            Assert.IsFalse(CopilotAdoptionEmailDomain.IsDefaultTenantDomain(null));
        }

        [TestMethod]
        public void Domain_OfARealDomainIsNeverOverriddenByMail()
        {
            // The rule above must not weaken the general case: a sign-in domain that names a company
            // always wins, or a vanity mail domain would split one organisation in two.
            Assert.AreEqual(
                "contoso.com",
                CopilotAdoptionEmailDomain.From("alice@contoso.com", "alice@marketing-brand.example"));
        }

        [TestMethod]
        public void Label_NamesTheMissingBucket_SoSeatsNeverVanishFromTheBreakdown()
        {
            Assert.AreEqual(CopilotAdoptionEmailDomain.NoDomainLabel, CopilotAdoptionEmailDomain.Label(null));
            Assert.AreEqual(CopilotAdoptionEmailDomain.NoDomainLabel, CopilotAdoptionEmailDomain.Label("  "));
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.Label("contoso.com"));
        }

        [TestMethod]
        public void Normalise_AcceptsWhatAnAdminWouldActuallyPaste()
        {
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.Normalise("contoso.com"));
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.Normalise("  CONTOSO.com "));
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.Normalise("@contoso.com"));
            Assert.AreEqual("contoso.com", CopilotAdoptionEmailDomain.Normalise("alice@contoso.com"));
            Assert.AreEqual(
                CopilotAdoptionEmailDomain.NoDomainLabel,
                CopilotAdoptionEmailDomain.Normalise(CopilotAdoptionEmailDomain.NoDomainLabel),
                "The no-domain bucket has to be selectable like any other.");
            Assert.IsNull(CopilotAdoptionEmailDomain.Normalise(null));
            Assert.IsNull(CopilotAdoptionEmailDomain.Normalise("   "));
            Assert.IsNull(CopilotAdoptionEmailDomain.Normalise("@"));
        }

        [TestMethod]
        public void Scope_WithNothingUsable_IsTheWholeTenant_NotAnEmptyResult()
        {
            // A hand-edited URL should show the tenant, not an empty dashboard that reads as zero
            // adoption - which is the failure the figures-incomplete banner exists to prevent.
            Assert.IsFalse(CopilotAdoptionScope.ForEmailDomain(null).IsNarrowed);
            Assert.IsFalse(CopilotAdoptionScope.ForEmailDomain("  ").IsNarrowed);
            Assert.IsFalse(CopilotAdoptionScope.ForEmailDomain("@").IsNarrowed);

            var scoped = CopilotAdoptionScope.ForEmailDomain("CONTOSO.com");
            Assert.IsTrue(scoped.IsNarrowed);
            Assert.AreEqual("contoso.com", scoped.EmailDomain);
        }

        #endregion

        #region The breakdown

        [TestMethod]
        public void Breakdown_ComparesTheOrganisationsSharingTheTenant()
        {
            var analysis = TwoCompanyAnalysis();

            Service().FinaliseSummary(analysis);

            var domains = analysis.Summary.EmailDomains;
            var contoso = domains.Single(d => d.Segment == "contoso.com");
            var fabrikam = domains.Single(d => d.Segment == "fabrikam.com");

            Assert.AreEqual(6, contoso.LicensedUsers);
            Assert.AreEqual(5, contoso.ActiveUsers);
            Assert.AreEqual(6, fabrikam.LicensedUsers);
            Assert.AreEqual(1, fabrikam.ActiveUsers);

            Assert.IsTrue(
                fabrikam.AdoptionRatePct < contoso.AdoptionRatePct,
                "The acquired company adopted far less; that difference is the whole point of this view.");
        }

        [TestMethod]
        public void Breakdown_PutsTheWorstAdoptionFirst_AndDomainsWithNoSeatsLast()
        {
            var analysis = TwoCompanyAnalysis();

            // A third organisation using Copilot Chat with no seats at all. It has no adoption rate
            // to rank on, so it must not be sorted as though it were 0% - and it must still appear,
            // because "an acquired business we never licensed" is the most actionable row here.
            analysis.UnlicensedUsers.AddRange(Enumerable.Range(0, 5)
                .Select(i => Unlicensed($"user{i}@northwind.example", 40)));

            Service().FinaliseSummary(analysis);

            var order = analysis.Summary.EmailDomains.Select(d => d.Segment).ToList();

            CollectionAssert.AreEqual(
                new List<string> { "fabrikam.com", "contoso.com", "northwind.example" },
                order);
        }

        [TestMethod]
        public void Breakdown_CountsUnlicensedUseAndLicenceCandidatesAgainstTheirOwnDomain()
        {
            var analysis = TwoCompanyAnalysis();

            Service().FinaliseSummary(analysis);

            var fabrikam = analysis.Summary.EmailDomains.Single(d => d.Segment == "fabrikam.com");

            Assert.AreEqual(2, fabrikam.UnlicensedActiveUsers,
                "Idle seats next to unlicensed Chat use is a seat-allocation problem, and it is only "
                + "visible when both are attributed to the same organisation.");
            Assert.AreEqual(1, fabrikam.RecommendedForLicence);
        }

        [TestMethod]
        public void Breakdown_SuppressesADomainTooSmallToMeanAnything()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.LicensedUsers.Add(Scored("solo@tiny.example", 0, AdoptionBand.NeverUsed));

            Service().FinaliseSummary(analysis);

            Assert.IsFalse(
                analysis.Summary.EmailDomains.Any(d => d.Segment == "tiny.example"),
                "One person is not a data point, and a 0%-of-one row at the top of the table invites a bad call.");
        }

        [TestMethod]
        public void Breakdown_FlagsAnAllGuestDomainAsExternal()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 5)
                .Select(i => Scored($"person{i}_partner.example#EXT#@contoso.onmicrosoft.com", 60, AdoptionBand.Established)));

            Service().FinaliseSummary(analysis);

            var partner = analysis.Summary.EmailDomains.Single(d => d.Segment == "partner.example");

            Assert.IsTrue(partner.External,
                "A guest domain is a partner being collaborated with, not a part of the business that "
                + "can be sent on a training course.");
            Assert.IsFalse(analysis.Summary.EmailDomains.Single(d => d.Segment == "contoso.com").External);
        }

        [TestMethod]
        public void Breakdown_GroupsUsersWithNoDerivableDomainUnderTheirOwnLabel()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 5)
                .Select(i => Scored($"service-account-{i}", 0, AdoptionBand.NeverUsed)));

            Service().FinaliseSummary(analysis);

            var none = analysis.Summary.EmailDomains
                .SingleOrDefault(d => d.Segment == CopilotAdoptionEmailDomain.NoDomainLabel);

            Assert.IsNotNull(none, "Seats that cannot be attributed must stay visible, not silently vanish.");
            Assert.AreEqual(5, none.LicensedUsers);
        }

        [TestMethod]
        public void Breakdown_SeatCountsAddUpToTheScoredPopulation()
        {
            var analysis = TwoCompanyAnalysis();

            Service().FinaliseSummary(analysis);

            Assert.AreEqual(
                analysis.Summary.ScoredUsers,
                analysis.Summary.EmailDomains.Sum(d => d.LicensedUsers),
                "With no domain suppressed, the breakdown has to reconcile with the headline.");
        }

        #endregion

        #region Narrowing the whole report

        [TestMethod]
        public void Narrowing_RecomputesEveryFigureForThatDomainAlone()
        {
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "fabrikam.com", service);

            Assert.AreEqual("fabrikam.com", scoped.Summary.ScopedEmailDomain);
            Assert.AreEqual(6, scoped.Summary.LicensedUsers);
            Assert.AreEqual(6, scoped.Summary.ScoredUsers);
            Assert.AreEqual(1, scoped.Summary.ActiveUsers);
            Assert.AreEqual(6, scoped.LicensedUsers.Count);
            Assert.IsTrue(scoped.LicensedUsers.All(u => u.EmailDomain == "fabrikam.com"));

            Assert.AreEqual(2, scoped.Summary.Unlicensed.ActiveUsers);
            Assert.AreEqual(1, scoped.Summary.RecommendedForLicence);
        }

        [TestMethod]
        public void Narrowing_LeavesTheCachedTenantAnalysisCompletelyUntouched()
        {
            // The tenant analysis is shared between every caller and cached for ten minutes. A scope
            // that wrote into it would corrupt the report for everybody else, and two concurrent
            // requests for different domains would fight over it.
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var licensedBefore = analysis.Summary.LicensedUsers;
            var activeBefore = analysis.Summary.ActiveUsers;
            var rowsBefore = analysis.LicensedUsers.Count;
            var idleBySkuBefore = analysis.Summary.SeatLicenceTypes.Select(l => l.AssignedIdleUsers).ToList();
            var assignedBySkuBefore = analysis.Summary.SeatLicenceTypes.Select(l => l.AssignedUsers).ToList();
            var warningsBefore = analysis.Summary.Warnings.Count;

            // The Cowork path rebuilds CoworkReadiness from the shared signal rows and writes fluency
            // back onto them, so it is the most likely place for a scoped run to corrupt the cache.
            Assert.IsTrue(analysis.Summary.CoworkReadinessAvailable, "The fixture must exercise the Cowork path.");
            var coworkBefore = analysis.CoworkReadiness.Count;
            var coworkScoredBefore = analysis.Summary.CoworkScoredUsers;
            var fluencyBefore = analysis.CoworkSignals.Select(s => s.AdoptionScore).ToList();

            Narrow(analysis, "fabrikam.com", service);

            Assert.AreEqual(licensedBefore, analysis.Summary.LicensedUsers);
            Assert.AreEqual(activeBefore, analysis.Summary.ActiveUsers);
            Assert.AreEqual(rowsBefore, analysis.LicensedUsers.Count);
            Assert.AreEqual(warningsBefore, analysis.Summary.Warnings.Count);
            Assert.AreEqual(coworkBefore, analysis.CoworkReadiness.Count);
            Assert.AreEqual(coworkScoredBefore, analysis.Summary.CoworkScoredUsers);
            CollectionAssert.AreEqual(
                fluencyBefore,
                analysis.CoworkSignals.Select(s => s.AdoptionScore).ToList(),
                "Re-scoring a subset must not rewrite the fluency carried on the shared signal rows.");
            CollectionAssert.AreEqual(
                idleBySkuBefore,
                analysis.Summary.SeatLicenceTypes.Select(l => l.AssignedIdleUsers).ToList(),
                "The per-SKU idle count is written during scoring, so the licence types must be cloned.");
            CollectionAssert.AreEqual(
                assignedBySkuBefore,
                analysis.Summary.SeatLicenceTypes.Select(l => l.AssignedUsers).ToList());
        }

        [TestMethod]
        public void Narrowing_KeepsTheSeatTableInternallyConsistent()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.Summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 99,
                Name = "Microsoft 365 E3",
                SkuPartNumber = "SPE_E3",
                IsCopilotSeat = false,
                AssignedUsers = 4000,
                PurchasedUnits = 4500,
                UnassignedUnits = 500,
            });

            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "fabrikam.com", service);
            var sku = scoped.Summary.SeatLicenceTypes.Single(l => l.IsCopilotSeat);

            Assert.AreEqual(6, sku.AssignedUsers,
                "Assigned and idle must describe the same population, or the idle share looks tiny.");
            Assert.IsTrue(sku.AssignedIdleUsers <= sku.AssignedUsers);
            Assert.AreEqual(500, sku.PurchasedUnits,
                "Seats are bought by the tenant, not by a domain, so the purchase figure stays whole.");

            // A user row only carries the COPILOT seat ids, so recounting a non-Copilot SKU against it
            // would replace a true tenant total with a fabricated zero, next to a tenant-wide purchased
            // figure - an internally impossible row.
            var e3 = scoped.Summary.SeatLicenceTypes.Single(l => !l.IsCopilotSeat);
            Assert.AreEqual(4000, e3.AssignedUsers);
        }

        [TestMethod]
        public void Narrowing_DoesNotLetTheDomainTableContradictTheHeadlineReclaimFigure()
        {
            // Under a usage-report window mismatch the headline holds PROBABLE report-sourced seats
            // back from the reclaim total. A per-domain table that counted them anyway would
            // recommend reclaiming seats the same page says are not reclaimable.
            var analysis = TwoCompanyAnalysis();
            analysis.Summary.DataSources.CopilotUsageReportPeriodDays = 7;

            var service = Service();
            service.FinaliseSummary(analysis);

            Assert.IsTrue(
                analysis.Summary.EmailDomains.Sum(d => d.ReclaimableSeats) <= analysis.Summary.ReclaimableSeats,
                "The domain breakdown must never claim more reclaimable seats than the headline.");
        }

        [TestMethod]
        public void Narrowing_NamesTheSectionsItCouldNotNarrow_AndKeepsThemWhole()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.Summary.UsageByApp.Add(new AdoptionCategory { Label = "Teams", Value = 900 });
            analysis.Summary.WeeklyTrend.Add(new AdoptionSeries { Name = "Active users" });

            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "fabrikam.com", service);

            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.UsageByApp);
            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.WeeklyTrend);
            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.Agents);
            CollectionAssert.Contains(scoped.Summary.UnscopedSections, CopilotAdoptionUnscopedSections.PurchasedSeats);

            Assert.AreEqual(1, scoped.Summary.UsageByApp.Count,
                "Dropping them would lose real information; the UI badges them instead.");
            Assert.AreEqual(1, scoped.Summary.WeeklyTrend.Count);
            Assert.AreEqual(
                analysis.Summary.Agents.KnownAgents,
                scoped.Summary.Agents.KnownAgents,
                "The agent estate is taken whole rather than half-narrowed, so the tab stays consistent.");
        }

        [TestMethod]
        public void Narrowing_InheritsDataSourceWarningsButNotThePopulationOnes()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.Summary.Warnings.Add("The Copilot audit import is behind.");
            analysis.Summary.MarkFiguresIncomplete("Copilot audit data");

            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "fabrikam.com", service);

            CollectionAssert.Contains(
                scoped.Summary.Warnings,
                "The Copilot audit import is behind.",
                "A broken import is as true of one subsidiary as of the whole tenant.");
            Assert.IsTrue(scoped.Summary.FiguresIncomplete);
            CollectionAssert.Contains(scoped.Summary.IncompleteReasons, "Copilot audit data");

            Assert.AreEqual(
                1,
                scoped.Summary.Warnings.Count(w => w == "The Copilot audit import is behind."),
                "Inherited once, not once per re-scoring.");
        }

        [TestMethod]
        public void Narrowing_CarriesTheAnalysisContextSoThePageStillKnowsWhatItIsShowing()
        {
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "fabrikam.com", service);

            Assert.AreEqual(analysis.Summary.GeneratedUtc, scoped.Summary.GeneratedUtc);
            Assert.AreEqual(analysis.Summary.WindowDays, scoped.Summary.WindowDays);
            Assert.AreEqual(analysis.Summary.FromUtc, scoped.Summary.FromUtc);
            Assert.AreEqual(analysis.Summary.ToUtc, scoped.Summary.ToUtc);
            Assert.AreSame(analysis.Summary.Options, scoped.Summary.Options);
            Assert.AreSame(analysis.Summary.DataSources, scoped.Summary.DataSources);
        }

        [TestMethod]
        public void Narrowing_ToTheWholeTenant_ReturnsTheSameAnalysis()
        {
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var same = CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.WholeTenant, service.FinaliseSummary);

            Assert.AreSame(analysis, same, "No narrowing means no work and no copy.");
            Assert.IsNull(analysis.Summary.ScopedEmailDomain);
        }

        [TestMethod]
        public void Narrowing_ToADomainNobodyIsOn_ReportsAnEmptyPopulationRatherThanTheTenant()
        {
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "nobody.example", service);

            Assert.AreEqual(0, scoped.LicensedUsers.Count);
            Assert.AreEqual(0, scoped.Summary.LicensedUsers);
            Assert.AreEqual(0, scoped.Summary.ActiveUsers);
            Assert.AreEqual("nobody.example", scoped.Summary.ScopedEmailDomain);
        }

        [TestMethod]
        public void NarrowingRowsOnly_FiltersEveryPopulationWithoutRescoring()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.Summary.Warnings.Add("The Copilot audit import is behind.");
            var service = Service();
            service.FinaliseSummary(analysis);

            var rows = CopilotAdoptionScopeFilter.FilterRows(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"));

            Assert.AreEqual(6, rows.LicensedUsers.Count);
            Assert.IsTrue(rows.Opportunities.All(o => o.EmailDomain == "fabrikam.com"));
            Assert.IsTrue(rows.UnlicensedUsers.All(u => u.EmailDomain == "fabrikam.com"));

            // Rescoring is skipped, so these describe the run rather than the population.
            Assert.AreEqual("fabrikam.com", rows.Summary.ScopedEmailDomain);
            Assert.AreEqual(analysis.Summary.GeneratedUtc, rows.Summary.GeneratedUtc);
            Assert.AreSame(analysis.Summary.DataSources, rows.Summary.DataSources);
        }

        [TestMethod]
        public void NarrowingRowsOnly_DoesNotStampTenantWidePopulationWarningsOntoADomainExport()
        {
            // These endpoints publish summary.Warnings with the rows and flatten them onto every
            // exported CSV row. A tenant-wide "N users were scored from Microsoft's usage report"
            // stamped on a one-organisation file states a number about a population it does not hold.
            var analysis = TwoCompanyAnalysis();
            CopilotAdoptionWarnings.Add(analysis.Summary, CopilotAdoptionWarningKeys.NoCopilotData);
            var service = Service();
            service.FinaliseSummary(analysis);

            var populationWarning = "Scoring said something about the whole tenant population.";
            analysis.Summary.Warnings.Add(populationWarning);

            var rows = CopilotAdoptionScopeFilter.FilterRows(
                analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"));

            CollectionAssert.Contains(
                rows.Summary.Warnings,
                CopilotAdoptionWarnings.RenderEnglish(CopilotAdoptionWarningKeys.NoCopilotData));
            CollectionAssert.DoesNotContain(rows.Summary.Warnings, populationWarning);
            Assert.AreEqual(rows.Summary.Warnings.Count, rows.Summary.WarningDetails.Count);
            Assert.AreEqual(CopilotAdoptionWarningKeys.NoCopilotData, rows.Summary.WarningDetails.Single().Key);
        }

        [TestMethod]
        public void Narrowing_IsCaseInsensitive_BecauseTheDomainArrivesFromAQueryString()
        {
            var analysis = TwoCompanyAnalysis();
            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, "FABRIKAM.COM", service);

            Assert.AreEqual(6, scoped.Summary.LicensedUsers);
        }

        [TestMethod]
        public void Narrowing_SelectsTheNoDomainBucketLikeAnyOtherDomain()
        {
            var analysis = TwoCompanyAnalysis();
            analysis.LicensedUsers.AddRange(Enumerable.Range(0, 5)
                .Select(i => Scored($"service-account-{i}", 0, AdoptionBand.NeverUsed)));

            var service = Service();
            service.FinaliseSummary(analysis);

            var scoped = Narrow(analysis, CopilotAdoptionEmailDomain.NoDomainLabel, service);

            Assert.AreEqual(5, scoped.Summary.LicensedUsers);
        }

        #endregion

        #region Fixtures

        private static CopilotAdoptionService Service()
        {
            return new CopilotAdoptionService(new CopilotAdoptionOptions { WindowDays = 28 });
        }

        private static CopilotAdoptionAnalysis Narrow(
            CopilotAdoptionAnalysis analysis, string domain, CopilotAdoptionService service)
        {
            return CopilotAdoptionScopeFilter.Apply(
                analysis, CopilotAdoptionScope.ForEmailDomain(domain), service.FinaliseSummary);
        }

        /// <summary>
        /// One tenant, two organisations: the company that ran the rollout and the one it acquired.
        /// Contoso adopted; Fabrikam mostly did not and is running unlicensed Copilot Chat instead.
        /// </summary>
        private static CopilotAdoptionAnalysis TwoCompanyAnalysis()
        {
            var analysis = new CopilotAdoptionAnalysis();
            var summary = analysis.Summary;

            summary.GeneratedUtc = Now;
            summary.WindowDays = 28;
            summary.FromUtc = Now.AddDays(-28);
            summary.ToUtc = Now;
            summary.Options = new CopilotAdoptionOptions { WindowDays = 28 };
            summary.SeatLicenceTypes.Add(new LicenceTypeClassification
            {
                Id = 1,
                Name = "Microsoft 365 Copilot",
                SkuPartNumber = "Microsoft_365_Copilot",
                IsCopilotSeat = true,
                AssignedUsers = 12,
                PurchasedUnits = 500,
                UnassignedUnits = 488,
            });

            analysis.LicensedUsers.AddRange(new[]
            {
                Scored("alice@contoso.com", 82, AdoptionBand.Champion),
                Scored("bob@contoso.com", 64, AdoptionBand.Established),
                Scored("carol@contoso.com", 55, AdoptionBand.Established),
                Scored("dan@contoso.com", 30, AdoptionBand.Developing),
                Scored("erin@contoso.com", 12, AdoptionBand.Trialling),
                Scored("frank@contoso.com", 0, AdoptionBand.NeverUsed),

                Scored("gail@fabrikam.com", 20, AdoptionBand.Trialling),
                Scored("hank@fabrikam.com", 0, AdoptionBand.NeverUsed),
                Scored("iris@fabrikam.com", 0, AdoptionBand.NeverUsed),
                Scored("jack@fabrikam.com", 0, AdoptionBand.NeverUsed),
                Scored("kara@fabrikam.com", 0, AdoptionBand.NeverUsed),
                Scored("liam@fabrikam.com", 0, AdoptionBand.NeverUsed),
            });

            foreach (var user in analysis.LicensedUsers)
            {
                user.SeatLicenceTypeIds = new List<int> { 1 };
            }

            analysis.UnlicensedUsers.AddRange(new[]
            {
                Unlicensed("mia@fabrikam.com", 120),
                Unlicensed("noah@fabrikam.com", 90),
                Unlicensed("olive@contoso.com", 20),
            });

            analysis.Opportunities.AddRange(new[]
            {
                Candidate("mia@fabrikam.com", recommended: true),
                Candidate("noah@fabrikam.com", recommended: false),
                Candidate("olive@contoso.com", recommended: true),
            });

            // Populated so the scoping tests actually execute FinaliseCowork - which REBUILDS
            // CoworkReadiness from these signals and writes fluency back onto them. Without signals
            // that method takes its empty early-exit and the whole Cowork path goes untested, which
            // is precisely where a scoped re-scoring would corrupt the shared cached analysis.
            analysis.CoworkSignals.AddRange(analysis.LicensedUsers.Select(u => new CoworkReadinessSignalRow
            {
                UserId = u.UserId,
                UserPrincipalName = u.UserPrincipalName,
                EmailDomain = u.EmailDomain,
                AccountEnabled = true,
                CoworkInteractions = u.AdoptionScore > 50 ? 12 : 0,
                CoworkActiveDays = u.AdoptionScore > 50 ? 5 : 0,
                TeamsMessages = 300,
                TeamsMeetings = 20,
                EmailsSent = 120,
                EmailsRead = 400,
                FilesViewedOrEdited = 80,
            }));

            return analysis;
        }

        private static LicensedUserAdoptionRow Scored(string upn, double score, AdoptionBand band)
        {
            var row = new LicensedUserAdoptionRow
            {
                UserId = upn.GetHashCode(),
                UserPrincipalName = upn,
                EmailDomain = CopilotAdoptionEmailDomain.From(upn),
                AccountEnabled = true,
                AccountCreatedUtc = Now.AddDays(-200),
                TenureStartUtc = Now.AddDays(-200),
                TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                DaysSinceTenureStart = 200,
                AdoptionScore = score,
                Band = band,
                BandName = CopilotAdoptionScoring.BandDisplayName(band),
                Interactions = band > AdoptionBand.Dormant ? 40 : 0,
                ActiveDays = band > AdoptionBand.Dormant ? 8 : 0,
            };

            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
            row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
            return row;
        }

        private static UnlicensedUsageQueryRow Unlicensed(string upn, long interactions)
        {
            return new UnlicensedUsageQueryRow
            {
                UserId = upn.GetHashCode(),
                UserPrincipalName = upn,
                EmailDomain = CopilotAdoptionEmailDomain.From(upn),
                Interactions = interactions,
                ActiveDays = 6,
                AppsUsed = 1,
                LastInteractionUtc = Now.AddDays(-1),
            };
        }

        private static LicenceOpportunityRow Candidate(string upn, bool recommended)
        {
            return new LicenceOpportunityRow
            {
                UserId = upn.GetHashCode(),
                UserPrincipalName = upn,
                EmailDomain = CopilotAdoptionEmailDomain.From(upn),
                Recommended = recommended,
                OpportunityScore = recommended ? 80 : 20,
            };
        }

        #endregion
    }
}
