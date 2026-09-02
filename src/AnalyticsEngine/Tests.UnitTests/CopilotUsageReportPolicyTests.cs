using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;
using WebJob.Office365ActivityImporter.Engine.Graph.UsageReports.Copilot;

namespace Tests.UnitTests
{
    /// <summary>
    /// Coverage for the concealed-identity rule extracted in issue #370 - the highest-stakes decision in
    /// the Copilot per-user usage import, and one that previously had no unit test.
    ///
    /// When a tenant enables "concealed user information", Graph still answers 200 OK with one row per
    /// licensed user but replaces the UPN and display name with hashes. Importing those would create one
    /// placeholder user per licensed account - around 200,000 on a large tenant - permanently polluting
    /// the users table and every report built on it, producing joins that are wrong rather than missing.
    ///
    /// Runs with zero Graph, SQL Server, Redis or Service Bus dependency.
    /// </summary>
    [TestClass]
    public class CopilotUsageReportPolicyTests
    {
        /// <summary>
        /// IsIdentityConcealed is computed from the UPN (!LooksLikeRealUpn), so concealment is driven by
        /// realistic data rather than a flag. Microsoft's pseudonyms are a 32-character hex hash.
        /// </summary>
        private static CopilotUsageUserDetailRow Row(string upn)
        {
            return new CopilotUsageUserDetailRow { UserPrincipalName = upn };
        }

        private const string ConcealedA = "0123456789abcdef0123456789abcdef@contoso.onmicrosoft.com";
        private const string ConcealedB = "fedcba9876543210fedcba9876543210@contoso.onmicrosoft.com";

        [TestMethod]
        public void CopilotUsage_AllIdentitiesConcealed_AbortsImportWithoutWriting()
        {
            var parsed = new List<CopilotUsageUserDetailRow>
            {
                Row(ConcealedA),
                Row(ConcealedB),
            };

            var decision = CopilotUsageReportPolicy.EvaluateConcealment(parsed);

            Assert.AreEqual(ConcealedIdentityOutcome.AbortImport, decision.Outcome);
            Assert.AreEqual(0, decision.Importable.Count,
                "Nothing may be written - importing hashes creates one junk user per licensed account.");
            Assert.AreEqual(2, decision.ConcealedCount);
            Assert.AreEqual(2, decision.TotalCount);
        }

        [TestMethod]
        public void CopilotUsage_SomeIdentitiesConcealed_SkipsConcealedRowsAndImportsVisibleOnes()
        {
            var parsed = new List<CopilotUsageUserDetailRow>
            {
                Row("chris@contoso.onmicrosoft.com"),
                Row(ConcealedA),
                Row("o'brien-smith@contoso.onmicrosoft.com"),
            };

            var decision = CopilotUsageReportPolicy.EvaluateConcealment(parsed);

            Assert.AreEqual(ConcealedIdentityOutcome.SkipConcealedRows, decision.Outcome);
            Assert.AreEqual(1, decision.ConcealedCount);
            Assert.AreEqual(3, decision.TotalCount);
            CollectionAssert.AreEqual(
                new[] { "chris@contoso.onmicrosoft.com", "o'brien-smith@contoso.onmicrosoft.com" },
                decision.Importable.Select(r => r.UserPrincipalName).ToArray(),
                "Visible identities must still import, verbatim and in order. The apostrophe and hyphen "
                + "are awkward characters Entra genuinely permits in a UPN (A-Z a-z 0-9 ' . - _ ! # ^ ~), "
                + "so this is reachable data - unlike a Greek UPN, which Entra disallows (#402/#414).");
        }

        [TestMethod]
        public void CopilotUsage_NoIdentitiesConcealed_ReturnsTheCallersOwnListInstance()
        {
            // A POLICY-CONTRACT test. It pins that EvaluateConcealment hands back the caller's own list
            // on the ImportAll path - it does NOT guard the loader's use of that list, and cannot: the
            // loader needs an AnalyticsEntitiesContext, so nothing here executes the handoff. Restoring
            // the `.ToList()` that an earlier revision of this PR had at that handoff would leave every
            // test in this class green. A real guard becomes possible once the persistence port lands
            // (the deferred half of #370).
            //
            // Why the contract matters: on ImportAll this list IS the caller's, and SaveAsync drops
            // unkeyable rows from it with an in-place RemoveAll while the closing "parsed N row(s)" log
            // reads the original. Copying it both allocates a second ~200k-element array and silently
            // changes that operator-facing count. Importable is typed List<T> so the loader cannot copy
            // it without a visible, deliberate call.
            var parsed = new List<CopilotUsageUserDetailRow>
            {
                Row("chris@contoso.onmicrosoft.com"),
                Row("alex@contoso.onmicrosoft.com"),
            };

            var decision = CopilotUsageReportPolicy.EvaluateConcealment(parsed);

            Assert.AreEqual(ConcealedIdentityOutcome.ImportAll, decision.Outcome);
            Assert.AreSame(parsed, decision.Importable, "Must be the caller's list, not a copy.");
        }

        [TestMethod]
        public void CopilotUsage_OnlyOneRowAndItIsConcealed_Aborts()
        {
            // Boundary: a single concealed row is still "every row concealed".
            var decision = CopilotUsageReportPolicy.EvaluateConcealment(
                new List<CopilotUsageUserDetailRow> { Row(ConcealedA) });

            Assert.AreEqual(ConcealedIdentityOutcome.AbortImport, decision.Outcome);
        }

        [TestMethod]
        public void CopilotUsage_EmptyReport_IsNotTreatedAsConcealed()
        {
            // "No rows" is not "every row concealed". Aborting here would mislabel an empty report as a
            // tenant-configuration problem on the Health page, and 0 == 0 makes that an easy mistake.
            var decision = CopilotUsageReportPolicy.EvaluateConcealment(new List<CopilotUsageUserDetailRow>());

            Assert.AreEqual(ConcealedIdentityOutcome.ImportAll, decision.Outcome);
            Assert.AreEqual(0, decision.TotalCount);
            Assert.AreEqual(0, decision.Importable.Count);
        }

        [TestMethod]
        public void CopilotUsage_NullReport_IsHandledWithoutThrowing()
        {
            var decision = CopilotUsageReportPolicy.EvaluateConcealment(null);

            Assert.AreEqual(ConcealedIdentityOutcome.ImportAll, decision.Outcome);
            Assert.AreEqual(0, decision.Importable.Count);
        }
    }
}
