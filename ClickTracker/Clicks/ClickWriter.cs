using Microsoft.Extensions.Options;

namespace ClickTracker.Clicks;

/// <summary>
/// Drains the click queue and writes batches to the store. The redirect path never waits for this.
/// </summary>
public sealed class ClickWriter : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    private readonly ClickQueue _queue;
    private readonly IClickStore _store;
    private readonly ILogger<ClickWriter> _logger;
    private readonly int _batchSize;
    private readonly int _batchIntervalMs;
    private long _lastLoggedDrops;

    public ClickWriter(
        ClickQueue queue,
        IClickStore store,
        IOptions<ClicksOptions> options,
        ILogger<ClickWriter> logger)
    {
        _queue = queue;
        _store = store;
        _logger = logger;
        _batchSize = Math.Max(1, options.Value.BatchSize);
        _batchIntervalMs = Math.Max(0, options.Value.BatchIntervalMs);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await ReadBatchAsync(stoppingToken);
                if (batch.Count == 0)
                {
                    continue;
                }

                await WriteBatchAsync(batch, stoppingToken);
                LogDrops();
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down.
        }
        finally
        {
            await FlushAsync();
        }
    }

    private async Task<List<ClickRecord>> ReadBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<ClickRecord>(_batchSize);

        if (!await _queue.Reader.WaitToReadAsync(cancellationToken))
        {
            return batch;
        }

        Drain(batch);

        var deadline = Environment.TickCount64 + _batchIntervalMs;
        while (batch.Count < _batchSize)
        {
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
            {
                break;
            }

            using var timeout = new CancellationTokenSource((int)Math.Min(remaining, int.MaxValue));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                if (!await _queue.Reader.WaitToReadAsync(linked.Token))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            Drain(batch);
        }

        return batch;
    }

    private void Drain(List<ClickRecord> batch)
    {
        while (batch.Count < _batchSize && _queue.Reader.TryRead(out var record))
        {
            batch.Add(record);
            _queue.MarkTaken(1);
        }
    }

    private async Task WriteBatchAsync(IReadOnlyList<ClickRecord> batch, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _store.WriteAsync(batch, cancellationToken);
                _queue.MarkWritten(batch.Count);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                if (attempt >= MaxAttempts)
                {
                    _logger.LogError(exception, "Dropping {Count} clicks after {Attempt} attempts", batch.Count, attempt);
                    return;
                }

                _logger.LogWarning(
                    exception,
                    "Writing {Count} clicks failed on attempt {Attempt}, retrying",
                    batch.Count,
                    attempt);

                try
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private void LogDrops()
    {
        var dropped = _queue.DroppedCount;
        if (dropped == _lastLoggedDrops)
        {
            return;
        }

        _lastLoggedDrops = dropped;
        if (dropped > 0)
        {
            _logger.LogWarning("{Count} clicks were dropped", dropped);
        }
    }

    private async Task FlushAsync()
    {
        var remaining = new List<ClickRecord>();
        while (_queue.Reader.TryRead(out var record))
        {
            remaining.Add(record);
            _queue.MarkTaken(1);
        }

        if (remaining.Count == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(FlushTimeout);
        await WriteBatchAsync(remaining, timeout.Token);
    }
}
