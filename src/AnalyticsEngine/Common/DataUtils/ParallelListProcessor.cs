using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils
{
    /// <summary>
    /// Process a big list of objects in parallel, via configurable chunk sizes.
    /// </summary>
    /// <typeparam name="T">Type of list object</typeparam>
    public class ParallelListProcessor<T>
    {
        private readonly int _maxItemsPerChunk;
        private readonly SemaphoreSlim _sem = null;
        const int MAX_CONCURRENT_THREADS = 20;

        private readonly List<Task> _tasks = new List<Task>();

        /// <param name="maxItemsPerChunk">How many items each parallel chunk processes.</param>
        /// <param name="maxConcurrentThreads">
        /// Max simultaneous chunk tasks. Defaults to <see cref="MAX_CONCURRENT_THREADS"/> (20) to
        /// preserve existing callers. Lower values reduce peak CPU; values &lt; 1 fall back to 1.
        /// </param>
        public ParallelListProcessor(int maxItemsPerChunk, int maxConcurrentThreads = MAX_CONCURRENT_THREADS)
        {
            var threadCap = maxConcurrentThreads < 1 ? 1 : maxConcurrentThreads;
            _sem = new SemaphoreSlim(threadCap, threadCap);
            if (maxItemsPerChunk < 1)
            {
                throw new ArgumentException(nameof(maxItemsPerChunk));
            }
            _maxItemsPerChunk = maxItemsPerChunk;
        }

        /// <summary>
        /// From a complete list, load in parallel chunks. Blocks until all tasks are complete.
        /// </summary>
        /// <param name="processListChunkDelegate">Function delegate for processing a chunk of all items + thread index. Must return Task</param>
        public async Task ProcessListInParallel(IEnumerable<T> allItems, Func<List<T>, int, Task> processListChunkDelegate)
        {
            await ProcessListInParallel(allItems, processListChunkDelegate, null);
        }

        /// <summary>
        /// From a complete list, load in parallel chunks. Blocks until all tasks are complete.
        /// </summary>
        /// <param name="processListChunkDelegate">Function delegate for processing a chunk of all items + thread index. Must return Task</param>
        public async Task ProcessListInParallel(IEnumerable<T> allItems, Func<List<T>, int, Task> processListChunkDelegate, Action<int> startingDelegate)
        {
            if (allItems is null)
            {
                throw new ArgumentNullException(nameof(allItems));
            }

            if (processListChunkDelegate is null)
            {
                throw new ArgumentNullException(nameof(processListChunkDelegate));
            }

            // Materialize the enumerable once to avoid repeated enumeration and prevent OOM from sorting
            var itemsList = allItems as List<T> ?? allItems.ToList();
            var totalCount = itemsList.Count;

            // Figure out how many threads we'll need
            int rem = 0;
            var threadsNeeded = Math.DivRem(totalCount, _maxItemsPerChunk, out rem);
            if (rem > 0)
            {
                threadsNeeded++;        // Make sure the last thread doesn't include diving remainder
            }

            var recordsInsertedAlready = 0;
            if (startingDelegate != null)
            {
                startingDelegate(threadsNeeded);
            }

            for (int threadIndex = 0; threadIndex < threadsNeeded; threadIndex++)
            {
                // Figure out next threaded chunk
                var recordsToTake = _maxItemsPerChunk;
                if (threadIndex == threadsNeeded - 1)
                {
                    recordsToTake = totalCount - recordsInsertedAlready;
                }

                // Split unique work for new thread using GetRange for better performance
                List<T> threadListChunk;
                if (recordsInsertedAlready + recordsToTake <= itemsList.Count)
                {
                    threadListChunk = itemsList.GetRange(recordsInsertedAlready, recordsToTake);
                }
                else
                {
                    // Fallback for edge cases
                    threadListChunk = itemsList.Skip(recordsInsertedAlready).Take(recordsToTake).ToList();
                }
                recordsInsertedAlready += recordsToTake;

                // Throttle threads to max
                await _sem.WaitAsync();

                // Track the actual WORK task (not a fire-and-forget release continuation) so any
                // exception a chunk throws is surfaced by the Task.WhenAll below. The previous
                // ContinueWith(_ => _sem.Release()) added the always-succeeding continuation to
                // _tasks instead of the work, so every chunk exception was silently swallowed and
                // callers (and operators) never found out a chunk had broken.
                _tasks.Add(RunChunkAsync(processListChunkDelegate, threadListChunk, threadIndex));
            }

            // Block for all threads. A faulted chunk re-throws here (unwrapped, so callers' typed
            // catches still work) instead of being lost.
            await Task.WhenAll(_tasks);
        }

        /// <summary>
        /// Runs a single chunk and always releases its throttle slot afterwards (success or failure).
        /// Exceptions are deliberately allowed to propagate so the chunk's task faults and the awaiting
        /// <see cref="Task.WhenAll(Task[])"/> re-throws them - the previous implementation released the
        /// semaphore in a fire-and-forget ContinueWith and tracked that (always-successful) continuation
        /// rather than the work, which silently swallowed every chunk exception.
        /// </summary>
        private async Task RunChunkAsync(Func<List<T>, int, Task> processListChunkDelegate, List<T> chunk, int threadIndex)
        {
            try
            {
                await processListChunkDelegate(chunk, threadIndex);
            }
            finally
            {
                _sem.Release();
            }
        }
    }
}
