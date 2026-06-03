using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using WebJob.AppInsightsImporter.Engine.APIResponseParsers.CustomEvents;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for the PageRequestId validation tightened in
    /// PR #95 (PageExit IsValid must reject Guid.Empty). PageRequestId is
    /// declared as Guid? but constructor-initialised to Guid.Empty, so the
    /// pre-fix check (!= null) was effectively always true.
    /// </summary>
    [TestClass]
    public class PageExitEventValidationTests
    {
        [TestMethod]
        public void PageExit_DefaultCustomProps_PageRequestIdIsGuidEmpty()
        {
            var pe = new PageExitEventAppInsightsQueryResult();
            Assert.IsNotNull(pe.CustomProperties);
            Assert.AreEqual(Guid.Empty, pe.CustomProperties.PageRequestId,
                "Sanity check: default-constructed PageExit has Guid.Empty (not null) PageRequestId; IsValid must therefore reject Guid.Empty explicitly.");
        }

        [TestMethod]
        public void PageExit_GuidEmptyPageRequestId_IsInvalid()
        {
            var pe = new PageExitEventAppInsightsQueryResult();
            pe.CustomProperties.PageRequestId = Guid.Empty;
            pe.CustomProperties.ActiveTime = 46;
            Assert.IsFalse(pe.IsValid,
                "PageExit with Guid.Empty PageRequestId must not be considered valid.");
        }

        [TestMethod]
        public void PageExit_NullPageRequestId_IsInvalid()
        {
            var pe = new PageExitEventAppInsightsQueryResult();
            pe.CustomProperties.PageRequestId = null;
            pe.CustomProperties.ActiveTime = 46;
            Assert.IsFalse(pe.IsValid,
                "PageExit with null PageRequestId must not be considered valid.");
        }

        [TestMethod]
        public void PageExit_NonEmptyPageRequestId_IsValid()
        {
            var pe = new PageExitEventAppInsightsQueryResult();
            pe.CustomProperties.PageRequestId = Guid.NewGuid();
            pe.CustomProperties.ActiveTime = 46;
            Assert.IsTrue(pe.IsValid);
        }
    }
}
