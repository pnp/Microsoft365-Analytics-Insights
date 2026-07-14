using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils
{
    /// <summary>
    /// Process list items into batches. 
    /// </summary>
    /// <typeparam name="T">Type of list item</typeparam>
    public class ListBatchProcessor<T>
    {
        private readonly Func<List<T>, Task> _batchDone;
        private readonly int _batchSize;
        private readonly int _maxConcurrentBatches;
        private readonly SemaphoreSlim _batchSemaphore = new SemaphoreSlim(1, 1);

        // Only used when _maxConcurrentBatches > 1: bounds how many _batchDone callbacks run at once and
        // tracks the in-flight tasks so Flush can await them (and so any failure is surfaced).
        private readonly SemaphoreSlim _dispatchGate;
        private readonly List<Task> _inFlight;
        private readonly object _inFlightLock = new object();

        private List<T> _buffer;

        public ListBatchProcessor(int batchSize, Func<List<T>, Task> batchDone)
            : this(batchSize, batchDone, 1)
        {
        }

        /// <summary>
        /// <paramref name="maxConcurrentBatches"/> = 1 (default) preserves the original strictly-serial
        /// behaviour: each completed batch's callback runs to completion while the buffer lock is held.
        /// A value &gt; 1 dispatches completed batches to up to that many concurrent callbacks (the buffer
        /// lock is released while they run), so producers keep filling the buffer while batches commit in
        /// parallel. Callers whose callback writes shared state must make that safe themselves.
        /// </summary>
        public ListBatchProcessor(int batchSize, Func<List<T>, Task> batchDone, int maxConcurrentBatches)
        {
            _batchDone = batchDone;
            _batchSize = batchSize;
            _maxConcurrentBatches = Math.Max(1, maxConcurrentBatches);
            _buffer = new List<T>();
            if (_maxConcurrentBatches > 1)
            {
                _dispatchGate = new SemaphoreSlim(_maxConcurrentBatches, _maxConcurrentBatches);
                _inFlight = new List<Task>();
            }
        }

        public async Task Add(T i)
        {
            if (_maxConcurrentBatches == 1)
            {
                await _batchSemaphore.WaitAsync();
                try
                {
                    _buffer.Add(i);
                    await BatchCheck();
                }
                finally
                {
                    _batchSemaphore.Release();
                }
                return;
            }

            List<List<T>> ready;
            await _batchSemaphore.WaitAsync();
            try
            {
                _buffer.Add(i);
                ready = ExtractFullBatches();
            }
            finally
            {
                _batchSemaphore.Release();
            }
            await DispatchAll(ready);
        }

        public async Task AddRange(IEnumerable<T> source)
        {
            if (_maxConcurrentBatches == 1)
            {
                await _batchSemaphore.WaitAsync();
                try
                {
                    _buffer.AddRange(source);
                    await BatchCheck();
                }
                finally
                {
                    _batchSemaphore.Release();
                }
                return;
            }

            List<List<T>> ready;
            await _batchSemaphore.WaitAsync();
            try
            {
                _buffer.AddRange(source);
                ready = ExtractFullBatches();
            }
            finally
            {
                _batchSemaphore.Release();
            }
            await DispatchAll(ready);
        }

        // Serial path (maxConcurrentBatches == 1): callback runs under the buffer lock, exactly as before.
        async Task BatchCheck()
        {
            while (_buffer.Count >= _batchSize)
            {
                var batch = _buffer.Take(_batchSize).ToList();
                _buffer.RemoveRange(0, _batchSize);
                await _batchDone(batch);
            }
        }

        // Concurrent path: pull all currently-full batches out under the buffer lock (caller holds it).
        private List<List<T>> ExtractFullBatches()
        {
            List<List<T>> ready = null;
            while (_buffer.Count >= _batchSize)
            {
                var batch = _buffer.GetRange(0, _batchSize);
                _buffer.RemoveRange(0, _batchSize);
                (ready ?? (ready = new List<List<T>>())).Add(batch);
            }
            return ready;
        }

        private async Task DispatchAll(List<List<T>> batches)
        {
            if (batches == null) return;
            foreach (var batch in batches) await Dispatch(batch);
        }

        private async Task Dispatch(List<T> batch)
        {
            // Surface any earlier failure promptly instead of only at Flush.
            ThrowIfAnyFaulted();

            await _dispatchGate.WaitAsync();
            var task = Task.Run(async () =>
            {
                try { await _batchDone(batch); }
                finally { _dispatchGate.Release(); }
            });
            lock (_inFlightLock)
            {
                _inFlight.Add(task);
                _inFlight.RemoveAll(t => t.IsCompleted && !t.IsFaulted && !t.IsCanceled);
            }
        }

        private void ThrowIfAnyFaulted()
        {
            Task faulted;
            lock (_inFlightLock)
            {
                faulted = _inFlight.FirstOrDefault(t => t.IsFaulted);
            }
            if (faulted != null) faulted.GetAwaiter().GetResult(); // rethrows
        }

        public async Task Flush()
        {
            if (_maxConcurrentBatches == 1)
            {
                await _batchSemaphore.WaitAsync();
                try
                {
                    if (_buffer.Count > 0)
                    {
                        var finalBuffer = _buffer;
                        _buffer = new List<T>();
                        await _batchDone(finalBuffer);
                    }
                }
                finally
                {
                    _batchSemaphore.Release();
                }
                return;
            }

            List<T> final = null;
            await _batchSemaphore.WaitAsync();
            try
            {
                if (_buffer.Count > 0)
                {
                    final = _buffer;
                    _buffer = new List<T>();
                }
            }
            finally
            {
                _batchSemaphore.Release();
            }
            if (final != null) await Dispatch(final);

            Task[] all;
            lock (_inFlightLock)
            {
                all = _inFlight.ToArray();
            }
            await Task.WhenAll(all);
        }

        public int BufferSize
        {
            get
            {
                lock (this)
                    return _buffer.Count;
            }
        }
    }
}
