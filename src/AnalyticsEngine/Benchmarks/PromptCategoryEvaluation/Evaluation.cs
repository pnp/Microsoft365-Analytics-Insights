using Common.Entities.PromptCategories;
using DataUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public int HttpAttempts { get; set; }
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<CategoryMetric> Categories { get; set; }
        public Dictionary<string, Dictionary<string, int>> Confusion { get; set; }
        public Dictionary<string, int> Failures { get; set; }
        public List<Decision> Decisions { get; set; }
        public string[] Limitations { get; set; }
    }

    public static class Evaluation
    {
        public const string DatasetVersion = "fictional-intents-v1";

        public static string ValidateOutputPath(string path)
        {
            var output = PrivateArtifactPath.Validate(path);
            if (File.Exists(output)) throw new ArgumentException();
            return output;
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
                    Recall = Divide(correct, total), Precision = Divide(correct, predicted)
                };
            }).ToList();
            report.MacroRecall = report.Categories.Where(c => c.Recall.HasValue).Select(c => c.Recall).DefaultIfEmpty(null).Average();
            report.Confusion = report.Categories.ToDictionary(c => c.Id, c =>
                report.Categories.Select(x => x.Id).Concat(new[] { "not-classified" }).ToDictionary(predicted => predicted,
                    predicted => decisions.Count(d => d.Expected == c.Id &&
                        (d.Failure == null ? d.CategoryId : "not-classified") == predicted)));
            report.Failures = decisions.Where(d => d.Failure != null).GroupBy(d => d.Failure)
                .ToDictionary(g => g.Key, g => g.Count());
        }

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
