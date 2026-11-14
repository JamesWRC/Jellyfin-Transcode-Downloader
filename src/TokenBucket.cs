using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Async token bucket measured in bytes. <see cref="TakeAsync"/> waits until the bytes may be
    /// sent. The rate is read on every call, so configuration changes apply to running downloads.
    /// </summary>
    public sealed class TokenBucket
    {
        private readonly Func<double> _bytesPerSecond;
        private readonly object _lock = new object();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _tokens;
        private double _lastSeconds;

        public TokenBucket(Func<double> bytesPerSecond)
        {
            _bytesPerSecond = bytesPerSecond;
        }

        /// <summary>
        /// Reserves <paramref name="bytes"/> and waits until they are covered. Reservations queue
        /// in arrival order because the balance may go negative: each caller waits for its own
        /// deficit, so concurrent callers share the rate instead of racing for it.
        /// </summary>
        public Task TakeAsync(int bytes, CancellationToken cancellationToken)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var rate = _bytesPerSecond();
                if (rate <= 0)
                {
                    _tokens = 0;
                    _lastSeconds = _clock.Elapsed.TotalSeconds;
                    return Task.CompletedTask;
                }

                var now = _clock.Elapsed.TotalSeconds;
                // Burst of at most half a second's worth, so an idle bucket cannot release a
                // large backlog in one go.
                var burst = Math.Max(rate * 0.5, 65536);
                _tokens = Math.Min(burst, _tokens + ((now - _lastSeconds) * rate));
                _lastSeconds = now;

                _tokens -= bytes;
                wait = _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / rate);
            }

            return wait <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(wait, cancellationToken);
        }
    }
}
