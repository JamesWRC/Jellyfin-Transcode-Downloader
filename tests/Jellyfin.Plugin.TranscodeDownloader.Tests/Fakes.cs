namespace Jellyfin.Plugin.TranscodeDownloader.Tests;

/// <summary>
/// A clock that only moves when something waits: <see cref="Delay"/> advances it by the wait instead of
/// sleeping. Gives a <see cref="TokenBucket"/> deterministic time, so pacing is asserted on arithmetic
/// rather than on how fast the machine happens to run.
/// </summary>
internal sealed class FakeClock
{
    public double Seconds { get; set; }

    public List<TimeSpan> Waits { get; } = new();

    public double TotalWaited => Waits.Sum(w => w.TotalSeconds);

    public Func<double> Now => () => Seconds;

    public Task Delay(TimeSpan wait, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        Waits.Add(wait);
        Seconds += wait.TotalSeconds;
        return Task.CompletedTask;
    }

    /// <summary>Records the waits but lets no time pass, as when several callers reserve at the same instant.</summary>
    public Task DelayWithoutAdvancing(TimeSpan wait, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        Waits.Add(wait);
        return Task.CompletedTask;
    }

    public TokenBucket Bucket(Func<double> bytesPerSecond, bool advance = true)
        => new(bytesPerSecond, Now, advance ? Delay : DelayWithoutAdvancing);

    public TokenBucket Bucket(double bytesPerSecond, bool advance = true) => Bucket(() => bytesPerSecond, advance);
}
