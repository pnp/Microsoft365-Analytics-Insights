using Azure;
using Azure.AI.TextAnalytics;
using Azure.Core;
using DataUtils;
using DataUtils.Sql;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Tests.UnitTests
{
    /// <summary>
    /// Tenant text - Teams messages, page comments, values SQL Server quotes in its errors - must never reach
    /// the logs, which go to Application Insights and the WebJob console.
    /// </summary>
    [TestClass]
    public class TenantContentLoggingTests
    {
        private const string TenantText = "Contoso private channel text";

        [TestMethod]
        public async Task RejectedDocuments_AreLoggedByIdAndErrorCode_NotByTheirText()
        {
            // "a": sentiment analysis rejects the document. "b": language detection rejects it.
            var client = new CognitiveServicesClient(new RejectingTextAnalyticsClient());
            var logger = new CapturingLogger();
            var samples = new[]
            {
                new TextAnalysisSample<object> { Id = "a", Text = TenantText + " one", Parent = new object() },
                new TextAnalysisSample<object> { Id = "b", Text = TenantText + " two", Parent = new object() },
            };

            var results = await samples.GetCognitiveDataStats(client, logger);

            Assert.AreEqual(0, results.Count, "Neither document can be scored.");
            var everything = string.Join(" | ", logger.Lines.Select(l => l.Message));
            Assert.IsFalse(everything.Contains(TenantText), "The text was logged: " + everything);
            var errors = logger.Lines.Where(l => l.Message.StartsWith("Error in sentiment analysis")).Select(l => l.Message).ToList();
            Assert.AreEqual(2, errors.Count, everything);
            Assert.IsTrue(errors.Any(m => m.Contains("document a") && m.Contains("InvalidDocument")), everything);
            Assert.IsTrue(errors.Any(m => m.Contains("document b") && m.Contains("language detection returned error UnsupportedLanguageCode")), everything);
        }

        private const string Value = "Contoso Tower 47";

        [DataTestMethod]
        [DataRow("String or binary data would be truncated in table 'tempdb.dbo.##staging', column 'full_url'. Truncated value: '" + Value + "'.",
            "Truncated value: <redacted>.")]
        [DataRow("Cannot insert duplicate key row in object 'dbo.user_office_locations' with unique index 'IX_name'. The duplicate key value is (" + Value + ").",
            "The duplicate key value is (<redacted>).")]
        [DataRow("Violation of PRIMARY KEY constraint 'PK_lookup'. Cannot insert duplicate key in object 'dbo.lookup'. The duplicate key value is (" + Value + ", 42).",
            "The duplicate key value is (<redacted>).")]
        [DataRow("Conversion failed when converting the nvarchar value '" + Value + "' to data type int.",
            "converting the nvarchar value '<redacted>' to data type int.")]
        public void SqlErrorMessages_RemoveTheValueSqlServerQuoted(string message, string expectedEnding)
        {
            var redacted = SqlErrorMessages.WithoutValues(message);

            Assert.IsFalse(redacted.Contains(Value), redacted);
            StringAssert.EndsWith(redacted, expectedEnding);
        }

        [TestMethod]
        public void SqlErrorMessages_LeaveAMessageWithoutAValueUnchanged()
        {
            const string message = "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.";

            Assert.AreEqual(message, SqlErrorMessages.WithoutValues(message));
        }

        /// <summary>Detects English for every document except "b", and rejects document "a" in sentiment analysis.</summary>
        private sealed class RejectingTextAnalyticsClient : TextAnalyticsClient
        {
            public override Task<Response<DetectLanguageResultCollection>> DetectLanguageBatchAsync(
                IEnumerable<DetectLanguageInput> documents, TextAnalyticsRequestOptions options = null, CancellationToken cancellationToken = default)
            {
                var results = documents.Select(d => d.Id == "b"
                    ? TextAnalyticsModelFactory.DetectLanguageResult(d.Id, Error("UnsupportedLanguageCode"))
                    : TextAnalyticsModelFactory.DetectLanguageResult(d.Id, TextAnalyticsModelFactory.TextDocumentStatistics(d.Text.Length, 1),
                        TextAnalyticsModelFactory.DetectedLanguage("English", "en", 1.0, new List<TextAnalyticsWarning>()))).ToList();
                return Task.FromResult(Response.FromValue(
                    TextAnalyticsModelFactory.DetectLanguageResultCollection(results, Batch(results.Count), "test"), new StubResponse()));
            }

            public override Task<Response<AnalyzeSentimentResultCollection>> AnalyzeSentimentBatchAsync(
                IEnumerable<TextDocumentInput> documents, AnalyzeSentimentOptions options = null, CancellationToken cancellationToken = default) =>
                Sentiment(documents);

            public override Task<Response<AnalyzeSentimentResultCollection>> AnalyzeSentimentBatchAsync(
                IEnumerable<TextDocumentInput> documents, TextAnalyticsRequestOptions options, CancellationToken cancellationToken = default) =>
                Sentiment(documents);

            private static Task<Response<AnalyzeSentimentResultCollection>> Sentiment(IEnumerable<TextDocumentInput> documents)
            {
                var results = documents.Select(d => d.Id == "a"
                    ? TextAnalyticsModelFactory.AnalyzeSentimentResult(d.Id, Error("InvalidDocument"))
                    : TextAnalyticsModelFactory.AnalyzeSentimentResult(d.Id, TextAnalyticsModelFactory.TextDocumentStatistics(d.Text.Length, 1),
                        TextAnalyticsModelFactory.DocumentSentiment(TextSentiment.Neutral, 0.1, 0.8, 0.1, new List<SentenceSentiment>(), new List<TextAnalyticsWarning>()))).ToList();
                return Task.FromResult(Response.FromValue(
                    TextAnalyticsModelFactory.AnalyzeSentimentResultCollection(results, Batch(results.Count), "test"), new StubResponse()));
            }

            private static TextAnalyticsError Error(string code) => TextAnalyticsModelFactory.TextAnalyticsError(code, "Rejected by the service.", null);

            private static TextDocumentBatchStatistics Batch(int count) => TextAnalyticsModelFactory.TextDocumentBatchStatistics(count, count, 0, count);
        }

        private sealed class StubResponse : Response
        {
            public override int Status => 200;
            public override string ReasonPhrase => "OK";
            public override Stream ContentStream { get; set; }
            public override string ClientRequestId { get; set; } = "test";
            public override void Dispose() { }
            protected override bool ContainsHeader(string name) => false;
            protected override IEnumerable<HttpHeader> EnumerateHeaders() => Enumerable.Empty<HttpHeader>();
            protected override bool TryGetHeader(string name, out string value) { value = null; return false; }
            protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = null; return false; }
        }
    }
}
