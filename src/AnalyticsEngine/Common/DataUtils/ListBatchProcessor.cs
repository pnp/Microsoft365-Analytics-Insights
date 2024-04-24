using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Common.DataUtils
{
    /// <summary>
    /// Process list items into batches. 
    /// </summary>
    /// <typeparam name="T">Type of list item</typeparam>
    public class ListBatchProcessor<T>
    {
        private readonly Func<List<T>, Task> _batchDone;
        private readonly int _batchSize;

        private List<T> _buffer;

        public ListBatchProcessor(int batchSize, Func<List<T>, Task> batchDone)
        {
            _batchDone = batchDone;
            _batchSize = batchSize;
            _buffer = new List<T>();
        }
        public void Add(T i)
        {
            lock (this)
            {
                _buffer.Add(i);
                BatchCheck();
            }

        }
        public void AddRange(IEnumerable<T> source)
        {
            lock (this)
            {
                _buffer.AddRange(source);
                BatchCheck();
            }
        }
        void BatchCheck()
        {
            lock (this)
            {
                while (_buffer.Count >= _batchSize)
                {
                    _batchDone(_buffer.Take(_batchSize).ToList()).Wait();
                    _buffer.RemoveRange(0, _batchSize);
                }
            }
        }

        public void Flush()
        {
            lock (this)
            {
                if (_buffer.Count > 0)
                {
                    _batchDone(_buffer).Wait();
                    _buffer.Clear();
                }
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
