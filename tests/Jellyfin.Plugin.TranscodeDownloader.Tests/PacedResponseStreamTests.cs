using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

public class PacedResponseStreamTests
{
    private const double MegabytePerSecond = 1_000_000;

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i % 251);
        return data;
    }

    private static PacedResponseStream Paced(Stream inner, TokenBucket own, TokenBucket shared, Func<bool>? exempt = null, DownloadSlot? slot = null, CancellationToken aborted = default)
        => new(inner, own, shared, exempt ?? (() => false), slot, aborted);

    private static DownloadSlot Slot(TokenBucket bucket) => new("ticket", Guid.NewGuid(), Guid.NewGuid(), 1_000_000, 0, bucket);

    [Fact]
    public async Task Bytes_arrive_intact_and_in_order()
    {
        var clock = new FakeClock();
        var inner = new MemoryStream();
        var paced = Paced(inner, clock.Bucket(MegabytePerSecond), clock.Bucket(10 * MegabytePerSecond));
        var data = Data(300_001); // not a multiple of the 64 kB slice

        await paced.WriteAsync(data, CancellationToken.None);

        Assert.Equal(data, inner.ToArray());
        Assert.Equal(300_001, paced.Written);
    }

    [Fact]
    public async Task The_per_download_rate_sets_the_pace()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(10 * MegabytePerSecond));

        await paced.WriteAsync(Data(300_000), CancellationToken.None);

        // 300 kB at 1 MB/s.
        Assert.InRange(clock.Seconds, 0.29, 0.31);
    }

    [Fact]
    public async Task The_shared_rate_sets_the_pace_when_it_is_the_tighter_one()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(10 * MegabytePerSecond), clock.Bucket(MegabytePerSecond / 2));

        await paced.WriteAsync(Data(300_000), CancellationToken.None);

        // 300 kB at 0.5 MB/s.
        Assert.InRange(clock.Seconds, 0.59, 0.61);
    }

    [Fact]
    public async Task Many_small_writes_are_paced_like_one_big_one()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(10 * MegabytePerSecond));

        for (var i = 0; i < 30; i++)
        {
            await paced.WriteAsync(Data(10_000), 0, 10_000, CancellationToken.None);
        }

        Assert.InRange(clock.Seconds, 0.29, 0.31);
        Assert.Equal(300_000, paced.Written);
    }

    [Fact]
    public async Task An_exempt_client_is_not_paced_at_all()
    {
        var clock = new FakeClock();
        var inner = new MemoryStream();
        var paced = Paced(inner, clock.Bucket(1_000), clock.Bucket(1_000), exempt: () => true);

        await paced.WriteAsync(Data(1_000_000), CancellationToken.None);

        Assert.Empty(clock.Waits);
        Assert.Equal(1_000_000, inner.Length);
        Assert.Equal(1_000_000, paced.Written);
        Assert.True(paced.Exempt);
    }

    [Fact]
    public async Task Exemption_is_decided_once_at_the_first_write_not_at_construction()
    {
        var clock = new FakeClock();
        var calls = 0;
        var paced = Paced(new MemoryStream(), clock.Bucket(10 * MegabytePerSecond), clock.Bucket(10 * MegabytePerSecond), exempt: () => { calls++; return false; });

        Assert.Null(paced.Exempt);
        Assert.Equal(0, calls);

        await paced.WriteAsync(Data(10), CancellationToken.None);
        await paced.WriteAsync(Data(10), CancellationToken.None);

        Assert.False(paced.Exempt);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Bytes_are_counted_on_the_slot_for_exempt_and_paced_downloads_alike()
    {
        foreach (var exempt in new[] { true, false })
        {
            var clock = new FakeClock();
            var slot = Slot(clock.Bucket(MegabytePerSecond));
            var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond), () => exempt, slot);

            await paced.WriteAsync(Data(150_000), CancellationToken.None);

            Assert.Equal(150_000, slot.BytesSent);
        }
    }

    [Fact]
    public async Task An_empty_write_is_a_no_op()
    {
        var clock = new FakeClock();
        var inner = new MemoryStream();
        var paced = Paced(inner, clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond));

        await paced.WriteAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal(0, inner.Length);
        Assert.Equal(0, paced.Written);
    }

    [Fact]
    public async Task A_cancelled_request_token_stops_the_write()
    {
        var clock = new FakeClock();
        var inner = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var paced = Paced(inner, clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond), aborted: cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paced.WriteAsync(Data(300_000), CancellationToken.None).AsTask());

        Assert.Equal(0, inner.Length);
    }

    [Fact]
    public async Task A_cancelled_caller_token_stops_the_write()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paced.WriteAsync(Data(300_000), cts.Token).AsTask());
    }

    [Fact]
    public async Task Cancelling_part_way_leaves_only_the_slices_already_released()
    {
        var clock = new FakeClock();
        using var cts = new CancellationTokenSource();
        var slices = 0;
        // A wait that cancels the client's connection after the third slice has been paid for.
        var own = new TokenBucket(() => MegabytePerSecond, clock.Now, (wait, token) =>
        {
            if (++slices == 3) cts.Cancel();
            return clock.Delay(wait, token);
        });
        var inner = new MemoryStream();
        var paced = Paced(inner, own, clock.Bucket(10 * MegabytePerSecond), aborted: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => paced.WriteAsync(Data(1_000_000), CancellationToken.None).AsTask());

        Assert.Equal(2 * 65_536, inner.Length);
        Assert.Equal(inner.Length, paced.Written);
    }

    [Fact]
    public void Synchronous_write_is_refused_so_a_paced_wait_can_never_block_a_thread()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond));

        Assert.Throws<NotSupportedException>(() => paced.Write(new byte[10], 0, 10));
        Assert.Throws<NotSupportedException>(() => paced.Write(new byte[10].AsSpan()));
        Assert.Throws<NotSupportedException>(() => paced.WriteByte(1));
    }

    [Fact]
    public void It_is_a_write_only_forward_only_stream()
    {
        var clock = new FakeClock();
        var paced = Paced(new MemoryStream(), clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond));

        Assert.True(paced.CanWrite);
        Assert.False(paced.CanRead);
        Assert.False(paced.CanSeek);
        Assert.Throws<NotSupportedException>(() => paced.Read(new byte[1], 0, 1));
        Assert.Throws<NotSupportedException>(() => paced.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => paced.SetLength(0));
        Assert.Throws<NotSupportedException>(() => paced.Length);
        Assert.Throws<NotSupportedException>(() => paced.Position);
    }

    [Fact]
    public async Task Disposing_it_leaves_the_servers_stream_open_and_flush_reaches_it()
    {
        var clock = new FakeClock();
        var inner = new MemoryStream();
        var paced = Paced(inner, clock.Bucket(MegabytePerSecond), clock.Bucket(MegabytePerSecond));

        await paced.FlushAsync();
        paced.Dispose();
        await paced.DisposeAsync();

        Assert.True(inner.CanWrite);
    }
}
