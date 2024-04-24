using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Common.DataUtils
{
    /// <summary>
    /// When you have a big list of items to process in parallel chunks, this class can help.
    /// Returns just one list for multiple parallel calls.
    /// </summary>
    public class ParallelCallsForSingleReturnListHander<TYPE_IN, TYPE_OUT>
    {
        public async Task<List<TYPE_OUT>> CallAndCompileToSingleList(IEnumerable<TYPE_IN> allItemsIn, Func<List<TYPE_IN>, Task<List<TYPE_OUT>>> processListChunkDelegate, int maxItemsPerChunk)
        {
            if (allItemsIn is null)
            {
                throw new ArgumentNullException(nameof(allItemsIn));
            }

            if (processListChunkDelegate is null)
            {
                throw new ArgumentNullException(nameof(processListChunkDelegate));
            }

            if (maxItemsPerChunk < 1)
            {
                throw new ArgumentException(nameof(maxItemsPerChunk));
            }

            // Start processing
            var plp = new ParallelListProcessor<TYPE_IN>(maxItemsPerChunk);

            var allItemsOut = new ConcurrentBag<TYPE_OUT>();
            await plp.ProcessListInParallel(allItemsIn, async (threadListChunk, threadIndex) =>
            {
                var result = await processListChunkDelegate(threadListChunk);
                foreach (var item in result)
                {
                    allItemsOut.Add(item);
                }
            });

            return allItemsOut.ToList();
        }
    }
}
