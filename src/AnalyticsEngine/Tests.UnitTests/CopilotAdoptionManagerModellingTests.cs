using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Tests.UnitTests
{
    /// <summary>
    /// Do people managers use Copilot themselves, and how do their direct reports compare (#641)?
    /// </summary>
    /// <remarks>
    /// Every case is a hand-built organisation, so each expected figure can be worked out on paper.
    /// Synthetic data only: Contoso names and made-up ids.
    /// </remarks>
    [TestClass]
    public class CopilotAdoptionManagerModellingTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        private const string Finance = "Contoso Finance";
        private const string GreekDepartment = "Καλημέρα κόσμε";

        #region Manager status

        [TestMethod]
        public void AManagerWithASeat_IsJudgedByTheirOwnScoredRow()
        {
            var analysis = Analysis(new[]
            {
                Seat(1, AdoptionBand.Established),          // manager with a seat, using Copilot
                Seat(2, AdoptionBand.NeverUsed),            // manager with a seat, not using it
                Seat(11, AdoptionBand.Champion, manager: 1),
                Seat(12, AdoptionBand.Trialling, manager: 1),
                Seat(13, AdoptionBand.NeverUsed, manager: 1),
                Seat(21, AdoptionBand.Developing, manager: 2),
                Seat(22, AdoptionBand.Dormant, manager: 2),
            });

            var summary = Finalise(analysis);

            Assert.AreEqual(5, summary.ReportsWithManager, "The two managers have no recorded manager of their own.");
            Assert.AreEqual(2, summary.ManagersStatusKnown);
            Assert.AreEqual(0, summary.ManagersStatusUnknown);
            Assert.AreEqual(1, summary.ManagersActive);
            Assert.AreEqual(50d, summary.ManagersActivePct);

            Assert.AreEqual(3, summary.ReportsManagerActive);
            Assert.AreEqual(66.7, summary.ReportsActiveRatePctManagerActive, "Champion and Trialling are active; Never used is not.");
            Assert.AreEqual(33.3, summary.ReportsHabitRatePctManagerActive, "Only the Champion has a habit (Established and above).");
            Assert.AreEqual(2, summary.ReportsManagerInactive);
            Assert.AreEqual(50d, summary.ReportsActiveRatePctManagerInactive, "Developing is active; Dormant is not.");
            Assert.AreEqual(0d, summary.ReportsHabitRatePctManagerInactive);

            Assert.AreEqual(3, summary.ReportsManagerActiveLicensed, "Both managers hold a seat.");
            Assert.AreEqual(66.7, summary.ReportsActiveRatePctManagerActiveLicensed);
            Assert.AreEqual(2, summary.ReportsManagerInactiveLicensed);
            Assert.AreEqual(0, summary.ReportsManagerActiveUnlicensed);
            Assert.IsNull(summary.ReportsActiveRatePctManagerActiveUnlicensed, "Nobody to divide: blank, not 0%.");
            Assert.AreEqual(0, summary.ReportsManagerInactiveUnlicensed);
            Assert.IsNull(summary.ReportsHabitRatePctManagerInactiveUnlicensed);
        }

        [TestMethod]
        public void AManagerWithoutASeat_IsActiveOnTheUnlicensedList_AndNotActiveWhenAbsentFromAWholeOne()
        {
            var analysis = Analysis(
                new[]
                {
                    Seat(11, AdoptionBand.Established, manager: 101),
                    Seat(12, AdoptionBand.NeverUsed, manager: 101),
                    Seat(21, AdoptionBand.Trialling, manager: 102),
                },
                chat: new[] { ChatUser(101) });

            var summary = Finalise(analysis);

            Assert.AreEqual(2, summary.ManagersStatusKnown);
            Assert.AreEqual(1, summary.ManagersActive, "Copilot Chat without a seat is Copilot use.");
            Assert.AreEqual(2, summary.ReportsManagerActiveUnlicensed);
            Assert.AreEqual(50d, summary.ReportsActiveRatePctManagerActiveUnlicensed);
            Assert.AreEqual(50d, summary.ReportsHabitRatePctManagerActiveUnlicensed);
            Assert.AreEqual(1, summary.ReportsManagerInactiveUnlicensed,
                "Absent from a complete unlicensed list, with no seat: known not to have used Copilot.");
            Assert.AreEqual(0, summary.ReportsManagerActiveLicensed);
            Assert.AreEqual(0, summary.ReportsManagerInactiveLicensed);
        }

        [TestMethod]
        public void ATruncatedUnlicensedList_LeavesAManagerWithoutASeatUnknown_NeverInactive()
        {
            var analysis = Analysis(
                new[]
                {
                    Seat(11, AdoptionBand.Established, manager: 101),
                    Seat(21, AdoptionBand.Trialling, manager: 102),
                    Seat(22, AdoptionBand.NeverUsed, manager: 102),
                },
                chat: new[] { ChatUser(101) });
            analysis.Summary.Unlicensed.Truncated = true;

            var summary = Finalise(analysis);

            Assert.AreEqual(1, summary.ManagersActive, "Being ON a truncated list still proves use.");
            Assert.AreEqual(1, summary.ManagersStatusKnown);
            Assert.AreEqual(1, summary.ManagersStatusUnknown,
                "Missing from a list that stopped at its cap says nothing: unknown, not 'did not use Copilot'.");
            Assert.AreEqual(2, summary.ReportsManagerUnknown);
            Assert.AreEqual(0, summary.ReportsManagerInactive, "An unknown manager's team is in neither side of the comparison.");
            Assert.AreEqual(1, summary.ReportsManagerActive);
            Assert.AreEqual(3, summary.ReportsWithManager);
        }

        [TestMethod]
        public void AnUnlicensedListThatNeverLoaded_LeavesManagersWithoutASeatUnknown()
        {
            // No Copilot audit import, or the query failed: an empty list is not evidence of anything.
            var analysis = Analysis(
                new[] { Seat(11, AdoptionBand.Established, manager: 101) },
                unlicensedAssessed: false);

            var summary = Finalise(analysis);

            Assert.AreEqual(1, summary.ManagersStatusUnknown);
            Assert.AreEqual(0, summary.ManagersStatusKnown);
            Assert.IsNull(summary.ManagersActivePct);
        }

        [TestMethod]
        public void ASeatHolderBeyondTheLicensedRowCap_IsUnknown()
        {
            var analysis = Analysis(new[]
            {
                Seat(11, AdoptionBand.Established, manager: 900),
                Seat(12, AdoptionBand.Trialling, manager: 1),
                Seat(1, AdoptionBand.Champion),
            });
            analysis.LicensedUsersCapped = true;
            analysis.LicensedUsersNotAnalysed = new[] { 900 };

            var summary = Finalise(analysis);

            Assert.AreEqual(1, summary.ManagersStatusUnknown, "Holds a seat but was never scored: the data cannot say.");
            Assert.AreEqual(1, summary.ReportsManagerUnknown);
            Assert.AreEqual(1, summary.ManagersActive);
        }

        [TestMethod]
        public void AnExternalGuestManagerWithoutASeat_IsUnknown_BecauseTheUnlicensedListLeavesGuestsOut()
        {
            var report = Seat(11, AdoptionBand.Established, manager: 101);
            report.ManagerUserPrincipalName = "guest_fabrikam.com#EXT#@contoso.onmicrosoft.com";
            var summary = Finalise(Analysis(new[] { report }));

            Assert.AreEqual(1, summary.ManagersStatusUnknown);
            Assert.AreEqual(0, summary.ManagersStatusKnown);
        }

        [TestMethod]
        public void SomeoneWithNoLicensedReports_IsNotAPeopleManager()
        {
            var analysis = Analysis(
                new[]
                {
                    Seat(1, AdoptionBand.Champion),                 // manages nobody
                    Seat(2, AdoptionBand.Established),              // manages only a disabled report
                    Disabled(Seat(21, AdoptionBand.NeverUsed, manager: 2)),
                    Seat(11, AdoptionBand.Trialling, manager: 3),   // manager 3 holds no seat, no Chat use
                },
                chat: new[] { ChatUser(4) });                      // uses Chat, manages nobody

            var summary = Finalise(analysis);

            Assert.AreEqual(1, summary.ManagersStatusKnown,
                "Only manager 3 directly manages an (enabled) licensed user.");
            Assert.AreEqual(0, summary.ManagersActive,
                "Active users who manage no licensed user are not people managers, however much they use Copilot.");
            Assert.AreEqual(1, summary.ReportsWithManager);
        }

        [TestMethod]
        public void ADisabledManager_IsLeftOutWithTheirReports_AsTheIssuePrototypeDoes()
        {
            var disabledWithSeat = Disabled(Seat(1, AdoptionBand.Champion));
            var underDisabledWithSeat = Seat(11, AdoptionBand.Established, manager: 1);
            var underDisabledWithoutSeat = Seat(12, AdoptionBand.Established, manager: 101);
            underDisabledWithoutSeat.ManagerAccountEnabled = false;
            var underActive = Seat(13, AdoptionBand.Trialling, manager: 2);

            var summary = Finalise(Analysis(
                new[] { disabledWithSeat, underDisabledWithSeat, underDisabledWithoutSeat, underActive, Seat(2, AdoptionBand.Developing) },
                chat: new[] { ChatUser(101) }));

            Assert.AreEqual(1, summary.ReportsWithManager,
                "Reports of a disabled manager are left out - with or without a seat, active or not.");
            Assert.AreEqual(1, summary.ManagersStatusKnown);
            Assert.AreEqual(1, summary.ManagersActive);
            Assert.AreEqual(0, summary.ManagersStatusUnknown, "Disabled is not 'unknown' either: it is out.");
        }

        [TestMethod]
        public void ADisabledReport_IsNotCounted()
        {
            var summary = Finalise(Analysis(new[]
            {
                Seat(1, AdoptionBand.Champion),
                Disabled(Seat(11, AdoptionBand.NeverUsed, manager: 1)),
                Seat(12, AdoptionBand.Established, manager: 1),
            }));

            Assert.AreEqual(1, summary.ReportsWithManager);
            Assert.AreEqual(100d, summary.ReportsActiveRatePctManagerActive);
        }

        [TestMethod]
        public void AUserWhoManagesThemselves_OrAManagementCycle_DoesNotLoopOrCrash()
        {
            var analysis = Analysis(new[]
            {
                Seat(1, AdoptionBand.Champion, manager: 1),     // their own manager
                Seat(2, AdoptionBand.Established, manager: 3),  // 2 and 3 manage each other
                Seat(3, AdoptionBand.NeverUsed, manager: 2),
            });

            var summary = Finalise(analysis);

            Assert.AreEqual(2, summary.ReportsWithManager, "Nobody is their own report.");
            Assert.AreEqual(2, summary.ManagersStatusKnown, "Each side of the cycle is the other's manager.");
            Assert.AreEqual(1, summary.ManagersActive);
            Assert.AreEqual(1, summary.ReportsManagerActive, "3 reports to 2, who uses Copilot.");
            Assert.AreEqual(0d, summary.ReportsActiveRatePctManagerActive);
            Assert.AreEqual(1, summary.ReportsManagerInactive, "2 reports to 3, who does not.");
            Assert.AreEqual(100d, summary.ReportsActiveRatePctManagerInactive);
        }

        [TestMethod]
        public void AnOrganisationWithNoManagersRecorded_PublishesBlanksNotZeroRates()
        {
            var summary = Finalise(Analysis(new[] { Seat(1, AdoptionBand.Champion), Seat(2, AdoptionBand.NeverUsed) }));

            Assert.AreEqual(0, summary.ReportsWithManager);
            Assert.AreEqual(0, summary.ManagersStatusKnown);
            Assert.IsNull(summary.ManagersActivePct);
            Assert.IsNull(summary.ReportsActiveRatePctManagerActive);
            Assert.IsNull(summary.ReportsManagerActive);
        }

        [TestMethod]
        public void TheProductVersionOfTheIssuePrototype_ReturnsSeventyFiveAgainstFifty()
        {
            // The issue's prototype returned 75.0% vs 50.0% on a hand-built organisation. The product
            // version limits reports to licensed users; managed this way it reproduces the same split.
            var analysis = Analysis(new[]
            {
                Seat(1, AdoptionBand.Established),
                Seat(2, AdoptionBand.NeverUsed),
                Seat(11, AdoptionBand.Trialling, manager: 1),
                Seat(12, AdoptionBand.Developing, manager: 1),
                Seat(13, AdoptionBand.Established, manager: 1),
                Seat(14, AdoptionBand.NeverUsed, manager: 1),
                Seat(21, AdoptionBand.Trialling, manager: 2),
                Seat(22, AdoptionBand.NeverUsed, manager: 2),
            });

            var summary = Finalise(analysis);

            Assert.AreEqual(75d, summary.ReportsActiveRatePctManagerActive);
            Assert.AreEqual(50d, summary.ReportsActiveRatePctManagerInactive);
        }

        #endregion

        #region Suppression

        [TestMethod]
        public void FewerKnownManagersThanTheMinimumGroup_PublishesNoFigureThatDependsOnTheirOwnUse()
        {
            // Four managers, default minimum of five: a rate here would describe four identifiable people.
            var seats = new List<LicensedUserAdoptionRow>();
            for (var m = 1; m <= 4; m++)
            {
                seats.Add(Seat(m, m % 2 == 0 ? AdoptionBand.Established : AdoptionBand.NeverUsed));
                for (var r = 0; r < 6; r++) seats.Add(Seat(m * 100 + r, AdoptionBand.Trialling, manager: m));
            }

            var summary = Finalise(Analysis(seats), minSeats: 5);

            Assert.AreEqual(4, summary.ManagersStatusKnown, "The count of managers says nothing about anyone's use.");
            Assert.AreEqual(24, summary.ReportsWithManager);
            Assert.IsNull(summary.ManagersActive);
            Assert.IsNull(summary.ManagersActivePct);
            Assert.IsNull(summary.ReportsManagerActive, "Even the split counts would reveal how many of four managers use Copilot.");
            Assert.IsNull(summary.ReportsManagerInactive);
            Assert.IsNull(summary.ReportsActiveRatePctManagerActive);
            Assert.IsNull(summary.ReportsHabitRatePctManagerInactiveLicensed);
        }

        [TestMethod]
        public void ASideWithTooFewManagersOrReports_WithholdsTheWholeComparison()
        {
            // Six managers with a seat: five use Copilot with three reports each, one does not, with two. The
            // inactive side is one manager and two reports - and since the sides add up to the reports with a
            // known manager, which is published, the active side cannot be shown without giving it away either.
            var seats = new List<LicensedUserAdoptionRow>();
            for (var m = 1; m <= 6; m++)
            {
                seats.Add(Seat(m, m <= 5 ? AdoptionBand.Champion : AdoptionBand.NeverUsed));
                var reports = m <= 5 ? 3 : 2;
                for (var r = 0; r < reports; r++) seats.Add(Seat(m * 100 + r, AdoptionBand.Established, manager: m));
            }

            var summary = Finalise(Analysis(seats), minSeats: 5);

            Assert.AreEqual(83.3, summary.ManagersActivePct, "A share of six known managers is still published.");
            Assert.AreEqual(17, summary.ReportsWithManager);
            Assert.IsNull(summary.ReportsManagerInactive, "One inactive manager's team size would point to them...");
            Assert.IsNull(summary.ReportsActiveRatePctManagerInactive, "...and a rate over two reports is not shown either...");
            Assert.IsNull(summary.ReportsManagerActive, "...nor the other side, which is the published total minus this one.");
            Assert.IsNull(summary.ReportsActiveRatePctManagerActive);
            Assert.IsNull(summary.ReportsHabitRatePctManagerInactive);
            Assert.IsNull(summary.ReportsManagerActiveLicensed);
        }

        [TestMethod]
        public void AManagerAloneOnOneSideOfASplit_IsNotGivenAwayByTheirTeamsFigures()
        {
            // Five managers whose use is known, each with five licensed reports. Four hold a seat and use Copilot;
            // the fifth holds no seat. Anyone who knows that much about the department could read the fifth
            // manager's own Copilot use off whichever side their team landed on - so neither split is published.
            foreach (var fifthUsesChat in new[] { true, false })
            {
                var seats = new List<LicensedUserAdoptionRow>();
                for (var m = 1; m <= 4; m++) seats.Add(Seat(m, AdoptionBand.Champion));
                for (var m = 1; m <= 5; m++)
                {
                    for (var r = 0; r < 5; r++) seats.Add(Seat(m * 100 + r, AdoptionBand.Established, manager: m));
                }

                var summary = Finalise(
                    Analysis(seats, chat: fifthUsesChat ? new[] { ChatUser(5) } : null),
                    minSeats: 5);
                var label = fifthUsesChat ? "fifth manager uses Copilot Chat" : "fifth manager does not use Copilot";

                Assert.AreEqual(5, summary.ManagersStatusKnown, label);
                Assert.AreEqual(fifthUsesChat ? 100d : 80d, summary.ManagersActivePct,
                    label + ": a share of five known managers is published, as before.");
                Assert.IsNull(summary.ReportsManagerActiveUnlicensed, label);
                Assert.IsNull(summary.ReportsManagerInactiveUnlicensed, label);
                Assert.IsNull(summary.ReportsManagerActiveLicensed, label + ": the sibling cell would give it back by subtraction.");
                Assert.IsNull(summary.ReportsActiveRatePctManagerActiveUnlicensed, label);
                Assert.IsNull(summary.ReportsActiveRatePctManagerInactiveUnlicensed, label);
                Assert.IsNull(summary.ReportsActiveRatePctManagerActiveLicensed, label);

                if (fifthUsesChat)
                {
                    // Everyone on one side, nobody on the other: the use split says nothing about any one manager.
                    Assert.AreEqual(25, summary.ReportsManagerActive, label);
                    Assert.AreEqual(0, summary.ReportsManagerInactive, label);
                    Assert.AreEqual(100d, summary.ReportsActiveRatePctManagerActive, label);
                }
                else
                {
                    Assert.IsNull(summary.ReportsManagerActive, label + ": one inactive manager is one identifiable person.");
                    Assert.IsNull(summary.ReportsManagerInactive, label);
                    Assert.IsNull(summary.ReportsActiveRatePctManagerActive, label);
                    Assert.IsNull(summary.ReportsActiveRatePctManagerInactive, label);
                }

                Assert.IsNull(summary.WithoutIndividualData().ReportsManagerActiveUnlicensed,
                    label + ": the copy served without See PII, and the workbook built from it, carry the same blanks.");
            }
        }

        [TestMethod]
        public void TheDepartmentSplit_FollowsTheAdoptionTable_AndIsSuppressedBelowMinSeatsPerSegment()
        {
            var seats = new List<LicensedUserAdoptionRow>();

            // A Greek-named department with ten managers, each with a seat and five reports in the same
            // department, five of them using Copilot and five not: big enough for every figure.
            for (var m = 1; m <= 10; m++)
            {
                seats.Add(Seat(m, m <= 5 ? AdoptionBand.Established : AdoptionBand.NeverUsed, department: GreekDepartment));
                for (var r = 0; r < 5; r++)
                {
                    seats.Add(Seat(m * 100 + r, r < (m <= 5 ? 4 : 1) ? AdoptionBand.Trialling : AdoptionBand.NeverUsed, manager: m, department: GreekDepartment));
                }
            }

            // Six seats but only two managers: listed, with its manager figures withheld.
            seats.Add(Seat(11, AdoptionBand.Champion, department: "Contoso Legal"));
            seats.Add(Seat(12, AdoptionBand.NeverUsed, department: "Contoso Legal"));
            for (var r = 0; r < 2; r++) seats.Add(Seat(1100 + r, AdoptionBand.Trialling, manager: 11, department: "Contoso Legal"));
            for (var r = 0; r < 2; r++) seats.Add(Seat(1200 + r, AdoptionBand.Trialling, manager: 12, department: "Contoso Legal"));

            // Four seats: below the minimum, so the department is not listed at all.
            seats.Add(Seat(13, AdoptionBand.Champion, department: "Contoso Tiny"));
            for (var r = 0; r < 3; r++) seats.Add(Seat(1300 + r, AdoptionBand.Trialling, manager: 13, department: "Contoso Tiny"));

            var summary = Finalise(Analysis(seats), minSeats: 5);

            CollectionAssert.AreEqual(
                summary.AdoptionByDepartment.Select(d => d.Segment).ToList(),
                summary.ManagerModellingByDepartment.Select(d => d.Segment).ToList(),
                "Shown as columns beside the adoption table, so it lists exactly its departments, in its order.");
            Assert.IsFalse(summary.ManagerModellingByDepartment.Any(d => d.Segment == "Contoso Tiny"));

            var greek = summary.ManagerModellingByDepartment.Single(d => d.Segment == GreekDepartment);
            Assert.AreEqual(60, greek.LicensedUsers);
            Assert.AreEqual(10, greek.ManagersStatusKnown);
            Assert.AreEqual(50d, greek.ManagersActivePct);
            Assert.AreEqual(25, greek.ReportsManagerActive);
            Assert.AreEqual(80d, greek.ReportsActiveRatePctManagerActive);
            Assert.AreEqual(20d, greek.ReportsActiveRatePctManagerInactive);
            Assert.AreEqual(80d, greek.ReportsActiveRatePctManagerActiveLicensed);

            var legal = summary.ManagerModellingByDepartment.Single(d => d.Segment == "Contoso Legal");
            Assert.AreEqual(6, legal.LicensedUsers, "The department clears the seat minimum...");
            Assert.AreEqual(2, legal.ManagersStatusKnown);
            Assert.IsNull(legal.ManagersActivePct, "...but two managers are too few to describe.");
            Assert.IsNull(legal.ReportsManagerActive);
            Assert.IsNull(legal.ReportsActiveRatePctManagerInactive);

            // The tenant-wide figure still covers every department, including the one not listed.
            Assert.AreEqual(13, summary.ManagersStatusKnown);
            Assert.AreEqual(57, summary.ReportsWithManager);
        }

        #endregion

        #region Narrowed views and privacy

        [TestMethod]
        public void ANarrowedView_ResolvesManagersAgainstTheWholeTenant()
        {
            // Reports on fabrikam.com; their managers on contoso.com, outside the slice. One uses Copilot
            // Chat without a seat, one holds a seat and is active, one is known not to use Copilot.
            var seats = new List<LicensedUserAdoptionRow>
            {
                Seat(1, AdoptionBand.Champion),
                Seat(2, AdoptionBand.NeverUsed),
            };
            for (var r = 0; r < 3; r++)
            {
                seats.Add(Seat(100 + r, AdoptionBand.Trialling, manager: 901, domain: "fabrikam.com"));
                seats.Add(Seat(200 + r, AdoptionBand.Trialling, manager: 1, domain: "fabrikam.com"));
                seats.Add(Seat(300 + r, AdoptionBand.NeverUsed, manager: 2, domain: "fabrikam.com"));
            }

            var analysis = Analysis(seats, chat: new[] { ChatUser(901) });
            var service = Service();
            analysis.Summary.Options = service.Options;
            service.FinaliseSummary(analysis);

            var scoped = CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.ForEmailDomain("fabrikam.com"), service.FinaliseSummary);

            Assert.AreEqual(9, scoped.Summary.ReportsWithManager);
            Assert.AreEqual(3, scoped.Summary.ManagersStatusKnown,
                "Managers outside the slice are still people whose own use is known.");
            Assert.AreEqual(0, scoped.Summary.ManagersStatusUnknown);
            Assert.AreEqual(2, scoped.Summary.ManagersActive);
            Assert.AreEqual(3, scoped.Summary.ReportsManagerActiveUnlicensed,
                "The Copilot Chat manager is on contoso.com, so the slice holds no unlicensed row for them.");
            Assert.AreEqual(3, scoped.Summary.ReportsManagerInactiveLicensed);

            Assert.IsTrue(scoped.ManagerDirectory.Inherited);
            Assert.IsFalse(analysis.ManagerDirectory.Inherited, "The cached tenant analysis keeps its own directory.");
            Assert.AreEqual(3, analysis.Summary.ManagersStatusKnown, "Narrowing must never edit the cached tenant figures.");
        }

        [TestMethod]
        public void TheFiguresAreAggregates_KeptForEveryReader_WhileTheManagerRollupStillNeedsSeePii()
        {
            var seats = new List<LicensedUserAdoptionRow>();
            for (var m = 1; m <= 5; m++)
            {
                seats.Add(Seat(m, AdoptionBand.Champion));
                for (var r = 0; r < 5; r++) seats.Add(Seat(m * 100 + r, AdoptionBand.Trialling, manager: m));
            }

            var analysis = Analysis(seats);
            var summary = Finalise(analysis, minSeats: 5);
            Assert.AreEqual(CopilotAdoptionAccountabilityDimensions.DirectManager, summary.AccountabilityDimension);
            Assert.IsTrue(summary.AccountabilityRollup.Count > 0, "The control: the roll-up names managers.");

            var trimmed = summary.WithoutIndividualData();

            Assert.AreEqual(0, trimmed.AccountabilityRollup.Count,
                "Grouped by direct manager, the roll-up still needs See PII (#661).");
            Assert.AreEqual(100d, trimmed.ManagersActivePct, "Aggregate manager figures are open to every signed-in reader.");
            Assert.AreEqual(summary.ManagerModellingByDepartment.Count, trimmed.ManagerModellingByDepartment.Count);
            Assert.IsTrue(summary.AccountabilityRollup.Count > 0, "The cached summary keeps its roll-up for readers who may see it.");
        }

        [TestMethod]
        public void WithoutSeePii_TheWholePopulationLessTheDepartmentsShown_NeverGivesBackAWithheldDepartment_AndWithSeePiiNothingChanges()
        {
            // Each department's manager figures are published in three steps - the share of managers using Copilot, the
            // comparison of their teams, and that comparison by seat - each withheld when it would describe too few
            // managers. The same figures for everyone head the table. Four departments, every manager with a seat and
            // three reports in the department:
            //   Sales    10 managers, 5 using Copilot
            //   Support   6 managers, 3 using it  - its comparison is withheld for every reader: 3 on each side
            //   Legal     2 managers, 1 using it  - everything withheld for every reader
            //   Finance  12 managers, 6 using it
            // Everyone: 30 managers, 15 using Copilot. Without more withheld, 15 - 5 - 3 - 6 = 1 is Legal's one manager.
            var seats = new List<LicensedUserAdoptionRow>();
            var next = 1;
            foreach (var (department, managers) in new[] { ("Contoso Sales", 10), ("Contoso Support", 6), ("Contoso Legal", 2), ("Contoso Finance", 12) })
            {
                for (var m = 0; m < managers; m++)
                {
                    var manager = next++;
                    seats.Add(Seat(manager, m < managers / 2 ? AdoptionBand.Established : AdoptionBand.NeverUsed, department: department));
                    for (var r = 0; r < 3; r++) seats.Add(Seat(manager * 100 + r, r == 0 ? AdoptionBand.Trialling : AdoptionBand.NeverUsed, manager: manager, department: department));
                }
            }

            var summary = Finalise(Analysis(seats), minSeats: 5);
            ManagerModellingSegmentRow Row(CopilotAdoptionSummary s, string department) => s.ManagerModellingByDepartment.Single(d => d.Segment == department);

            Assert.AreEqual(30, summary.ManagersStatusKnown);
            Assert.AreEqual(15, summary.ManagersActive);
            Assert.AreEqual(50d, Row(summary, "Contoso Support").ManagersActivePct, "The control: with See PII, Support's share is shown.");
            Assert.IsNotNull(Row(summary, "Contoso Sales").ReportsManagerActive, "The control: with See PII, Sales' comparison is shown.");
            Assert.IsNull(Row(summary, "Contoso Legal").ManagersActive);

            var trimmed = summary.WithoutIndividualData();

            // Step 1: Legal's two managers are withheld; the smallest department shown with five or more - Support, six -
            // goes too, so the difference holds eight managers.
            var support = Row(trimmed, "Contoso Support");
            Assert.IsNull(support.ManagersActive, "Withheld, never 0.");
            Assert.IsNull(support.ManagersActivePct);
            Assert.AreEqual(SmallGroupWithheldReasons.RevealsSmallerGroup, support.ManagersWithheld);
            Assert.AreEqual(6, support.ManagersStatusKnown, "Who is behind a row is published for every reader, as before.");

            // Step 2: the comparison is shown for Sales and Finance only, and Support's and Legal's - one to three managers
            // on each side - would come back as everyone's less theirs. Sales has the fewest on a side (five): withheld.
            var sales = Row(trimmed, "Contoso Sales");
            Assert.AreEqual(50d, sales.ManagersActivePct, "Sales' share stays: everyone's less Sales' and Finance's covers eight managers.");
            Assert.IsNull(sales.ReportsManagerActive);
            Assert.IsNull(sales.ReportsActiveRatePctManagerInactive);
            Assert.IsNull(sales.ReportsManagerActiveLicensed, "A step withheld takes the steps after it.");
            Assert.AreEqual(SmallGroupWithheldReasons.RevealsSmallerGroup, sales.ComparisonWithheld);
            Assert.IsNull(sales.ManagersWithheld);

            var finance = Row(trimmed, "Contoso Finance");
            Assert.AreEqual(50d, finance.ManagersActivePct);
            Assert.AreEqual(18, finance.ReportsManagerActive, "Finance is never the smallest: it stays whole.");
            Assert.AreEqual(18, finance.ReportsManagerActiveLicensed);
            Assert.IsNull(finance.ManagersWithheld);
            Assert.IsNull(finance.ComparisonWithheld);
            Assert.IsNull(finance.SeatSplitWithheld);

            var legal = Row(trimmed, "Contoso Legal");
            Assert.IsNull(legal.ManagersActive);
            Assert.IsNull(legal.ManagersWithheld, "Legal is withheld for its own size, for every reader, as before.");

            // The headline stays, and whatever it less the departments shown gives covers five managers or more.
            Assert.AreEqual(15, trimmed.ManagersActive);
            Assert.AreEqual(summary.ReportsManagerActive, trimmed.ReportsManagerActive);
            var shownStep1 = trimmed.ManagerModellingByDepartment.Where(d => d.ManagersActive.HasValue).Sum(d => d.ManagersStatusKnown);
            Assert.IsTrue(trimmed.ManagersStatusKnown - shownStep1 >= 5);
            var shownStep2 = trimmed.ManagerModellingByDepartment.Where(d => d.ReportsManagerActive.HasValue).Sum(d => d.ManagersActive.Value);
            Assert.IsTrue(trimmed.ManagersActive - shownStep2 >= 5, "The comparison's difference covers at least five managers using Copilot.");

            Assert.IsNotNull(Row(summary, "Contoso Support").ManagersActive, "The cached summary must never be edited for one reader.");
            Assert.IsNotNull(Row(summary, "Contoso Sales").ReportsManagerActive);
            Assert.IsNull(Row(summary, "Contoso Sales").ComparisonWithheld);
        }

        [TestMethod]
        public void WithoutSeePii_TheManagerOfADepartmentTooSmallToList_IsNotGivenBackByTheWholeLessTheDepartmentsShown()
        {
            // Nothing in the table is withheld, but a department of four seats is not listed at all. Its one manager is in
            // everyone's figures, so everyone's less the two departments shown would be that one person's own use.
            var seats = new List<LicensedUserAdoptionRow>();
            var next = 1;
            foreach (var (department, managers) in new[] { ("Contoso Sales", 10), ("Contoso Finance", 12), ("Contoso Tiny", 1) })
            {
                for (var m = 0; m < managers; m++)
                {
                    var manager = next++;
                    seats.Add(Seat(manager, m % 2 == 0 ? AdoptionBand.Established : AdoptionBand.NeverUsed, department: department));
                    for (var r = 0; r < 3; r++) seats.Add(Seat(manager * 100 + r, AdoptionBand.Trialling, manager: manager, department: department));
                }
            }

            var summary = Finalise(Analysis(seats), minSeats: 5);
            Assert.IsFalse(summary.ManagerModellingByDepartment.Any(d => d.Segment == "Contoso Tiny"), "The control: too few seats to list.");
            Assert.AreEqual(23, summary.ManagersStatusKnown);
            Assert.IsTrue(summary.ManagerModellingByDepartment.All(d => d.ManagersActive.HasValue), "The control: nothing listed is withheld.");

            var trimmed = summary.WithoutIndividualData();

            // The department breakdown leaves Tiny out, so for a reader without See PII it leaves out its smallest listed
            // department as well - Sales - from every view of it, this table included (#844). Everyone's figures less the
            // department shown then hold eleven managers.
            CollectionAssert.AreEqual(new[] { "Contoso Finance" }, trimmed.ManagerModellingByDepartment.Select(d => d.Segment).ToList(),
                "With nothing more withheld, everyone's figures less the two departments shown would be the unlisted manager's own use.");
            Assert.IsTrue(trimmed.ManagerModellingByDepartment.All(d => d.ManagersActive.HasValue), "Finance stays whole.");
            Assert.AreEqual(11, trimmed.ManagersStatusKnown - trimmed.ManagerModellingByDepartment.Sum(d => d.ManagersStatusKnown));
            Assert.AreEqual(summary.ManagersActive, trimmed.ManagersActive, "The headline stays.");
            Assert.AreEqual(2, summary.ManagerModellingByDepartment.Count, "The cached summary must never be edited for one reader.");
        }

        [TestMethod]
        public void TheRollupByManager_CarriesNoManagersOwnStatus()
        {
            // The issue puts a manager's own status on a roll-up row out of scope: one row per manager
            // would disclose that person's use. Pinned on the type AND on the serialised payload.
            var figureNames = JsonNames(typeof(ManagerModellingFigures));
            var rollupNames = JsonNames(typeof(AccountabilityRollupRow));

            CollectionAssert.AreEqual(new List<string>(), figureNames.Intersect(rollupNames).ToList());
            Assert.IsFalse(typeof(AccountabilityRollupRow).IsSubclassOf(typeof(ManagerModellingFigures)));

            var seats = new List<LicensedUserAdoptionRow>();
            for (var m = 1; m <= 5; m++)
            {
                seats.Add(Seat(m, AdoptionBand.Champion));
                for (var r = 0; r < 5; r++) seats.Add(Seat(m * 100 + r, AdoptionBand.Trialling, manager: m));
            }

            var summary = Finalise(Analysis(seats), minSeats: 5);
            var json = JsonConvert.SerializeObject(summary.AccountabilityRollup);

            foreach (var name in figureNames)
            {
                Assert.IsFalse(json.Contains("\"" + name + "\""), $"A roll-up row must not carry '{name}'.");
            }
        }

        [TestMethod]
        public void APersonsRow_NeverSerialisesTheirManagersId()
        {
            var row = Seat(11, AdoptionBand.Champion, manager: 1);
            var json = JsonConvert.SerializeObject(row);

            Assert.IsFalse(json.Contains("ManagerUserId") || json.Contains("managerUserId"));
            Assert.IsTrue(json.IndexOf("managerAccountEnabled", StringComparison.OrdinalIgnoreCase) < 0);
        }

        #endregion

        #region Model plumbing

        [TestMethod]
        public void EveryFigure_ReachesTheSummaryAtTopLevel_UnderTheSameName()
        {
            // Snapshot facts reflects over the summary's own scalar properties, so a figure that existed
            // only on the department row would never reach it.
            var summaryProperties = typeof(CopilotAdoptionSummary).GetProperties()
                .ToDictionary(p => JsonName(p), p => p);

            foreach (var property in typeof(ManagerModellingFigures).GetProperties())
            {
                var name = JsonName(property);
                Assert.IsTrue(summaryProperties.ContainsKey(name), $"The summary has no top-level '{name}'.");
                Assert.AreEqual(property.PropertyType, summaryProperties[name].PropertyType, name);
                Assert.IsTrue(Regex.IsMatch(name, "^[a-z][A-Za-z]*$"), $"'{name}' must be an explicit camelCase JSON name.");
            }
        }

        [TestMethod]
        public void CopyToAndCopyFrom_CarryEveryFigure()
        {
            var source = new ManagerModellingFigures();
            var value = 1;
            foreach (var property in typeof(ManagerModellingFigures).GetProperties())
            {
                var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                property.SetValue(source, type == typeof(double) ? (object)(value + 0.5) : value);
                value++;
            }

            var summary = new CopilotAdoptionSummary();
            source.CopyTo(summary);
            var back = new ManagerModellingFigures();
            back.CopyFrom(summary);

            foreach (var property in typeof(ManagerModellingFigures).GetProperties())
            {
                Assert.AreEqual(property.GetValue(source), property.GetValue(back), property.Name);
            }
        }

        [TestMethod]
        public void TheLicensedUserQuery_CarriesTheManagerById_WithoutANewCopilotScan()
        {
            var sql = CopilotAdoptionSql.LicensedUsersSql(new[] { 1 }, new int[0], includeCopilotReport: true);

            StringAssert.Contains(sql, "u.manager_id AS ManagerUserId");
            StringAssert.Contains(sql, "manager.account_enabled AS ManagerAccountEnabled");
            Assert.AreEqual(1, Regex.Matches(sql, @"LEFT JOIN dbo\.users AS manager ON manager\.id = u\.manager_id").Count,
                "The manager's account state comes from the manager join the query already made.");
            Assert.AreEqual(1, Regex.Matches(sql, @"dbo\.copilot_chats").Count,
                "Still exactly one read of copilot_chats.");
        }

        [TestMethod]
        public void TheUnlicensedRows_AlreadyCarryTheUserIdAManagerIsMatchedOn()
        {
            StringAssert.Contains(CopilotAdoptionSql.UnlicensedUsageRowsSql(new[] { 1 }), "t.user_id AS UserId");
        }

        [TestMethod]
        public void ScoringCarriesTheManagerThrough()
        {
            var scored = CopilotAdoptionScoring.Score(
                new LicensedUserUsageRow { UserId = 11, UserPrincipalName = "user11@contoso.com", ManagerUserId = 1, ManagerAccountEnabled = false },
                Now.AddDays(-28),
                Now,
                auditAvailable: true);

            Assert.AreEqual(1, scored.ManagerUserId);
            Assert.AreEqual(false, scored.ManagerAccountEnabled);
        }

        #endregion

        #region Fixture

        private static CopilotAdoptionService Service(int minSeats = 1)
        {
            return new CopilotAdoptionService(new CopilotAdoptionOptions { MinSeatsPerSegment = minSeats });
        }

        private static CopilotAdoptionSummary Finalise(CopilotAdoptionAnalysis analysis, int minSeats = 1)
        {
            var service = Service(minSeats);
            analysis.Summary.Options = service.Options;
            service.FinaliseSummary(analysis);
            return analysis.Summary;
        }

        private static CopilotAdoptionAnalysis Analysis(
            IEnumerable<LicensedUserAdoptionRow> seats,
            IEnumerable<UnlicensedUsageQueryRow> chat = null,
            bool unlicensedAssessed = true)
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.LicensedUsers.AddRange(seats);
            if (chat != null) analysis.UnlicensedUsers.AddRange(chat);
            analysis.UnlicensedUsageAssessed = unlicensedAssessed;
            analysis.Summary.LicensedUsers = analysis.LicensedUsers.Count;
            return analysis;
        }

        /// <summary>A scored licensed user in a given band, optionally reporting to <paramref name="manager"/>.</summary>
        private static LicensedUserAdoptionRow Seat(
            int id,
            AdoptionBand band,
            int? manager = null,
            string department = Finance,
            string domain = "contoso.com")
        {
            var active = band > AdoptionBand.Dormant;
            var row = new LicensedUserAdoptionRow
            {
                UserId = id,
                UserPrincipalName = "user" + id + "@" + domain,
                EmailDomain = domain,
                Department = department,
                AccountEnabled = true,
                ManagerUserId = manager,
                ManagerAccountEnabled = manager.HasValue ? (bool?)true : null,
                ManagerUserPrincipalName = manager.HasValue ? "user" + manager + "@contoso.com" : null,
                AccountCreatedUtc = Now.AddDays(-120),
                TenureStartUtc = Now.AddDays(-120),
                TenureBasis = CopilotAdoptionScoring.TenureBasisAccountAge,
                DaysSinceTenureStart = 120,
                Interactions = active ? 12 : 0,
                AuditInteractions = active ? 12 : 0,
                ActiveDays = active ? 4 : 0,
                AppsUsed = active ? 2 : 0,
                AdoptionScore = band == AdoptionBand.Champion ? 85 : band == AdoptionBand.Established ? 60 : band == AdoptionBand.Developing ? 30 : active ? 10 : 0,
                Band = band,
                BandName = CopilotAdoptionScoring.BandDisplayName(band),
            };
            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            row.RecommendedActionCode = CopilotAdoptionScoring.RecommendedActionCode(row);
            row.RecommendedActionLabel = CopilotAdoptionScoring.ActionLabel(row.RecommendedActionCode);
            return row;
        }

        private static LicensedUserAdoptionRow Disabled(LicensedUserAdoptionRow row)
        {
            row.AccountEnabled = false;
            CopilotAdoptionScoring.ApplyReclaimEligibility(row);
            return row;
        }

        /// <summary>Someone with no Copilot seat who used Copilot Chat in the period.</summary>
        private static UnlicensedUsageQueryRow ChatUser(int id, string domain = "contoso.com")
        {
            return new UnlicensedUsageQueryRow
            {
                UserId = id,
                UserPrincipalName = "user" + id + "@" + domain,
                EmailDomain = domain,
                Department = Finance,
                Interactions = 6,
                ActiveDays = 3,
                AppsUsed = 1,
                LastInteractionUtc = Now.AddDays(-2),
            };
        }

        private static string JsonName(PropertyInfo property)
        {
            return property.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName ?? property.Name;
        }

        private static List<string> JsonNames(Type type)
        {
            return type.GetProperties()
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() == null)
                .Select(JsonName)
                .ToList();
        }

        #endregion
    }
}
