using Common.Entities.CopilotAdoption;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// The Agents tab's list of the heaviest agent users: who it names, how a filtered view narrows it,
    /// and that a reader without the See PII permission never receives it.
    /// </summary>
    [TestClass]
    public class CopilotAdoptionAgentUsersTests
    {
        private static AgentUserRow Person(int id, string domain, long interactions, bool seat = true)
        {
            return new AgentUserRow
            {
                UserId = id,
                UserPrincipalName = $"person{id}@{domain}",
                Mail = $"person{id}@{domain}",
                EmailDomain = domain,
                Interactions = interactions,
                AgentsUsed = 1,
                ActiveDays = 1,
                TopAgentName = "Expenses helper",
                TopAgentInteractions = interactions,
                HoldsCopilotSeat = seat,
            };
        }

        private static CopilotAdoptionAnalysis Analysis(params AgentUserRow[] people)
        {
            var analysis = new CopilotAdoptionAnalysis();
            analysis.AgentUsers.AddRange(people);
            return analysis;
        }

        [TestMethod]
        public void Summary_ListsTheHeaviestAgentUsers_HeaviestFirst_UpToTheOption()
        {
            var analysis = Analysis(
                Person(1, "contoso.com", 10),
                Person(2, "contoso.com", 50, seat: false),
                Person(3, "contoso.com", 30),
                Person(4, "contoso.com", 30));

            new CopilotAdoptionService(new CopilotAdoptionOptions { TopAgentUsers = 3 }).FinaliseSummary(analysis);

            CollectionAssert.AreEqual(
                new[] { 2, 3, 4 },
                analysis.Summary.TopAgentUsers.Select(u => u.UserId).ToArray(),
                "Heaviest first, ties by user id, and no more than the option allows.");
            Assert.IsFalse(analysis.Summary.TopAgentUsers[0].HoldsCopilotSeat);
            Assert.IsFalse(analysis.Summary.TopAgentUsersCapped);
        }

        [TestMethod]
        public void Summary_SaysTheListMayBeShort_WhenThePeopleItWasPickedFromWereCapped()
        {
            var analysis = Analysis(Person(1, "contoso.com", 10));
            analysis.AgentUsersCapped = true;

            new CopilotAdoptionService().FinaliseSummary(analysis);

            Assert.IsTrue(analysis.Summary.TopAgentUsersCapped);
        }

        [TestMethod]
        public void FilteredView_ListsItsOwnHeaviestAgentUsers_NotTheTenants()
        {
            var service = new CopilotAdoptionService();
            var analysis = Analysis(
                Person(1, "fabrikam.com", 500),
                Person(2, "contoso.com", 40),
                Person(3, "contoso.com", 20));
            service.FinaliseSummary(analysis);

            var scoped = CopilotAdoptionScopeFilter.Apply(analysis, CopilotAdoptionScope.ForEmailDomain("contoso.com"), service.FinaliseSummary);

            CollectionAssert.AreEqual(new[] { 2, 3 }, scoped.Summary.TopAgentUsers.Select(u => u.UserId).ToArray(),
                "A filtered view names its own people only - never the tenant's heaviest user from another organisation.");
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, analysis.Summary.TopAgentUsers.Select(u => u.UserId).ToArray(),
                "The cached tenant-wide summary is not modified.");
        }

        [TestMethod]
        public void WithoutIndividualData_RemovesTheHeaviestAgentUsers_OnACopy()
        {
            var summary = new CopilotAdoptionSummary
            {
                AccountabilityDimension = CopilotAdoptionAccountabilityDimensions.Department,
                TopAgentUsers = new List<AgentUserRow> { Person(1, "contoso.com", 10) },
            };
            summary.AccountabilityRollup.Add(new AccountabilityRollupRow { Segment = "Finance", LicensedUsers = 9 });

            var copy = summary.WithoutIndividualData();

            Assert.AreNotSame(summary, copy);
            Assert.AreEqual(0, copy.TopAgentUsers.Count, "Named people never reach a reader without See PII.");
            Assert.AreEqual(1, summary.TopAgentUsers.Count, "The cached summary must not be mutated.");
            Assert.AreEqual(1, copy.AccountabilityRollup.Count, "A department roll-up is an aggregate and stays.");
        }

        [TestMethod]
        public void PastRange_HidesTheHeaviestAgentUsers_EvenForSeePiiReaders()
        {
            var summary = new CopilotAdoptionSummary
            {
                Options = new CopilotAdoptionOptions { UsesExplicitDates = true },
                TopAgentUsers = new List<AgentUserRow> { Person(1, "contoso.com", 10) },
            };

            Assert.AreEqual(0, summary.WithoutPastRangeNamedLists().TopAgentUsers.Count);
        }
    }
}
