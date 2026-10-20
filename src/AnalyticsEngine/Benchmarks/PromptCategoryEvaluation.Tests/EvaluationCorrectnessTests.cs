using Common.Entities.Config;
using Common.Entities.PromptCategories;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PromptCategoryEvaluation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCategoryEvaluationTests
{
    // These are deterministic harness/transport correctness checks, never evidence of model quality.
    [TestClass]
    public class EvaluationCorrectnessTests
    {
        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OutputPath_RejectsJunctionIntoWorktreeBeforeCreatingReport(bool nested)
        {
            WithPrivateDirectory(root =>
            {
                var checkout = Path.Combine(root, "checkout");
                var target = Path.Combine(checkout, "reports");
                Directory.CreateDirectory(Path.Combine(checkout, ".git"));
                Directory.CreateDirectory(nested ? Path.Combine(target, "nested") : target);
                var alias = Path.Combine(root, "private-reports");
                using (var process = Process.Start(new ProcessStartInfo(
                    Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    $"/d /c mklink /J \"{alias}\" \"{target}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    process.WaitForExit();
                    Assert.AreEqual(0, process.ExitCode, "The synthetic junction must exist for this regression.");
                }
                try
                {
                    var output = Path.Combine(nested ? Path.Combine(alias, "nested") : alias, "report.json");
                    Assert.ThrowsException<ArgumentException>(() => Evaluation.ValidateOutputPath(output));
                    Assert.IsFalse(File.Exists(output));
                }
                finally { Directory.Delete(alias); }
            });
        }

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OutputPath_RejectsRepositoryDirectoryOrLinkedWorktreeFile(bool linked)
        {
            WithPrivateDirectory(root =>
            {
                if (linked) File.WriteAllText(Path.Combine(root, ".git"), @"gitdir: ..\main\.git\worktrees\linked");
                else Directory.CreateDirectory(Path.Combine(root, ".git"));
                var directory = Path.Combine(root, "reports");
                Directory.CreateDirectory(directory);
                var output = Path.Combine(directory, "report.json");
                Assert.ThrowsException<ArgumentException>(() => Evaluation.ValidateOutputPath(output));
                Assert.IsFalse(File.Exists(output));
            });
        }

        [TestMethod]
        public void OutputPath_AcceptsNewPrivateFileButNeverOverwritesOrCreatesDirectories()
        {
            WithPrivateDirectory(root =>
            {
                var output = Path.Combine(root, "report.json");
                Assert.AreEqual(Path.GetFullPath(output), Evaluation.ValidateOutputPath(output));
                Assert.IsFalse(File.Exists(output));
                File.WriteAllText(output, "{}");
                Assert.ThrowsException<ArgumentException>(() => Evaluation.ValidateOutputPath(output));
                Assert.AreEqual("{}", File.ReadAllText(output));
                var missing = Path.Combine(root, "missing", "report.json");
                Assert.ThrowsException<ArgumentException>(() => Evaluation.ValidateOutputPath(missing));
                Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(missing)));
            });
        }

        private static void WithPrivateDirectory(Action<string> test)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "M365AnalyticsTests", "Evaluation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { test(root); }
            finally { Directory.Delete(root, recursive: true); }
        }

        [TestMethod]
        public void Metrics_KeepWrongAnswersUnknownRefusalAndErrorsDistinct()
        {
            var report = new EvaluationReport
            {
                Decisions = new List<Decision>
                {
                    D("analysis", "analysis"), D("analysis", "other"),
                    D("other", "other"), D("other", "analysis"),
                    D("analysis", null, "refused"), D("other", null, "timeout")
                }
            };
            Evaluation.Calculate(report, new[] { "analysis", "other" });
            Assert.AreEqual(6, report.Total);
            Assert.AreEqual(4, report.Classified);
            Assert.AreEqual(2, report.Correct);
            Assert.AreEqual(2, report.Unknown);
            Assert.AreEqual(2, report.NotClassified);
            Assert.AreEqual(1.0 / 3, report.OverallAccuracy.Value, 0.00001);
            Assert.AreEqual(0.5, report.ClassifiedAccuracy.Value, 0.00001);
            Assert.AreEqual(1.0 / 3, report.UnknownRate.Value, 0.00001);
            Assert.AreEqual(1.0 / 3, report.NotClassifiedRate.Value, 0.00001);
            Assert.AreEqual(1.0 / 3, report.MacroRecall.Value, 0.00001);
            Assert.AreEqual(1, report.Confusion["analysis"]["analysis"]);
            Assert.AreEqual(1, report.Confusion["analysis"]["other"]);
            Assert.AreEqual(1, report.Confusion["analysis"]["not-classified"]);
            Assert.AreEqual(1, report.Failures["refused"]);
            Assert.AreEqual(1, report.Failures["timeout"]);
            Assert.AreEqual(3, report.Categories[0].Total);
            Assert.AreEqual(2, report.Categories[0].Classified);
            Assert.AreEqual(0.5, report.Categories[0].Precision.Value, 0.00001);
        }

        [TestMethod]
        public void Metrics_EmptyOrEntirelyFailedRunsHaveNoConditionalAccuracy()
        {
            var report = new EvaluationReport { Decisions = new List<Decision>() };
            Evaluation.Calculate(report, new[] { "other" });
            Assert.IsNull(report.OverallAccuracy);
            Assert.IsNull(report.ClassifiedAccuracy);
            Assert.IsNull(report.MacroRecall);
            report.Decisions.Add(D("other", null, "refused"));
            Evaluation.Calculate(report, new[] { "other" });
            Assert.AreEqual(0.0, report.OverallAccuracy.Value);
            Assert.IsNull(report.ClassifiedAccuracy);
            Assert.IsNull(report.Categories.Single().Precision);
            Assert.AreEqual(1.0, report.NotClassifiedRate.Value);
        }

        [TestMethod]
        public void Metrics_LanguageAndChallengeSlicesKeepCoverageFailuresAndFalseUnknownsVisible()
        {
            var report = new EvaluationReport
            {
                Decisions = new List<Decision>
                {
                    new Decision { Language = "en", Stratum = "clear", Expected = "analysis", CategoryId = "analysis" },
                    new Decision { Language = "en", Stratum = "injection", Expected = "analysis", CategoryId = "other" },
                    new Decision { Language = "es", Stratum = "unknown", Expected = "other", CategoryId = "other" },
                    new Decision { Language = "es", Stratum = "clear", Expected = "analysis", Failure = "timeout" },
                    new Decision { Language = "el", Stratum = "injection", Expected = "analysis", Failure = "not-sent" }
                }
            };
            Evaluation.Calculate(report, new[] { "analysis", "other" });
            Assert.AreEqual(0.25, report.KnownIntentUnknownRate.Value);
            Assert.AreEqual(0.4, report.Categories.Single(c => c.Id == "analysis").F1.Value, 0.00001);
            Assert.AreEqual(2.0 / 3, report.Categories.Single(c => c.Id == "other").F1.Value, 0.00001);
            Assert.AreEqual((0.4 + 2.0 / 3) / 2, report.MacroF1.Value, 0.00001);
            var english = report.Languages.Single(s => s.Id == "en");
            Assert.AreEqual(0.5, english.OverallAccuracy.Value);
            Assert.AreEqual(0.5, english.UnknownRate.Value);
            var spanish = report.Languages.Single(s => s.Id == "es");
            Assert.AreEqual(0.5, spanish.OverallAccuracy.Value);
            Assert.AreEqual(1.0, spanish.ClassifiedAccuracy.Value);
            Assert.AreEqual(0.5, spanish.NotClassifiedRate.Value);
            var greek = report.Languages.Single(s => s.Id == "el");
            Assert.AreEqual(1, greek.NotClassified);
            Assert.IsNull(greek.ClassifiedAccuracy);
            var injection = report.Strata.Single(s => s.Id == "injection");
            Assert.AreEqual(0.0, injection.OverallAccuracy.Value);
            Assert.AreEqual(0.5, injection.NotClassifiedRate.Value);
            Assert.AreEqual(report.Total, report.Languages.Sum(s => s.Total));
            Assert.AreEqual(report.Total, report.Strata.Sum(s => s.Total));
        }

        [TestMethod]
        public void Budget_RequiresCurrentPositiveConservativeCurrencyApprovalIncludingFallback()
        {
            Assert.AreEqual(0.01m, EvaluationBudget.FromApproval("USD", "0.01", "0.001", 10).MaximumEstimatedRunCost);
            Assert.ThrowsException<ArgumentException>(() => EvaluationBudget.FromApproval("USD", "0,01", "0.001", 10));
            Assert.ThrowsException<ArgumentException>(() => EvaluationBudget.FromApproval("USD", "0.01", "0,001", 10));
            Assert.ThrowsException<ArgumentException>(() => EvaluationBudget.FromApproval("USD", "1,000.00", "0.001", 10));
            var budget = new EvaluationBudget
            {
                Currency = "USD", ApprovedMaximumCost = 0.01m, MaximumEstimatedCostPerHttpAttempt = 0.001m
            };
            budget.Validate(10);
            Assert.AreEqual(0.01m, budget.MaximumEstimatedRunCost);
            budget.ApprovedMaximumCost = 0.009m;
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10));
            budget.ApprovedMaximumCost = decimal.MaxValue;
            budget.MaximumEstimatedCostPerHttpAttempt = decimal.MaxValue;
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10), "Overflow must reject, not wrap the approved envelope.");
            budget.MaximumEstimatedCostPerHttpAttempt = 0;
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10));
            budget.MaximumEstimatedCostPerHttpAttempt = -1;
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10));
            budget.MaximumEstimatedCostPerHttpAttempt = 0.001m;
            budget.Currency = "usd";
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10));
            budget.Currency = "USD";
            budget.ApprovedMaximumCost = 0;
            Assert.ThrowsException<ArgumentException>(() => budget.Validate(10));
        }

        [TestMethod]
        public async Task Preparation_RecordsFrozenCoverageAndBoundsButNeverQualityOrPromptContent()
        {
            var samples = Evaluation.LoadSamples();
            var plan = Evaluation.Prepare(PromptCategoryConfiguration.Defaults(), samples, 50);
            var report = await Evaluation.RunAsync(new FixtureBackend(), PromptCategoryConfiguration.Defaults(),
                samples, 50, CancellationToken.None);
            Assert.AreEqual(report.DatasetSha256, plan.DatasetSha256);
            Assert.AreEqual(report.TaxonomyVersion, plan.TaxonomyVersion);
            Assert.AreEqual(samples.Count, plan.CategoryCounts.Values.Sum());
            Assert.AreEqual(samples.Count, plan.LanguageCounts.Values.Sum());
            Assert.AreEqual(samples.Count, plan.StratumCounts.Values.Sum());
            Assert.AreEqual(2 * ((samples.Count + 9) / 10), plan.MaximumHttpAttempts);
            Assert.AreEqual(plan.MaximumHttpAttempts * 500, plan.MaximumOutputTokens);
            var serialized = JsonConvert.SerializeObject(plan);
            Assert.IsFalse(serialized.Contains("\"OverallAccuracy\""));
            Assert.IsFalse(serialized.Contains("\"Decisions\""));
            Assert.IsFalse(serialized.Contains(samples[0].Prompt));
            StringAssert.Contains(plan.EvidenceKind, "no-model-calls-no-accuracy-evidence");
            // An opt-in test-host export supports offline preparation on hosts that cannot launch native runners.
            var output = Environment.GetEnvironmentVariable("PROMPT_EVAL_PREPARE_OUTPUT");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Evaluation.WritePrivateReport(output, plan);
                Assert.AreEqual(serialized, JsonConvert.SerializeObject(JsonConvert.DeserializeObject(File.ReadAllText(output))));
                Assert.ThrowsException<ArgumentException>(() => Evaluation.WritePrivateReport(output, plan));
            }
        }

        [TestMethod]
        public void Dataset_CoversEveryAllowedCategoryLanguagesAndChallengeStrata()
        {
            var samples = Evaluation.LoadSamples();
            var taxonomy = PromptCategoryConfiguration.Defaults();
            Evaluation.Validate(samples, taxonomy, 100);
            foreach (var category in taxonomy.Categories)
                foreach (var language in new[] { "en", "es", "el" })
                    Assert.IsTrue(samples.Any(s => s.Expected == category.Id && s.Language == language));
            foreach (var stratum in new[] { "clear", "unknown", "ambiguous", "injection", "refusal-probe" })
                Assert.IsTrue(samples.Any(s => s.Stratum == stratum));
            Assert.ThrowsException<ArgumentException>(() => Evaluation.Validate(samples, taxonomy, samples.Count - 1));
            var duplicate = samples.Concat(new[] { samples[0] }).ToArray();
            Assert.ThrowsException<ArgumentException>(() => Evaluation.Validate(duplicate, taxonomy, 100));
            Assert.ThrowsException<ArgumentException>(() => Evaluation.Validate(samples.Where(s => s.Expected != "other").ToArray(), taxonomy, 100));
        }

        [DataTestMethod]
        [DataRow("refused")]
        [DataRow("invalid-response")]
        [DataRow("throttled")]
        [DataRow("access-denied")]
        [DataRow("timeout")]
        [DataRow("service-failure")]
        public async Task Failures_AreNotSemanticMisclassifications_AndRetriesRemainBounded(string failure)
        {
            var backend = new FixtureBackend { Failure = failure };
            var samples = Evaluation.LoadSamples();
            var report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(), samples, 100, CancellationToken.None);
            var halts = new[] { "throttled", "access-denied", "timeout", "service-failure" }.Contains(failure);
            Assert.AreEqual(halts ? 1 : (samples.Count + 9) / 10, backend.Calls);
            Assert.AreEqual(0, report.Classified);
            Assert.AreEqual(0, report.Unknown);
            Assert.AreEqual(samples.Count, report.NotClassified);
            Assert.AreEqual(halts ? 10 : samples.Count, report.Failures[failure]);
            if (halts) Assert.AreEqual(samples.Count - 10, report.Failures["not-sent"]);
            Assert.IsNull(report.ClassifiedAccuracy);
        }

        [TestMethod]
        public async Task BackendContract_UsesProductionStrictSchemaAndSeparatesMalformedOutput()
        {
            var handler = new FixtureHandler();
            var settings = new AppConfig
            {
                FoundryPromptCategorisationEndpoint = "https://contoso.openai.azure.com/",
                FoundryPromptCategorisationDeployment = "fictional-evaluation-model",
                FoundryPromptCategorisationKey = "fictional-key"
            };
            using (var backend = new AzureFoundryPromptCategoryBackend(settings, new HttpClient(handler)))
            {
                var report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(),
                    Evaluation.LoadSamples(), 100, CancellationToken.None);
                Assert.AreEqual(0, report.Classified);
                Assert.AreEqual(report.Total, report.Failures["invalid-response"]);
                Assert.AreEqual(handler.Calls, report.HttpAttempts);
                var serialized = JsonConvert.SerializeObject(report);
                Assert.IsFalse(serialized.Contains("private-raw-response"));
                Assert.IsFalse(serialized.Contains("fictional-key"));
                Assert.IsFalse(serialized.Contains("openai.azure.com"));
                Assert.IsFalse(serialized.Contains("Summarise this fictional"));
            }
        }

        [DataTestMethod]
        [DataRow("valid", null)]
        [DataRow("refusal", "refused")]
        [DataRow("content-filter", "content-filtered")]
        [DataRow("length", "output-limit")]
        [DataRow("missing", "invalid-envelope")]
        [DataRow("malformed", "invalid-envelope")]
        [DataRow("missing-message", "invalid-envelope")]
        [DataRow("missing-content", "invalid-envelope")]
        [DataRow("incomplete", "incomplete-response")]
        [DataRow("http-error", "service-failure")]
        public async Task BackendContract_ValidDecisionsRefusalsAndErrorBodiesStaySeparate(string response, string failure)
        {
            var handler = new FixtureHandler { Response = response };
            var settings = new AppConfig
            {
                FoundryPromptCategorisationEndpoint = "https://contoso.openai.azure.com/",
                FoundryPromptCategorisationDeployment = "fictional-evaluation-model",
                FoundryPromptCategorisationKey = "fictional-key"
            };
            using (var backend = new AzureFoundryPromptCategoryBackend(settings, new HttpClient(handler)))
            {
                var result = await backend.ClassifyAsync(PromptCategoryConfiguration.Defaults(), new[] { "Fictional greeting." }, CancellationToken.None);
                var consumerFailure = failure == null ? null :
                    failure == "refused" || failure == "content-filtered" ? "refused" :
                    failure == "service-failure" ? "service-failure" : "invalid-response";
                Assert.AreEqual(consumerFailure, result.Failure,
                    "Importer/test-drive must receive an existing translated failure code, not evaluation-only detail.");
                Assert.AreEqual(1, result.Attempts);
                Assert.AreEqual(failure == null, result.Categories != null);
                var report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(),
                    Evaluation.LoadSamples(), 100, CancellationToken.None);
                if (failure == null)
                {
                    Assert.AreEqual(report.Total, report.Classified);
                    Assert.AreEqual(report.Total, report.Unknown);
                    Assert.AreEqual(0, report.NotClassified);
                }
                else
                {
                    Assert.AreEqual(0, report.Classified);
                    Assert.AreEqual(report.Total, report.NotClassified);
                    Assert.AreEqual(0, report.Unknown);
                    Assert.AreEqual(failure == "service-failure" ? 10 : report.Total, report.Failures[failure]);
                    if (failure != "refused") Assert.IsFalse(report.Failures.ContainsKey("refused"));
                }
                Assert.IsFalse(JsonConvert.SerializeObject(report).Contains("private-raw-response"));
            }
        }

        [TestMethod]
        public async Task InvalidOrExceptionPayloadsCannotEscapeReport()
        {
            var backend = new FixtureBackend { Failure = "private-raw-response" };
            var report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(),
                Evaluation.LoadSamples(), 100, CancellationToken.None);
            Assert.IsTrue(report.Failures.ContainsKey("service-failure"));
            Assert.IsFalse(JsonConvert.SerializeObject(report).Contains("private-raw-response"));
            backend = new FixtureBackend { Throws = true };
            report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(),
                Evaluation.LoadSamples(), 100, CancellationToken.None);
            Assert.AreEqual(1, backend.Calls);
            Assert.IsFalse(JsonConvert.SerializeObject(report).Contains("private-raw-response"));
        }

        [TestMethod]
        public async Task SuccessfulFixture_ProvesBatchAccountingNotSemanticAccuracy()
        {
            var backend = new FixtureBackend();
            var samples = Evaluation.LoadSamples();
            var report = await Evaluation.RunAsync(backend, PromptCategoryConfiguration.Defaults(), samples, 100, CancellationToken.None);
            Assert.AreEqual((samples.Count + 9) / 10, backend.Calls);
            Assert.AreEqual(samples.Count, report.Classified);
            Assert.AreEqual(samples.Count, report.Unknown);
            Assert.AreEqual(samples.Count(s => s.Expected == "other"), report.Correct);
            Assert.AreEqual(backend.Calls * 100, report.InputTokens);
            Assert.AreEqual(backend.Calls * 10, report.OutputTokens);
            Assert.IsTrue(report.Decisions.All(d => d.CategoryId == "other"));
            Assert.AreEqual(64, report.DatasetSha256.Length);
        }

        [TestMethod]
        public async Task CancellationAndInvalidBudgetsPreventModelCalls()
        {
            var backend = new FixtureBackend();
            var samples = Evaluation.LoadSamples();
            await Assert.ThrowsExceptionAsync<ArgumentException>(() => Evaluation.RunAsync(backend,
                PromptCategoryConfiguration.Defaults(), samples, 1, CancellationToken.None));
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => Evaluation.RunAsync(backend,
                PromptCategoryConfiguration.Defaults(), samples, 100, new CancellationToken(true)));
            Assert.AreEqual(0, backend.Calls);
        }

        private static Decision D(string expected, string category, string failure = null) =>
            new Decision { Expected = expected, CategoryId = category, Failure = failure };

        private sealed class FixtureBackend : IPromptCategoryBackend
        {
            public int Calls;
            public string Failure;
            public bool Throws;
            public Task<PromptCategoryBatchResult> ClassifyAsync(PromptCategoryConfiguration taxonomy,
                IReadOnlyList<string> prompts, CancellationToken cancellationToken)
            {
                Calls++;
                Assert.IsTrue(prompts.Count >= 1 && prompts.Count <= 10);
                if (Throws) throw new InvalidOperationException("private-raw-response");
                return Task.FromResult(new PromptCategoryBatchResult
                {
                    Failure = Failure, Categories = Enumerable.Repeat("other", prompts.Count).ToArray(),
                    InputTokens = 100, OutputTokens = 10
                });
            }
        }

        private sealed class FixtureHandler : HttpMessageHandler
        {
            public int Calls;
            public string Response = "invalid";
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Calls++;
                var payload = Newtonsoft.Json.Linq.JObject.Parse(await request.Content.ReadAsStringAsync());
                Assert.AreEqual(0, (int)payload["temperature"]);
                Assert.AreEqual(500, (int)payload["max_tokens"]);
                Assert.AreEqual("json_schema", (string)payload["response_format"]["type"]);
                Assert.IsTrue((bool)payload["response_format"]["json_schema"]["strict"]);
                Assert.IsNull(payload["tools"]);
                if (Response == "http-error")
                    return new HttpResponseMessage(HttpStatusCode.BadRequest)
                    {
                        Content = new StringContent("private-raw-response")
                    };
                if (Response == "missing")
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{\"choices\":[]}")
                    };
                if (Response == "malformed" || Response == "missing-message" || Response == "missing-content")
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(Response == "malformed" ? "private-raw-response" :
                            Response == "missing-message" ? "{\"choices\":[{\"finish_reason\":\"stop\"}]}" :
                            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{}}]}")
                    };
                var count = Newtonsoft.Json.Linq.JArray.Parse((string)payload["messages"][1]["content"]).Count;
                var content = Response == "valid"
                    ? JsonConvert.SerializeObject(new { categories = Enumerable.Repeat("other", count).ToArray() })
                    : "private-raw-response";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonConvert.SerializeObject(new
                    {
                        choices = new[] { new { finish_reason = Response == "length" ? "length" : Response == "content-filter" ? "content_filter" :
                            Response == "incomplete" ? "tool_calls" : "stop", message = new
                        {
                            content, refusal = Response == "refusal" ? "private-raw-response" : null
                        } } }
                    }), Encoding.UTF8, "application/json")
                };
            }
        }
    }
}
