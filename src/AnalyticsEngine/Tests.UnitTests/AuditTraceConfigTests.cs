using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WebJob.Office365ActivityImporter.Engine.ActivityAPI;

namespace Tests.UnitTests
{
    /// <summary>
    /// Which raw audit records <c>--traceAuditDir</c> / <c>--traceAuditEmail</c> write to disk. They hold personal
    /// data, so the email filter must catch the traced person's records wherever the address appears, and nobody
    /// else's.
    /// </summary>
    [TestClass]
    public class AuditTraceConfigTests
    {
        // ShouldTrace only decides; nothing is written here.
        private const string TraceDirectory = @"C:\audit-trace";

        private static bool TracedFor(string email, string recordJson)
            => AuditTraceConfig.ShouldTrace(JObject.Parse(recordJson), TraceDirectory, email);

        [TestMethod]
        public void Trace_EmailFilter_MatchesTheAddressAnywhereInTheRecord_IgnoringCase()
        {
            Assert.IsTrue(TracedFor("ann@contoso.com", "{\"UserId\":\"Ann@Contoso.com\"}"));
            Assert.IsTrue(TracedFor(" ann@contoso.com ", "{\"Item\":{\"Recipients\":[\"bob@contoso.com\",null,\"ANN@contoso.com\"]}}"),
                "Nested arrays, null entries and a padded command-line value.");
            Assert.IsTrue(TracedFor("ann@contoso.com", "{\"Parameters\":\"To: Bob <bob@contoso.com>; Ann <ann@contoso.com>\"}"));
            Assert.IsTrue(TracedFor("ann@contoso.com", "{\"Comment\":\"Shared by ann@contoso.com.\"}"), "A full stop after the address ends it.");
        }

        [TestMethod]
        public void Trace_EmailFilter_DoesNotWriteOtherPeoplesRecords()
        {
            Assert.IsFalse(TracedFor("ann@contoso.com", "{\"UserId\":\"bob@contoso.com\",\"ClientIP\":null,\"RecordType\":2}"));
            Assert.IsFalse(TracedFor("ann@contoso.com", "{\"UserId\":\"joann@contoso.com\"}"), "A different address that ends with the traced one.");
            Assert.IsFalse(TracedFor("ann@contoso.com", "{\"UserId\":\"j.ann@contoso.com\"}"));
            Assert.IsFalse(TracedFor("ann@contoso.com", "{\"UserId\":\"ann@contoso.com.au\"}"), "The same name under a longer domain.");
        }

        [TestMethod]
        public void Trace_WithoutAnEmail_WritesEveryRecord_AndWithoutADirectory_WritesNone()
        {
            var record = JObject.Parse("{\"UserId\":\"bob@contoso.com\"}");

            Assert.IsTrue(AuditTraceConfig.ShouldTrace(record, TraceDirectory, null), "A directory alone is the documented dump-everything mode.");
            Assert.IsTrue(AuditTraceConfig.ShouldTrace(record, TraceDirectory, "  "));
            Assert.IsFalse(AuditTraceConfig.ShouldTrace(record, null, "bob@contoso.com"), "Nothing is written without a directory.");
            Assert.IsFalse(AuditTraceConfig.ShouldTrace(record, " ", null));
        }
    }
}
