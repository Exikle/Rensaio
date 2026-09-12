using System.Threading.Channels;

namespace RensaioBackend.Services.Contributions;

/// <summary>
/// FIFO channel that queues "export contributions to cloud" requests. The
/// controller writes a signal and returns immediately; a hosted background
/// service drains the channel and performs the actual upload.
/// </summary>
public interface IContributionUploadQueue
{
    /// <summary>Enqueue an upload request. Returns false if the queue is complete.</summary>
    bool Enqueue();

    /// <summary>Wait for a request to become available (returns false when the channel is complete).</summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken token);

    /// <summary>Try to dequeue a pending request. Returns false when the queue is empty.</summary>
    bool TryRead(out byte item);
}

public sealed class ContributionUploadQueue : IContributionUploadQueue
{
    // Single-producer/single-consumer — one enqueuer (the controller) and one
    // consumer (the hosted background service). Bounded at 16 so a burst of
    // clicks cannot grow memory unboundedly; WriteAsync back-pressures.
    private readonly Channel<byte> _channel =
        Channel.CreateBounded<byte>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool Enqueue()
    {
        // TryWrite fails when the channel is full; fall back to dropping the
        // request (the daily cron will upload anyway).
        return _channel.Writer.TryWrite(0);
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken token) =>
        _channel.Reader.WaitToReadAsync(token);

    public bool TryRead(out byte item) =>
        _channel.Reader.TryRead(out item);
}