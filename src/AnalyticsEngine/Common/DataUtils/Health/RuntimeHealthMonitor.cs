using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataUtils.Health
{
    public sealed class RuntimeHealthResult
    {
        public RuntimeHealthResult(HealthStatus status, string detail, int? daysToExpiry = null, string reasonKey = null)
        {
            Status = status;
            Detail = detail;
            DaysToExpiry = daysToExpiry;
            ReasonKey = reasonKey;
        }
        public HealthStatus Status { get; }
        public string Detail { get; }
        public int? DaysToExpiry { get; }
        public string ReasonKey { get; }
    }

    /// <summary>
    /// Independent, bounded dependency checks. A timed-out, non-cooperative check is never started
    /// again until it returns; it continues to report failure rather than accumulating background work.
    /// </summary>
    public sealed class RuntimeHealthMonitor : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private readonly object _gate = new object();
        private readonly Task _loop;
        private bool _disposed;

        public RuntimeHealthMonitor(
            IReadOnlyDictionary<HealthComponent, Func<CancellationToken, Task<RuntimeHealthResult>>> checks,
            Action<HealthComponent, RuntimeHealthResult> emit)
            : this(checks, emit, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30))
        {
        }

        internal RuntimeHealthMonitor(
            IReadOnlyDictionary<HealthComponent, Func<CancellationToken, Task<RuntimeHealthResult>>> checks,
            Action<HealthComponent, RuntimeHealthResult> emit, TimeSpan interval, TimeSpan timeout)
        {
            if (checks == null) throw new ArgumentNullException(nameof(checks));
            if (emit == null) throw new ArgumentNullException(nameof(emit));
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
            var probes = checks.Select(c => new Probe(c.Key, c.Value)).ToArray();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        await Task.WhenAll(probes.Select(p => RunProbe(p, emit, timeout))).ConfigureAwait(false);
                        await Task.Delay(interval, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            });
        }

        private async Task RunProbe(Probe probe, Action<HealthComponent, RuntimeHealthResult> emit, TimeSpan timeout)
        {
            RuntimeHealthResult result;
            if (probe.Pending != null && !probe.Pending.IsCompleted)
                result = new RuntimeHealthResult(HealthStatus.Unhealthy, "Health check timed out; no successful result is available.", reasonKey: "defaultHealth.timeout");
            else
            {
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                cancellation.CancelAfter(timeout);
                // Task.Run also bounds a check that blocks before returning its Task.
                probe.Pending = Task.Run(() => probe.Check(cancellation.Token), cancellation.Token);
                var current = probe.Pending;
                _ = current.ContinueWith(t =>
                {
                    var observed = t.Exception;
                    cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                var finished = await Task.WhenAny(current, Task.Delay(timeout, _stop.Token)).ConfigureAwait(false);
                try
                {
                    result = finished == current
                        ? await current.ConfigureAwait(false)
                        : new RuntimeHealthResult(HealthStatus.Unhealthy, "Health check timed out; no successful result is available.", reasonKey: "defaultHealth.timeout");
                    if (result == null) throw new InvalidOperationException();
                }
                catch (Exception)
                {
                    // Never publish exception text: connection strings, identifiers and token errors can
                    // contain customer data. The component and fixed outcome are sufficient to alert.
                    result = new RuntimeHealthResult(HealthStatus.Unhealthy, "Health check failed; inspect the component's operational logs.", reasonKey: "defaultHealth.failed");
                }
            }
            lock (_gate)
            {
                if (_disposed) return;
                try { emit(probe.Component, result); }
                catch (Exception) { Console.Error.WriteLine("Health telemetry failed; import continues."); }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _stop.Cancel();
                _ = _loop.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        private sealed class Probe
        {
            public Probe(HealthComponent component, Func<CancellationToken, Task<RuntimeHealthResult>> check)
            {
                Component = component;
                Check = check ?? throw new ArgumentNullException(nameof(check));
            }
            public HealthComponent Component { get; }
            public Func<CancellationToken, Task<RuntimeHealthResult>> Check { get; }
            public Task<RuntimeHealthResult> Pending { get; set; }
        }
    }
}
