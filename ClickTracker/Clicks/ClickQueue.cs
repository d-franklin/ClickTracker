using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace ClickTracker.Clicks;

/// <summary>
/// Holds captured clicks until the background writer can persist them. The queue is bounded, so a
/// storage outage cannot grow memory without limit: the oldest click is dropped instead.
/// </summary>
public sealed class ClickQueue
{
    private readonly Channel<ClickRecord> _channel;
    private long _enqueued;
    private long _taken;
    private long _written;

    public ClickQueue(IOptions<ClicksOptions> options)
    {
        _channel = Channel.CreateBounded<ClickRecord>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>The reader the background writer drains.</summary>
    public ChannelReader<ClickRecord> Reader => _channel.Reader;

    /// <summary>How many clicks have been handed to the queue.</summary>
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);

    /// <summary>How many clicks the writer has taken out of the queue.</summary>
    public long TakenCount => Interlocked.Read(ref _taken);

    /// <summary>How many clicks have been written to the store.</summary>
    public long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>
    /// How many clicks were lost, either because the queue was full or because a batch could not be
    /// written. The count is derived rather than incremented at the drop site, because the channel
    /// drops the oldest record without telling anyone. A click is in flight between being taken and
    /// being written, so it counts as neither pending nor dropped.
    /// </summary>
    public long DroppedCount
    {
        get
        {
            var pending = Reader.Count;
            var dropped = EnqueuedCount - TakenCount - pending;
            return dropped > 0 ? dropped : 0;
        }
    }

    /// <summary>Adds a click. This never blocks and never fails.</summary>
    public void Enqueue(ClickRecord record)
    {
        Interlocked.Increment(ref _enqueued);
        _channel.Writer.TryWrite(record);
    }

    /// <summary>Records that the writer took a batch out of the queue.</summary>
    public void MarkTaken(int count) => Interlocked.Add(ref _taken, count);

    /// <summary>Records that a batch reached the store.</summary>
    public void MarkWritten(int count) => Interlocked.Add(ref _written, count);
}
