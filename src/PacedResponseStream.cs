using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.TranscodeDownloader
{
    /// <summary>
    /// Write-only wrapper around a response body that releases bytes no faster than its buckets
    /// allow. Slowing the response is all it takes to slow the transcode too: Jellyfin's
    /// ProgressiveFileStream counts what the client has read (TranscodingJob.BytesDownloaded),
    /// and TranscodingThrottler pauses ffmpeg once it is ThrottleDelaySeconds of output ahead.
    /// </summary>
    public sealed class PacedResponseStream : Stream
    {
        private const int SliceBytes = 64 * 1024;

        private readonly Stream _inner;
        private readonly TokenBucket _own;
        private readonly TokenBucket _shared;
        private readonly Func<bool> _isExempt;
        private readonly DownloadSlot? _slot;
        private readonly CancellationToken _aborted;
        private bool? _exempt;
        private long _written;

        public PacedResponseStream(Stream inner, TokenBucket own, TokenBucket shared, Func<bool> isExempt, DownloadSlot? slot, CancellationToken aborted)
        {
            _inner = inner;
            _own = own;
            _shared = shared;
            _isExempt = isExempt;
            _slot = slot;
            _aborted = aborted;
        }

        /// <summary>Null until the first write decides it.</summary>
        public bool? Exempt => _exempt;

        public long Written => Interlocked.Read(ref _written);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            // Decided at the first write, not when the request arrives: by now Jellyfin's
            // forwarded-headers middleware has replaced the proxy's address with the client's.
            _exempt ??= _isExempt();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _aborted);
            var token = linked.Token;

            if (_exempt == true)
            {
                await _inner.WriteAsync(buffer, token).ConfigureAwait(false);
                Count(buffer.Length);
                return;
            }

            for (var offset = 0; offset < buffer.Length; offset += SliceBytes)
            {
                var slice = buffer.Slice(offset, Math.Min(SliceBytes, buffer.Length - offset));
                await _own.TakeAsync(slice.Length, token).ConfigureAwait(false);
                await _shared.TakeAsync(slice.Length, token).ConfigureAwait(false);
                await _inner.WriteAsync(slice, token).ConfigureAwait(false);
                Count(slice.Length);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Pacing waits for seconds at a time; blocking a thread for that would starve the server.
        // Response bodies are written asynchronously, so nothing should call this.
        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException("PacedResponseStream is async-only.");

        public override void Flush() => _inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        // The inner stream belongs to the server; never dispose it here.
        protected override void Dispose(bool disposing)
        {
        }

        private void Count(int bytes)
        {
            Interlocked.Add(ref _written, bytes);
            _slot?.AddBytes(bytes);
        }
    }
}
