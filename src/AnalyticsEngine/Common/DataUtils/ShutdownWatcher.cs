using System;
using System.IO;
using System.Threading;

namespace DataUtils
{
    /// <summary>
    /// Notices that Kudu is stopping the WebJob. Kudu creates the file named by <c>WEBJOBS_SHUTDOWN_FILE</c> and kills
    /// the process when its stop wait (5 seconds unless <c>settings.job</c> says otherwise) runs out, so a host that
    /// ignores the file loses whatever it had not yet sent. A <see cref="FileSystemWatcher"/> sees the file at once and
    /// a short poll covers a watcher that misses it (a network share, a dropped event).
    /// </summary>
    public sealed class ShutdownWatcher : IDisposable
    {
        public const string ShutdownFileVariable = "WEBJOBS_SHUTDOWN_FILE";

        private readonly string _path;
        private readonly Action _onRequested;
        private readonly ManualResetEventSlim _requested = new ManualResetEventSlim(false);
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _poll;
        private int _raised;

        /// <param name="path">The shutdown file. Null or empty (a run outside Kudu) means shutdown is never requested by a file.</param>
        /// <param name="pollInterval">How often to look for the file as well as watching for it.</param>
        /// <param name="onRequested">Runs once, on a thread-pool thread, when the file is first seen. Must not throw.</param>
        public ShutdownWatcher(string path, TimeSpan pollInterval, Action onRequested = null)
        {
            _path = path;
            _onRequested = onRequested;
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
                    {
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    };
                    _watcher.Created += (s, e) => Check();
                    _watcher.Changed += (s, e) => Check();
                    _watcher.Renamed += (s, e) => Check();
                    _watcher.EnableRaisingEvents = true;
                }
            }
            catch (Exception)
            {
                // Polling alone still works.
            }

            _poll = new Timer(_ => Check(), null, TimeSpan.Zero, pollInterval < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : pollInterval);
        }

        /// <summary>A watcher on the file Kudu names in <c>WEBJOBS_SHUTDOWN_FILE</c>, polling every second.</summary>
        public static ShutdownWatcher ForEnvironment(Action onRequested = null) =>
            new ShutdownWatcher(Environment.GetEnvironmentVariable(ShutdownFileVariable), TimeSpan.FromSeconds(1), onRequested);

        public bool IsRequested => _requested.IsSet;

        /// <summary>Sleeps for up to <paramref name="timeout"/>, waking early when shutdown is requested.</summary>
        /// <returns>True when shutdown was requested (before or during the wait).</returns>
        public bool Wait(TimeSpan timeout) => _requested.Wait(timeout);

        /// <summary>Re-reads the file; called by the watcher and the poll, and by tests.</summary>
        internal void Check()
        {
            try
            {
                if (_path == null || !File.Exists(_path)) return;
            }
            catch (Exception)
            {
                return;
            }

            if (Interlocked.Exchange(ref _raised, 1) != 0) return;
            _requested.Set();
            try
            {
                _onRequested?.Invoke();
            }
            catch (Exception)
            {
                // Telemetry about the stop must never stop the stop.
            }
        }

        public void Dispose()
        {
            _poll?.Dispose();
            _watcher?.Dispose();
        }
    }
}
