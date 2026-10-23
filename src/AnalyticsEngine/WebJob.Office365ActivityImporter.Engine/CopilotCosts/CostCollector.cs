using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebJob.Office365ActivityImporter.Engine.CopilotCosts
{
    public sealed class CostCollector
    {
        public static readonly HashSet<string> Outcomes = new HashSet<string>(StringComparer.Ordinal)
        {
            "observed", "partial", "complete", "inventory", "context", "busy", "throttled",
            "sourceUnavailable", "sourceFailure", "invalidResponse", "invalidPage", "permissionDenied",
            "notFound", "reconnectRequired", "disconnected", "identityUnavailable", "databaseUnavailable",
            "storageUnavailable", "storageCorrupt", "accountMismatch", "sliceCompleted", "sliceTimedOut", "idle"
        };
        private readonly SqlCostCollectorStore _store;
        private readonly CostConnectionService _connection;
        private readonly HttpClient _http;
        private readonly Func<CancellationToken, Task<string>> _directoryToken;

        public CostCollector(SqlCostCollectorStore store, CostConnectionService connection, HttpClient http,
            Func<CancellationToken, Task<string>> directoryToken)
        { _store = store; _connection = connection; _http = http; _directoryToken = directoryToken; }

        public async Task<string> RunSliceAsync(CancellationToken ct)
        {
            var outcome = "sliceCompleted";
            await _connection.RunWithTokenAsync(async (state, token, cancellation) =>
            {
                Func<CancellationToken, Task> guard = c => _connection.AssertOwnershipAsync(state, c);
                var run = await _store.AcquireRunAsync(state.Generation, DateTimeOffset.UtcNow, cancellation, guard);
                if (run == null) { outcome = "idle"; return; }
                try
                {
                    var source = new CostGraphSource(_http, token, assertAccess: guard);
                    if (!run.InventoryComplete)
                    {
                        // Complete, unfiltered application-identity directory inventory MUST finish
                        // before user reads. No licence, active-user, group or importer filter applies.
                        var directory = new CostGraphSource(_http, await _directoryToken(cancellation), assertAccess: guard);
                        for (var page = 0; page < 10 && !run.InventoryComplete; page++)
                        {
                            await guard(cancellation);
                            var result = await directory.ReadDirectoryPageAsync(run.NextLink, cancellation);
                            await _store.SaveInventoryPageAsync(run, result.Item1, result.Item2, cancellation);
                            run.NextLink = result.Item2;
                            run.Pages++;
                            run.InventoryComplete = result.Item2 == null;
                        }
                        return;
                    }
                    var cursor = await _store.ReadContextCursorAsync(run, cancellation);
                    if (cursor.Item1 < 2)
                    {
                        var task = await _store.ReadContextTaskAsync(run, cancellation);
                        if (task == null) throw new CostConnectionException("invalidPage");
                        var page = await source.ReadContextPageAsync(task, cancellation);
                        await _store.SaveContextAsync(run, task, page, guard, cancellation);
                        return;
                    }
                    var work = await _store.ReadWorkAsync(run, DateTimeOffset.UtcNow, cancellation);
                    foreach (var item in work)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        try
                        {
                            var observation = await source.ReadUserAsync(item.UserId.ToString(), cancellation);
                            await _store.PublishAsync(run, observation, guard, cancellation);
                        }
                        catch (CostConnectionException ex)
                        {
                            outcome = ex.Status;
                            if (ex.Status == "busy" || ex.Status == "databaseUnavailable" || ex.Status == "storageUnavailable") throw;
                            await _store.RecordFailureAsync(run, item, ex.Status, DateTimeOffset.UtcNow, cancellation, ex.RetryAfter);
                            // Permission/connection failures are not permission to skip the population.
                            // Keep work pending and stop hammering a globally denied source.
                            if (ex.Status == "reconnectRequired" || ex.Status == "permissionDenied") throw;
                            if (ex.Status == "throttled") break;
                        }
                        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
                        {
                            outcome = "sourceUnavailable";
                            await _store.RecordFailureAsync(run, item, outcome, DateTimeOffset.UtcNow, cancellation);
                        }
                        catch (OperationCanceledException)
                        {
                            // A slow last user must not monopolise the next slice indefinitely.
                            using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                                await _store.RecordFailureAsync(run, item, "sliceTimedOut", DateTimeOffset.UtcNow, cleanup.Token);
                            throw;
                        }
                    }
                }
                catch (CostConnectionException ex)
                {
                    using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await _store.RecordRunOutcomeAsync(run, ex.Status, cleanup.Token, ex.RetryAfter);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await _store.RecordRunOutcomeAsync(run, "sliceTimedOut", cleanup.Token);
                    throw;
                }
                finally
                {
                    using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await _store.ReleaseAsync(run, cleanup.Token);
                }
            }, ct);
            return outcome;
        }
    }
}
