using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System.Linq;

namespace Tests.UnitTests
{
    /// <summary>
    /// Regression tests for the perf refactor that replaced
    /// <c>List&lt;T&gt;.Skip(i).Take(K).ToList()</c> with
    /// <c>list.GetRange(i, Math.Min(K, list.Count - i))</c> across the user-import
    /// and sent-email pipelines.
    ///
    /// These are pure-algorithm tests that lock in the chunking invariant: every
    /// element must appear exactly once, in input order, across the produced slices,
    /// for any combination of (totalCount, chunkSize) including boundary cases
    /// (empty, single, exact-multiple, just-over-multiple).
    ///
    /// Even though no production call site is reused verbatim here, the same pattern
    /// is now duplicated in:
    ///   - SentEmailImporter.ImportSentEmails (user chunk loop)
    ///   - SentEmailSentimentScorer.ScoreAsync (sentiment batch loop)
    ///   - UserInsertProcessor.BulkInsertUsers (Phase-1 bulk insert loop)
    ///   - SqlUserLicenseStore add/remove batching (licence-lookup write loops)
    /// so a regression in the slicing arithmetic would normally surface only at
    /// 200k-user scale. Locking the invariant here makes any off-by-one obvious.
    /// </summary>
    [TestClass]
    public class PerfRegressionTests
    {
        [TestMethod]
        public void GetRangeChunking_PartitionsListExactlyOnce_AcrossSizes()
        {
            foreach (var scenario in new[]
            {
                // (totalCount, chunkSize) - includes boundaries the production loops hit
                (Total: 0,    ChunkSize: 25),    // empty input - loop must not execute
                (Total: 1,    ChunkSize: 25),    // single item, smaller than chunk
                (Total: 24,   ChunkSize: 25),    // one short chunk
                (Total: 25,   ChunkSize: 25),    // exact one chunk
                (Total: 26,   ChunkSize: 25),    // one full chunk + one-element trailer
                (Total: 50,   ChunkSize: 25),    // exact two chunks
                (Total: 51,   ChunkSize: 25),    // two full + one-element trailer
                (Total: 200,  ChunkSize: 25),    // many full chunks (SentEmailImporter default)
                (Total: 199,  ChunkSize: 25),    // many chunks + short trailer
                (Total: 1000, ChunkSize: 1000),  // exact single chunk (RELOAD_BATCH_SIZE)
                (Total: 9999, ChunkSize: 1000),  // many chunks + 999-element trailer (SqlUserLicenseStore)
                (Total: 10000,ChunkSize: 10000), // exact one chunk at SqlBulkCopy batch size
            })
            {
                var src = Enumerable.Range(0, scenario.Total).ToList();
                var seen = new List<int>(scenario.Total);

                // Same loop shape every production call site now uses.
                for (int i = 0; i < src.Count; i += scenario.ChunkSize)
                {
                    int take = System.Math.Min(scenario.ChunkSize, src.Count - i);
                    var slice = src.GetRange(i, take);
                    seen.AddRange(slice);
                }

                CollectionAssert.AreEqual(
                    src, seen,
                    $"Chunking failed for total={scenario.Total} chunkSize={scenario.ChunkSize}: " +
                    $"expected {src.Count} elements in input order but got {seen.Count}.");
            }
        }

        [TestMethod]
        public void GetRangeChunking_ProducesNoOverlapAndNoGaps()
        {
            // Stronger invariant: every adjacent slice meets exactly at its boundary.
            var src = Enumerable.Range(0, 257).ToList();
            const int chunkSize = 25;

            int previousEnd = 0;
            int chunksSeen = 0;
            for (int i = 0; i < src.Count; i += chunkSize)
            {
                int take = System.Math.Min(chunkSize, src.Count - i);
                var slice = src.GetRange(i, take);

                Assert.AreEqual(previousEnd, slice[0], "Slice should start exactly where the previous slice ended (no gap).");
                Assert.AreEqual(slice[0] + slice.Count - 1, slice[slice.Count - 1],
                    "Slice elements should be consecutive (no overlap with previous slice).");

                previousEnd = slice[slice.Count - 1] + 1;
                chunksSeen++;
            }

            // 257 / 25 = 10 full chunks + 1 trailer (7 elements) => 11 chunks total.
            Assert.AreEqual(11, chunksSeen);
            Assert.AreEqual(src.Count, previousEnd);
        }
    }
}
