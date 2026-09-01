using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;
using WebJob.AppInsightsImporter.Engine.Sql.Rules;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the click staging rules extracted from PageClicksSaveExtension (issue #369).
    /// Runs with zero SQL Server, Graph, Redis or Service Bus dependency.
    /// </summary>
    [TestClass]
    public class AppInsightsClickRulesTests
    {
        private static ClickEventAppInsightsQueryResult Click(Guid? pageRequestId, string linkText, DateTime? when = null)
        {
            return new ClickEventAppInsightsQueryResult
            {
                CustomProperties = new ClickCustomProps
                {
                    PageRequestId = pageRequestId,
                    LinkText = linkText,
                    HRef = "https://contoso.sharepoint.com/sites/example/καλημέρα.aspx",
                    EventTimestamp = when ?? new DateTime(2026, 1, 5, 9, 30, 0, DateTimeKind.Utc)
                }
            };
        }

        [TestMethod]
        public void Clicks_ValidEvent_IsStaged()
        {
            var plan = ClickEventRules.Plan(new[] { Click(Guid.NewGuid(), "Quarterly report") });

            Assert.AreEqual(1, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.InvalidClicks);
        }

        [TestMethod]
        public void Clicks_EventWithoutRequiredProperties_IsNotStaged()
        {
            var events = new[]
            {
                Click(Guid.Empty, "Empty id"),
                Click(Guid.NewGuid(), null),
                Click(Guid.NewGuid(), string.Empty),
                Click(Guid.NewGuid(), "No timestamp", DateTime.MinValue),
            };

            var plan = ClickEventRules.Plan(events);

            Assert.AreEqual(0, plan.RowsToStage.Count, "None of these carry enough data to stage.");
            Assert.AreEqual(events.Length, plan.InvalidClicks, "Every rejection must be counted, not just logged.");
        }

        [TestMethod]
        public void Clicks_NullPageRequestId_IsRejectedInsteadOfThrowing()
        {
            // Regression guard. ClickEventAppInsightsQueryResult.IsValid tests
            //     CustomProperties?.PageRequestId != Guid.Empty
            // where PageRequestId is a Guid?. When it is null the lifted comparison is
            // (null != Guid.Empty) == true, so the event passed IsValid and reached the
            // ClickTempEntity constructor, which requires HasValue and throws ArgumentNullException.
            // That exception was caught by SaveSectionSafe, discarding EVERY click in the cycle
            // rather than just this one row.
            var offending = Click(null, "Has link text but no page-request id");

            Assert.IsTrue(offending.IsValid, "Precondition: the original IsValid still lets this through.");
            Assert.IsFalse(ClickEventRules.CanStage(offending), "The rule must reject it before it can throw.");

            var plan = ClickEventRules.Plan(new BaseCustomEventAppInsightsQueryResult[]
            {
                offending,
                Click(Guid.NewGuid(), "Good click")
            });

            Assert.AreEqual(1, plan.RowsToStage.Count, "The good click must still be staged.");
            Assert.AreEqual(1, plan.InvalidClicks);
        }

        [TestMethod]
        public void Clicks_NullCustomProperties_IsRejected()
        {
            var click = new ClickEventAppInsightsQueryResult { CustomProperties = null };

            Assert.IsFalse(ClickEventRules.CanStage(click));
        }

        [TestMethod]
        public void Clicks_NonClickEvents_AreIgnoredNotCountedAsInvalid()
        {
            var events = new BaseCustomEventAppInsightsQueryResult[]
            {
                new SearchEventAppInsightsQueryResult { CustomProperties = new SearchCustomProps { SearchText = "report", SessionId = "s1" } },
                Click(Guid.NewGuid(), "Real click")
            };

            var plan = ClickEventRules.Plan(events);

            Assert.AreEqual(1, plan.RowsToStage.Count);
            Assert.AreEqual(0, plan.InvalidClicks, "A search event is another section's business, not an invalid click.");
        }

        [TestMethod]
        public void Clicks_EmptyOrNullCollection_StagesNothing()
        {
            Assert.AreEqual(0, ClickEventRules.Plan(Enumerable.Empty<BaseCustomEventAppInsightsQueryResult>()).RowsToStage.Count);
            Assert.AreEqual(0, ClickEventRules.Plan(null).RowsToStage.Count);
        }

        [TestMethod]
        public void Clicks_UnicodeLinkTarget_SurvivesProjection()
        {
            var plan = ClickEventRules.Plan(new[] { Click(Guid.NewGuid(), "Καλημέρα") });

            Assert.AreEqual(1, plan.RowsToStage.Count);
            StringAssert.Contains(plan.RowsToStage[0].Url, "καλημέρα", "A Greek URL must not be mangled on the way to staging.");
            Assert.AreEqual("Καλημέρα", plan.RowsToStage[0].LinkText);
        }
    }
}
