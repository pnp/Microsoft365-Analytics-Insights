using Common.Entities.PromptCategories;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptCategoryEvaluation
{
    public sealed class Sample
    {
        public string Id { get; set; }
        public string Language { get; set; }
        public string Stratum { get; set; }
        public string Expected { get; set; }
        public string Prompt { get; set; }
    }

    public sealed class Decision
    {
        public string SampleId { get; set; }
        public string Language { get; set; }
        public string Stratum { get; set; }
        public string Expected { get; set; }
        public string CategoryId { get; set; }
        public string Failure { get; set; }
    }

    public sealed class CategoryMetric
    {
        public string Id { get; set; }
        public int Total { get; set; }
        public int Classified { get; set; }
        public int Correct { get; set; }
        public int Predicted { get; set; }
        public double? Recall { get; set; }
        public double? Precision { get; set; }
        public double? F1 { get; set; }
    }

    public sealed class SliceMetric
    {
        public string Id { get; set; }
        public int Total { get; set; }
        public int Classified { get; set; }
        public int Correct { get; set; }
        public int Unknown { get; set; }
        public int NotClassified { get; set; }
        public double? OverallAccuracy { get; set; }
        public double? ClassifiedAccuracy { get; set; }
        public double? UnknownRate { get; set; }
        public double? NotClassifiedRate { get; set; }
    }

    public sealed class EvaluationBudget
    {
        public string Currency { get; set; }
        public decimal ApprovedMaximumCost { get; set; }
        public decimal MaximumEstimatedCostPerHttpAttempt { get; set; }
        public decimal MaximumEstimatedRunCost { get; private set; }
        public string Basis => "Operator-supplied conservative quote per HTTP attempt, including the full input and output allowance. Not an Azure billing cap or verified price.";

        public static EvaluationBudget FromApproval(string currency, string approvedMaximum, string costPerAttempt, int maximumHttpAttempts)
        {
            // Reject locale separators rather than silently reading a Spanish decimal comma as a thousands separator.
            if (!decimal.TryParse(approvedMaximum, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var maximum) ||
                !decimal.TryParse(costPerAttempt, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var perAttempt))
                throw new ArgumentException("Budget amounts require invariant decimal notation without group separators.");
            var budget = new EvaluationBudget
            {
                Currency = currency, ApprovedMaximumCost = maximum, MaximumEstimatedCostPerHttpAttempt = perAttempt
            };
            budget.Validate(maximumHttpAttempts);
            return budget;
        }

        public void Validate(int maximumHttpAttempts)
        {
            if (maximumHttpAttempts < 1 || maximumHttpAttempts > 200 ||
                Currency == null || !System.Text.RegularExpressions.Regex.IsMatch(Currency, @"\A[A-Z]{3}\z") ||
                ApprovedMaximumCost <= 0 || MaximumEstimatedCostPerHttpAttempt <= 0 ||
                MaximumEstimatedCostPerHttpAttempt > ApprovedMaximumCost / maximumHttpAttempts)
                throw new ArgumentException("Explicit currency approval and a sufficient conservative request-cost budget are required.");
            MaximumEstimatedRunCost = MaximumEstimatedCostPerHttpAttempt * maximumHttpAttempts;
        }
    }

    public sealed class EvaluationPlan
    {
        public string EvidenceKind => "prepared-synthetic-evaluation-no-model-calls-no-accuracy-evidence";
        public string DatasetVersion { get; set; }
        public string DatasetSha256 { get; set; }
        public string TaxonomyVersion { get; set; }
        public object Taxonomy { get; set; }
        public int Total { get; set; }
        public int MaximumScheduledRequests { get; set; }
        public int MaximumHttpAttempts { get; set; }
        public int MaximumOutputTokens { get; set; }
        public int PromptCharacters { get; set; }
        public Dictionary<string, int> CategoryCounts { get; set; }
        public Dictionary<string, int> LanguageCounts { get; set; }
        public Dictionary<string, int> StratumCounts { get; set; }
        public string[] RemainingGates { get; set; }
    }

    public sealed class EvaluationReport
    {
        public string EvidenceKind { get; set; }
        public string DatasetVersion { get; set; }
        public string DatasetSha256 { get; set; }
        public string TaxonomyVersion { get; set; }
        public object Taxonomy { get; set; }
        public object Model { get; set; }
        public object Parameters { get; set; }
        public int Total { get; set; }
        public int Classified { get; set; }
        public int Correct { get; set; }
        public int Unknown { get; set; }
        public int NotClassified { get; set; }
        public double? OverallAccuracy { get; set; }
        public double? ClassifiedAccuracy { get; set; }
        public double? UnknownRate { get; set; }
        public double? NotClassifiedRate { get; set; }
        public double? MacroRecall { get; set; }
        public double? MacroF1 { get; set; }
        public double? KnownIntentUnknownRate { get; set; }
        public EvaluationBudget BudgetApproval { get; set; }
        public int HttpAttempts { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<CategoryMetric> Categories { get; set; }
        public List<SliceMetric> Languages { get; set; }
        public List<SliceMetric> Strata { get; set; }
        public Dictionary<string, Dictionary<string, int>> Confusion { get; set; }
        public Dictionary<string, int> Failures { get; set; }
        public List<Decision> Decisions { get; set; }
        public string[] Limitations { get; set; }
    }

    public static class Evaluation
    {
        public const string DatasetVersion = "fictional-intents-v1";

        public static EvaluationPlan Prepare(PromptCategoryConfiguration taxonomy, IReadOnlyList<Sample> samples, int maxPrompts)
        {
            Validate(samples, taxonomy, maxPrompts);
            var requests = (samples.Count + 9) / 10;
            return new EvaluationPlan
            {
                DatasetVersion = DatasetVersion,
                DatasetSha256 = Hash(JsonConvert.SerializeObject(samples)),
                TaxonomyVersion = taxonomy.Version,
                Taxonomy = taxonomy.Categories.Select(c => new { c.Id, c.Description }).ToArray(),
                Total = samples.Count, MaximumScheduledRequests = requests,
                MaximumHttpAttempts = requests * 2, MaximumOutputTokens = requests * 2 * 500,
                PromptCharacters = samples.Sum(s => s.Prompt.Length),
                CategoryCounts = samples.GroupBy(s => s.Expected).ToDictionary(g => g.Key, g => g.Count()),
                LanguageCounts = samples.GroupBy(s => s.Language).ToDictionary(g => g.Key, g => g.Count()),
                StratumCounts = samples.GroupBy(s => s.Stratum).ToDictionary(g => g.Key, g => g.Count()),
                RemainingGates = new[]
                {
                    "An explicitly authorized existing target deployment, verified model/version, credentials and current currency spend approval are required before inference.",
                    "Obtain a conservative per-HTTP-attempt cost quote covering the full payload and 500 output tokens; authentication fallback is included in the attempt bound.",
                    "No model calls or semantic quality measurements have occurred in preparation mode. Do not publish this plan as accuracy evidence.",
                    "Freeze labels before inference. This small purposive single-curator corpus is not tenant-representative; translations are correlated and strata are uneven.",
                    "Sample order is frozen and grouped by intent. Circuit-breaking can prevent later categories from being sent; inspect category coverage and not-sent outcomes.",
                    "Review real-model unknown/error rates, per-category precision/recall/F1, language/stratum slices and confusion before making release-quality claims."
                }
            };
        }

        public static string ValidateOutputPath(string path)
        {
            var output = PrivateArtifactPath.Validate(path);
            if (File.Exists(output)) throw new ArgumentException();
            return output;
        }

        public static void WritePrivateReport(string output, object report)
        {
            using (var file = new FileStream(ValidateOutputPath(output), FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(file))
                writer.Write(JsonConvert.SerializeObject(report, Formatting.Indented));
        }

        public static List<Sample> LoadSamples()
        {
            using (var stream = typeof(Evaluation).Assembly.GetManifestResourceStream("PromptCategoryEvaluation.synthetic-v1.json"))
            using (var reader = new System.IO.StreamReader(stream))
                return JsonConvert.DeserializeObject<List<Sample>>(reader.ReadToEnd());
        }

        public static void Validate(IReadOnlyList<Sample> samples, PromptCategoryConfiguration taxonomy, int maxPrompts)
        {
            taxonomy.ValidateAndVersion();
            var allowed = new HashSet<string>(taxonomy.Categories.Select(c => c.Id), StringComparer.Ordinal);
            if (maxPrompts < 1 || maxPrompts > 1000 || samples.Count == 0 || samples.Count > maxPrompts ||
                samples.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != samples.Count ||
                samples.Any(s => string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Language) ||
                    string.IsNullOrWhiteSpace(s.Stratum) || string.IsNullOrWhiteSpace(s.Prompt) ||
                    s.Prompt.Length > 5000 || !allowed.Contains(s.Expected)) ||
                allowed.Any(id => !samples.Any(s => s.Expected == id)))
                throw new ArgumentException("Invalid or incomplete labelled dataset, or insufficient prompt budget.");
        }

        public static async Task<EvaluationReport> RunAsync(IPromptCategoryBackend backend,
            PromptCategoryConfiguration taxonomy, IReadOnlyList<Sample> samples, int maxPrompts,
            CancellationToken cancellationToken)
        {
            Validate(samples, taxonomy, maxPrompts);
            var report = new EvaluationReport
            {
                DatasetVersion = Evaluation.DatasetVersion,
                DatasetSha256 = Hash(JsonConvert.SerializeObject(samples)),
                TaxonomyVersion = taxonomy.Version,
                Taxonomy = taxonomy.Categories.Select(c => new { c.Id, c.Description }).ToArray(),
                Decisions = new List<Decision>()
            };
            var watch = Stopwatch.StartNew();
            var allowed = new HashSet<string>(taxonomy.Categories.Select(c => c.Id), StringComparer.Ordinal);
            string halted = null;
            for (var offset = 0; offset < samples.Count; offset += 10)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = new Sample[Math.Min(10, samples.Count - offset)];
                for (var i = 0; i < batch.Length; i++) batch[i] = samples[offset + i];
                PromptCategoryBatchResult result;
                if (halted != null)
                    result = new PromptCategoryBatchResult { Failure = "not-sent", Attempts = 0 };
                else
                {
                    try
                    {
                        result = await backend.ClassifyAsync(taxonomy, batch.Select(s => s.Prompt).ToArray(), cancellationToken)
                            ?? new PromptCategoryBatchResult { Failure = "invalid-response", Attempts = 0 };
                    }
                    catch (OperationCanceledException) { result = new PromptCategoryBatchResult { Failure = "timeout", Attempts = 0 }; }
                    catch { result = new PromptCategoryBatchResult { Failure = "service-failure", Attempts = 0 }; }
                }
                var failure = ResponseFailure(result);
                if (failure == null && (result.ResponseWasInvalid || result.Categories?.Count != batch.Length ||
                    result.Categories.Any(id => id == null || !allowed.Contains(id))))
                    failure = "invalid-response";
                // Match the importer's circuit breaker; no new retries or cost-amplifying recovery.
                if (failure == "throttled" || failure == "access-denied" || failure == "timeout" || failure == "service-failure")
                    halted = failure;
                report.HttpAttempts += Math.Max(0, result.Attempts);
                report.InputTokens += Math.Max(0, result.InputTokens);
                report.OutputTokens += Math.Max(0, result.OutputTokens);
                for (var i = 0; i < batch.Length; i++)
                    report.Decisions.Add(new Decision
                    {
                        SampleId = batch[i].Id, Language = batch[i].Language, Stratum = batch[i].Stratum,
                        Expected = batch[i].Expected, CategoryId = failure == null ? result.Categories[i] : null,
                        Failure = failure
                    });
            }
            report.ElapsedMilliseconds = watch.ElapsedMilliseconds;
            Calculate(report, taxonomy.Categories.Select(c => c.Id));
            return report;
        }

        public static void Calculate(EvaluationReport report, IEnumerable<string> ids)
        {
            var decisions = report.Decisions;
            report.Total = decisions.Count;
            report.Classified = decisions.Count(d => d.Failure == null);
            report.Correct = decisions.Count(d => d.Failure == null && d.Expected == d.CategoryId);
            report.Unknown = decisions.Count(d => d.Failure == null && d.CategoryId == "other");
            report.NotClassified = report.Total - report.Classified;
            report.OverallAccuracy = Divide(report.Correct, report.Total);
            report.ClassifiedAccuracy = Divide(report.Correct, report.Classified);
            report.UnknownRate = Divide(report.Unknown, report.Total);
            report.NotClassifiedRate = Divide(report.NotClassified, report.Total);
            report.Categories = ids.Select(id =>
            {
                var total = decisions.Count(d => d.Expected == id);
                var correct = decisions.Count(d => d.Failure == null && d.Expected == id && d.CategoryId == id);
                var predicted = decisions.Count(d => d.Failure == null && d.CategoryId == id);
                return new CategoryMetric
                {
                    Id = id, Total = total, Correct = correct, Predicted = predicted,
                    Classified = decisions.Count(d => d.Expected == id && d.Failure == null),
                    Recall = Divide(correct, total), Precision = Divide(correct, predicted),
                    F1 = Divide(2 * correct, total + predicted)
                };
            }).ToList();
            report.MacroRecall = report.Categories.Where(c => c.Recall.HasValue).Select(c => c.Recall).DefaultIfEmpty(null).Average();
            report.MacroF1 = report.Categories.Select(c => c.F1).DefaultIfEmpty(null).Average();
            report.KnownIntentUnknownRate = Divide(
                decisions.Count(d => d.Expected != "other" && d.Failure == null && d.CategoryId == "other"),
                decisions.Count(d => d.Expected != "other"));
            report.Languages = Slices(decisions, d => d.Language);
            report.Strata = Slices(decisions, d => d.Stratum);
            report.Confusion = report.Categories.ToDictionary(c => c.Id, c =>
                report.Categories.Select(x => x.Id).Concat(new[] { "not-classified" }).ToDictionary(predicted => predicted,
                    predicted => decisions.Count(d => d.Expected == c.Id &&
                        (d.Failure == null ? d.CategoryId : "not-classified") == predicted)));
            report.Failures = decisions.Where(d => d.Failure != null).GroupBy(d => d.Failure)
                .ToDictionary(g => g.Key, g => g.Count());
        }

        private static List<SliceMetric> Slices(IEnumerable<Decision> decisions, Func<Decision, string> key) =>
            decisions.GroupBy(d => key(d) ?? "unspecified").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var total = g.Count();
                var classified = g.Count(d => d.Failure == null);
                var correct = g.Count(d => d.Failure == null && d.Expected == d.CategoryId);
                var unknown = g.Count(d => d.Failure == null && d.CategoryId == "other");
                return new SliceMetric
                {
                    Id = g.Key, Total = total, Classified = classified, Correct = correct,
                    Unknown = unknown, NotClassified = total - classified,
                    OverallAccuracy = Divide(correct, total), ClassifiedAccuracy = Divide(correct, classified),
                    UnknownRate = Divide(unknown, total), NotClassifiedRate = Divide(total - classified, total)
                };
            }).ToList();

        private static string SafeFailure(string failure)
        {
            if (failure == null) return null;
            return new[] { "throttled", "access-denied", "service-failure", "timeout", "refused", "invalid-response", "not-sent" }
                .Contains(failure, StringComparer.Ordinal) ? failure : "service-failure";
        }
        private static string ResponseFailure(PromptCategoryBatchResult result)
        {
            switch (result.ResponseFailure)
            {
                case PromptCategoryResponseFailure.Refused: return "refused";
                case PromptCategoryResponseFailure.ContentFiltered: return "content-filtered";
                case PromptCategoryResponseFailure.OutputLimit: return "output-limit";
                case PromptCategoryResponseFailure.InvalidEnvelope: return "invalid-envelope";
                case PromptCategoryResponseFailure.IncompleteResponse: return "incomplete-response";
                default: return SafeFailure(result.Failure);
            }
        }
        private static double? Divide(int numerator, int denominator) => denominator == 0 ? (double?)null : (double)numerator / denominator;
        private static string Hash(string text)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
    }
}
