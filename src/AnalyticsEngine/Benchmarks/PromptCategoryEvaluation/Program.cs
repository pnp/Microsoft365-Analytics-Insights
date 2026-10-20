using Common.Entities.Config;
using Common.Entities.PromptCategories;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCategoryEvaluation
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try { return MainAsync(args).GetAwaiter().GetResult(); }
            catch
            {
                // Exception messages may contain environment identity or model output.
                Console.Error.WriteLine("Evaluation failed. Check private configuration, authorization, output path and budgets; no quality evidence was produced.");
                return 2;
            }
        }

        private static async Task<int> MainAsync(string[] args)
        {
            if (args.Length == 1 && args[0] == "--prepare-synthetic-only")
            {
                var plan = Evaluation.Prepare(PromptCategoryConfiguration.Defaults(), Evaluation.LoadSamples(),
                    int.Parse(Environment.GetEnvironmentVariable("PROMPT_EVAL_MAX_PROMPTS") ?? "50"));
                Evaluation.WritePrivateReport(Required("PROMPT_EVAL_OUTPUT"), plan);
                Console.WriteLine("Synthetic evaluation prepared; no credentials read, no model calls and no accuracy evidence produced.");
                return 0;
            }
            if (args.Length != 1 || args[0] != "--authorized-synthetic-only" ||
                Environment.GetEnvironmentVariable("PROMPT_EVAL_AUTHORIZED") != "yes")
                throw new ArgumentException();
            var endpoint = Required("PROMPT_EVAL_ENDPOINT");
            var deployment = Required("PROMPT_EVAL_DEPLOYMENT");
            var model = Required("PROMPT_EVAL_MODEL");
            var version = Required("PROMPT_EVAL_MODEL_VERSION");
            var output = Evaluation.ValidateOutputPath(Required("PROMPT_EVAL_OUTPUT"));
            var key = Environment.GetEnvironmentVariable("PROMPT_EVAL_KEY");
            var token = Environment.GetEnvironmentVariable("PROMPT_EVAL_ACCESS_TOKEN");
            if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(token)) throw new ArgumentException();
            var maxPrompts = int.Parse(Required("PROMPT_EVAL_MAX_PROMPTS"));
            var maxAttempts = int.Parse(Required("PROMPT_EVAL_MAX_HTTP_ATTEMPTS"));
            var taxonomy = PromptCategoryConfiguration.Defaults();
            var samples = Evaluation.LoadSamples();
            Evaluation.Validate(samples, taxonomy, maxPrompts);
            // Backend permits one key-to-Entra fallback; no throttling or outer retries.
            var worstAttempts = 2 * ((samples.Count + 9) / 10);
            if (maxAttempts < worstAttempts || maxAttempts > 200) throw new ArgumentException();
            var budget = EvaluationBudget.FromApproval(Required("PROMPT_EVAL_CURRENCY"),
                Required("PROMPT_EVAL_APPROVED_MAX_COST"), Required("PROMPT_EVAL_MAX_COST_PER_HTTP_ATTEMPT"), worstAttempts);
            var settings = new AppConfig
            {
                FoundryPromptCategorisationEndpoint = endpoint,
                FoundryPromptCategorisationDeployment = deployment,
                FoundryPromptCategorisationKey = key
            };
            if (!FoundryPromptSettings.IsConfigured(settings)) throw new ArgumentException();
            using (var backend = new AzureFoundryPromptCategoryBackend(settings,
                tokenProvider: _ => string.IsNullOrWhiteSpace(token)
                    ? Task.FromException<string>(new InvalidOperationException())
                    : Task.FromResult(token)))
            using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
            {
                var report = await Evaluation.RunAsync(backend, taxonomy, samples, maxPrompts, timeout.Token);
                report.EvidenceKind = report.Classified > 0
                    ? "real-model-decisions-production-backend-synthetic-only"
                    : "service-attempt-without-valid-model-decisions";
                report.Model = new { Name = model, Version = version, DeploymentIdentity = "withheld-private-run-context" };
                report.BudgetApproval = budget;
                report.Parameters = new
                {
                    ApiVersion = "2024-10-21", Temperature = 0, MaxOutputTokensPerRequest = 500,
                    BatchSize = 10, Concurrency = 1, MaxPrompts = maxPrompts, MaxHttpAttempts = maxAttempts,
                    MaximumScheduledRequests = (samples.Count + 9) / 10,
                    MaximumOutputTokenBudget = worstAttempts * 500,
                    DatasetPromptCharacters = samples.Sum(s => s.Prompt.Length),
                    TimeoutSecondsPerBatch = 20, RunTimeoutSeconds = 300,
                    Retries = "No outer retries; production backend allows one key-to-Entra authentication fallback.",
                    SpendLimit = "Request/prompt/output-token bounds plus an operator-approved conservative cost envelope, not an Azure monetary cap. Actual charges depend on deployment pricing."
                };
                report.Limitations = new[]
                {
                    "Small purposive fictional dataset, not tenant traffic, a random sample, a confidence interval or a guarantee.",
                    "Labels curated from taxonomy descriptions before model execution; no training, tuning or synthetic memorizer. Single curator; ambiguous labels are contestable.",
                    "English/Spanish/Greek paraphrases are correlated, not independent observations. Categories, language and adversarial strata are uneven.",
                    "Sample order is frozen and grouped by intent. A circuit-breaker failure can prevent later categories from being sent; inspect per-category coverage and not-sent outcomes.",
                    "Other is the allowed unknown category. Unknown is a valid model decision; not-classified is an operational failure, never a wrong classified answer.",
                    "Overall accuracy includes failures in its denominator; classified accuracy excludes them. Recall and F1 include failures; confusion has a separate not-classified column.",
                    "Language/stratum slices are descriptive, not independent quality estimates. Known-intent unknown rate excludes expected Other labels from its denominator.",
                    "Refusal probes do not require a refusal: a category-only classifier may legitimately classify their intent. Actual refusals are counted separately from semantic errors.",
                    "Explicit refusal, content-filter refusal, output-token exhaustion and malformed/missing response envelopes have distinct failure counters; a truncated answer is not evidence of refusal.",
                    "Invalid model responses are separated as failures here even though the importer conservatively maps invalid category output to other.",
                    "One run at temperature zero is not deterministic across backend revisions. No repeatability, latency-at-load or 200k-user throughput claim.",
                    "Evaluates the production backend/schema, not Graph import, persistence, purge, reporting or historical recategorisation.",
                    "Model name/version are operator supplied out-of-band and must be verified against deployment metadata; no cloud discovery is performed.",
                    "No endpoint, deployment identity, prompts, exception messages or raw model responses are persisted in this report."
                };
                Evaluation.WritePrivateReport(output, report);
                Console.WriteLine($"Synthetic evaluation: {report.Total} samples; {report.Classified} classified; {report.NotClassified} not classified. Private report written.");
                // An operationally incomplete run is evidence of failure, not a passing quality gate.
                return report.NotClassified == 0 ? 0 : 3;
            }
        }

        private static string Required(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
            return value;
        }
    }
}
