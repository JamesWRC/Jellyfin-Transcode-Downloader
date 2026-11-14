using Xunit;

namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

public class TokenBucketTests
{
    private const double MegabytePerSecond = 1_000_000;

    [Fact]
    public async Task A_new_bucket_has_no_credit_so_the_first_bytes_wait_for_their_own_cost()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);

        await bucket.TakeAsync(500_000, CancellationToken.None);

        Assert.Equal(0.5, clock.TotalWaited, 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_rate_of_zero_or_less_means_unlimited(double rate)
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(rate);

        for (var i = 0; i < 100; i++)
        {
            await bucket.TakeAsync(10_000_000, CancellationToken.None);
        }

        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Sustained_throughput_equals_the_rate()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);

        // 20 x 100 kB = 2 MB, which at 1 MB/s must take two seconds of (fake) time.
        for (var i = 0; i < 20; i++)
        {
            await bucket.TakeAsync(100_000, CancellationToken.None);
        }

        Assert.InRange(clock.Seconds, 1.99, 2.01);
    }

    [Fact]
    public async Task Idle_time_earns_at_most_half_a_seconds_burst()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);
        clock.Seconds = 3600; // an hour idle

        await bucket.TakeAsync(500_000, CancellationToken.None);   // exactly the burst: free
        Assert.Empty(clock.Waits);

        await bucket.TakeAsync(500_000, CancellationToken.None);   // nothing left: pays full price
        Assert.Equal(0.5, clock.TotalWaited, 3);
    }

    [Fact]
    public async Task A_backlog_larger_than_the_burst_waits_for_the_excess()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);
        clock.Seconds = 3600;

        await bucket.TakeAsync(1_500_000, CancellationToken.None);

        // 1.5 MB taken against a 0.5 MB burst: 1 MB over, so one second.
        Assert.Equal(1.0, clock.TotalWaited, 3);
    }

    [Fact]
    public async Task Tokens_refill_with_time()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);
        clock.Seconds = 3600;
        await bucket.TakeAsync(500_000, CancellationToken.None); // drain the burst

        clock.Seconds += 0.25;                                   // refills 250 kB
        await bucket.TakeAsync(250_000, CancellationToken.None);
        Assert.Empty(clock.Waits);

        clock.Seconds += 0.1;                                    // refills 100 kB
        await bucket.TakeAsync(300_000, CancellationToken.None); // 200 kB short
        Assert.Equal(0.2, clock.TotalWaited, 3);
    }

    [Fact]
    public async Task Callers_arriving_together_queue_behind_each_other()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond, advance: false);

        await bucket.TakeAsync(500_000, CancellationToken.None);
        await bucket.TakeAsync(500_000, CancellationToken.None);
        await bucket.TakeAsync(500_000, CancellationToken.None);

        // Each waits for its own deficit on top of everyone ahead of it.
        Assert.Equal(new[] { 0.5, 1.0, 1.5 }, clock.Waits.Select(w => Math.Round(w.TotalSeconds, 3)));
    }

    [Fact]
    public async Task The_burst_never_drops_below_64_kB_even_at_a_trickle()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(10_000); // half a second would be 5 kB
        clock.Seconds = 3600;

        await bucket.TakeAsync(65_536, CancellationToken.None);

        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task A_changed_rate_applies_to_the_next_take()
    {
        var clock = new FakeClock();
        var rate = MegabytePerSecond;
        var bucket = clock.Bucket(() => rate, advance: false);

        await bucket.TakeAsync(1_000_000, CancellationToken.None);   // 1 MB in debt at 1 MB/s: 1 s
        Assert.Equal(1.0, clock.TotalWaited, 3);

        rate = 4 * MegabytePerSecond;
        clock.Waits.Clear();
        await bucket.TakeAsync(1_000_000, CancellationToken.None);   // 2 MB in debt, now at 4 MB/s: 0.5 s
        Assert.Equal(0.5, clock.TotalWaited, 3);

        rate = 0; // limit removed while the download runs
        clock.Waits.Clear();
        await bucket.TakeAsync(50_000_000, CancellationToken.None);
        Assert.Empty(clock.Waits);
    }

    [Fact]
    public async Task Switching_to_unlimited_and_back_does_not_bank_credit()
    {
        var clock = new FakeClock();
        var rate = 0d;
        var bucket = clock.Bucket(() => rate);
        await bucket.TakeAsync(1, CancellationToken.None);

        clock.Seconds += 1000;
        rate = MegabytePerSecond;
        await bucket.TakeAsync(500_000, CancellationToken.None);   // the burst, no more
        await bucket.TakeAsync(500_000, CancellationToken.None);

        Assert.Equal(0.5, clock.TotalWaited, 3);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_the_wait()
    {
        var clock = new FakeClock();
        var bucket = clock.Bucket(MegabytePerSecond);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bucket.TakeAsync(500_000, cts.Token));
    }

    [Fact]
    public async Task The_default_bucket_uses_real_time_and_honours_cancellation()
    {
        // 1 MB at 1 kB/s would take ~1000 s, so the task is pending until it is cancelled.
        var bucket = new TokenBucket(() => 1_000);
        using var cts = new CancellationTokenSource();

        var task = bucket.TakeAsync(1_000_000, cts.Token);
        Assert.False(task.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task The_default_bucket_lets_an_unlimited_rate_straight_through()
    {
        var bucket = new TokenBucket(() => 0);

        var task = bucket.TakeAsync(int.MaxValue, CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        await task;
    }
}
