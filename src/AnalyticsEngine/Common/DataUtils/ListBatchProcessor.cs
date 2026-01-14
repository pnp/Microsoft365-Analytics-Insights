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
        private readonly SemaphoreSlim _batchSemaphore = new SemaphoreSlim(1, 1);

        private List<T> _buffer;

        public ListBatchProcessor(int batchSize, Func<List<T>, Task> batchDone)
        {
            _batchDone = batchDone;
            _batchSize = batchSize;
            _buffer = new List<T>();
        }
        public async Task Add(T i)
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

        }
        public async Task AddRange(IEnumerable<T> source)
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
        }
        async Task BatchCheck()
        {
            while (_buffer.Count >= _batchSize)
            {
                var batch = _buffer.Take(_batchSize).ToList();
                _buffer.RemoveRange(0, _batchSize);
                await _batchDone(batch);
            }
        }

        public async Task Flush()
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
