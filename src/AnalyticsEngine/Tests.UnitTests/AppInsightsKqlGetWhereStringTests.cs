using Azure.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;
using System.Threading.Tasks;
using WebJob.AppInsightsImporter.Engine;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression coverage for AppInsightsAPIClient.GetWhereString, the KQL fragment
    /// that bounds every Custom Events / Page Views query. The earlier cleanup PR
    /// URL-encoded the query body; this pins down the underlying timestamp formatting
    /// so a future change cannot silently break all queries (which would silently
    /// return empty result sets, not throw).
    /// </summary>
    [TestClass]
    public class AppInsightsKqlGetWhereStringTests
    {
        // Minimal TokenCredential stub - the ctor only requires non-null; no token request is made by GetWhereString.
        private sealed class NoOpCredential : TokenCredential
        {
            public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new AccessToken("noop", DateTimeOffset.UtcNow.AddHours(1));
            public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
                => new ValueTask<AccessToken>(GetToken(requestContext, cancellationToken));
        }

        private static AppInsightsAPIClient NewClient()
        {
            const string cs = "InstrumentationKey=11111111-1111-1111-1111-111111111111;ApplicationId=22222222-2222-2222-2222-222222222222";
            return new AppInsightsAPIClient(cs, new NoOpCredential(), NullLogger.Instance);
        }

        [TestMethod]
        public void GetWhereString_ForGivenDate_BoundsExactlyOneFullDay()
        {
            using (var client = NewClient())
            {
                var forDate = new DateTime(2026, 5, 30, 0, 0, 0, DateTimeKind.Utc);
                var where = client.GetWhereString(forDate);

                // Inclusive lower bound of the day at 00:00:00 and exclusive upper bound at the
                // next day's 00:00:00 (note: 31, not 30 23:59:59 - the previous-day-overlap
                // semantics would double-count or miss midnight rows).
                StringAssert.Contains(where, "timestamp >= todatetime('2026-05-30 00:00:00')");
                StringAssert.Contains(where, "timestamp < todatetime('2026-05-31 00:00:00')");
                StringAssert.Contains(where, " and ");
            }
        }

        [TestMethod]
        public void GetWhereString_AtMonthBoundary_AdvancesMonthCorrectly()
        {
            using (var client = NewClient())
            {
                var where = client.GetWhereString(new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc));

                StringAssert.Contains(where, "todatetime('2026-01-31 00:00:00')");
                StringAssert.Contains(where, "todatetime('2026-02-01 00:00:00')");
            }
        }

        [TestMethod]
        public void GetWhereString_AtYearBoundary_AdvancesYearCorrectly()
        {
            using (var client = NewClient())
            {
                var where = client.GetWhereString(new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc));

                StringAssert.Contains(where, "todatetime('2026-12-31 00:00:00')");
                StringAssert.Contains(where, "todatetime('2027-01-01 00:00:00')");
            }
        }
    }
}
