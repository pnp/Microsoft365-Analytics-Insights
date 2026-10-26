using DataUtils;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Net;
using System.Net.Http;

namespace Tests.UnitTests
{
    [TestClass]
    public class Net10ReleaseDiagnosticTests
    {
        [TestMethod]
        public void DerivedStatusTakesPrecedenceWithoutAmbiguousReflection()
        {
            var error = new LegacyStatusException();
            Assert.AreEqual(403, ExceptionDescriber.FindHttpStatus(error));
            Assert.AreEqual("403", ExceptionDescriber.Dimensions(error)["HttpStatus"]);
        }

        [TestMethod]
        public void NativeNullableStatusAndInnerFailuresStillHaveTheirStatus()
        {
            var error = new HttpRequestException("synthetic", null, HttpStatusCode.Gone);
            Assert.AreEqual(410, ExceptionDescriber.FindHttpStatus(new InvalidOperationException("synthetic", error)));
            Assert.IsNull(ExceptionDescriber.FindHttpStatus(new HttpRequestException("synthetic")));
        }

        [TestMethod]
        public void AThrowingStatusGetterCannotReplaceTheFailureBeingLogged()
        {
            Assert.AreEqual(503, ExceptionDescriber.FindHttpStatus(new ThrowingStatusException()));
            Assert.IsNull(ExceptionDescriber.FindHttpStatus(new InvalidOperationException("synthetic")));
        }

        [TestMethod]
        public void LocalDbDiscoveryUsesPathOrNewestInstalledToolsNotAPinnedVersion()
        {
            var root = Path.Combine(Path.GetTempPath(), "Net10SqlToolLookup_" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.IsNull(CopilotCostCollectorSqlTests.FindLocalDbExecutable(null, root));
                var oldTools = Path.Combine(root, "Microsoft SQL Server", "150", "Tools", "Binn");
                var newTools = Path.Combine(root, "Microsoft SQL Server", "170", "Tools", "Binn");
                var onPath = Path.Combine(root, "tools-on-path");
                foreach (var directory in new[] { oldTools, newTools, onPath })
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "SqlLocalDB.exe"), "synthetic lookup only; never executed");
                }
                Assert.AreEqual(Path.Combine(onPath, "SqlLocalDB.exe"),
                    CopilotCostCollectorSqlTests.FindLocalDbExecutable("\"" + onPath + "\"", root));
                Assert.AreEqual(Path.Combine(newTools, "SqlLocalDB.exe"),
                    CopilotCostCollectorSqlTests.FindLocalDbExecutable(string.Empty, root));
                File.Delete(Path.Combine(newTools, "SqlLocalDB.exe"));
                Assert.AreEqual(Path.Combine(oldTools, "SqlLocalDB.exe"),
                    CopilotCostCollectorSqlTests.FindLocalDbExecutable(string.Empty, root));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed class LegacyStatusException : HttpRequestException
        {
            internal LegacyStatusException() : base("synthetic", null, HttpStatusCode.ServiceUnavailable) { }
            public new HttpStatusCode StatusCode => HttpStatusCode.Forbidden;
        }

        private sealed class ThrowingStatusException : HttpRequestException
        {
            internal ThrowingStatusException() : base("synthetic", null, HttpStatusCode.ServiceUnavailable) { }
            public new int StatusCode => throw new InvalidOperationException("synthetic getter failure");
        }
    }
}
