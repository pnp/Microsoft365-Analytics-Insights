extern alias AnalyticsWeb;

using AnalyticsWeb::Web.AnalyticsWeb.Models.UserOrgs;
using Common.Entities.UserOrgs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace Tests.UnitTests
{
    /// <summary>
    /// How a failed attribute probe is explained to the administrator testing it.
    /// </summary>
    [TestClass]
    public class UserOrgGraphProbeTests
    {
        private static EntraOrgAttributeSpec Spec(string name)
        {
            EntraOrgAttributeSpec spec;
            string error;
            Assert.IsTrue(EntraOrgAttributeSpec.TryParse(name, out spec, out error), error);
            return spec;
        }

        [TestMethod]
        public void ARejectedPropertyNamesThePropertyGraphWasAskedFor()
        {
            var outcome = UserOrgGraphProbe.DescribeFailure(HttpStatusCode.BadRequest, Spec("extensionAttribute4"));

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(UserOrgMessageCodes.PropertyRejected, outcome.MessageCode);
            Assert.AreEqual(
                "onPremisesExtensionAttributes",
                outcome.MessageValues["property"],
                "What went into $select - the container - is what Graph rejected.");
            StringAssert.Contains(outcome.Message, "onPremisesExtensionAttributes");
        }

        [DataTestMethod]
        [DataRow(HttpStatusCode.NotFound, UserOrgMessageCodes.UserNotFound, DisplayName = "404: the person, not the attribute")]
        [DataRow(HttpStatusCode.Unauthorized, UserOrgMessageCodes.NotAuthorised, DisplayName = "401")]
        [DataRow(HttpStatusCode.Forbidden, UserOrgMessageCodes.NotAuthorised, DisplayName = "403")]
        [DataRow((HttpStatusCode)429, UserOrgMessageCodes.Throttled, DisplayName = "429")]
        public void AKnownFailureHasItsOwnCodeAndNothingToQuote(HttpStatusCode status, string code)
        {
            var outcome = UserOrgGraphProbe.DescribeFailure(status, Spec("employeeType"));

            Assert.IsFalse(outcome.Succeeded);
            Assert.AreEqual(code, outcome.MessageCode);
            Assert.IsNull(outcome.MessageValues);
            Assert.IsFalse(string.IsNullOrWhiteSpace(outcome.Message));
        }

        [DataTestMethod]
        [DataRow(500)]
        [DataRow(502)]
        [DataRow(503)]
        [DataRow(409)]
        public void AnythingElseGivesOnlyTheStatus(int status)
        {
            // Graph's own error text is never passed on: it is English whatever the reader's language,
            // it can echo the request back, and it is rendered in the portal. The status is the only
            // fact - which is why DescribeFailure is not handed the response body at all.
            var outcome = UserOrgGraphProbe.DescribeFailure((HttpStatusCode)status, Spec("employeeType"));

            Assert.AreEqual(UserOrgMessageCodes.GraphError, outcome.MessageCode);
            Assert.AreEqual(status, outcome.MessageValues["status"]);
            Assert.AreEqual(1, outcome.MessageValues.Count);
            Assert.AreEqual(
                $"Microsoft Graph returned HTTP {status}. Try again in a moment; the service logs have the detail "
                    + "if it keeps happening.",
                outcome.Message);
        }

        [TestMethod]
        public void DescribingAFailureNeverDecidesWhetherThePropertyWasRejected()
        {
            // Only the caller, which knows it was a 400 to a $select, may block a save; DescribeFailure
            // must not quietly mark a throttled or missing user as a bad attribute.
            foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, (HttpStatusCode)429, HttpStatusCode.InternalServerError })
            {
                Assert.IsFalse(UserOrgGraphProbe.DescribeFailure(status, Spec("employeeType")).RejectedTheProperty, status.ToString());
            }
        }
    }
}
