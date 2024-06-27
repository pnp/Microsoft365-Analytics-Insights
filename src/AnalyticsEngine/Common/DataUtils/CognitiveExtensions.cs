using Azure;
using Azure.AI.TextAnalytics;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DataUtils
{
    public class TextAnalysisSample<T> where T : class
    {
        public string Text { get; set; }
        public string Id { get; set; }

        public T Parent { get; set; } = null;
    }

    public class TextAnalysisResult<T> where T : class
    {
        public CognitiveStat CognitiveStat { get; set; }

        public T Parent { get; set; } = null;
    }

    public class CognitiveStat
    {
        public double? SentimentScore { get; set; } = null;
        public string LanguageName { get; set; }
    }

    /// <summary>
    /// Common cognitive services extension methods for text analysis; Teams messages, etc.
    /// </summary>
    public static class CognitiveExtensions
    {
        const int API_MAX_BATCH_SIZE = 10;

        /// <summary>
        /// Loads Azure Cognitive data for a message
        /// </summary>
        public static async Task<List<TextAnalysisResult<T>>> GetCognitiveDataStats<T>(this IEnumerable<TextAnalysisSample<T>> inputData, TextAnalyticsClient client, ILogger telemetry) where T : class
        {
            if (inputData == null || inputData.Count() == 0) return null;

            var validInput = new ConcurrentBag<TextAnalysisSample<T>>();

            // Step 1: detect language msg
            var detectedLanguages = new List<DetectLanguageResult>();
            bool success = false;
            try
            {
                // Break down API calls into chunks of 10 max and compile results
                var listProcessorLang = new ParallelCallsForSingleReturnListHander<TextAnalysisSample<T>, DetectLanguageResult>();
                detectedLanguages = await listProcessorLang.CallAndCompileToSingleList(inputData, async (chunk) =>
                {
                    var languageBatchInput = new List<DetectLanguageInput>();
                    foreach (var d in inputData)
                    {
                        if (d.Id != null && !string.IsNullOrEmpty(d.Text) && d.Parent != null)
                        {
                            validInput.Add(d);

                            // Detect lang
                            languageBatchInput.Add(new DetectLanguageInput(d.Id, d.Text));
                        }
                    }

                    if (languageBatchInput.Count > 0)
                    {
                        try
                        {
                            var result = await client.DetectLanguageBatchAsync(languageBatchInput);
                            return result.Value.ToList();
                        }
                        catch (RequestFailedException ex)
                        {
                            telemetry.LogError(ex, $"Couldn't detect languages: cognitive services error - {ex.Message}");
                            return new List<DetectLanguageResult>();
                        }
                    }
                    else
                    {
                        return new List<DetectLanguageResult>();
                    }
                }, API_MAX_BATCH_SIZE);

                success = true;
            }
            catch (RequestFailedException ex)
            {
                telemetry.LogError(ex, $"Couldn't detect languages: cognitive services error - {ex.Message}");
                return null;
            }
            if (validInput.Count == 0) return null;
            if (success)
            {
                // Remove for now, too much noise
                //telemetry.TrackEvent(DebugTracer.AnalyticsEvent.AzureAIQuery, "DetectLanguage");
            }


            // Step 2: Send msg content to Azure AI with pre-detected language
            // Build a cognitive input batch using language results

            var sentimentBatchInput = new List<TextDocumentInput>();
            foreach (var dataItem in validInput)
            {
                var itemLanguages = detectedLanguages.Where(d => d.Id == dataItem.Id).ToList();

                // Don't bother getting cognitive stats for anything we can't detect language on
                if (itemLanguages.Count > 0)
                {
                    sentimentBatchInput.Add(new TextDocumentInput(dataItem.Id, dataItem.Text));
                }
            }

            var analysisResults = new List<TextAnalysisResult<T>>();

            // Is there anything to analyse? Messages might be empty
            if (sentimentBatchInput.Count > 0)
            {
                bool sentimentSuccess = false;

                var listProcessorSentiment = new ParallelCallsForSingleReturnListHander<TextDocumentInput, AnalyzeSentimentResult>();
                var allAnalyzeSentimentResults = new List<AnalyzeSentimentResult>();
                try
                {
                    // Break down API calls into chunks of 10 max and compile results
                    allAnalyzeSentimentResults = await listProcessorSentiment.CallAndCompileToSingleList(sentimentBatchInput, async (chunk) =>
                    {
                        var result = await client.AnalyzeSentimentBatchAsync(chunk);
                        return result.Value.ToList();
                    }, API_MAX_BATCH_SIZE);

                    sentimentSuccess = true;
                }
                catch (RequestFailedException ex)
                {
                    telemetry.LogError(ex, $"Cognitive services error {ex.Message}");
                    return null;
                }
                if (sentimentSuccess)
                {
                    telemetry.LogInformation($"Sentiment results for chat messages: {allAnalyzeSentimentResults.Count} documents processed");

                    foreach (var sentimentResult in allAnalyzeSentimentResults)
                    {
                        var langResult = detectedLanguages.Where(d => d.Id == sentimentResult.Id).FirstOrDefault();

                        var originalInput = validInput.Where(d => d.Id == sentimentResult.Id).FirstOrDefault();
                        if (originalInput != null)
                        {
                            if (!sentimentResult.HasError && !langResult.HasError)
                            {
                                var overralScore = sentimentResult.DocumentSentiment.Sentiment == TextSentiment.Neutral ? 0.5 :
                                sentimentResult.DocumentSentiment.Sentiment == TextSentiment.Positive ? 1 : 0;

                                var stat = new CognitiveStat { SentimentScore = overralScore, LanguageName = langResult.PrimaryLanguage.Name };

                                analysisResults.Add(new TextAnalysisResult<T> { Parent = originalInput?.Parent, CognitiveStat = stat });
                            }
                            else
                            {
                                telemetry.LogError($"Error in sentiment analysis for message: {originalInput.Text}");
                            }
                        }
                        else
                        {
                            telemetry.LogWarning($"Error in sentiment analysis for message {sentimentResult.Id}. Original message not found.");
                        }
                    }
                }
            }

            return analysisResults;
        }
    }
}
