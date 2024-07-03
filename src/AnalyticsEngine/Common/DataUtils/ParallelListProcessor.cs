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
        private int _completedTaskCount = 0;
        private bool _running = false;

        private readonly ConcurrentBag<Task> _tasks = new ConcurrentBag<Task>();

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

            // Figure out how many threads we'll need
            int rem = 0;
            var threadsNeeded = Math.DivRem(allItems.Count(), _maxItemsPerChunk, out rem);
            if (rem > 0)
            {
                threadsNeeded++;        // Make sure the last thread doesn't include diving remainder
            }

            Task unlockThreadLimitTask = null;

            var recordsInsertedAlready = 0;
            if (startingDelegate != null)
            {
                startingDelegate(threadsNeeded);
            }

            _running = true;

            for (int threadIndex = 0; threadIndex < threadsNeeded; threadIndex++)
            {
                // Figure out next threaded chunk
                var recordsToTake = _maxItemsPerChunk;
                if (threadIndex == threadsNeeded - 1)
                {
                    recordsToTake = allItems.Count() - recordsInsertedAlready;
                }

                // Split unique work for new thread
                var threadListChunk = allItems.Skip(recordsInsertedAlready).Take(recordsToTake).ToList();
                recordsInsertedAlready += recordsToTake;

                // Throttle threads to max
                _sem.Wait();
                lock (this)
                {
                    // Load chunk via delegate
                    var newTask = processListChunkDelegate(threadListChunk, threadIndex);

                    _tasks.Add(newTask);
                }

                // Start unlock check
                if (unlockThreadLimitTask == null)
                {
                    unlockThreadLimitTask = Task.Factory.StartNew(() => UnlockThreads());
                }
            }

            lock (this)
            {
                _running = false;
            }

            // Block for all threads
            await Task.WhenAll(_tasks);
        }

        void UnlockThreads()
        {
            // Loop, unthrottling tasks as they finish
            var keepRunning = true;
            lock (this)
            {
                keepRunning = _running;
            }

            while (keepRunning)
            {
                if (_tasks.Count > 0)
                {
                    lock (this)
                    {
                        // Remove # of threads finished from semaphore, or only max semaphore count if that's less.
                        var finishedTaskCountAll = _tasks.Where(t => t.IsCompleted).Count();
                        var newTasksFinishedCount = finishedTaskCountAll - _completedTaskCount;

                        if (newTasksFinishedCount > 0)
                        {
                            _sem.Release(newTasksFinishedCount);
                        }

                        _completedTaskCount = finishedTaskCountAll;
                    }
                }

                Thread.Sleep(10);
                lock (this)
                {
                    keepRunning = _running;
                }
            }
            // Finished adding work
        }
    }
}
