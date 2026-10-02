using System.Diagnostics;

namespace HelixTool.Core.Collect;

internal sealed class CollectProgressReporter(Action<string>? report, TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private long _lastEmission;

    public void Report(string message, bool force = false)
    {
        if (report is null)
            return;

        lock (_gate)
        {
            var now = _timeProvider.GetTimestamp();
            if (!force && _timeProvider.GetElapsedTime(_lastEmission, now) < TimeSpan.FromSeconds(2))
                return;
            _lastEmission = now;
            report(message);
        }
    }

    public async Task<T> WithHeartbeatAsync<T>(
        string operation,
        Func<Task<T>> action,
        CancellationToken ct)
    {
        if (report is null)
            return await action();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var elapsed = Stopwatch.StartNew();
        var heartbeat = HeartbeatAsync();
        try
        {
            return await action();
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await heartbeat;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
        }

        async Task HeartbeatAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stop.Token))
                Report($"{operation}: {elapsed.Elapsed.TotalSeconds:F0}s elapsed; acquisition still running.");
        }
    }
}
