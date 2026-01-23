using System;
using System.Collections.Concurrent;
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

        public ParallelListProcessor(int maxItemsPerChunk)
        {
            _sem = new SemaphoreSlim(MAX_CONCURRENT_THREADS, MAX_CONCURRENT_THREADS);
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
                
                // Load chunk via delegate and release semaphore when done
                var newTask = processListChunkDelegate(threadListChunk, threadIndex)
                    .ContinueWith(_ => _sem.Release());

                _tasks.Add(newTask);
            }

            // Block for all threads
            await Task.WhenAll(_tasks);
        }
    }
}
