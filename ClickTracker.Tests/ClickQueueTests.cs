using ClickTracker.Clicks;
using Microsoft.Extensions.Options;
using Xunit;

namespace ClickTracker.Tests;

public sealed class ClickQueueTests
{
    private static ClickRecord Click(string linkId) =>
        new(
            DateTimeOffset.UtcNow,
            linkId,
            "https://example.com",
            "203.0.113.4",
            "GET",
            "https",
            "short.example.com",
            new Dictionary<string, string[]>());

    [Fact]
    public void The_queue_drops_the_oldest_click_when_it_is_full()
    {
        var queue = new ClickQueue(Options.Create(new ClicksOptions { QueueCapacity = 2 }));

        queue.Enqueue(Click("one"));
        queue.Enqueue(Click("two"));
        queue.Enqueue(Click("three"));

        Assert.True(queue.Reader.TryRead(out var first));
        Assert.Equal("two", first!.LinkId);
        queue.MarkTaken(1);
        Assert.True(queue.Reader.TryRead(out var second));
        Assert.Equal("three", second!.LinkId);
        queue.MarkTaken(1);

        Assert.Equal(3, queue.EnqueuedCount);
        Assert.Equal(1, queue.DroppedCount);
    }

    [Fact]
    public void The_drop_count_stays_at_zero_while_the_queue_has_room()
    {
        var queue = new ClickQueue(Options.Create(new ClicksOptions { QueueCapacity = 10 }));

        queue.Enqueue(Click("one"));
        queue.Enqueue(Click("two"));

        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public void A_click_that_the_writer_has_taken_is_not_counted_as_dropped()
    {
        var queue = new ClickQueue(Options.Create(new ClicksOptions { QueueCapacity = 10 }));

        queue.Enqueue(Click("one"));
        Assert.True(queue.Reader.TryRead(out _));
        queue.MarkTaken(1);

        Assert.Equal(0, queue.DroppedCount);
    }
}
